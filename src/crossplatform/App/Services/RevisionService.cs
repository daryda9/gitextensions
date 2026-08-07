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
    ///  Walk an explicitly provided set of refs — the grid's "Filtered branches"
    ///  picker fills it. An EMPTY set falls back to HEAD, i.e. to
    ///  <see cref="CurrentBranch"/>; that is the "nothing chosen yet" case, and the
    ///  picker says so rather than implying a filter is in effect.
    /// </summary>
    Filtered,
}

/// <summary>
///  The criteria of the revision filter, mirroring the original
///  <c>FormRevisionFilter</c> / <c>FilterInfo</c> pair. Every criterion is
///  translated into <c>git log</c> arguments by <see cref="BuildLogArguments"/>
///  and applied by git DURING THE WALK — never by post-filtering rows in memory,
///  so the filter sees the whole history and not just the pages already loaded.
///
///  <para>An empty instance (<see cref="None"/>) means "no filter": the walk runs
///  exactly as it did before this type existed.</para>
/// </summary>
public sealed record RevisionFilter
{
    /// <summary>The neutral filter: every criterion off.</summary>
    public static readonly RevisionFilter None = new();

    /// <summary>Author pattern → <c>--author=</c>.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Committer pattern → <c>--committer=</c>.</summary>
    public string Committer { get; init; } = string.Empty;

    /// <summary>Commit-message pattern → <c>--grep=</c>.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    ///  Diff-content ("pickaxe") search → <c>-S</c> (occurrence count of a literal
    ///  string changed) or <c>-G</c> (added/removed line matches a regex), per
    ///  <see cref="DiffContentIsRegex"/>. The original only ever offers <c>-G</c>;
    ///  <c>-S</c> is added here because it is both cheaper and what most users
    ///  actually mean by "which commit introduced this text".
    /// </summary>
    public string DiffContent { get; init; } = string.Empty;

    /// <summary><see langword="true"/> → <c>-G</c> (regex), otherwise <c>-S</c> (literal).</summary>
    public bool DiffContentIsRegex { get; init; }

    /// <summary>
    ///  Lower bound of the date range → <c>--since=</c>. Free text: anything git's
    ///  approxidate parser accepts ("2024-01-31", "3 weeks ago", "last monday").
    /// </summary>
    public string DateFrom { get; init; } = string.Empty;

    /// <summary>Upper bound of the date range → <c>--until=</c>. Same syntax as <see cref="DateFrom"/>.</summary>
    public string DateTo { get; init; } = string.Empty;

    /// <summary>
    ///  Path filter: one or more paths/globs restricting the walk to commits that
    ///  touch them. Emitted AFTER the <c>--</c> separator (see
    ///  <see cref="BuildPathArgument"/>) — never before it, or git would try to
    ///  resolve the path as a revision.
    /// </summary>
    public string PathFilter { get; init; } = string.Empty;

    /// <summary>
    ///  Hard cap on how many commits the filtered walk may yield (0 = no cap), the
    ///  equivalent of the original's "Limit". Applied ACROSS pages, not per page.
    /// </summary>
    public int CommitsLimit { get; init; }

    /// <summary>
    ///  When <see langword="false"/> (the original's default "Ignore case" ticked)
    ///  the text patterns match case-insensitively → <c>--regexp-ignore-case</c>.
    /// </summary>
    public bool CaseSensitive { get; init; }

    /// <summary>
    ///  When <see langword="false"/> the text patterns are literal →
    ///  <c>--fixed-strings</c>; when <see langword="true"/> they are git regexes
    ///  (git's own default). Does not affect <c>-G</c>, which is always a regex,
    ///  nor <c>-S</c>, which is always literal.
    /// </summary>
    public bool UseRegex { get; init; }

    /// <summary>Skip merge commits → <c>--no-merges</c>.</summary>
    public bool HideMergeCommits { get; init; }

    /// <summary>Follow only the first parent of each merge → <c>--first-parent</c>.</summary>
    public bool FirstParentOnly { get; init; }

    /// <summary>Keep only commits referenced by a branch/tag → <c>--simplify-by-decoration</c>.</summary>
    public bool SimplifyByDecoration { get; init; }

    /// <summary>
    ///  Trace the single path in <see cref="PathFilter"/> across renames →
    ///  <c>--follow --find-renames --find-copies</c>. This is what the file history
    ///  needs: without it the walk stops dead at the commit that renamed the file.
    ///
    ///  <para>Only honoured when the path filter names EXACTLY ONE path (see
    ///  <see cref="FollowsSinglePath"/>) — git rejects <c>--follow</c> with several
    ///  pathspecs. It also constrains the walk itself, which
    ///  <see cref="RevisionService.LoadRevisionPage"/> enforces; see the notes there.</para>
    /// </summary>
    public bool FollowRenames { get; init; }

    /// <summary>
    ///  Restrict rename/copy detection to identical content →
    ///  <c>--find-renames="100%" --find-copies="100%"</c> instead of the default
    ///  similarity heuristics. Inert unless <see cref="FollowRenames"/> applies.
    ///  Upstream: <c>FormFileHistory</c>'s "Detect and follow - exact renames and
    ///  copies only".
    /// </summary>
    public bool ExactRenamesAndCopiesOnly { get; init; }

    /// <summary>
    ///  Do not simplify the history to the commits that changed the path →
    ///  <c>--full-history</c>. Upstream: the file history's "Show full history".
    /// </summary>
    public bool FullHistory { get; init; }

    /// <summary>
    ///  Prune the merges that <see cref="FullHistory"/> brings back to the ones that
    ///  actually matter for the path → <c>--simplify-merges</c>. Emitted only
    ///  together with <see cref="FullHistory"/>, exactly as upstream only enables the
    ///  entry while "Show full history" is on.
    /// </summary>
    public bool SimplifyMerges { get; init; }

    /// <summary>
    ///  True when <see cref="FollowRenames"/> can actually be handed to git: it is
    ///  asked for AND the path filter resolves to exactly one path.
    /// </summary>
    public bool FollowsSinglePath => FollowRenames && PathCount == 1;

    /// <summary>
    ///  How many paths <see cref="PathFilter"/> names. Whitespace separates paths
    ///  (as upstream does), but not inside a quoted value — a single path containing
    ///  spaces is one path, not several.
    /// </summary>
    public int PathCount => SplitPaths(PathFilter).Count;

    // Splits the path filter into its paths, honouring single/double quotes so that
    // "my dir/a b.txt" counts as ONE path.
    private static List<string> SplitPaths(string value)
    {
        List<string> paths = [];
        System.Text.StringBuilder current = new();
        char quote = '\0';

        foreach (char c in value)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    paths.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            paths.Add(current.ToString());
        }

        return paths;
    }

    /// <summary>True when at least one text pattern is set (author/committer/message/diff).</summary>
    public bool HasTextCriteria
        => Has(Author) || Has(Committer) || Has(Message) || Has(DiffContent);

    /// <summary>
    ///  True when any criterion is set, i.e. the walk is narrower than the plain
    ///  history. Drives the "filter active" indicator and the reset affordance.
    /// </summary>
    public bool IsActive
        => HasTextCriteria
        || Has(DateFrom) || Has(DateTo) || Has(PathFilter)
        || CommitsLimit > 0
        || HideMergeCommits || FirstParentOnly || SimplifyByDecoration;

    /// <summary>
    ///  True when the filter makes git REWRITE parent links (history simplification),
    ///  which is what keeps the DAG connected across the commits the filter removed.
    ///  Mirrors the original's <c>HasRevisionFilter</c>.
    /// </summary>
    public bool RewritesParents
        => HasTextCriteria || Has(PathFilter) || HideMergeCommits || SimplifyByDecoration;

    /// <summary>
    ///  The <c>git log</c> arguments for every criterion EXCEPT the path filter
    ///  (which must follow the <c>--</c> separator) and the paging window
    ///  (<c>--max-count</c>/<c>--skip</c>, owned by the caller).
    ///
    ///  <para>Values are quoted so that patterns containing spaces survive the
    ///  command line; <c>--regexp-ignore-case</c> / <c>--fixed-strings</c> are
    ///  emitted once for the whole set, as git applies them to every pattern.</para>
    /// </summary>
    public IReadOnlyList<string> BuildLogArguments()
    {
        List<string> args = [];

        if (HideMergeCommits)
        {
            args.Add("--no-merges");
        }

        if (FirstParentOnly)
        {
            args.Add("--first-parent");
        }

        if (SimplifyByDecoration)
        {
            args.Add("--simplify-by-decoration");
        }

        // Rename following, for the file history. Emitted only when the path filter
        // names ONE path: git refuses --follow with several pathspecs, and passing it
        // anyway would fail the whole walk.
        if (FollowsSinglePath)
        {
            args.Add("--follow");
            args.Add(ExactRenamesAndCopiesOnly
                ? "--find-renames=\"100%\" --find-copies=\"100%\""
                : "--find-renames --find-copies");
        }

        if (FullHistory)
        {
            args.Add("--full-history");

            // Upstream only enables "Simplify merges" while "Show full history" is on.
            if (SimplifyMerges)
            {
                args.Add("--simplify-merges");
            }
        }

        if (Has(DateFrom))
        {
            args.Add($"--since={Quote(DateFrom.Trim())}");
        }

        if (Has(DateTo))
        {
            args.Add($"--until={Quote(DateTo.Trim())}");
        }

        // Case / literalness apply to --author, --committer and --grep alike, so
        // they are emitted once, before the patterns themselves.
        if (HasTextCriteria)
        {
            if (!CaseSensitive)
            {
                args.Add("--regexp-ignore-case");
            }

            if (!UseRegex)
            {
                args.Add("--fixed-strings");
            }
        }

        if (Has(Author))
        {
            args.Add($"--author={Quote(Author.Trim())}");
        }

        if (Has(Committer))
        {
            args.Add($"--committer={Quote(Committer.Trim())}");
        }

        if (Has(Message))
        {
            args.Add($"--grep={Quote(Message.Trim())}");
        }

        if (Has(DiffContent))
        {
            // -S counts occurrences of a literal string, -G matches a regex against
            // the added/removed lines. Both are written as a separate argument so a
            // pattern starting with "-" cannot be mistaken for an option.
            args.Add(DiffContentIsRegex ? "-G" : "-S");
            args.Add(Quote(DiffContent.Trim()));
        }

        // With any history-simplifying criterion, ask git to rewrite the parent
        // links (%P then reports the nearest SURVIVING ancestors). Without this the
        // filtered rows would reference parents that are not in the result set and
        // the DAG would degenerate into disconnected dots.
        if (RewritesParents)
        {
            args.Add("--parents");
        }

        return args;
    }

    /// <summary>
    ///  The path filter as it must appear AFTER the <c>--</c> separator, or an empty
    ///  string when unset. A single path is quoted as one argument; whitespace in an
    ///  unquoted value is read as a separator between SEVERAL paths (as upstream
    ///  does), and a value the user already quoted is passed through untouched.
    /// </summary>
    public string BuildPathArgument()
    {
        string path = PathFilter.Trim();
        if (path.Length == 0)
        {
            return string.Empty;
        }

        // Already quoted by the user (single or double): trust it verbatim.
        if (path.Contains('"') || path.Contains('\''))
        {
            return path;
        }

        string[] parts = path.Split((char[]?)null, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(Quote));
    }

    /// <summary>
    ///  A short human-readable description of the active criteria, for the grid's
    ///  status line ("author: foo · path: src/"). Empty when the filter is inert.
    /// </summary>
    public string Summarize(Func<string, string> translate)
    {
        List<string> parts = [];
        void Add(string label, string value)
        {
            if (Has(value))
            {
                parts.Add($"{translate(label)}: {value.Trim()}");
            }
        }

        Add("author", Author);
        Add("committer", Committer);
        Add("message", Message);
        if (Has(DiffContent))
        {
            parts.Add($"{translate(DiffContentIsRegex ? "diff ~" : "diff")}: {DiffContent.Trim()}");
        }

        Add("since", DateFrom);
        Add("until", DateTo);
        Add("path", PathFilter);
        if (CommitsLimit > 0)
        {
            parts.Add($"{translate("limit")}: {CommitsLimit}");
        }

        if (HideMergeCommits)
        {
            parts.Add(translate("no merges"));
        }

        if (FirstParentOnly)
        {
            parts.Add(translate("first parent"));
        }

        if (SimplifyByDecoration)
        {
            parts.Add(translate("decorated only"));
        }

        return string.Join(" · ", parts);
    }

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    // git is started through ProcessStartInfo.Arguments, i.e. a single command line
    // that .NET re-splits with quote-aware rules on Unix too. Wrapping the value in
    // double quotes (escaping any it contains) keeps spaces inside one argument.
    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
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
    ///  Palette index of this row's node dot. Distinct from <see cref="NodeLane"/>:
    ///  a lane is only a column and gets recycled between unrelated branches, so the
    ///  colour is keyed on the edge identity assigned by the lane-assignment pass.
    /// </summary>
    public int NodeColor { get; init; }

    /// <summary>
    ///  Parents used by the lane-assignment pass instead of <see cref="ParentHashes"/>,
    ///  or <c>null</c> to use the real ones. Set by the synthesised
    ///  "working directory" / "commit index" rows, which have no parents of their own
    ///  (DAG navigation must never walk into or out of them) but do need a real edge
    ///  down to the checked-out commit, laid out by the graph rather than painted over
    ///  it afterwards — and by <see cref="ChainFollowedHistory"/>, where git refuses to
    ///  rewrite the parent links at all.
    /// </summary>
    public IReadOnlyList<string>? GraphParents { get; init; }

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
///  node. <see cref="ColorLane"/> selects the segment colour from the palette; it
///  is an edge identity, NOT the lane index (lanes are recycled between unrelated
///  branches, colours are not).
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
    ///  order to topological order (<c>--topo-order</c>); the page loader also
    ///  accepts an author-date order (<c>--author-date-order</c>), which is the
    ///  original's third <c>RevisionSortOrder</c>.</para>
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
        RevisionFilter? filter = null,
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
            filter: filter,
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
    ///
    ///  <para><paramref name="filter"/> narrows the walk ITSELF (author, committer,
    ///  message, diff content, date range, path, …): the criteria become <c>git log</c>
    ///  arguments, so the filter applies to the whole history rather than to the pages
    ///  already loaded, and paging keeps working — <c>--skip</c>/<c>--max-count</c> then
    ///  index into the FILTERED walk. Its "Limit" caps the total across pages.</para>
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
        RevisionFilter? filter = null,
        bool authorDateOrder = false,
        CancellationToken cancellationToken = default)
    {
        RevisionFilter criteria = filter ?? RevisionFilter.None;

        // The filter's own "Limit" caps the WHOLE filtered walk, not each page: the
        // window this page may still use is what is left of that budget. Once the
        // budget is spent the walk is over, without asking git anything.
        if (criteria.CommitsLimit > 0)
        {
            int remaining = criteria.CommitsLimit - Math.Max(0, skip);
            if (remaining <= 0)
            {
                return new RevisionPage([], HasMore: false);
            }

            maxCount = Math.Min(maxCount, remaining);
        }

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
        // --follow is measurably fragile (git 2.43): it needs a SINGLE starting commit
        // and the default date order, or it silently stops at the commit that renamed
        // the file instead of failing. Measured on a repo with one rename:
        //   git log --follow -- sub/new.txt                       -> 6 commits (correct)
        //   git log --follow HEAD --branches --remotes --tags -- … -> 3 commits (truncated)
        //   git log --follow --topo-order HEAD -- …                -> 3 commits (truncated)
        //   git log --follow --author-date-order HEAD -- …         -> 6 commits (fine)
        // A truncated history looks exactly like a complete one, so the walk is forced
        // into the shape that works rather than trusting the caller's toggles: the
        // scope/remotes/tags/stashes and the topological order are IGNORED while
        // following. The caller (RevisionGridView's file-history mode) hides the
        // controls that would suggest otherwise.
        bool following = criteria.FollowsSinglePath;

        string scopeArgs;
        if (following)
        {
            // Empty: `git log` then walks HEAD, its single-starting-commit default.
            scopeArgs = string.Empty;
        }
        else if (scope == BranchScope.AllBranches)
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

        // Walk order, mirroring the original's RevisionSortOrder (GitDefault /
        // AuthorDate / Topology): topological order wins when both are asked for,
        // since it is the stronger constraint.
        string orderArg = topoOrder && !following
            ? " --topo-order"
            : authorDateOrder ? " --author-date-order" : string.Empty;

        // --skip selects the page. git applies it BEFORE --max-count, and the walk is
        // deterministic for a fixed ref set + order, so consecutive pages line up into
        // exactly the list a single big --max-count would have produced.
        //
        // …EXCEPT while following renames: --skip and --follow do not compose. Measured
        // on the same repo (rename at position 3 of 6):
        //   --follow --skip=1 -> 5 commits, --skip=2 -> 4 commits (both correct)
        //   --follow --skip=3 -> EMPTY, --skip=4 -> EMPTY (should be 3 and 2)
        // git drops the whole tail once the skip walks past the rename, so a page
        // beyond it would report "end of history". The page is therefore taken by
        // walking from the top with a wider window and dropping the first `skip` rows
        // HERE — one file's history is short enough for that to be cheap, and it keeps
        // the caller's paging contract identical.
        int localSkip = following ? Math.Max(0, skip) : 0;
        int windowCount = following ? maxCount + localSkip : maxCount;
        string skipArg = skip > 0 && !following ? $" --skip={skip}" : string.Empty;

        string countArgs = $"--max-count={windowCount}{skipArg}{orderArg}";

        // Order matters: options first (paging, then the filter criteria), then the
        // revisions to walk. The path filter is NOT part of this string — it must go
        // after the "--" separator, which the core's RevisionReader appends itself
        // (RevisionReader.BuildArguments), or git would read the path as a revision.
        List<string> logArgs = [countArgs];
        logArgs.AddRange(criteria.BuildLogArguments());
        if (scopeArgs.Length > 0)
        {
            logArgs.Add(scopeArgs);
        }

        string revisionFilter = string.Join(' ', logArgs);

        reader.GetLog(
            subject: collector,
            revisionFilter: revisionFilter,
            pathFilter: criteria.BuildPathArgument(),
            hasNotes: false,
            autostashLabel: string.Empty,
            cancellationToken: cancellationToken);

        // localSkip is 0 unless renames are being followed, where the page window was
        // widened above instead of using --skip; dropping the head here yields exactly
        // the page the caller asked for, so `rows.Count >= maxCount` below still means
        // "the page came back full".
        List<RevisionRow> rows = new(Math.Max(0, collector.Revisions.Count - localSkip));
        foreach (GitRevision revision in collector.Revisions.Skip(localSkip))
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
        // A filter "Limit" that is now exhausted ends it regardless.
        bool hasMore = maxCount > 0 && rows.Count >= maxCount;
        if (hasMore && criteria.CommitsLimit > 0 && Math.Max(0, skip) + rows.Count >= criteria.CommitsLimit)
        {
            hasMore = false;
        }

        return new RevisionPage(rows, HasMore: hasMore);
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
    ///  Re-links a <c>--follow</c> walk into the chain it actually is: each row's
    ///  graph parent becomes the row BELOW it, and the last row of what is loaded so
    ///  far has none.
    ///
    ///  <para><b>Why it is needed.</b> Every other narrowed walk gets its parent links
    ///  rewritten by git (<c>--parents</c> + history simplification: <c>%P</c> then
    ///  names the nearest SURVIVING ancestor), which is what keeps the DAG connected
    ///  across the commits the filter removed. <c>--follow</c> is the exception —
    ///  measured on this repository, git 2.51:</para>
    ///  <code>
    ///  git log --parents --follow --format=%h^%p -- src/crossplatform/HANDOFF.md
    ///    94525c1d0^4384fbc1c   &lt;- 4384fbc1c is NOT in the result set
    ///  git log --parents --format=%h^%p -- src/crossplatform/HANDOFF.md
    ///    94525c1d0^b292aa32d   &lt;- rewritten to the next row, as everywhere else
    ///  </code>
    ///  <para>So while following renames every row named a parent that is not on
    ///  screen: the lane pass found no lane waiting for any commit, opened a fresh one
    ///  for each row and closed it again one row later. That is the staircase of
    ///  disconnected stubs the file history was drawing — one lane per commit instead
    ///  of one line through them all.</para>
    ///
    ///  <para><b>Why chaining is honest here.</b> <c>--follow</c> produces exactly one
    ///  line of history by construction (git rejects it for several paths, and the
    ///  walk is forced onto a single starting commit and the default order — see
    ///  <see cref="LoadRevisionPage"/>), so "the next row" IS the surviving ancestor
    ///  that rewriting would have named. The REAL parents are untouched: only
    ///  <see cref="RevisionRow.GraphParents"/> is set, so DAG navigation, the parent
    ///  menus and every diff keep using the commit's true parentage.</para>
    /// </summary>
    public static IReadOnlyList<RevisionRow> ChainFollowedHistory(IReadOnlyList<RevisionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        List<RevisionRow> chained = new(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            // The last loaded row's edge is left open: the walk continues below it,
            // and pointing at a commit that is not there is what this fixes.
            chained.Add(rows[i] with
            {
                GraphParents = i + 1 < rows.Count ? [rows[i + 1].Hash] : [],
            });
        }

        return chained;
    }

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
    ///   <item>Colour is tracked SEPARATELY from the column. A lane index is freed
    ///    whenever two branches converge and is then handed to an unrelated branch
    ///    further down; keying the palette on the index therefore painted two
    ///    unrelated lines in the same colour in the same column, which reads as one
    ///    continuous branch. Each lane carries an edge identity instead, allocated
    ///    when the lane is newly occupied and inherited when it merely continues.</item>
    ///  </list>
    /// </summary>
    private static IReadOnlyList<RevisionRow> BuildGraph(List<RevisionRow> input)
    {
        List<string?> lanes = [];

        // Palette identity of each lane slot, kept the same length as `lanes`
        // (-1 = free). See the colour note above.
        List<int> colors = [];
        int nextColor = 0;
        int laneCount = 1;
        List<RevisionRow> result = new(input.Count);

        foreach (RevisionRow row in input)
        {
            string?[] incoming = lanes.ToArray();
            int[] incomingColors = [.. colors];

            // The node lane: reuse the lowest lane already waiting for this commit
            // (inheriting its colour), otherwise take the lowest free lane — a branch
            // tip with no descendant, which starts a new colour.
            int nodeLane = IndexOf(lanes, row.Hash);
            int nodeColor;
            if (nodeLane < 0)
            {
                nodeLane = FirstFree(lanes);
                nodeColor = nextColor++;
            }
            else
            {
                nodeColor = colors[nodeLane];
            }

            // Every lane that was waiting for this commit ends here (children merge in).
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] == row.Hash)
                {
                    lanes[i] = null;
                    colors[i] = -1;
                }
            }

            // The node's edges to its parents all emanate from the node lane.
            HashSet<int> nodeOrigin = [];

            // Merge edges into a lane that was ALREADY carrying that parent before this
            // row: (targetLane, colour). They are extra diagonals, not replacements —
            // see the comment where they are emitted.
            List<(int Lane, int Colour)> joinEdges = [];
            IReadOnlyList<string> parents = row.GraphParents ?? row.ParentHashes;
            if (parents.Count > 0)
            {
                // First parent continues straight down the node lane, same colour.
                SetLane(nodeLane, parents[0], nodeColor);
                nodeOrigin.Add(nodeLane);

                // Extra parents (merge) branch off into reused or fresh lanes.
                for (int p = 1; p < parents.Count; p++)
                {
                    int existing = IndexOf(lanes, parents[p]);
                    if (existing >= 0)
                    {
                        // Somebody else's lane is already flowing down to this parent, so
                        // it must KEEP flowing straight through the row: claiming it as a
                        // node origin re-sourced its lower half from the node and left the
                        // upper half a dead end — the lane looked broken in two, and the
                        // fragment below took the node's colour. Record the merge edge
                        // instead and draw it as an extra diagonal into that lane.
                        if (existing != nodeLane)
                        {
                            joinEdges.Add((existing, colors[existing]));
                        }

                        continue;
                    }

                    int pl = FirstFree(lanes);
                    SetLane(pl, parents[p], nextColor++);
                    nodeOrigin.Add(pl);
                }
            }
            else
            {
                // Root commit: nothing continues below the node.
                SetLane(nodeLane, null, -1);
            }

            // Drop trailing free lanes so the graph stays as narrow as possible.
            while (lanes.Count > 0 && lanes[^1] is null)
            {
                lanes.RemoveAt(lanes.Count - 1);
                colors.RemoveAt(colors.Count - 1);
            }

            string?[] outgoing = lanes.ToArray();
            int[] outgoingColors = [.. colors];

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
                segments.Add(new RevisionGraphSegment(i, 0.0, target, 0.5, Colour(incomingColors, i)));
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
                segments.Add(new RevisionGraphSegment(source, 0.5, i, 1.0, Colour(outgoingColors, i)));
            }

            // Merge edges into an already-flowing lane, drawn IN ADDITION to that lane's
            // straight bottom half: the branch keeps its unbroken line and the merge joins
            // it at the row's bottom edge, in the colour of the branch being merged.
            foreach ((int lane, int colour) in joinEdges)
            {
                segments.Add(new RevisionGraphSegment(nodeLane, 0.5, lane, 1.0, colour >= 0 ? colour : lane));
            }

            laneCount = Math.Max(laneCount, Math.Max(nodeLane + 1, Math.Max(incoming.Length, outgoing.Length)));

            result.Add(row with { NodeLane = nodeLane, NodeColor = nodeColor, GraphSegments = segments });
        }

        // Writes a lane slot, growing both parallel lists as needed.
        void SetLane(int index, string? value, int color)
        {
            while (lanes.Count <= index)
            {
                lanes.Add(null);
                colors.Add(-1);
            }

            lanes[index] = value;
            colors[index] = value is null ? -1 : color;
        }

        // Defensive: an occupied lane always has an identity, but never let a stale
        // -1 reach the palette — fall back to the column, the old behaviour.
        static int Colour(int[] source, int index)
            => index < source.Length && source[index] >= 0 ? source[index] : index;

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
