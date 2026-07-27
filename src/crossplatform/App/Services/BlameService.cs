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
        string repoPath, string filePath, string? commitOrHead = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GitModule module = GitContext.CreateModule(repoPath);
        string from = string.IsNullOrWhiteSpace(commitOrHead) ? "HEAD" : commitOrHead!;

        GitBlame blame = module.Blame(
            fileName: filePath,
            from: from,
            encoding: GitModule.SystemEncoding,
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
