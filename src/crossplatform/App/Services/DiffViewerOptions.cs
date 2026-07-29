using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The diff-viewer options the port was missing, kept next to
///  <see cref="DiffDisplayOptions"/> rather than inside it because that type
///  belongs to <see cref="DiffTextService"/>.
///
///  <para>Three of them are git arguments the upstream <c>FileViewer</c> passes
///  (<c>--ignore-space-at-eol</c>, <c>-b</c>, <c>--text</c>: the
///  <c>ignoreWhitespaceAtEol</c> / <c>ignoreWhiteSpaces</c> buttons and
///  <c>treatAllFilesAsTextToolStripMenuItem</c>); the fourth is the display-only
///  <c>ShowSyntaxHighlightingInDiff</c> setting.</para>
///
///  <para>Like <see cref="DiffTextService.Session"/> this is a process-wide instance
///  shared by every diff view, but since M69 the pair is no longer merely
///  session-scoped: <see cref="EnsureRestored"/> seeds both from
///  <c>view-prefs.json</c> and <see cref="Persist"/> writes them back, so the whole
///  strip comes back as the user left it.</para>
/// </summary>
public sealed class DiffViewerOptions
{
    /// <summary>The instance shared by the diff views.</summary>
    public static DiffViewerOptions Session { get; } = new();

    /// <summary>Upstream <c>ignoreWhitespaceAtEol</c> — <c>git diff --ignore-space-at-eol</c>.</summary>
    public bool IgnoreWhitespaceAtEol { get; set; }

    /// <summary>Upstream <c>ignoreWhiteSpaces</c> — <c>git diff -b</c> (amount of whitespace).</summary>
    public bool IgnoreWhitespaceChange { get; set; }

    /// <summary>Upstream <c>treatAllFilesAsTextToolStripMenuItem</c> — <c>git diff --text</c>.</summary>
    public bool TreatAllFilesAsText { get; set; }

    /// <summary>Upstream <c>showSyntaxHighlighting</c> — colours the patch content.</summary>
    public bool SyntaxHighlighting { get; set; }

    /// <summary>Whether any of the three git flags is on.</summary>
    public bool HasGitFlags => IgnoreWhitespaceAtEol || IgnoreWhitespaceChange || TreatAllFilesAsText;

    private static readonly ViewPrefsService PrefsService = new();

    private static bool _restored;

    /// <summary>
    ///  Seeds <see cref="Session"/> and <see cref="DiffTextService.Session"/> from
    ///  <c>view-prefs.json</c>, once per process.
    ///
    ///  <para>Called at the very top of <c>DiffView</c>'s constructor body rather than
    ///  from a static initialiser of either singleton: the view aliases both objects in
    ///  its FIELD initialisers and only reads their properties in the constructor body
    ///  (to seed each toggle button's checked state), so this is the last moment that is
    ///  both early enough for every reader and independent of the order in which the two
    ///  singletons happen to be touched first. Idempotent, so the second and later views
    ///  — including the ones <c>CommitDialog</c> opens — do not re-read the file and
    ///  cannot clobber a toggle the user has flipped since.</para>
    /// </summary>
    public static void EnsureRestored()
    {
        if (_restored)
        {
            return;
        }

        // Set before loading: a throwing load must not leave the flag clear and have
        // every later view retry (and re-apply defaults over live toggles).
        _restored = true;

        DiffPrefs prefs = PrefsService.Load().Diff;
        DiffDisplayOptions display = DiffTextService.Session;

        display.ShowEntireFile = prefs.ShowEntireFile;
        display.IgnoreWhitespace = prefs.IgnoreWhitespace;
        display.ShowNonPrinting = prefs.ShowNonPrinting;
        display.WordDiff = prefs.WordDiff;
        display.EncodingName = prefs.EncodingName;
        display.ContextLines = prefs.ContextLines;
        display.FontSize = prefs.FontSize;

        Session.IgnoreWhitespaceAtEol = prefs.IgnoreWhitespaceAtEol;
        Session.IgnoreWhitespaceChange = prefs.IgnoreWhitespaceChange;
        Session.TreatAllFilesAsText = prefs.TreatAllFilesAsText;
        Session.SyntaxHighlighting = prefs.SyntaxHighlighting;
    }

    /// <summary>
    ///  Writes the current state of both singletons back to <c>view-prefs.json</c>.
    ///  Called after every toolbar/menu change; the write goes through
    ///  <see cref="ViewPrefsService.Update"/> so it cannot revert another surface's
    ///  group (the file also carries the file-history switches, the left panel filters
    ///  and the filter MRU).
    /// </summary>
    public static void Persist()
    {
        DiffDisplayOptions display = DiffTextService.Session;

        PrefsService.Update(prefs => prefs.Diff = new DiffPrefs
        {
            ShowEntireFile = display.ShowEntireFile,
            IgnoreWhitespace = display.IgnoreWhitespace,
            ShowNonPrinting = display.ShowNonPrinting,
            WordDiff = display.WordDiff,
            EncodingName = display.EncodingName,
            ContextLines = display.ContextLines,
            FontSize = display.FontSize,
            IgnoreWhitespaceAtEol = Session.IgnoreWhitespaceAtEol,
            IgnoreWhitespaceChange = Session.IgnoreWhitespaceChange,
            TreatAllFilesAsText = Session.TreatAllFilesAsText,
            SyntaxHighlighting = Session.SyntaxHighlighting,
        });
    }
}

/// <summary>
///  Runs the same diff as <see cref="DiffTextService"/>, plus the extra git flags
///  of <see cref="DiffViewerOptions"/>.
///
///  <para>It exists because <see cref="DiffTextService.BuildArguments"/> knows a
///  fixed set of options: rather than fork that type, this one takes its argument
///  list and splices the extra flags in, so the two stay in sync by
///  construction. When no extra flag is set it delegates outright.</para>
/// </summary>
public static class ExtendedDiffTextService
{
    /// <summary>
    ///  <see cref="DiffTextService.BuildArguments"/> with the extra flags inserted
    ///  before the revision arguments.
    /// </summary>
    public static List<string> BuildArguments(
        DiffTextRequest request,
        DiffDisplayOptions options,
        DiffViewerOptions extra)
    {
        List<string> args = DiffTextService.BuildArguments(request, options);
        if (!extra.HasGitFlags)
        {
            return args;
        }

        List<string> flags = [];
        if (extra.IgnoreWhitespaceAtEol)
        {
            flags.Add("--ignore-space-at-eol");
        }

        if (extra.IgnoreWhitespaceChange)
        {
            flags.Add("-b");
        }

        if (extra.TreatAllFilesAsText)
        {
            flags.Add("--text");
        }

        // "--find-renames" is emitted by BuildArguments for every request kind and
        // always precedes the revisions, so it is a stable splice point.
        int at = args.IndexOf("--find-renames");
        args.InsertRange(at < 0 ? args.Count : at + 1, flags);

        return args;
    }

    /// <summary>Renders the git command line as text, for the status bar.</summary>
    public static string DescribeCommand(
        DiffTextRequest request,
        DiffDisplayOptions options,
        DiffViewerOptions extra) =>
        "git " + string.Join(' ', BuildArguments(request, options, extra).Select(Quote));

    /// <summary>
    ///  Runs git and returns the patch text, decoded with the encoding named by
    ///  <paramref name="options"/>. Never throws for a failed git run: git's own
    ///  error output is returned as the text, so the pane can show it. Must not be
    ///  called from the UI thread.
    /// </summary>
    public static async Task<string> GetDiffTextAsync(
        DiffTextRequest request,
        DiffDisplayOptions options,
        DiffViewerOptions extra,
        CancellationToken cancellationToken = default)
    {
        if (!extra.HasGitFlags)
        {
            return await DiffTextService.GetDiffTextAsync(request, options, cancellationToken)
                .ConfigureAwait(false);
        }

        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = request.RepoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in BuildArguments(request, options, extra))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using Process process = new() { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return "Cannot run git: " + ex.Message;
        }

        // Raw bytes, so the caller-selected encoding decides how the file content
        // is interpreted (git has no diff-content encoding switch).
        using MemoryStream stdout = new();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await copy.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string error = await stderr.ConfigureAwait(false);
        Encoding encoding = DiffTextService.ResolveEncoding(options.EncodingName);
        string text = encoding.GetString(stdout.ToArray());

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.IsNullOrWhiteSpace(error)
                ? "(no textual diff — binary file or no changes)"
                : error;
        }

        return text;
    }

    /// <summary>
    ///  Reads a file's content at a revision as text, decoded with
    ///  <paramref name="encodingName"/> — the data behind "Copy new/old version".
    ///  Pass <see langword="null"/> for <paramref name="rev"/> to read the
    ///  working-tree copy. Must not be called from the UI thread.
    /// </summary>
    public static async Task<string> GetFileTextAsync(
        string repoPath,
        string? rev,
        string path,
        string encodingName,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, rev, path, cancellationToken)
            .ConfigureAwait(false);

        return DiffTextService.ResolveEncoding(encodingName).GetString(bytes);
    }

    private static string Quote(string arg) =>
        arg.Contains(' ', StringComparison.Ordinal)
            ? string.Format(CultureInfo.InvariantCulture, "\"{0}\"", arg)
            : arg;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process already went away; nothing to clean up.
        }
    }
}
