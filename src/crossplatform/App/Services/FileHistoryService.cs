using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
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

    /// <summary>
    ///  The path the file had <em>in this revision</em>. With <c>--follow</c> the
    ///  history reaches back past renames, so for commits older than a rename this
    ///  is the OLD path — the only one that resolves against that commit's tree.
    ///  Everything that reads a blob ("Save as", difftool, open) must use it and not
    ///  the path the file has today. Upstream keeps the same mapping in
    ///  <c>RevisionGridControl.FilePathByObjectId</c> and reads it through
    ///  <c>FormFileHistory.GetFileNameForRevision</c>.
    ///  Empty when git could not name the file for that commit.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;
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

        // The per-revision file name. Built in ONE extra pass instead of upstream's
        // lazy per-selection call, because every row needs it (the "Save as" of an
        // older revision has to read the pre-rename blob).
        Dictionary<string, string> pathByHash =
            GetFilePathByHash(module, filePath, options ?? new FileHistoryOptions(), cancellationToken);

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
                FilePath = pathByHash.TryGetValue(revision.ObjectId.ToString(), out string? historic)
                    ? historic
                    : string.Empty,
            });
        }

        return rows;
    }

    // Upstream's marker in the "commit info" tab caption.
    private const string ObjectIdPrefix = "????";

    /// <summary>
    ///  Maps commit hash → the name the file had in that commit, by asking git for
    ///  the name it printed while following the file. Mirrors
    ///  <c>RevisionGridControl.GetRevisionFileName</c>/<c>ParseFileNames</c>:
    ///  <c>--name-only</c> prints one path per line under a <c>????&lt;sha&gt;</c>
    ///  header, and with <c>--follow</c> those paths are the pre-rename ones for the
    ///  commits that precede a rename. Blocking; call it off the UI thread.
    /// </summary>
    private static Dictionary<string, string> GetFilePathByHash(
        GitModule module,
        string filePath,
        FileHistoryOptions options,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        // The map only makes sense while renames are followed; without --follow the
        // path never changes and the caller's own path is already correct. Still run
        // it, so a row whose path is genuinely absent stays detectable.
        GitArgumentBuilder args = new("log")
        {
            $"--format=\"{ObjectIdPrefix}%H\"",
            "--name-only",
            "--diff-merges=separate",
            options.ToRevisionFilter(),
            "--",
            (filePath.ToPosixPath() ?? filePath).Quote(),
        };

        ExecutionResult result;
        try
        {
            result = module.GitExecutable.Execute(
                args,
                outputEncoding: GitModule.LosslessEncoding,
                throwOnErrorExit: false,
                cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            // A missing path map degrades to "use the current path", i.e. the old
            // behaviour — never to a broken history list.
            return map;
        }

        if (!result.ExitedSuccessfully)
        {
            return map;
        }

        string? currentHash = null;
        foreach (string? raw in (result.StandardOutput ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = GitModule.ReEncodeFileNameFromLossless(raw) ?? string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith(ObjectIdPrefix, StringComparison.Ordinal))
            {
                string hash = line[ObjectIdPrefix.Length..].Trim();
                currentHash = hash.Length == ObjectId.Sha1CharCount ? hash : null;
                continue;
            }

            if (currentHash is null)
            {
                continue;
            }

            // Only the FIRST path under a header is the followed file (upstream does
            // the same); later ones belong to the rest of the commit's diff.
            map.TryAdd(currentHash, line.Trim());
        }

        return map;
    }

    /// <summary>
    ///  True when <paramref name="path"/> resolves to a blob in
    ///  <paramref name="hash"/>. Drives the port's equivalent of upstream's
    ///  " - Git could not identify the file {0}" marker
    ///  (<c>FormFileHistory._fileNotFound</c>). Blocking; call it off the UI thread.
    /// </summary>
    public bool FileExistsInRevision(string repoPath, string hash, string path)
    {
        try
        {
            if (path.Length == 0 || path.EndsWith('/'))
            {
                return false;
            }

            if (!ObjectId.TryParse(hash, out ObjectId objectId))
            {
                return false;
            }

            GitModule module = GitContext.CreateModule(repoPath);
            return !module.GetFileBlobHash(path.ToPosixPath() ?? path, objectId).IsZero;
        }
        catch (Exception)
        {
            return false;
        }
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
