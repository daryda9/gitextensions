using GitCommands;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single commit in a file's history, projected from a core
///  <see cref="GitRevision"/>. Field names are prefixed with <c>FileHistory</c>
///  to stay unique across sibling views.
/// </summary>
public sealed record FileHistoryRow(
    string Hash,
    string ShortHash,
    string Author,
    string Date,
    string Subject);

/// <summary>
///  Loads the commit history of a single file by reusing the Git Extensions core
///  (<see cref="GitContext.CreateModule"/> + <see cref="RevisionReader"/>) — the
///  same log code path the revision grid uses, but with <c>--follow</c> and a
///  path filter so renames are traced. The single call is blocking and meant to
///  run off the UI thread.
/// </summary>
public sealed class FileHistoryService
{
    /// <summary>
    ///  Returns the commits that touched <paramref name="filePath"/>, newest
    ///  first, following the file across renames.
    /// </summary>
    public IReadOnlyList<FileHistoryRow> GetHistory(string repoPath, string filePath, CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        RevisionCollector collector = new();
        RevisionReader reader = new(module);

        // --follow (in the revision filter, before the "--") traces the file
        // across renames; the path itself goes through pathFilter (after "--").
        string pathFilter = (filePath.ToPosixPath() ?? filePath).Quote();

        reader.GetLog(
            subject: collector,
            revisionFilter: "--follow",
            pathFilter: pathFilter,
            hasNotes: false,
            autostashLabel: string.Empty,
            cancellationToken: cancellationToken);

        List<FileHistoryRow> rows = new(collector.Revisions.Count);
        foreach (GitRevision revision in collector.Revisions)
        {
            rows.Add(new FileHistoryRow(
                Hash: revision.ObjectId.ToString(),
                ShortHash: revision.ObjectId.ToShortString(),
                Author: revision.Author ?? string.Empty,
                Date: revision.CommitDate == DateTime.MaxValue ? string.Empty : revision.CommitDate.ToString("yyyy-MM-dd HH:mm"),
                Subject: revision.Subject ?? string.Empty));
        }

        return rows;
    }

    /// <summary>
    ///  Minimal <see cref="IObserver{T}"/> that accumulates the batches emitted by
    ///  <see cref="RevisionReader.GetLog"/> into a single list.
    /// </summary>
    private sealed class RevisionCollector : IObserver<IReadOnlyList<GitRevision>>
    {
        public List<GitRevision> Revisions { get; } = [];

        public void OnNext(IReadOnlyList<GitRevision> value) => Revisions.AddRange(value);

        public void OnError(Exception error) => throw error;

        public void OnCompleted()
        {
        }
    }
}
