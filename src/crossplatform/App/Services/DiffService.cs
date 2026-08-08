using GitCommands;
using GitExtUtils;
using GitExtensions.Extensibility;
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
///  Which of the two artificial (non-commit) revisions of the revision grid a
///  diff refers to — the counterpart of the core's
///  <see cref="ObjectId.WorkTreeId"/> / <see cref="ObjectId.IndexId"/> sentinels
///  and of <c>RevisionGridView.ArtificialRevision</c>.
/// </summary>
public enum ArtificialDiff
{
    /// <summary>
    ///  The "Working directory" row: unstaged changes, i.e. the worktree against
    ///  the index (<c>git diff</c>), untracked files included.
    /// </summary>
    WorkTree,

    /// <summary>
    ///  The "Commit index" row: staged changes, i.e. the index against HEAD
    ///  (<c>git diff --cached</c>).
    /// </summary>
    Index,
}

/// <summary>
///  The user-visible name of each artificial row, in one place so the Diff, File
///  tree, Commit details and GPG tabs cannot drift apart. The wording and the
///  translation ids are upstream's own
///  (<c>ResourceManager/TranslatedStrings.cs</c>: <c>_workspaceText</c> =
///  "Working directory", <c>_indexText</c> = "Commit index" — the same strings the
///  revision grid puts in the two rows' Subject).
/// </summary>
public static class ArtificialRevisionName
{
    /// <summary>The row's name in the active language.</summary>
    public static string Of(ArtificialDiff which) => which == ArtificialDiff.Index
        ? TranslationService.T("TranslatedStrings/_indexText.Text", "Commit index")
        : TranslationService.T("TranslatedStrings/_workspaceText.Text", "Working directory");
}

/// <summary>
///  Reads diff data for a commit by reusing the Git Extensions core module
///  (<see cref="GitModule"/>) obtained from <see cref="GitContext.CreateModule"/>.
///  All calls are blocking and meant to run off the UI thread.
/// </summary>
public static class DiffService
{
    /// <summary>
    ///  The sentinel hash of the "Working directory" row, taken from the core
    ///  (<see cref="ObjectId.WorkTreeId"/>) rather than spelled out again.
    /// </summary>
    public static string WorkTreeHash { get; } = ObjectId.WorkTreeId.ToString();

    /// <summary>The sentinel hash of the "Commit index" row (<see cref="ObjectId.IndexId"/>).</summary>
    public static string IndexHash { get; } = ObjectId.IndexId.ToString();

    /// <summary>
    ///  Maps a sentinel hash back to the artificial revision it names, or
    ///  <see langword="null"/> when <paramref name="hash"/> is a real commit.
    ///  Lets a host that only has the hash from
    ///  <c>RevisionGridView.ArtificialRevisionSelected</c> reach these APIs.
    /// </summary>
    public static ArtificialDiff? ArtificialFromHash(string? hash) =>
        hash == IndexHash ? ArtificialDiff.Index
        : hash == WorkTreeHash ? ArtificialDiff.WorkTree
        : null;

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
    ///  Returns <b>every tracked file</b> of the tree at <paramref name="commitHash"/>
    ///  — not the files it changed — which is what the File-tree tab lists. Reuses
    ///  the core module's <see cref="GitModule.GetTreeFiles"/> (<c>git ls-tree</c>
    ///  with the object ids), so the port does not parse the tree itself.
    ///
    ///  <para>The rows come back as <see cref="DiffChangeKind.Modified"/> with no
    ///  old name: a tree entry has no change kind (upstream sets
    ///  <c>IsNew/IsChanged/IsDeleted = false</c> for all of them), so the list must
    ///  be told not to draw a status glyph
    ///  (<c>FileStatusListView.ShowStatusGlyphs = false</c>).</para>
    /// </summary>
    public static IReadOnlyList<DiffFileRow> GetTreeFiles(string repoPath, string commitHash)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);

        IReadOnlyList<GitItemStatus> files = module.GetTreeFiles(commitId, full: true);

        List<DiffFileRow> rows = new(files.Count);
        foreach (GitItemStatus item in files)
        {
            rows.Add(new DiffFileRow(item.Name, OldName: null, DiffChangeKind.Modified, IsTracked: true));
        }

        return rows;
    }

    /// <summary>
    ///  Returns the changed files of one of the two <b>artificial</b> revisions:
    ///  <see cref="ArtificialDiff.WorkTree"/> is <c>git diff</c> (worktree vs
    ///  index, untracked files included), <see cref="ArtificialDiff.Index"/> is
    ///  <c>git diff --cached</c> (index vs HEAD).
    ///
    ///  <para>Both go through the very same core entry point the commit modes use,
    ///  <see cref="GitModule.GetDiffFilesWithSubmodulesStatus"/>, with the sentinel
    ///  ids the core recognises — so the port does not invent a second code path.
    ///  The core turns those sentinels into <c>StagedStatus.WorkTree</c> /
    ///  <c>StagedStatus.Index</c> and answers from <c>git status</c> rather than
    ///  <c>git diff</c>, which is why untracked files can appear at all and why
    ///  renames are reported (<c>git status</c> detects them for the index).</para>
    ///
    ///  <para><b>Repository without HEAD</b> (a fresh <c>git init</c>, nothing
    ///  committed): <see cref="GitModule.GetCurrentCheckout"/> answers a zero
    ///  <see cref="ObjectId"/> there, which is a legal "old" side — the core reads
    ///  <c>git status</c> anyway, so nothing has to be diffed against the empty
    ///  tree and nothing throws. Everything staged shows up as added.</para>
    /// </summary>
    public static IReadOnlyList<DiffFileRow> GetArtificialChangedFiles(string repoPath, ArtificialDiff which)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // The pairs below are the ones GitModule.GetStagedStatus recognises:
        //  * (Index, WorkTree)          => StagedStatus.WorkTree  ("git diff")
        //  * (HEAD, Index) with parent
        //    == first                   => StagedStatus.Index     ("git diff --cached")
        // For the index side "HEAD" may legitimately be the zero id (no commits
        // yet): first == parentToSecond is what selects Index, not its value.
        (ObjectId firstId, ObjectId secondId) = which == ArtificialDiff.WorkTree
            ? (ObjectId.IndexId, ObjectId.WorkTreeId)
            : (module.GetCurrentCheckout(), ObjectId.IndexId);

        IReadOnlyList<GitItemStatus> changes = module.GetDiffFilesWithSubmodulesStatus(
            firstId: firstId,
            secondId: secondId,
            parentToSecond: firstId,
            excludeSkipWorktreeFiles: true,

            // Untracked files belong to the working directory row (they are what
            // "git status" reports and what the Windows view lists there); the
            // index row can only ever hold tracked entries.
            untrackedFilesMode: which == ArtificialDiff.WorkTree
                ? UntrackedFilesMode.Default
                : UntrackedFilesMode.No,
            cancellationToken: CancellationToken.None);

        List<DiffFileRow> rows = [];
        foreach (GitItemStatus item in changes)
        {
            rows.Add(new DiffFileRow(item.Name, item.OldName, MapKind(item), item.IsTracked));
        }

        return rows;
    }

    /// <summary>
    ///  Returns the file list the <b>File tree</b> tab shows for an artificial
    ///  revision — every file of that "tree", not the changed ones:
    ///  <list type="bullet">
    ///   <item><see cref="ArtificialDiff.Index"/>: the index, i.e.
    ///    <c>git ls-files --cached</c> (through the core's
    ///    <see cref="GitModule.GetTreeFiles"/>, which already maps the
    ///    <see cref="ObjectId.IndexId"/> sentinel onto that command).</item>
    ///   <item><see cref="ArtificialDiff.WorkTree"/>: the files as they are
    ///    <b>on disk</b> — tracked entries plus untracked non-ignored ones, minus
    ///    the entries deleted from the working tree. The core cannot answer this
    ///    one: for the worktree sentinel it runs <c>git ls-files --no-cached</c>,
    ///    an option git ignores when no other selector is given, so it returns the
    ///    index again (verified with git 2.43) — a file deleted on disk would still
    ///    be listed and an untracked one would not. Hence the two explicit
    ///    <c>ls-files</c> runs here.</item>
    ///  </list>
    ///
    ///  <para>As with <see cref="GetTreeFiles"/> the rows carry no change kind, so
    ///  the list must be told not to draw status glyphs.</para>
    /// </summary>
    public static IReadOnlyList<DiffFileRow> GetArtificialTreeFiles(string repoPath, ArtificialDiff which)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        if (which == ArtificialDiff.Index)
        {
            IReadOnlyList<GitItemStatus> files = module.GetTreeFiles(ObjectId.IndexId, full: true);
            List<DiffFileRow> indexRows = new(files.Count);
            foreach (GitItemStatus item in files)
            {
                indexRows.Add(new DiffFileRow(item.Name, OldName: null, DiffChangeKind.Modified, IsTracked: true));
            }

            return indexRows;
        }

        // On disk = (tracked ∪ untracked-not-ignored) \ deleted-from-worktree.
        // "--deduplicate" would spare the HashSet but only exists since git 2.31.
        HashSet<string> onDisk = new(StringComparer.Ordinal);
        foreach (string name in LsFiles(module, "--cached", "--others", "--exclude-standard"))
        {
            onDisk.Add(name);
        }

        foreach (string name in LsFiles(module, "--deleted"))
        {
            onDisk.Remove(name);
        }

        List<DiffFileRow> rows = new(onDisk.Count);
        foreach (string name in onDisk.OrderBy(n => n, StringComparer.Ordinal))
        {
            rows.Add(new DiffFileRow(name, OldName: null, DiffChangeKind.Modified, IsTracked: true));
        }

        return rows;
    }

    // "git ls-files -z <selectors>", NUL-separated so odd path names survive.
    private static IEnumerable<string> LsFiles(GitModule module, params string[] selectors)
    {
        GitArgumentBuilder args = new("ls-files") { "-z" };
        foreach (string selector in selectors)
        {
            args.Add(selector);
        }

        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return [];
        }

        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
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
    ///  Returns the files that differ between two commits — the changed-file set
    ///  of <c>git diff &lt;baseHash&gt; &lt;otherHash&gt;</c>. <paramref name="baseHash"/>
    ///  is the "old" side, <paramref name="otherHash"/> the "new" side.
    /// </summary>
    public static IReadOnlyList<DiffFileRow> GetDiffFilesBetween(string repoPath, string baseHash, string otherHash)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId baseId = ObjectId.Parse(baseHash);
        ObjectId otherId = ObjectId.Parse(otherHash);

        IReadOnlyList<GitItemStatus> changes = module.GetDiffFilesWithSubmodulesStatus(
            firstId: baseId,
            secondId: otherId,
            parentToSecond: baseId,
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
    ///  Returns the files that differ between <paramref name="commitHash"/> and
    ///  the current working tree — the changed-file set of <c>git diff &lt;commitHash&gt;</c>
    ///  (the commit is the "old" side, the working tree the "new" side).
    /// </summary>
    public static IReadOnlyList<DiffFileRow> GetChangedFilesAgainstWorkingTree(string repoPath, string commitHash)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);

        // secondId default (zero) => second revision null => "git diff <commit>"
        // compares the commit against the working tree.
        IReadOnlyList<GitItemStatus> changes = module.GetDiffFilesWithSubmodulesStatus(
            firstId: commitId,
            secondId: default,
            parentToSecond: commitId,
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
    ///  Returns the unified diff text for a single <paramref name="file"/> between
    ///  two commits — i.e. <c>git diff &lt;baseHash&gt; &lt;otherHash&gt; -- &lt;path&gt;</c>.
    /// </summary>
    public static async Task<string> GetFileDiffBetweenAsync(
        string repoPath,
        string baseHash,
        string otherHash,
        DiffFileRow file,
        CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId baseId = ObjectId.Parse(baseHash);
        ObjectId otherId = ObjectId.Parse(otherHash);

        (Patch? patch, string? errorMessage) = await module.GetSingleDiffAsync(
            firstId: baseId,
            secondId: otherId,
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

    /// <summary>
    ///  How a revision is named in the changed-file list's group header:
    ///  <c>&lt;short hash&gt;: &lt;subject&gt;</c>, which is upstream's
    ///  <c>DescribeRevision</c> (<c>FileStatusDiffCalculator</c>) in the form the
    ///  port can build without a revision cache.
    ///
    ///  <para>An empty or unparsable hash — the root commit's absent parent — gives
    ///  the empty string, and the caller then omits the header rather than naming a
    ///  revision that does not exist.</para>
    /// </summary>
    public static string DescribeRevision(string repoPath, string? hash)
    {
        if (hash is not { Length: > 0 } || !ObjectId.TryParse(hash, out ObjectId id) || id.IsZero)
        {
            return string.Empty;
        }

        GitModule module = GitContext.CreateModule(repoPath);

        // One cheap plumbing call rather than a revision cache: this runs on the
        // background thread of the file-list load, alongside the diff itself.
        GitArgumentBuilder args = new("log") { "-1", "--format=%s", id.ToString() };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        string subject = result.ExitedSuccessfully ? result.StandardOutput.Trim() : string.Empty;
        string shortHash = hash.Length > 8 ? hash[..8] : hash;

        return subject.Length > 0 ? $"{shortHash}: {subject}" : shortHash;
    }

    /// <summary>
    ///  The first parent of a commit as a hash string, empty for a root commit — the
    ///  "A" side of the comparison a single-commit selection shows.
    /// </summary>
    public static string FirstParentOf(string repoPath, string commitHash)
    {
        if (!ObjectId.TryParse(commitHash, out ObjectId id))
        {
            return string.Empty;
        }

        ObjectId parent = GetFirstParent(GitContext.CreateModule(repoPath), id);
        return parent.IsZero ? string.Empty : parent.ToString();
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
