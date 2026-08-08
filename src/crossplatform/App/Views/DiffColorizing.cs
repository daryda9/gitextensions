using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>What one line of a unified diff is, as far as colouring is concerned.</summary>
internal enum DiffLineKind
{
    /// <summary>An unchanged context line (or anything the classifier does not recognise).</summary>
    Context,

    /// <summary>A file/meta header: <c>diff --git</c>, <c>index</c>, <c>+++</c>, <c>---</c>, <c>rename</c>…</summary>
    Header,

    /// <summary>A <c>@@ … @@</c> hunk header.</summary>
    Hunk,

    /// <summary>An added line.</summary>
    Added,

    /// <summary>A removed line.</summary>
    Removed,
}

/// <summary>
///  The prefix rules that turn a raw patch line into a <see cref="DiffLineKind"/>.
///  Shared because the diff view needs the same answer twice: once to collect the
///  hunk-header line numbers for ▲/▼ navigation, and once per VISIBLE line inside
///  the colorizing transformer.
/// </summary>
internal static class DiffLineClassifier
{
    /// <summary>Classifies one patch line by its leading characters.</summary>
    public static DiffLineKind Of(string line)
    {
        // Order matters: "+++"/"---" are headers, not an added/removed line, so
        // they have to be tested before the single-character prefixes.
        if (line.StartsWith("+++", StringComparison.Ordinal) ||
            line.StartsWith("---", StringComparison.Ordinal) ||
            line.StartsWith("diff ", StringComparison.Ordinal) ||
            line.StartsWith("index ", StringComparison.Ordinal) ||
            line.StartsWith("new file", StringComparison.Ordinal) ||
            line.StartsWith("deleted file", StringComparison.Ordinal) ||
            line.StartsWith("rename ", StringComparison.Ordinal) ||
            line.StartsWith("copy ", StringComparison.Ordinal) ||
            line.StartsWith("similarity ", StringComparison.Ordinal))
        {
            return DiffLineKind.Header;
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return DiffLineKind.Hunk;
        }

        if (line.StartsWith('+'))
        {
            return DiffLineKind.Added;
        }

        return line.StartsWith('-') ? DiffLineKind.Removed : DiffLineKind.Context;
    }

    /// <summary>
    ///  Whether the line carries file content the syntax highlighter should look
    ///  at — everything but the headers, whose colour is diff syntax, not code.
    /// </summary>
    public static bool IsCode(DiffLineKind kind)
        => kind is DiffLineKind.Context or DiffLineKind.Added or DiffLineKind.Removed;

    /// <summary>
    ///  Index of the first character of actual file content: the leading
    ///  <c>+</c>/<c>-</c>/space of a unified diff is not part of the code.
    /// </summary>
    public static int ContentStart(string line)
        => line.Length > 0 && line[0] is '+' or '-' or ' ' ? 1 : 0;
}

/// <summary>
///  The diff pane's colours, resolved from the application palette on first use
///  and then cached.
///
///  <para>The cached value is the resource brush INSTANCE: <c>ThemeManager</c>
///  mutates a palette brush's <c>Color</c> in place rather than replacing it, so a
///  hot theme switch reaches these without invalidating the cache. Copying into a
///  fresh <see cref="SolidColorBrush"/> here would instead freeze the pane on
///  whichever theme happened to be active first.</para>
/// </summary>
internal static class DiffPalette
{
    private static IBrush? _header;
    private static IBrush? _hunk;
    private static IBrush? _added;
    private static IBrush? _removed;
    private static IBrush? _keyword;
    private static IBrush? _string;
    private static IBrush? _comment;
    private static IBrush? _number;
    private static IBrush? _preprocessor;

    /// <summary>File/meta header lines.</summary>
    public static IBrush Header => _header ??= B("App.TextDim");

    /// <summary><c>@@</c> hunk headers.</summary>
    public static IBrush Hunk => _hunk ??= B("App.Accent");

    // App.DiffAdded / App.DiffRemoved rather than literals: as literals "tuned for
    // the dark palette" the pair scored 1.88:1 and 2.90:1 against the light theme's
    // #F3F3F3 — measurably unreadable. The dark values in ThemeManager are exactly
    // those two literals, so the dark theme is unchanged.

    /// <summary>Added lines.</summary>
    public static IBrush Added => _added ??= B("App.DiffAdded");

    /// <summary>Removed lines.</summary>
    public static IBrush Removed => _removed ??= B("App.DiffRemoved");

    // Syntax highlighting repaints the content of a +/- line with the token
    // colours, so the line's identity moves to a background tint (which is how the
    // original marks added/removed lines too). Literal, because the palette has no
    // resource for a translucent wash.

    /// <summary>Wash behind a syntax-highlighted added line.</summary>
    public static IBrush AddedTint { get; } = new SolidColorBrush(Color.FromArgb(0x28, 0x6A, 0xC7, 0x76));

    /// <summary>Wash behind a syntax-highlighted removed line.</summary>
    public static IBrush RemovedTint { get; } = new SolidColorBrush(Color.FromArgb(0x28, 0xE0, 0x6C, 0x6C));

    // Search highlight: amber for every occurrence, a stronger amber for the one
    // the ▲/▼ navigation currently sits on. Literal for the same reason as the tints.

    /// <summary>Wash behind every occurrence of the search term.</summary>
    public static IBrush Match { get; } = new SolidColorBrush(Color.FromArgb(0x70, 0xC8, 0x9B, 0x2C));

    /// <summary>Wash behind the occurrence the ▲/▼ navigation currently sits on.</summary>
    public static IBrush CurrentMatch { get; } = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x2E));

    /// <summary>The colour a syntax token of <paramref name="kind"/> is painted in.</summary>
    public static IBrush Token(SyntaxTokenKind kind) => kind switch
    {
        SyntaxTokenKind.Keyword => _keyword ??= B("App.TokenKeyword"),
        SyntaxTokenKind.String => _string ??= B("App.TokenString"),
        SyntaxTokenKind.Comment => _comment ??= B("App.TokenComment"),
        SyntaxTokenKind.Number => _number ??= B("App.TokenNumber"),
        _ => _preprocessor ??= B("App.TokenPreprocessor"),
    };

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;
}

/// <summary>
///  Paints the diff: +/- / <c>@@</c> / header colours, the added/removed tints and
///  the syntax tokens produced by <see cref="DiffSyntaxHighlighter"/>.
///
///  <para>This is the whole reason the pane moved onto AvaloniaEdit. A
///  <see cref="DocumentColorizingTransformer"/> is invoked once per <b>visible</b>
///  line, so the cost of colouring — and of the syntax scan behind it — no longer
///  scales with the size of the patch. The previous renderer built one
///  <c>Run</c> per line (plus one per token, plus one per search hit) up front,
///  which is what made "Show entire file" on a few thousand lines take seconds.</para>
///
///  <para>The one thing virtualization takes away is the running state of the
///  scanner: whether a line starts inside a <c>/* … */</c> block can only be known
///  by having read everything above it. <see cref="EnsureScanned"/> therefore keeps
///  a lazily grown, monotonically advancing table of that single bit — a linear
///  scan that happens at most once per line per render, and only for the lines the
///  user actually reaches.</para>
/// </summary>
internal sealed class DiffLineColorizer : DocumentColorizingTransformer
{
    // Reused across lines: the scanner allocates nothing per character and the
    // caller is expected to keep the span list, so colouring a screenful of text
    // costs no allocations at all.
    private readonly List<SyntaxSpan> _spans = [];

    // Two states on purpose: _scanState belongs to the forward-only prepass in
    // EnsureScanned, _lineState is seeded from the table for the line being
    // painted. Sharing one would make a repaint of an already-scanned line move
    // the prepass's cursor.
    private readonly SyntaxState _scanState = new();
    private readonly SyntaxState _lineState = new();

    // _blockComment[n - 1] == "line n starts inside a block comment", valid for
    // the first _scanned entries.
    private bool[] _blockComment = [];
    private int _scanned;

    private SyntaxLanguage? _language;

    /// <summary>
    ///  The language the patch's content is tokenized as, or <see langword="null"/>
    ///  to paint the diff structure only. Setting it discards the block-comment
    ///  table, so the next repaint rescans.
    /// </summary>
    public SyntaxLanguage? Language
    {
        get => _language;
        set
        {
            _language = value;
            Invalidate();
        }
    }

    /// <summary>
    ///  Forgets the scanned state. Must be called whenever the document changes:
    ///  the table is indexed by line number, and those now mean something else.
    /// </summary>
    public void Invalidate()
    {
        _scanned = 0;
        _scanState.Reset();
    }

    /// <inheritdoc/>
    protected override void ColorizeLine(DocumentLine line)
    {
        TextDocument document = CurrentContext.Document;
        string text = document.GetText(line);
        DiffLineKind kind = DiffLineClassifier.Of(text);

        IBrush? foreground = kind switch
        {
            DiffLineKind.Header => DiffPalette.Header,
            DiffLineKind.Hunk => DiffPalette.Hunk,
            DiffLineKind.Added => DiffPalette.Added,
            DiffLineKind.Removed => DiffPalette.Removed,
            _ => null,
        };

        if (foreground is not null && line.Length > 0)
        {
            Paint(line.Offset, line.EndOffset, foreground, background: null);
        }

        if (_language is null || !DiffLineClassifier.IsCode(kind) || line.Length == 0)
        {
            return;
        }

        // A tokenized +/- line moves its identity to a background tint, because its
        // foreground now belongs to the tokens.
        IBrush? tint = kind switch
        {
            DiffLineKind.Added => DiffPalette.AddedTint,
            DiffLineKind.Removed => DiffPalette.RemovedTint,
            _ => null,
        };

        if (tint is not null)
        {
            Paint(line.Offset, line.EndOffset, foreground: null, tint);
        }

        EnsureScanned(document, line.LineNumber);
        _lineState.InBlockComment = _blockComment[line.LineNumber - 1];
        DiffSyntaxHighlighter.Tokenize(
            _language, text, DiffLineClassifier.ContentStart(text), _lineState, _spans);

        foreach (SyntaxSpan span in _spans)
        {
            int start = line.Offset + span.Start;
            int end = Math.Min(start + span.Length, line.EndOffset);
            if (end > start)
            {
                Paint(start, end, DiffPalette.Token(span.Kind), background: null);
            }
        }
    }

    private void Paint(int start, int end, IBrush? foreground, IBrush? background)
        => ChangeLinePart(start, end, element =>
        {
            if (foreground is not null)
            {
                element.TextRunProperties.SetForegroundBrush(foreground);
            }

            if (background is not null)
            {
                element.TextRunProperties.SetBackgroundBrush(background);
            }
        });

    // Advances the block-comment table to (at least) lineNumber. Forward-only and
    // idempotent: scrolling straight to the middle of a big file pays for the lines
    // above once, and every later repaint of that region is a table lookup.
    private void EnsureScanned(TextDocument document, int lineNumber)
    {
        if (_blockComment.Length < document.LineCount)
        {
            _blockComment = new bool[document.LineCount];
            _scanned = 0;
            _scanState.Reset();
        }

        while (_scanned < lineNumber)
        {
            int next = _scanned + 1;
            _blockComment[next - 1] = _scanState.InBlockComment;

            string text = document.GetText(document.GetLineByNumber(next));
            DiffLineKind kind = DiffLineClassifier.Of(text);
            if (DiffLineClassifier.IsCode(kind))
            {
                DiffSyntaxHighlighter.Tokenize(
                    _language!, text, DiffLineClassifier.ContentStart(text), _scanState, _spans);
            }

            _scanned = next;
        }
    }
}

/// <summary>
///  Washes the occurrences of the find bar's term, the current one in a stronger
///  amber. Also per visible line, so a search on a huge patch no longer has to
///  choose between highlighting and staying responsive — the caps the previous
///  renderer needed (one Run per hit, all built up front) are gone.
/// </summary>
internal sealed class DiffSearchColorizer : DocumentColorizingTransformer
{
    private IReadOnlyList<DiffSearchMatch> _matches = [];
    private int _current = -1;

    /// <summary>Replaces the highlighted set. The list must be ordered by line.</summary>
    public void SetMatches(IReadOnlyList<DiffSearchMatch> matches)
    {
        _matches = matches;
        _current = -1;
    }

    /// <summary>Moves the "current match" marker (-1 for none).</summary>
    public void SetCurrent(int index) => _current = index;

    /// <inheritdoc/>
    protected override void ColorizeLine(DocumentLine line)
    {
        for (int i = FirstOn(line.LineNumber); i < _matches.Count && _matches[i].Line == line.LineNumber; i++)
        {
            int start = line.Offset + Math.Min(_matches[i].Column, line.Length);
            int end = Math.Min(start + _matches[i].Length, line.EndOffset);
            if (end <= start)
            {
                continue;
            }

            IBrush brush = i == _current ? DiffPalette.CurrentMatch : DiffPalette.Match;
            ChangeLinePart(start, end, element => element.TextRunProperties.SetBackgroundBrush(brush));
        }
    }

    // Binary search rather than a scan: the transformer runs per visible line, so a
    // linear walk of a 20 000-entry match list would be paid ~50 times per frame.
    private int FirstOn(int lineNumber)
    {
        int low = 0;
        int high = _matches.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_matches[mid].Line < lineNumber)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}

/// <summary>
///  One occurrence of the find bar's term: <paramref name="Line"/> is a 1-based
///  document line, <paramref name="Column"/> a 0-based index into it.
/// </summary>
internal readonly record struct DiffSearchMatch(int Line, int Column, int Length);
