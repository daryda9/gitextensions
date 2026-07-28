using System.Text;
using System.Text.RegularExpressions;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single blamed source line, projected from a core <see cref="GitBlameLine"/>
///  for display in the Avalonia blame view. Field names are prefixed with
///  <c>Blame</c> to stay unique across sibling views.
/// </summary>
///  <para><see cref="CommitHash"/>, <see cref="Summary"/>, <see cref="Details"/> and
///  <see cref="OriginFileName"/> come from the very same
///  <c>git blame --porcelain</c> pass as the rest of the row (the core parser
///  already hands over a fully populated <see cref="GitBlameCommit"/>), so the
///  context menu, the commit-details panel and the hover tooltip cost no extra
///  git invocation — let alone one per line.</para>
public sealed record BlameLineRow(
    int LineNumber,
    string ShortHash,
    string Author,
    string Date,
    string Text,
    string CommitHash = "",
    string Summary = "",
    string Details = "",
    string OriginFileName = "",
    int OriginLineNumber = 0)
{
    /// <summary>
    ///  True for lines that are not committed yet (the all-zero blame boundary
    ///  git reports for working-tree changes): nothing can be blamed or copied
    ///  for those.
    /// </summary>
    public bool IsUncommitted
        => CommitHash.Length == 0 || CommitHash.All(c => c == '0');
}

/// <summary>
///  The outcome of one blame run: the rows plus the full hash the requested
///  revision resolved to, which the view needs to tell "the line was last touched
///  by the revision being blamed" from "the line comes from an older commit".
/// </summary>
public sealed record BlameResult(string ResolvedCommit, IReadOnlyList<BlameLineRow> Lines);

/// <summary>
///  The three switches upstream's <c>BlameViewerSettingsPage</c> offers, which change
///  what <c>git blame</c> itself is asked to do:
///
///  <list type="bullet">
///   <item><see cref="IgnoreWhitespace"/> → <c>-w</c>, so a re-indentation stops
///    reassigning every line it touched to the commit that re-indented it.</item>
///   <item><see cref="DetectCopyInFile"/> → <c>-M</c>, following a block moved
///    inside the same file back to where it was written.</item>
///   <item><see cref="DetectCopyInAll"/> → <c>-C</c>, following a block copied in
///    from another file of the same commit.</item>
///  </list>
///
///  <para><b>Where these live.</b> In upstream's own store: the core's
///  <c>GitModule.Blame</c> builds its argument list straight from
///  <c>AppSettings.IgnoreWhitespaceOnBlame</c> / <c>DetectCopyInFileOnBlame</c> /
///  <c>DetectCopyInAllOnBlame</c> (<c>GitModule.cs:3278-3280</c>), and the port reuses
///  that method verbatim. Keeping a second copy of the flags in the port's own JSON
///  would mean a setting that reads back correctly and changes nothing, so this record
///  is a typed <i>view</i> of those three statics — the same way <c>StashPanel</c>
///  already rides on <c>AppSettings.StashKeepIndex</c>. Defaults are upstream's:
///  whitespace ignored, neither copy detection on.</para>
/// </summary>
public sealed record BlameOptions(bool IgnoreWhitespace, bool DetectCopyInFile, bool DetectCopyInAll)
{
    /// <summary>Reads the three flags as they stand now.</summary>
    public static BlameOptions Load()
        => new(
            AppSettings.IgnoreWhitespaceOnBlame,
            AppSettings.DetectCopyInFileOnBlame,
            AppSettings.DetectCopyInAllOnBlame);

    /// <summary>
    ///  Persists the three flags, which is also what makes the next
    ///  <see cref="BlameService.GetBlameResult"/> use them: the core reads the statics
    ///  as it assembles the command line, so there is no separate plumbing to keep in
    ///  step. Writing goes through upstream's settings container, so it survives a
    ///  restart and is shared with any other view that blames.
    /// </summary>
    public void Apply()
    {
        AppSettings.IgnoreWhitespaceOnBlame = IgnoreWhitespace;
        AppSettings.DetectCopyInFileOnBlame = DetectCopyInFile;
        AppSettings.DetectCopyInAllOnBlame = DetectCopyInAll;
    }
}

/// <summary>
///  Computes <c>git blame</c> for a file by reusing the Git Extensions core
///  module (<see cref="GitModule"/>) obtained from
///  <see cref="GitContext.CreateModule"/>. The single call is blocking and meant
///  to run off the UI thread.
/// </summary>
public sealed partial class BlameService
{
    /// <summary>
    ///  Blames <paramref name="filePath"/> at <paramref name="commitOrHead"/>
    ///  (defaults to <c>HEAD</c> when null/empty), returning one row per source
    ///  line with the commit that last touched it.
    /// </summary>
    public IReadOnlyList<BlameLineRow> GetBlame(
        string repoPath, string filePath, string? commitOrHead = null, CancellationToken cancellationToken = default)
        => GetBlameResult(repoPath, filePath, commitOrHead, cancellationToken).Lines;

    /// <summary>
    ///  As <see cref="GetBlame"/>, but also reports the full hash
    ///  <paramref name="commitOrHead"/> resolved to. Blocking; call off the UI thread.
    ///
    ///  <para><paramref name="cancellationToken"/> is handed straight to the core
    ///  blame call, which is what makes a superseded request stop instead of racing
    ///  the new one to the UI (upstream serialises the same way, with
    ///  <c>AsyncLoader</c> + <c>CancellationTokenSequence</c>,
    ///  <c>BlameControl.cs:34,132,151</c>).</para>
    /// </summary>
    public BlameResult GetBlameResult(
        string repoPath,
        string filePath,
        string? commitOrHead = null,
        CancellationToken cancellationToken = default,
        BlameOptions? options = null,
        Encoding? encoding = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GitModule module = GitContext.CreateModule(repoPath);
        string from = string.IsNullOrWhiteSpace(commitOrHead) ? "HEAD" : commitOrHead!;

        // The core reads the three flags off AppSettings while building the command
        // line, so an explicit set has to be published there before the call. Passing
        // null means "whatever is configured", which is the normal case and writes
        // nothing.
        options?.Apply();

        GitBlame blame = module.Blame(
            fileName: filePath,
            from: from,
            // Upstream hands the blame the encoding the file viewer is using and
            // re-runs it whenever that changes (BlameControl.cs:117-135); pinning
            // SystemEncoding here made a Latin-1 or Shift-JIS source unreadable with
            // no way to say so. Null keeps the old behaviour for callers that have no
            // selector of their own.
            encoding: encoding ?? GitModule.SystemEncoding,
            lines: null,
            cancellationToken: cancellationToken);

        List<BlameLineRow> rows = new(blame.Lines.Count);
        foreach (GitBlameLine line in blame.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitBlameCommit commit = line.Commit;
            rows.Add(new BlameLineRow(
                LineNumber: line.FinalLineNumber,
                ShortHash: commit.ObjectId.ToShortString(),
                Author: commit.Author ?? string.Empty,
                Date: commit.AuthorTime == DateTime.MaxValue ? string.Empty : commit.AuthorTime.ToString("yyyy-MM-dd"),
                Text: line.Text ?? string.Empty,
                CommitHash: commit.ObjectId.ToString(),
                Summary: commit.Summary ?? string.Empty,

                // Upstream feeds exactly this text to the hover tooltip and to
                // "Copy to clipboard ▸ All commit info" (BlameControl.cs:206 and
                // copyAllCommitInfoToClipboardToolStripMenuItem_Click).
                Details: commit.ToString(),
                OriginFileName: commit.FileName ?? string.Empty,

                // The line's number as of the commit that introduced it, which is
                // where "Blame this revision" has to open (upstream uses exactly this
                // field, BlameControl.cs:126).
                OriginLineNumber: line.OriginLineNumber));
        }

        // One more git call follows (rev-parse); skip it outright if the request has
        // already been superseded.
        cancellationToken.ThrowIfCancellationRequested();
        return new BlameResult(ResolveFullHash(module, from) ?? from, rows);
    }

    /// <summary>
    ///  Returns the full hash of the first parent of <paramref name="commitHash"/>,
    ///  or null for a root commit (or when the revision is unknown). Blocking; call
    ///  off the UI thread. This is the one extra git call the "Blame previous
    ///  revision" command needs, and it runs once per invocation, not per line.
    /// </summary>
    public string? ResolveParent(string repoPath, string commitHash)
        => ResolveFullHash(GitContext.CreateModule(repoPath), commitHash + "^");

    /// <summary>
    ///  Maps <paramref name="line"/> — a 1-based line of <paramref name="filePath"/>
    ///  as it stands in <paramref name="commitHash"/> — to the line it occupied in
    ///  <paramref name="parentHash"/>, so "Blame previous revision" lands on the
    ///  same piece of code instead of on line 1. Blocking; call off the UI thread.
    ///
    ///  <para>This is upstream's <c>GitBlameParser.GetOriginalLineInPreviousCommit</c>
    ///  (<c>GitBlameParser.cs:26-88</c>): the commit's own diff is taken with
    ///  <c>-U0</c>, so every hunk header is a precise statement about where lines
    ///  moved, and the last hunk starting at or above the line supplies the offset.
    ///  Hunks below the line cannot affect it, and with no hunk above it the line has
    ///  not moved at all.</para>
    ///
    ///  <para>The three blame switches are mirrored onto the diff — a mapping computed
    ///  under different rename/whitespace rules than the blame it serves would not
    ///  line up.</para>
    /// </summary>
    /// <returns>
    ///  The 1-based line in the parent; <paramref name="line"/> itself when the file
    ///  is untouched by the commit, when git fails, or when nothing can be concluded.
    /// </returns>
    public int MapLineToParent(string repoPath, string commitHash, string parentHash, string filePath, int line)
    {
        if (line <= 0 || string.IsNullOrEmpty(parentHash))
        {
            return line;
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            BlameOptions options = BlameOptions.Load();

            GitArgumentBuilder args = new("diff")
            {
                "--no-ext-diff",
                "-U0",
                { options.DetectCopyInFile, "--find-renames" },   // git-blame only has -M
                { options.DetectCopyInAll, "--find-copies" },     // git-blame only has -C
                { options.IgnoreWhitespace, "--ignore-all-space" }, // git-blame only has -w
                parentHash,
                commitHash,
                "--",
                filePath,
            };

            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            return result.ExitedSuccessfully
                ? MapLineThroughDiff(result.StandardOutput, line)
                : line;
        }
        catch (Exception)
        {
            // A mapping that cannot be computed is the unmapped line, never an error:
            // the command still has to open the parent's blame.
            return line;
        }
    }

    // "@@ -a,b +c,d @@" — the counts are optional and mean 1 when absent.
    [GeneratedRegex(@"^@@ -(?<prev>\d+)(,(?<removed>\d+))? \+(?<cur>\d+)(,(?<added>\d+))? @@", RegexOptions.ExplicitCapture)]
    private static partial Regex HunkHeaderRegex { get; }

    /// <summary>
    ///  The arithmetic of <see cref="MapLineToParent"/>, split out so it can be
    ///  reasoned about (and checked against real git) without running git here.
    ///  <paramref name="diff"/> is the output of a <c>-U0</c> diff from the parent to
    ///  the commit; <paramref name="line"/> and the result are 1-based.
    ///
    ///  <para><b>Why this is not upstream's expression verbatim.</b> Upstream computes
    ///  <c>max(prev, line - cur + prev - added + removed)</c> and takes any hunk with
    ///  <c>cur &lt;= line</c> (<c>GitBlameParser.cs:70-78</c>). That is off by one
    ///  wherever a hunk range is empty, and with <c>-U0</c> empty ranges are the norm:
    ///  git writes a pure insertion as <c>@@ -0,0 +1,5 @@</c> and a pure deletion as
    ///  <c>@@ -10,3 +14,0 @@</c>, where the <c>,0</c> side names the line <i>before</i>
    ///  the gap rather than the first line of a range. Checked against a real
    ///  repository — mapping each unchanged line and comparing the parent's text at
    ///  the mapped line — upstream's expression missed on every line of the file:
    ///  the first line after a 5-line insertion came back as 0, not 1, and a line just
    ///  above a deletion was pulled down into the deleted block.</para>
    ///
    ///  <para>So the hunk is treated as the pair of ranges it is. For a line below the
    ///  hunk, the answer is the parent's last line of the hunk plus the distance; for a
    ///  line inside it (one the commit added or rewrote) the parent has no exact
    ///  counterpart and the hunk's own parent range is the honest answer.</para>
    /// </summary>
    public static int MapLineThroughDiff(string diff, int line)
    {
        List<Match> hunks = [];
        foreach (string raw in diff.Split('\n'))
        {
            if (raw.StartsWith("@@ ", StringComparison.Ordinal) && HunkHeaderRegex.Match(raw) is { Success: true } match)
            {
                hunks.Add(match);
            }
        }

        // From the end: the first hunk that reaches the line is the nearest one at or
        // above it, and it alone determines the offset — the hunk positions git prints
        // already account for every earlier hunk.
        for (int i = hunks.Count - 1; i >= 0; i--)
        {
            Match hunk = hunks[i];
            int previousStart = int.Parse(hunk.Groups["prev"].ValueSpan);
            int currentStart = int.Parse(hunk.Groups["cur"].ValueSpan);
            int removed = hunk.Groups["removed"].Success ? int.Parse(hunk.Groups["removed"].ValueSpan) : 1;
            int added = hunk.Groups["added"].Success ? int.Parse(hunk.Groups["added"].ValueSpan) : 1;

            // An empty range sits *after* the line it names, so its "last line" is that
            // line itself and its "first line" is the one after — which is never
            // reached, exactly as an empty range should not be.
            int childFirst = added == 0 ? currentStart + 1 : currentStart;
            int childLast = added == 0 ? currentStart : currentStart + added - 1;
            int parentLast = removed == 0 ? previousStart : previousStart + removed - 1;

            if (line > childLast)
            {
                return Math.Max(1, parentLast + (line - childLast));
            }

            if (line >= childFirst)
            {
                // Inside the hunk: the commit wrote this line. Walk into the parent's
                // range by the same offset, stopping at its end.
                return removed == 0
                    ? Math.Max(1, previousStart)
                    : Math.Clamp(previousStart + (line - childFirst), previousStart, parentLast);
            }
        }

        // Above every hunk: the commit changed nothing before this line.
        return line;
    }

    private static string? ResolveFullHash(GitModule module, string rev)
    {
        try
        {
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("rev-parse") { "--verify", "-q", rev },
                throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return null;
            }

            string hash = result.StandardOutput.Trim();
            return hash.Length == 0 ? null : hash;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
