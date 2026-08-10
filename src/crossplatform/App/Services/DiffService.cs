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
    /// <summary>
    ///  The "old" side of the comparison this row belongs to, when the row was
    ///  produced for a specific GROUP of a multi-group list
    ///  (<see cref="DiffService.GetSelectionDiffGroups"/>). Null for the ordinary
    ///  single-comparison lists, whose whole list shares the host's own pair.
    ///
    ///  <para>The pair rides on the ROW rather than on the group node because the
    ///  list control hands its host a <see cref="DiffFileRow"/> and nothing else
    ///  (<c>FileStatusListView.SelectedFile</c>), and the node types that would
    ///  otherwise have to carry it live in a file this change is not allowed to
    ///  touch. It is also the honest place for it: which two revisions a patch is
    ///  between is a property of the changed file, not of the header above it.</para>
    /// </summary>
    public string? FirstRev { get; init; }

    /// <summary>The "new" side of this row's comparison; see <see cref="FirstRev"/>.</summary>
    public string? SecondRev { get; init; }

    /// <summary>
    ///  Where the change was made, relative to the merge base, when this row is part
    ///  of a BASE-with-A / BASE-with-B comparison — upstream's
    ///  <c>GitItemStatus.DiffStatus</c>. <see cref="DiffBranchStatus.Unknown"/>
    ///  everywhere else, and the list then draws no marker.
    /// </summary>
    public DiffBranchStatus BranchStatus { get; init; }

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
///  One collapsible section of a changed-files list: a caption and the rows below
///  it — the port of upstream's <c>FileStatusWithDescription</c>.
///
///  <para>Deliberately thinner than upstream's: the two revisions and the icon
///  name it carries are not here, because the rows themselves carry the pair
///  (<see cref="DiffFileRow.FirstRev"/>) and the port draws no per-group icon —
///  the caption already says "Diff with A …" / "Diff BASE with A …", which is the
///  information the icon duplicates.</para>
/// </summary>
public sealed record DiffFileGroup(string Summary, IReadOnlyList<DiffFileRow> Rows);

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

        // A ROOT commit is listed from its own tree, every entry marked as added —
        // which is what upstream does (FileStatusDiffCalculator.CalculateDiffs, the
        // "no ParentIds" branch: GetTreeFiles + IsNew = true).
        //
        // It cannot go through the diff below. A zero first id does NOT mean "the
        // empty tree" to the core, whatever the comment on GetFirstParent used to
        // claim: the argument is simply omitted, so git receives `git diff <commit>`
        // and reads it as WORKTREE vs commit. The root commit of a repository with a
        // dirty working tree therefore listed the files the worktree happens to
        // differ in — one file, marked Modified — instead of the files the commit
        // introduced. Measured on a four-file root commit: it listed one.
        if (parentId.IsZero)
        {
            IReadOnlyList<GitItemStatus> tree = module.GetTreeFiles(commitId, full: true);
            List<DiffFileRow> added = new(tree.Count);
            foreach (GitItemStatus item in tree)
            {
                added.Add(new DiffFileRow(item.Name, OldName: null, DiffChangeKind.Added, IsTracked: true));
            }

            return added;
        }

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
    ///  The changed-file list of a single commit, as GROUPS: one per parent when the
    ///  commit is a merge and <paramref name="allParents"/> is on, a single unnamed
    ///  group otherwise — upstream's <c>ShowDiffForAllParents</c>
    ///  (<c>FileStatusDiffCalculator.CalculateDiffs</c>, the <c>Take(multipleParents)</c>
    ///  line).
    ///
    ///  <para>Each row of a per-parent group carries that parent as its
    ///  <see cref="DiffFileRow.FirstRev"/>, which is what makes a click under "Diff with
    ///  parent 2" load the patch against THAT parent. A non-merge commit yields exactly
    ///  what <see cref="GetChangedFiles"/> returns, unnamed and un-paired, so the common
    ///  case gains neither a caption nor a second git call.</para>
    /// </summary>
    public static IReadOnlyList<DiffFileGroup> GetCommitFileGroups(
        string repoPath, string commitHash, bool allParents)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);
        IReadOnlyList<ObjectId> parents = module.GetParents(commitId);

        if (!allParents || parents.Count < 2)
        {
            return [new DiffFileGroup(string.Empty, GetChangedFiles(repoPath, commitHash))];
        }

        List<DiffFileGroup> groups = new(parents.Count);
        foreach (ObjectId parent in parents)
        {
            groups.Add(new DiffFileGroup(
                DiffWithACaption(module, parent.ToString()),
                Between(module, parent.ToString(), commitHash)));
        }

        return groups;
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
    ///  The changed-file GROUPS a multi-revision selection produces — the port of
    ///  upstream's <c>FileStatusDiffCalculator.CalculateDiffs</c>, its
    ///  <c>revisions.Count > 1</c> branch (FileStatusDiffCalculator.cs:148-307).
    ///  <paramref name="revisions"/> is the selection NEWEST FIRST, as upstream
    ///  orders it, and must hold at least two real commits.
    ///
    ///  <para>What it answers, in upstream's own terms:</para>
    ///  <list type="bullet">
    ///   <item>More than four selected: only "first → selected" is interesting, so
    ///    that single group comes back and no merge base is looked for.</item>
    ///   <item>"first" is the LAST selected revision, except with exactly four,
    ///    where it is <c>revisions[2]</c> — four rows are read as two ranges
    ///    <c>baseA..headA baseB..headB</c>, so the first range's head is the third
    ///    row from the top.</item>
    ///   <item>Then the merge base: <c>merge-base(first, selected)</c> with two,
    ///    the middle revision with three (only if it really is an ancestor of both),
    ///    and with four the two ranges must check out as ranges.</item>
    ///   <item>No usable merge base — including a base that IS one of the two ends,
    ///    which means one side is simply an ancestor of the other — gives a MULTI
    ///    DIFF: one group per selected revision that is neither end, each against
    ///    the selected one.</item>
    ///   <item>A usable merge base gives two further groups, BASE→B and BASE→A,
    ///    whose rows are tagged with <see cref="DiffBranchStatus"/>.</item>
    ///  </list>
    ///
    ///  <para><b>Deliberately NOT ported</b>: upstream's last step, the synthetic
    ///  <c>Range diff … BASE …</c> row (<c>git range-diff</c>, lines 287-305). It is
    ///  a pseudo file row whose only purpose is to open a dedicated range-diff
    ///  viewer, and the port has no such viewer — the row would be a line of text
    ///  that does nothing when clicked. The two BASE groups carry the same
    ///  information in a form the port can actually show.</para>
    ///
    ///  <para><b>Also not ported</b>: <c>GetRevisionOrHead</c>, upstream's mapping of
    ///  the artificial work-tree/index rows onto HEAD. The port's grid never
    ///  announces an artificial row as part of a selection (see
    ///  <c>RevisionGridView.SelectedRevisionsNewestFirst</c>), so every hash reaching
    ///  this method is a real commit and there is nothing to substitute.</para>
    ///
    ///  <para>Blocking, like every other method here: several <c>git diff</c> and
    ///  <c>git merge-base</c> runs, to be called from a background thread only.</para>
    /// </summary>
    public static IReadOnlyList<DiffFileGroup> GetSelectionDiffGroups(string repoPath, IReadOnlyList<string> revisions)
    {
        if (revisions.Count < 2)
        {
            return [];
        }

        // One module for the whole calculation: it is the handle on the repository
        // that every git run below goes through, and building it per call would pay
        // for the same discovery a dozen times in one selection change.
        GitModule module = GitContext.CreateModule(repoPath);

        string selected = revisions[0];

        // Upstream's maxMultiCompare. Beyond it a selection is a range, not a
        // comparison of branches, and only its two ends mean anything.
        const int maxMultiCompare = 4;
        string first = revisions.Count == maxMultiCompare ? revisions[2] : revisions[^1];

        List<DiffFileRow> aToB = Between(module, first, selected);
        List<DiffFileGroup> groups =
        [
            new DiffFileGroup(DiffWithACaption(module, first), aToB),
        ];

        if (revisions.Count > maxMultiCompare)
        {
            return groups;
        }

        string? baseRev;
        if (revisions.Count != 3)
        {
            baseRev = MergeBaseService.FindMergeBase(module, first, selected);
        }
        else
        {
            // Three selected: the middle row is offered AS the base, and is accepted
            // only if it is an ancestor of both ends. Upstream tests that by asking
            // for the merge base and checking it comes back as the middle commit
            // itself, which also accepts a commit that sits EARLIER than the real
            // base — the user pointed at a common starting point, and that is enough.
            string middle = revisions[1];
            baseRev = Same(MergeBaseService.FindMergeBase(module, first, middle), middle)
                      && Same(MergeBaseService.FindMergeBase(module, selected, middle), middle)
                ? middle
                : null;
        }

        if (baseRev is not null && revisions.Count < maxMultiCompare)
        {
            // A base that is one of the ends means one end is an ancestor of the
            // other: the selection is a plain range, not two branches, and a
            // "BASE with A" group would repeat the first group verbatim.
            if (Same(baseRev, first) || Same(baseRev, selected))
            {
                baseRev = null;
            }
        }
        else if (baseRev is not null)
        {
            // Four selected: only two genuine ranges may be read as A and B. Row 3 has
            // to be the base of row 2 (= first) and row 1 the base of row 0 (=
            // selected); anything else is four unrelated commits, and upstream then
            // falls back to the multi diff rather than inventing a BASE.
            string? baseA = MergeBaseService.FindMergeBase(module, revisions[3], first);
            string? baseB = Same(baseA, revisions[3])
                ? MergeBaseService.FindMergeBase(module, revisions[1], selected)
                : null;

            if (!Same(baseB, revisions[1]))
            {
                baseRev = null;
            }
        }

        if (baseRev is null)
        {
            // No variant of a range diff: show each remaining selected revision as its
            // own comparison against the selected one, which is all that can honestly
            // be said about an arbitrary set of commits.
            foreach (string rev in revisions)
            {
                if (!Same(rev, first) && !Same(rev, selected))
                {
                    groups.Add(new DiffFileGroup(DiffWithACaption(module, rev), Between(module, rev, selected)));
                }
            }

            return groups;
        }

        List<DiffFileRow> baseToB = Between(module, baseRev, selected);
        List<DiffFileRow> baseToA = Between(module, baseRev, first);
        TagBranchStatus(aToB, baseToA, baseToB);

        string baseWith = TranslationService.T("TranslatedStrings/_diffBaseWith.Text", "Diff BASE with");
        groups.Add(new DiffFileGroup($"{baseWith} B {Describe(module, selected)}", baseToB));
        groups.Add(new DiffFileGroup($"{baseWith} A {Describe(module, first)}", baseToA));

        return groups;
    }

    // Upstream's per-file DiffBranchStatus, computed once over the three lists and
    // written back onto every row of all three — a file is marked with WHERE the
    // change was made, so the same file reads the same way in the A→B group and in
    // the two BASE groups. Sets are compared by name only, with a rename's old name
    // counting as the same file (upstream's GitItemStatusNameEqualityComparer).
    private static void TagBranchStatus(
        List<DiffFileRow> aToB,
        List<DiffFileRow> baseToA,
        List<DiffFileRow> baseToB)
    {
        // An exact rename/copy is left out of the "changed in both, identically"
        // test: it moves a file without touching its content, so counting it as a
        // change would mark untouched files as unequal between the two branches.
        // The port has no rename PERCENTAGE (GetDiffFilesBetween keeps only the
        // kind), so every rename/copy is treated as the exact one upstream excludes.
        List<DiffFileRow> aToBChanges =
            [.. aToB.Where(r => r.Kind is not (DiffChangeKind.Renamed or DiffChangeKind.Copied))];

        bool InAny(List<DiffFileRow> list, DiffFileRow row) => list.Exists(other => SameFile(other, row));

        DiffBranchStatus StatusOf(DiffFileRow row)
        {
            if (InAny(baseToB, row) && InAny(baseToA, row) && !InAny(aToBChanges, row))
            {
                return DiffBranchStatus.SameChange;
            }

            bool inA = InAny(baseToA, row);
            bool inB = InAny(baseToB, row);
            return inA && !inB ? DiffBranchStatus.OnlyAChange
                : inB && !inA ? DiffBranchStatus.OnlyBChange
                : DiffBranchStatus.UnequalChange;
        }

        foreach (List<DiffFileRow> list in new[] { aToB, baseToA, baseToB })
        {
            for (int i = 0; i < list.Count; i++)
            {
                list[i] = list[i] with { BranchStatus = StatusOf(list[i]) };
            }
        }
    }

    // Two rows name the same file when either of their names matches either of the
    // other's — a rename has to be recognised from both sides of the branch point.
    private static bool SameFile(DiffFileRow x, DiffFileRow y)
        => x.Name == y.Name
           || (!string.IsNullOrWhiteSpace(x.OldName) && x.OldName == y.Name)
           || (!string.IsNullOrWhiteSpace(y.OldName) && x.Name == y.OldName)
           || (!string.IsNullOrWhiteSpace(x.OldName) && !string.IsNullOrWhiteSpace(y.OldName) && x.OldName == y.OldName);

    private static bool Same(string? a, string? b)
        => a is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string DiffWithACaption(GitModule module, string rev)
        => TranslationService.T("TranslatedStrings/_diffWithParent.Text", "Diff with A ") + Describe(module, rev);

    // The changed files of first..second, each row already knowing which pair it
    // belongs to — that is what lets a click in ANY group load the patch of THAT
    // group instead of the one the pane was opened with.
    private static List<DiffFileRow> Between(GitModule module, string firstRev, string secondRev)
    {
        IReadOnlyList<GitItemStatus> changes = module.GetDiffFilesWithSubmodulesStatus(
            firstId: ObjectId.Parse(firstRev),
            secondId: ObjectId.Parse(secondRev),
            parentToSecond: ObjectId.Parse(firstRev),
            excludeSkipWorktreeFiles: true,
            untrackedFilesMode: UntrackedFilesMode.No,
            cancellationToken: CancellationToken.None);

        List<DiffFileRow> rows = new(changes.Count);
        foreach (GitItemStatus item in changes)
        {
            rows.Add(new DiffFileRow(item.Name, item.OldName, MapKind(item), item.IsTracked)
            {
                FirstRev = firstRev,
                SecondRev = secondRev,
            });
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

        return Describe(GitContext.CreateModule(repoPath), id.ToString());
    }

    // The same naming, for a caller that already holds the module — the multi-group
    // calculation names up to four revisions and must not open the repository again
    // for each of them.
    private static string Describe(GitModule module, string hash)
    {
        // One cheap plumbing call rather than a revision cache: this runs on the
        // background thread of the file-list load, alongside the diff itself.
        GitArgumentBuilder args = new("log") { "-1", "--format=%s", hash };
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

        // Root commit (no parents): a zero ObjectId, which every caller has to
        // recognise. It does NOT stand for the empty tree — the core omits the
        // argument, so git gets `git diff <commit>` and answers about the WORKTREE.
        // GetChangedFiles lists the tree instead; the patch path is unaffected
        // because `git show`-style single-file diffs already handle a root commit.
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
