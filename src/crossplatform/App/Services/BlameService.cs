using GitCommands;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single blamed source line, projected from a core <see cref="GitBlameLine"/>
///  for display in the Avalonia blame view. Field names are prefixed with
///  <c>Blame</c> to stay unique across sibling views.
/// </summary>
public sealed record BlameLineRow(
    int LineNumber,
    string ShortHash,
    string Author,
    string Date,
    string Text);

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
    public IReadOnlyList<BlameLineRow> GetBlame(string repoPath, string filePath, string? commitOrHead = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string from = string.IsNullOrWhiteSpace(commitOrHead) ? "HEAD" : commitOrHead!;

        GitBlame blame = module.Blame(
            fileName: filePath,
            from: from,
            encoding: GitModule.SystemEncoding,
            lines: null,
            cancellationToken: CancellationToken.None);

        List<BlameLineRow> rows = new(blame.Lines.Count);
        foreach (GitBlameLine line in blame.Lines)
        {
            GitBlameCommit commit = line.Commit;
            rows.Add(new BlameLineRow(
                LineNumber: line.FinalLineNumber,
                ShortHash: commit.ObjectId.ToShortString(),
                Author: commit.Author ?? string.Empty,
                Date: commit.AuthorTime == DateTime.MaxValue ? string.Empty : commit.AuthorTime.ToString("yyyy-MM-dd"),
                Text: line.Text ?? string.Empty));
        }

        return rows;
    }
}
