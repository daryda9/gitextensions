using System.Text;
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
    string OriginFileName = "")
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
public sealed class BlameService
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
                OriginFileName: commit.FileName ?? string.Empty));
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
