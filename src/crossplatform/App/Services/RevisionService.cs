using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Which refs the revision log should walk, mirroring the branch-scope toggle
///  of the original Git Extensions revision grid.
/// </summary>
public enum BranchScope
{
    /// <summary>Walk every ref (<c>git log --all</c>) — the grid's default.</summary>
    AllBranches,

    /// <summary>Walk only the checked-out branch (plain <c>git log</c>, i.e. HEAD).</summary>
    CurrentBranch,

    /// <summary>
    ///  Walk an explicitly provided set of refs (plus HEAD when none is given).
    ///  With no selection UI wired yet this behaves as <see cref="CurrentBranch"/>.
    /// </summary>
    Filtered,
}

/// <summary>
///  A single commit row, projected from a core <see cref="GitRevision"/> for
///  display in the Avalonia revision grid. Field/type names are prefixed with
///  <c>Revision</c> to stay unique across sibling views.
/// </summary>
public sealed record RevisionRow(
    string Hash,
    string ShortHash,
    string Author,
    DateTime AuthorDate,
    DateTime CommitDate,
    string Subject,
    IReadOnlyList<string> ParentHashes,
    IReadOnlyList<string> RefNames)
{
    /// <summary>
    ///  The commit author's email address, used to derive a deterministic, offline
    ///  identicon avatar in the grid (no network / gravatar). Empty when the core
    ///  did not report one, in which case the avatar falls back to the author name.
    /// </summary>
    public string AuthorEmail { get; init; } = string.Empty;

    /// <summary>
    ///  True when this commit has a git note attached (loaded cheaply via a single
    ///  <c>git notes list</c> for the whole repository — see
    ///  <see cref="RevisionService.LoadNotes"/>). Shown as an indicator in the grid.
    /// </summary>
    public bool HasNotes { get; init; }

    /// <summary>Ref names joined for inline display, e.g. "[main] [origin/main] [v1.0]".</summary>
    public string RefsDisplay
        => RefNames.Count == 0 ? string.Empty : string.Join(" ", RefNames.Select(r => $"[{r}]"));

    /// <summary>Subject prefixed with any ref names.</summary>
    public string SubjectWithRefs
        => RefNames.Count == 0 ? Subject : $"{RefsDisplay} {Subject}";

    /// <summary>
    ///  Zero-based lane (column) that this commit's node dot occupies in the DAG
    ///  graph. Assigned by <see cref="RevisionService"/>'s lane-assignment pass.
    /// </summary>
    public int NodeLane { get; init; }

    /// <summary>
    ///  The line segments to draw in this row's graph cell (vertical lane lines,
    ///  plus the diagonal branch/merge edges into and out of the node).
    /// </summary>
    public IReadOnlyList<RevisionGraphSegment> GraphSegments { get; init; } = [];

    /// <summary>
    ///  Total number of lanes used by the whole graph (same value on every row),
    ///  so the view can size the fixed-width graph column consistently.
    /// </summary>
    public int LaneCount { get; init; } = 1;

    /// <summary>
    ///  True when this commit is the currently checked-out HEAD. Used by the view
    ///  to compute reachability (relatives / non-relatives) and to highlight the
    ///  current branch line — both are render-time styles, no reload.
    /// </summary>
    public bool IsHead { get; init; }
}

/// <summary>
///  A single line segment inside a row's graph cell. Coordinates are expressed
///  as a lane index (mapped by the view to an x pixel = lane * laneWidth +
///  laneWidth / 2) and a vertical fraction of the row height
///  (0 = top edge, 0.5 = vertical centre / node row, 1 = bottom edge). Every
///  edge is split at the centre so branch/merge diagonals meet cleanly at the
///  node. <see cref="ColorLane"/> selects the segment colour from the palette.
/// </summary>
public sealed record RevisionGraphSegment(
    double FromLane,
    double FromY,
    double ToLane,
    double ToY,
    int ColorLane);

/// <summary>
///  One page of the revision walk, as returned by
///  <see cref="RevisionService.LoadRevisionPage"/>.
///
///  <para><see cref="Rows"/> carries the commits of the requested window in walk
///  order (newest first), <b>without</b> DAG geometry: lanes and segments are only
///  meaningful for a contiguous run of rows, so the caller accumulates the pages it
///  has and rebuilds the graph over the whole accumulated list with
///  <see cref="RevisionService.BuildRevisionGraph"/>.</para>
///
///  <para><see cref="HasMore"/> is true when the page came back full, i.e. the walk
///  very likely continues past it — the cheap equivalent of the original's
///  incremental loading, without paying for a full <c>git rev-list --count</c>.</para>
/// </summary>
public sealed record RevisionPage(IReadOnlyList<RevisionRow> Rows, bool HasMore);

/// <summary>
///  Loads revisions for a repository by reusing the Git Extensions core
///  (<see cref="GitContext.CreateModule"/> + <see cref="RevisionReader"/>),
///  the same code path the Windows app uses.
/// </summary>
public sealed class RevisionService
{
    // Per-repository metadata (HEAD, ref names, commits carrying notes) shared by
    // every page of the same walk. Refreshed when a walk restarts (skip == 0) and
    // reused by the follow-up pages, so scrolling further back does not re-run
    // `git for-each-ref` / `git notes list` on every append.
    private readonly object _metadataLock = new();
    private string _metadataRepo = string.Empty;
    private RepoMetadata? _metadata;
    /// <summary>
    ///  Loads the most recent <paramref name="maxCount"/> commits, newest first,
    ///  with author, date, subject, parent hashes and ref names (branches/tags)
    ///  attached. The <paramref name="scope"/> selects which refs the log walks:
    ///  every ref (<see cref="BranchScope.AllBranches"/>, the default), only the
    ///  current branch (<see cref="BranchScope.CurrentBranch"/>, plain HEAD), or a
    ///  provided ref set (<see cref="BranchScope.Filtered"/>; falls back to HEAD
    ///  when <paramref name="filteredRefs"/> is empty).
    ///
    ///  <para>Under <see cref="BranchScope.AllBranches"/> the walked ref set is
    ///  built explicitly from local branches plus, per the "View" toggles,
    ///  remote-tracking branches (<paramref name="showRemotes"/> → <c>--remotes</c>),
    ///  tags (<paramref name="showTags"/> → <c>--tags</c>) and stash commits
    ///  (<paramref name="showStashes"/> → the hashes from <c>git stash list</c>).
    ///  These toggles are inert for the current-branch / filtered scopes, which
    ///  walk an explicit HEAD/ref set only.</para>
    ///
    ///  <para><paramref name="topoOrder"/> switches the walk from the default date
    ///  order to topological order (<c>--topo-order</c>).</para>
    /// </summary>
    public IReadOnlyList<RevisionRow> LoadRevisions(
        string repoPath,
        int maxCount = 200,
        BranchScope scope = BranchScope.AllBranches,
        IReadOnlyList<string>? filteredRefs = null,
        bool showRemotes = true,
        bool showTags = true,
        bool showStashes = false,
        bool topoOrder = false,
        CancellationToken cancellationToken = default)
        => BuildRevisionGraph(LoadRevisionPage(
            repoPath,
            skip: 0,
            maxCount: maxCount,
            scope: scope,
            filteredRefs: filteredRefs,
            showRemotes: showRemotes,
            showTags: showTags,
            showStashes: showStashes,
            topoOrder: topoOrder,
            cancellationToken: cancellationToken).Rows);

    /// <summary>
    ///  Loads ONE PAGE of the revision walk: the <paramref name="maxCount"/> commits
    ///  that follow the first <paramref name="skip"/> ones, newest first. This is the
    ///  primitive behind the grid's incremental history loading — the first page is
    ///  fetched when the repository opens and further pages are appended on demand,
    ///  so a repository with tens of thousands of commits opens as fast as a small
    ///  one while its whole history stays reachable.
    ///
    ///  <para>The returned rows carry NO graph geometry (see <see cref="RevisionPage"/>);
    ///  the caller rebuilds it over the accumulated list via
    ///  <see cref="BuildRevisionGraph"/>. Every other parameter has exactly the meaning
    ///  documented on <see cref="LoadRevisions"/>.</para>
    /// </summary>
    public RevisionPage LoadRevisionPage(
        string repoPath,
        int skip,
        int maxCount,
        BranchScope scope = BranchScope.AllBranches,
        IReadOnlyList<string>? filteredRefs = null,
        bool showRemotes = true,
        bool showTags = true,
        bool showStashes = false,
        bool topoOrder = false,
        CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // HEAD, ref names and note-carrying commits. Re-read when the walk restarts
        // (first page), reused as-is by the follow-up pages of the same walk.
        RepoMetadata metadata = GetMetadata(module, repoPath, refresh: skip <= 0);
        string headHash = metadata.HeadHash;
        Dictionary<ObjectId, List<string>> refsByCommit = metadata.RefsByCommit;
        HashSet<string> commitsWithNotes = metadata.CommitsWithNotes;

        RevisionCollector collector = new();
        RevisionReader reader = new(module);

        // The revision filter is fed verbatim into `git log`. --max-count caps it;
        // the scope suffix chooses the walked refs:
        //   AllBranches   -> HEAD + --branches, plus --remotes/--tags/stash hashes
        //                    per the "View" toggles (an explicit form of --all so
        //                    remote/tag/stash inclusion can be switched off).
        //   CurrentBranch -> ""             (git log defaults to HEAD)
        //   Filtered      -> the given refs (or HEAD when none supplied)
        string scopeArgs;
        if (scope == BranchScope.AllBranches)
        {
            List<string> parts = ["HEAD", "--branches"];
            if (showRemotes)
            {
                parts.Add("--remotes");
            }

            if (showTags)
            {
                parts.Add("--tags");
            }

            if (showStashes)
            {
                // Stashes are not reachable from ordinary refs, so include their
                // commit hashes explicitly (their ancestry is already covered by
                // the branch walk). Best-effort: absent/failed stash list is fine.
                parts.AddRange(LoadStashHashes(module));
            }

            scopeArgs = string.Join(' ', parts);
        }
        else if (scope == BranchScope.CurrentBranch)
        {
            scopeArgs = string.Empty;
        }
        else
        {
            scopeArgs = filteredRefs is { Count: > 0 }
                ? string.Join(' ', filteredRefs)
                : "HEAD";
        }

        // Topological vs. the default (commit-date) ordering.
        string orderArg = topoOrder ? " --topo-order" : string.Empty;

        // --skip selects the page. git applies it BEFORE --max-count, and the walk is
        // deterministic for a fixed ref set + order, so consecutive pages line up into
        // exactly the list a single big --max-count would have produced.
        string skipArg = skip > 0 ? $" --skip={skip}" : string.Empty;

        string countArgs = $"--max-count={maxCount}{skipArg}{orderArg}";
        string revisionFilter = scopeArgs.Length == 0
            ? countArgs
            : $"{countArgs} {scopeArgs}";

        reader.GetLog(
            subject: collector,
            revisionFilter: revisionFilter,
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
                AuthorDate: revision.AuthorDate,
                CommitDate: revision.CommitDate,
                Subject: revision.Subject ?? string.Empty,
                ParentHashes: parents,
                RefNames: refNames)
            {
                AuthorEmail = revision.AuthorEmail ?? string.Empty,
                HasNotes = commitsWithNotes.Contains(hash),
                IsHead = headHash.Length > 0 && hash.Equals(headHash, StringComparison.OrdinalIgnoreCase),
            });
        }

        // A full page means the walk very likely continues; a short one is the end.
        return new RevisionPage(rows, HasMore: maxCount > 0 && rows.Count >= maxCount);
    }

    /// <summary>
    ///  Assigns DAG lanes and computes the graph segments for an ordered
    ///  (newest-first) run of revisions — the accumulated pages of one walk. Pure and
    ///  in-memory: no git is run, so it is cheap enough to redo on every appended page
    ///  (which is also what keeps the artificial "working directory" / "index" rows and
    ///  the lane lines correct as the history grows).
    /// </summary>
    public static IReadOnlyList<RevisionRow> BuildRevisionGraph(IReadOnlyList<RevisionRow> rows)
        => BuildGraph(rows as List<RevisionRow> ?? [.. rows]);

    /// <summary>
    ///  HEAD, the ref-name lookup and the note-carrying commits of one repository —
    ///  the per-walk metadata shared by every page.
    /// </summary>
    private sealed record RepoMetadata(
        string HeadHash,
        Dictionary<ObjectId, List<string>> RefsByCommit,
        HashSet<string> CommitsWithNotes);

    // Returns the cached metadata for the repository, re-reading it when the walk
    // restarts, when the repository changed, or when nothing is cached yet.
    private RepoMetadata GetMetadata(GitModule module, string repoPath, bool refresh)
    {
        lock (_metadataLock)
        {
            if (!refresh
                && _metadata is not null
                && string.Equals(_metadataRepo, repoPath, StringComparison.Ordinal))
            {
                return _metadata;
            }
        }

        RepoMetadata fresh = LoadMetadata(module);
        lock (_metadataLock)
        {
            _metadata = fresh;
            _metadataRepo = repoPath;
        }

        return fresh;
    }

    private static RepoMetadata LoadMetadata(GitModule module)
    {
        // The currently checked-out commit, so the view can mark the HEAD row and
        // compute reachability for the relatives/highlight render styles.
        string headHash = string.Empty;
        try
        {
            ObjectId head = module.GetCurrentCheckout();
            if (head is { IsZero: false, IsArtificial: false })
            {
                headHash = head.ToString();
            }
        }
        catch
        {
            // HEAD is a nicety for styling only; never block the log on it.
        }

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

        // Commits carrying a git note. Loaded with a SINGLE `git notes list` for the
        // whole repository (not one call per row) so the indicator column is cheap.
        return new RepoMetadata(headHash, refsByCommit, LoadNotes(module));
    }

    /// <summary>
    ///  Returns the set of commit hashes that have a git note attached, using a
    ///  single <c>git notes list</c> invocation for the whole repository. Each line
    ///  of the output is "&lt;noteBlob&gt; &lt;annotatedCommit&gt;"; we keep the second
    ///  token. Failures (no notes ref, older git) are swallowed and yield an empty
    ///  set so the log always loads.
    /// </summary>
    private static HashSet<string> LoadNotes(GitModule module)
    {
        HashSet<string> withNotes = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            GitArgumentBuilder args = new("notes") { "list" };
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return withNotes;
            }

            foreach (string rawLine in result.StandardOutput.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                int space = line.IndexOf(' ');
                string commit = space >= 0 ? line[(space + 1)..].Trim() : line;
                if (commit.Length > 0)
                {
                    withNotes.Add(commit);
                }
            }
        }
        catch
        {
            // Notes are a nicety; never block the log on their absence/failure.
        }

        return withNotes;
    }

    /// <summary>
    ///  Returns the commit hashes of the repository's stashes via a single
    ///  <c>git stash list --format=%H</c>. Used to include stash commits in the
    ///  "All branches" walk when the "Show stashes" toggle is on. Failures (no
    ///  stashes, older git) yield an empty list so the log always loads.
    /// </summary>
    private static IEnumerable<string> LoadStashHashes(GitModule module)
    {
        List<string> hashes = [];
        try
        {
            GitArgumentBuilder args = new("stash") { "list", "--format=%H" };
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return hashes;
            }

            foreach (string rawLine in result.StandardOutput.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length > 0)
                {
                    hashes.Add(line);
                }
            }
        }
        catch
        {
            // Stashes are a nicety; never block the log on their absence/failure.
        }

        return hashes;
    }

    /// <summary>
    ///  Assigns DAG lanes to an ordered (newest-first) list of revisions and
    ///  computes the line segments needed to render the commit graph.
    ///
    ///  <para>Algorithm — a single top-to-bottom sweep with stable lane indices
    ///  (gitk / Git Extensions style):</para>
    ///  <list type="bullet">
    ///   <item>A running list of "active lanes" is kept; each slot holds the hash
    ///    of the commit that lane is currently waiting to reach (its next
    ///    ancestor), or <c>null</c> when free.</item>
    ///   <item>For each commit, its node lane is the lowest lane already waiting
    ///    for it (a descendant's edge); if none exists it takes the lowest free
    ///    lane (a branch tip).</item>
    ///   <item>Every lane waiting for this commit terminates at the node (merge
    ///    of children); the node's first parent continues down the same lane and
    ///    each further parent takes a free lane (a fork / merge-parent edge).</item>
    ///   <item>Lanes keep their column for their whole life, so lines are drawn
    ///    as: straight verticals for pass-through lanes, diagonals from top-edge
    ///    to centre for edges converging on the node, and diagonals from centre
    ///    to bottom-edge for the node's edges to its parents.</item>
    ///  </list>
    /// </summary>
    private static IReadOnlyList<RevisionRow> BuildGraph(List<RevisionRow> input)
    {
        List<string?> lanes = [];
        int laneCount = 1;
        List<RevisionRow> result = new(input.Count);

        foreach (RevisionRow row in input)
        {
            string?[] incoming = lanes.ToArray();

            // The node lane: reuse the lowest lane already waiting for this commit,
            // otherwise take the lowest free lane (a branch tip with no descendant).
            int nodeLane = IndexOf(lanes, row.Hash);
            if (nodeLane < 0)
            {
                nodeLane = FirstFree(lanes);
            }

            // Every lane that was waiting for this commit ends here (children merge in).
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] == row.Hash)
                {
                    lanes[i] = null;
                }
            }

            // The node's edges to its parents all emanate from the node lane.
            HashSet<int> nodeOrigin = [];
            IReadOnlyList<string> parents = row.ParentHashes;
            if (parents.Count > 0)
            {
                // First parent continues straight down the node lane.
                Set(lanes, nodeLane, parents[0]);
                nodeOrigin.Add(nodeLane);

                // Extra parents (merge) branch off into reused or fresh lanes.
                for (int p = 1; p < parents.Count; p++)
                {
                    int existing = IndexOf(lanes, parents[p]);
                    int pl = existing >= 0 ? existing : FirstFree(lanes);
                    Set(lanes, pl, parents[p]);
                    nodeOrigin.Add(pl);
                }
            }
            else
            {
                // Root commit: nothing continues below the node.
                Set(lanes, nodeLane, null);
            }

            // Drop trailing free lanes so the graph stays as narrow as possible.
            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
            }

            string?[] outgoing = lanes.ToArray();

            List<RevisionGraphSegment> segments = [];

            // Top halves: every incoming lane runs from the top edge to the centre.
            // Edges targeting this commit converge on the node lane; the rest pass straight.
            for (int i = 0; i < incoming.Length; i++)
            {
                if (incoming[i] is null)
                {
                    continue;
                }

                int target = incoming[i] == row.Hash ? nodeLane : i;
                segments.Add(new RevisionGraphSegment(i, 0.0, target, 0.5, i));
            }

            // Bottom halves: every outgoing lane runs from the centre to the bottom edge.
            // Lanes that originate at the node (parent edges) start at the node lane.
            for (int i = 0; i < outgoing.Length; i++)
            {
                if (outgoing[i] is null)
                {
                    continue;
                }

                int source = nodeOrigin.Contains(i) ? nodeLane : i;
                segments.Add(new RevisionGraphSegment(source, 0.5, i, 1.0, i));
            }

            laneCount = Math.Max(laneCount, Math.Max(nodeLane + 1, Math.Max(incoming.Length, outgoing.Length)));

            result.Add(row with { NodeLane = nodeLane, GraphSegments = segments });
        }

        // Stamp the shared lane count onto every row so the column width is uniform.
        for (int i = 0; i < result.Count; i++)
        {
            result[i] = result[i] with { LaneCount = laneCount };
        }

        return result;
    }

    private static int FirstFree(List<string?> lanes)
    {
        for (int i = 0; i < lanes.Count; i++)
        {
            if (lanes[i] is null)
            {
                return i;
            }
        }

        return lanes.Count;
    }

    private static int IndexOf(List<string?> lanes, string value)
    {
        for (int i = 0; i < lanes.Count; i++)
        {
            if (lanes[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Set(List<string?> lanes, int index, string? value)
    {
        while (lanes.Count <= index)
        {
            lanes.Add(null);
        }

        lanes[index] = value;
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
