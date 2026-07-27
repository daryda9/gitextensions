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
///  <c>ShowSyntaxHighlightingInDiff</c> setting. Like
///  <see cref="DiffTextService.Session"/>, they live for the session.</para>
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
