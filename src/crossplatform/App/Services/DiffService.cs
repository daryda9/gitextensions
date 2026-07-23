using GitCommands;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The kind of change a file underwent in a commit.
/// </summary>
public enum DiffChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
}

/// <summary>
///  A single changed file in a commit, for display in the diff view.
///  <paramref name="Name"/> is the (new) path; <paramref name="OldName"/> is
///  the previous path for renames/copies (otherwise <c>null</c>).
/// </summary>
public sealed record DiffFileRow(string Name, string? OldName, DiffChangeKind Kind, bool IsTracked)
{
    private static char KindGlyph(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Added => 'A',
        DiffChangeKind.Deleted => 'D',
        DiffChangeKind.Renamed => 'R',
        DiffChangeKind.Copied => 'C',
        _ => 'M',
    };

    public string Display => OldName is null || OldName == Name
        ? $"{KindGlyph(Kind)}  {Name}"
        : $"{KindGlyph(Kind)}  {OldName} -> {Name}";
}

/// <summary>
///  Reads diff data for a commit by reusing the Git Extensions core module
///  (<see cref="GitModule"/>) obtained from <see cref="GitContext.CreateModule"/>.
///  All calls are blocking and meant to run off the UI thread.
/// </summary>
public static class DiffService
{
    /// <summary>
    ///  Returns the files changed by <paramref name="commitHash"/> compared with
    ///  its first parent (or the empty tree for a root commit).
    /// </summary>
    public static IReadOnlyList<DiffFileRow> GetChangedFiles(string repoPath, string commitHash)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);
        ObjectId parentId = GetFirstParent(module, commitId);

        IReadOnlyList<GitItemStatus> changes = module.GetDiffFilesWithSubmodulesStatus(
            firstId: parentId,
            secondId: commitId,
            parentToSecond: parentId,
            excludeSkipWorktreeFiles: true,
            untrackedFilesMode: UntrackedFilesMode.No,
            cancellationToken: CancellationToken.None);

        List<DiffFileRow> rows = [];
        foreach (GitItemStatus item in changes)
        {
            rows.Add(new DiffFileRow(item.Name, item.OldName, MapKind(item), item.IsTracked));
        }

        return rows;
    }

    /// <summary>
    ///  Returns the unified diff text for a single <paramref name="file"/> in
    ///  <paramref name="commitHash"/> (compared with its first parent). Returns an
    ///  error/placeholder string if no patch could be produced.
    /// </summary>
    public static async Task<string> GetFileDiffAsync(
        string repoPath,
        string commitHash,
        DiffFileRow file,
        CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);
        ObjectId parentId = GetFirstParent(module, commitId);

        (Patch? patch, string? errorMessage) = await module.GetSingleDiffAsync(
            firstId: parentId,
            secondId: commitId,
            fileName: file.Name,
            oldFileName: file.OldName,
            extraDiffArguments: string.Empty,
            encoding: GitModule.SystemEncoding,
            cacheResult: true,
            isTracked: file.IsTracked,
            useGitColoring: false,
            commandConfiguration: null!,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(errorMessage))
        {
            return errorMessage!;
        }

        return patch?.Text ?? "(no textual diff — binary file or no changes)";
    }

    /// <summary>
    ///  Launches the user's configured external diff tool (fire-and-forget,
    ///  non-blocking) for <paramref name="file"/>, comparing the version in
    ///  <paramref name="commitHash"/> against its first parent — i.e.
    ///  <c>git difftool --no-prompt &lt;parent&gt; &lt;commit&gt; -- &lt;path&gt;</c>.
    ///  The launch is detached via the core runner, so the tool runs
    ///  independently of the app and the UI never blocks.
    ///  Returns <c>null</c> on a successful launch, or a human-readable message
    ///  (e.g. no difftool configured) to surface in the UI.
    /// </summary>
    public static string? LaunchExternalDiffTool(string repoPath, string commitHash, DiffFileRow file)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);
        ObjectId parentId = GetFirstParent(module, commitId);

        // "git difftool --gui" resolves diff.guitool -> diff.tool -> merge.guitool
        // -> merge.tool. If none of these is set, difftool would fail silently on
        // a detached process, so surface a friendly message instead.
        bool hasTool =
            !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("diff.guitool")) ||
            !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("diff.tool")) ||
            !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("merge.guitool")) ||
            !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("merge.tool"));
        if (!hasTool)
        {
            return "No external difftool is configured. Set one with e.g. "
                + "\"git config --global diff.tool <tool>\".";
        }

        // Reuse the core's detached difftool launch (uses "--no-prompt" and runs
        // the process detached, so the GUI tool stays open and the app never waits).
        module.OpenWithDifftool(
            filename: file.Name,
            oldFileName: file.OldName,
            firstRevision: parentId.IsZero ? null : parentId.ToString(),
            secondRevision: commitId.ToString(),
            isTracked: file.IsTracked);

        return null;
    }

    /// <summary>
    ///  Returns the unified diff between the working-tree version of
    ///  <paramref name="file"/> and its version in <paramref name="commitHash"/>
    ///  — i.e. <c>git diff &lt;commit&gt; -- &lt;path&gt;</c> (that commit is the
    ///  "old" side, the current working tree the "new" side). The result is a
    ///  plain unified-diff string rendered by the same coloured diff pane.
    /// </summary>
    public static async Task<string> GetFileDiffAgainstWorkingTreeAsync(
        string repoPath,
        string commitHash,
        DiffFileRow file,
        CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);

        (Patch? patch, string? errorMessage) = await module.GetSingleDiffAsync(
            firstId: commitId,
            secondId: default, // zero ObjectId => working tree ("secondRevision" null)
            fileName: file.Name,
            oldFileName: null, // compare by current path only against the working tree
            extraDiffArguments: string.Empty,
            encoding: GitModule.SystemEncoding,
            cacheResult: false, // the working tree is volatile; never cache
            isTracked: file.IsTracked,
            useGitColoring: false,
            commandConfiguration: null!,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(errorMessage))
        {
            return errorMessage!;
        }

        return patch?.Text ?? "(no differences between the commit and the working tree)";
    }

    private static ObjectId GetFirstParent(GitModule module, ObjectId commitId)
    {
        IReadOnlyList<ObjectId> parents = module.GetParents(commitId);

        // Root commit (no parents): a zero ObjectId is treated by the core as
        // "no revision", which diffs the commit against the empty tree.
        return parents.Count > 0 ? parents[0] : default;
    }

    private static DiffChangeKind MapKind(GitItemStatus item)
    {
        if (item.IsNew)
        {
            return DiffChangeKind.Added;
        }

        if (item.IsDeleted)
        {
            return DiffChangeKind.Deleted;
        }

        if (item.IsRenamed)
        {
            return DiffChangeKind.Renamed;
        }

        if (item.IsCopied)
        {
            return DiffChangeKind.Copied;
        }

        return DiffChangeKind.Modified;
    }
}
