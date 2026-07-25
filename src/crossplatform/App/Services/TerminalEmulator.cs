using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>Character attributes carried by a screen cell.</summary>
[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
}

/// <summary>
///  One screen cell: a character plus its colours. Colours are stored as
///  -1 (terminal default), 0..255 (xterm palette index) or
///  <c>0x1000000 | rgb</c> for a direct 24-bit colour.
/// </summary>
public struct TerminalCell
{
    public char Ch;
    public int Fg;
    public int Bg;
    public CellFlags Flags;

    public static TerminalCell Blank => new() { Ch = ' ', Fg = -1, Bg = -1, Flags = CellFlags.None };
}

/// <summary>
///  A small but honest VT100/xterm terminal emulator: it owns the screen buffer and
///  consumes the byte stream produced by a shell running under a PTY.
///  <para>Supported: UTF-8 text, CR/LF/BS/TAB/BEL, SGR (bold, dim, italic, underline,
///  inverse, 16 colours, bright colours, 256-colour and 24-bit colour), cursor motion
///  (CUU/CUD/CUF/CUB/CUP/CHA/VPA/CNL/CPL), save/restore cursor, erase in display and
///  line (ED/EL, including scrollback clear), insert/delete lines and characters
///  (IL/DL/ICH/DCH/ECH), scroll up/down (SU/SD), scrolling region (DECSTBM), index and
///  reverse index, autowrap, cursor visibility, application cursor keys (DECCKM),
///  bracketed paste state, the alternate screen (?1047/?1049/?47) so full-screen
///  programs such as <c>top</c> and <c>less</c> do not corrupt the scrollback, device
///  status / device attribute replies, OSC title strings (parsed and exposed), tab
///  stops every 8 columns, and a bounded scrollback buffer.</para>
///  <para>Not supported (deliberately): double-width/double-height lines, character
///  sets other than UTF-8 (SCS sequences are parsed and ignored), mouse reporting,
///  and per-cell wide-character (CJK) advance.</para>
///  <para>All public members are guarded by <see cref="SyncRoot"/>; the PTY reader
///  thread writes while the UI thread renders.</para>
/// </summary>
public sealed class TerminalEmulator
{
    private const int MaxScrollback = 5000;

    private enum State
    {
        Ground,
        Escape,
        Csi,
        Osc,
        OscEsc,
        Charset,
    }

    /// <summary>Lock held while mutating or reading the screen.</summary>
    public object SyncRoot { get; } = new();

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _charBuffer = new char[16384];
    private readonly List<int> _params = [];
    private readonly StringBuilder _paramDigits = new();
    private readonly StringBuilder _osc = new();
    private readonly List<TerminalCell[]> _scrollback = [];

    private State _state = State.Ground;
    private bool _csiPrivate;
    private char _csiIntermediate;

    private TerminalCell[] _cells;
    private TerminalCell[]? _altCells;
    private int _cols;
    private int _rows;
    private int _cursorX;
    private int _cursorY;
    private int _savedX;
    private int _savedY;
    private int _altSavedX;
    private int _altSavedY;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _wrapPending;
    private TerminalCell _pen = TerminalCell.Blank;
    private TerminalCell _savedPen = TerminalCell.Blank;

    public TerminalEmulator(int cols, int rows)
    {
        _cols = Math.Max(1, cols);
        _rows = Math.Max(1, rows);
        _cells = NewBuffer(_cols, _rows);
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    /// <summary>Raised when the emulator must answer the host (DSR / DA replies).</summary>
    public event Action<string>? Respond;

    /// <summary>Raised when a bell character arrives.</summary>
    public event Action? Bell;

    public int Cols => _cols;

    public int Rows => _rows;

    public int CursorX => _cursorX;

    public int CursorY => _cursorY;

    public bool CursorVisible { get; private set; } = true;

    /// <summary>True while DECCKM is set: arrow keys must be sent as SS3 (ESC O A).</summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>True while the alternate screen is active (full-screen program).</summary>
    public bool AltScreen => _altCells is not null;

    /// <summary>True while the program requested bracketed paste (?2004).</summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>Window/tab title last set through OSC 0/2.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Number of lines currently retained above the visible screen.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Serial number bumped on every mutation, so a view can skip redraws.</summary>
    public long Version { get; private set; }

    private static TerminalCell[] NewBuffer(int cols, int rows)
    {
        TerminalCell[] buffer = new TerminalCell[cols * rows];
        Array.Fill(buffer, TerminalCell.Blank);
        return buffer;
    }

    /// <summary>Copies one visible row (0 = top of screen) into <paramref name="destination"/>.</summary>
    public void CopyRow(int row, TerminalCell[] destination)
    {
        if (row < 0 || row >= _rows)
        {
            Array.Fill(destination, TerminalCell.Blank, 0, Math.Min(destination.Length, _cols));
            return;
        }

        Array.Copy(_cells, row * _cols, destination, 0, Math.Min(_cols, destination.Length));
    }

    /// <summary>Copies a scrollback row (0 = oldest) into <paramref name="destination"/>.</summary>
    public void CopyScrollbackRow(int index, TerminalCell[] destination)
    {
        if (index < 0 || index >= _scrollback.Count)
        {
            Array.Fill(destination, TerminalCell.Blank, 0, Math.Min(destination.Length, _cols));
            return;
        }

        TerminalCell[] source = _scrollback[index];
        int n = Math.Min(Math.Min(source.Length, _cols), destination.Length);
        Array.Copy(source, destination, n);
        if (n < Math.Min(_cols, destination.Length))
        {
            Array.Fill(destination, TerminalCell.Blank, n, Math.Min(_cols, destination.Length) - n);
        }
    }

    /// <summary>Resizes the screen, keeping the bottom-most content when shrinking.</summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == _cols && rows == _rows)
        {
            return;
        }

        _cells = Reflow(_cells, _cols, _rows, cols, rows, pushToScrollback: _altCells is null, ref _cursorY);
        if (_altCells is not null)
        {
            int dummy = 0;
            _altCells = Reflow(_altCells, _cols, _rows, cols, rows, pushToScrollback: false, ref dummy);
        }

        _cols = cols;
        _rows = rows;
        _cursorX = Math.Clamp(_cursorX, 0, _cols - 1);
        _cursorY = Math.Clamp(_cursorY, 0, _rows - 1);
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _wrapPending = false;
        Version++;
    }

    private TerminalCell[] Reflow(TerminalCell[] source, int oldCols, int oldRows, int cols, int rows, bool pushToScrollback, ref int cursorY)
    {
        TerminalCell[] target = NewBuffer(cols, rows);

        // Keep the last `rows` lines when the screen gets shorter; the shell prompt
        // lives at the bottom, which is what the user cares about.
        int firstRow = 0;
        if (oldRows > rows)
        {
            firstRow = Math.Min(oldRows - rows, Math.Max(0, cursorY - rows + 1));
            if (pushToScrollback)
            {
                for (int r = 0; r < firstRow; r++)
                {
                    PushScrollback(source, r, oldCols);
                }
            }

            cursorY -= firstRow;
        }

        int copyRows = Math.Min(rows, oldRows - firstRow);
        int copyCols = Math.Min(cols, oldCols);
        for (int r = 0; r < copyRows; r++)
        {
            Array.Copy(source, (firstRow + r) * oldCols, target, r * cols, copyCols);
        }

        return target;
    }

    private void PushScrollback(TerminalCell[] source, int row, int cols)
    {
        TerminalCell[] line = new TerminalCell[cols];
        Array.Copy(source, row * cols, line, 0, cols);
        _scrollback.Add(line);
        if (_scrollback.Count > MaxScrollback)
        {
            _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
        }
    }

    /// <summary>Feeds raw PTY bytes into the parser.</summary>
    public void Feed(byte[] data, int count)
    {
        int charCount = _decoder.GetChars(data, 0, count, _charBuffer, 0, flush: false);
        for (int i = 0; i < charCount; i++)
        {
            Consume(_charBuffer[i]);
        }

        Version++;
    }

    private void Consume(char c)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c);
                break;

            case State.Escape:
                Escape(c);
                break;

            case State.Csi:
                Csi(c);
                break;

            case State.Osc:
                if (c == '\a')
                {
                    EndOsc();
                }
                else if (c == '\x1b')
                {
                    _state = State.OscEsc;
                }
                else
                {
                    _osc.Append(c);
                }

                break;

            case State.OscEsc:
                // ESC \ terminates a string; anything else aborts it.
                EndOsc();
                if (c != '\\')
                {
                    Consume(c);
                }

                break;

            case State.Charset:
                _state = State.Ground;
                break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = State.Escape;
                return;

            case '\r':
                _cursorX = 0;
                _wrapPending = false;
                return;

            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                return;

            case '\b':
                if (_wrapPending)
                {
                    _wrapPending = false;
                }
                else if (_cursorX > 0)
                {
                    _cursorX--;
                }

                return;

            case '\t':
                _cursorX = Math.Min(_cols - 1, (_cursorX / 8 * 8) + 8);
                _wrapPending = false;
                return;

            case '\a':
                Bell?.Invoke();
                return;

            case '\x0e':
            case '\x0f':
                return;   // shift out / shift in: single-byte charsets, ignored
        }

        if (c < ' ' || c == '\x7f')
        {
            return;
        }

        Put(c);
    }

    private void Put(char c)
    {
        if (_wrapPending)
        {
            _cursorX = 0;
            LineFeed();
            _wrapPending = false;
        }

        int index = (_cursorY * _cols) + _cursorX;
        if ((uint)index < (uint)_cells.Length)
        {
            _cells[index] = new TerminalCell { Ch = c, Fg = _pen.Fg, Bg = _pen.Bg, Flags = _pen.Flags };
        }

        if (_cursorX + 1 >= _cols)
        {
            _wrapPending = true;
        }
        else
        {
            _cursorX++;
        }
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (_cursorY == _scrollBottom)
        {
            ScrollUp(1, toScrollback: _altCells is null && _scrollTop == 0);
        }
        else if (_cursorY < _rows - 1)
        {
            _cursorY++;
        }
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_cursorY == _scrollTop)
        {
            ScrollDown(1);
        }
        else if (_cursorY > 0)
        {
            _cursorY--;
        }
    }

    private void ScrollUp(int n, bool toScrollback)
    {
        n = Math.Clamp(n, 1, _scrollBottom - _scrollTop + 1);
        for (int i = 0; i < n; i++)
        {
            if (toScrollback)
            {
                PushScrollback(_cells, _scrollTop, _cols);
            }

            for (int r = _scrollTop; r < _scrollBottom; r++)
            {
                Array.Copy(_cells, (r + 1) * _cols, _cells, r * _cols, _cols);
            }

            ClearRow(_scrollBottom);
        }
    }

    private void ScrollDown(int n)
    {
        n = Math.Clamp(n, 1, _scrollBottom - _scrollTop + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = _scrollBottom; r > _scrollTop; r--)
            {
                Array.Copy(_cells, (r - 1) * _cols, _cells, r * _cols, _cols);
            }

            ClearRow(_scrollTop);
        }
    }

    private void ClearRow(int row)
    {
        TerminalCell blank = TerminalCell.Blank;
        blank.Bg = _pen.Bg;
        Array.Fill(_cells, blank, row * _cols, _cols);
    }

    private void Escape(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _params.Clear();
                _paramDigits.Clear();
                _csiPrivate = false;
                _csiIntermediate = '\0';
                _state = State.Csi;
                break;

            case ']':
                _osc.Clear();
                _state = State.Osc;
                break;

            case 'P':   // DCS
            case '^':
            case '_':
                _osc.Clear();
                _state = State.Osc;
                break;

            case '(':
            case ')':
            case '*':
            case '+':
                _state = State.Charset;
                break;

            case '7':
                SaveCursor();
                break;

            case '8':
                RestoreCursor();
                break;

            case 'D':
                LineFeed();
                break;

            case 'E':
                _cursorX = 0;
                LineFeed();
                break;

            case 'M':
                ReverseIndex();
                break;

            case 'c':
                Reset();
                break;

            case '=':
            case '>':
                break;   // keypad modes: no separate numeric keypad handling
        }
    }

    private void SaveCursor()
    {
        _savedX = _cursorX;
        _savedY = _cursorY;
        _savedPen = _pen;
    }

    private void RestoreCursor()
    {
        _cursorX = Math.Clamp(_savedX, 0, _cols - 1);
        _cursorY = Math.Clamp(_savedY, 0, _rows - 1);
        _pen = _savedPen;
        _wrapPending = false;
    }

    private void Reset()
    {
        _pen = TerminalCell.Blank;
        _cursorX = 0;
        _cursorY = 0;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        Array.Fill(_cells, TerminalCell.Blank);
    }

    private void Csi(char c)
    {
        if (c is >= '0' and <= '9')
        {
            _paramDigits.Append(c);
            return;
        }

        if (c == ';')
        {
            PushParam();
            return;
        }

        if (c is '?' or '<' or '=' or '>')
        {
            _csiPrivate = true;
            return;
        }

        if (c is ' ' or '!' or '"' or '$' or '\'' or '*')
        {
            _csiIntermediate = c;
            return;
        }

        PushParam();
        _state = State.Ground;
        Dispatch(c);
    }

    private void PushParam()
    {
        if (_paramDigits.Length > 0)
        {
            _params.Add(int.TryParse(_paramDigits.ToString(), out int v) ? v : 0);
            _paramDigits.Clear();
        }
        else
        {
            _params.Add(0);
        }
    }

    private int Param(int index, int fallback)
    {
        if (index >= _params.Count)
        {
            return fallback;
        }

        int v = _params[index];
        return v == 0 ? fallback : v;
    }

    private void Dispatch(char final)
    {
        switch (final)
        {
            case 'A':
                _cursorY = Math.Max(_scrollTop <= _cursorY ? _scrollTop : 0, _cursorY - Param(0, 1));
                _wrapPending = false;
                break;

            case 'B':
                _cursorY = Math.Min(_scrollBottom >= _cursorY ? _scrollBottom : _rows - 1, _cursorY + Param(0, 1));
                _wrapPending = false;
                break;

            case 'C':
                _cursorX = Math.Min(_cols - 1, _cursorX + Param(0, 1));
                _wrapPending = false;
                break;

            case 'D':
                _cursorX = Math.Max(0, _cursorX - Param(0, 1));
                _wrapPending = false;
                break;

            case 'E':
                _cursorX = 0;
                _cursorY = Math.Min(_rows - 1, _cursorY + Param(0, 1));
                break;

            case 'F':
                _cursorX = 0;
                _cursorY = Math.Max(0, _cursorY - Param(0, 1));
                break;

            case 'G':
            case '`':
                _cursorX = Math.Clamp(Param(0, 1) - 1, 0, _cols - 1);
                _wrapPending = false;
                break;

            case 'd':
                _cursorY = Math.Clamp(Param(0, 1) - 1, 0, _rows - 1);
                _wrapPending = false;
                break;

            case 'H':
            case 'f':
                _cursorY = Math.Clamp(Param(0, 1) - 1, 0, _rows - 1);
                _cursorX = Math.Clamp(Param(1, 1) - 1, 0, _cols - 1);
                _wrapPending = false;
                break;

            case 'J':
                EraseInDisplay(_params.Count > 0 ? _params[0] : 0);
                break;

            case 'K':
                EraseInLine(_params.Count > 0 ? _params[0] : 0);
                break;

            case 'L':
                InsertLines(Param(0, 1));
                break;

            case 'M':
                DeleteLines(Param(0, 1));
                break;

            case '@':
                InsertChars(Param(0, 1));
                break;

            case 'P':
                DeleteChars(Param(0, 1));
                break;

            case 'X':
                EraseChars(Param(0, 1));
                break;

            case 'S':
                ScrollUp(Param(0, 1), toScrollback: false);
                break;

            case 'T':
                ScrollDown(Param(0, 1));
                break;

            case 'r':
                {
                    int top = Math.Clamp(Param(0, 1) - 1, 0, _rows - 1);
                    int bottom = Math.Clamp(Param(1, _rows) - 1, 0, _rows - 1);
                    if (top < bottom)
                    {
                        _scrollTop = top;
                        _scrollBottom = bottom;
                        _cursorX = 0;
                        _cursorY = top;
                    }

                    break;
                }

            case 'm':
                ApplySgr();
                break;

            case 'h':
                SetMode(true);
                break;

            case 'l':
                SetMode(false);
                break;

            case 's':
                SaveCursor();
                break;

            case 'u':
                RestoreCursor();
                break;

            case 'n':
                if (!_csiPrivate && _params.Count > 0 && _params[0] == 6)
                {
                    Respond?.Invoke($"\x1b[{_cursorY + 1};{_cursorX + 1}R");
                }
                else if (!_csiPrivate && _params.Count > 0 && _params[0] == 5)
                {
                    Respond?.Invoke("\x1b[0n");
                }

                break;

            case 'c':
                if (!_csiPrivate)
                {
                    Respond?.Invoke("\x1b[?1;2c");
                }

                break;

            case 'g':
            case 't':
            case 'q':
            case 'p':
                break;   // tab stops / window ops / cursor style: ignored
        }
    }

    private void SetMode(bool set)
    {
        foreach (int mode in _params)
        {
            if (_csiPrivate)
            {
                switch (mode)
                {
                    case 1:
                        ApplicationCursorKeys = set;
                        break;

                    case 25:
                        CursorVisible = set;
                        break;

                    case 47:
                    case 1047:
                    case 1049:
                        SetAltScreen(set, saveCursor: mode == 1049);
                        break;

                    case 2004:
                        BracketedPaste = set;
                        break;
                }
            }
        }
    }

    private void SetAltScreen(bool enable, bool saveCursor)
    {
        if (enable)
        {
            if (_altCells is not null)
            {
                return;
            }

            _altCells = _cells;
            _altSavedX = _cursorX;
            _altSavedY = _cursorY;
            _cells = NewBuffer(_cols, _rows);
            _scrollTop = 0;
            _scrollBottom = _rows - 1;
            if (saveCursor)
            {
                _cursorX = 0;
                _cursorY = 0;
            }
        }
        else
        {
            if (_altCells is null)
            {
                return;
            }

            _cells = _altCells;
            _altCells = null;
            _cursorX = Math.Clamp(_altSavedX, 0, _cols - 1);
            _cursorY = Math.Clamp(_altSavedY, 0, _rows - 1);
            _scrollTop = 0;
            _scrollBottom = _rows - 1;
        }

        _wrapPending = false;
    }

    private void EraseInDisplay(int mode)
    {
        TerminalCell blank = TerminalCell.Blank;
        blank.Bg = _pen.Bg;
        switch (mode)
        {
            case 0:
                {
                    int from = (_cursorY * _cols) + _cursorX;
                    Array.Fill(_cells, blank, from, _cells.Length - from);
                    break;
                }

            case 1:
                {
                    int to = (_cursorY * _cols) + _cursorX + 1;
                    Array.Fill(_cells, blank, 0, Math.Min(to, _cells.Length));
                    break;
                }

            case 2:
                Array.Fill(_cells, blank);
                break;

            case 3:
                _scrollback.Clear();
                break;
        }

        _wrapPending = false;
    }

    private void EraseInLine(int mode)
    {
        TerminalCell blank = TerminalCell.Blank;
        blank.Bg = _pen.Bg;
        int rowStart = _cursorY * _cols;
        switch (mode)
        {
            case 0:
                Array.Fill(_cells, blank, rowStart + _cursorX, _cols - _cursorX);
                break;

            case 1:
                Array.Fill(_cells, blank, rowStart, Math.Min(_cursorX + 1, _cols));
                break;

            case 2:
                Array.Fill(_cells, blank, rowStart, _cols);
                break;
        }

        _wrapPending = false;
    }

    private void InsertLines(int n)
    {
        if (_cursorY < _scrollTop || _cursorY > _scrollBottom)
        {
            return;
        }

        n = Math.Clamp(n, 1, _scrollBottom - _cursorY + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = _scrollBottom; r > _cursorY; r--)
            {
                Array.Copy(_cells, (r - 1) * _cols, _cells, r * _cols, _cols);
            }

            ClearRow(_cursorY);
        }
    }

    private void DeleteLines(int n)
    {
        if (_cursorY < _scrollTop || _cursorY > _scrollBottom)
        {
            return;
        }

        n = Math.Clamp(n, 1, _scrollBottom - _cursorY + 1);
        for (int i = 0; i < n; i++)
        {
            for (int r = _cursorY; r < _scrollBottom; r++)
            {
                Array.Copy(_cells, (r + 1) * _cols, _cells, r * _cols, _cols);
            }

            ClearRow(_scrollBottom);
        }
    }

    private void InsertChars(int n)
    {
        n = Math.Clamp(n, 1, _cols - _cursorX);
        int rowStart = _cursorY * _cols;
        for (int x = _cols - 1; x >= _cursorX + n; x--)
        {
            _cells[rowStart + x] = _cells[rowStart + x - n];
        }

        TerminalCell blank = TerminalCell.Blank;
        blank.Bg = _pen.Bg;
        Array.Fill(_cells, blank, rowStart + _cursorX, n);
    }

    private void DeleteChars(int n)
    {
        n = Math.Clamp(n, 1, _cols - _cursorX);
        int rowStart = _cursorY * _cols;
        for (int x = _cursorX; x < _cols - n; x++)
        {
            _cells[rowStart + x] = _cells[rowStart + x + n];
        }

        TerminalCell blank = TerminalCell.Blank;
        blank.Bg = _pen.Bg;
        Array.Fill(_cells, blank, rowStart + _cols - n, n);
    }

    private void EraseChars(int n)
    {
        n = Math.Clamp(n, 1, _cols - _cursorX);
        TerminalCell blank = TerminalCell.Blank;
        blank.Bg = _pen.Bg;
        Array.Fill(_cells, blank, (_cursorY * _cols) + _cursorX, n);
    }

    private void ApplySgr()
    {
        if (_params.Count == 0)
        {
            _params.Add(0);
        }

        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0:
                    _pen = TerminalCell.Blank;
                    break;

                case 1:
                    _pen.Flags |= CellFlags.Bold;
                    break;

                case 2:
                    _pen.Flags |= CellFlags.Dim;
                    break;

                case 3:
                    _pen.Flags |= CellFlags.Italic;
                    break;

                case 4:
                    _pen.Flags |= CellFlags.Underline;
                    break;

                case 7:
                    _pen.Flags |= CellFlags.Inverse;
                    break;

                case 21:
                case 22:
                    _pen.Flags &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;

                case 23:
                    _pen.Flags &= ~CellFlags.Italic;
                    break;

                case 24:
                    _pen.Flags &= ~CellFlags.Underline;
                    break;

                case 27:
                    _pen.Flags &= ~CellFlags.Inverse;
                    break;

                case 39:
                    _pen.Fg = -1;
                    break;

                case 49:
                    _pen.Bg = -1;
                    break;

                case 38:
                case 48:
                    {
                        int colour = ReadExtendedColour(ref i);
                        if (colour != int.MinValue)
                        {
                            if (p == 38)
                            {
                                _pen.Fg = colour;
                            }
                            else
                            {
                                _pen.Bg = colour;
                            }
                        }

                        break;
                    }

                default:
                    if (p is >= 30 and <= 37)
                    {
                        _pen.Fg = p - 30;
                    }
                    else if (p is >= 40 and <= 47)
                    {
                        _pen.Bg = p - 40;
                    }
                    else if (p is >= 90 and <= 97)
                    {
                        _pen.Fg = p - 90 + 8;
                    }
                    else if (p is >= 100 and <= 107)
                    {
                        _pen.Bg = p - 100 + 8;
                    }

                    break;
            }
        }
    }

    private int ReadExtendedColour(ref int i)
    {
        if (i + 1 >= _params.Count)
        {
            return int.MinValue;
        }

        int kind = _params[i + 1];
        if (kind == 5 && i + 2 < _params.Count)
        {
            i += 2;
            return Math.Clamp(_params[i], 0, 255);
        }

        if (kind == 2 && i + 4 < _params.Count)
        {
            int r = Math.Clamp(_params[i + 2], 0, 255);
            int g = Math.Clamp(_params[i + 3], 0, 255);
            int b = Math.Clamp(_params[i + 4], 0, 255);
            i += 4;
            return 0x1000000 | (r << 16) | (g << 8) | b;
        }

        i = _params.Count - 1;
        return int.MinValue;
    }

    private void EndOsc()
    {
        string text = _osc.ToString();
        _osc.Clear();
        _state = State.Ground;

        int sep = text.IndexOf(';');
        if (sep > 0 && int.TryParse(text[..sep], out int code) && code is 0 or 1 or 2)
        {
            Title = text[(sep + 1)..];
        }
    }
}
