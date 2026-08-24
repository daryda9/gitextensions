using System.Diagnostics;
using GitExtensions.Avalonia.Services;

// Regression suite for DiffSyntaxHighlighter.Tokenize, the per-line scanner behind
// the diff pane's syntax colouring.
//
// Usage: dotnet run --project Tests/SyntaxTokenizeRegression/SyntaxTokenizeRegression.Harness.csproj
//
// Exit code 0 means every call returned and every span invariant held; any other
// value means at least one broke, and each broken one is printed.
//
// Why this exists: Tokenize runs on the UI THREAD, inside AvaloniaEdit's measure
// pass (DiffLineColorizer.ColorizeLine / EnsureScanned). A scan that does not
// terminate is therefore not a slow scan — it is the whole application frozen at
// 100% CPU with no error anywhere, which is exactly how the defect this suite
// pins was found: a live process 91 minutes into `while (i < line.Length)` on the
// first bare '@' of a patch. IsWordStart accepted '@' but IsWordChar did not, so
// the identifier branch computed an empty word and put the cursor back where it
// started. No output is wrong, no exception is thrown, no build notices: the only
// contract Tokenize has is "returns, with spans inside the line", and that is what
// is asserted here — for every language the scanner knows, over every printable
// ASCII character (plus a few beyond) in the positions that reach every branch.
//
// Termination cannot be asserted from the same thread that hangs, so a watchdog
// thread watches a per-call counter: if no call completes for five seconds, it
// prints WHICH case is stuck and exits non-zero. Without it, a hang would surface
// as run-all.sh's timeout with an empty log — a verdict with no name on it.

const int WatchdogSeconds = 5;

List<string> failures = [];
int cases = 0;

// ---- the watchdog -------------------------------------------------------------

long started = 0;               // incremented before each Tokenize call
long finished = 0;              // incremented after it returns
string current = "(none)";      // the case the main thread is inside

Thread watchdog = new(() =>
{
    long lastSeen = -1;
    Stopwatch since = Stopwatch.StartNew();
    while (true)
    {
        Thread.Sleep(250);
        long s = Interlocked.Read(ref started);
        long f = Interlocked.Read(ref finished);
        if (f >= s)
        {
            // Between calls (or done): nothing to time.
            lastSeen = f;
            since.Restart();
            continue;
        }

        if (s != lastSeen)
        {
            lastSeen = s;
            since.Restart();
            continue;
        }

        if (since.Elapsed.TotalSeconds > WatchdogSeconds)
        {
            Console.WriteLine($"FAIL: Tokenize did not return within {WatchdogSeconds}s — stuck case: {current}");
            Console.Out.Flush();
            Environment.Exit(1);
        }
    }
})
{
    IsBackground = true,
    Name = "tokenize-watchdog",
};
watchdog.Start();

// ---- the languages ------------------------------------------------------------

// One extension per SyntaxLanguage instance Detect can return, covering all
// twelve: the scanner's behaviour differs by keyword set, comment markers,
// quotes, hash-preprocessor and the separate markdown path.
(string Ext, SyntaxLanguage Language)[] languages =
    new[] { "cs", "c", "js", "py", "sh", "go", "rs", "sql", "xml", "yml", "json", "md" }
        .Select(ext => (ext, DiffSyntaxHighlighter.Detect($"file.{ext}")
            ?? throw new InvalidOperationException($"Detect knows no language for .{ext}")))
        .ToArray();

// ---- the sweep ----------------------------------------------------------------

// Every printable ASCII character, plus a tab, plus a few beyond ASCII: an
// accented letter, a two-char surrogate pair, and typography that IsLetter
// answers differently about than ASCII does.
List<string> symbols = [];
for (char c = ' '; c <= '~'; c++)
{
    symbols.Add(c.ToString());
}

symbols.AddRange(["\t", "é", "µ", "€", "中", "𝄞", "«»", "™"]);

foreach ((string ext, SyntaxLanguage language) in languages)
{
    foreach (string sym in symbols)
    {
        // The shapes are chosen to land the symbol in every scanner position:
        // alone; repeated (runs, e.g. markdown emphasis); between words and
        // digits (word-boundary tests index i-1); after a sigil; inside quotes;
        // and at the very end of the line (every lookahead's edge).
        Scan(ext, language, $"+{sym}");
        Scan(ext, language, $"+{sym}{sym}{sym}");
        Scan(ext, language, $"+x{sym}y 1{sym}2");
        Scan(ext, language, $"+@{sym}");
        Scan(ext, language, $"+\"{sym}\" '{sym}'");
        Scan(ext, language, $"+ word {sym}");
    }

    // A block opened on an earlier line: the resume branch (and markdown's
    // inside-a-fence branch) run with InBlockComment already true.
    foreach (string sym in new[] { "*", "/", "-", ">", "`", "@", "x" })
    {
        SyntaxState open = new() { InBlockComment = true };
        Tokenize(ext, language, $"+{sym} rest */ tail", open);
    }
}

// ---- the regression, by name ----------------------------------------------------

// The lines that pinned the UI thread: a bare '@' reaching the identifier branch.
// Before the fix each of these looped forever; the watchdog is what would catch it.
Scan("cs", Lang("cs"), "+        var s = @\"hello\";");
Scan("cs", Lang("cs"), "+    public void M([CallerMemberName] string? @class = null) { }");
Scan("py", Lang("py"), "+@property");
Scan("sql", Lang("sql"), "+SELECT @var FROM t WHERE x = @y");

// '@identifier' is an identifier, not a keyword: '@class' in C# is precisely the
// way to NOT say the keyword. The '@' must be part of the scanned word (or at
// least never colour the word as a keyword).
{
    List<SyntaxSpan> spans = Scan("cs", Lang("cs"), "+var x = @class;");
    int at = "+var x = ".Length;
    if (spans.Any(s => s.Kind == SyntaxTokenKind.Keyword && s.Start >= at))
    {
        failures.Add($"'@class' was coloured as a keyword: {Render(spans)}");
    }
}

// '$' is a word character everywhere here (shell/PHP variables, JS template
// hosts): '$x' and 'x$' must scan as one word and terminate.
Scan("sh", Lang("sh"), "+echo $PATH ${HOME} $1");
Scan("js", Lang("js"), "+const $el = $('#x'); let a$ = 1;");

// ---- verdict --------------------------------------------------------------------

if (failures.Count > 0)
{
    foreach (string failure in failures.Take(50))
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    if (failures.Count > 50)
    {
        Console.WriteLine($"… and {failures.Count - 50} more");
    }

    Console.WriteLine($"FAILED: {failures.Count} of {cases} syntax-tokenize cases");
    return 1;
}

Console.WriteLine(
    $"PASS: {cases} syntax-tokenize cases over {languages.Length} languages — every call returned, every span in bounds");
return 0;

// ---- helpers --------------------------------------------------------------------

SyntaxLanguage Lang(string ext) => languages.First(l => l.Ext == ext).Language;

List<SyntaxSpan> Scan(string ext, SyntaxLanguage language, string line)
    => Tokenize(ext, language, line, new SyntaxState());

List<SyntaxSpan> Tokenize(string ext, SyntaxLanguage language, string line, SyntaxState state)
{
    // ContentStart is 1 for a +/- line, mirroring DiffLineClassifier's callers;
    // hand-rolled here so the suite exercises the scanner, not the classifier.
    int from = line.Length > 0 && line[0] is '+' or '-' ? 1 : 0;

    List<SyntaxSpan> spans = [];
    current = $".{ext} from={from} line={Printable(line)}";
    cases++;
    Interlocked.Increment(ref started);
    DiffSyntaxHighlighter.Tokenize(language, line, from, state, spans);
    Interlocked.Increment(ref finished);

    // The span contract a renderer relies on: ordered, non-overlapping, non-empty,
    // never before `from` and never past the end of the line.
    int previousEnd = from;
    foreach (SyntaxSpan span in spans)
    {
        if (span.Length <= 0)
        {
            failures.Add($"empty span {span} in {current}");
        }

        if (span.Start < previousEnd)
        {
            failures.Add($"span {span} overlaps or is out of order (previous end {previousEnd}) in {current}");
        }

        if (span.Start + span.Length > line.Length)
        {
            failures.Add($"span {span} runs past the line (length {line.Length}) in {current}");
        }

        previousEnd = Math.Max(previousEnd, span.Start + span.Length);
    }

    return spans;
}

static string Printable(string line)
    => string.Concat(line.Select(c => c < ' ' ? $"\\u{(int)c:x4}" : c.ToString()));

static string Render(IEnumerable<SyntaxSpan> spans)
    => string.Join(", ", spans.Select(s => $"[{s.Start}+{s.Length} {s.Kind}]"));
