using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single commit row, projected from a core <see cref="GitRevision"/> for
///  display in the Avalonia revision grid. Field/type names are prefixed with
///  <c>Revision</c> to stay unique across sibling views.
/// </summary>
public sealed record RevisionRow(
    string Hash,
    string ShortHash,
    string Author,
    string Date,
    string Subject,
    IReadOnlyList<string> ParentHashes,
    IReadOnlyList<string> RefNames)
{
    /// <summary>Ref names joined for inline display, e.g. "[main] [origin/main] [v1.0]".</summary>
    public string RefsDisplay
        => RefNames.Count == 0 ? string.Empty : string.Join(" ", RefNames.Select(r => $"[{r}]"));

    /// <summary>Subject prefixed with any ref names.</summary>
    public string SubjectWithRefs
        => RefNames.Count == 0 ? Subject : $"{RefsDisplay} {Subject}";
}

/// <summary>
///  Loads revisions for a repository by reusing the Git Extensions core
///  (<see cref="GitContext.CreateModule"/> + <see cref="RevisionReader"/>),
///  the same code path the Windows app uses.
/// </summary>
public sealed class RevisionService
{
    /// <summary>
    ///  Loads the most recent <paramref name="maxCount"/> commits reachable from
    ///  HEAD (the current branch), newest first, with author, date, subject,
    ///  parent hashes and ref names (branches/tags) attached.
    /// </summary>
    public IReadOnlyList<RevisionRow> LoadRevisions(string repoPath, int maxCount = 200, CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // Build an ObjectId -> ref-names lookup so we can show branches/tags inline.
        Dictionary<ObjectId, List<string>> refsByCommit = [];
        try
        {
            foreach (IGitRef gitRef in module.GetRefs(RefsFilter.NoFilter))
            {
                if (gitRef.ObjectId.IsZero || gitRef.ObjectId.IsArtificial)
                {
                    continue;
                }

                if (!refsByCommit.TryGetValue(gitRef.ObjectId, out List<string>? names))
                {
                    names = [];
                    refsByCommit[gitRef.ObjectId] = names;
                }

                names.Add(gitRef.Name);
            }
        }
        catch
        {
            // Refs are a nicety; a failure here must not prevent the log from loading.
        }

        RevisionCollector collector = new();
        RevisionReader reader = new(module);

        // No revision/path filter => log the current branch (HEAD). --max-count limits it.
        reader.GetLog(
            subject: collector,
            revisionFilter: $"--max-count={maxCount}",
            pathFilter: string.Empty,
            hasNotes: false,
            autostashLabel: string.Empty,
            cancellationToken: cancellationToken);

        List<RevisionRow> rows = new(collector.Revisions.Count);
        foreach (GitRevision revision in collector.Revisions)
        {
            string hash = revision.ObjectId.ToString();
            string[] parents = revision.ParentIds is { Count: > 0 } parentIds
                ? parentIds.Select(p => p.ToString()).ToArray()
                : [];

            string[] refNames = refsByCommit.TryGetValue(revision.ObjectId, out List<string>? names)
                ? names.ToArray()
                : [];

            rows.Add(new RevisionRow(
                Hash: hash,
                ShortHash: revision.ObjectId.ToShortString(),
                Author: revision.Author ?? string.Empty,
                Date: revision.CommitDate == DateTime.MaxValue ? string.Empty : revision.CommitDate.ToString("yyyy-MM-dd HH:mm"),
                Subject: revision.Subject ?? string.Empty,
                ParentHashes: parents,
                RefNames: refNames));
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

        public void OnError(Exception error)
        {
            // Surface parse/stream errors to the caller running GetLog.
            throw error;
        }

        public void OnCompleted()
        {
        }
    }
}
