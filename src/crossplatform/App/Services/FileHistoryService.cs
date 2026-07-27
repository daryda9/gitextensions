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
    string Subject)
{
    /// <summary>Author e-mail, for the "Copy author" menu entry.</summary>
    public string AuthorEmail { get; init; } = string.Empty;

    /// <summary>Full message (subject + body) when the core kept the body.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Author date, rendered; empty when unknown.</summary>
    public string AuthorDate { get; init; } = string.Empty;

    /// <summary>Commit date, rendered; empty when unknown.</summary>
    public string CommitDate { get; init; } = string.Empty;
}

/// <summary>
///  The four <c>git log</c> switches the upstream <c>FormFileHistory</c> exposes
///  (context menu "Detect and follow renames" / "…exact renames and copies only",
///  and the "Show Full History" drop-down "Show full history" / "Simplify merges").
///  Upstream stores them in <c>AppSettings.FollowRenamesInFileHistory</c> and
///  friends; the port keeps them session-local like the other view toggles.
/// </summary>
public sealed record FileHistoryOptions(
    bool FollowRenames = true,
    bool ExactRenamesAndCopiesOnly = false,
    bool FullHistory = false,
    bool SimplifyMerges = false)
{
    /// <summary>
    ///  The revision-filter fragment (everything that goes before the <c>--</c>).
    ///  Mirrors <c>RevisionGridControl.FindRenamesAndCopiesOpts()</c> and
    ///  <c>FilterInfo</c>'s <c>--full-history</c> / <c>--simplify-merges</c>.
    /// </summary>
    public string ToRevisionFilter()
    {
        List<string> parts = [];

        if (FollowRenames)
        {
            parts.Add("--follow");
            parts.Add(ExactRenamesAndCopiesOnly
                ? "--find-renames=\"100%\" --find-copies=\"100%\""
                : "--find-renames --find-copies");
        }

        if (FullHistory)
        {
            parts.Add("--full-history");

            // Upstream only enables the entry while full history is on.
            if (SimplifyMerges)
            {
                parts.Add("--simplify-merges");
            }
        }

        return string.Join(' ', parts);
    }
}

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
    public IReadOnlyList<FileHistoryRow> GetHistory(
        string repoPath,
        string filePath,
        FileHistoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        RevisionCollector collector = new();
        RevisionReader reader = new(module);

        // --follow (in the revision filter, before the "--") traces the file
        // across renames; the path itself goes through pathFilter (after "--").
        string pathFilter = (filePath.ToPosixPath() ?? filePath).Quote();

        reader.GetLog(
            subject: collector,
            revisionFilter: (options ?? new FileHistoryOptions()).ToRevisionFilter(),
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
                Date: Render(revision.CommitDate),
                Subject: revision.Subject ?? string.Empty)
            {
                AuthorEmail = revision.AuthorEmail ?? string.Empty,
                Message = revision.Body ?? revision.Subject ?? string.Empty,
                AuthorDate = Render(revision.AuthorDate),
                CommitDate = Render(revision.CommitDate),
            });
        }

        return rows;
    }

    // The core uses DateTime.MaxValue as "unknown".
    private static string Render(DateTime value)
        => value == DateTime.MaxValue ? string.Empty : value.ToString("yyyy-MM-dd HH:mm");

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
