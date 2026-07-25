using System.Diagnostics;
using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>Which comparison a <see cref="DiffTextRequest"/> describes.</summary>
public enum DiffTextKind
{
    /// <summary>A single commit against its first parent (<c>git show</c>).</summary>
    Commit,

    /// <summary>Two commits (<c>git diff base other</c>).</summary>
    Range,

    /// <summary>A commit against the working tree (<c>git diff commit</c>).</summary>
    WorkingTree,
}

/// <summary>The identity of the diff to produce: what to compare and for which path.</summary>
/// <param name="Kind">Which comparison to run.</param>
/// <param name="RepoPath">Repository working directory.</param>
/// <param name="CommitHash">The "new" side (or the commit shown).</param>
/// <param name="BaseHash">The "old" side, for <see cref="DiffTextKind.Range"/>.</param>
/// <param name="Path">Repo-relative path of the file.</param>
/// <param name="OldPath">Previous path, for renames (passed as an extra pathspec).</param>
public sealed record DiffTextRequest(
    DiffTextKind Kind,
    string RepoPath,
    string CommitHash,
    string? BaseHash,
    string Path,
    string? OldPath);

/// <summary>
///  The user-toggleable diff presentation options of the diff toolbar. A single
///  instance (<see cref="DiffTextService.Session"/>) is shared for the lifetime of
///  the process so the toggles persist across selections and view rebuilds, the way
///  the Windows diff viewer keeps its toolbar state.
/// </summary>
public sealed class DiffDisplayOptions
{
    /// <summary>Adds <c>-w</c> to the git diff invocation.</summary>
    public bool IgnoreWhitespace { get; set; }

    /// <summary>Renders spaces/tabs/CR with visible symbols (client-side only).</summary>
    public bool ShowNonPrinting { get; set; }

    /// <summary>Adds <c>--word-diff=plain</c> to the git diff invocation.</summary>
    public bool WordDiff { get; set; }

    /// <summary>Display name of the encoding used to decode git's output.</summary>
    public string EncodingName { get; set; } = DiffTextService.DefaultEncodingName;

    /// <summary>Font size of the diff pane (zoom + / −).</summary>
    public double FontSize { get; set; } = DefaultFontSize;

    /// <summary>The font size the zoom-reset command restores.</summary>
    public const double DefaultFontSize = 12;
}

/// <summary>
///  Produces unified-diff text by invoking <c>git</c> directly, so the toolbar
///  toggles (ignore whitespace, word diff) map onto real git arguments and the
///  output can be decoded with a user-chosen encoding. Blocking/async work here
///  must run off the UI thread.
/// </summary>
public static class DiffTextService
{
    /// <summary>Display name of the default (and initially selected) encoding.</summary>
    public const string DefaultEncodingName = "Unicode (UTF-8)";

    // Only encodings built into the BCL, so no CodePages provider is required.
    private static readonly (string Name, Func<Encoding> Factory)[] EncodingTable =
    [
        (DefaultEncodingName, () => new UTF8Encoding(false)),
        ("Western European (ISO-8859-1)", () => Encoding.Latin1),
        ("US-ASCII", () => Encoding.ASCII),
        ("Unicode (UTF-16 LE)", () => Encoding.Unicode),
        ("Unicode (UTF-16 BE)", () => Encoding.BigEndianUnicode),
        ("Unicode (UTF-32)", () => Encoding.UTF32),
    ];

    /// <summary>Process-wide toolbar state (see <see cref="DiffDisplayOptions"/>).</summary>
    public static DiffDisplayOptions Session { get; } = new();

    /// <summary>The encoding display names offered by the toolbar combo, in order.</summary>
    public static IReadOnlyList<string> EncodingNames { get; } =
        EncodingTable.Select(e => e.Name).ToArray();

    /// <summary>Resolves a display name from <see cref="EncodingNames"/> to an encoding.</summary>
    public static Encoding ResolveEncoding(string? name)
    {
        foreach ((string candidate, Func<Encoding> factory) in EncodingTable)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return factory();
            }
        }

        return new UTF8Encoding(false);
    }

    /// <summary>
    ///  Builds the git argument list for <paramref name="request"/> under
    ///  <paramref name="options"/>. Exposed so the caller can show/log the exact
    ///  command that produced the displayed diff.
    /// </summary>
    public static List<string> BuildArguments(DiffTextRequest request, DiffDisplayOptions options)
    {
        List<string> args = ["--no-pager", "-c", "core.quotepath=false"];

        switch (request.Kind)
        {
            case DiffTextKind.Commit:
                // "show --format=" prints only the patch and, unlike "<sha>^!",
                // also works for a root commit.
                args.Add("show");
                args.Add("--format=");
                break;
            default:
                args.Add("diff");
                break;
        }

        args.Add("--no-color");
        args.Add("--find-renames");

        if (options.IgnoreWhitespace)
        {
            args.Add("-w");
        }

        if (options.WordDiff)
        {
            args.Add("--word-diff=plain");
        }

        switch (request.Kind)
        {
            case DiffTextKind.Range:
                args.Add(request.BaseHash ?? request.CommitHash);
                args.Add(request.CommitHash);
                break;
            default:
                args.Add(request.CommitHash);
                break;
        }

        args.Add("--");
        args.Add(request.Path);
        if (!string.IsNullOrEmpty(request.OldPath) && request.OldPath != request.Path)
        {
            args.Add(request.OldPath!);
        }

        return args;
    }

    /// <summary>Renders the git command line as text, for status/tooltip display.</summary>
    public static string DescribeCommand(DiffTextRequest request, DiffDisplayOptions options) =>
        "git " + string.Join(' ', BuildArguments(request, options).Select(Quote));

    private static string Quote(string arg) =>
        arg.Contains(' ', StringComparison.Ordinal) ? "\"" + arg + "\"" : arg;

    /// <summary>
    ///  Runs git and returns the patch text, decoded with the encoding named by
    ///  <paramref name="options"/>. Never throws for a failed git run: the error
    ///  output is returned as the text so the pane can show it.
    /// </summary>
    public static async Task<string> GetDiffTextAsync(
        DiffTextRequest request,
        DiffDisplayOptions options,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = request.RepoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in BuildArguments(request, options))
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

        // Read the raw bytes so the caller-selected encoding decides how the file
        // content is interpreted (git itself has no diff-content encoding switch).
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
        string text = ResolveEncoding(options.EncodingName).GetString(stdout.ToArray());

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.IsNullOrWhiteSpace(error)
                ? "(no textual diff — binary file or no changes)"
                : error;
        }

        return text;
    }

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
