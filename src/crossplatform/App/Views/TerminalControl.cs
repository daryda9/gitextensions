using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Renders a <see cref="TerminalEmulator"/> screen and forwards keyboard input to the
///  attached <see cref="PtyProcess"/>. The control owns nothing but pixels and keys:
///  the shell lives in the PTY, the screen state lives in the emulator.
///  <para>The PTY reader thread parses into the emulator and only requests a redraw on
///  the UI thread (coalesced at ~60 Hz), so no shell output can block the UI.</para>
/// </summary>
public sealed class TerminalControl : Control
{
    private static readonly Color[] s_palette = BuildPalette();

    private readonly TerminalEmulator _emulator = new(80, 24);
    private readonly Typeface _typeface;
    private readonly Typeface _typefaceBold;
    private readonly DispatcherTimer _blink;
    private TerminalCell[] _rowBuffer = new TerminalCell[80];
    private PtyProcess? _pty;
    private double _charWidth;
    private double _lineHeight;
    private double _baselineOffset;
    private int _scrollOffset;
    private bool _cursorOn = true;
    private bool _redrawQueued;
    private bool _exited;

    public TerminalControl()
    {
        _typeface = new Typeface(new FontFamily("DejaVu Sans Mono,Liberation Mono,monospace,Consolas,Menlo"));
        _typefaceBold = new Typeface(
            new FontFamily("DejaVu Sans Mono,Liberation Mono,monospace,Consolas,Menlo"),
            FontStyle.Normal,
            FontWeight.Bold);
        FontSize = 13;
        Focusable = true;
        ClipToBounds = true;
        MeasureFont();

        _emulator.Respond += text => _pty?.Write(text);
        _emulator.Bell += () => { };

        _blink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
        {
            _cursorOn = !_cursorOn;
            if (IsFocused)
            {
                InvalidateVisual();
            }
        });
    }

    /// <summary>Font size in device-independent pixels; changing it re-measures the grid.</summary>
    public double FontSize { get; set; }

    /// <summary>Default foreground, used for cells without an explicit SGR colour.</summary>
    public IBrush DefaultForeground { get; set; } = Brushes.Gainsboro;

    /// <summary>Default background, used for cells without an explicit SGR colour.</summary>
    public IBrush DefaultBackground { get; set; } = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));

    /// <summary>Brush of the block cursor.</summary>
    public IBrush CursorBrush { get; set; } = Brushes.DarkOrange;

    /// <summary>Raised (on the UI thread) when the shell terminates.</summary>
    public event Action? ShellExited;

    /// <summary>Current window title as set by the shell through OSC 0/2.</summary>
    public string Title => _emulator.Title;

    /// <summary>True while a shell is attached and running.</summary>
    public bool IsRunning => _pty?.IsRunning == true && !_exited;

    /// <summary>Starts a shell in <paramref name="workingDirectory"/>. Any previous
    /// session is terminated first. Throws if the PTY cannot be created.</summary>
    public void StartShell(string workingDirectory)
    {
        StopShell();
        _exited = false;
        _scrollOffset = 0;

        (int cols, int rows) = GridSize();
        _emulator.Resize(cols, rows);

        PtyProcess pty = new();
        pty.Output += OnOutput;
        pty.Exited += OnExited;
        pty.Start(workingDirectory, cols, rows);
        _pty = pty;

        _blink.Start();
        InvalidateVisual();
    }

    /// <summary>Terminates the shell and releases the PTY.</summary>
    public void StopShell()
    {
        _blink.Stop();
        PtyProcess? pty = _pty;
        _pty = null;
        if (pty is not null)
        {
            pty.Output -= OnOutput;
            pty.Exited -= OnExited;
            pty.Dispose();
        }
    }

    /// <summary>Sends text (for example a pasted command) to the shell.</summary>
    public void Send(string text) => _pty?.Write(text);

    private void OnOutput(byte[] data, int count)
    {
        lock (_emulator.SyncRoot)
        {
            _emulator.Feed(data, count);
        }

        RequestRedraw();
    }

    private void OnExited()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _exited = true;
            _blink.Stop();
            InvalidateVisual();
            ShellExited?.Invoke();
        });
    }

    private void RequestRedraw()
    {
        if (_redrawQueued)
        {
            return;
        }

        _redrawQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _redrawQueued = false;
                _cursorOn = true;
                InvalidateVisual();
            },
            DispatcherPriority.Background);
    }

    private void MeasureFont()
    {
        FormattedText probe = new(
            "MMMMMMMMMM",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            FontSize,
            Brushes.White);
        _charWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace / 10.0);
        _lineHeight = Math.Max(1, Math.Ceiling(probe.Height));
        _baselineOffset = 0;
    }

    private (int Cols, int Rows) GridSize()
    {
        int cols = Math.Clamp((int)Math.Floor((Bounds.Width - 8) / _charWidth), 20, 500);
        int rows = Math.Clamp((int)Math.Floor((Bounds.Height - 6) / _lineHeight), 4, 300);
        return (cols, rows);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        (int cols, int rows) = GridSize();
        lock (_emulator.SyncRoot)
        {
            if (cols != _emulator.Cols || rows != _emulator.Rows)
            {
                _emulator.Resize(cols, rows);
                if (_rowBuffer.Length < cols)
                {
                    _rowBuffer = new TerminalCell[cols];
                }

                _pty?.Resize(cols, rows);
            }
        }

        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        lock (_emulator.SyncRoot)
        {
            if (_emulator.AltScreen)
            {
                // Full-screen programs get the wheel as arrow keys instead of scrolling
                // our scrollback, which would be meaningless there.
                string key = e.Delta.Y > 0 ? "\x1b[A" : "\x1b[B";
                _pty?.Write(string.Concat(Enumerable.Repeat(key, 3)));
                e.Handled = true;
                return;
            }

            int max = _emulator.ScrollbackCount;
            _scrollOffset = Math.Clamp(_scrollOffset + (e.Delta.Y > 0 ? 3 : -3), 0, max);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!string.IsNullOrEmpty(e.Text) && _pty is not null)
        {
            _scrollOffset = 0;
            _pty.Write(e.Text!);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_pty is null || e.Handled)
        {
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        string? send = null;

        if (ctrl && shift && e.Key is Key.C)
        {
            return;   // leave Ctrl+Shift+C to the host (copy) rather than SIGINT
        }

        bool appCursor;
        lock (_emulator.SyncRoot)
        {
            appCursor = _emulator.ApplicationCursorKeys;
        }

        string csi = appCursor ? "\x1bO" : "\x1b[";

        switch (e.Key)
        {
            case Key.Enter:
                send = "\r";
                break;

            case Key.Back:
                send = "\x7f";
                break;

            case Key.Tab:
                send = "\t";
                break;

            case Key.Escape:
                send = "\x1b";
                break;

            case Key.Up:
                send = csi + "A";
                break;

            case Key.Down:
                send = csi + "B";
                break;

            case Key.Right:
                send = csi + "C";
                break;

            case Key.Left:
                send = csi + "D";
                break;

            case Key.Home:
                send = csi + "H";
                break;

            case Key.End:
                send = csi + "F";
                break;

            case Key.Insert:
                send = "\x1b[2~";
                break;

            case Key.Delete:
                send = "\x1b[3~";
                break;

            case Key.PageUp:
                send = "\x1b[5~";
                break;

            case Key.PageDown:
                send = "\x1b[6~";
                break;

            case >= Key.F1 and <= Key.F4:
                send = "\x1bO" + (char)('P' + (e.Key - Key.F1));
                break;

            case Key.Space when ctrl:
                send = "\0";
                break;

            case >= Key.A and <= Key.Z when ctrl && !alt:
                send = ((char)(e.Key - Key.A + 1)).ToString();
                break;

            case >= Key.A and <= Key.Z when alt && !ctrl:
                send = "\x1b" + (char)((shift ? 'A' : 'a') + (e.Key - Key.A));
                break;
        }

        if (send is not null)
        {
            _scrollOffset = 0;
            _pty.Write(send);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(DefaultBackground, new Rect(Bounds.Size));

        int rows;
        int cols;
        int scrollback;
        int offset;
        int cursorX;
        int cursorY;
        bool cursorVisible;

        lock (_emulator.SyncRoot)
        {
            rows = _emulator.Rows;
            cols = _emulator.Cols;
            scrollback = _emulator.ScrollbackCount;
            offset = Math.Clamp(_scrollOffset, 0, scrollback);
            cursorX = _emulator.CursorX;
            cursorY = _emulator.CursorY;
            cursorVisible = _emulator.CursorVisible;

            if (_rowBuffer.Length < cols)
            {
                _rowBuffer = new TerminalCell[cols];
            }

            double left = 4;
            double top = 3;

            for (int screenRow = 0; screenRow < rows; screenRow++)
            {
                int logical = screenRow - offset;
                if (logical >= 0)
                {
                    _emulator.CopyRow(logical, _rowBuffer);
                }
                else
                {
                    _emulator.CopyScrollbackRow(scrollback + logical, _rowBuffer);
                }

                DrawRow(context, _rowBuffer, cols, left, top + (screenRow * _lineHeight));
            }

            if (cursorVisible && offset == 0 && (_cursorOn || !IsFocused) && !_exited)
            {
                Rect cursor = new(
                    left + (cursorX * _charWidth),
                    top + (cursorY * _lineHeight),
                    _charWidth,
                    _lineHeight);

                if (IsFocused)
                {
                    Color cursorColour = CursorBrush is ISolidColorBrush scb ? scb.Color : Colors.DarkOrange;
                    context.FillRectangle(new SolidColorBrush(cursorColour, 0.55), cursor);
                }
                else
                {
                    context.DrawRectangle(new Pen(CursorBrush, 1), cursor);
                }
            }
        }
    }

    private void DrawRow(DrawingContext context, TerminalCell[] row, int cols, double left, double top)
    {
        int x = 0;
        while (x < cols)
        {
            TerminalCell first = row[x];
            int runStart = x;
            while (x < cols
                   && row[x].Fg == first.Fg
                   && row[x].Bg == first.Bg
                   && row[x].Flags == first.Flags)
            {
                x++;
            }

            int length = x - runStart;
            (Color fg, Color bg, bool hasBg) = ResolveColours(first);

            double runLeft = left + (runStart * _charWidth);
            double runWidth = length * _charWidth;

            if (hasBg)
            {
                context.FillRectangle(new SolidColorBrush(bg), new Rect(runLeft, top, runWidth, _lineHeight));
            }

            StringBuilder text = new(length);
            bool anyGlyph = false;
            for (int i = 0; i < length; i++)
            {
                char ch = row[runStart + i].Ch;
                if (ch == '\0')
                {
                    ch = ' ';
                }

                if (ch != ' ')
                {
                    anyGlyph = true;
                }

                text.Append(ch);
            }

            if (anyGlyph)
            {
                bool bold = first.Flags.HasFlag(CellFlags.Bold);
                FormattedText formatted = new(
                    text.ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    bold ? _typefaceBold : _typeface,
                    FontSize,
                    new SolidColorBrush(fg));

                context.DrawText(formatted, new Point(runLeft, top + _baselineOffset));

                if (first.Flags.HasFlag(CellFlags.Underline))
                {
                    double y = top + _lineHeight - 1.5;
                    context.DrawLine(new Pen(new SolidColorBrush(fg), 1), new Point(runLeft, y), new Point(runLeft + runWidth, y));
                }
            }
        }
    }

    private (Color Fg, Color Bg, bool HasBg) ResolveColours(TerminalCell cell)
    {
        Color defaultFg = DefaultForeground is ISolidColorBrush sf ? sf.Color : Colors.Gainsboro;
        Color defaultBg = DefaultBackground is ISolidColorBrush sb ? sb.Color : Color.FromRgb(0x1B, 0x1B, 0x1B);

        Color fg = cell.Fg < 0 ? defaultFg : ToColor(cell.Fg);
        Color bg = cell.Bg < 0 ? defaultBg : ToColor(cell.Bg);
        bool hasBg = cell.Bg >= 0;

        if (cell.Flags.HasFlag(CellFlags.Bold) && cell.Fg is >= 0 and < 8)
        {
            fg = ToColor(cell.Fg + 8);
        }

        if (cell.Flags.HasFlag(CellFlags.Inverse))
        {
            (fg, bg) = (bg, fg);
            hasBg = true;
        }

        if (cell.Flags.HasFlag(CellFlags.Dim))
        {
            fg = Color.FromArgb(fg.A, (byte)(fg.R * 0.65), (byte)(fg.G * 0.65), (byte)(fg.B * 0.65));
        }

        return (fg, bg, hasBg);
    }

    private static Color ToColor(int value)
    {
        if ((value & 0x1000000) != 0)
        {
            return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }

        return s_palette[Math.Clamp(value, 0, 255)];
    }

    private static Color[] BuildPalette()
    {
        Color[] palette = new Color[256];

        // The 16 ANSI colours: xterm defaults, slightly brightened for dark UIs.
        Color[] basics =
        [
            Color.FromRgb(0x2E, 0x34, 0x36), Color.FromRgb(0xCC, 0x00, 0x00),
            Color.FromRgb(0x4E, 0x9A, 0x06), Color.FromRgb(0xC4, 0xA0, 0x00),
            Color.FromRgb(0x34, 0x65, 0xA4), Color.FromRgb(0x75, 0x50, 0x7B),
            Color.FromRgb(0x06, 0x98, 0x9A), Color.FromRgb(0xD3, 0xD7, 0xCF),
            Color.FromRgb(0x55, 0x57, 0x53), Color.FromRgb(0xEF, 0x29, 0x29),
            Color.FromRgb(0x8A, 0xE2, 0x34), Color.FromRgb(0xFC, 0xE9, 0x4F),
            Color.FromRgb(0x72, 0x9F, 0xCF), Color.FromRgb(0xAD, 0x7F, 0xA8),
            Color.FromRgb(0x34, 0xE2, 0xE2), Color.FromRgb(0xEE, 0xEE, 0xEC),
        ];
        Array.Copy(basics, palette, 16);

        byte[] steps = [0x00, 0x5F, 0x87, 0xAF, 0xD7, 0xFF];
        for (int i = 0; i < 216; i++)
        {
            palette[16 + i] = Color.FromRgb(steps[i / 36], steps[i / 6 % 6], steps[i % 6]);
        }

        for (int i = 0; i < 24; i++)
        {
            byte v = (byte)(8 + (i * 10));
            palette[232 + i] = Color.FromRgb(v, v, v);
        }

        return palette;
    }
}
