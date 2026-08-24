using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A commit-list view (revision grid) for the Avalonia/Linux port. Loads the
///  recent history of a repository off the UI thread and renders it as a
///  multi-column list in the original's column order (DAG graph / Subject with
///  its ref labels / Avatar / Author / Date / Commit ID). Uses a
///  <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) or theme
///  registration is required.
///
///  <para>The left-most column draws the commit DAG (colored lane lines + a
///  node dot per row, with branch/merge edges between adjacent rows), using the
///  lane layout computed by <see cref="RevisionService"/>.</para>
/// </summary>
public sealed class RevisionGridView : UserControl
{
    // Shared column widths so the header and every row line up. The three the user can
    // drag start at these values and are remembered from then on (see ColumnWidths).
    //
    // The three draggable ones start at the original's own defaults: Author 130
    // (AuthorNameColumnProvider), Date 130 (DateColumnProvider, the relative-date
    // case), Commit ID 60 (CommitIdColumnProvider). The port started Author at 170
    // and the hash at 90, which took ~70px away from the subject — the column the
    // original lets fill the grid. The hash gets 64 rather than 60: the original
    // shortens the id to whatever the width holds, this port always prints the
    // 8-character short hash, which needs those extra pixels not to be trimmed.
    private const double HashWidth = 64;
    private const double AvatarWidth = 28;
    private const double AuthorWidth = 130;
    private const double DateWidth = 130;

    // A dragged column may not disappear, and the subject may not be squeezed out of
    // existence by the columns to its right: the subject is the one that absorbs every
    // drag, so without a floor a determined pull would leave the messages unreadable.
    private const double MinColumnWidth = 40;
    private const double MinSubjectWidth = 120;

    // The grab strip on a column's left edge. 6px is what the eye can hit without the
    // handle stealing clicks from the header label next to it.
    private const double ResizeHandleWidth = 6;

    /// <summary>Gap between a column's divider and the text in that column.</summary>
    private const double ColumnTextInset = 6;

    /// <summary>Marks the grids whose columns are THE columns (header and rows).</summary>
    private const string ColumnGridTag = "revision-grid-columns";

    // Size of the identicon square drawn inside the avatar cell (centred).
    private const double AvatarSize = 18;

    // Graph rendering metrics. 16 is the original's GraphRenderer.LaneWidth at 100%
    // scaling; the port used 14, which packed the lanes tighter than the node dots
    // they carry and made a busy graph look like a single ribbon.
    private const double LaneWidth = 16;

    // Upper bound on the width of the graph column, in lanes. A sparse walk — most
    // visibly one produced by a revision filter, where each surviving commit tends
    // to sit in a lane of its own — can reach dozens of lanes and would otherwise
    // squeeze Author/Date/Subject off the pane. Lanes past this cap are simply
    // clipped (every graph control has ClipToBounds set), which degrades the DAG
    // cleanly instead of destroying the row.
    //
    // The value is the original's: RevisionGraph.MaxLanes = 40 (Graph/RevisionGraph.cs:20).
    // The port used to cap at 8, above which the DAG simply stopped being drawn —
    // any repository with more than eight concurrent branches lost its graph.
    private const int MaxGraphLanes = 40;

    // Row metrics — kept tight for a dense, GitExtensions-like log.
    private const double RowFontSize = 12;

    private readonly RevisionService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ContentControl _headerHost;
    private readonly TextBox _search;

    // "Go to ▾" bar button (holds the navigation flyout) and its hash entry box,
    // kept as fields so a keyboard shortcut (Ctrl+G) can open + focus them.
    // The box is re-created together with the flyout on a language switch (a
    // control cannot be moved between two visual trees), so it is not readonly.
    private readonly Button _goToButton;
    private TextBox _goToBox;

    // The other compact bar buttons, kept so Relabel() can re-caption them and
    // rebuild their flyouts (which carry translated labels) in place.
    private readonly Button _dateButton;
    private readonly Button _columnsButton;
    private readonly Button _branchesButton;
    private readonly Button _viewButton;

    // --- Real (git) revision filter -------------------------------------------
    //
    // The criteria edited in RevisionFilterDialog. They are handed to
    // RevisionService and become `git log` arguments, so the filter applies to the
    // WHOLE history and paging keeps working on the filtered walk — as opposed to
    // the quick box below, which only sifts the rows already loaded.
    private RevisionFilter _gitFilter = RevisionFilter.None;

    // --- The quick filter box: which field it searches, and what it submitted ----
    //
    // The box is upstream's FilterToolBar "Filter:" combo: typing narrows nothing by
    // itself, ENTER hands the text to git (FilterToolBar.cs:386-434), and a "Filter
    // type" dropdown says which field the text applies to
    // (FilterToolBar.Designer.cs:48-70). This port keeps the as-you-type sieve over
    // the ALREADY LOADED rows as a free preview, and promotes it to a real git filter
    // on Enter — the only way a term living in a commit that has not been paged in
    // yet can ever be found.
    private enum QuickFilterField
    {
        Message,
        Committer,
        Author,
        DiffContent,
    }

    private QuickFilterField _quickFilterField = QuickFilterField.Message;

    // The text currently APPLIED TO GIT through the quick box (empty when none).
    // While the box still shows exactly this text there is nothing left to sift in
    // memory — git already returned the matching set — so ApplyFilterCore treats it
    // as no quick filter at all, which also keeps the graph column and the
    // artificial rows visible. Typing further re-enables the in-memory preview on
    // top of the git result.
    private string _submittedQuickText = string.Empty;

    // The last searches submitted from the quick box, newest first, capped at 30 —
    // upstream's AppSettings.RevisionFilterDropdowns (FilterToolBar.cs:394-399).
    private readonly List<string> _filterMru = [];
    private const int MaxFilterMru = 30;

    // The dropdown listing the MRU, and the button opening it. Rebuilt when the MRU
    // changes — never from an Opening handler.
    private readonly Button _mruButton;
    private readonly Button _filterTypeButton;

    // The -S/-G sub-choice inside the "Filter type" flyout. Held because it is shown
    // only while the diff field is the armed one, and the flyout's content is built
    // once (rebuilding it while it is open would pull the tree out from under the
    // pointer — see OptionsChanged).
    private StackPanel? _quickFilterDiffMode;

    // Opens the filter dialog; captioned with a funnel so an active filter is
    // visible even before reading the status line.
    private readonly Button _filterButton;

    // Reset affordance, shown only while a git filter is active.
    private readonly Button _resetFilterButton;

    // --- Type-to-search (quick-search) -------------------------------------
    //
    // Distinct from the text FILTER above (_search), which HIDES non-matching
    // rows. Quick-search never touches the row set: while the grid/list itself
    // has focus, printable keystrokes accumulate into _quickSearch and the
    // selection JUMPS to the next row whose subject/author contains the buffer,
    // leaving every other row visible. F3/Enter jump to the next match,
    // Shift+F3 to the previous (both wrap); Backspace edits the buffer; Esc
    // clears it; and a short idle timeout auto-dismisses it. Because it lives on
    // _list's own key/text handlers (not _search's), it never competes with the
    // filter box for typed characters.
    private readonly Border _quickSearchOverlay;

    // The spinner drawn over the list while a restart of the walk is in flight. Its own
    // delay keeps it off the screen for the reloads that finish in a blink, which is
    // most of them (see Views/BusyOverlay).
    private readonly BusyOverlay _busy = new();
    private readonly TextBlock _quickSearchLabel;
    private readonly DispatcherTimer _quickSearchTimer;
    private string _quickSearch = string.Empty;

    // --- Artificial rows ("Working directory" / "Commit index") ---------------
    //
    // Like the original Windows grid, the pending work is shown as the FIRST TWO
    // NODES OF THE DAG: they are real items of the same ListBox (same columns,
    // same selection model), carried as RevisionRow records with the sentinel
    // hashes below (mirroring the core's ObjectId.WorkTreeId / IndexId), and the
    // graph column draws them in HEAD's lane with a distinct hollow-square node
    // and a continuous lane line down to the HEAD commit.
    //
    // They are synthesised by the view (never by the git walk), appear only when
    // there is something to show (dirty working dir / non-empty index), and are
    // hidden while a text filter is active (the graph column is collapsed then).
    //
    // PUBLIC because they are also the identity the host needs when it routes the
    // bottom tabs for an artificial selection (see ArtificialRevisionSelected):
    // they are byte-for-byte the core's ObjectId.WorkTreeId / ObjectId.IndexId, so
    // a tab that understands those ids can be handed this hash unchanged.
    // They were SWAPPED with respect to the core until M64: the core has
    // ObjectId.WorkTreeId = 1111…, ObjectId.IndexId = 2222…
    // (GitExtensions.Extensibility/Git/ObjectId.cs:33,38), so a tab that mapped the
    // hash through the core's ids showed the staged diff for the working-directory
    // row. Both are only compared symbolically here, so the values can be aligned.
    public const string WorkTreeHash = "1111111111111111111111111111111111111111";
    public const string IndexHash = "2222222222222222222222222222222222222222";

    // Pending-work counts, pushed in by MainWindow via SetWorkingState; this view
    // never queries git for them itself.
    private int _unstaged;
    private int _staged;

    // How many artificial rows are currently at the head of the displayed set (0-2).
    // Their lanes and segments come from the DAG layout like any other row.
    private int _artificialCount;

    // --- Incremental history loading ------------------------------------------
    //
    // The grid never asks git for the whole history at once: it walks the log one
    // PAGE at a time (RevisionService.LoadRevisionPage), so opening a repository
    // with tens of thousands of commits costs one bounded `git log` and the rest of
    // the history is appended on demand — when the user scrolls to the end of the
    // list, or presses the "load more" button in the footer. This replaces the old
    // hard-wired 200-commit ceiling, which silently truncated the history AND the
    // text filter (which only ever sees loaded rows).
    //
    // _pageSize is user-configurable from the "View" menu, mirroring the original's
    // AppSettings.MaxRevisionGraphCommits.
    private const int DefaultPageSize = 500;

    private int _pageSize = DefaultPageSize;

    // True while the last page came back full, i.e. the walk very likely continues.
    private bool _hasMore;

    // Set while a page request is in flight, so scrolling cannot pile up requests.
    private bool _loadingPage;

    // Bumped by every Reload(): a page that completes after a reload (scope change,
    // language switch, another repository) belongs to a dead walk and is discarded.
    private int _loadGeneration;

    // Cancels the walk of the CURRENT generation. Replaced on every restart, so the
    // pages of one walk share a token and a restart aborts the git process behind
    // the walk it replaces instead of merely ignoring its result (see LoadPage).
    private CancellationTokenSource? _loadCts;

    // The accumulated pages exactly as git returned them, WITHOUT graph geometry.
    // The DAG is rebuilt from this whole list on every append (off the UI thread),
    // which is what keeps lanes, edges and the artificial rows correct as the
    // history grows.
    private IReadOnlyList<RevisionRow> _loaded = [];

    // The list's own ScrollViewer, captured from the first scroll event, so an
    // append can restore the exact scroll offset instead of jumping to the top.
    private ScrollViewer? _scroll;

    // The full, graph-built revision set as loaded from git; filtering selects a
    // subset from this without re-running git or touching the underlying model.
    private IReadOnlyList<RevisionRow> _allRows = [];

    // The rows currently displayed, kept so BuildRow can compute a row's index
    // (for the subtle alternating-row background).
    private IReadOnlyList<RevisionRow> _rows = [];

    // True while the QUICK box (in-memory, over the loaded rows) hides rows. The
    // DAG graph is drawn from segments precomputed against ADJACENT rows in the
    // full list, so showing an arbitrary subset would leave lane lines/edges
    // pointing at hidden neighbours (a garbled graph). While quick-filtering we
    // therefore collapse the graph column to zero width and skip drawing it,
    // restoring it in full when the box is cleared. The underlying model
    // (_allRows) is never mutated.
    //
    // The GIT filter (_gitFilter) is different: git itself rewrites the parent
    // links of the commits it keeps (`--parents`), so the walk it returns is a
    // self-consistent — merely sparser — DAG. Its graph therefore stays drawn.
    private bool _quickFilterActive;

    /// <summary>True while the git-side revision filter narrows the walk.</summary>
    private bool GitFilterActive => _gitFilter.IsActive;

    /// <summary>True while either filter hides commits.</summary>
    private bool AnyFilterActive => _quickFilterActive || GitFilterActive;

    // Path of the loaded repository, for the status line.
    private string _repoLabel = string.Empty;

    // --- "View" options, matching the original Git Extensions revision grid. ---

    // Which timestamp the Date column shows, and whether it is rendered relative
    // ("3 days ago") or absolute ("yyyy-MM-dd HH:mm"). Applied live via RefreshView.
    private enum DateSource { Commit, Author }

    private DateSource _dateSource = DateSource.Commit;
    private bool _relativeDates = true; // default to relative ("2 hours ago"), matching original GitExtensions

    // Which refs the log walks (All branches / current branch only / filtered).
    // Changing it re-runs the log via the existing load path; the choice is carried
    // across sessions through PersistedViewOptions.
    private BranchScope _branchScope = BranchScope.AllBranches;

    // --- "Filtered branches": the explicitly chosen ref set ---------------------
    //
    // Under BranchScope.Filtered the walk is handed exactly these refs (see
    // RevisionService.LoadRevisionPage), so "filtered" finally means what it says
    // instead of quietly walking HEAD. An empty selection still falls back to HEAD,
    // and the flyout says so rather than pretending a filter is in effect.
    //
    // The names are ref names as `git for-each-ref` reports them ("main",
    // "origin/main", "v1.0"), which is what git log accepts as revision arguments.
    private IReadOnlyList<string> _filteredRefs = [];

    // Where the walk STOPS: this commit and everything it reaches are left out (see
    // SetWalkBound). Never persisted and never set from a menu — it belongs to the
    // caller that opened this grid for a purpose, which today is the commit picker.
    private string? _walkBound;

    // Every ref of the repository with its kind ('b' local branch, 'r' remote
    // branch, 't' tag), refreshed alongside the walk by RefreshRefContext. The
    // picker is built from this, never from a git call of its own.
    private IReadOnlyList<(string Name, char Kind)> _refCatalogue = [];

    // The picker's live controls. Held so the check marks can be synced IN PLACE:
    // rebuilding the flyout content while it is open would pull the visual tree out
    // from under the pointer (same rule as OptionsChanged).
    private StackPanel? _refPickerHost;
    private TextBlock? _refPickerSummary;
    private readonly Dictionary<string, CheckBox> _refPickerChecks = new(StringComparer.Ordinal);

    // The picker's own narrowing box and kind toggles — this port's stand-in for
    // upstream's autocompleting branch combo plus its Local/Remote/Tag selector
    // (FilterToolBar.Designer.cs:174-215).
    private string _refPickerQuery = string.Empty;
    private bool _refKindLocal = true;
    private bool _refKindRemote = true;
    private bool _refKindTags = true;

    // Path of the repository last asked to load, so a scope change can re-run the
    // log without the caller re-supplying it (LoadRepository stores it here).
    private string _repoPath = string.Empty;

    // --- File-history mode ------------------------------------------------------
    //
    // Set by LoadFileHistory instead of LoadRepository: the same grid, but showing
    // ONE file's history rather than the repository's. The walk is an ORDINARY
    // path-filtered walk over every name the file has had (see
    // RevisionService.LoadRevisionPage), so it is as free to be reshaped as the
    // repository's: "Branches" and "View" (walk order, remotes/tags/stashes,
    // highlighting) mean exactly what they say here and stay visible. Only two things
    // do not survive the mode: the advanced "Filter…"/reset pair, whose own path field
    // would fight this one, and the "Artificial commits" entry of the View flyout —
    // the working directory and the index are the pending work of the REPOSITORY, not
    // commits in a file's log.
    private bool _fileHistoryMode;

    /// <summary>
    ///  Raised (on the UI thread) after a file-history page has loaded, with commit
    ///  hash → the name the file had in that commit. It is the by-product of resolving
    ///  the historic names into the walk's pathspec, so the host gets it for free
    ///  instead of running a second <c>git log --follow --name-only</c> of its own.
    ///  Empty for a walk that resolved nothing.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, string>>? FileHistoryPathsResolved;

    // The file whose history is shown (repository-relative, as given), for the
    // status line. Empty outside file-history mode.
    private string _fileHistoryFile = string.Empty;

    // Column visibility toggles (the Subject column always stays — upstream has no
    // toggle for it either, it is the Fill column of the grid).
    // Widths of the three columns the user can drag. Loaded from ViewPrefs on
    // construction, written back when a drag ends (see BeginColumnResize).
    private double _authorWidth = AuthorWidth;
    private double _dateWidth = DateWidth;
    private double _hashWidth = HashWidth;

    // The header's own Grid, kept so a drag can move its column definitions live: the
    // ROWS are re-templated only when the drag ends, which is what keeps a drag over a
    // grid of thousands of rows smooth.
    private Grid? _headerGrid;

    private bool _showGraph = true;    // "Show revision graph column"
    private bool _showHash = true;
    private bool _showAvatar = true;   // offline identicon avatar; default ON
    private bool _showAuthor = true;
    private bool _showDate = true;

    // Whether the synthesised "Working directory" / "Commit index" rows are shown at
    // all (upstream's AppSettings.RevisionGraphShowArtificialCommits). Independent of
    // whether there IS pending work: with the toggle off the rows never appear.
    private bool _showArtificial = true;

    // Whether the git-note indicator is drawn on the commits that carry a note
    // (upstream's AppSettings.ShowGitNotes). The note flag itself is always loaded.
    private bool _showGitNotes = true;

    // Walk order: author-date order (--author-date-order), the original's third
    // RevisionSortOrder. Mutually exclusive with _topoOrder below.
    private bool _authorDateSort;

    // "View" toggles from the original grid. The first four change WHICH commits
    // the walk includes (or the walk order) and therefore reload via the existing
    // load path; the last two are render-time styles applied by RefreshView().
    private bool _showRemotes = true;   // include refs/remotes in the walk
    private bool _showTags = true;      // include refs/tags in the walk
    private bool _showStashes;          // include stash commits in the walk
    private bool _topoOrder;            // --topo-order vs default date order
    // Gray out everything that is not a relative of the highlight anchor. ON by
    // default, matching AppSettings.RevisionGraphDrawNonRelativesGray upstream, so
    // the graph already shows the anchor's history in colour on first open.
    private bool _drawNonRelativesGray = true;
    private bool _highlightCurrentBranch; // emphasise the current branch's first-parent line

    // The commit the highlighting is anchored on, as a full hash. Null (or a hash
    // no longer loaded) means HEAD, which is the state the original starts from:
    // RevisionGraph marks the checked-out revision relative and AddParent
    // propagates the flag to its ancestors. ALT+CLICK re-anchors it on the clicked
    // commit, exactly like RevisionGridControl.OnGridViewMouseClick ->
    // HighlightSelectedBranch -> RevisionGraph.HighlightBranch. A plain click
    // never re-anchors, and every refresh falls back to HEAD.
    private string? _highlightAnchor;

    // The author of the currently selected revision, emphasised (bold + full text
    // brush) on every row that shares it — upstream's AuthorRevisionHighlighting.
    // Empty while nothing (or only an artificial row) is selected. Kept in step by
    // UpdateAuthorHighlight, which is the only writer.
    private string _highlightedAuthor = string.Empty;

    // The grid's half of app-settings.json, re-read whenever it is saved (see the
    // SettingsService.Changed subscription in the constructor). Held as a snapshot
    // because every row build consults it.
    private AppPreferences _gridPrefs = new();

    // Whether the authored highlight is worth painting at all (see
    // RecomputeAuthoredHighlight), and the tint it uses, resolved once per theme.
    private bool _authoredHighlightIsUseful;
    private IBrush? _authoredBrush;
    private IBrush? _authoredAltBrush;

    // Set while ANY assignment to _list.ItemsSource is in flight — RebindRows' swap
    // (plus the selection it puts back) and Reload's unbind alike. Every assignment
    // goes through SetListItems, which raises this flag.
    //
    // Two distinct reasons, and both matter:
    //
    //  * COSMETIC. The swap raises SelectionChanged synchronously (empty, then
    //    re-selected), which is NOT a user selection: the very same commits end up
    //    selected. Announcing it would re-raise RevisionSelected/RangeSelected on
    //    every refresh — and, since the author highlight rebinds too, twice per click.
    //
    //  * FATAL. Avalonia's SelectingItemsControl re-points its SelectionModel at the
    //    new source INSIDE the ItemsSource setter and raises SelectionChanged from
    //    within that batch update. Assigning ItemsSource again from such a handler
    //    throws InvalidOperationException("Cannot change source while update is in
    //    progress") and, on the posted call path an external caller uses, takes the
    //    process down. That is exactly what "Filter file in grid" hit: Reload's
    //    unbind was NOT guarded, so its SelectionChanged reached
    //    UpdateAuthorHighlight -> RefreshView -> RebindRows -> ItemsSource. The
    //    guard below plus the re-entrancy check at the top of RebindRows close both
    //    halves of that loop.
    private bool _rebinding;

    // Set while THE WIDGET'S OWN selection update is running — a click, a Ctrl+click,
    // an arrow key — as opposed to _rebinding, which covers the batch this view starts
    // itself. Both are fatal to the same assignment, and only one of them was guarded.
    //
    // Avalonia raises SelectionChanged from inside SelectionModel's batch update
    // whatever opened it, so a handler that assigns ItemsSource throws the same
    // "Cannot change source while update is in progress" whether the batch came from
    // RebindRows or from the pointer. A CTRL+CLICK THAT CLEARED THE LAST SELECTED ROW
    // took the process down for exactly this reason: emptying the selection changes the
    // highlighted author, and a changed author re-templates every row.
    private bool _inSelectionChanged;

    // A rebind asked for while one was already in flight. It cannot run now (see
    // above), so it is coalesced into a single deferred pass at Background priority
    // — after the in-flight assignment, and after the layout it triggers.
    private bool _rebindQueued;
    private bool _rebindQueuedPreserveViewport;

    // Reachability sets computed from the loaded rows whenever _allRows changes,
    // keyed by full hash. _headRelatives = the highlight anchor ∪ its ancestors,
    // which is upstream's RevisionGraphRevision.MakeRelative() semantics (it walks
    // PARENTS only — descendants of the anchor are NOT relative, and the port used
    // to include them, showing too much colour). _currentBranchLine is HEAD's
    // first-parent chain (used by the separate "highlight current branch" style).
    private HashSet<string> _headRelatives = [];
    private HashSet<string> _currentBranchLine = [];

    // Per display row (index into _rows), the "is relative" flags the graph cell
    // needs: the node's own flag plus one flag per segment, in the exact order the
    // segment list comes out of the DAG layout (RevisionService.BuildGraph). Recomputed
    // by ComputeGraphRelatives() on every rebind, since the flag of a segment
    // depends on the row that opened its lane (see that method).
    private List<(bool Node, bool[] Segments)> _graphRelatives = [];

    // Palette pulled from the shared app resources (see App.cs).
    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Width of the graph column when NOT filtering; updated to fit the loaded
    // graph's lane count. While a filter is active the effective width is 0.
    private double _graphWidth = LaneWidth;

    // The column width actually used by the header/rows right now (0 while filtering,
    // or while the graph column is switched off from the View menu).
    private double EffectiveGraphWidth => _quickFilterActive || !_showGraph ? 0 : _graphWidth;

    /// <summary>
    ///  What the grid currently has selected, reduced to the two facts the menu bar
    ///  needs to decide what makes sense: how many rows, and whether every one of
    ///  them is a real commit (the "Working directory" / "Commit index" rows are
    ///  not). Upstream reads the same two facts straight off
    ///  <c>RevisionGrid.GetSelectedRevisions()</c> in
    ///  <c>FormBrowse.CommandsToolStripMenuItem_DropDownOpening</c>
    ///  (FormBrowse.cs:2332-2333); this port has no public revision list on the grid,
    ///  so the summary is exposed instead of the rows.
    ///
    ///  <para>A pull, not a push: it is read when the Commands menu drops down, which
    ///  is exactly when upstream recomputes it, so no event has to fire on every
    ///  arrow-key move.</para>
    /// </summary>
    public (int Count, bool AllNonArtificial) SelectionSummary
    {
        get
        {
            IList<RevisionRow> rows = _list.SelectedItems?.OfType<RevisionRow>().ToList() ?? [];
            return (rows.Count, rows.Count > 0 && rows.All(r => !IsArtificial(r)));
        }
    }

    /// <summary>
    ///  Supplied by the host: whether a bisect session is open in the repository the
    ///  grid is showing. The row menu asks it as it opens, and enables the
    ///  mark-good / mark-bad / skip / stop entries only when it answers true — the
    ///  gate upstream applies at <c>RevisionGridControl.cs:2256-2261</c>. "Start
    ///  bisect" is enabled on the opposite answer.
    ///
    ///  <para>Left unset, the four in-session entries stay disabled and only "Start
    ///  bisect" is offered, so a host that does not wire this up cannot end up with a
    ///  menu that silently begins a bisect.</para>
    /// </summary>
    public Func<bool>? IsBisectInProgress { get; set; }

    /// <summary>
    ///  The full hashes of the currently selected real commits, <b>oldest first</b>
    ///  (the grid itself is newest-first). Artificial work-tree / index rows are left
    ///  out. Used for the bisect dialog's range seeding, which needs the two ends of
    ///  the selection.
    ///
    ///  <para>Ordered by row index rather than by <c>SelectedItems</c> order, which
    ///  reflects the order the user clicked in and would make "oldest" depend on
    ///  which end of the range was clicked first — the same reason the two-row diff
    ///  path resolves older/newer through <c>Items.IndexOf</c>.</para>
    /// </summary>
    public IReadOnlyList<string> SelectedCommitHashes
        => SelectedCommits()
            .OrderByDescending(r => _list.Items.IndexOf(r))
            .Select(r => r.Hash)
            .ToList();

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    /// <summary>
    ///  Raised when two or more real commits are selected (ctrl/shift multi-select).
    ///  Carries the WHOLE selection, newest first — the order upstream's
    ///  <c>FileStatusDiffCalculator</c> works in, where <c>revisions[0]</c> is the
    ///  selected revision and <c>revisions[^1]</c> the oldest.
    ///
    ///  <para>The whole selection and not only its two ends: with two to four rows
    ///  the host does not show one diff but several groups (the merge base of the two
    ///  branches, each side against it), and the middle rows are what decide which of
    ///  those shapes applies. Artificial rows never appear here — the working-tree and
    ///  index rows are not commits and cannot be an end, or a member, of a range.</para>
    /// </summary>
    public event Action<IReadOnlyList<string>>? RangeSelected;

    /// <summary>
    ///  Raised when the selection becomes EMPTY — a Ctrl+click that unpicks the last
    ///  selected row, which is how the original lets go of a commit (its grid is a
    ///  multi-select <c>DataGridView</c>; upstream's Escape only hides the tooltip,
    ///  <c>RevisionGridControl.ProcessHotkey</c>).
    ///
    ///  <para>It exists because "nothing is selected" is a state the host has to be
    ///  told about, not one it can infer: the other three events all carry something,
    ///  so a grid that went empty simply stopped talking and the panes below kept
    ///  describing the commit the user had just released.</para>
    /// </summary>
    public event Action? SelectionCleared;

    /// <summary>Raised when the artificial "Working directory" row is clicked.</summary>
    public event Action? WorkingDirectorySelected;

    /// <summary>Raised when the artificial "Commit index" row is clicked.</summary>
    public event Action? CommitIndexSelected;

    /// <summary>
    ///  Which of the two artificial rows a selection landed on, as reported by
    ///  <see cref="RevisionGridView.ArtificialRevisionSelected"/>.
    /// </summary>
    public enum ArtificialRevision
    {
        /// <summary>The "Working directory" row: unstaged changes (worktree vs index).</summary>
        WorkingDirectory,

        /// <summary>The "Commit index" row: staged changes (index vs HEAD).</summary>
        Index,
    }

    /// <summary>
    ///  Raised whenever the SELECTION lands on one of the two artificial rows, by
    ///  mouse or by keyboard. Like <see cref="RevisionSelected"/> it is NOT raised
    ///  for the cosmetic re-selection a rebind performs. Carries which row it is,
    ///  plus the sentinel hash that identifies it
    ///  (<see cref="WorkTreeHash"/> / <see cref="IndexHash"/>, i.e. the core's
    ///  <c>ObjectId.WorkTreeId</c> / <c>ObjectId.IndexId</c>).
    ///
    ///  <para>WHY IT EXISTS. <see cref="RevisionSelected"/> is deliberately NOT
    ///  raised for these rows (they are not commits, and a tab that ran
    ///  <c>git show &lt;sentinel&gt;</c> would simply fail). Without a signal of
    ///  their own the host had no way to know the selection moved at all, so the
    ///  bottom tabs kept showing the PREVIOUS commit: stale content, which is
    ///  worse than empty. Upstream populates Commit, Diff and File tree for the
    ///  artificial revisions too (<c>CommitInfo.cs:328-343</c>, and the explicit
    ///  comment at <c>FormBrowse.cs:1223</c>).</para>
    ///
    ///  <para>CONTRACT FOR THE HOST. On this event the host owns the tabs and must,
    ///  for each one, either show the artificial content or show it as unavailable —
    ///  never leave the previous commit's content in place:</para>
    ///  <list type="bullet">
    ///   <item><b>Diff</b>: <see cref="ArtificialRevision.WorkingDirectory"/> is
    ///    <c>git diff</c> (worktree vs index), <see cref="ArtificialRevision.Index"/>
    ///    is <c>git diff --cached</c> (index vs HEAD).</item>
    ///   <item><b>File tree</b>: the worktree as it is on disk; the index for the
    ///    staged row.</item>
    ///   <item><b>Commit details / GPG</b>: there IS no commit object, so there is
    ///    nothing to show — the honest rendering is a placeholder naming the row,
    ///    not the previous commit's message.</item>
    ///  </list>
    ///
    ///  <para>The grid raises this on every artificial selection, including repeats,
    ///  because a host that reloaded a tab in between still needs to be told.</para>
    /// </summary>
    public event Action<ArtificialRevision, string>? ArtificialRevisionSelected;

    /// <summary>
    ///  Raised when a commit row is ACTIVATED (double-clicked, or Enter on the row),
    ///  as opposed to merely selected; the argument is the full commit hash. Mirrors
    ///  the original grid, where a double click opens the commit's details. The view
    ///  already selects the row and flashes its identity in the status line; a host
    ///  can subscribe to bring the commit-details tab forward.
    /// </summary>
    public event Action<string>? RevisionActivated;

    /// <summary>
    ///  Raised when one of the artificial rows is ACTIVATED (double-clicked). The
    ///  argument is <see langword="true"/> for the "Commit index" row and
    ///  <see langword="false"/> for the "Working directory" row — in the original a
    ///  double click there opens the commit dialog, which in this port a single click
    ///  already does through <see cref="WorkingDirectorySelected"/> /
    ///  <see cref="CommitIndexSelected"/>. Those two are deliberately NOT re-raised
    ///  here, so a double click cannot open the dialog twice.
    /// </summary>
    public event Action<bool>? ArtificialRowActivated;

    // Host-registered commit-targeted actions (checkout, cherry-pick, reset, …),
    // appended to each row's context menu. Each handler receives the full hash.
    private readonly List<(string Header, Action<string> Handler)> _commitCommands = [];

    // The user scripts marked "add to the revision grid context menu". Kept apart from
    // _commitCommands because that list only grows — the shell registers into it once —
    // whereas this one is REPLACED every time scripts.json is saved.
    private IReadOnlyList<(string Header, Action<string> Handler)> _scriptCommands = [];

    /// <summary>
    ///  Registers an extra context-menu command shown on each commit row; the
    ///  handler is invoked with the row's full commit hash.
    ///
    ///  <para>The structured context menu places the commands it knows by header
    ///  (checkout, resets, compare, bisect, …) in the matching submenu and any other
    ///  registration under "Other actions", so the shell's wiring keeps working
    ///  unchanged. Registering invalidates the built menu, which is rebuilt on the
    ///  next row that needs it.</para>
    /// </summary>
    /// <summary>
    ///  Replaces the user-script entries of the row menu. Called by the shell on start
    ///  and whenever the script list is saved, so a script added in Settings is in the
    ///  menu without a restart.
    /// </summary>
    public void SetScriptCommands(IReadOnlyList<(string Header, Action<string> Handler)> commands)
    {
        _scriptCommands = commands;
        InvalidateRowMenu();
    }

    public void AddCommitCommand(string header, Action<string> handler)
    {
        _commitCommands.Add((header, handler));
        InvalidateRowMenu();
    }

    // Items cannot be added to a live popup (it would not re-measure), so the whole menu
    // is discarded and rebuilt on the next row that needs one.
    private void InvalidateRowMenu()
    {
        if (_rowMenu is not null)
        {
            _rowMenu.Opening -= OnRowMenuOpening;
            _rowMenu = null;
        }
    }

    public RevisionGridView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(10, 6, 10, 6),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            Background = B("App.Toolbar"),
            Padding = new Thickness(0, 2, 0, 2),
            Text = T("No repository loaded."),
            // A deep repository path would otherwise run past the right edge and
            // hide the commit count / scope that follow it. Ellipsize instead, and
            // keep the full line reachable through the tooltip.
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The bar buttons of this view are bars, so they wear the app's bar look.
        // Installed before any of them is built.
        Theming.BarButtonStyles.Apply(Styles);

        // Before the header is built: it is the header that carries the widths.
        LoadColumnWidths();
        _headerHost = new ContentControl { Content = BuildHeader() };

        _search = new TextBox
        {
            Watermark = QuickFilterWatermark,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            // App.BorderStrong, not App.Border: the quick filter is an editable box on
            // the search bar's App.Toolbar fill, which App.Panel only clears by 1.13:1,
            // so the 1px outline is the sole boundary and WCAG 1.4.11 wants 3:1 of it.
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // A small inline "clear" affordance, shown only when the box has text.
        Button clearButton = new()
        {
            Content = "✕",
            Foreground = B("App.TextDim"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            FontSize = 12,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        clearButton.Click += (_, _) =>
        {
            _search.Text = string.Empty;

            // The "✕" means "no filter", so it must also withdraw what was already
            // handed to git — otherwise the box would read empty while the walk
            // stayed narrowed.
            SubmitQuickFilter(string.Empty);
            _search.Focus();
        };
        _search.InnerRightContent = clearButton;

        // Typing sifts the rows ALREADY LOADED — a free preview, no git per
        // keystroke. It is not the filter: the real one runs on Enter, below.
        _search.TextChanged += (_, _) =>
        {
            clearButton.IsVisible = !string.IsNullOrEmpty(_search.Text);
            ApplyFilterCore(_search.Text);
        };

        _search.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                // ENTER hands the text to git, exactly as upstream's filter combo
                // does (FilterToolBar.cs:386-434): only this reaches commits that
                // have not been paged into memory yet.
                SubmitQuickFilter(_search.Text);
                e.Handled = true;
                return;
            }

            // Esc drops both halves — the preview and anything already submitted —
            // and keeps focus in the box.
            if (e.Key == Key.Escape)
            {
                _search.Text = string.Empty;
                SubmitQuickFilter(string.Empty);
                e.Handled = true;
            }
        };

        // Which field the quick box searches, and the list of what was searched
        // before. Both sit immediately left of the box, so "what does Enter do" is
        // readable without opening anything.
        _filterTypeButton = MakeBarButton(Chevron(QuickFilterFieldLabel));
        _filterTypeButton.Margin = new Thickness(0, 0, 6, 0);
        _filterTypeButton.Flyout = BuildQuickFilterTypeFlyout();
        ToolTip.SetTip(_filterTypeButton, T("Which field Enter searches in git"));

        _mruButton = MakeBarButton("⌄");
        _mruButton.Margin = new Thickness(0, 0, 6, 0);
        ToolTip.SetTip(_mruButton, T("Recent searches"));
        RebuildFilterMruFlyout();

        // Compact "View" controls sitting to the right of the filter box: a Date
        // menu (author/commit + relative/absolute) and a Columns menu (show/hide
        // Author, Date, Commit-ID). Both apply live via RefreshView().
        _dateButton = MakeBarButton(Chevron(T("TranslatedStrings/_dateText.Text", "Date")));
        _dateButton.Flyout = BuildDateFlyout();

        _columnsButton = MakeBarButton(Chevron(T("RevisionGrid/ColumnsToolStripMenuItem.Text", "Columns")));
        _columnsButton.Flyout = BuildColumnsFlyout();

        // Compact commit-navigation control: first-parent / child jumps plus a
        // "go to commit" hash box. Also reachable via keyboard (Alt+↑ / Alt+↓ / Ctrl+G).
        _goToBox = MakeGoToBox();
        _goToButton = MakeBarButton(Chevron(T("RevisionGrid/GotoCommit.Text", "Go to")));
        _goToButton.Flyout = BuildGoToFlyout();

        // Branch-scope control: All branches / Current branch only / Filtered.
        // Switching re-runs the log through the existing load path (Reload).
        _branchesButton = MakeBarButton(Chevron(T("RevisionGrid/BranchesToolStripMenuItem.Text", "Branches")));
        _branchesButton.Flyout = BuildBranchesFlyout();

        // "View" control: remote/tag/stash inclusion, walk order, and the two
        // render-time highlight styles. Walk-affecting toggles reload; render-time
        // ones re-template via RefreshView().
        _viewButton = MakeBarButton(Chevron(T("RevisionGridControl/viewToolStripMenuItem.Text", "View")));
        _viewButton.Flyout = BuildViewFlyout();

        // The REAL filter: opens RevisionFilterDialog and re-runs the walk with the
        // criteria translated into `git log` arguments. Distinct from the quick box
        // on its left, which only sifts the rows already loaded.
        _filterButton = MakeBarButton(FilterButtonCaption);
        _filterButton.Click += (_, _) => _ = ShowFilterDialogAsync();

        // One-click "reset all filters", visible only while a git filter is set.
        _resetFilterButton = MakeBarButton("✕");
        _resetFilterButton.IsVisible = false;
        _resetFilterButton.Click += (_, _) => ResetAllFilters();
        ToolTip.SetTip(_resetFilterButton, ResetFilterTip);

        DockPanel bar = new();
        DockPanel.SetDock(_resetFilterButton, Dock.Right);
        DockPanel.SetDock(_filterButton, Dock.Right);
        bar.Children.Add(_resetFilterButton);
        bar.Children.Add(_filterButton);
        DockPanel.SetDock(_dateButton, Dock.Right);
        DockPanel.SetDock(_columnsButton, Dock.Right);
        DockPanel.SetDock(_viewButton, Dock.Right);
        DockPanel.SetDock(_branchesButton, Dock.Right);
        DockPanel.SetDock(_goToButton, Dock.Right);
        bar.Children.Add(_columnsButton);
        bar.Children.Add(_dateButton);
        bar.Children.Add(_viewButton);
        bar.Children.Add(_branchesButton);
        bar.Children.Add(_goToButton);
        DockPanel.SetDock(_filterTypeButton, Dock.Left);
        DockPanel.SetDock(_mruButton, Dock.Left);
        bar.Children.Add(_filterTypeButton);
        bar.Children.Add(_mruButton);
        bar.Children.Add(_search); // fills the remaining space

        Border searchBar = new()
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6, 10, 6),
            Child = bar,
        };

        _list = new ListBox
        {
            Background = B("App.Window"),
            Foreground = B("App.Text"),
            FontSize = RowFontSize,
            BorderThickness = new Thickness(0),
            ClipToBounds = true,
            // Multiple allows ctrl/shift extend while a plain click still replaces the
            // selection with a single row (so single-select behaviour is preserved).
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<RevisionRow?>((row, _) => BuildRow(row), supportsRecycling: true),
        };

        // Dense rows, transparent containers, and an App.Selection highlight for
        // the selected/hovered row (styling the Fluent ListBoxItem template).
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            },
        });
        // Row backgrounds (alternating stripe, hover wash and — above all — the solid
        // full-width selection fill) are NOT painted by the ListBoxItem template any
        // more: the row content itself is opaque (alternating App.Panel/App.PanelAlt),
        // so anything drawn behind it would be invisible. Instead every row root is a
        // RevisionRowView, which watches its ListBoxItem and repaints itself plus its
        // text / ref pills / DAG lanes for the selected state (see BuildRow).
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()
            .Template().OfType<ContentPresenter>())
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
                new Setter(ContentPresenter.BorderThicknessProperty, new Thickness(0)),
            },
        });

        _list.SelectionChanged += (_, _) =>
        {
            if (_rebinding)
            {
                // A rebind in progress: the selection is being put back, not changed.
                return;
            }

            // Everything below runs INSIDE the widget's selection batch, where an
            // ItemsSource assignment is fatal (see _inSelectionChanged). Anything that
            // wants to re-template rows from here is deferred by RebindRows instead.
            _inSelectionChanged = true;
            try
            {
                OnSelectionChanged();
            }
            finally
            {
                _inSelectionChanged = false;
            }
        };

        void OnSelectionChanged()
        {
            // The author of the selected revision is emphasised on every row that
            // shares it; a change of author re-templates the rows (see D7).
            UpdateAuthorHighlight();

            // An artificial row (working directory / commit index) is not a commit:
            // it never fires RevisionSelected and never takes part in a range diff.
            // WorkingDirectorySelected / CommitIndexSelected stay bound to an explicit
            // click on the row (see BuildArtificialRow), so merely arrowing past it
            // does not open the commit dialog — but the SELECTION did move, and the
            // bottom tabs must not keep showing the commit that was selected before.
            // That is what ArtificialRevisionSelected is for; see its contract.
            if (_list.SelectedItems is { Count: 1 } one && one[0] is RevisionRow art && IsArtificial(art))
            {
                ArtificialRevisionSelected?.Invoke(
                    art.Hash == IndexHash ? ArtificialRevision.Index : ArtificialRevision.WorkingDirectory,
                    art.Hash);
                return;
            }

            // Two rows or more selected => diff the range they span. The grid is
            // newest-first, so the row with the HIGHEST index in Items is the older
            // end (= baseHash) and the lowest is the newer end (= otherHash).
            //
            // Ctrl-picking two commits and Shift-picking a run of them therefore
            // answer the same way, which is the point: a range of five commits used
            // to fall through to the single-commit branch below and show the diff of
            // whichever row happened to be SelectedItem — the selection said "compare
            // these" and the pane showed one commit.
            if (_list.SelectedItems is { Count: >= 2 } sel && SelectedRevisionsNewestFirst(sel) is { Count: >= 2 } picked)
            {
                // One Ctrl-click raises SelectionChanged TWICE (the removal and the
                // addition are reported separately), and every announcement costs the
                // host several git diffs now, not one. Announcing a selection only when
                // it actually changed keeps one gesture to one comparison.
                if (_announcedRange is null || !_announcedRange.SequenceEqual(picked))
                {
                    _announcedRange = picked;
                    RangeSelected?.Invoke(picked);
                }
            }
            else if (_list.SelectedItem is RevisionRow row && !IsArtificial(row))
            {
                _announcedRange = null;
                AnnounceRevision(row.Hash);
            }
            else
            {
                _announcedRange = null;

                // Nothing selected at all — a Ctrl+click that unpicked the last row.
                // The panes below still hold the commit that WAS selected, so the host
                // is told to empty them; without this the grid says "nothing" and the
                // diff, detail, tree and GPG tabs go on describing a commit the user
                // has just let go of.
                if (_list.SelectedItems is null or { Count: 0 })
                {
                    _announcedHash = null;
                    SelectionCleared?.Invoke();
                }
            }
        }

        // Keyboard shortcuts of the grid (see OnListKeyDown). Registered
        // TUNNELLING, and with handledEventsToo: the ListBox's own class handler runs
        // on the bubble stage and swallows the arrow keys for its selection movement —
        // which is why an Alt+arrow shortcut registered on the bubble stage would never
        // be seen (Alt+↑/↓ silently behaved as plain ↑/↓). Tunnelling gives this view
        // first refusal; anything it does not claim falls through to the ListBox
        // unchanged, so plain arrow navigation keeps working.
        _list.AddHandler(
            InputElement.KeyDownEvent,
            OnListKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Transient quick-search adorner: a small pill floating at the bottom-left
        // of the list, shown only while a quick-search is in progress.
        _quickSearchLabel = new TextBlock
        {
            Foreground = B("App.Text"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _quickSearchOverlay = new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Accent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(10, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            IsVisible = false,
            Child = _quickSearchLabel,
        };

        // Idle timeout: a pause in typing dismisses the quick-search buffer, so a
        // later keystroke starts fresh (matching the original grid's behaviour).
        // Upstream's RevisionGridQuickSearchTimeout (4 s by default); the port had 3.
        _quickSearchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(new SettingsService().Load().RevisionGridQuickSearchTimeout),
        };
        _quickSearchTimer.Tick += (_, _) => EndQuickSearch();

        // Printable characters typed while the list is focused feed the buffer.
        // Using the TextInput event (rather than KeyDown) gives the actual typed
        // character with keyboard layout / shift applied, and only fires for real
        // text input — never for Enter/Backspace/F3, which KeyDown handles below.
        _list.AddHandler(InputElement.TextInputEvent, OnListTextInput, RoutingStrategies.Bubble);

        // ALT+CLICK re-anchors the graph highlighting on the clicked commit, the way
        // the original does it (OnGridViewMouseClick checks ModifierKeys for Alt and
        // calls HighlightSelectedBranch). Registered TUNNELLING so the ListBoxItem's
        // own pointer handling cannot swallow it first, and NOT marked handled: the
        // click keeps doing everything it normally does (selection, focus), only the
        // highlight anchor moves. A plain click never touches the anchor.
        _list.AddHandler(
            InputElement.PointerPressedEvent,
            OnListPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Double click ACTIVATES the row under the pointer (commit details / commit
        // dialog for the artificial rows), as in the original grid.
        _list.AddHandler(InputElement.DoubleTappedEvent, OnListDoubleTapped, RoutingStrategies.Bubble);

        // A right click has to be seen BEFORE the row's own ContextMenu opens the
        // shared popup, so the menu's Opening handler knows which row it is for:
        // hence TUNNELLING on the list, which runs ahead of the bubbling handler
        // Control.ContextMenu installs on the row.
        _list.AddHandler(
            Control.ContextRequestedEvent, OnListContextRequested, RoutingStrategies.Tunnel);

        // Reaching the end of the list appends the next page of history. The event
        // bubbles from the list's own ScrollViewer, which is also captured here so an
        // append can restore the scroll offset afterwards.
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, OnListScrolled, RoutingStrategies.Bubble);

        Panel listHost = new();
        listHost.Children.Add(_list);
        listHost.Children.Add(_quickSearchOverlay);

        // Over the LIST only, not the whole view: the search box, the scope buttons and
        // the status line stay legible and usable while the walk runs — covering them
        // would make a reload feel like a modal wait, which it is not.
        listHost.Children.Add(_busy);

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(searchBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_headerHost, Dock.Top);
        root.Children.Add(searchBar);
        root.Children.Add(_status);
        root.Children.Add(_headerHost);
        root.Children.Add(listHost);

        Content = root;

        // A language switch re-labels this view in place — no restart, and no
        // loss of filter / scope / selection (see Relabel).
        TranslationService.LanguageChanged += OnLanguageChanged;

        // The row height is a per-STYLE number (RowMinHeight) and this grid draws its
        // own rows, so no Style can reach it: the rows have to be rebuilt. Viewport
        // preserved, because the user did not ask for a different SET of rows — they
        // changed the appearance, and losing the scroll position over that would be the
        // "never acceptable" case documented on RebindRows. Subscribed here and not
        // unsubscribed, like LanguageChanged above: both instances of this view
        // (MainWindow, FileHistoryView) are readonly fields that live as long as the
        // app, so there is nothing to leak.
        ThemeManager.StyleChanged += OnStyleChanged;

        // The Settings dialog writes app-settings.json off the UI thread, so the reload
        // is posted. Same lifetime argument as the two subscriptions above: both
        // instances of this view live as long as the app.
        SettingsService.Changed += OnAppSettingsChanged;
        ReadGridPreferences();
    }

    // Re-reads the settings this grid draws from and rebuilds the rows in place. The
    // viewport is preserved: the user changed an appearance, not the set of rows.
    private void OnAppSettingsChanged() => Dispatcher.UIThread.Post(() =>
    {
        bool colouredPerBranch = ColourPerBranch;
        bool colouredAtMirror = ColourAtRemoteMirror;
        ReadGridPreferences();

        // Per-branch colouring is decided by the lane pass, not by the row template, so
        // these are the settings here a rebind cannot show.
        if (ColourPerBranch != colouredPerBranch || ColourAtRemoteMirror != colouredAtMirror)
        {
            RefreshGraph();
            return;
        }

        RefreshView();
    });

    private void ReadGridPreferences()
    {
        _gridPrefs = new SettingsService().Load();
        _authoredBrush = null;
        _authoredAltBrush = null;
        RecomputeAuthoredHighlight();
        _quickSearchTimer.Interval = TimeSpan.FromMilliseconds(_gridPrefs.RevisionGridQuickSearchTimeout);

        // Pushed onto the graph control rather than read by it: the lane palette is
        // static, and a per-cell settings read would cost one file check per row.
        RevisionGraphControl.Multicolor = _gridPrefs.MulticolorBranches;
    }

    /// <summary>
    ///  Whether the graph starts a new colour at every branch tip. Inert while the
    ///  palette is off: with one colour there is nothing to divide, and asking the
    ///  builder to allocate identities nobody can see would only make the graph
    ///  rebuild differently for no visible reason.
    /// </summary>
    private bool ColourPerBranch => _gridPrefs.MulticolorBranches && _gridPrefs.GraphColorPerBranch;

    /// <summary>
    ///  Whether the split also happens at <c>origin/X</c> when the local <c>X</c>
    ///  exists. Only meaningful while the split happens at all.
    /// </summary>
    private bool ColourAtRemoteMirror => ColourPerBranch && _gridPrefs.GraphColorAtRemoteMirror;

    /// <summary>
    ///  The height of a revision row, per app style: <see cref="Metrics.Density.RowHeight"/>
    ///  (22) in modern, the original 20 in classic.
    ///
    ///  <para>20 leaves 12px of text with 4px above and below; 22 gives it 5, which is
    ///  the difference between rows that scan and rows that touch, and costs ~4 rows on
    ///  a 900px window. Classic keeps 20 because that is upstream's grid.</para>
    /// </summary>
    private static double RowMinHeight
        => ThemeManager.CurrentStyle == AppStyle.Modern ? Metrics.Density.RowHeight : 20;

    // Keyboard handling for the grid, on the TUNNEL stage (see the registration in
    // the constructor). Plain ↑/↓ are deliberately left to the ListBox.
    //
    // The bindings are the original's, from HotkeySettingsManager.cs:272-319 (scope
    // RevisionGridControl). Three of them used to differ, and each divergence was a
    // real defect:
    //
    //  * Alt+↑ / Alt+↓ are QUICK-SEARCH previous / next upstream, not parent / child.
    //    Parent and child are Ctrl+P / Ctrl+N (plus Ctrl+← for the first parent).
    //  * "Go to commit" is Ctrl+Shift+G. The port had it on Ctrl+G, which upstream
    //    reserves for GitBash — so with the focus in the grid Ctrl+G no longer opened
    //    the terminal. Ctrl+G is now left alone and falls through to the shell.
    //  * F3 is not a grid key at all: in the FormBrowse scope it is OpenWithDifftool.
    //    Quick-search stepping is on Alt+↑/↓ and Enter, so F3 is released here.
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool quickActive = _quickSearch.Length > 0;

        // The gestures come from the RevisionGrid scope, not from this method: they used
        // to be compared inline here, which made them upstream-faithful but impossible to
        // reconfigure. Same defaults, now the user's to change.
        switch (HotkeyService.Shared.Command(HotkeyScope.RevisionGrid, e))
        {
            case "SelectCurrentRevision":
                SelectCurrentRevision();
                e.Handled = true;
                return;

            // GoToParent and GoToFirstParent are one jump in this port: the parent
            // navigation always takes ParentHashes[0]. Both names are offered because
            // both exist upstream and a user may have bound either.
            case "GoToParent":
            case "GoToFirstParent":
                GoToParent();
                e.Handled = true;
                return;

            case "GoToChild":
                GoToChild();
                e.Handled = true;
                return;

            case "GoToCommit":
                OpenGoToCommit();
                e.Handled = true;
                return;

            case "GoToMergeBase":
                GoToMergeBase();
                e.Handled = true;
                return;

            case "ToggleBetweenArtificialAndHeadCommits":
                ToggleBetweenArtificialAndHeadCommits();
                e.Handled = true;
                return;

            case "RevisionFilter":
                _ = ShowFilterDialogAsync();
                e.Handled = true;
                return;

            case "ResetRevisionFilter":
                ResetAllFilters();
                e.Handled = true;
                return;

            case "ToggleHighlightSelectedBranch":
                ToggleHighlightSelectedBranch();
                e.Handled = true;
                return;

            case "ShowAllBranches":
                ShowAllBranches();
                e.Handled = true;
                return;

            case "ShowCurrentBranchOnly":
                ShowCurrentBranchOnly();
                e.Handled = true;
                return;

            case "ShowFilteredBranches":
                ShowFilteredBranches();
                e.Handled = true;
                return;

            case "ShowRemoteBranches":
                ToggleShowRemoteBranches();
                e.Handled = true;
                return;

            case "ToggleShowTags":
                ToggleShowTags();
                e.Handled = true;
                return;

            case "PrevQuickSearch":
                QuickSearchPrevious();
                e.Handled = true;
                return;

            case "NextQuickSearch":
                QuickSearchNext();
                e.Handled = true;
                return;

            case "NavigateBackward":
                NavigateBack();
                e.Handled = true;
                return;

            case "NavigateForward":
                NavigateForward();
                e.Handled = true;
                return;
        }

        // Below: keys that are NOT hotkeys and must not become configurable — copying
        // the selection, and the quick-search buffer's own editing keys. Rebinding
        // Backspace or Esc inside a type-to-search would only be a way to break it.
        if (ctrl && !shift && !alt && e.Key == Key.C && _list.SelectedItem is RevisionRow row)
        {
            Copy(row.Hash);
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.V)
        {
            // The quick-search buffer accepts a paste (QuickSearchProvider.cs:67-72).
            _ = PasteIntoQuickSearchAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !quickActive && !ctrl && !alt
            && _list.SelectedItem is RevisionRow activated)
        {
            // Enter activates the focused row, like a double click.
            Activate(activated);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && quickActive && !ctrl && !alt)
        {
            // Enter also advances to the next match while quick-searching.
            QuickSearchStep(forward: !shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && quickActive)
        {
            // Backspace edits the buffer; emptying it dismisses the adorner.
            _quickSearch = _quickSearch[..^1];
            if (_quickSearch.Length == 0)
            {
                EndQuickSearch();
            }
            else
            {
                QuickSearchApply(fromCurrentInclusive: true);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && quickActive)
        {
            // Esc clears/dismisses the quick-search.
            EndQuickSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !ctrl && !shift && !alt
            && _list.SelectedItems is { Count: > 0 })
        {
            // Esc lets go of the selection — asked for explicitly, and NOT what the
            // original does: upstream's Escape on this grid only hides the tooltip
            // (RevisionGridControl.ProcessHotkey:914), and a commit is released there
            // with Ctrl+click on the selected row. That gesture works here too; this is
            // a second way to reach the same state, which the port has because the
            // state itself is now something the grid can announce (SelectionCleared,
            // M169).
            //
            // AFTER the quick-search branch above, never before it: while a
            // type-to-search is running Esc belongs to the search, exactly as it does
            // upstream, and unpicking the row the search just found would undo the
            // search's own result.
            ClearSelection();
            e.Handled = true;
        }
    }

    /// <summary>
    ///  Drops the selection, leaving no row picked — the state the grid is born in.
    /// </summary>
    /// <remarks>
    ///  Assigning <c>SelectedIndex = -1</c> rather than emptying <c>SelectedItems</c>:
    ///  the list is <see cref="SelectionMode.Multiple"/>, so clearing the collection
    ///  raises one change per removed row, and each one costs the host the announcement
    ///  work of a selection that is on its way out anyway. The single assignment empties
    ///  the model in one batch — from which <c>SelectionChanged</c> is raised inside the
    ///  <c>SelectionModel</c> update, which is why the re-template it triggers has to be
    ///  deferred (see <c>_inSelectionChanged</c>).
    /// </remarks>
    public void ClearSelection() => _list.SelectedIndex = -1;

    // ---- translation ---------------------------------------------------------

    // "<caption>   (Ctrl+P)", with the gesture as currently bound — and just the
    // caption when the user has cleared it, rather than an empty pair of brackets.
    private static string Gesture(string caption, string command)
        => HotkeyService.Shared.Display(HotkeyScope.RevisionGrid, command) is { } shown
            ? $"{caption}   ({shown})"
            : caption;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // Appends the drop-down chevron to a bar-button caption without making the
    // glyph part of the translatable string.
    private static string Chevron(string caption) => string.Format("{0} ▾", caption);

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Relabel);

    // Posted, not called inline: ThemeManager raises StyleChanged in the middle of
    // installing the style block, and RebindRows assigns ItemsSource, which runs a
    // layout pass. Let the switch finish first.
    private void OnStyleChanged() => Dispatcher.UIThread.Post(() =>
    {
        // The authored tint is mixed from the palette, so it dies with the old theme.
        _authoredBrush = null;
        _authoredAltBrush = null;
        RebindRows(preserveViewport: true);
    });

    /// <summary>
    ///  Re-captions every piece of chrome this view owns after a language change.
    ///
    ///  <para>The view's STATE is deliberately untouched: the filter text stays in
    ///  the box, the branch scope / date mode / column toggles stay in their fields
    ///  (the rebuilt flyouts are seeded from them), and the selected commits are
    ///  captured by hash and re-selected afterwards. The artificial "Working
    ///  directory" / "Commit index" rows are re-synthesised by the same
    ///  <see cref="ApplyFilterCore"/> path that builds them normally, so they never
    ///  disappear — they just change language with everything else.</para>
    /// </summary>
    private void Relabel()
    {
        _dateButton.Content = Chevron(T("TranslatedStrings/_dateText.Text", "Date"));
        _dateButton.Flyout = BuildDateFlyout();
        _columnsButton.Content = Chevron(T("RevisionGrid/ColumnsToolStripMenuItem.Text", "Columns"));
        _columnsButton.Flyout = BuildColumnsFlyout();
        _branchesButton.Content = Chevron(T("RevisionGrid/BranchesToolStripMenuItem.Text", "Branches"));
        _branchesButton.Flyout = BuildBranchesFlyout();
        _viewButton.Content = Chevron(T("RevisionGridControl/viewToolStripMenuItem.Text", "View"));
        _viewButton.Flyout = BuildViewFlyout();

        // The hash box is re-created inside the flyout (a control cannot have two
        // visual parents), so the field is re-pointed at the fresh one.
        _goToButton.Content = Chevron(T("RevisionGrid/GotoCommit.Text", "Go to"));
        _goToBox = MakeGoToBox();
        _goToButton.Flyout = BuildGoToFlyout();

        _search.Watermark = QuickFilterWatermark;
        _filterTypeButton.Content = Chevron(QuickFilterFieldLabel);
        UpdateFilterChrome();
        ToolTip.SetTip(_resetFilterButton, ResetFilterTip);

        // Rebuilds the artificial rows, the header and the status line, and
        // re-templates every visible row against the new catalogue. The rebind
        // carries the selection, the scroll offset and the keyboard focus across:
        // changing language must not move the list.
        ApplyFilterCore(_search.Text, preserveViewport: true);
    }

    // Re-selects the rows whose hashes were selected before a rebuild. Rows that
    // no longer exist are simply skipped.
    private void RestoreSelection(IReadOnlyList<string> hashes)
    {
        if (hashes.Count == 0 || _list.SelectedItems is not { } selection)
        {
            return;
        }

        HashSet<string> wanted = new(hashes, StringComparer.Ordinal);
        selection.Clear();
        foreach (RevisionRow row in _rows)
        {
            if (wanted.Contains(row.Hash))
            {
                selection.Add(row);
            }
        }
    }

    /// <summary>
    ///  True for the two synthesised rows ("Working directory" / "Commit index"),
    ///  identified by their sentinel hashes. They are list items like any other
    ///  row but are not commits: no range diff, no parent/child navigation, no
    ///  commit context menu.
    /// </summary>
    private static bool IsArtificial(RevisionRow? row)
        => row?.Hash is WorkTreeHash or IndexHash;

    /// <summary>
    ///  Feeds the artificial DAG rows their pending-work counts. A row exists only
    ///  while its count is &gt; 0 (dirty working directory / non-empty index), so a
    ///  change in the counts rebuilds the displayed set. Fed by MainWindow (which
    ///  already computes these); this view never queries git for them itself.
    /// </summary>
    public void SetWorkingState(int unstaged, int staged)
    {
        if (_unstaged == unstaged && _staged == staged)
        {
            return;
        }

        _unstaged = unstaged;
        _staged = staged;

        // Rebuild the displayed rows so the artificial nodes appear/disappear (and
        // their counts refresh) without re-running git. Keeps the current filter —
        // and the viewport: these counts are fed by a background poll the user did
        // not ask for (touching any file in the work tree changes them), so this
        // must never move the list under him.
        if (_allRows.Count > 0)
        {
            ApplyFilterCore(_search.Text, preserveViewport: true);
        }
    }

    // Builds one synthesised row. Dates are DateTime.MaxValue so FormatDate renders
    // the Date cell blank, and ParentHashes stays empty so DAG navigation never walks
    // into (or out of) an artificial node.
    //
    // GraphParents carries the edge the graph must draw — the next artificial row, or
    // HEAD — WITHOUT making it a navigable parent. It is empty when HEAD is outside the
    // loaded window: the node then stands alone rather than pointing at a commit that
    // is not the checked-out one.
    private static RevisionRow MakeArtificial(string hash, string subject, string? graphParent)
        => new(
            Hash: hash,
            ShortHash: string.Empty,
            Author: string.Empty,
            AuthorDate: DateTime.MaxValue,
            CommitDate: DateTime.MaxValue,
            Subject: subject,
            ParentHashes: [],
            RefNames: [])
        {
            GraphParents = graphParent is null ? [] : [graphParent],
        };

    // Prepends the artificial rows to the filtered commit rows and re-runs the DAG
    // layout over the whole displayed set, so their edge down to HEAD is a real graph
    // edge with a lane of its own.
    //
    // It used to be painted on afterwards instead (a segment forced into HEAD's lane on
    // every row above HEAD), which the layout knew nothing about: whenever HEAD was not
    // the topmost row — i.e. after checking out any branch that is behind another in
    // date order — that stroke ran through rows whose lane was free or, worse, already
    // carried an unrelated branch, and the two read as ONE line. Branches looked joined
    // that are not.
    //
    // They are dropped whenever they could not be drawn honestly:
    //  * quick (in-memory) filter — the rows shown are a non-contiguous subset and
    //    the graph column is collapsed anyway, so there is nothing to attach to;
    //  * git filter that dropped HEAD itself — the "Working directory" node would
    //    otherwise dangle onto an unrelated commit. (The original instead runs an
    //    extra `git rev-list` to re-attach them to the nearest surviving ancestor;
    //    here they simply stay hidden until HEAD is back in the result.)
    // With a git filter that DID keep HEAD they are shown normally: git rewrites the
    // parent links, so the surrounding lanes remain meaningful.
    private IReadOnlyList<RevisionRow> BuildDisplayRows(IReadOnlyList<RevisionRow> commits)
    {
        _artificialCount = 0;

        bool wanted = _showArtificial
            && (_unstaged > 0 || _staged > 0) && !_quickFilterActive && commits.Count > 0;
        if (!wanted)
        {
            return commits;
        }

        string? head = null;
        for (int i = 0; i < commits.Count; i++)
        {
            if (commits[i].IsHead)
            {
                head = commits[i].Hash;
                break;
            }
        }

        if (head is null && GitFilterActive)
        {
            // The filter excluded the checked-out commit: nothing to hang them off.
            return commits;
        }

        // Chain them the way the pending work actually flows: working directory →
        // index → HEAD. With no index row the working directory hangs off HEAD.
        List<RevisionRow> display = [];
        if (_unstaged > 0)
        {
            display.Add(MakeArtificial(WorkTreeHash,
                T("TranslatedStrings/_workingDirectoryText.Text", "Working directory"),
                _staged > 0 ? IndexHash : head));
        }

        if (_staged > 0)
        {
            display.Add(MakeArtificial(IndexHash,
                T("TranslatedStrings/_indexText.Text", "Commit index"), head));
        }

        _artificialCount = display.Count;

        display.AddRange(commits);

        // Re-run the layout over the combined list so the artificial nodes get real
        // lanes and the edge to HEAD is routed like any other, instead of being drawn
        // over lanes the layout has already given to somebody else.
        return RevisionService.BuildRevisionGraph(display, ColourPerBranch, ColourAtRemoteMirror);
    }

    /// <summary>
    ///  Loads and displays the recent revisions of the repository at
    ///  <paramref name="repoPath"/>. Heavy git work runs off the UI thread.
    ///
    ///  <para>Asking again for the repository that is ALREADY shown is a refresh,
    ///  not a load: the shell calls this from <c>RefreshAll</c>, which the
    ///  repository watcher fires on its own whenever a file moves under the work
    ///  tree. A refresh must therefore be invisible to a user who is reading the
    ///  history — same depth, same scroll position, same selection — otherwise the
    ///  list snaps back to the first commit under his hands. Only a DIFFERENT
    ///  repository starts from scratch.</para>
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        bool sameRepo = _loaded.Count > 0
            && string.Equals(_repoPath, repoPath, StringComparison.Ordinal);

        _repoPath = repoPath;

        if (sameRepo)
        {
            RefreshKeepingView();
            return;
        }

        // Which of this repository's remotes are on the GitHub host, answered ONCE per
        // repository rather than on every right-click: the answer costs a `git remote
        // -v` and changes only when the remotes do, which is a reload away.
        _gitHubRemotes = null;

        Reload();
    }

    /// <summary>
    ///  Loads the history of ONE FILE — the same grid, graph, ref decorations,
    ///  columns, multi-selection, row menu and hotkeys, with the walk narrowed to
    ///  <paramref name="filePath"/> and following it across renames. This is the
    ///  entry point the File history tab uses; <see cref="LoadRepository"/> stays the
    ///  repository-wide one.
    ///
    ///  <para><paramref name="filePath"/> is repository-relative (POSIX or native
    ///  separators; it is normalised here) and is quoted when it contains blanks, so
    ///  a name with spaces stays ONE path — which is also what
    ///  <see cref="RevisionFilter.FollowsSinglePath"/> requires for the rename
    ///  following to be honoured at all.</para>
    ///
    ///  <para>Following renames does NOT mean the walk uses <c>--follow</c>: the
    ///  service first expands the request into every name the file has had and then
    ///  runs an ordinary path-filtered walk over all of them, which is what keeps the
    ///  file's real branches and merges in the graph. See
    ///  <see cref="RevisionService.LoadRevisionPage"/> and
    ///  <see cref="FollowedPathService"/>.</para>
    ///
    ///  <para><paramref name="options"/> are the four <c>git log</c> switches the
    ///  upstream <c>FormFileHistory</c> exposes (follow renames / exact renames only /
    ///  full history / simplify merges). Asking again for the same file with the same
    ///  options is a refresh: depth, scroll offset and selection survive, so the
    ///  shell's global refresh does not pull the list out from under the user. A
    ///  different file — or a changed switch — restarts the walk.</para>
    /// </summary>
    public void LoadFileHistory(string repoPath, string filePath, FileHistoryOptions? options = null)
    {
        FileHistoryOptions opts = options ?? new FileHistoryOptions();
        string path = filePath.Replace('\\', '/').Trim();

        // Blanks in an unquoted value read as a SEPARATOR between several paths
        // (RevisionFilter.BuildPathArgument, mirroring upstream), which would both
        // filter on the wrong paths and suppress --follow.
        string pathArgument = path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path;

        RevisionFilter filter = new()
        {
            PathFilter = pathArgument,
            FollowRenames = opts.FollowRenames,
            ExactRenamesAndCopiesOnly = opts.ExactRenamesAndCopiesOnly,
            FullHistory = opts.FullHistory,
            SimplifyMerges = opts.SimplifyMerges,
        };

        bool sameWalk = _fileHistoryMode
            && _loaded.Count > 0
            && string.Equals(_repoPath, repoPath, StringComparison.Ordinal)
            && string.Equals(_fileHistoryFile, path, StringComparison.Ordinal)
            && _gitFilter == filter;

        bool entering = !_fileHistoryMode;

        _fileHistoryMode = true;
        _fileHistoryFile = path;
        _repoPath = repoPath;
        _gitFilter = filter;

        if (entering)
        {
            // The walk is an ordinary path-filtered one, so it reaches every branch
            // that touched the file — which is the whole point: the repository grid's
            // "filter file in grid" shows those commits, and a file history that
            // stopped at the current branch was showing fewer. The user can still
            // narrow it from the Branches flyout; only the default is set here, and
            // only on entry, so a later choice survives a refresh.
            _branchScope = BranchScope.AllBranches;
        }

        // No working-directory / index rows: they are the pending work of the
        // REPOSITORY, they are never part of a file's log, and nothing feeds their
        // counts here (SetWorkingState is the shell's call on the main grid). Off
        // explicitly rather than "happens to be empty".
        _showArtificial = false;

        // "Branches" and "View" now reshape a walk that can be reshaped, so they stay;
        // only the entry that cannot (artificial commits) is dropped from the View
        // flyout, which is rebuilt here because it was first built outside this mode.
        _viewButton.Flyout = BuildViewFlyout();

        // Still hidden: the advanced filter's own path field would fight this one, and
        // its ✕ would empty the tab.
        _filterButton.IsVisible = false;
        _resetFilterButton.IsVisible = false;

        if (sameWalk)
        {
            RefreshKeepingView();
            return;
        }

        Reload();
    }

    /// <summary>
    ///  Re-runs the walk for the repository already on screen without disturbing
    ///  what the user is looking at.
    ///
    ///  <para>Three things are preserved that a plain <see cref="Reload"/> destroys:
    ///  the DEPTH (a single page as long as everything paged in so far, so the rows
    ///  the user scrolled down to are still there afterwards), the scroll offset and
    ///  the selection — the latter two by <see cref="ApplyFilterCore"/>'s
    ///  preserve-viewport path. The ItemsSource is not unbound up-front either: the
    ///  old rows stay on screen for the (off-thread) duration of the walk instead of
    ///  blanking the grid.</para>
    /// </summary>
    private void RefreshKeepingView()
    {
        if (string.IsNullOrEmpty(_repoPath))
        {
            return;
        }

        // Re-walk exactly as far as the user has already paged in, in one page. One
        // commit MORE is asked for: "the page came back full" is how the service
        // reports that the walk continues, so a request for exactly `depth` would
        // always come back full and leave _hasMore (hence the "N+" commit count, and a
        // pointless extra walk at the bottom) set on a fully-walked history. The extra
        // row is trimmed off in the merge.
        int depth = Math.Max(_pageSize, _loaded.Count);

        _loaded = [];
        _hasMore = false;
        LoadPage(restart: true, preserveView: true, maxCount: depth + 1);

        RefreshRefContext();
    }

    /// <summary>
    ///  (Re-)runs the git log for the stored repository under the current branch
    ///  scope, off the UI thread. Used both for the initial load and whenever the
    ///  branch-scope toggle changes. All view state (text filter, git-notes, date
    ///  mode, column show/hide) is preserved: the DAG graph is rebuilt by the
    ///  service, and the current filter text is re-applied on completion.
    /// </summary>
    private void Reload()
    {
        if (string.IsNullOrEmpty(_repoPath))
        {
            return;
        }

        // Drop everything loaded so far and restart the walk at its first page.
        _loaded = [];
        _hasMore = false;
        _scroll = null;
        SetListItems(null);
        LoadPage(restart: true);

        // The context menu's predicates need the checked-out branch and the kind of
        // every ref; both are refreshed alongside the walk, off the UI thread.
        RefreshRefContext();
    }

    /// <summary>
    ///  Appends the next page of history, silently, when the list is scrolled to its
    ///  end. A no-op while a page is already in flight or when the walk is exhausted.
    /// </summary>
    private void LoadMore()
    {
        if (_loadingPage || !_hasMore || string.IsNullOrEmpty(_repoPath))
        {
            return;
        }

        LoadPage(restart: false);
    }

    /// <summary>
    ///  Runs ONE page of the git walk off the UI thread and merges it into the view.
    ///
    ///  <para>All git work (the paged <c>git log</c>) AND the DAG rebuild happen inside
    ///  <see cref="Task.Run(Action)"/>; only the final merge touches the UI, through
    ///  <see cref="Dispatcher"/>. A generation token discards pages belonging to a walk
    ///  that a <see cref="Reload"/> has since replaced.</para>
    ///
    ///  <para>Appending is transparent to the rest of the view: the graph is rebuilt
    ///  over the whole accumulated list (so lanes/edges and the artificial
    ///  "Working directory" / "Commit index" nodes stay correct), the selection is
    ///  restored by hash, and the scroll offset is put back where the user left it.</para>
    /// </summary>
    private void LoadPage(bool restart, bool preserveView = false, int maxCount = 0)
    {
        string repoPath = _repoPath;
        BranchScope scope = _branchScope;
        IReadOnlyList<string> filteredRefs = _filteredRefs;
        bool showRemotes = _showRemotes;
        bool showTags = _showTags;
        bool showStashes = _showStashes;
        bool topoOrder = _topoOrder;
        bool authorDateOrder = _authorDateSort;
        string? walkBound = _walkBound;
        int pageSize = maxCount > 0 ? maxCount : _pageSize;
        RevisionFilter filter = _gitFilter;

        // An append continues where the user is; a silent refresh re-walks the same
        // history underneath him. Both must leave the viewport alone — only a real
        // (re)start, i.e. a different repository or an explicitly changed scope /
        // filter / page size, is allowed to go back to the first commit.
        bool keepViewport = !restart || preserveView;

        // How many rows the merge may keep (0 = no trimming); see the merge below.
        int trimTo = preserveView && maxCount > 0 ? maxCount - 1 : 0;

        IReadOnlyList<RevisionRow> before = restart ? [] : _loaded;
        int skip = before.Count;

        if (restart)
        {
            _loadGeneration++;

            // A real (re)start rebuilds the history, so the highlighting goes back to
            // HEAD — the state the original's freshly built RevisionGraph is in. An
            // APPEND keeps the anchor, so scrolling further back does not undo an
            // Alt+click (upstream loads incrementally into the same graph, too).
            _highlightAnchor = null;
        }

        if (restart)
        {
            // Stop the walk that is still running. The generation counter above already
            // makes its result unusable, but on its own it lets the superseded `git log`
            // read the history to the end for nobody — and a pickaxe (-S/-G) walk over a
            // large repository is exactly the walk that takes minutes, so the user who
            // narrows the filter to escape it would be waiting behind BOTH.
            //
            // The token reaches the process through RevisionService.LoadRevisionPage;
            // the core's reader observes it between output chunks and kills git when the
            // process handle is disposed. No machinery of our own beyond the source.
            CancellationTokenSource? previous = _loadCts;
            _loadCts = new CancellationTokenSource();
            previous?.Cancel();
            previous?.Dispose();
        }

        CancellationToken cancellation = (_loadCts ??= new CancellationTokenSource()).Token;

        int generation = _loadGeneration;
        _loadingPage = true;

        // Read on the UI thread and captured: the graph is rebuilt on the pool below,
        // and _gridPrefs is replaced wholesale whenever the settings change.
        bool colourPerBranch = ColourPerBranch;
        bool colourAtRemoteMirror = ColourAtRemoteMirror;

        if (restart && !preserveView)
        {
            // A silent refresh keeps the real status line: flashing "Loading…" over
            // it would be the visible symptom the refresh is meant not to have.
            _status.Text = T("RevisionGridControl/_strLoading.Text", "Loading…");
        }

        if (restart)
        {
            // Only a RESTART is worth a spinner. An append is the user scrolling into
            // older history: the rows they are reading stay where they are, and veiling
            // them to announce work happening below the fold would be a lie about what
            // is going on. The overlay's own delay then decides whether a restart is slow
            // enough to be worth saying anything at all.
            _busy.Show();
        }

        _ = Task.Run(() =>
        {
            try
            {
                RevisionPage page = _service.LoadRevisionPage(
                    repoPath,
                    skip: skip,
                    maxCount: pageSize,
                    scope: scope,
                    filteredRefs: filteredRefs,
                    showRemotes: showRemotes,
                    showTags: showTags,
                    showStashes: showStashes,
                    topoOrder: topoOrder,
                    filter: filter,
                    authorDateOrder: authorDateOrder,
                    excludeAncestorsOf: walkBound,
                    cancellationToken: cancellation);

                // Merge and rebuild the DAG here, still off the UI thread.
                List<RevisionRow> merged = new(before.Count + page.Rows.Count);
                merged.AddRange(before);
                merged.AddRange(page.Rows);

                // The refresh asked for one commit more than it wants to display
                // (see RefreshKeepingView): getting it back is the proof the walk
                // continues, and it is dropped so the loaded depth — and therefore
                // the scroll extent — is exactly what it was before the refresh.
                bool hasMore = page.HasMore;
                if (trimTo > 0 && merged.Count > trimTo)
                {
                    merged.RemoveRange(trimTo, merged.Count - trimTo);
                    hasMore = true;
                }

                // A file history is normally walked as an ordinary path filter over
                // every name the file has had, so git rewrites the parents and the real
                // branches and merges survive. Only where that expansion could not run
                // (folder, several paths, over-long pathspec, failed expansion) does the
                // walk still use --follow — and there git rewrites nothing, so the rows
                // name parents that are not in the result set and the lane pass would
                // draw one dead-end stub per commit. The page says which one it was.
                IReadOnlyList<RevisionRow> forGraph = page.FollowedWithoutParentRewrite
                    ? RevisionService.ChainFollowedHistory(merged)
                    : merged;

                IReadOnlyList<RevisionRow> graphed = RevisionService.BuildRevisionGraph(forGraph, colourPerBranch, colourAtRemoteMirror);

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _loadGeneration)
                    {
                        // A reload happened while this page was loading: drop it.
                        return;
                    }

                    _loadingPage = false;
                    _busy.Hide();
                    _loaded = merged;
                    _hasMore = hasMore;

                    if (_fileHistoryMode)
                    {
                        // The expansion collected the name the file had in each commit
                        // on the way to building the pathspec; hand it to the host
                        // rather than have it ask git the same question again.
                        if (page.PathByHash is { Count: > 0 } pathByHash)
                        {
                            FileHistoryPathsResolved?.Invoke(pathByHash);
                        }

                        // The --follow fallback IS pinned to one starting commit, so the
                        // scope the flyout shows would be a claim the walk did not keep.
                        if (page.FollowedWithoutParentRewrite)
                        {
                            _branchScope = BranchScope.CurrentBranch;
                        }
                    }

                    int laneCount = graphed.Count > 0 ? graphed[0].LaneCount : 1;
                    _graphWidth = Math.Clamp(laneCount, 1, MaxGraphLanes) * LaneWidth;
                    _allRows = graphed;
                    // Display-only: _repoPath keeps the absolute path, the status
                    // line shows the same "~" form as the toolbar repo dropdown.
                    _repoLabel = PathDisplay.CollapseHome(repoPath);
                    // Recompute HEAD reachability for the relatives/highlight styles.
                    ComputeReachability();
                    // Re-apply any current filter text so a reload keeps the view
                    // consistent. This is also what rebinds the rows, so it is the
                    // single place where scroll offset, selection and keyboard focus
                    // are carried across the rebind (see ApplyFilterCore).
                    ApplyFilterCore(_search.Text, keepViewport);
                    ApplyPendingSelection();
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer walk, which owns the spinner, the status line
                // and _loadingPage from the moment it started. Reporting anything here
                // would overwrite the state of a load that is still running.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _loadGeneration)
                    {
                        return;
                    }

                    _loadingPage = false;
                    _busy.Hide();
                    _hasMore = false;
                    _status.Text = string.Format(T("Error: {0}"), ex.Message);
                });
            }
        });
    }

    // The list was scrolled: capture its ScrollViewer and, once the end of the
    // loaded history comes into view, append the next page automatically.
    private void OnListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer viewer)
        {
            return;
        }

        _scroll = viewer;

        double remaining = viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y;
        if (remaining <= 2 && viewer.Extent.Height > viewer.Viewport.Height)
        {
            LoadMore();
        }
    }

    /// <summary>
    ///  Sets how many commits one page of the walk loads (the port's equivalent of
    ///  the original's <c>MaxRevisionGraphCommits</c>) and restarts the walk.
    /// </summary>
    public void SetPageSize(int pageSize)
    {
        int value = Math.Max(50, pageSize);
        if (_pageSize == value)
        {
            return;
        }

        _pageSize = value;
        Reload();

        // The page size is persisted alongside the checkable options, so it rides the
        // same change notification: a host that stores view state on ViewOptionsChanged
        // then needs no second subscription. (No check mark corresponds to it, so the
        // in-place sync OptionsChanged does is simply a no-op for it.)
        OptionsChanged();
    }

    // Path display (home collapsed to "~") is shared with the toolbar's repository


    // Human label for the current branch scope, shown in the status line so the
    // effect of the toggle (and the resulting commit count) is visible.
    private string ScopeLabel
    {
        get
        {
            string scope = _branchScope switch
            {
                BranchScope.CurrentBranch => T("current branch"),
                BranchScope.Filtered => _filteredRefs.Count == 0
                    ? T("filtered (no ref selected → HEAD)")
                    : string.Format(T("filtered ({0})"), string.Join(", ", _filteredRefs)),
                _ => T("all branches"),
            };

            // A bounded walk shows FEWER commits than its scope names, and the count next
            // to this label is the visible consequence: saying only "current branch" for
            // `HEAD ^<merge-base>` would make the two disagree with no explanation.
            return _walkBound is { Length: > 0 } bound
                ? scope + string.Format(T(", down to {0}"), bound[..Math.Min(8, bound.Length)])
                : scope;
        }
    }

    /// <summary>
    ///  SUBMITS a search term, the way pressing Enter in the grid's own filter box
    ///  does: the text becomes a <c>git log</c> criterion on the field chosen in the
    ///  "Filter type" dropdown, so the whole history is searched — not merely the
    ///  pages already in memory — and it joins the recent-searches list.
    ///
    ///  <para>This is the host-driven entry point (the toolbar's "Filter:" box). It
    ///  used to be an in-memory sieve over the loaded rows, called on every
    ///  keystroke, which is why a term living in a commit that had not been paged in
    ///  yet was simply never found. The toolbar now raises its event on Enter only,
    ///  matching upstream's FilterToolBar, and this applies it to git.</para>
    ///
    ///  <para>An empty text withdraws the quick criteria (and only those: a path,
    ///  date range or limit set from the filter dialog stays).</para>
    /// </summary>
    public void ApplyFilter(string text)
    {
        string value = text ?? string.Empty;

        // Keep the grid's own box showing what was submitted, so the two surfaces
        // read the same. Assigning raises TextChanged -> ApplyFilterCore, which is
        // the in-memory preview; SubmitQuickFilter then supersedes it.
        if (_search.Text != value)
        {
            _search.Text = value;
        }

        SubmitQuickFilter(value);
    }

    // What the box invites the user to do: name the field Enter will search, so the
    // difference between the as-you-type preview and the real filter is on screen.
    private string QuickFilterWatermark
        => string.Format(T("{0} — press Enter to search git"), QuickFilterFieldLabel);

    // The label of the field the quick box searches, on the dropdown button.
    //
    // The diff field names WHICH of the two pickaxe questions is armed: -S and -G
    // do not answer the same one (a commit that only MOVES the line is a -G hit and
    // not a -S hit), so a box that says merely "Diff contains" would leave the user
    // reading a result set without knowing which question produced it.
    private string QuickFilterFieldLabel => _quickFilterField switch
    {
        QuickFilterField.Committer => T("FilterToolBar/tsmiCommitterFilter.Text", "Committer"),
        QuickFilterField.Author => T("TranslatedStrings/_author.Text", "Author"),
        QuickFilterField.DiffContent => string.Format(
            T("{0} ({1})"),
            T("FilterToolBar/tsmiDiffContainsFilter.Text", "Diff contains"),
            QuickFilterDiffModeLabel),
        _ => T("FilterToolBar/tsmiMessageFilter.Text", "Commit message"),
    };

    // Short name of the armed pickaxe form, for the button and the watermark. Kept
    // to one word each because it rides inside another label; the flyout carries
    // the full sentence.
    private string QuickFilterDiffModeLabel
        => _gitFilter.DiffContentIsRegex ? T("pattern") : T("text");

    // Upstream's "Filter type" dropdown (FilterToolBar.Designer.cs:48-70).
    //
    // Upstream lets SEVERAL fields be ticked at once; git then ANDs them, so the
    // same text has to appear in the message AND in the author for a commit to
    // survive — a combination that is almost always empty and reads as a broken
    // filter. Here the four are a radio group: one field at a time, which is how
    // the box is actually used. Combining criteria is still possible, and explicit,
    // in the filter dialog.
    private Flyout BuildQuickFilterTypeFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 180 };
        panel.Children.Add(SectionLabel(T("FilterToolBar/tsddbtnRevisionFilter.Text", "Filter type")));

        panel.Children.Add(OptionRadio(
            OptQuickFilterMessage,
            T("FilterToolBar/tsmiMessageFilter.Text", "Commit message"),
            "revQuickFilterField",
            () => SetQuickFilterField(QuickFilterField.Message)));
        panel.Children.Add(OptionRadio(
            OptQuickFilterCommitter,
            T("FilterToolBar/tsmiCommitterFilter.Text", "Committer"),
            "revQuickFilterField",
            () => SetQuickFilterField(QuickFilterField.Committer)));
        panel.Children.Add(OptionRadio(
            OptQuickFilterAuthor,
            T("TranslatedStrings/_author.Text", "Author"),
            "revQuickFilterField",
            () => SetQuickFilterField(QuickFilterField.Author)));
        panel.Children.Add(OptionRadio(
            OptQuickFilterDiff,
            T("FilterToolBar/tsmiDiffContainsFilter.Text", "Diff contains (SLOW)"),
            "revQuickFilterField",
            () => SetQuickFilterField(QuickFilterField.DiffContent)));

        // The -S/-G choice, subordinate to the field it belongs to: indented, and on
        // screen only while "Diff contains" is the armed field. Kept out of the four
        // radios above because it is NOT a fifth field — it is which question the
        // diff field asks — and offering it as a peer would suggest the box can
        // search something else again.
        //
        // The wording is the one RevisionFilter.DiffContent documents, and it is the
        // difference measured on a repository where a line is added, moved, removed
        // and re-added: -S skips the commit that only MOVED the line (the number of
        // occurrences did not change), -G reports it (a removed and an added line
        // both match). "Contains" would describe neither.
        _quickFilterDiffMode = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(16, 2, 0, 0),
            IsVisible = _quickFilterField == QuickFilterField.DiffContent,
        };
        _quickFilterDiffMode.Children.Add(OptionRadio(
            OptQuickFilterDiffLiteral,
            T("The text appears or disappears"),
            "revQuickFilterDiffMode",
            () => SetQuickFilterDiffMode(isRegex: false)));
        _quickFilterDiffMode.Children.Add(OptionRadio(
            OptQuickFilterDiffRegex,
            T("An added or removed line matches this pattern"),
            "revQuickFilterDiffMode",
            () => SetQuickFilterDiffMode(isRegex: true)));
        panel.Children.Add(_quickFilterDiffMode);

        panel.Children.Add(new TextBlock
        {
            Text = T("Press Enter in the box to search git; typing alone only sifts the rows already loaded."),
            Foreground = B("App.TextDim"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            MaxWidth = 220,
        });

        return new Flyout
        {
            Content = panel,
        };
    }

    // Switches the field the quick box searches. A term already submitted is
    // re-submitted against the new field, so the change is visible at once instead
    // of waiting for the next Enter — unless the caller is about to submit a term of
    // its own (resubmit: false), where re-running the OLD term would be a walk whose
    // result is discarded a moment later.
    private void SetQuickFilterField(QuickFilterField field, bool resubmit = true)
    {
        if (_quickFilterField == field)
        {
            return;
        }

        _quickFilterField = field;
        _filterTypeButton.Content = Chevron(QuickFilterFieldLabel);
        _search.Watermark = QuickFilterWatermark;
        if (_quickFilterDiffMode is not null)
        {
            _quickFilterDiffMode.IsVisible = field == QuickFilterField.DiffContent;
        }

        OptionsChanged();

        if (resubmit && _submittedQuickText.Length > 0)
        {
            SubmitQuickFilter(_submittedQuickText);
        }
    }

    /// <summary>
    ///  Chooses which pickaxe form the quick box arms: <c>-S</c> (the number of
    ///  occurrences of the literal text changed) or <c>-G</c> (an added or removed
    ///  line matches the pattern as a regex).
    /// </summary>
    private void SetQuickFilterDiffMode(bool isRegex)
    {
        if (_gitFilter.DiffContentIsRegex == isRegex)
        {
            return;
        }

        // Only an ARMED pickaxe has an argument that just changed. With an empty
        // diff text the flag is nowhere in the command line, and reloading would
        // re-read the whole history to produce byte-identical rows.
        bool armed = _gitFilter.DiffContent.Length > 0;
        ArmQuickFilterDiffMode(isRegex);
        if (armed)
        {
            Reload();
        }
    }

    // Stores the pickaxe form and re-reads the two surfaces that name the armed
    // field (see QuickFilterFieldLabel), WITHOUT restarting the walk — so a caller
    // that is about to submit a search pays for one walk instead of two.
    private void ArmQuickFilterDiffMode(bool isRegex)
    {
        _gitFilter = _gitFilter with { DiffContentIsRegex = isRegex };
        _filterTypeButton.Content = Chevron(QuickFilterFieldLabel);
        _search.Watermark = QuickFilterWatermark;
        OptionsChanged();
    }

    /// <summary>
    ///  Searches the history for <paramref name="text"/> as diff content, the way
    ///  choosing "Diff contains" in the quick box and pressing Enter would: the
    ///  LITERAL form (<c>-S</c>) is armed, because the caller (the diff view's
    ///  "Search history for this text") hands over selected source code, which is a
    ///  broken regex far more often than an intended one.
    ///
    ///  <para>Whitespace-only or empty text is ignored rather than submitted: it
    ///  would arm a criterion that matches every commit touching any file, i.e. an
    ///  expensive way to filter nothing.</para>
    /// </summary>
    public void SearchDiffContent(string text)
    {
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return;
        }

        // Neither call reloads: the submit below is the one walk this costs.
        SetQuickFilterField(QuickFilterField.DiffContent, resubmit: false);
        ArmQuickFilterDiffMode(isRegex: false);

        // ApplyFilter (not SubmitQuickFilter directly) so the box shows what is being
        // searched: the criterion is otherwise invisible, and the user would have no
        // way to see, edit or undo the text a context menu put into git's hands.
        ApplyFilter(value);
    }

    /// <summary>
    ///  Hands <paramref name="text"/> to git as a criterion on the currently chosen
    ///  field, records it in the recent-searches list and restarts the walk.
    ///
    ///  <para>The four quick fields (author / committer / message / diff) are OWNED
    ///  by this box: submitting replaces whatever they held, including a value the
    ///  filter dialog put there — upstream's toolbar drives exactly the same four.
    ///  Everything else the dialog can set (path, dates, limit, no-merges, …) is
    ///  left untouched.</para>
    /// </summary>
    public void SubmitQuickFilter(string? text)
    {
        string value = (text ?? string.Empty).Trim();

        RevisionFilter next = _gitFilter with
        {
            Author = string.Empty,
            Committer = string.Empty,
            Message = string.Empty,
            DiffContent = string.Empty,
        };

        if (value.Length > 0)
        {
            next = _quickFilterField switch
            {
                QuickFilterField.Committer => next with { Committer = value },
                QuickFilterField.Author => next with { Author = value },
                QuickFilterField.DiffContent => next with { DiffContent = value },
                _ => next with { Message = value },
            };
        }

        _submittedQuickText = value;
        PushFilterMru(value);

        // ApplyRevisionFilter reloads; on completion it re-runs ApplyFilterCore,
        // which now sees the box text as "already applied by git" and stops sifting
        // in memory. When the criteria did not actually change it returns early, so
        // the in-memory preview still has to be dropped explicitly.
        if (next == _gitFilter)
        {
            ApplyFilterCore(_search.Text, preserveViewport: true);
            return;
        }

        ApplyRevisionFilter(next);
    }

    // --- the recent-searches list (MRU) -----------------------------------------

    /// <summary>
    ///  The searches submitted from the quick box, newest first, capped at 30 —
    ///  upstream's <c>AppSettings.RevisionFilterDropdowns</c>
    ///  (<c>FilterToolBar.cs:394-399</c>).
    /// </summary>
    public IReadOnlyList<string> FilterMru => _filterMru;

    /// <summary>
    ///  Raised whenever the recent-searches list changes, so a host that wants to
    ///  mirror it elsewhere can. Persistence needs no subscription: the list rides
    ///  <see cref="PersistedViewOptions"/> like every other piece of grid state.
    /// </summary>
    public event Action<IReadOnlyList<string>>? FilterMruChanged;

    private void PushFilterMru(string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        int existing = _filterMru.FindIndex(v => string.Equals(v, value, StringComparison.Ordinal));
        if (existing == 0)
        {
            return;
        }

        if (existing > 0)
        {
            _filterMru.RemoveAt(existing);
        }

        _filterMru.Insert(0, value);
        while (_filterMru.Count > MaxFilterMru)
        {
            _filterMru.RemoveAt(_filterMru.Count - 1);
        }

        RebuildFilterMruFlyout();
        FilterMruChanged?.Invoke(_filterMru);
        OptionsChanged();
    }

    // Rebuilt whenever the list changes — never from an Opening handler, which for a
    // popup is too late to re-measure.
    private void RebuildFilterMruFlyout()
    {
        _mruButton.IsEnabled = _filterMru.Count > 0;

        StackPanel panel = new() { Spacing = 1, Margin = new Thickness(4), MinWidth = 200 };
        panel.Children.Add(SectionLabel(T("Recent searches")));

        if (_filterMru.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = T("Nothing searched yet."),
                Foreground = B("App.TextDim"),
                FontSize = 11,
            });
        }

        foreach (string entry in _filterMru)
        {
            Button item = new()
            {
                Content = new TextBlock
                {
                    Text = entry,
                    Foreground = B("App.Text"),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 260,
                },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            item.Classes.Add(Theming.BarButtonStyles.MenuClass);
            item.Click += (_, _) =>
            {
                _mruButton.Flyout?.Hide();
                ApplyFilter(entry);
            };
            panel.Children.Add(item);
        }

        _mruButton.Flyout = new Flyout
        {
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = panel,
            },
        };
    }

    /// <summary>
    ///  The criteria currently narrowing the git walk (never <see langword="null"/>;
    ///  <see cref="RevisionFilter.None"/> when nothing is filtered).
    /// </summary>
    public RevisionFilter CurrentFilter => _gitFilter;

    /// <summary>Raised whenever the git filter changes, with its "is active" state.</summary>
    public event Action<bool>? FilterStateChanged;

    // Funnel + caption; the funnel is filled while a filter is set, so the state is
    // readable at a glance (the original swaps the toolbar's funnel icon likewise).
    private string FilterButtonCaption
        => string.Format("{0} {1}…", GitFilterActive ? "⧩" : "⧨", T("FormRevisionFilter/$this.Text", "Filter"));

    private static string ResetFilterTip
        => RevisionFilterDialog.StripMnemonic(
            T("FormBrowse/tsmiResetAllFilters.Text", "&Reset revision filters"));

    /// <summary>
    ///  Opens the revision filter dialog and, when confirmed, re-runs the walk with
    ///  the new criteria. Public so the shell (menu item / toolbar button) can open
    ///  it too. Safe to call with no top-level window (headless): it does nothing.
    /// </summary>
    public async Task ShowFilterDialogAsync()
    {
        Window? owner = TopLevel.GetTopLevel(this) as Window;
        RevisionFilter? updated = await RevisionFilterDialog.AskAsync(owner, _gitFilter);
        if (updated is null)
        {
            return;
        }

        ApplyRevisionFilter(updated);
    }

    /// <summary>
    ///  Applies a set of git filter criteria and restarts the walk (page 1) so the
    ///  filter is honoured by git itself. A no-op when nothing actually changed.
    /// </summary>
    public void ApplyRevisionFilter(RevisionFilter filter)
    {
        RevisionFilter value = filter ?? RevisionFilter.None;

        // In file-history mode the path + rename-following criteria ARE the subject
        // of the view (LoadFileHistory owns them); a filter arriving from anywhere
        // else may narrow the walk further, never take the file away.
        if (_fileHistoryMode)
        {
            value = value with
            {
                PathFilter = _gitFilter.PathFilter,
                FollowRenames = _gitFilter.FollowRenames,
                ExactRenamesAndCopiesOnly = _gitFilter.ExactRenamesAndCopiesOnly,
                FullHistory = _gitFilter.FullHistory,
                SimplifyMerges = _gitFilter.SimplifyMerges,
            };
        }

        if (value == _gitFilter)
        {
            return;
        }

        _gitFilter = value;
        UpdateFilterChrome();
        FilterStateChanged?.Invoke(GitFilterActive);

        // The criteria change WHICH commits git returns, so the walk restarts from
        // its first page (paging then indexes into the filtered walk).
        Reload();
    }

    /// <summary>
    ///  The original's "Reset all filters": drops both the git criteria and the
    ///  quick box, bringing the full history back. Public so the shell can offer it
    ///  from a menu as well.
    /// </summary>
    public void ResetAllFilters()
    {
        bool hadGitFilter = GitFilterActive;
        _search.Text = string.Empty;
        _submittedQuickText = string.Empty;

        if (!hadGitFilter)
        {
            // Only the quick box was set; clearing it already restored every row.
            ApplyFilterCore(string.Empty);
            return;
        }

        ApplyRevisionFilter(RevisionFilter.None);
    }

    // Keeps the funnel caption and the reset affordance in step with the filter.
    private void UpdateFilterChrome()
    {
        _filterButton.Content = FilterButtonCaption;

        // File-history mode hides both (its path filter is the tab's subject, not a
        // user filter): a "reset filters" ✕ there would empty the tab.
        _resetFilterButton.IsVisible = GitFilterActive && !_fileHistoryMode;
    }

    /// <summary>
    ///  Rebinds <see cref="_rows"/> onto the list, optionally without the user
    ///  noticing.
    ///
    ///  <para>Avalonia's <c>ListBox</c> has no "re-template the rows in place" hook,
    ///  so every change to the row catalogue (filter, graph width, date mode,
    ///  language, artificial rows) goes through a null-then-reassign of
    ///  <c>ItemsSource</c>. That rebind resets the viewport to the top, empties the
    ///  selection and drops keyboard focus — acceptable when the USER asked for a
    ///  different set of rows, never acceptable when something refreshed by itself.
    ///  With <paramref name="preserveViewport"/> the offset is put back after
    ///  layout; the selection and focus are put back either way, since a row that
    ///  still exists was never meant to be deselected.</para>
    ///
    ///  <para>Re-selecting is done with <c>AutoScrollToSelectedItem</c> suppressed:
    ///  otherwise the list would jump to the selected row and undo the offset that
    ///  is being restored.</para>
    /// </summary>
    private void RebindRows(bool preserveViewport)
    {
        if (_rebinding || _inSelectionChanged)
        {
            // Re-entered from a SelectionChanged that is still inside its selection
            // batch — this view's own ItemsSource assignment, or the widget's own
            // response to a click. Assigning again now is the fatal case
            // documented on _rebinding, so the request is remembered instead and run
            // once, later. Background priority (not Send/Normal) so it lands after
            // the current assignment AND the layout pass it schedules — the same
            // reason the scroll offset has to be restored there.
            //
            // Coalescing rule: the viewport is only preserved when EVERY pending
            // requester wanted it preserved. A caller that asked for a reset (the
            // user chose a different set of rows) must not have it silently
            // upgraded to "keep looking at the same place".
            _rebindQueuedPreserveViewport = _rebindQueued
                ? _rebindQueuedPreserveViewport && preserveViewport
                : preserveViewport;

            if (!_rebindQueued)
            {
                _rebindQueued = true;
                Dispatcher.UIThread.Post(FlushQueuedRebind, DispatcherPriority.Background);
            }

            return;
        }

        // The graph's relative/non-relative flags are per DISPLAY ROW, so they are
        // refreshed here — the single place every rebind goes through, whether the
        // rows, the filter, the anchor or a highlight toggle changed.
        ComputeGraphRelatives();

        List<string> selected = _list.SelectedItems is { Count: > 0 } items
            ? items.OfType<RevisionRow>().Select(r => r.Hash).ToList()
            : [];
        Vector offset = _scroll?.Offset ?? default;

        // IsFocused as well as IsKeyboardFocusWithin: the two are not the same control.
        // Keyboard focus in this list lives on a ListBoxItem, so IsKeyboardFocusWithin
        // is what a clicked row sets — but a restore that landed on the ListBox itself
        // (see FocusSelectedRow's fallback) only sets IsFocused, and losing the flag on
        // the next rebind would drop focus out of the grid for good.
        bool hadFocus = _list.IsKeyboardFocusWithin || _list.IsFocused;

        // A FRESH collection instance, not _rows itself: re-assigning the very same
        // instance lets the virtualizing panel keep its realized containers (their
        // DataContext did not change), so rows already on screen would keep their old
        // visuals — a re-anchored highlight, say, would only show up on rows scrolled
        // into view afterwards. A copy guarantees every visible row goes through
        // BuildRow again. The items are the same RevisionRow objects, so selection
        // and the index lookups against _rows are unaffected.
        _rebinding = true;
        try
        {
            SetListItems(null);
            SetListItems(new List<RevisionRow>(_rows));

            bool autoScroll = _list.AutoScrollToSelectedItem;
            _list.AutoScrollToSelectedItem = false;
            RestoreSelection(selected);
            _list.AutoScrollToSelectedItem = autoScroll;
        }
        finally
        {
            _rebinding = false;
        }

        // A rebind can change WHICH row is selected (a reload rebuilt the rows, a
        // filter dropped the old selection), so the author highlight is re-evaluated
        // here as well. When it actually changed this re-enters once and then settles,
        // since the second pass finds the author unchanged.
        UpdateAuthorHighlight();

        if (!preserveViewport && !hadFocus)
        {
            return;
        }

        // The offset can only be restored once the rebound rows have been laid out,
        // hence the deferral to DispatcherPriority.Loaded — and once more at
        // Background priority: with the list virtualized, the first attempt can land
        // while the new panel still has a short extent, so the offset gets clamped
        // (deep in the history that threw the view back to the newest commit) and has
        // to be re-applied after the layout pass that follows.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (preserveViewport && _scroll is not null && offset.Y > 0)
                {
                    _scroll.Offset = offset;
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            if (_scroll is not null && _scroll.Offset.Y < offset.Y)
                            {
                                _scroll.Offset = offset;
                            }
                        },
                        DispatcherPriority.Background);
                }

                if (hadFocus)
                {
                    FocusSelectedRow();
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    ///  Puts keyboard focus back on the SELECTED ROW's container, falling back to the
    ///  list itself only when that container is not realized.
    /// </summary>
    /// <remarks>
    ///  <para><b>Why not simply <c>_list.Focus()</c>.</b> A <see cref="ListBox"/> is not
    ///  a focusable control — its <see cref="ListBoxItem"/>s are — so focusing the list
    ///  is close to a no-op, and every caller that did it was quietly leaving the grid
    ///  unfocused. That is what "the first click on a commit draws no white outline"
    ///  was: the click focuses the clicked item, the author highlight it triggers
    ///  re-templates every row (a rebind destroys and recreates the containers), the
    ///  focused container goes with them, and the restore aimed at the wrong control.
    ///  The row then painted its INACTIVE selection — a calmer blue, no focus
    ///  rectangle — because both of those read the real focus (see the row visual's
    ///  Sync). A second click looked like it "fixed" it only because the author was
    ///  already highlighted by then, so nothing re-templated and the focus survived.</para>
    ///
    ///  <para>It also restores what a focused grid can DO, not only how it looks: with
    ///  focus outside the list the arrow keys move nothing and type-to-search never
    ///  starts, because both are raised on the focused element.</para>
    /// </remarks>
    private void FocusSelectedRow()
    {
        int index = _list.SelectedIndex;
        if (index >= 0
            && _list.ContainerFromIndex(index) is Control container
            && container.Focus())
        {
            return;
        }

        _list.Focus();
    }

    // Runs the single rebind that was coalesced while another one was in flight.
    // A no-op if something already rebound in the meantime and cleared the flag.
    private void FlushQueuedRebind()
    {
        if (!_rebindQueued)
        {
            return;
        }

        _rebindQueued = false;
        RebindRows(_rebindQueuedPreserveViewport);
    }

    // The ONLY writer of _list.ItemsSource, so that no assignment can ever be made
    // without the re-entrancy guard raised (see _rebinding for why that is fatal
    // rather than merely untidy). Nested calls restore the flag to what they found
    // instead of clearing it, so RebindRows' outer guard survives them.
    private void SetListItems(IEnumerable<RevisionRow>? items)
    {
        bool outer = _rebinding;
        _rebinding = true;
        try
        {
            _list.ItemsSource = items;
        }
        finally
        {
            _rebinding = outer;
        }
    }

    /// <param name="preserveViewport">
    ///  <see langword="true"/> when the rebuild was not asked for by the user (a
    ///  watcher-driven refresh, new working-directory counts, a language change, an
    ///  appended page, the status flash expiring): the scroll position must survive
    ///  it. <see langword="false"/> only for an explicit filter change, where
    ///  showing the first match of the new result is the expected behaviour.
    /// </param>
    private void ApplyFilterCore(string? text, bool preserveViewport = false)
    {
        string query = (text ?? string.Empty).Trim();

        // Once the term has been SUBMITTED, git returned exactly the matching set,
        // so sifting the same term again in memory would only cost the graph column
        // and the artificial rows (both suppressed while the quick sieve is on) for
        // no change in the rows shown. Editing the box past the submitted text
        // re-enables the preview on top of the git result.
        if (query.Length > 0 && string.Equals(query, _submittedQuickText, StringComparison.Ordinal))
        {
            query = string.Empty;
        }

        bool wasFiltering = _quickFilterActive;
        _quickFilterActive = query.Length > 0;

        IReadOnlyList<RevisionRow> filtered;
        if (!_quickFilterActive)
        {
            filtered = _allRows;
        }
        else
        {
            List<RevisionRow> matches = [];
            foreach (RevisionRow row in _allRows)
            {
                if (Matches(row, query))
                {
                    matches.Add(row);
                }
            }

            filtered = matches;
        }

        // The artificial rows ("Working directory" / "Commit index") are the first
        // items of the very same list, so they take part in the grid's columns,
        // graph and selection model.
        _rows = BuildDisplayRows(filtered);

        // The row set decides whether the authored highlight means anything, so it is
        // re-judged here and not only when the selection moves.
        RecomputeAuthoredHighlight();

        // The graph column width changes with the filter state; rebuild the
        // header so its columns stay aligned with the (re-templated) rows.
        _headerHost.Content = BuildHeader();

        // Reassign the source so every visible row is rebuilt against the current
        // filter/graph state, carrying the viewport across when the rebuild was not
        // the user's doing.
        RebindRows(preserveViewport);

        if (_quickFilterActive)
        {
            // One format string with placeholders, never a concatenation: a
            // translator can reorder every part, and the "commits" noun is looked
            // up separately so it agrees with the catalogue.
            _status.Text = string.Format(
                T("{0}  —  {1} of {2} {3}  ({4}; filter: \"{5}\")"),
                _repoLabel, filtered.Count, LoadedCountText, CommitsNoun, ScopeLabel, query);
        }
        else
        {
            _status.Text = _allRows.Count > 0
                ? string.Format(T("{0}  —  {1} {2}  ({3})"),
                    _repoLabel, LoadedCountText, CommitsNoun, ScopeLabel)
                : GitFilterActive
                    // An empty result under a filter is a legitimate answer, not an
                    // unloaded repository — say so, or the ✕ looks like a bug.
                    ? string.Format(T("{0}  —  no commit matches the filter"), _repoLabel)
                    : T("No repository loaded.");
        }

        // The GIT filter is reported separately from the quick box, because it is a
        // different thing: it changed WHICH commits git walked, so the counts above
        // are counts WITHIN the filtered history. Reset it with the ✕ next to the
        // "Filter…" button.
        //
        // In file-history mode the criterion IS the tab's subject, not something the
        // user set and can reset: it is named once, as the file, instead of being
        // repeated as "git filter: path: …".
        if (_fileHistoryMode)
        {
            _status.Text = string.Format(
                T("{0}  —  {1}"),
                _fileHistoryFile,
                _status.Text);
        }
        else if (GitFilterActive && _repoLabel.Length > 0)
        {
            _status.Text = string.Format(
                T("{0}  —  git filter: {1}"),
                _status.Text,
                _gitFilter.Summarize(T));
        }

        UpdateFilterChrome();

        // The line is ellipsized when the pane is narrow; keep it fully readable.
        ToolTip.SetTip(_status, _status.Text);

        _ = wasFiltering; // (state kept for clarity; no extra action needed)
    }

    // The plural noun used by the status line's commit count.
    private static string CommitsNoun => T("CommitInfo/_plusCommits.Text", "commits");

    // How many commits are loaded right now. A trailing "+" says the history goes
    // further and only has not been walked yet — so the number can never be read as
    // "this repository has N commits" the way the old fixed 200-commit cap was.
    private string LoadedCountText
    {
        get
        {
            string count = _allRows.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            return _hasMore ? string.Format("{0}+", count) : count;
        }
    }

    private static bool Matches(RevisionRow row, string query)
        => row.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Hash.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.ShortHash.Contains(query, StringComparison.OrdinalIgnoreCase);

    // Re-applies the current "View" options (date mode / visible columns) without
    // re-running git: it rebuilds the header and re-templates the currently shown
    // rows (respecting any active filter, since _rows is the filtered subset).
    private void RefreshView()
    {
        _headerHost.Content = BuildHeader();

        // The row SET is unchanged — only how each row is drawn — so the user must
        // keep looking at the same commits, still selected.
        RebindRows(preserveViewport: true);
    }

    /// <summary>
    ///  <see cref="RefreshView"/> for a setting that changes the GRAPH rather than the
    ///  painting of a row: the lane and colour pass runs before any row is built, so a
    ///  rebind alone would redraw the same segments and the change would only appear at
    ///  the next reload. Same row set, same viewport, same selection — the geometry is
    ///  simply recomputed first.
    /// </summary>
    private void RefreshGraph()
    {
        // The lane pass is run HERE and not left to BuildDisplayRows: that one only
        // re-runs it when it has artificial rows to splice in, and returns the list
        // untouched otherwise — which is why toggling the setting on a clean working
        // tree changed nothing at all until the next reload (seen on screen, and the
        // reason this method exists rather than a plain RefreshView).
        List<RevisionRow> commits = [.. _rows.Skip(_artificialCount)];
        _rows = BuildDisplayRows(
            RevisionService.BuildRevisionGraph(commits, ColourPerBranch, ColourAtRemoteMirror));
        RefreshView();
    }

    // Recomputes, from the loaded rows, the reachability sets used by the two
    // render-time "View" highlight styles. Best-effort and bounded to the loaded
    // window: if neither the anchor nor HEAD is among the loaded rows the sets stay
    // empty and both styles become no-ops. Uses only ParentHashes already carried
    // on each row — no git.
    //
    // The "relatives" are the highlight anchor plus its ANCESTORS, and nothing
    // else. That is what the original does: RevisionGraph.HighlightBranch() clears
    // every IsRelative flag and calls MakeRelative() on the anchor, which walks the
    // start segments (i.e. the parents) transitively. Descendants of the anchor stay
    // non-relative, so only the path leading UP TO the anchor keeps its colours —
    // the port previously also walked the children, which coloured whole branches
    // that the Windows grid draws gray.
    private void ComputeReachability()
    {
        _headRelatives = [];
        _currentBranchLine = [];

        // Index by hash for O(1) parent lookups while walking.
        Dictionary<string, RevisionRow> byHash = new(_allRows.Count);
        RevisionRow? head = null;
        foreach (RevisionRow row in _allRows)
        {
            byHash[row.Hash] = row;
            if (head is null && row.IsHead)
            {
                head = row;
            }
        }

        // The anchor is the ALT+CLICKed commit while it is still loaded, HEAD
        // otherwise (also the state right after every refresh).
        string? anchor = _highlightAnchor is not null && byHash.ContainsKey(_highlightAnchor)
            ? _highlightAnchor
            : head?.Hash;

        if (anchor is null)
        {
            return;
        }

        HashSet<string> relatives = [anchor];
        Walk(anchor, relatives, h => byHash.TryGetValue(h, out RevisionRow? r) ? r.ParentHashes : []);
        _headRelatives = relatives;

        if (head is null)
        {
            return;
        }

        // Current-branch line: HEAD's first-parent chain (approximates the branch).
        HashSet<string> line = [];
        string? cursor = head.Hash;
        while (cursor is not null && line.Add(cursor) && byHash.TryGetValue(cursor, out RevisionRow? cur))
        {
            cursor = cur.ParentHashes.Count > 0 ? cur.ParentHashes[0] : null;
        }

        _currentBranchLine = line;
    }

    // Iterative transitive walk over a neighbour function, accumulating into seen.
    private static void Walk(string start, HashSet<string> seen, Func<string, IReadOnlyList<string>> neighbours)
    {
        Stack<string> stack = new();
        stack.Push(start);
        while (stack.Count > 0)
        {
            string node = stack.Pop();
            foreach (string next in neighbours(node))
            {
                if (seen.Add(next))
                {
                    stack.Push(next);
                }
            }
        }
    }

    // Decides, for every display row, which graph segments and which node dot belong
    // to a RELATIVE revision, so the graph cell can draw the rest gray — the
    // per-segment decision the original makes in
    // GraphRenderer.GetBrushForLaneInfo(laneInfo, segment.Child.IsRelative, …).
    //
    // Upstream a segment knows its child (the newer end) and asks that child for its
    // flag. Here the geometry produced by RevisionService only carries lanes, so the
    // flag is carried DOWN the lanes instead, which is equivalent: a lane's segments
    // belong to the commit that opened the lane, i.e. the node of the row above.
    //  - a bottom half starting on the node lane is an edge from THIS commit to one
    //    of its parents => it takes this row's node flag;
    //  - every other half continues a lane opened further up => it takes the flag the
    //    lane arrived with;
    //  - the flags a row hands to the next row are those of its bottom halves.
    // The artificial rows (working directory / commit index) are the checked-out
    // working state, hence always relative, which also colours the connector lane
    // that runs from them down to HEAD.
    private void ComputeGraphRelatives()
    {
        _graphRelatives = new List<(bool, bool[])>(_rows.Count);

        // Which EDGES crossing the top edge of the current row are relative, keyed by
        // ColorLane.
        //
        // Keyed by edge identity and not by lane index, which is what this used to do
        // and what made it wrong: a lane is only a column and BuildGraph recycles it —
        // it frees the column when two branches converge and FirstFree hands the same
        // column to an unrelated branch further down. The flag of the branch that left
        // was then read by the branch that arrived, so an unrelated line kept the
        // colour of an ancestor of HEAD. ColorLane already exists for exactly this
        // reason (M75 gave the graph an edge identity so colours would stop following
        // recycled columns); the relatives pass simply had not been moved onto it.
        HashSet<int> incoming = [];

        for (int i = 0; i < _rows.Count; i++)
        {
            RevisionRow row = _rows[i];
            bool artificial = IsArtificial(row);
            bool nodeRelative = artificial
                || _headRelatives.Count == 0
                || _headRelatives.Contains(row.Hash);
            int nodeLane = row.NodeLane;
            IReadOnlyList<RevisionGraphSegment> segments = row.GraphSegments;

            bool[] flags = new bool[segments.Count];
            HashSet<int> outgoing = [];

            for (int s = 0; s < segments.Count; s++)
            {
                RevisionGraphSegment seg = segments[s];

                // An edge that LEAVES this node — its lower half starts at the node
                // lane — is relative because the node is; anything else inherits the
                // flag its own edge arrived with.
                bool leavesNode = seg.FromY >= 0.5 && (int)Math.Round(seg.FromLane) == nodeLane;
                bool relative = leavesNode
                    ? nodeRelative || incoming.Contains(seg.ColorLane)
                    : incoming.Contains(seg.ColorLane);

                flags[s] = relative;

                // Whatever reaches the bottom edge is what the next row inherits —
                // full-height segments included, which is why this asks about ToY
                // rather than about the half the segment lives in.
                if (relative && seg.ToY >= 1.0)
                {
                    outgoing.Add(seg.ColorLane);
                }
            }

            _graphRelatives.Add((nodeRelative, flags));
            incoming = outgoing;
        }
    }

    // The graph flags of one display row, or null when they are unavailable (row
    // index out of the computed range, e.g. a rebind still in flight): the caller
    // then draws everything in colour, never throwing from a render path.
    private (bool Node, bool[] Segments)? GraphRelatives(int displayIndex)
        => displayIndex >= 0 && displayIndex < _graphRelatives.Count
            ? _graphRelatives[displayIndex]
            : null;

    // Re-anchors the highlighting on the given commit (ALT+CLICK) and redraws. A
    // hash equal to the current anchor is a no-op, so repeated Alt+clicks on the
    // same row cost nothing.
    private void HighlightBranchOf(string? hash)
    {
        if (_highlightAnchor == hash)
        {
            return;
        }

        _highlightAnchor = hash;
        ComputeReachability();
        RefreshView();
    }

    // Re-evaluates the author emphasis from the current selection (upstream's
    // AuthorRevisionHighlighting): the author of the selected revision is drawn bold
    // on EVERY row that shares it. Only an actual change costs a re-template, so
    // arrowing through the commits of one person is free.
    //
    // An artificial row (working directory / commit index) has no author, so it
    // clears the emphasis rather than blanking every row.
    /// <summary>
    ///  The background of one row: the alternating stripe, with the authored tint
    ///  COMPOSED on top of it rather than replacing it.
    ///
    ///  <para>Upstream replaces (<c>RevisionDataGridView.GetBackground</c>: the authored
    ///  colour wins over the stripe, so a highlighted run reads as one block), and so
    ///  did this port. Reported by the user with a screenshot: selecting a commit in a
    ///  repository they wrote most of turns a dozen consecutive rows into one flat
    ///  slab — the highlight arrives and the row separation leaves with it, which is a
    ///  bad trade when the run is long. Composing keeps both: the tint says "same
    ///  author", the stripe still says where each row ends.</para>
    /// </summary>
    private IBrush RowBackground(RevisionRow row, int index)
    {
        // App.Panel for both when the stripe is off: it is the plain row surface, and
        // the alternate is the one that carries the tint.
        bool alternate = _gridPrefs.GraphDrawAlternateBackColor && (index & 1) == 1;

        if (_authoredHighlightIsUseful
            && !IsArtificial(row)
            && string.Equals(row.Author, _highlightedAuthor, StringComparison.Ordinal))
        {
            return AuthoredBackground(alternate);
        }

        return alternate ? B("App.PanelAlt") : B("App.Panel");
    }

    // The text of a row's tooltip: what the truncated columns cannot say.
    private static string RowTooltip(RevisionRow row)
    {
        string author = string.IsNullOrEmpty(row.AuthorEmail)
            ? row.Author
            : $"{row.Author} <{row.AuthorEmail}>";

        return $"{row.Subject}\n\n{author}\n{row.Hash}";
    }

    /// <summary>
    ///  The authored-row tint: the row's own base nudged towards <c>App.AuthoredTint</c>,
    ///  a violet that belongs to no other state.
    ///
    ///  <para><b>It used to lean on the accent, and that was wrong twice over.</b> The
    ///  accent is blue, the selection fill is blue and <c>App.HoverRow</c> is a blue-grey:
    ///  three of the six things a row background can mean were the same hue, and once
    ///  M162 split the tint in two to keep the stripe, the hover landed between them.
    ///  Measured in CIE76 over the four palettes, the hovered row was ΔE 2.8–8.1 from an
    ///  authored row — at 2.8 (dark classic) literally the same colour, which is how a
    ///  user ends up asking why they cannot see which row is selected: the row under
    ///  their pointer looked exactly like the fifteen tinted ones around it.</para>
    ///
    ///  <para>Off the blue axis the same measurement gives ΔE 7.1–12.1 from the hover
    ///  and 10.2–11.7 from the plain row, while the stripe inside the tint keeps ΔE
    ///  3.2–6.0 — the same order as the stripe outside it — and the selection still
    ///  stands 42–89 away from everything. Text on the tint stays above 7.4:1.</para>
    /// </summary>
    /// <param name="alternate">
    ///  Whether this is a striped row. The tint is mixed into the row's OWN base rather
    ///  than into one shared colour, which is what keeps the stripe legible under it:
    ///  both bases move the same tenth of the way towards the accent, so the difference
    ///  between them survives at nine tenths of its strength.
    /// </param>
    private IBrush AuthoredBackground(bool alternate)
    {
        if (alternate)
        {
            return _authoredAltBrush ??= Tinted("App.PanelAlt");
        }

        return _authoredBrush ??= Tinted("App.Panel");
    }

    private static IBrush Tinted(string baseKey)
    {
        Color background = B(baseKey) is ISolidColorBrush b ? b.Color : Colors.Black;
        Color tint = B("App.AuthoredTint") is ISolidColorBrush a ? a.Color : Color.FromRgb(0x9B, 0x8F, 0xD6);

        // 0.14, not the 0.10 the accent used: violet is a quieter hue against these
        // greys, and at a tenth it read as a smudge rather than as a colour.
        return new SolidColorBrush(Mix(background, tint, 0.14));
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    private void UpdateAuthorHighlight()
    {
        string author = _list.SelectedItem is RevisionRow row && !IsArtificial(row)
            ? row.Author
            : string.Empty;

        if (string.Equals(author, _highlightedAuthor, StringComparison.Ordinal))
        {
            return;
        }

        _highlightedAuthor = author;
        RecomputeAuthoredHighlight();
        RefreshView();
    }

    /// <summary>
    ///  Decides whether the authored highlight says anything at all. In a repository
    ///  with one author it would tint EVERY row, which is not a highlight — it is a
    ///  second background colour applied to the whole grid, and it reads as a selection.
    ///  Upstream never hits this because it highlights on Windows-grey; the port has to
    ///  say it out loud.
    /// </summary>
    private void RecomputeAuthoredHighlight()
    {
        _authoredHighlightIsUseful = false;
        if (!_gridPrefs.HighlightAuthoredRevisions || _highlightedAuthor.Length == 0 || _rows.Count == 0)
        {
            return;
        }

        int matching = 0;
        int total = 0;
        foreach (RevisionRow row in _rows)
        {
            if (IsArtificial(row))
            {
                continue;
            }

            total++;
            if (string.Equals(row.Author, _highlightedAuthor, StringComparison.Ordinal))
            {
                matching++;
            }
        }

        // Anything above nine rows in ten is "the whole grid", and marking the whole
        // grid marks nothing.
        _authoredHighlightIsUseful = total > 0 && matching * 10 < total * 9;
    }

    // "View" menu: which refs the log walks (remotes / tags / stashes), the walk
    // order (date vs topological), and the two render-time highlight styles. The
    // first four reload via Reload() (preserving filter/notes/date/columns/DAG);
    // the last two only re-template via RefreshView().
    private Flyout BuildViewFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 210 };

        panel.Children.Add(SectionLabel(T("Show in log")));

        panel.Children.Add(OptionCheck(
            OptShowRemoteBranches,
            T("RevisionGrid/ShowRemoteBranches.Text", "Remote branches"),
            ToggleShowRemoteBranches));
        panel.Children.Add(OptionCheck(
            OptShowTags,
            T("TranslatedStrings/_tagsText.Text", "Tags"),
            ToggleShowTags));
        panel.Children.Add(OptionCheck(
            OptShowStashes,
            T("TranslatedStrings/_stashesText.Text", "Stashes"),
            ToggleShowStashes));
        // The working directory and the index are the pending work of the REPOSITORY;
        // a file's log never contains them, and nothing feeds their counts there
        // (LoadFileHistory turns them off), so the entry is left out rather than
        // offered as a toggle that does nothing.
        if (!_fileHistoryMode)
        {
            panel.Children.Add(OptionCheck(
                OptShowArtificialCommits,
                T("Artificial commits"),
                ToggleShowArtificialCommits));
        }

        panel.Children.Add(OptionCheck(
            OptShowGitNotes,
            T("RevisionGridControl/showGitNotesToolStripMenuItem.Text", "Git notes"),
            ToggleShowGitNotes));

        panel.Children.Add(SectionLabel(T("RevisionGrid/SortingToolStripMenuItem.Text", "Order")));
        panel.Children.Add(OptionRadio(OptOrderDefault, T("Date order"), "revOrder", SetDefaultSort));
        panel.Children.Add(OptionRadio(OptOrderAuthorDate, T("Author date order"), "revOrder", SetAuthorDateSort));
        panel.Children.Add(OptionRadio(OptOrderTopo, T("Topo-order"), "revOrder", SetTopoSort));

        // How much history one page of the incremental walk loads — the port's
        // counterpart of the original's MaxRevisionGraphCommits setting. Changing it
        // restarts the walk at the new page size; the rest of the history stays one
        // scroll (or one footer click) away either way.
        panel.Children.Add(SectionLabel(T("Commits per page")));
        foreach (int size in new[] { 200, 500, 1000, 2000 })
        {
            int value = size;
            RadioButton option = MakeRadio(
                value.ToString(System.Globalization.CultureInfo.CurrentCulture),
                "revPageSize",
                _pageSize == value);
            option.IsCheckedChanged += (_, _) =>
            {
                if (option.IsChecked == true)
                {
                    SetPageSize(value);
                }
            };
            panel.Children.Add(option);
        }

        panel.Children.Add(SectionLabel(T("Highlighting")));

        CheckBox nonRelatives = OptionCheck(
            OptDrawNonRelativesGray,
            T("RevisionGrid/drawNonrelativesGrayToolStripMenuItem.Text", "Draw non-relatives gray"),
            ToggleDrawNonRelativesGray);
        ToolTip.SetTip(
            nonRelatives,
            T("Alt+click a commit to highlight the history leading to it"));

        CheckBox highlight = OptionCheck(
            OptHighlightCurrentBranch,
            T("Highlight current branch"),
            ToggleHighlightCurrentBranch);

        // Upstream's ToggleHighlightSelectedBranch has no "back to HEAD" counterpart
        // other than a refresh; this puts the anchor back on the checked-out commit
        // without re-walking the history.
        Button anchorToHead = MakeMenuButton(T("Highlight current branch's history"));
        anchorToHead.Margin = new Thickness(0, 3, 0, 0);
        anchorToHead.HorizontalAlignment = HorizontalAlignment.Stretch;
        anchorToHead.Click += (_, _) => HighlightBranchOf(null);

        panel.Children.Add(nonRelatives);
        panel.Children.Add(highlight);
        panel.Children.Add(anchorToHead);

        return new Flyout
        {
            Content = panel,
        };
    }

    // Formats a row's Date cell from the selected source (commit vs author) and
    // mode (absolute vs relative). Artificial/empty timestamps render as blank.
    private string FormatDate(RevisionRow row)
    {
        DateTime dt = _dateSource == DateSource.Author ? row.AuthorDate : row.CommitDate;
        if (dt == DateTime.MaxValue || dt == DateTime.MinValue)
        {
            return string.Empty;
        }

        return _relativeDates ? Relative(dt) : dt.ToString("yyyy-MM-dd HH:mm");
    }

    // A compact human "… ago" rendering (dates are LocalDateTime, so compare to now).
    //
    // The English wording and the plural rule both come from the catalogue: the
    // upstream TranslatedStrings entries are "{0} {1:minute|minutes} ago", i.e. a
    // count placeholder plus a SmartFormat plural list. The port cannot pull in
    // SmartFormat (it is not part of the Linux build's dependency set), so
    // Pluralize below expands that one construct itself — which is enough for
    // every "… ago" unit in every shipped catalogue.
    private static string Relative(DateTime dt)
    {
        TimeSpan span = DateTime.Now - dt;
        if (span.Ticks < 0)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return T("just now");
        }

        if (span.TotalMinutes < 60)
        {
            return Ago("TranslatedStrings/_minutesAgo.Text", "{0} {1:minute|minutes} ago", (int)span.TotalMinutes);
        }

        if (span.TotalHours < 24)
        {
            return Ago("TranslatedStrings/_hoursAgo.Text", "{0} {1:hour|hours} ago", (int)span.TotalHours);
        }

        if (span.TotalDays < 30)
        {
            return Ago("TranslatedStrings/_daysAgo.Text", "{0} {1:day|days} ago", (int)span.TotalDays);
        }

        if (span.TotalDays < 365)
        {
            return Ago("TranslatedStrings/_monthsAgo.Text", "{0} {1:month|months} ago", (int)(span.TotalDays / 30));
        }

        return Ago("TranslatedStrings/_yearsAgo.Text", "{0} {1:year|years} ago", (int)(span.TotalDays / 365));
    }

    // Renders one "{0} {1:singular|plural} ago" catalogue entry for a count.
    private static string Ago(string key, string english, int value)
        => Pluralize(T(key, english), value);

    // Expands "{0}" (the count) and "{1:singular|plural}" (the noun) of the
    // upstream relative-date format. Some catalogues separate the two forms with
    // a backslash instead of a pipe, and some give a single invariant form, so
    // both are accepted; anything unrecognised is left verbatim rather than
    // throwing inside a cell renderer.
    private static string Pluralize(string format, int value)
    {
        string result = Regex.Replace(
            format,
            @"\{1:([^}]*)\}",
            m =>
            {
                string[] forms = m.Groups[1].Value.Split('|', '\\');
                if (forms.Length == 0)
                {
                    return m.Value;
                }

                return value == 1 ? forms[0] : forms[^1];
            });

        return result.Replace("{0}", value.ToString(System.Globalization.CultureInfo.CurrentCulture),
            StringComparison.Ordinal);
    }

    // A small compact toolbar button (styled from App.* brushes) used for the
    // Date and Columns dropdown menus next to the filter box.
    /// <summary>
    ///  A button of the grid's own bar (Go to, Branches, View, Date, Columns, Filter…).
    ///
    ///  <para>Flat, like every other bar in the app: the main toolbar above it and the
    ///  commit dialog's pane toolbars both draw their buttons this way
    ///  (<see cref="Theming.BarButtonStyles"/>), and this strip was the last one still
    ///  framing each button in a 1px outline — a row of boxes under a row of flat
    ///  buttons.</para>
    ///
    ///  <para>The outline used to be argued as the affordance, because the resting fill
    ///  had no contrast against the strip. What replaces it is the same thing that
    ///  replaces it on the toolbar: the label carries the button, and the fill appears
    ///  under the pointer. That is a deliberate trade — one look for every bar, rather
    ///  than one bar that measures 3:1 on its own and looks foreign next to the others.</para>
    /// </summary>
    private static Button MakeBarButton(string text)
    {
        Button button = new()
        {
            Content = text,
            Background = Brushes.Transparent,
            Foreground = B("App.Text"),
            BorderThickness = new Thickness(0),
            Padding = StyleDensity.BarButtonWide,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.Classes.Add(Theming.BarButtonStyles.Class);
        return button;
    }

    // Date menu: choose the timestamp source (commit/author) and the display mode
    // (absolute/relative). Selections apply live.
    private Flyout BuildDateFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 150 };

        panel.Children.Add(SectionLabel(T("Date shown")));
        panel.Children.Add(OptionRadio(
            OptCommitDate, T("Commit date"), "revDateSrc", () => SetAuthorDate(false)));
        panel.Children.Add(OptionRadio(
            OptAuthorDate, T("Author date"), "revDateSrc", () => SetAuthorDate(true)));

        panel.Children.Add(SectionLabel(T("Format")));
        panel.Children.Add(OptionRadio(
            OptAbsoluteDate, T("Absolute"), "revDateFmt", () => SetRelativeDate(false)));
        panel.Children.Add(OptionRadio(
            OptRelativeDate, T("Relative"), "revDateFmt", () => SetRelativeDate(true)));

        return new Flyout
        {
            Content = panel,
        };
    }

    // Columns menu: toggle visibility of the Author, Date and Commit-ID columns.
    private Flyout BuildColumnsFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 140 };
        panel.Children.Add(SectionLabel(T("Show columns")));

        // Same order as the columns themselves (and as the original's Columns group):
        // graph, avatar, author name, date, SHA-1.
        panel.Children.Add(OptionCheck(
            OptGraphColumn, T("Revision graph"), ToggleRevisionGraphColumn));
        panel.Children.Add(OptionCheck(
            OptAvatarColumn, T("Avatar"), ToggleAuthorAvatarColumn));
        panel.Children.Add(OptionCheck(
            OptAuthorColumn, T("TranslatedStrings/_author.Text", "Author"), ToggleAuthorNameColumn));
        panel.Children.Add(OptionCheck(
            OptDateColumn, T("TranslatedStrings/_dateText.Text", "Date"), ToggleDateColumn));
        panel.Children.Add(OptionCheck(
            OptIdColumn, T("Commit ID"), ToggleObjectIdColumn));

        return new Flyout
        {
            Content = panel,
        };
    }

    // Branches menu: choose which refs the log walks. Each selection re-runs the
    // log via Reload(), preserving the text filter, git-notes, date mode, column
    // toggles and the (service-rebuilt) DAG graph.
    private Flyout BuildBranchesFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 190 };

        panel.Children.Add(SectionLabel(T("Branches shown")));

        panel.Children.Add(OptionRadio(
            OptShowAllBranches,
            T("FormBrowse/tssbtnShowBranches.Text", "All branches"),
            "revBranchScope",
            ShowAllBranches));
        panel.Children.Add(OptionRadio(
            OptShowCurrentBranchOnly,
            T("RevisionGrid/ShowCurrentBranchOnly.Text", "Current branch only"),
            "revBranchScope",
            ShowCurrentBranchOnly));
        panel.Children.Add(OptionRadio(
            OptShowFilteredBranches,
            T("RevisionGrid/ShowFilteredBranches.Text", "Filtered branches"),
            "revBranchScope",
            ShowFilteredBranches));

        // --- the ref picker that gives "Filtered" its meaning --------------------
        panel.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 2) });
        panel.Children.Add(SectionLabel(T("Refs walked when filtered")));

        TextBox query = new()
        {
            Text = _refPickerQuery,
            Watermark = T("Find a ref…"),
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            // Editable box inside the ref picker flyout: outline is the only boundary.
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(5, 2, 4, 2),
        };
        query.TextChanged += (_, _) =>
        {
            _refPickerQuery = query.Text ?? string.Empty;

            // Only the LIST is rebuilt, and only from the box's own handler — the
            // flyout's other controls (and the pointer) stay where they are.
            PopulateRefPicker();
        };
        panel.Children.Add(query);

        // Kind toggles: which families of ref the list offers, mirroring upstream's
        // Local / Remote / Tag selector next to its branch combo.
        StackPanel kinds = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 2, 0, 2),
        };
        kinds.Children.Add(RefKindCheck(T("Local"), _refKindLocal, v => _refKindLocal = v));
        kinds.Children.Add(RefKindCheck(T("Remote"), _refKindRemote, v => _refKindRemote = v));
        kinds.Children.Add(RefKindCheck(T("TranslatedStrings/_tags.Text", "Tags"), _refKindTags, v => _refKindTags = v));
        panel.Children.Add(kinds);

        _refPickerHost = new StackPanel { Spacing = 2 };
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 240,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _refPickerHost,
        });

        _refPickerSummary = new TextBlock
        {
            Foreground = B("App.TextDim"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        };
        panel.Children.Add(_refPickerSummary);

        Button clear = MakeBarButton(T("Clear ref selection"));
        clear.Margin = new Thickness(0, 3, 0, 0);
        clear.Click += (_, _) => SetFilteredRefs([]);
        panel.Children.Add(clear);

        PopulateRefPicker();

        return new Flyout
        {
            Content = panel,
        };
    }

    // One of the picker's three kind toggles. Flipping it only re-lists the refs;
    // the walked set (and therefore the log) is untouched.
    private CheckBox RefKindCheck(string text, bool value, Action<bool> assign)
    {
        CheckBox box = MakeCheck(text, value);
        box.FontSize = 11;
        box.IsCheckedChanged += (_, _) =>
        {
            assign(box.IsChecked == true);
            PopulateRefPicker();
        };

        return box;
    }

    /// <summary>
    ///  The refs the "Filtered" scope walks. Empty means "none chosen", in which
    ///  case the walk falls back to HEAD (and the picker says so).
    /// </summary>
    public IReadOnlyList<string> FilteredRefs => _filteredRefs;

    /// <summary>
    ///  Replaces the set of refs walked under <see cref="BranchScope.Filtered"/>.
    ///
    ///  <para>Choosing refs IS choosing the filtered scope, as upstream's branch
    ///  combo does: a non-empty selection made while another scope is active
    ///  switches to <see cref="BranchScope.Filtered"/>, so the choice takes effect
    ///  where the user made it instead of silently waiting for a second click.</para>
    /// </summary>
    public void SetFilteredRefs(IReadOnlyList<string>? refs)
    {
        List<string> wanted = refs is null
            ? []
            : refs.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();

        if (wanted.SequenceEqual(_filteredRefs, StringComparer.Ordinal))
        {
            return;
        }

        _filteredRefs = wanted;
        SyncRefPickerChecks();

        if (wanted.Count > 0 && _branchScope != BranchScope.Filtered)
        {
            // SetBranchScope reloads and notifies; nothing further to do here.
            SetBranchScope(BranchScope.Filtered);
            return;
        }

        // The walked set changed under the scope that uses it.
        if (_branchScope == BranchScope.Filtered)
        {
            Reload();
        }

        OptionsChanged();
    }

    // Adds/removes one ref from the walked set.
    private void ToggleFilteredRef(string name, bool wanted)
    {
        bool has = _filteredRefs.Contains(name, StringComparer.Ordinal);
        if (has == wanted)
        {
            return;
        }

        List<string> next = [.. _filteredRefs];
        if (wanted)
        {
            next.Add(name);
        }
        else
        {
            next.RemoveAll(r => string.Equals(r, name, StringComparison.Ordinal));
        }

        SetFilteredRefs(next);
    }

    // Feeds the picker the repository's refs (from RefreshRefContext, off the UI
    // thread originally). Rebuilds the list only when the ref SET actually changed —
    // a mere selection change must not rebuild a flyout that may be open.
    private void SetRefCatalogue(IReadOnlyList<(string Name, char Kind)> catalogue)
    {
        if (catalogue.Count == _refCatalogue.Count
            && catalogue.Select(r => $"{r.Kind}{r.Name}")
                .SequenceEqual(_refCatalogue.Select(r => $"{r.Kind}{r.Name}"), StringComparer.Ordinal))
        {
            return;
        }

        _refCatalogue = catalogue;

        // A ref that no longer exists cannot be walked; dropping it here keeps the
        // status line honest about what the filter actually is.
        List<string> alive = _filteredRefs
            .Where(r => catalogue.Any(c => string.Equals(c.Name, r, StringComparison.Ordinal)))
            .ToList();
        if (!alive.SequenceEqual(_filteredRefs, StringComparer.Ordinal))
        {
            _filteredRefs = alive;
        }

        PopulateRefPicker();
    }

    // (Re)builds the checkbox list from the catalogue, honouring the narrowing box
    // and the kind toggles. Grouped by kind, in the order local / remote / tags.
    private void PopulateRefPicker()
    {
        if (_refPickerHost is null)
        {
            return;
        }

        _refPickerChecks.Clear();
        _refPickerHost.Children.Clear();

        string query = _refPickerQuery.Trim();
        int shown = 0;

        foreach ((char kind, string heading, bool enabled) in new[]
        {
            ('b', T("Local branches"), _refKindLocal),
            ('r', T("Remote branches"), _refKindRemote),
            ('t', T("TranslatedStrings/_tags.Text", "Tags"), _refKindTags),
        })
        {
            if (!enabled)
            {
                continue;
            }

            List<string> group = _refCatalogue
                .Where(r => r.Kind == kind
                    && (query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.Name)
                .ToList();
            if (group.Count == 0)
            {
                continue;
            }

            _refPickerHost.Children.Add(SectionLabel(heading));
            foreach (string name in group)
            {
                CheckBox box = MakeCheck(name, _filteredRefs.Contains(name, StringComparer.Ordinal));
                box.FontSize = 11;
                box.IsCheckedChanged += (_, _) =>
                {
                    if (_syncingOptions)
                    {
                        return;
                    }

                    ToggleFilteredRef(name, box.IsChecked == true);
                };

                _refPickerChecks[name] = box;
                _refPickerHost.Children.Add(box);
                shown++;
            }
        }

        if (shown == 0)
        {
            _refPickerHost.Children.Add(new TextBlock
            {
                Text = _refCatalogue.Count == 0
                    ? T("No refs loaded yet.")
                    : T("No ref matches the current narrowing."),
                Foreground = B("App.TextDim"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        UpdateRefPickerSummary();
    }

    // Brings the picker's check marks back in line with _filteredRefs without
    // rebuilding anything (so it is safe while the flyout is open).
    private void SyncRefPickerChecks()
    {
        _syncingOptions = true;
        try
        {
            foreach ((string name, CheckBox box) in _refPickerChecks)
            {
                bool wanted = _filteredRefs.Contains(name, StringComparer.Ordinal);
                if (box.IsChecked != wanted)
                {
                    box.IsChecked = wanted;
                }
            }
        }
        finally
        {
            _syncingOptions = false;
        }

        UpdateRefPickerSummary();
    }

    private void UpdateRefPickerSummary()
    {
        if (_refPickerSummary is null)
        {
            return;
        }

        _refPickerSummary.Text = _filteredRefs.Count == 0
            ? T("No ref selected — \"Filtered branches\" walks HEAD.")
            : string.Format(T("Walking {0}"), string.Join(", ", _filteredRefs));
    }

    /// <summary>
    ///  Ends the walk at <paramref name="hash"/>: that commit and everything it reaches
    ///  are left out, so with the current-branch scope the grid shows the commits of
    ///  this branch alone (<c>git log HEAD ^&lt;merge-base&gt;</c>). <see langword="null"/>
    ///  removes the bound.
    ///
    ///  <para>For a grid opened to answer one question — the commit picker of
    ///  <see cref="ChooseCommitDialog"/> is the first caller — and deliberately NOT a
    ///  view option: it has no menu entry, is not persisted, and no ref name is involved,
    ///  so unlike the filtered-ref set it cannot be dropped when the ref catalogue
    ///  arrives (which is exactly what happened when this was first tried by passing
    ///  <c>^&lt;hash&gt;</c> as a fake ref: the first walk was bounded, the next one
    ///  silently was not).</para>
    /// </summary>
    public void SetWalkBound(string? hash)
    {
        string? wanted = string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();
        if (string.Equals(wanted, _walkBound, StringComparison.Ordinal))
        {
            return;
        }

        _walkBound = wanted;
        Reload();
    }

    /// <summary>
    ///  Sets the branch scope (All branches / current branch / filtered) and
    ///  re-runs the log. Shared by the grid's own header menu and by the host
    ///  toolbar's scope dropdown. A no-op re-selection of the same scope does
    ///  nothing.
    /// </summary>
    public void SetBranchScope(BranchScope scope)
    {
        if (_branchScope == scope)
        {
            return;
        }

        _branchScope = scope;
        Reload();
        OptionsChanged();
    }

    // =========================================================================
    //  The grid's "Navigate" / "View" command surface
    // =========================================================================
    //
    // Upstream, the Navigate and View menus of FormBrowse are not wired to the grid
    // item by item: RevisionGridMenuCommands builds a list of MenuCommand records,
    // each with a NAME, an ExecuteAction and (for the checkable ones) an
    // IsCheckedFunc, and the menu is generated from that list. This port keeps the
    // same indirection: MainMenu builds its items from the ids below and raises ONE
    // event carrying the id, which lands in <see cref="ExecuteMenuCommand"/>; the
    // check marks come from <see cref="ViewOptions"/> and are refreshed whenever
    // <see cref="ViewOptionsChanged"/> fires.
    //
    // The ids are upstream's MenuCommand.Name values wherever one exists, so the two
    // menus can be compared entry by entry.

    // --- Navigate -------------------------------------------------------------
    public const string CmdToggleArtificialAndHead = "ToggleBetweenArtificialAndHeadCommits";
    public const string CmdGoToCurrentRevision = "GotoCurrentRevision";
    public const string CmdGoToCommit = "GotoCommit";
    public const string CmdGoToChildCommit = "GotoChildCommit";
    public const string CmdGoToParentCommit = "GotoParentCommit";
    public const string CmdGoToMergeBase = "GotoMergeBaseCommit";
    public const string CmdNavigateBackward = "NavigateBackward";
    public const string CmdNavigateForward = "NavigateForward";
    public const string CmdQuickSearchHelp = "QuickSearch";
    public const string CmdQuickSearchPrevious = "PrevQuickSearch";
    public const string CmdQuickSearchNext = "NextQuickSearch";

    // --- View: commands (not checkable) ---------------------------------------
    public const string CmdAdvancedFilter = "filterToolStripMenuItem";
    public const string CmdHighlightSelectedBranch = "HighlightSelectedBranch";

    // --- View: checkable toggles ----------------------------------------------
    // Branch scope.
    public const string OptShowAllBranches = "ShowAllBranches";
    public const string OptShowCurrentBranchOnly = "ShowCurrentBranchOnly";
    public const string OptShowFilteredBranches = "ShowFilteredBranches";

    // Highlighting.
    public const string OptDrawNonRelativesGray = "drawNonrelativesGrayToolStripMenuItem";
    public const string OptHighlightCurrentBranch = "HighlightCurrentBranch";

    // Which commits the walk includes.
    public const string OptShowArtificialCommits = "ShowArtificialCommits";
    public const string OptShowStashes = "ShowStashes";
    public const string OptShowGitNotes = "showGitNotesToolStripMenuItem";

    // Grid labels (the ref pills).
    public const string OptShowRemoteBranches = "ShowRemoteBranches";
    public const string OptShowTags = "showTagsToolStripMenuItem";

    // Grid info (what the Date column shows).
    public const string OptAuthorDate = "showAuthorDateToolStripMenuItem";
    public const string OptCommitDate = "showCommitDateToolStripMenuItem";
    public const string OptRelativeDate = "showRelativeDateToolStripMenuItem";
    public const string OptAbsoluteDate = "showAbsoluteDateToolStripMenuItem";

    // Columns.
    public const string OptGraphColumn = "showRevisionGraphColumnToolStripMenuItem";
    public const string OptAvatarColumn = "showAuthorAvatarColumnToolStripMenuItem";
    public const string OptAuthorColumn = "showAuthorNameColumnToolStripMenuItem";
    public const string OptDateColumn = "showDateColumnToolStripMenuItem";
    public const string OptIdColumn = "showIdColumnToolStripMenuItem";

    // Quick-filter field (upstream's "Filter type" dropdown).
    public const string OptQuickFilterMessage = "tsmiCommitFilter";
    public const string OptQuickFilterCommitter = "tsmiCommitterFilter";
    public const string OptQuickFilterAuthor = "tsmiAuthorFilter";
    public const string OptQuickFilterDiff = "tsmiDiffContainsFilter";

    // Which pickaxe the diff field arms. Not a fifth field: a sub-choice of
    // OptQuickFilterDiff, exposed as options so it syncs and persists like the rest.
    public const string OptQuickFilterDiffLiteral = "tsmiDiffContainsFilterLiteral";
    public const string OptQuickFilterDiffRegex = "tsmiDiffContainsFilterRegex";

    // Sorting.
    public const string OptOrderDefault = "GitDefaultOrder";
    public const string OptOrderAuthorDate = "AuthorDateSort";
    public const string OptOrderTopo = "TopoOrder";

    /// <summary>
    ///  Raised whenever any "View" option changes, from whichever surface changed it
    ///  (the grid's own header flyouts, the main menu, a keyboard shortcut). The
    ///  argument is the same snapshot <see cref="ViewOptions"/> returns, so a menu
    ///  can simply re-apply it to its checkable items.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, bool>>? ViewOptionsChanged;

    /// <summary>
    ///  The current state of every checkable "View" option, keyed by the
    ///  <c>Opt…</c> ids above. This view is the single source of truth: neither the
    ///  header flyouts nor the main menu keep a copy.
    /// </summary>
    public IReadOnlyDictionary<string, bool> ViewOptions => new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        [OptShowAllBranches] = _branchScope == BranchScope.AllBranches,
        [OptShowCurrentBranchOnly] = _branchScope == BranchScope.CurrentBranch,
        [OptShowFilteredBranches] = _branchScope == BranchScope.Filtered,
        [OptDrawNonRelativesGray] = _drawNonRelativesGray,
        [OptHighlightCurrentBranch] = _highlightCurrentBranch,
        [OptShowArtificialCommits] = _showArtificial,
        [OptShowStashes] = _showStashes,
        [OptShowGitNotes] = _showGitNotes,
        [OptShowRemoteBranches] = _showRemotes,
        [OptShowTags] = _showTags,
        [OptAuthorDate] = _dateSource == DateSource.Author,
        [OptCommitDate] = _dateSource == DateSource.Commit,
        [OptRelativeDate] = _relativeDates,
        [OptAbsoluteDate] = !_relativeDates,
        [OptGraphColumn] = _showGraph,
        [OptAvatarColumn] = _showAvatar,
        [OptAuthorColumn] = _showAuthor,
        [OptDateColumn] = _showDate,
        [OptIdColumn] = _showHash,
        [OptOrderDefault] = !_topoOrder && !_authorDateSort,
        [OptOrderAuthorDate] = _authorDateSort,
        [OptOrderTopo] = _topoOrder,
        [OptQuickFilterMessage] = _quickFilterField == QuickFilterField.Message,
        [OptQuickFilterCommitter] = _quickFilterField == QuickFilterField.Committer,
        [OptQuickFilterAuthor] = _quickFilterField == QuickFilterField.Author,
        [OptQuickFilterDiff] = _quickFilterField == QuickFilterField.DiffContent,
        [OptQuickFilterDiffLiteral] = !_gitFilter.DiffContentIsRegex,
        [OptQuickFilterDiffRegex] = _gitFilter.DiffContentIsRegex,
    };

    /// <summary>
    ///  The ids of the "View" options worth carrying across sessions, in the order
    ///  they are written to <c>ui-state.json</c>.
    ///
    ///  <para>A CANONICAL subset of <see cref="ViewOptions"/>: the complements
    ///  (<see cref="OptCommitDate"/>, <see cref="OptAbsoluteDate"/>,
    ///  <see cref="OptOrderDefault"/>) are left out, because storing both halves of a
    ///  pair invites a file that says two contradictory things and gives
    ///  <see cref="RestoreViewOptions"/> no way to choose. Each stored id is read as
    ///  "this one is on"; everything else follows from it.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> PersistedOptionIds =
    [
        OptShowAllBranches,
        OptShowCurrentBranchOnly,
        OptShowFilteredBranches,
        OptShowRemoteBranches,
        OptShowTags,
        OptShowStashes,
        OptShowArtificialCommits,
        OptShowGitNotes,
        OptDrawNonRelativesGray,
        OptHighlightCurrentBranch,
        OptGraphColumn,
        OptAvatarColumn,
        OptAuthorColumn,
        OptDateColumn,
        OptIdColumn,
        OptAuthorDate,
        OptRelativeDate,
        OptOrderAuthorDate,
        OptOrderTopo,

        // Only the three non-default quick-filter fields are stored: "commit
        // message" is the fallback, so a file that names none of them restores it —
        // the same canonical-subset rule as the date / order pairs above.
        OptQuickFilterCommitter,
        OptQuickFilterAuthor,
        OptQuickFilterDiff,

        // Same canonical-subset rule: -S (literal) is the fallback, so only the -G
        // half is stored. The advanced filter's own copy of this flag is not
        // persisted at all, which is exactly why it is stored here — otherwise the
        // quick box would come back arming whichever form the last session left.
        OptQuickFilterDiffRegex,
    ];

    /// <summary>
    ///  The persistable slice of <see cref="ViewOptions"/> (see
    ///  <see cref="PersistedOptionIds"/>), ready to be stored by the host.
    ///
    ///  <para>Deliberately a plain snapshot rather than a file write of its own: the
    ///  host owns ONE <c>UiState</c> instance and re-serializes all of it when the
    ///  window closes, so a view that wrote the file behind its back would simply see
    ///  that write overwritten. Same contract as
    ///  <c>RepoObjectsTree.CategoryOrder</c>.</para>
    /// </summary>
    public IReadOnlyDictionary<string, bool> PersistedViewOptions
    {
        get
        {
            IReadOnlyDictionary<string, bool> current = ViewOptions;
            Dictionary<string, bool> snapshot = new(StringComparer.Ordinal);
            foreach (string id in PersistedOptionIds)
            {
                if (current.TryGetValue(id, out bool value))
                {
                    snapshot[id] = value;
                }
            }

            // The refs chosen for the "Filtered branches" scope ride the same bag,
            // one key per ref. UiState documents this dictionary as an open,
            // id-keyed store whose unknown keys are ignored on load, so a ref set
            // survives a restart without a schema of its own — and without this
            // view writing ui-state.json behind the host's back.
            foreach (string name in _filteredRefs)
            {
                snapshot[FilteredRefKeyPrefix + name] = true;
            }

            // The recent-searches list rides the same bag. The rank is part of the
            // KEY, not the dictionary's order: a JSON object's member order is not
            // something to stake the MRU's ordering on.
            for (int i = 0; i < _filterMru.Count; i++)
            {
                snapshot[$"{FilterMruKeyPrefix}{i:D2}:{_filterMru[i]}"] = true;
            }

            return snapshot;
        }
    }

    /// <summary>
    ///  Key prefix under which <see cref="PersistedViewOptions"/> stores one entry
    ///  per ref of the "Filtered branches" selection.
    /// </summary>
    public const string FilteredRefKeyPrefix = "filteredRef:";

    /// <summary>
    ///  Key prefix under which <see cref="PersistedViewOptions"/> stores the recent
    ///  searches, as <c>filterMru:&lt;rank&gt;:&lt;text&gt;</c>.
    /// </summary>
    public const string FilterMruKeyPrefix = "filterMru:";

    /// <summary>How many commits one page of the walk loads (see <see cref="SetPageSize"/>).</summary>
    public int PageSize => _pageSize;

    /// <summary>
    ///  Puts a previously stored set of "View" options (and page size) back, as the
    ///  host restores them at start-up.
    ///
    ///  <para>The backing fields are assigned DIRECTLY instead of replaying the
    ///  public toggles: each of those reloads or re-templates on its own, so
    ///  replaying nineteen of them would cost several <c>git log</c> runs before the
    ///  first page is even on screen. One rebuild of the affected surfaces happens at
    ///  the end instead.</para>
    ///
    ///  <para>Intended to be called BEFORE the repository is loaded, which is the
    ///  cheap case (nothing to reload). It is still safe afterwards: a repository
    ///  already on screen is re-walked once, because the ref scope and the walk order
    ///  are decided by git.</para>
    /// </summary>
    public void RestoreViewOptions(IReadOnlyDictionary<string, bool>? options, int pageSize)
    {
        _pageSize = Math.Max(50, pageSize);

        if (options is { Count: > 0 })
        {
            bool Get(string id, bool fallback) => options.TryGetValue(id, out bool v) ? v : fallback;

            // The scope is one of three: an explicitly stored "current branch" or
            // "filtered" wins, anything else (including a file that stored none of
            // them) means all branches.
            _branchScope = Get(OptShowCurrentBranchOnly, false) ? BranchScope.CurrentBranch
                : Get(OptShowFilteredBranches, false) ? BranchScope.Filtered
                : BranchScope.AllBranches;

            // The ref set that gives the "filtered" scope its meaning. Refs that no
            // longer exist are dropped as soon as the catalogue arrives
            // (SetRefCatalogue), so a stale file cannot make the walk fail.
            _filteredRefs = options
                .Where(kv => kv.Value && kv.Key.StartsWith(FilteredRefKeyPrefix, StringComparison.Ordinal))
                .Select(kv => kv.Key[FilteredRefKeyPrefix.Length..])
                .Where(name => name.Length > 0)
                .ToList();

            _showRemotes = Get(OptShowRemoteBranches, _showRemotes);
            _showTags = Get(OptShowTags, _showTags);
            _showStashes = Get(OptShowStashes, _showStashes);
            _showArtificial = Get(OptShowArtificialCommits, _showArtificial);
            _showGitNotes = Get(OptShowGitNotes, _showGitNotes);
            _drawNonRelativesGray = Get(OptDrawNonRelativesGray, _drawNonRelativesGray);
            _highlightCurrentBranch = Get(OptHighlightCurrentBranch, _highlightCurrentBranch);

            _showGraph = Get(OptGraphColumn, _showGraph);
            _showAvatar = Get(OptAvatarColumn, _showAvatar);
            _showAuthor = Get(OptAuthorColumn, _showAuthor);
            _showDate = Get(OptDateColumn, _showDate);
            _showHash = Get(OptIdColumn, _showHash);

            _dateSource = Get(OptAuthorDate, false) ? DateSource.Author : DateSource.Commit;
            _relativeDates = Get(OptRelativeDate, _relativeDates);

            // Topological order is the stronger constraint and wins if a file somehow
            // stored both, mirroring how the two are emitted in RevisionService.
            _topoOrder = Get(OptOrderTopo, false);
            _authorDateSort = !_topoOrder && Get(OptOrderAuthorDate, false);

            // The quick box's field; "commit message" is what a file naming none of
            // the three restores.
            _quickFilterField = Get(OptQuickFilterCommitter, false) ? QuickFilterField.Committer
                : Get(OptQuickFilterAuthor, false) ? QuickFilterField.Author
                : Get(OptQuickFilterDiff, false) ? QuickFilterField.DiffContent
                : QuickFilterField.Message;

            // The pickaxe form the diff field arms; nothing is armed yet, so this only
            // stores the flag (the walk below starts from the restored state anyway).
            _gitFilter = _gitFilter with { DiffContentIsRegex = Get(OptQuickFilterDiffRegex, false) };
            _filterTypeButton.Content = Chevron(QuickFilterFieldLabel);
            _search.Watermark = QuickFilterWatermark;
            if (_quickFilterDiffMode is not null)
            {
                _quickFilterDiffMode.IsVisible = _quickFilterField == QuickFilterField.DiffContent;
            }

            // The recent searches, ordered by the rank encoded in the key (see
            // PersistedViewOptions) rather than by the file's member order.
            _filterMru.Clear();
            _filterMru.AddRange(options
                .Where(kv => kv.Value && kv.Key.StartsWith(FilterMruKeyPrefix, StringComparison.Ordinal))
                .Select(kv => kv.Key[FilterMruKeyPrefix.Length..])
                .Select(rest =>
                {
                    int colon = rest.IndexOf(':');
                    return colon < 0
                        ? (Rank: int.MaxValue, Text: rest)
                        : (Rank: int.TryParse(rest[..colon], out int r) ? r : int.MaxValue, Text: rest[(colon + 1)..]);
                })
                .Where(e => e.Text.Length > 0)
                .OrderBy(e => e.Rank)
                .Select(e => e.Text)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxFilterMru));
            RebuildFilterMruFlyout();
        }

        // The header is built from the column flags, so it is rebuilt; OptionsChanged()
        // then brings the flyout check marks (built in the constructor, i.e. before this
        // call) in line and tells the host's mirrored menu.
        _headerHost.Content = BuildHeader();
        OptionsChanged();

        if (!string.IsNullOrEmpty(_repoPath))
        {
            Reload();
        }
    }

    // Every toggle control this view built for a "View" option, by id, together with
    // the reader for its value. The builders overwrite their entry each time a flyout
    // is rebuilt (a language switch), so no stale control is ever synced.
    private readonly Dictionary<string, (Control Toggle, Func<bool> Value)> _optionSurfaces =
        new(StringComparer.Ordinal);

    // Set while the option controls are being brought in line with the state, so
    // their own change handlers do not read the write back as a user action.
    private bool _syncingOptions;

    // Builds a check box for an option: reads its current value from ViewOptions and
    // invokes the toggle when the user flips it.
    private CheckBox OptionCheck(string id, string text, Action toggle)
    {
        bool Value() => ViewOptions.TryGetValue(id, out bool v) && v;

        CheckBox box = MakeCheck(text, Value());
        box.IsCheckedChanged += (_, _) =>
        {
            if (_syncingOptions || (box.IsChecked == true) == Value())
            {
                return;
            }

            toggle();
        };

        _optionSurfaces[id] = (box, Value);
        return box;
    }

    // Same for a radio button: only the CHECKED half of the pair acts, and a
    // re-selection of the already-active option is a no-op (no redundant reload).
    private RadioButton OptionRadio(string id, string text, string group, Action select)
    {
        bool Value() => ViewOptions.TryGetValue(id, out bool v) && v;

        RadioButton radio = MakeRadio(text, group, Value());
        radio.IsCheckedChanged += (_, _) =>
        {
            if (_syncingOptions || radio.IsChecked != true || Value())
            {
                return;
            }

            select();
        };

        _optionSurfaces[id] = (radio, Value);
        return radio;
    }

    // Called by every toggle below: brings the header flyouts back in line with the
    // state (so a change made from the main menu shows up there too) and tells the
    // host, which re-applies the check marks to its own menu items.
    //
    // The controls are UPDATED IN PLACE rather than rebuilt: rebuilding a flyout's
    // content while it is open would pull the visual tree out from under the pointer.
    private void OptionsChanged()
    {
        _syncingOptions = true;
        try
        {
            foreach ((Control toggle, Func<bool> value) in _optionSurfaces.Values)
            {
                bool wanted = value();
                switch (toggle)
                {
                    case CheckBox box when box.IsChecked != wanted:
                        box.IsChecked = wanted;
                        break;

                    // A radio button is only ever SET, never unset directly: checking
                    // its sibling is what clears it.
                    case RadioButton radio when wanted && radio.IsChecked != true:
                        radio.IsChecked = true;
                        break;
                }
            }
        }
        finally
        {
            _syncingOptions = false;
        }

        ViewOptionsChanged?.Invoke(ViewOptions);
    }

    /// <summary>
    ///  Runs one of the grid's Navigate/View menu commands, named by the
    ///  <c>Cmd…</c> / <c>Opt…</c> ids above. An unknown id is ignored, so a host
    ///  menu can carry entries this view does not implement without crashing.
    /// </summary>
    public void ExecuteMenuCommand(string id)
    {
        switch (id)
        {
            // --- Navigate ----------------------------------------------------
            case CmdToggleArtificialAndHead: ToggleBetweenArtificialAndHeadCommits(); break;
            case CmdGoToCurrentRevision: SelectCurrentRevision(); break;
            case CmdGoToCommit: OpenGoToCommit(); break;
            case CmdGoToChildCommit: GoToChild(); break;
            case CmdGoToParentCommit: GoToParent(); break;
            case CmdGoToMergeBase: GoToMergeBase(); break;
            case CmdNavigateBackward: NavigateBack(); break;
            case CmdNavigateForward: NavigateForward(); break;
            case CmdQuickSearchHelp: ShowQuickSearchHelp(); break;
            case CmdQuickSearchPrevious: QuickSearchPrevious(); break;
            case CmdQuickSearchNext: QuickSearchNext(); break;

            // --- View: commands ----------------------------------------------
            case CmdAdvancedFilter: _ = ShowFilterDialogAsync(); break;
            case CmdHighlightSelectedBranch: ToggleHighlightSelectedBranch(); break;

            // --- View: toggles ------------------------------------------------
            case OptShowAllBranches: ShowAllBranches(); break;
            case OptShowCurrentBranchOnly: ShowCurrentBranchOnly(); break;
            case OptShowFilteredBranches: ShowFilteredBranches(); break;
            case OptDrawNonRelativesGray: ToggleDrawNonRelativesGray(); break;
            case OptHighlightCurrentBranch: ToggleHighlightCurrentBranch(); break;
            case OptShowArtificialCommits: ToggleShowArtificialCommits(); break;
            case OptShowStashes: ToggleShowStashes(); break;
            case OptShowGitNotes: ToggleShowGitNotes(); break;
            case OptShowRemoteBranches: ToggleShowRemoteBranches(); break;
            case OptShowTags: ToggleShowTags(); break;
            case OptAuthorDate: SetAuthorDate(_dateSource != DateSource.Author); break;
            case OptRelativeDate: SetRelativeDate(!_relativeDates); break;
            case OptGraphColumn: ToggleRevisionGraphColumn(); break;
            case OptAvatarColumn: ToggleAuthorAvatarColumn(); break;
            case OptAuthorColumn: ToggleAuthorNameColumn(); break;
            case OptDateColumn: ToggleDateColumn(); break;
            case OptIdColumn: ToggleObjectIdColumn(); break;
            case OptOrderAuthorDate: ToggleAuthorDateSort(); break;
            case OptOrderTopo: ToggleTopoOrder(); break;
        }
    }

    // --- Branch scope ---------------------------------------------------------

    /// <summary>Walks every ref (upstream's "Show all branches", Ctrl+Shift+A).</summary>
    public void ShowAllBranches() => SetBranchScope(BranchScope.AllBranches);

    /// <summary>Walks HEAD only (upstream's "Show current branch only", Ctrl+Shift+U).</summary>
    public void ShowCurrentBranchOnly() => SetBranchScope(BranchScope.CurrentBranch);

    /// <summary>Walks the filtered ref set (upstream's "Show filtered branches", Ctrl+Shift+T).</summary>
    public void ShowFilteredBranches() => SetBranchScope(BranchScope.Filtered);

    // --- Walk contents (these re-run the log) ---------------------------------

    /// <summary>Includes/excludes remote-tracking branches in the walk (Ctrl+Shift+R).</summary>
    public void ToggleShowRemoteBranches()
    {
        _showRemotes = !_showRemotes;
        Reload();
        OptionsChanged();
    }

    /// <summary>Includes/excludes tags in the walk (Ctrl+Alt+T).</summary>
    public void ToggleShowTags()
    {
        _showTags = !_showTags;
        Reload();
        OptionsChanged();
    }

    /// <summary>Includes/excludes stash commits in the walk.</summary>
    public void ToggleShowStashes()
    {
        _showStashes = !_showStashes;
        Reload();
        OptionsChanged();
    }

    /// <summary>
    ///  Shows/hides the synthesised "Working directory" and "Commit index" rows.
    ///  No git work: the rows are rebuilt from the pending-work counts already held.
    /// </summary>
    public void ToggleShowArtificialCommits()
    {
        _showArtificial = !_showArtificial;
        ApplyFilterCore(_search.Text, preserveViewport: true);
        OptionsChanged();
    }

    // --- Render-time options (no reload) --------------------------------------

    /// <summary>Shows/hides the git-note indicator on the commits that carry one.</summary>
    public void ToggleShowGitNotes()
    {
        _showGitNotes = !_showGitNotes;
        RefreshView();
        OptionsChanged();
    }

    /// <summary>Grays out everything that is not a relative of the highlight anchor.</summary>
    public void ToggleDrawNonRelativesGray()
    {
        _drawNonRelativesGray = !_drawNonRelativesGray;

        // RefreshView() re-templates every visible row, so the graph cells are
        // rebuilt with (or without) the gray brush right away.
        RefreshView();
        OptionsChanged();
    }

    /// <summary>Emphasises HEAD's first-parent line.</summary>
    public void ToggleHighlightCurrentBranch()
    {
        _highlightCurrentBranch = !_highlightCurrentBranch;
        RefreshView();
        OptionsChanged();
    }

    /// <summary>
    ///  Re-anchors the graph highlighting on the SELECTED commit — upstream's
    ///  "Highlight selected branch (until refresh)" (Ctrl+Shift+B), the keyboard
    ///  equivalent of Alt+clicking the row. With nothing selected (or an artificial
    ///  row selected) the anchor goes back to HEAD.
    /// </summary>
    public void ToggleHighlightSelectedBranch()
        => HighlightBranchOf(_list.SelectedItem is RevisionRow row && !IsArtificial(row) ? row.Hash : null);

    /// <summary>Switches the Date column between the author and the commit timestamp.</summary>
    public void SetAuthorDate(bool authorDate)
    {
        DateSource wanted = authorDate ? DateSource.Author : DateSource.Commit;
        if (_dateSource == wanted)
        {
            return;
        }

        _dateSource = wanted;
        RefreshView();
        OptionsChanged();
    }

    /// <summary>Switches the Date column between "3 days ago" and an absolute stamp.</summary>
    public void SetRelativeDate(bool relative)
    {
        if (_relativeDates == relative)
        {
            return;
        }

        _relativeDates = relative;
        RefreshView();
        OptionsChanged();
    }

    /// <summary>Shows/hides the DAG column.</summary>
    public void ToggleRevisionGraphColumn() => ToggleColumn(() => _showGraph = !_showGraph);

    /// <summary>Shows/hides the author-avatar (identicon) column.</summary>
    public void ToggleAuthorAvatarColumn() => ToggleColumn(() => _showAvatar = !_showAvatar);

    /// <summary>Shows/hides the author-name column.</summary>
    public void ToggleAuthorNameColumn() => ToggleColumn(() => _showAuthor = !_showAuthor);

    /// <summary>Shows/hides the date column.</summary>
    public void ToggleDateColumn() => ToggleColumn(() => _showDate = !_showDate);

    /// <summary>Shows/hides the SHA-1 column.</summary>
    public void ToggleObjectIdColumn() => ToggleColumn(() => _showHash = !_showHash);

    private void ToggleColumn(Action flip)
    {
        flip();
        RefreshView();
        OptionsChanged();
    }

    // --- Sorting (these re-run the log) --------------------------------------

    /// <summary>
    ///  Turns author-date ordering on, or back to git's default order when it is
    ///  already on — the behaviour of upstream's checkable "Sort commits by author
    ///  date" entry.
    /// </summary>
    public void ToggleAuthorDateSort()
    {
        if (_authorDateSort)
        {
            SetDefaultSort();
            return;
        }

        SetAuthorDateSort();
    }

    /// <summary>Same for "Arrange commits by topo order".</summary>
    public void ToggleTopoOrder()
    {
        if (_topoOrder)
        {
            SetDefaultSort();
            return;
        }

        SetTopoSort();
    }

    private void SetDefaultSort() => SetSort(authorDate: false, topo: false);

    private void SetAuthorDateSort() => SetSort(authorDate: true, topo: false);

    private void SetTopoSort() => SetSort(authorDate: false, topo: true);

    // The three orders are mutually exclusive (upstream's single RevisionSortOrder
    // enum), so one setter owns both flags.
    private void SetSort(bool authorDate, bool topo)
    {
        if (_authorDateSort == authorDate && _topoOrder == topo)
        {
            return;
        }

        _authorDateSort = authorDate;
        _topoOrder = topo;
        Reload();
        OptionsChanged();
    }

    // --- Navigation commands -------------------------------------------------

    /// <summary>Selects the checked-out revision (upstream's Ctrl+Shift+C).</summary>
    public void SelectCurrentRevision() => GoToCurrentRevision();

    /// <summary>Opens the "Go to commit" entry box (upstream's Ctrl+Shift+G).</summary>
    public void OpenGoToCommit() => OpenGoTo();

    /// <summary>Selects the first parent of the current selection (Ctrl+P).</summary>
    public void GoToParentCommit() => GoToParent();

    /// <summary>Selects the nearest child of the current selection (Ctrl+N).</summary>
    public void GoToChildCommit() => GoToChild();

    /// <summary>Advances the quick-search to the next match (Alt+↓).</summary>
    public void QuickSearchNext() => QuickSearchStepOrHint(forward: true);

    /// <summary>Steps the quick-search back to the previous match (Alt+↑).</summary>
    public void QuickSearchPrevious() => QuickSearchStepOrHint(forward: false);

    // With an empty buffer there is nothing to step through, so say how to start one
    // instead of silently doing nothing.
    private void QuickSearchStepOrHint(bool forward)
    {
        if (_quickSearch.Length == 0)
        {
            FlashStatus(QuickSearchHelpText);
            return;
        }

        QuickSearchStep(forward);
    }

    /// <summary>
    ///  Explains the quick-search, like the information box behind upstream's
    ///  "Quick search" Navigate entry. Shown modally when there is a window to own
    ///  the dialog, and in the status line when there is not.
    /// </summary>
    public void ShowQuickSearchHelp() => _ = ShowQuickSearchHelpAsync();

    private async Task ShowQuickSearchHelpAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window)
        {
            FlashStatus(QuickSearchHelpText);
            return;
        }

        await InfoAsync(QuickSearchHelpText);
    }

    private static string QuickSearchHelpText => T(
        "RevisionGridMenuCommands/_quickSearchQuickHelp.Text",
        "Start typing in revision grid to start quick search.");

    /// <summary>
    ///  Moves the selection between the artificial rows and the checked-out commit
    ///  (upstream's ToggleBetweenArtificialAndHeadCommits, Ctrl+\): from a commit it
    ///  jumps to the topmost artificial row, from an artificial row back to HEAD.
    ///  With no artificial row on screen it simply goes to HEAD.
    /// </summary>
    public void ToggleBetweenArtificialAndHeadCommits()
    {
        bool onArtificial = _list.SelectedItem is RevisionRow row && IsArtificial(row);
        if (!onArtificial && _artificialCount > 0 && _rows.Count > 0)
        {
            string? from = CurrentHash;
            SelectIndex(0);
            PushHistory(from);
            return;
        }

        GoToCurrentRevision();
    }

    /// <summary>
    ///  Selects the merge base of the selection — upstream's "Go to common ancestor
    ///  (merge base)" (Ctrl+Shift+K). With two commits selected it is their common
    ///  ancestor; with one, the common ancestor of that commit and HEAD.
    ///
    ///  <para>The <c>git merge-base</c> call runs off the UI thread (M43) and the
    ///  result is applied through the dispatcher. Unrelated histories, or a base that
    ///  is not inside the loaded window, are reported in the status line rather than
    ///  silently doing nothing.</para>
    /// </summary>
    public void GoToMergeBase()
    {
        List<RevisionRow> selected = SelectedCommits();
        if (_repoPath.Length == 0 || selected.Count == 0)
        {
            return;
        }

        string first = selected[0].Hash;
        string second = selected.Count >= 2 ? selected[1].Hash : "HEAD";
        if (selected.Count == 1 && selected[0].IsHead)
        {
            FlashStatus(T("The selected commit is the current revision."));
            return;
        }

        string repoPath = _repoPath;
        FlashStatus(T("Looking for the merge base…"));

        _ = Task.Run(() =>
        {
            string? mergeBase = MergeBaseService.FindMergeBase(repoPath, first, second);
            Dispatcher.UIThread.Post(() =>
            {
                if (mergeBase is null)
                {
                    FlashStatus(T("The selected commits have no common ancestor."));
                    return;
                }

                if (FindIndex(_rows, mergeBase) < 0 && FindIndex(_allRows, mergeBase) < 0)
                {
                    FlashStatus(T("The merge base is not in the loaded history."));
                    return;
                }

                GoToCommit(mergeBase);
            });
        });
    }

    // A minimal modal information box (one OK button), the port's counterpart of
    // upstream's MessageBoxes.Show(..., MessageBoxIcon.Information).
    private async Task InfoAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        Button ok = new() { Content = T("TranslatedStrings/_okText.Text", "OK") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Information"),
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("App.Panel"),
        };
        ok.Click += (_, _) => dialog.Close();

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = B("App.Text"),
        });
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok },
        });
        dialog.Content = content;

        await dialog.ShowDialog(owner);
    }

    // "Go to" menu: buttons to jump to the first parent / nearest child of the
    // current selection, plus a hash box to select an arbitrary commit. All three
    // also work via keyboard. The gestures in the captions are READ from the hotkey
    // table, not typed here: they are configurable now, and a hard-coded "(Ctrl+P)"
    // would go on claiming a key the user has since given to something else.
    private Flyout BuildGoToFlyout()
    {
        StackPanel panel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 190 };

        Flyout flyout = new();

        panel.Children.Add(SectionLabel(T("FormBrowse/navigateToolStripMenuItem.Text", "Navigate")));

        Button parent = MakeMenuButton(Gesture(
            "↑  " + T("RevisionGrid/GotoFirstParentCommit.Text", "First parent"), "GoToParent"));
        parent.Click += (_, _) =>
        {
            flyout.Hide();
            GoToParent();
        };

        Button child = MakeMenuButton(Gesture(
            "↓  " + T("RevisionGrid/GotoChildCommit.Text", "Nearest child"), "GoToChild"));
        child.Click += (_, _) =>
        {
            flyout.Hide();
            GoToChild();
        };

        panel.Children.Add(parent);
        panel.Children.Add(child);

        // Navigation history: the two directions of the jump stack.
        Button back = MakeMenuButton(Gesture(
            "←  " + T("RevisionGrid/NavigateBackward.Text", "Backward"), "NavigateBackward"));
        back.Click += (_, _) =>
        {
            flyout.Hide();
            NavigateBack();
        };

        Button forward = MakeMenuButton(Gesture(
            "→  " + T("RevisionGrid/NavigateForward.Text", "Forward"), "NavigateForward"));
        forward.Click += (_, _) =>
        {
            flyout.Hide();
            NavigateForward();
        };

        panel.Children.Add(back);
        panel.Children.Add(forward);

        panel.Children.Add(SectionLabel(T("FormGoToCommit/$this.Text", "Go to commit")));
        panel.Children.Add(_goToBox);

        Button go = MakeMenuButton(T("Select commit"));
        void RunGoTo()
        {
            string text = _goToBox.Text ?? string.Empty;
            flyout.Hide();
            GoToCommit(text);
        }

        go.Click += (_, _) => RunGoTo();

        // Enter in the hash box triggers the jump.
        _goToBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RunGoTo();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                flyout.Hide();
                e.Handled = true;
            }
        };

        panel.Children.Add(go);

        flyout.Content = panel;
        return flyout;
    }

    // The "go to commit" hash entry box. Re-created whenever the Go-to flyout is
    // rebuilt, because an Avalonia control can only have one visual parent.
    private static TextBox MakeGoToBox()
        => new()
        {
            Watermark = T("hash (full or short)"),
            Background = B("App.Window"),
            Foreground = B("App.Text"),
            // Editable hash entry in the Go-to flyout: outline is the only boundary.
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            MinWidth = 150,
            Padding = new Thickness(6, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    // A full-width, left-aligned command inside a flyout card — a menu entry in all
    // but type, so it wears the menu-entry look (Theming/BarButtonStyles.ApplyMenus:
    // flat at rest, a rounded fill under the pointer). The outline it used to carry
    // was the affordance for a button that stands alone; inside a card of stacked
    // commands it drew a box per row instead, which is not how the app's other menus
    // read.
    private static Button MakeMenuButton(string text)
    {
        Button button = new()
        {
            Content = text,
            Background = Brushes.Transparent,
            Foreground = B("App.Text"),
            BorderThickness = new Thickness(0),
            Padding = StyleDensity.BarButtonWide,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Classes.Add(Theming.BarButtonStyles.MenuClass);
        return button;
    }

    // Opens the "Go to" flyout and focuses the hash box (Ctrl+Shift+G).
    private void OpenGoTo()
    {
        if (_goToButton.Flyout is Flyout f)
        {
            f.ShowAt(_goToButton);
            Dispatcher.UIThread.Post(() =>
            {
                _goToBox.Focus();
                _goToBox.SelectAll();
            });
        }
    }

    // --- Type-to-search (quick-search) ---------------------------------------
    //
    // Fed by _list's TextInput event, so it only runs when the grid/list has
    // focus — the filter box (_search) owns its own typing and is untouched.
    // Never hides rows; it only moves the selection and shows a transient pill.

    // A printable character was typed with the list focused: extend the buffer
    // and jump to the next match from the current selection (inclusive), so a
    // still-matching current row stays put as the query is refined.
    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        string text = e.Text ?? string.Empty;
        if (text.Length == 0 || _rows.Count == 0)
        {
            return;
        }

        // Ignore control characters (e.g. an escape sequence surfacing as text).
        foreach (char c in text)
        {
            if (char.IsControl(c))
            {
                return;
            }
        }

        _quickSearch += text;
        QuickSearchApply(fromCurrentInclusive: true);
        e.Handled = true;
    }

    // Searches from the current selection and (re)positions it on the first
    // matching row, updating the adorner. Restarts the idle-dismiss timer.
    private void QuickSearchApply(bool fromCurrentInclusive)
    {
        int start = _list.SelectedIndex >= 0 ? _list.SelectedIndex : 0;
        int index = QuickMatchIndex(_quickSearch, start, forward: true, inclusive: fromCurrentInclusive);
        bool found = index >= 0;
        if (found)
        {
            SelectIndex(index);
        }

        ShowQuickSearch(found);
    }

    // F3 / Shift+F3 (and Enter): advance to the next/previous match, starting
    // just past the current selection so repeated presses cycle through matches.
    private void QuickSearchStep(bool forward)
    {
        int start = _list.SelectedIndex >= 0 ? _list.SelectedIndex : 0;
        int index = QuickMatchIndex(_quickSearch, start, forward, inclusive: false);
        bool found = index >= 0;
        if (found)
        {
            SelectIndex(index);
        }

        ShowQuickSearch(found);
    }

    // Finds the index of the next row matching the buffer, scanning the displayed
    // rows in the requested direction and wrapping around. When inclusive, the
    // start row itself is considered first; otherwise the scan begins one step on.
    private int QuickMatchIndex(string query, int start, bool forward, bool inclusive)
    {
        int n = _rows.Count;
        if (n == 0 || query.Length == 0)
        {
            return -1;
        }

        int step = forward ? 1 : -1;
        int begin = inclusive ? start : start + step;
        for (int k = 0; k < n; k++)
        {
            int i = (((begin + (step * k)) % n) + n) % n;
            if (QuickMatches(_rows[i], query))
            {
                return i;
            }
        }

        return -1;
    }

    // Quick-search matches exactly the fields the original tests, in the same order
    // (GitRevisionTester.Matches, GitRevisionTester.cs:97-109):
    //  * any REF NAME containing the query — which is how "type a branch name to
    //    jump to its tip" works, and what the port was missing;
    //  * the commit hash PREFIX, but only from three characters on, so a one- or
    //    two-letter query does not snap onto an unrelated commit;
    //  * finally the author and the subject.
    private static bool QuickMatches(RevisionRow row, string query)
    {
        foreach (string refName in row.RefNames)
        {
            if (refName.Length > 0 && refName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (query.Length > 2 && row.Hash.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return row.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Subject.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // Ctrl+V while quick-searching appends the clipboard to the buffer, as the
    // original's QuickSearchProvider does (QuickSearchProvider.cs:67-72). The
    // clipboard is asynchronous here, so the buffer is extended when it arrives;
    // a paste with an empty clipboard leaves the buffer untouched.
    private async Task PasteIntoQuickSearchAsync()
    {
        string? text = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
            ? await clipboard.GetTextAsync()
            : null;

        if (string.IsNullOrEmpty(text) || _rows.Count == 0)
        {
            return;
        }

        // One line only: a multi-line paste would never match a subject anyway, and
        // the adorner is a single-line pill.
        int newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            text = text[..newline];
        }

        if (text.Length == 0)
        {
            return;
        }

        _quickSearch += text;
        QuickSearchApply(fromCurrentInclusive: true);
    }

    // Shows/refreshes the transient adorner and (re)arms the idle-dismiss timer.
    private void ShowQuickSearch(bool found)
    {
        _quickSearchLabel.Text = found
            ? string.Format(T("{0}: {1}…"), QuickSearchNoun, _quickSearch)
            : string.Format(T("{0}: {1}…  (no match)"), QuickSearchNoun, _quickSearch);
        _quickSearchLabel.Foreground = found ? B("App.Text") : B("App.TextDim");
        _quickSearchOverlay.IsVisible = true;

        _quickSearchTimer.Stop();
        _quickSearchTimer.Start();
    }

    private static string QuickSearchNoun => T("RevisionGrid/QuickSearch.Text", "quick-search");

    // Clears the buffer and hides the adorner (Esc, empty backspace, or idle).
    private void EndQuickSearch()
    {
        _quickSearchTimer.Stop();
        _quickSearch = string.Empty;
        _quickSearchOverlay.IsVisible = false;
    }

    // --- Row activation (double click / Enter) --------------------------------
    //
    // The original grid opens the commit's details on a double click, and the commit
    // dialog when the double click lands on an artificial row. Here the view does
    // what it owns — select the row, re-announce the selection and flash the commit's
    // identity in the status line — and raises RevisionActivated /
    // ArtificialRowActivated so the host can bring its details tab forward.

    // ALT+CLICK on a row: re-anchor the highlighting on that commit. Everything else
    // about the click is left alone (the event is not marked handled), and an Alt+click
    // on an artificial row re-anchors on HEAD, since those rows are not commits.
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (RowUnder(e) is not { } row)
        {
            return;
        }

        HighlightBranchOf(IsArtificial(row) ? null : row.Hash);
    }

    // Resolves the row a pointer event landed on, from ANY point of the row. The
    // cheap path walks the visual ancestors of the event source (the row content is
    // a tree of cells — graph control, text blocks, avatar, ref badges — all of them
    // below the item container). Hits that do not sit under a container at all (list
    // chrome, gaps between the realized items, a source hosted outside the item's
    // ancestor chain) fall back to matching the pointer's Y against the realized
    // containers, which is what makes the whole row width behave alike.
    //
    // Clicks on the list's scroll bars are deliberately NOT resolved to a row: they
    // overlay the rows, and in the original grid the scroll bar is not part of any
    // row either.
    private RevisionRow? RowUnder(PointerEventArgs e)
    {
        if (e.Source is Visual source)
        {
            foreach (Visual ancestor in source.GetSelfAndVisualAncestors())
            {
                if (ancestor is ScrollBar)
                {
                    return null;
                }

                if (ancestor is ListBoxItem container)
                {
                    return container.DataContext as RevisionRow;
                }

                if (ReferenceEquals(ancestor, _list))
                {
                    break;
                }
            }
        }

        foreach (Control container in _list.GetRealizedContainers())
        {
            if (container.DataContext is not RevisionRow candidate)
            {
                continue;
            }

            double y = e.GetPosition(container).Y;
            if (y >= 0 && y < container.Bounds.Height)
            {
                return candidate;
            }
        }

        return null;
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        RevisionRow? row = (e.Source as Visual)?
            .FindAncestorOfType<ListBoxItem>(includeSelf: true)?
            .DataContext as RevisionRow;

        row ??= _list.SelectedItem as RevisionRow;
        if (row is null)
        {
            return;
        }

        Activate(row);
        e.Handled = true;
    }

    // Activates a row: artificial rows notify the host (the single click already
    // opened the commit dialog, so it is NOT opened a second time here), commit rows
    // re-announce their selection and get their identity flashed in the status line.
    private void Activate(RevisionRow row)
    {
        if (IsArtificial(row))
        {
            bool isIndex = row.Hash == IndexHash;
            FlashStatus(isIndex
                ? T("Show staged changes")
                : T("Show working directory changes"));
            ArtificialRowActivated?.Invoke(isIndex);
            return;
        }

        SelectByHash(row.Hash);
        RevisionSelected?.Invoke(row.Hash);
        RevisionActivated?.Invoke(row.Hash);
        FlashStatus(string.Format(T("Commit {0}: {1}"), row.ShortHash, row.Subject));
    }

    // Shows a transient message in the status line, restoring the regular
    // repository/count line after a few seconds (the status line is rebuilt from
    // scratch by ApplyFilterCore, so nothing has to be remembered).
    private void FlashStatus(string message)
    {
        _status.Text = message;
        ToolTip.SetTip(_status, message);

        _statusFlashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusFlashTimer.Stop();
        _statusFlashTimer.Tick -= OnStatusFlashElapsed;
        _statusFlashTimer.Tick += OnStatusFlashElapsed;
        _statusFlashTimer.Start();
    }

    private void OnStatusFlashElapsed(object? sender, EventArgs e)
    {
        _statusFlashTimer?.Stop();

        // Five seconds after a flash message the status line goes back to the
        // repository/count text. Nothing about the rows changed and the user asked
        // for nothing: the viewport stays exactly where it is.
        ApplyFilterCore(_search.Text, preserveViewport: true);
    }

    private DispatcherTimer? _statusFlashTimer;

    // --- Navigation history ---------------------------------------------------
    //
    // The original grid keeps a NavigationHistory driven by Alt+←/Alt+→. Only
    // EXPLICIT jumps are recorded — "go to commit", first-parent / nearest-child
    // jumps and the parent/child links of the commit details (which come through
    // SelectCommit) — never plain arrow-key or click selection, which would turn the
    // history into a log of every row the user walked past.

    private readonly List<string> _navBack = [];
    private readonly List<string> _navForward = [];

    // Records the position a jump is leaving, and invalidates the forward stack (a
    // new jump from the middle of the history starts a new branch of it).
    private void PushHistory(string? from)
    {
        if (string.IsNullOrEmpty(from))
        {
            return;
        }

        if (_navBack.Count > 0 && _navBack[^1] == from)
        {
            return;
        }

        _navBack.Add(from);
        if (_navBack.Count > 200)
        {
            _navBack.RemoveAt(0);
        }

        _navForward.Clear();
    }

    // The hash of the row the history should record as "where we are now".
    private string? CurrentHash => (_list.SelectedItem as RevisionRow)?.Hash;

    /// <summary>
    ///  Goes back to the previous position in the navigation history (Alt+←).
    ///  Entries that are no longer displayed (a reload / a scope change dropped
    ///  them) are skipped. Returns false when the history is exhausted.
    /// </summary>
    public bool NavigateBack() => Step(_navBack, _navForward, T("No earlier position in the history."));

    /// <summary>
    ///  Goes forward again after one or more <see cref="NavigateBack"/> (Alt+→).
    ///  Returns false when there is nothing to go forward to.
    /// </summary>
    public bool NavigateForward() => Step(_navForward, _navBack, T("No later position in the history."));

    // Pops entries off one stack until one of them can actually be selected, pushing
    // the position being left onto the other stack.
    private bool Step(List<string> from, List<string> to, string emptyMessage)
    {
        string? current = CurrentHash;
        while (from.Count > 0)
        {
            string target = from[^1];
            from.RemoveAt(from.Count - 1);
            if (target == current)
            {
                continue;
            }

            if (SelectByHash(target))
            {
                if (!string.IsNullOrEmpty(current))
                {
                    to.Add(current);
                }

                return true;
            }
        }

        FlashStatus(emptyMessage);
        return false;
    }

    // --- Commit navigation ---------------------------------------------------
    //
    // Parent/child use the real DAG relationship carried on each row
    // (RevisionRow.ParentHashes), NOT graph-lane geometry — so a jump lands on the
    // exact commit even across merges. Navigation targets the currently displayed
    // rows (_rows), which equal _allRows when no filter is applied; "Go to commit"
    // additionally clears an active filter if the target is hidden by it.

    // Selects the first parent (ParentHashes[0]) of the current selection.
    private void GoToParent()
    {
        if (_list.SelectedItem is not RevisionRow row)
        {
            return;
        }

        if (row.ParentHashes.Count == 0)
        {
            _status.Text = T("No parent commit (root).");
            return;
        }

        if (SelectByHash(row.ParentHashes[0]))
        {
            PushHistory(row.Hash);
        }
        else
        {
            _status.Text = T("Parent commit is not in the loaded history.");
        }
    }

    // Selects the child commit nearest to the current selection: any loaded row
    // that lists the current commit among its parents, closest by list position.
    private void GoToChild()
    {
        if (_list.SelectedItem is not RevisionRow row)
        {
            return;
        }

        int current = _list.SelectedIndex;
        RevisionRow? best = null;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < _rows.Count; i++)
        {
            foreach (string parent in _rows[i].ParentHashes)
            {
                if (parent == row.Hash)
                {
                    int distance = Math.Abs(i - current);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = _rows[i];
                    }

                    break;
                }
            }
        }

        if (best is null)
        {
            _status.Text = T("No child commit in the loaded history.");
            return;
        }

        SelectRow(best);
        PushHistory(row.Hash);
    }

    // Selects the commit matching an entered hash (full or abbreviated). Searches
    // the displayed rows first; if a filter hides the target, it is cleared and the
    // full set is retried so the jump still lands.
    /// <summary>
    ///  Selects and scrolls to the row whose commit hash matches <paramref name="hash"/>
    ///  (full or abbreviated). Best-effort: if the target is hidden by an active
    ///  filter the filter is cleared and the lookup retried; if it is not among the
    ///  loaded rows this is a no-op. Used by the commit-detail parent/child links.
    /// </summary>
    public void SelectCommit(string hash) => GoToCommit(hash);

    /// <summary>
    ///  Asks for <paramref name="hash"/> to be selected as soon as the walk has rows —
    ///  <see cref="SelectCommit"/> can only find what is already loaded, and the caller
    ///  that knows which commit it wants (the repository tab strip, restoring the row
    ///  the user was on) asks for it at the very moment the load STARTS.
    ///
    ///  <para>One shot, and cleared on the first page that arrives whether or not it
    ///  contained the commit: a hash that is not in this repository's history — a stale
    ///  entry in the saved tab state, a rewritten commit — must not sit there waiting to
    ///  hijack a later reload's selection. Nothing is reported when it misses, because
    ///  restoring a selection is not a command the user issued.</para>
    /// </summary>
    /// <remarks>
    ///  Recorded only — deliberately NOT tried against the rows that happen to be on
    ///  screen. The caller asks the instant the switch begins, when those rows still
    ///  belong to the repository being left: attempting the lookup there consumes the
    ///  one shot on a history that cannot contain the commit, and the page that does
    ///  arrives to find nothing pending.
    /// </remarks>
    public void SelectCommitWhenLoaded(string? hash)
    {
        _pendingSelection = string.IsNullOrWhiteSpace(hash) ? null : hash.Trim();

        // Asking for a commit is also saying "deliver it": the host that calls this has
        // just reset its panes, so whatever was announced before no longer counts.
        // Without this a duplicated tab could never repaint — it inherits the commit
        // that is ALREADY selected and already announced, so neither the widget nor the
        // check in ApplyPendingSelection would have anything to report.
        _announcedHash = null;
    }

    private string? _pendingSelection;

    /// <summary>The revision last handed to the host, so a restored selection can tell
    /// whether the host already knows about it.</summary>
    private string? _announcedHash;

    // The single funnel for RevisionSelected: every announcement records what was
    // announced, which is what lets ApplyPendingSelection below decide whether the
    // host still needs telling.
    private void AnnounceRevision(string hash)
    {
        _announcedHash = hash;
        RevisionSelected?.Invoke(hash);
    }

    private void ApplyPendingSelection()
    {
        if (_pendingSelection is not { Length: > 0 } hash || _rows.Count == 0)
        {
            return;
        }

        _pendingSelection = null;
        int index = FindIndex(_rows, hash);
        if (index < 0)
        {
            return;
        }

        SelectIndex(index);

        // Selecting is not the same as ANNOUNCING, and a restored selection routinely
        // does the first without the second: the widget raises SelectionChanged only
        // when the index actually changes, and the rebind that precedes this puts the
        // old selection back from inside SetListItems, where the _rebinding guard
        // deliberately swallows it. Either way the row ends up highlighted while the
        // host was never told — and the host has just CLEARED the bottom panes
        // (MainWindow.ResetBottomPanes), so they stay empty under a selected row.
        // That is what "No commit selected." after switching repository tabs was, and
        // it was at its most obvious on a duplicated tab, where the inherited commit
        // is the one already selected and so cannot possibly raise the event.
        //
        // Announced here rather than on every rebind: only a PENDING selection — a
        // host asking for a specific commit — passes through, so a watcher-driven
        // refresh that re-selects the same row still costs nothing.
        if (!string.Equals(_announcedHash, hash, StringComparison.Ordinal))
        {
            AnnounceRevision(hash);
        }
    }

    private void GoToCommit(string? text)
    {
        string query = (text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return;
        }

        // Where the jump starts from, recorded in the navigation history below.
        string? from = CurrentHash;

        int index = FindIndex(_rows, query);
        if (index < 0 && _quickFilterActive)
        {
            // Drop the filter (ApplyFilter resets _rows to _allRows) and retry.
            _search.Text = string.Empty;
            index = FindIndex(_rows, query);
        }

        if (index < 0)
        {
            _status.Text = string.Format(T("No commit matching \"{0}\"."), query);
            return;
        }

        SelectIndex(index);
        PushHistory(from);
    }

    // Locates a commit by hash: exact full/short match first, then a hash prefix.
    private static int FindIndex(IReadOnlyList<RevisionRow> rows, string query)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Hash.Equals(query, StringComparison.OrdinalIgnoreCase)
                || rows[i].ShortHash.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Hash.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Selects a specific row (by full hash) in the displayed set; returns false if
    // it is not currently shown. Scrolls the target into view and keeps focus on
    // the list so successive keyboard jumps chain naturally.
    private bool SelectByHash(string hash)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Hash == hash)
            {
                SelectIndex(i);
                return true;
            }
        }

        return false;
    }

    private void SelectRow(RevisionRow row)
    {
        int index = FindIndex(_rows, row.Hash);
        if (index >= 0)
        {
            SelectIndex(index);
        }
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        _list.SelectedIndex = index;
        _list.ScrollIntoView(_rows[index]);

        // The row, not the list (see FocusSelectedRow). Posted at Loaded priority
        // because ScrollIntoView has only just asked for the row: its container does
        // not exist until the layout pass that follows, and focusing before that would
        // fall through to the list-level no-op.
        Dispatcher.UIThread.Post(FocusSelectedRow, DispatcherPriority.Loaded);
    }

    private static TextBlock SectionLabel(string text)
        => new()
        {
            Text = text,
            Foreground = B("App.TextDim"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 2, 0, 1),
        };

    private static RadioButton MakeRadio(string text, string group, bool isChecked)
        => new()
        {
            Content = text,
            GroupName = group,
            IsChecked = isChecked,
            Foreground = B("App.Text"),
            FontSize = 12,
        };

    private static CheckBox MakeCheck(string text, bool isChecked)
        => new()
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = B("App.Text"),
            FontSize = 12,
        };

    // A small amber "note" pill indicating the commit carries a git note.
    // The same outline pill BuildRefBadge draws, in the notes ink. It used to be the
    // one badge with hard-coded colours — an opaque brown fill carrying pale amber
    // text — which made it the odd one out on a light row, where every other badge is
    // an outline on App.RefPillBg, and left its own text-on-fill pair at 5.34:1, the
    // weakest contrast on the row. Brushes taken BY REFERENCE for the same reason the
    // ref pill does it: a live theme switch repaints without rebuilding the row.
    private static Border BuildNotesBadge()
        => new()
        {
            Background = B("App.RefPillBg"),
            BorderBrush = B("App.RefNote"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 0, 7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = T("This commit has a git note"),
            Child = new TextBlock
            {
                Text = T("note"),
                Foreground = B("App.RefNote"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    private Grid MakeColumns()
    {
        // Hidden columns collapse to zero width; their content is simply not added
        // (see BuildHeader/BuildRow) so nothing overflows into the neighbouring cell.
        double hash = _showHash ? _hashWidth : 0;
        double avatar = _showAvatar ? AvatarWidth : 0;
        double author = _showAuthor ? _authorWidth : 0;
        double date = _showDate ? _dateWidth : 0;

        // Columns: 0 graph, 1 subject (fills), 2 avatar, 3 author, 4 date, 5 hash.
        //
        // That is the ORIGINAL registration order (RevisionGridControl.cs:342-351):
        // graph, then MessageColumnProvider — which is the column with
        // AutoSizeMode = Fill (MessageColumnProvider.cs:78-86) — then the notes,
        // avatar, author-name and date columns, and CommitIdColumnProvider LAST
        // (CommitIdColumnProvider.cs:21-29). This port used to put the SHA-1 in
        // second place and the subject last, which was the single most visible
        // divergence from the Windows grid.
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{EffectiveGraphWidth},*,{avatar},{author},{date},{hash}"),

            // Marked so a live column drag can find the row grids among the panels the
            // row template nests inside them, without guessing from the column count.
            Tag = ColumnGridTag,
        };
    }

    private Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);

        AddCell(grid, 0, string.Empty, B("App.TextDim"), bold: true);

        AddCell(grid, 1, T("PatchGrid/subjectDataGridViewTextBoxColumn.HeaderText", "Subject"),
            B("App.TextDim"), bold: true);

        // Column 2 (avatar) has no textual header — the identicons speak for themselves.
        if (_showAuthor)
        {
            AddCell(grid, 3, T("TranslatedStrings/_author.Text", "Author"), B("App.TextDim"), bold: true);
        }

        if (_showDate)
        {
            string dateHeader = T("TranslatedStrings/_dateText.Text", "Date");
            AddCell(grid, 4, _relativeDates ? string.Format(T("{0} (rel.)"), dateHeader) : dateHeader,
                B("App.TextDim"), bold: true);
        }

        if (_showHash)
        {
            AddCell(grid, 5, T("Commit ID"), B("App.TextDim"), bold: true);
        }

        // A grab strip on the LEFT edge of each fixed column. Upstream's grid is a
        // DataGridView, where resizable columns come for free and the message column is
        // the Fill one; this port draws its own header, so the dividers are drawn too —
        // over the same set of columns upstream lets the user drag (author, date and
        // commit id; the graph and the avatar are fixed there as well, and the subject
        // has no width of its own because it absorbs the others' — see MakeColumns).
        if (_showAuthor)
        {
            grid.Children.Add(ResizeHandle(3, () => _authorWidth, width => _authorWidth = width));
        }

        if (_showDate)
        {
            grid.Children.Add(ResizeHandle(4, () => _dateWidth, width => _dateWidth = width));
        }

        if (_showHash)
        {
            grid.Children.Add(ResizeHandle(5, () => _hashWidth, width => _hashWidth = width));
        }

        _headerGrid = grid;

        return new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 3, 0, 3),
            Child = grid,
        };
    }

    /// <summary>
    ///  The divider on a column's left edge: dragging it left widens that column and
    ///  narrows the subject, dragging it right does the opposite.
    ///
    ///  <para><b>Why the left edge and not the right.</b> The subject is the column that
    ///  gives and takes the space (it is the only <c>*</c> one), and it sits to the LEFT
    ///  of the three fixed columns. A divider always resizes the column it belongs to
    ///  against the one that absorbs, so it has to be on the side facing the subject.</para>
    ///
    ///  <para><b>The rows follow live, without being rebuilt.</b> Re-templating them per
    ///  pointer move would be a slideshow, not a drag; instead the drag walks the rows
    ///  that are REALIZED — a screenful, never the whole history — and moves their column
    ///  definitions, exactly as it moves the header's. A row realized mid-drag is already
    ///  correct: <see cref="MakeColumns"/> reads the same fields. Nothing is rebound at
    ///  the end, so there is no flash when the button comes up; that is only when the
    ///  width is written to the preferences.</para>
    /// </summary>
    private Control ResizeHandle(int column, Func<double> get, Action<double> set)
    {
        // The 1px rule the user actually sees: a grab strip with no mark on it is a
        // feature nobody finds. It brightens under the pointer, which is the other half
        // of the affordance — the cursor alone only speaks once you are already on it.
        Rectangle rule = new()
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = B("App.Rule"),
        };

        Border handle = new()
        {
            Width = ResizeHandleWidth,
            HorizontalAlignment = HorizontalAlignment.Left,

            // Straddling the boundary rather than sitting inside the column: half of the
            // strip hangs over the neighbour, so the pointer finds it from either side.
            Margin = new Thickness(-ResizeHandleWidth / 2, 0, 0, 0),

            // Transparent, not null: a null background is not hit-testable, and the
            // strip would be invisible to the pointer as well as to the eye.
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Child = rule,
        };

        handle.PointerEntered += (_, _) => rule.Fill = B("App.Accent");
        handle.PointerExited += (_, _) => rule.Fill = B("App.Rule");

        double startWidth = 0;
        double startX = 0;
        bool dragging = false;

        handle.PointerPressed += (_, e) =>
        {
            dragging = true;
            startWidth = get();
            startX = e.GetPosition(this).X;
            e.Pointer.Capture(handle);
            e.Handled = true;
        };

        handle.PointerMoved += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            // Leftwards is wider, hence the reversed subtraction.
            double wanted = startWidth + (startX - e.GetPosition(this).X);
            set(ClampColumnWidth(column, get(), wanted));
            ApplyHeaderWidths();
            ApplyRowWidths();
        };

        handle.PointerReleased += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            e.Pointer.Capture(null);
            PersistColumnWidths();
        };

        Grid.SetColumn(handle, column);
        return handle;
    }

    /// <summary>
    ///  Keeps a dragged width between "still visible" and "the subject is still
    ///  readable": the subject absorbs every pixel the other columns take, so the ceiling
    ///  is whatever it can give up before hitting its own floor.
    /// </summary>
    private double ClampColumnWidth(int column, double current, double wanted)
    {
        double subject = _headerGrid is { } grid && grid.ColumnDefinitions.Count > 1
            ? grid.ColumnDefinitions[1].ActualWidth
            : 0;

        double ceiling = subject > 0
            ? current + Math.Max(0, subject - MinSubjectWidth)
            : double.MaxValue;

        return Math.Clamp(wanted, MinColumnWidth, Math.Max(MinColumnWidth, ceiling));
    }

    /// <summary>Moves the header's own definitions, mid-drag.</summary>
    private void ApplyHeaderWidths() => ApplyWidths(_headerGrid);

    /// <summary>
    ///  Moves the definitions of every row currently on screen, so the columns under the
    ///  header keep up with it while the divider is being dragged.
    ///
    ///  <para>Only the realized containers: virtualization means that is a screenful,
    ///  and the rows outside it will be built from the fields when they scroll in.</para>
    /// </summary>
    private void ApplyRowWidths()
    {
        foreach (Control container in _list.GetRealizedContainers())
        {
            foreach (Visual child in container.GetVisualDescendants())
            {
                if (child is Grid { Tag: ColumnGridTag } grid)
                {
                    ApplyWidths(grid);
                    break;
                }
            }
        }
    }

    private void ApplyWidths(Grid? grid)
    {
        if (grid is null || grid.ColumnDefinitions.Count < 6)
        {
            return;
        }

        if (_showAuthor)
        {
            grid.ColumnDefinitions[3].Width = new GridLength(_authorWidth);
        }

        if (_showDate)
        {
            grid.ColumnDefinitions[4].Width = new GridLength(_dateWidth);
        }

        if (_showHash)
        {
            grid.ColumnDefinitions[5].Width = new GridLength(_hashWidth);
        }
    }

    /// <summary>
    ///  Remembers the widths. Upstream does NOT — its columns are re-created at their
    ///  hard-coded defaults on every start — and that is a difference on purpose: a width
    ///  the user dragged is a decision, and losing it at every launch is the kind of
    ///  small betrayal the port has been removing everywhere else.
    /// </summary>
    private void PersistColumnWidths()
    {
        try
        {
            new Services.ViewPrefsService().Update(prefs => prefs.GridColumns = new Services.GridColumnPrefs
            {
                Author = _authorWidth,
                Date = _dateWidth,
                Hash = _hashWidth,
            });
        }
        catch (Exception)
        {
            // A width is not worth a crash; the session keeps the dragged value anyway.
        }
    }

    /// <summary>
    ///  Reads back what the last drag stored. A zero (or absurd) value means "never
    ///  dragged", so the built-in default stands — which is also what a hand-edited or
    ///  truncated preferences file lands on.
    /// </summary>
    private void LoadColumnWidths()
    {
        try
        {
            Services.GridColumnPrefs prefs = new Services.ViewPrefsService().Load().GridColumns;
            _authorWidth = Sane(prefs.Author, AuthorWidth);
            _dateWidth = Sane(prefs.Date, DateWidth);
            _hashWidth = Sane(prefs.Hash, HashWidth);
        }
        catch (Exception)
        {
            // Defaults are already in the fields.
        }

        static double Sane(double stored, double fallback)
            => stored >= MinColumnWidth && stored <= 2000 ? stored : fallback;
    }

    private Control BuildRow(RevisionRow? row)
    {
        // A container being CLEARED re-invokes the template with an unset (null)
        // item; the previous build then ran straight into IsArtificial(row.Hash) and
        // took the process down with a NullReferenceException — reproducible by
        // opening any repository from the dashboard. Same defect that BlameView's
        // template already guards against (BlameView.BuildRow). Note this is a
        // separate concern from the ItemsSource re-entrancy guard: that one is about
        // WHO may assign the source, this one about what the template is handed.
        if (row is null)
        {
            return MakeColumns();
        }

        if (IsArtificial(row))
        {
            return BuildArtificialRow(row);
        }

        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);
        grid.MinHeight = RowMinHeight;

        // Subtle alternating-row background (App.Panel / App.PanelAlt). It lives on
        // the RevisionRowView wrapper (full row width, no margin) so the selection
        // fill can cover every column edge to edge, like the original grid.
        int index = _rows is List<RevisionRow> list ? list.IndexOf(row) : IndexOf(_rows, row);
        RevisionRowView view = new(RowBackground(row, index), grid);

        // Upstream's ShowRevisionGridTooltips. The columns truncate — the subject most
        // of all — so the tip carries what the row cannot show: the full subject, the
        // author with their address, and the full hash. Set on the row wrapper, so it
        // answers wherever the pointer rests instead of only over one column.
        if (_gridPrefs.ShowRevisionGridTooltips && !IsArtificial(row))
        {
            ToolTip.SetTip(view, RowTooltip(row));
        }

        // Graph cell (column 0): the DAG lanes for this row. While a filter is
        // active the rows shown are a non-contiguous subset, so the precomputed
        // segments (which reference adjacent rows in the full list) no longer make
        // sense — the column is collapsed to zero width and the graph is skipped
        // to avoid rendering a garbled DAG. It returns in full once the filter clears.
        if (_showGraph && !_quickFilterActive)
        {
            (bool Node, bool[] Segments)? flags = _drawNonRelativesGray
                ? GraphRelatives(index)
                : null;

            RevisionGraphControl graph = new(
                row.GraphSegments,
                row.NodeLane,
                LaneWidth,
                nodeColor: row.NodeColor,
                relativeSegments: flags?.Segments,
                relativeNode: flags?.Node ?? true,
                nonRelativeBrush: flags is null ? null : B("App.TextDim"),
                squareNode: row.RefNames.Count > 0,
                headNode: row.IsHead);
            Grid.SetColumn(graph, 0);
            grid.Children.Add(graph);
            view.TrackGraph(graph);
        }

        // Render-time "View" highlight styles (no reload):
        //  - highlight current branch: HEAD's first-parent line is emphasised
        //    (accent + bold), taking precedence over graying.
        //  - draw non-relatives gray: rows that are not the highlight anchor nor one
        //    of its ancestors are dimmed (the graph cell above grays their lanes the
        //    same way). Guarded on a non-empty relatives set so it is a no-op when
        //    neither the anchor nor HEAD is inside the loaded window.
        bool onBranch = _highlightCurrentBranch && _currentBranchLine.Contains(row.Hash);

        // The LANE graying and the TEXT graying are two settings upstream and one flag
        // here until now: with RevisionGraphDrawNonRelativesTextGray off, the lanes
        // still fade but the subjects stay readable.
        bool nonRelative = !onBranch && _drawNonRelativesGray && _gridPrefs.GraphDrawNonRelativesTextGray
            && _headRelatives.Count > 0 && !_headRelatives.Contains(row.Hash);

        IBrush hashBrush = nonRelative ? B("App.TextDim") : B("App.Accent");
        IBrush subjectBrush = onBranch ? B("App.Accent") : nonRelative ? B("App.TextDim") : B("App.Text");

        // Hash: monospace + accent so it reads as a code identifier. Rightmost
        // column, as in the original (CommitIdColumnProvider is registered last).
        if (_showHash)
        {
            view.TrackText(AddCell(grid, 5, row.ShortHash, hashBrush, bold: onBranch, monospace: true));
        }

        // Avatar (column 2): a deterministic offline identicon per author, cached.
        if (_showAvatar)
        {
            AvatarControl avatar = new(GetIdenticon(row.AuthorEmail, row.Author))
            {
                Width = AvatarSize,
                Height = AvatarSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                [ToolTip.TipProperty] = string.IsNullOrEmpty(row.AuthorEmail)
                    ? row.Author
                    : $"{row.Author} <{row.AuthorEmail}>",
            };
            Grid.SetColumn(avatar, 2);
            grid.Children.Add(avatar);
        }

        if (_showAuthor)
        {
            // The author of the SELECTED revision is emphasised on EVERY row that
            // shares it, so all the commits of one person stand out at a glance —
            // upstream's AuthorRevisionHighlighting, applied by
            // AuthorNameColumnProvider.cs:38-40 (bold font for the highlighted
            // author). The port drew every author in App.TextDim regardless.
            bool sameAuthor = _gridPrefs.HighlightAuthoredRevisions
                && _highlightedAuthor.Length > 0
                && string.Equals(row.Author, _highlightedAuthor, StringComparison.Ordinal);

            view.TrackText(
                AddCell(grid, 3, row.Author, sameAuthor ? B("App.Text") : B("App.TextDim"), bold: sameAuthor),
                dim: !sameAuthor);
        }

        if (_showDate)
        {
            view.TrackText(AddCell(grid, 4, FormatDate(row), B("App.TextDim")), dim: true);
        }

        // Subject cell: an optional git-notes indicator, then ref badges, then the
        // subject text.
        //
        // A DockPanel, NOT a horizontal StackPanel: now that Subject is the Fill
        // column and the Author/Date/SHA-1 columns sit to its RIGHT, a stack would
        // measure its children against infinite width — the subject would never
        // ellipsize and would simply be painted over the author name. Here the badges
        // are docked left and the subject text is the last child, so it gets exactly
        // the space that is left and trims inside it.
        DockPanel subject = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };

        void Badge(Control control)
        {
            control.Margin = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(control, Dock.Left);
            subject.Children.Add(control);
        }

        if (row.HasNotes && _showGitNotes)
        {
            Badge(BuildNotesBadge());
        }

        foreach (string refName in row.RefNames)
        {
            // Kind comes from the ref listing git gave us, not from the shape of the
            // name — see RefKindOf. Respect the remote/tag "View" toggles so what is
            // badged stays consistent with what the walk includes.
            char kind = RefKindOf(refName);
            if ((!_showRemotes && kind == 'r') || (!_showTags && kind == 't'))
            {
                continue;
            }

            // The checked-out branch, marked the way the original marks it. It is THE
            // branch HEAD is on, matched by name — not "any local branch on the HEAD
            // row": with two branches on one commit only one of them is checked out,
            // and upstream puts the marker on that one.
            bool isCurrent = row.IsHead
                && kind == 'b'
                && _currentBranch.Length > 0
                && string.Equals(refName, _currentBranch, StringComparison.Ordinal);
            Badge(BuildRefBadge(refName, isCurrent, view, kind));
        }

        TextBlock subjectText = new()
        {
            Text = row.Subject,
            Foreground = subjectBrush,
            FontWeight = onBranch ? FontWeight.Bold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view.TrackText(subjectText);
        subject.Children.Add(subjectText);

        Grid.SetColumn(subject, 1);
        grid.Children.Add(subject);

        view.ContextMenu = RowMenu();
        return view;
    }

    // Builds one artificial row ("Working directory" / "Commit index"): the same
    // column grid as a commit row, with the DAG node in HEAD's lane and, in the
    // Subject column, the original's boxed label followed by a green check and the
    // pending-work count. Selecting it raises the matching event (see the
    // SelectionChanged handler), which opens the commit dialog.
    private Control BuildArtificialRow(RevisionRow row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);
        grid.MinHeight = RowMinHeight;

        int index = IndexOf(_rows, row);
        RevisionRowView view = new(RowBackground(row, index), grid);

        if (_showGraph && !_quickFilterActive)
        {
            // Same treatment as a commit row: the artificial nodes are ordinary graph
            // rows now, so they can carry other branches' lanes through and those must
            // gray out with everything else.
            (bool Node, bool[] Segments)? flags = _drawNonRelativesGray
                ? GraphRelatives(index)
                : null;

            RevisionGraphControl graph = new(
                row.GraphSegments,
                row.NodeLane,
                LaneWidth,
                artificialNode: true,
                nodeColor: row.NodeColor,
                relativeSegments: flags?.Segments,
                relativeNode: flags?.Node ?? true,
                nonRelativeBrush: flags is null ? null : B("App.TextDim"));
            Grid.SetColumn(graph, 0);
            grid.Children.Add(graph);
            view.TrackGraph(graph);
        }

        // Same DockPanel layout as a commit row (see BuildRow): the label, the
        // marker and the count are docked left so nothing spills into the Author
        // column now that Subject is no longer the last column.
        DockPanel subject = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
        };

        void Cell(Control control)
        {
            control.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(control, Dock.Left);
            subject.Children.Add(control);
        }

        // The boxed label, matching the original's outlined "Working directory" /
        // "Commit index" cell. Unlike a ref pill the label uses the themed text
        // brush, so on a selected (solid blue) row the box KEEPS its dark backdrop
        // and only the text switches to white — swapping the box to white would
        // leave light-on-light text.
        TextBlock labelText = new()
        {
            Text = row.Subject,
            Foreground = B("App.Text"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Border box = new()
        {
            Background = B("App.Panel"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 0, 7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = labelText,
        };
        view.TrackText(labelText);
        Cell(box);

        bool isWorkTree = row.Hash == WorkTreeHash;
        int count = isWorkTree ? _unstaged : _staged;

        TextBlock check = new()
        {
            Text = isWorkTree ? "✔" : "✚",
            Foreground = Brushes.MediumSeaGreen,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view.TrackMarker(check);
        Cell(check);

        TextBlock countText = new()
        {
            Text = isWorkTree
                ? string.Format(T("{0} modified"), count)
                : string.Format(T("{0} staged"), count),
            Foreground = B("App.TextDim"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view.TrackText(countText, dim: true);
        Cell(countText);

        Grid.SetColumn(subject, 1);
        grid.Children.Add(subject);

        // A DOUBLE click opens the commit dialog; a single click only selects, so the
        // bottom tabs can show the row's own content (worktree/index diff, file tree,
        // placeholders — M64). Until then a single click popped the dialog over that
        // content, which upstream never does: FormBrowse just fills the tabs for the
        // artificial rows and reaches the dialog from the Commit button. Bound to the
        // click and not to selection, so keyboard navigation passes over freely.
        void Raise()
        {
            if (isWorkTree)
            {
                WorkingDirectorySelected?.Invoke();
            }
            else
            {
                CommitIndexSelected?.Invoke();
            }
        }

        view.Cursor = new Cursor(StandardCursorType.Hand);
        view.AddHandler(
            InputElement.DoubleTappedEvent,
            (_, _) => Raise(),
            RoutingStrategies.Bubble);

        // The SAME shared menu as a commit row: on an artificial row its rules hide
        // everything that cannot apply (cherry-pick, reword, branch/tag operations,
        // copy) and leave only the "show changes" entry and Navigate.
        view.ContextMenu = RowMenu();
        return view;
    }

    private static int IndexOf(IReadOnlyList<RevisionRow> rows, RevisionRow row)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return 0;
    }

    // A rounded, OUTLINE "pill" for a ref name, coloured by kind: local branch,
    // remote-tracking branch, or tag — echoing the original GitExtensions look
    // (light background, 1px coloured border, coloured text). When isCurrent, the
    // pill is bold and prefixed by a small green ▶ marker for the checked-out branch.
    private static Control BuildRefBadge(
        string refName, bool isCurrent = false, RevisionRowView? view = null, char refKind = '\0')
    {
        // Both the outline and the glyphs, by REFERENCE, so a live theme switch
        // repaints the pill without rebuilding the row (ThemeManager mutates the
        // brushes in place).
        IBrush kind = refKind switch
        {
            'r' => B("App.RefRemote"),
            't' => B("App.RefTag"),
            'b' => B("App.RefBranch"),
            _ => RefBrush(refName),
        };

        Border pill = new()
        {
            // App.RefPillBg is the pill's own surface, not App.Panel: it is the
            // background the three App.Ref* values are measured against, so it must not
            // be able to drift when App.Panel is retuned. It is opaque in both themes
            // and therefore also covers the selection fill (see below).
            Background = B("App.RefPillBg"),
            BorderBrush = kind,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(isCurrent ? 5 : 7, 0, 7, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        TextBlock caption = new()
        {
            Text = refName,
            Foreground = kind,
            FontSize = 11,
            FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // NOTE: the pill used to swap its backdrop to hard-coded opaque WHITE while its
        // row was selected. That was only ever right in the light theme, where the pill
        // surface IS white; in the dark theme it turned every pill into a white chip
        // carrying ink chosen for a #252526 background, and the arithmetic says no
        // single ink can serve both — a colour clearing 4.5:1 on #252526 needs
        // relative luminance >= 0.254, one clearing 4.5:1 on white needs <= 0.183.
        // Since App.RefPillBg is opaque and themed, the pill covers the selection fill
        // on its own and the swap is gone rather than merely themed.

        if (!isCurrent)
        {
            pill.Child = caption;
            return pill;
        }

        // The ▶ goes INSIDE the pill, which is where the original puts it
        // (RevisionGridRefRenderer draws the arrow within the ref box, not next to it).
        // The port had it outside, so the checked-out branch read as "an arrow, and
        // then a branch" instead of as one marked label.
        TextBlock marker = new()
        {
            Text = "▶",
            Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xAE, 0x5A)),
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        view?.TrackMarker(marker);

        StackPanel inside = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        inside.Children.Add(marker);
        inside.Children.Add(caption);
        pill.Child = inside;
        return pill;
    }

    // Ref-kind heuristics (shared by badge coloring and the remote/tag toggles):
    // a "/" marks a remote-tracking ref (origin/main); a leading version-like
    // token (v1.2, 2.0) marks a tag. Local branches match neither.
    private static bool IsRemoteRef(string refName) => refName.Contains('/');

    private static bool IsTagRef(string refName)
        => !IsRemoteRef(refName) && Regex.IsMatch(refName, @"^v?\d");

    // Kind brush used for BOTH the outline border and the text of a ref pill.
    // Remote-tracking refs contain a "/" (e.g. origin/main); simple version-like
    // names (v1.2, 2.0) are treated as tags; everything else is a local branch.
    //
    // The three values live in ThemeManager (App.RefBranch/Remote/Tag), per theme,
    // because they are small text and have to clear WCAG AA on App.RefPillBg — which
    // is white in one theme and #252526 in the other, so one hard-coded triple cannot
    // be right for both. The previous hard-coded trio was tuned on white and measured
    // 2.82–2.99:1 in the dark theme; see ThemeManager for the six numbers.
    private static IBrush RefBrush(string refName)
    {
        if (refName.Contains('/'))
        {
            return B("App.RefRemote"); // remote-tracking
        }

        if (Regex.IsMatch(refName, @"^v?\d"))
        {
            return B("App.RefTag"); // tag
        }

        return B("App.RefBranch"); // local branch
    }

    // --- Row context menu -----------------------------------------------------
    //
    // The original menu (RevisionGridControl.Designer.cs + ContextMenuOpening) is
    // CONTEXTUAL: ~60 entries, grouped, each shown/hidden and enabled/disabled from
    // the current selection. Two Avalonia constraints shape this port:
    //
    //  * A popup does NOT re-measure when Items change while it is opening (the
    //    menu collapses to a thin strip). So the menu is built ONCE — every entry,
    //    including the per-ref slots below — and `Opening` only flips
    //    IsVisible/IsEnabled/Header. Nothing is ever added or removed there.
    //  * A ref-targeted entry ("Merge 'master' into current branch…") needs one item
    //    per ref sitting on the row, which for the same reason cannot be created on
    //    demand. Each such operation therefore owns a fixed number of SLOTS whose
    //    header is rewritten on open; rows carrying more refs than that show the
    //    first RefSlotCount of them.
    //
    // ONE menu instance is shared by every row (commit rows and the two artificial
    // rows alike), which also keeps list virtualisation cheap. The row the menu was
    // opened on is captured by a TUNNELLING ContextRequested handler on the list, so
    // it is known before the popup's own bubbling handler opens it.

    // Per-ref slots per operation. Three covers the realistic cases (a commit
    // carrying master + a tag + one topic branch) without a wall of hidden items.
    private const int RefSlotCount = 3;

    private ContextMenu? _rowMenu;

    // One closure per entry, run on Opening: it decides the entry's visibility,
    // enablement and (for ref slots) caption from the selection context.
    private readonly List<Action<MenuCtx>> _menuRules = [];

    // Headers of the host-registered commands that the structured menu already
    // places itself; everything else lands in "Other actions".
    private readonly HashSet<string> _routedCommands = new(StringComparer.OrdinalIgnoreCase);

    // The row the menu was opened on (right-clicked), and the selection context.
    private RevisionRow? _menuRow;

    private readonly BranchTagService _branchTags = new();

    // Ref metadata refreshed with every reload, off the UI thread: the checked-out
    // branch and a name -> kind map ('b' local branch, 'r' remote, 't' tag) used to
    // classify RevisionRow.RefNames exactly, instead of the display-only '/'-and-
    // digits heuristics the badges use.
    private string _currentBranch = string.Empty;
    private Dictionary<string, char> _refKinds = new(StringComparer.Ordinal);

    /// <summary>
    ///  Raised by the menu's "Select in left panel" entry with the ref name to
    ///  reveal (branch or tag). The shell wires this to the repository tree; when
    ///  nothing is subscribed the entry stays disabled.
    /// </summary>
    public event Action<string>? SelectRefInLeftPanelRequested;

    /// <summary>
    ///  Raised after a ref operation from the grid's menu changed HEAD or refs, so
    ///  the shell can refresh every panel at once (same contract as the tree's
    ///  event). When nothing is subscribed the grid reloads itself instead.
    /// </summary>
    public event Action? OperationCompleted;

    // Everything the entries need to decide about themselves. Cheap to build: no
    // git call, only what is already loaded in the view.
    private sealed record MenuCtx(
        RevisionRow Row,
        bool Artificial,
        bool IsIndexRow,
        int SelectionCount,
        bool TwoCommitsSelected,
        IReadOnlyList<string> Local,
        IReadOnlyList<string> Remote,
        IReadOnlyList<string> Tags,
        string CurrentBranch,
        bool BisectInProgress)
    {
        // A single, real commit: the shape almost every commit operation needs.
        public bool SingleCommit => !Artificial && SelectionCount <= 1;

        public bool HasRefs => Local.Count > 0 || Remote.Count > 0 || Tags.Count > 0;
    }

    // Builds (once) and returns the menu shared by every row.
    private ContextMenu RowMenu()
    {
        if (_rowMenu is not null)
        {
            return _rowMenu;
        }

        _menuRules.Clear();
        _routedCommands.Clear();

        ContextMenu menu = new();
        foreach (object item in BuildRowMenuItems())
        {
            menu.Items.Add(item);
        }

        menu.Opening += OnRowMenuOpening;
        _rowMenu = menu;
        return menu;
    }

    // The full entry list, in the original's runtime order (bisect and scripts
    // aside, which live under "Other actions" here).
    private List<object> BuildRowMenuItems()
    {
        List<object> items = [];

        void Add(object? item)
        {
            if (item is MenuItem[] slots)
            {
                // A slot group contributes its entries individually.
                items.AddRange(slots);
                return;
            }

            if (item is not null)
            {
                items.Add(item);
            }
        }

        void Sep() => Add(new Separator());

        // Artificial rows: the one action that makes sense there. The caption
        // switches between working directory / index on open.
        MenuItem artificial = new();
        artificial.Click += (_, _) =>
        {
            if (_menuRow is not { } row || !IsArtificial(row))
            {
                return;
            }

            if (row.Hash == IndexHash)
            {
                CommitIndexSelected?.Invoke();
            }
            else
            {
                WorkingDirectorySelected?.Invoke();
            }
        };
        _menuRules.Add(ctx =>
        {
            artificial.IsVisible = ctx.Artificial;
            artificial.Header = ctx.IsIndexRow
                ? T("Show staged changes")
                : T("Show working directory changes");
        });
        Add(artificial);

        // Copy to clipboard: the original's CopyContextMenuItem, i.e. a submenu over
        // the selected commits (values joined by newlines, duplicates dropped).
        Add(BuildCopySubmenu());
        Sep();

        // --- Branch level ----------------------------------------------------
        Add(RefSlots(
            T("RevisionGridControl/checkoutBranchToolStripMenuItem.Text", "Chec&kout branch..."),
            ctx => Concat(ctx.Local, ctx.Remote),
            name => _ = CheckoutRefAsync(name),
            (ctx, name) => !string.Equals(name, ctx.CurrentBranch, StringComparison.Ordinal)));

        Add(RefSlots(
            T("RevisionGridControl/mergeBranchToolStripMenuItem.Text", "&Merge into current branch..."),
            // Merging the branch you are on is a no-op: the original drops it from
            // the dropdown, so the slot is not shown for it at all.
            ctx => Where(Concat(ctx.Local, ctx.Remote), n => !string.Equals(n, ctx.CurrentBranch, StringComparison.Ordinal)),
            name => _ = MergeRefAsync(name)));

        MenuItem rebase = new()
        {
            Header = Strip(T("RevisionGridControl/rebaseOnToolStripMenuItem.Text", "&Rebase current branch on"))
                + " " + Strip(T("RevisionGridControl/rebaseToolStripMenuItem.Text", "&Selected commit")).ToLowerInvariant()
                + "…",
        };
        rebase.Click += (_, _) => _ = RebaseOnSelectedAsync();
        Rule(rebase, ctx => !ctx.Artificial, ctx => ctx.SingleCommit && !ctx.Row.IsHead && ctx.CurrentBranch.Length > 0);
        Add(rebase);

        // "Reset current branch to here" — the host registers the three modes, so
        // they become the submenu the original opens as a dialog with three radios.
        MenuItem resetCurrent = new()
        {
            Header = Strip(T("RevisionGridControl/resetCurrentBranchToHereToolStripMenuItem.Text", "Reset c&urrent branch to here...")),
        };
        AddRouted(resetCurrent, "Reset (soft) to here", ctx => ctx.SingleCommit);
        AddRouted(resetCurrent, "Reset (mixed) to here", ctx => ctx.SingleCommit);
        AddRouted(resetCurrent, "Reset (HARD) to here…", ctx => ctx.SingleCommit);
        Rule(resetCurrent, ctx => !ctx.Artificial, ctx => ctx.SingleCommit);
        Add(resetCurrent);

        MenuItem resetAnother = new()
        {
            Header = Strip(T("RevisionGridControl/resetAnotherBranchToHereToolStripMenuItem.Text", "Reset an&other branch to here...")),
        };
        resetAnother.Click += (_, _) => _ = ResetAnotherBranchAsync();
        Rule(resetAnother, ctx => !ctx.Artificial, ctx => ctx.SingleCommit);
        Add(resetAnother);

        Sep();

        MenuItem selectInLeftPanel = new()
        {
            Header = Strip(T("RevisionGridControl/tsmiSelectInLeftPanel.Text", "Se&lect in left panel")),
        };
        selectInLeftPanel.Click += (_, _) =>
        {
            if (selectInLeftPanel.Tag is string name && name.Length > 0)
            {
                SelectRefInLeftPanelRequested?.Invoke(name);
            }
        };
        _menuRules.Add(ctx =>
        {
            string? first = FirstOrDefault(Concat(Concat(ctx.Local, ctx.Remote), ctx.Tags));
            selectInLeftPanel.Tag = first;
            selectInLeftPanel.IsVisible = !ctx.Artificial && first is not null;
            selectInLeftPanel.IsEnabled = first is not null && SelectRefInLeftPanelRequested is not null;
        });
        Add(selectInLeftPanel);

        Add(Routed("Create branch here…", ctx => !ctx.Artificial, ctx => ctx.SingleCommit));
        Add(Routed("Create tag here…", ctx => !ctx.Artificial, ctx => ctx.SingleCommit));

        Add(RefSlots(
            T("RevisionGridControl/renameBranchToolStripMenuItem.Text", "R&ename branch..."),
            ctx => ctx.Local,
            name => _ = RenameBranchAsync(name)));

        Add(RefSlots(
            T("RevisionGridControl/deleteBranchToolStripMenuItem.Text", "&Delete branch..."),
            ctx => ctx.Local,
            name => _ = DeleteBranchAsync(name),
            // The original keeps the entry VISIBLE but disabled on the current
            // branch, so the reason it cannot be deleted is discoverable.
            (ctx, name) => !string.Equals(name, ctx.CurrentBranch, StringComparison.Ordinal)));

        Add(RefSlots(
            T("RevisionGridControl/deleteTagToolStripMenuItem.Text", "&Delete tag..."),
            ctx => ctx.Tags,
            name => _ = DeleteTagAsync(name)));

        Sep();

        // --- Commit level ----------------------------------------------------
        Add(Routed("Checkout this commit", ctx => !ctx.Artificial, ctx => ctx.SingleCommit && !ctx.Row.IsHead));
        Add(Routed("Revert this commit…", ctx => !ctx.Artificial, ctx => ctx.SingleCommit));
        Add(Routed("Cherry-pick", ctx => !ctx.Artificial, ctx => ctx.SingleCommit && !ctx.Row.IsHead));
        Add(Routed("Archive this commit…", ctx => !ctx.Artificial, ctx => ctx.SingleCommit));

        MenuItem advanced = new()
        {
            Header = Strip(T("RevisionGridControl/manipulateCommitToolStripMenuItem.Text", "&Advanced")),
        };
        AddRouted(advanced, "Reword commit…", ctx => ctx.SingleCommit);
        AddRouted(advanced, "Squash with previous…", ctx => ctx.SingleCommit);
        AddRouted(advanced, "Fixup with previous…", ctx => ctx.SingleCommit);
        Rule(advanced, ctx => !ctx.Artificial, ctx => ctx.SingleCommit);
        Add(advanced);

        Sep();

        // --- Compare ----------------------------------------------------------
        MenuItem compare = new()
        {
            Header = Strip(T("RevisionGridControl/compareToolStripMenuItem.Text", "Com&pare")),
        };
        AddRouted(compare, "Select as BASE to compare", ctx => ctx.SingleCommit);
        AddRouted(compare, "Compare to BASE", ctx => ctx.SingleCommit);
        AddRouted(compare, "Compare to working directory", ctx => ctx.SingleCommit);
        AddRouted(compare, "Compare to branch…", ctx => ctx.SingleCommit);

        // No upstream menu twin: upstream picks an arbitrary commit from INSIDE its
        // compare window (FormDiff's btnAnotherFirstCommit/btnAnotherSecondCommit →
        // FormChooseCommit, FormDiff.cs:231-240). The port's compare surface is the
        // shared DiffView, which has no chrome of its own, so the picker is offered
        // here, next to the other compare entries.
        AddRouted(compare, "Compare to commit…", ctx => ctx.SingleCommit);

        MenuItem compareSelected = new()
        {
            Header = Strip(T("RevisionGridControl/compareSelectedCommitsMenuItem.Text", "Compare &selected commits")),
        };
        compareSelected.Click += (_, _) => CompareSelectedCommits();
        AddChild(compare, compareSelected, ctx => true, ctx => ctx.TwoCommitsSelected);
        Rule(compare, ctx => !ctx.Artificial);
        Add(compare);

        Sep();

        // --- Navigate ----------------------------------------------------------
        MenuItem navigate = new()
        {
            Header = Strip(T("FormBrowse/navigateToolStripMenuItem.Text", "&Navigate")),
        };
        AddChild(navigate, MakeItem(T("RevisionGrid/GotoFirstParentCommit.Text", "First parent") + "   (Ctrl+P)", GoToParent),
            ctx => true, ctx => ctx.SelectionCount <= 1);
        AddChild(navigate, MakeItem(T("RevisionGrid/GotoChildCommit.Text", "Nearest child") + "   (Ctrl+N)", GoToChild),
            ctx => true, ctx => ctx.SelectionCount <= 1);
        AddChild(
            navigate,
            MakeItem(T("RevisionGrid/GotoMergeBaseCommit.Text", "Common ancestor (merge base)") + "   (Ctrl+Shift+K)", GoToMergeBase),
            ctx => !ctx.Artificial,
            ctx => ctx.SelectionCount is 1 or 2);
        AddChild(navigate, MakeItem(T("RevisionGrid/GotoCurrentRevision.Text", "Go to current revision") + "   (Ctrl+Shift+C)", GoToCurrentRevision));
        AddChild(navigate, MakeItem(T("FormGoToCommit/$this.Text", "Go to commit") + "…   (Ctrl+Shift+G)", () => _ = GoToCommitPromptAsync()));
        AddChild(navigate, MakeItem(T("RevisionGrid/NavigateBackward.Text", "Backward") + "   (Alt+←)", () => NavigateBack()));
        AddChild(navigate, MakeItem(T("RevisionGrid/NavigateForward.Text", "Forward") + "   (Alt+→)", () => NavigateForward()));
        Rule(navigate, ctx => true);
        Add(navigate);

        // --- Other actions ------------------------------------------------------
        MenuItem other = new()
        {
            Header = Strip(T("RevisionGridControl/tsmiOtherActions.Text", "&Other actions")),
        };
        // Bisect. Upstream gates the four in-session entries on
        // Module.InTheMiddleOfBisect() (RevisionGridControl.cs:2256-2261) and offers
        // no way to start one from here at all — the start lives in FormBisect. This
        // port needs the start reachable from the grid, because the grid is where the
        // commit you want to bound the range with is; it opens the same panel rather
        // than running `git bisect start` behind the menu, which is what these
        // entries used to do implicitly for you.
        AddRouted(
            other,
            "Bisect: start…",
            ctx => !ctx.BisectInProgress,
            T("FormBisect/Start.Text", "Start bisect") + "…",
            "Bisect");
        AddRouted(
            other,
            "Bisect: mark good",
            ctx => ctx.SingleCommit && ctx.BisectInProgress,
            T("RevisionGridControl/markRevisionAsGoodToolStripMenuItem.Text", "Mark revision as good"),
            "BisectGood");
        AddRouted(
            other,
            "Bisect: mark bad",
            ctx => ctx.SingleCommit && ctx.BisectInProgress,
            T("RevisionGridControl/markRevisionAsBadToolStripMenuItem.Text", "Mark revision as bad"),
            "BisectBad");
        AddRouted(
            other,
            "Bisect: skip",
            ctx => ctx.SingleCommit && ctx.BisectInProgress,
            T("RevisionGridControl/bisectSkipRevisionToolStripMenuItem.Text", "Skip revision"),
            "BisectSkip");
        AddRouted(
            other,
            "Bisect: stop/reset",
            ctx => ctx.BisectInProgress,
            T("RevisionGridControl/stopBisectToolStripMenuItem.Text", "Stop bisect"),
            "BisectStop");

        // Anything the host registered that this menu does not place explicitly
        // still has to appear (AddCommitCommand is the shell's only hook), so it
        // lands here rather than being dropped.
        // The scripts go with them, under the same "Other actions" heading: they are
        // commands on the selected commit, which is exactly what that submenu holds.
        foreach ((string header, Action<string> handler) in _commitCommands.Concat(_scriptCommands))
        {
            if (_routedCommands.Contains(header))
            {
                continue;
            }

            MenuItem leftover = new() { Header = header };
            Action<string> captured = handler;
            leftover.Click += (_, _) =>
            {
                if (_menuRow is { } row)
                {
                    captured(row.Hash);
                }
            };
            AddChild(other, leftover, ctx => !ctx.Artificial, ctx => ctx.SingleCommit);
        }

        Rule(other, ctx => true);
        Add(other);

        // "View in GitHub" — upstream's IRepositoryHostPlugin.ConfigureContextMenu,
        // which it only ever calls for the BLAME menu. The commit page is at least as
        // useful from here: it is where the pull request, the checks and the review
        // conversation live, none of which the local repository knows about.
        //
        // One entry per hosted remote when there is more than one: a commit that exists
        // in a fork and in its parent has two pages, and only the reader knows which
        // one they meant.
        MenuItem view = new() { Header = TranslationService.TFormat(null, "View in {0}", "GitHub") };
        Separator viewSeparator = new();
        _menuRules.Add(ctx =>
        {
            IReadOnlyList<GitHubHostedRemote> remotes = GitHubRemotes();
            view.IsVisible = remotes.Count > 0 && !ctx.Artificial && ctx.SingleCommit;
            viewSeparator.IsVisible = view.IsVisible;
            if (!view.IsVisible)
            {
                return;
            }

            view.Items.Clear();
            GitHubService service = new();
            foreach (GitHubHostedRemote remote in remotes)
            {
                GitHubHostedRemote captured = remote;
                MenuItem entry = new() { Header = captured.Data.Replace("_", "__") };
                entry.Click += (_, _) =>
                {
                    if (_menuRow is { } row)
                    {
                        new ExternalToolService().OpenUrl(service.CommitUrl(captured, row.Hash));
                    }
                };
                view.Items.Add(entry);
            }
        });
        Add(viewSeparator);
        Add(view);

        return items;
    }

    /// <summary>
    ///  This repository's remotes that live on the configured GitHub host, computed on
    ///  first use and kept until the repository changes.
    /// </summary>
    private IReadOnlyList<GitHubHostedRemote> GitHubRemotes()
        => _gitHubRemotes ??= _repoPath.Length == 0
            ? []
            : new GitHubService().GetHostedRemotes(_repoPath);

    private IReadOnlyList<GitHubHostedRemote>? _gitHubRemotes;

    // The Copy submenu: hash / short hash / message / author / dates / refs, each
    // over the whole selection like the original's CopyContextMenuItem.
    private MenuItem BuildCopySubmenu()
    {
        MenuItem copy = new()
        {
            Header = Strip(T("RevisionGridControl/copyToClipboardToolStripMenuItem.Text", "&Copy to clipboard")),
        };

        void Entry(string caption, Func<RevisionRow, string> value, Func<MenuCtx, bool>? visible = null)
        {
            MenuItem item = new() { Header = caption };
            item.Click += (_, _) => CopySelected(value);
            AddChild(copy, item, visible ?? (ctx => true));
        }

        Entry(Strip(T("TranslatedStrings/_commitHashText.Text", "Commit hash")), r => r.Hash);
        Entry(T("Short hash"), r => r.ShortHash);
        Entry(Strip(T("TranslatedStrings/_message.Text", "Message")), r => r.Subject);
        Entry(Strip(T("TranslatedStrings/_author.Text", "Author")), r =>
            r.AuthorEmail.Length > 0 ? $"{r.Author} <{r.AuthorEmail}>" : r.Author);
        Entry(T("TranslatedStrings/_authorDateText.Text", "Author date"), r => r.AuthorDate.ToString("yyyy-MM-dd HH:mm:ss"));
        Entry(T("TranslatedStrings/_commitDateText.Text", "Commit date"), r => r.CommitDate.ToString("yyyy-MM-dd HH:mm:ss"));
        Entry(
            $"{Strip(T("TranslatedStrings/_branchesText.Text", "Branches"))} / {Strip(T("TranslatedStrings/_tagsText.Text", "Tags"))}",
            r => string.Join(" ", r.RefNames),
            ctx => ctx.HasRefs);

        Rule(copy, ctx => !ctx.Artificial);
        return copy;
    }

    // Copies one field of every selected commit (duplicates dropped, newline
    // separated), falling back to the right-clicked row when nothing is selected.
    private void CopySelected(Func<RevisionRow, string> value)
    {
        List<RevisionRow> rows = SelectedCommits();
        if (rows.Count == 0 && _menuRow is { } row && !IsArtificial(row))
        {
            rows.Add(row);
        }

        string text = string.Join(
            "\n",
            rows.Select(value).Where(v => !string.IsNullOrEmpty(v)).Distinct());

        Copy(text);
    }

    private List<RevisionRow> SelectedCommits()
        => _list.SelectedItems is { Count: > 0 } items
            ? items.OfType<RevisionRow>().Where(r => !IsArtificial(r)).ToList()
            : [];

    /// <summary>The selection last announced through <see cref="RangeSelected"/>, so
    /// the second SelectionChanged of one Ctrl-click does not repeat it.</summary>
    private IReadOnlyList<string>? _announcedRange;

    /// <summary>
    ///  The real commits of a multi-row selection, NEWEST FIRST.
    ///
    ///  <para>Ordered by ROW INDEX and not by the order the user clicked in: which
    ///  revision is "the selected one" and which are the bases must not depend on
    ///  which end of the range was picked first, and the whole multi-diff shape
    ///  upstream computes is keyed on that order
    ///  (<c>revisions[0]</c>, <c>revisions[1]</c>, <c>revisions[2]</c>).</para>
    ///
    ///  <para>Artificial rows are skipped — a "working directory" row caught inside a
    ///  Shift-run is not a commit and cannot take part in a comparison of commits.</para>
    /// </summary>
    private List<string> SelectedRevisionsNewestFirst(System.Collections.IList selection)
    {
        List<RevisionRow> rows = [];
        foreach (object? item in selection)
        {
            if (item is RevisionRow row && !IsArtificial(row))
            {
                rows.Add(row);
            }
        }

        return [.. rows.OrderBy(r => _list.Items.IndexOf(r)).Select(r => r.Hash)];
    }

    // --- Menu wiring helpers ---------------------------------------------------

    private static string Strip(string caption) => RevisionFilterDialog.StripMnemonic(caption);

    private static MenuItem MakeItem(string header, Action action)
    {
        // Escaped, not just stripped: half these captions quote a ref name, and an
        // underscore in one is a character and not an access key (see Theming/MenuText).
        MenuItem item = new() { Header = Theming.MenuText.Escape(Strip(header)) };
        item.Click += (_, _) => action();
        return item;
    }

    // Registers the visibility/enablement rule of an entry.
    private void Rule(MenuItem item, Func<MenuCtx, bool> visible, Func<MenuCtx, bool>? enabled = null)
    {
        _menuRules.Add(ctx =>
        {
            item.IsVisible = visible(ctx);
            item.IsEnabled = enabled?.Invoke(ctx) ?? true;
        });
    }

    private void AddChild(MenuItem parent, MenuItem child, Func<MenuCtx, bool>? visible = null, Func<MenuCtx, bool>? enabled = null)
    {
        parent.Items.Add(child);
        Rule(child, visible ?? (_ => true), enabled);
    }

    // Creates an entry driven by a host-registered command (AddCommitCommand). The
    // header is matched exactly; a command the shell did not register simply has no
    // entry, so the menu never shows a dead item.
    /// <param name="caption">
    ///  What the entry reads, when that has to differ from the registration key —
    ///  the key is the host's contract and stays English, while the caption can be a
    ///  translated upstream resource string. Omitted, the key itself is shown, which
    ///  is how every other entry here works.
    /// </param>
    /// <param name="icon">
    ///  Base name of an upstream icon asset (case-sensitive, see
    ///  <see cref="IconLoader"/>). A name that does not resolve leaves the entry
    ///  without an icon rather than blank.
    /// </param>
    private MenuItem? Routed(
        string header,
        Func<MenuCtx, bool> visible,
        Func<MenuCtx, bool>? enabled = null,
        string? caption = null,
        string? icon = null)
    {
        foreach ((string registered, Action<string> handler) in _commitCommands)
        {
            if (!string.Equals(registered, header, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _routedCommands.Add(registered);
            MenuItem item = new() { Header = Strip(caption ?? registered) };

            if (icon is { Length: > 0 } && IconLoader.Image(icon) is { } image)
            {
                item.Icon = image;
            }

            Action<string> captured = handler;
            item.Click += (_, _) =>
            {
                if (_menuRow is { } row)
                {
                    captured(row.Hash);
                }
            };

            Rule(item, visible, enabled);
            return item;
        }

        return null;
    }

    private void AddRouted(
        MenuItem parent,
        string header,
        Func<MenuCtx, bool> enabled,
        string? caption = null,
        string? icon = null)
    {
        if (Routed(header, ctx => true, enabled, caption, icon) is { } item)
        {
            parent.Items.Add(item);
        }
    }

    // A group of RefSlotCount interchangeable entries for a ref-targeted operation.
    // Created once; on open each slot either takes the n-th ref of the row (caption
    // rewritten to name it) or hides itself.
    private MenuItem[] RefSlots(
        string caption,
        Func<MenuCtx, IReadOnlyList<string>> refsOf,
        Action<string> action,
        Func<MenuCtx, string, bool>? enabled = null)
    {
        string baseCaption = Strip(caption).TrimEnd('.', '…', ' ');
        MenuItem[] slots = new MenuItem[RefSlotCount];

        for (int i = 0; i < RefSlotCount; i++)
        {
            int index = i;
            MenuItem item = new();
            item.Click += (_, _) =>
            {
                if (item.Tag is string name && name.Length > 0)
                {
                    action(name);
                }
            };

            _menuRules.Add(ctx =>
            {
                IReadOnlyList<string> names = ctx.Artificial || ctx.SelectionCount > 1
                    ? []
                    : refsOf(ctx);

                if (index >= names.Count)
                {
                    item.IsVisible = false;
                    item.Tag = null;
                    return;
                }

                string name = names[index];
                item.Tag = name;
                item.Header = Theming.MenuText.Escape($"{baseCaption} '{name}'…");
                item.IsVisible = true;
                item.IsEnabled = enabled?.Invoke(ctx, name) ?? true;
            });

            slots[i] = item;
        }

        return slots;
    }

    /// <summary>
    ///  What kind of ref a name is — <c>'b'</c> local branch, <c>'r'</c> remote-tracking,
    ///  <c>'t'</c> tag — from the listing git gave us, falling back to the display
    ///  heuristics only for a name the listing does not know yet (a walk can outrun the
    ///  ref refresh).
    ///
    ///  <para><b>The heuristics are not good enough to decide with.</b> They read a
    ///  slash as "remote" and a leading digit as "tag", so a perfectly ordinary local
    ///  branch called <c>feat/PO-52-activation-ladder</c> was classified as remote —
    ///  which is how the checked-out branch stopped getting its ▶ for anyone whose
    ///  branch names contain a slash. They stay as the fallback, and nothing decides
    ///  on them while the map has an answer.</para>
    /// </summary>
    private char RefKindOf(string refName)
        => _refKinds.TryGetValue(refName, out char known)
            ? known
            : IsTagRef(refName) ? 't' : IsRemoteRef(refName) ? 'r' : 'b';

    // Splits the row's refs into local branches / remote branches / tags using the
    // kind map loaded with the repository; falls back to the badge heuristics for a
    // ref the map does not know (a walk can outrun the ref refresh).
    private (List<string> Local, List<string> Remote, List<string> Tags) ClassifyRefs(RevisionRow row)
    {
        List<string> local = [];
        List<string> remote = [];
        List<string> tags = [];

        foreach (string name in row.RefNames)
        {
            switch (RefKindOf(name))
            {
                case 't': tags.Add(name); break;
                case 'r': remote.Add(name); break;
                default: local.Add(name); break;
            }
        }

        return (local, remote, tags);
    }

    private MenuCtx BuildMenuContext(RevisionRow row)
    {
        (List<string> local, List<string> remote, List<string> tags) = ClassifyRefs(row);
        List<RevisionRow> selection = SelectedCommits();
        int count = _list.SelectedItems?.Count ?? 0;

        return new MenuCtx(
            row,
            IsArtificial(row),
            row.Hash == IndexHash,
            count,
            selection.Count == 2,
            local,
            remote,
            tags,
            _currentBranch,
            ReadBisectState());
    }

    /// <summary>
    ///  Whether a bisect session is open, asked at menu-open time. Upstream asks the
    ///  identical question in the identical place and just as synchronously —
    ///  <c>RevisionGridControl.cs:2256</c> calls <c>Module.InTheMiddleOfBisect()</c>,
    ///  which is one <c>File.Exists</c> on <c>.git/BISECT_START</c>
    ///  (<c>GitModule.cs:1968-1971</c>). No process is spawned, so the UI-thread rule
    ///  about the blocking services is not in play here.
    ///
    ///  <para>Unset (no host), or on any failure, answers false: the bisect entries
    ///  then stay disabled, which is the safe direction — the alternative is offering
    ///  a mark that git would reject.</para>
    /// </summary>
    private bool ReadBisectState()
    {
        try
        {
            return IsBisectInProgress?.Invoke() ?? false;
        }
        catch
        {
            return false;
        }
    }

    // Applies every rule, then collapses submenus with no visible child and
    // separators that would end up leading, trailing or doubled — the equivalent of
    // the original's UpdateSeparators().
    private void OnRowMenuOpening(object? sender, CancelEventArgs e)
    {
        RevisionRow? row = _menuRow ?? _list.SelectedItem as RevisionRow;
        if (row is null || _rowMenu is null)
        {
            e.Cancel = true;
            return;
        }

        _menuRow = row;
        MenuCtx ctx = BuildMenuContext(row);
        foreach (Action<MenuCtx> rule in _menuRules)
        {
            rule(ctx);
        }

        foreach (object? item in _rowMenu.Items)
        {
            if (item is MenuItem { ItemCount: > 0, IsVisible: true } parent)
            {
                parent.IsVisible = parent.Items.OfType<MenuItem>().Any(child => child.IsVisible);
            }
        }

        bool seenItem = false;
        Separator? pending = null;
        foreach (object? item in _rowMenu.Items)
        {
            if (item is Separator separator)
            {
                separator.IsVisible = false;
                pending = seenItem ? separator : null;
                seenItem = false;
            }
            else if (item is Control control && control.IsVisible)
            {
                if (pending is not null)
                {
                    pending.IsVisible = true;
                    pending = null;
                }

                seenItem = true;
            }
        }
    }

    // Captures the row a right-click landed on BEFORE the popup opens, and makes
    // that row the selection unless it is already part of a multi-selection (so a
    // two-commit compare survives a right-click inside it).
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        RevisionRow? row = (e.Source as Visual)?
            .FindAncestorOfType<ListBoxItem>(includeSelf: true)?
            .DataContext as RevisionRow;

        if (row is null)
        {
            return;
        }

        _menuRow = row;
        if (_list.SelectedItems is not { } selection || !selection.Contains(row))
        {
            SelectRow(row);
        }
    }

    // --- Menu operations -------------------------------------------------------
    //
    // Every one of these runs git in Task.Run and comes back to the UI thread
    // through the dispatcher (M43: a sync-over-async git call on the UI thread
    // freezes the whole window). Destructive ones ask first.

    private void CompareSelectedCommits()
    {
        List<RevisionRow> rows = SelectedCommits();
        if (rows.Count != 2)
        {
            return;
        }

        // Newest first, exactly as the grid's own selection announcement orders it —
        // SelectedCommits() answers in click order, which says nothing about age.
        RangeSelected?.Invoke([.. rows.OrderBy(r => _list.Items.IndexOf(r)).Select(r => r.Hash)]);
    }

    private void GoToCurrentRevision()
    {
        foreach (RevisionRow row in _rows)
        {
            if (row.IsHead)
            {
                string? from = CurrentHash;
                if (SelectByHash(row.Hash))
                {
                    PushHistory(from);
                }

                return;
            }
        }

        FlashStatus(T("The current revision is not in the loaded history."));
    }

    private async Task GoToCommitPromptAsync()
    {
        if (await PromptAsync(T("FormGoToCommit/$this.Text", "Go to commit"), string.Empty, T("FormGoToCommit/$this.Text", "Go to commit")) is { Length: > 0 } hash)
        {
            GoToCommit(hash);
        }
    }

    private async Task CheckoutRefAsync(string name)
    {
        if (_repoPath.Length == 0)
        {
            return;
        }

        // Same dialog the rest of the port uses (M47/F4): it decides what happens to
        // local changes and returns immediately on a clean tree.
        GitCommands.LocalChangesAction? action = await CheckoutBranchDialog.AskAsync(
            TopLevel.GetTopLevel(this) as Window, _repoPath, name);

        if (action is not { } changes)
        {
            return;
        }

        // The checkout itself runs inside the process dialog (as upstream's
        // FormCheckoutBranch does through FormProcess), so there is no RunRefOp
        // wrapper here — it would check out a second time.
        bool ok = await RefProcessRunner.CheckoutAsync(
            TopLevel.GetTopLevel(this) as Window, _repoPath, name, changes, service: _branchTags);

        // Reloaded on failure too: an aborted checkout can leave HEAD or the working
        // tree already moved, and the grid must show what the repository is now.
        AfterRefOp(ok
            ? string.Format(T("Checked out {0}."), name)
            : string.Format(T("Checkout of {0} did not complete."), name));
    }

    private async Task MergeRefAsync(string name)
    {
        if (_repoPath.Length == 0 || _currentBranch.Length == 0)
        {
            FlashStatus(T("TranslatedStrings/_errorCaptionNotOnBranch.Text", "Not on a branch"));
            return;
        }

        // The merge configuration dialog (port of FormMergeBranch) replaces both the
        // bare confirmation and the hard-wired options, and runs `git merge` itself
        // through the process dialog — hence no RunRefOp wrapper here, which would
        // merge a second time.
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        MergeDialogResult? result = await MergeDialog.ShowAsync(owner, _repoPath, name);
        if (result is null)
        {
            return;
        }

        AfterRefOp(result.Success
            ? string.Format(T("Merged {0}."), result.Branch)
            : string.Format(T("Merge of {0} did not complete."), result.Branch));

        // Conflicts left by the merge: ask, as upstream does.
        await ConflictFlow.HandleAsync(owner, _repoPath);
        AfterRefOp(string.Empty);
    }

    private async Task RebaseOnSelectedAsync()
    {
        if (_repoPath.Length == 0 || _menuRow is not { } row || IsArtificial(row))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        // The yes/no "are you sure you want to rewrite history" that used to stand here
        // is gone on purpose: the options dialog replaces it. Upstream reaches the same
        // command through FormRebase and asks no such question either — a window that
        // names the target, lists the options and shows the git command line is a
        // better confirmation than a prompt that shows none of them, and it is the only
        // way to reach the options at all.
        RebaseDialogResult? result = await RebaseDialog.ShowAsync(owner, _repoPath, row.Hash);
        if (result is not { Executed: true })
        {
            return;
        }

        AfterRefOp(result.Success
            ? string.Format(T("Rebased on {0}."), row.ShortHash)
            : FirstLine(result.Output));

        // A stopped rebase now has real buttons in the banner, so asking here leads
        // somewhere: resolve, then Continue / Skip / Abort.
        if (await ConflictFlow.HandleAsync(owner, _repoPath) is { HadConflicts: true })
        {
            AfterRefOp(string.Empty);
        }
    }

    private async Task ResetAnotherBranchAsync()
    {
        if (_repoPath.Length == 0 || _menuRow is not { } row || IsArtificial(row))
        {
            return;
        }

        (List<string> local, _, _) = ClassifyRefs(row);
        string suggestion = local.FirstOrDefault(n => !string.Equals(n, _currentBranch, StringComparison.Ordinal)) ?? string.Empty;

        if (await PromptAsync(
                string.Format(T("Move which branch to {0}?"), row.ShortHash),
                suggestion,
                Strip(T("RevisionGridControl/resetAnotherBranchToHereToolStripMenuItem.Text", "Reset an&other branch to here...")))
            is not { Length: > 0 } branch)
        {
            return;
        }

        string target = row.Hash;

        // Would the move orphan commits? Upstream asks this before letting the reset
        // through, and since e3206275a it asks with "git merge-base --is-ancestor"
        // rather than a dry-run push to the repository itself (which LFS breaks). Off
        // the UI thread: it is a git call.
        bool lossy = await Task.Run(() => _branchTags.ResetWouldLoseCommits(_repoPath, branch, target));

        // Upstream's own wording for this hazard, so a translated build says it in the
        // user's language. Shown only when the answer is "yes, commits would be lost";
        // upstream prints it unconditionally because its dialog has room for a standing
        // explanation, where this is a one-shot confirmation.
        string warning = lossy
            ? " " + T("FormResetAnotherBranch/lblResetBranchWarning.Text",
                "You can only reset a branch safely if there is a direct path from it to selected revision.\n"
                + "Forcing a branch to reset if it has not been merged might leave some commits unreachable.")
            : string.Empty;

        if (!await ConfirmAsync(string.Format(
                T("Move branch '{0}' to commit {1}? {2}"),
                branch,
                row.ShortHash,
                T("TranslatedStrings/_cannotBeUndone.Text", "This action cannot be undone.")) + warning))
        {
            return;
        }

        RunRefOp(
            string.Format(T("Moving {0}…"), branch),
            repo => _branchTags.ResetBranchTo(repo, branch, target));
    }

    private async Task RenameBranchAsync(string name)
    {
        if (_repoPath.Length == 0)
        {
            return;
        }

        if (await PromptAsync(
                string.Format(T("New name for branch '{0}':"), name),
                name,
                Strip(T("RevisionGridControl/renameBranchToolStripMenuItem.Text", "R&ename branch...")))
            is not { Length: > 0 } renamed || renamed == name)
        {
            return;
        }

        RunRefOp(
            string.Format(T("Renaming {0}…"), name),
            repo => _branchTags.RenameBranch(repo, name, renamed));
    }

    private async Task DeleteBranchAsync(string name)
    {
        if (_repoPath.Length == 0)
        {
            return;
        }

        if (!await ConfirmAsync(string.Format(T("Delete branch '{0}'?"), name)))
        {
            return;
        }

        // Plain delete first: git refuses an unmerged branch, and only then is the
        // second (explicitly destructive) question asked.
        BranchTagResult result = await Task.Run(() => _branchTags.DeleteBranch(_repoPath, name, force: false));
        if (result.Success)
        {
            AfterRefOp(string.Format(T("Deleted branch {0}."), name));
            return;
        }

        if (!result.Output.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            FlashStatus(FirstLine(result.Output));
            return;
        }

        if (!await ConfirmAsync(string.Format(
                T("Branch '{0}' is not fully merged. Delete it anyway and lose its commits?"),
                name)))
        {
            return;
        }

        RunRefOp(
            string.Format(T("Deleting {0}…"), name),
            repo => _branchTags.DeleteBranch(repo, name, force: true));
    }

    private async Task DeleteTagAsync(string name)
    {
        if (_repoPath.Length == 0)
        {
            return;
        }

        if (!await ConfirmAsync(string.Format(T("Delete tag '{0}'?"), name)))
        {
            return;
        }

        RunRefOp(
            string.Format(T("Deleting tag {0}…"), name),
            repo => _branchTags.DeleteTag(repo, name));
    }

    // Runs a ref mutation off the UI thread and, on success, reloads the walk (the
    // refs drawn on the rows have just changed) — failures surface git's own first
    // line in the status bar instead of throwing.
    private void RunRefOp(string busy, Func<string, BranchTagResult> work)
    {
        string repo = _repoPath;
        if (repo.Length == 0)
        {
            return;
        }

        FlashStatus(busy);
        _ = Task.Run(() =>
        {
            BranchTagResult result;
            try
            {
                result = work(repo);
            }
            catch (Exception ex)
            {
                result = new BranchTagResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (result.Success)
                {
                    AfterRefOp(FirstLine(result.Output) is { Length: > 0 } line ? line : busy);
                }
                else
                {
                    FlashStatus(FirstLine(result.Output));
                }
            });
        });
    }

    private void AfterRefOp(string message)
    {
        // The shell's handler reloads every panel, this grid included (and tells the
        // watcher the window is up to date), so reloading here as well would be a
        // second pass over the same commits.
        if (OperationCompleted is { } completed)
        {
            completed();
        }
        else
        {
            Reload();
        }

        RefreshRefContext();
        FlashStatus(message);
    }

    private static string FirstLine(string? output)
    {
        string text = (output ?? string.Empty).Trim();
        int end = text.IndexOf('\n');
        return end < 0 ? text : text[..end].Trim();
    }

    // Reloads the checked-out branch and the ref-kind map used by the menu's
    // predicates. Off the UI thread; failures leave the previous values in place.
    // Whether two ref-kind maps say the same thing. Cheap enough to run on every
    // refresh (a few hundred names) and it is what stops an unchanged listing from
    // re-templating every visible row.
    private static bool SameKinds(IReadOnlyDictionary<string, char> a, IReadOnlyDictionary<string, char> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach ((string name, char kind) in a)
        {
            if (!b.TryGetValue(name, out char other) || other != kind)
            {
                return false;
            }
        }

        return true;
    }

    private void RefreshRefContext()
    {
        string repo = _repoPath;
        if (repo.Length == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                string current = _branchTags.GetCurrentBranch(repo);
                BranchTagListing listing = _branchTags.LoadRefs(repo);

                Dictionary<string, char> kinds = new(StringComparer.Ordinal);
                List<(string Name, char Kind)> catalogue = [];
                foreach (BranchTagRow branch in listing.Branches)
                {
                    char kind = branch.IsRemote ? 'r' : 'b';
                    kinds[branch.Name] = kind;
                    catalogue.Add((branch.Name, kind));
                }

                foreach (BranchTagRow tag in listing.Tags)
                {
                    kinds[tag.Name] = 't';
                    catalogue.Add((tag.Name, 't'));
                }

                Dispatcher.UIThread.Post(() =>
                {
                    // The rows may already be on screen: this listing and the walk are
                    // two independent background reads, and which one lands first is a
                    // coin toss. Whoever is second has to say so, or the badges keep
                    // whatever the earlier answer implied — which is how the ▶ went
                    // missing after a checkout made OUTSIDE the app: the walk came back
                    // with the new HEAD while `_currentBranch` was still the old branch,
                    // and nothing rebuilt the rows once it caught up.
                    bool changed = !string.Equals(_currentBranch, current, StringComparison.Ordinal)
                        || !SameKinds(_refKinds, kinds);

                    _currentBranch = current;
                    _refKinds = kinds;

                    // The same listing feeds the "Filtered branches" ref picker, so
                    // it never runs a git call of its own.
                    SetRefCatalogue(catalogue);

                    if (changed && _rows.Count > 0)
                    {
                        RefreshView();
                    }
                });
            }
            catch (Exception)
            {
                // Best effort: the menu falls back to the display heuristics.
            }
        });
    }

    // Minimal modal yes/no confirmation (same pattern as the tree/panels); allows
    // the action when there is no window to own the dialog (headless).
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return true;
        }

        TaskCompletionSource<bool> tcs = new();
        Button yes = new() { Content = T("TranslatedStrings/_yes.Text", "Yes"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("TranslatedStrings/_no.Text", "No") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Confirm"),
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("App.Panel"),
        };
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = B("App.Text") });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    // Minimal modal text prompt; null when cancelled or headless.
    private async Task<string?> PromptAsync(string message, string initial, string title)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        TaskCompletionSource<string?> tcs = new();
        TextBox input = new() { Text = initial };
        Button ok = new() { Content = T("TranslatedStrings/_okText.Text", "OK"), Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = B("App.Panel"),
        };
        ok.Click += (_, _) => { tcs.TrySetResult(input.Text?.Trim()); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                tcs.TrySetResult(input.Text?.Trim());
                dialog.Close();
                e.Handled = true;
            }
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = B("App.Text") });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    private static IReadOnlyList<string> Concat(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (second.Count == 0)
        {
            return first;
        }

        List<string> all = new(first.Count + second.Count);
        all.AddRange(first);
        all.AddRange(second);
        return all;
    }

    private static IReadOnlyList<string> Where(IReadOnlyList<string> names, Func<string, bool> predicate)
        => names.Where(predicate).ToList();

    private static string? FirstOrDefault(IReadOnlyList<string> names) => names.Count > 0 ? names[0] : null;

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private static TextBlock AddCell(Grid grid, int column, string text, IBrush? foreground = null, bool bold = false, bool monospace = false)
    {
        TextBlock block = new()
        {
            Text = text,
            Foreground = foreground ?? B("App.Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        };

        if (monospace)
        {
            block.FontFamily = Theming.AppFonts.Monospace;
        }

        // Air between a column divider and the text that starts right after it. Only the
        // three columns that HAVE a divider on their left (author, date, commit id): the
        // graph and the subject begin at the edge of the grid, where there is nothing to
        // stand clear of. Applied here rather than at the call sites so a header cell and
        // the row cells under it can never drift apart — they all come through here.
        if (column >= 3)
        {
            block.Margin = new Thickness(ColumnTextInset, 0, 0, 0);
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
        return block;
    }

    // --- Offline author avatars (identicons) ---------------------------------
    //
    // The original Git Extensions fetches gravatar images over the network. On
    // Linux we avoid any network call and instead synthesise a deterministic
    // "identicon" per author entirely offline: a 5x5, left/right-mirrored block
    // pattern with a hue, both derived from a stable hash of the author's email
    // (or name when no email is present). The computed pattern is cached per
    // author key so building it is a dictionary lookup across all 200 rows.

    // Per-author identicon cache, keyed by lower-cased email (or name fallback).
    private static readonly Dictionary<string, Identicon> _avatarCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Returns the cached identicon for an author, computing it once on first use.
    private static Identicon GetIdenticon(string? email, string? author)
    {
        string key = !string.IsNullOrWhiteSpace(email)
            ? email!.Trim().ToLowerInvariant()
            : (author ?? string.Empty).Trim().ToLowerInvariant();

        if (_avatarCache.TryGetValue(key, out Identicon cached))
        {
            return cached;
        }

        Identicon icon = Identicon.Create(key);
        _avatarCache[key] = icon;
        return icon;
    }

    // A precomputed identicon "recipe": a 5x5 on/off block grid (already mirrored)
    // plus a foreground colour, both derived deterministically from a hash of the
    // author key. Cached and rendered by <see cref="AvatarControl"/>.
    private readonly struct Identicon
    {
        public bool[,] Cells { get; }

        public Color Foreground { get; }

        private Identicon(bool[,] cells, Color foreground)
        {
            Cells = cells;
            Foreground = foreground;
        }

        public static Identicon Create(string key)
        {
            ulong h = Fnv1a64(key);

            // Hue from the high bits; keep saturation/lightness fixed for a
            // consistent, readable look on the dark theme.
            double hue = (h >> 40) % 360;
            Color fg = FromHsl(hue, 0.55, 0.60);

            // 5x5 grid, mirrored left-to-right: decide the left 3 columns
            // (indices 0..2, index 2 being the centre) from 15 hash bits, then
            // mirror columns 0/1 onto 4/3.
            bool[,] cells = new bool[5, 5];
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    bool on = ((h >> ((r * 3) + c)) & 1UL) == 1UL;
                    cells[r, c] = on;
                    cells[r, 4 - c] = on;
                }
            }

            return new Identicon(cells, fg);
        }

        // FNV-1a 64-bit hash — process-stable (unlike string.GetHashCode), so the
        // same author always maps to the same identicon across runs.
        private static ulong Fnv1a64(string s)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s))
            {
                hash ^= b;
                hash *= prime;
            }

            return hash;
        }

        // Minimal HSL -> RGB (h in [0,360), s/l in [0,1]).
        private static Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs((2 * l) - 1)) * s;
            double x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
            double m = l - (c / 2);
            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }
    }

    /// <summary>
    ///  Root visual of one revision row. It owns the row background so the fill can
    ///  span the full grid width (all columns, no margin) and repaints itself — and
    ///  the cells and DAG lanes it tracks (ref pills paint themselves: their surface
    ///  is the themed, opaque App.RefPillBg) — whenever its
    ///  <see cref="ListBoxItem"/> becomes selected / focused / hovered.
    ///  <para>
    ///  Selected rows are filled with solid <c>App.Accent</c> blue and their text is
    ///  switched to white, matching the original Windows grid. When the grid does not
    ///  own the keyboard focus the fill is muted (accent mixed toward gray) so an
    ///  inactive selection still reads as blue but is clearly less loud; within a
    ///  multi-selection the row that has focus additionally gets a light focus
    ///  rectangle, keeping "focused row" and "selected row" distinguishable.
    ///  </para>
    /// </summary>
    private sealed class RevisionRowView : Border
    {
        private static readonly IBrush SelectedText = new SolidColorBrush(Colors.White);
        private static readonly IBrush SelectedTextDim = new SolidColorBrush(Color.FromRgb(0xDF, 0xEC, 0xFA));
        private static readonly IBrush SelectedMarker = new SolidColorBrush(Color.FromRgb(0x9C, 0xF0, 0xB8));
        private static readonly IBrush FocusRect = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

        // Fallbacks used only when the themed brushes are missing/non-solid.
        private static readonly Color AccentFallback = Color.FromRgb(0x00, 0x7A, 0xCC);
        private static readonly Color SelectionFallback = Color.FromRgb(0x09, 0x47, 0x71);
        private static readonly Color AccentFillFallback = Color.FromRgb(0x21, 0x5B, 0xDD);

        private readonly IBrush _normalBg;
        private readonly Border _focusRect;
        private readonly List<(TextBlock Block, IBrush Normal, IBrush Selected)> _texts = [];
        private readonly List<(TextBlock Marker, IBrush Normal)> _markers = [];
        private readonly List<RevisionGraphControl> _graphs = [];

        private ListBoxItem? _item;
        private ListBox? _owner;

        public RevisionRowView(IBrush normalBackground, Control content)
        {
            _normalBg = normalBackground;
            Background = normalBackground;

            _focusRect = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                IsHitTestVisible = false,
            };

            Child = new Panel { Children = { content, _focusRect } };
        }

        public void TrackText(TextBlock block, bool dim = false)
            => _texts.Add((block, block.Foreground ?? B("App.Text"), dim ? SelectedTextDim : SelectedText));

        public void TrackMarker(TextBlock marker)
            => _markers.Add((marker, marker.Foreground ?? B("App.Text")));

        public void TrackGraph(RevisionGraphControl graph) => _graphs.Add(graph);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _item = this.FindAncestorOfType<ListBoxItem>();
            _owner = this.FindAncestorOfType<ListBox>();
            if (_item is not null)
            {
                _item.PropertyChanged += OnStateChanged;
            }

            // The grid losing/gaining focus switches every selected row between the
            // active and the muted fill, so listen on the ListBox as well.
            if (_owner is not null)
            {
                _owner.PropertyChanged += OnStateChanged;
            }

            Sync();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_item is not null)
            {
                _item.PropertyChanged -= OnStateChanged;
                _item = null;
            }

            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnStateChanged;
                _owner = null;
            }

            base.OnDetachedFromVisualTree(e);
        }

        private static Color ColorOf(string key, Color fallback)
            => Application.Current?.Resources[key] is ISolidColorBrush b ? b.Color : fallback;

        // Mixes two colours (t = 0 → a, t = 1 → b).
        private static Color Mix(Color a, Color b, double t)
            => Color.FromRgb(
                (byte)Math.Round(a.R + ((b.R - a.R) * t)),
                (byte)Math.Round(a.G + ((b.G - a.G) * t)),
                (byte)Math.Round(a.B + ((b.B - a.B) * t)));

        private void OnStateChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ListBoxItem.IsSelectedProperty
                || e.Property == InputElement.IsFocusedProperty
                || e.Property == InputElement.IsKeyboardFocusWithinProperty
                || e.Property == InputElement.IsPointerOverProperty)
            {
                Sync();
            }
        }

        private void Sync()
        {
            bool selected = _item?.IsSelected == true;
            bool gridActive = _owner?.IsKeyboardFocusWithin == true || _item?.IsKeyboardFocusWithin == true;
            bool focusedRow = selected && (_item?.IsFocused == true || _item?.IsKeyboardFocusWithin == true);
            bool hover = _item?.IsPointerOver == true;

            // App.AccentFill, not App.Accent: this is a FILL that carries white text,
            // and the two roles need different blues. The accent that reads as ink on a
            // panel puts the selected row's white subject at 3.68:1 — below AA.
            Color accent = ColorOf("App.AccentFill", AccentFillFallback);
            Color selection = ColorOf("App.Selection", SelectionFallback);

            if (selected)
            {
                // Active: full-strength accent blue. Inactive: the same blue pulled a
                // third of the way toward the theme's selection tint, still solidly
                // blue but visibly calmer.
                Background = new SolidColorBrush(gridActive ? accent : Mix(accent, Mix(selection, Colors.Gray, 0.35), 0.45));
            }
            else if (hover)
            {
                // App.HoverRow, not App.PanelAlt: PanelAlt IS the alternate stripe, so
                // hovering an odd row changed nothing and hovering an even one merely
                // looked like the stripe. HoverRow is the one row background with a hue.
                Background = B("App.HoverRow");
            }
            else
            {
                Background = _normalBg;
            }

            _focusRect.BorderBrush = focusedRow ? FocusRect : Brushes.Transparent;

            foreach ((TextBlock block, IBrush normal, IBrush sel) in _texts)
            {
                block.Foreground = selected ? sel : normal;
            }

            foreach ((TextBlock marker, IBrush normal) in _markers)
            {
                marker.Foreground = selected ? SelectedMarker : normal;
            }

            foreach (RevisionGraphControl graph in _graphs)
            {
                graph.RowSelected = selected;
            }
        }
    }

    // Draws a cached identicon: a subtly tinted rounded background with the 5x5
    // colored block pattern on top. Custom-drawn Controls do not clip by default,
    // so ClipToBounds is set to keep the drawing inside the tiny avatar cell.
    private sealed class AvatarControl : Control
    {
        private readonly Identicon _icon;

        public AvatarControl(Identicon icon)
        {
            _icon = icon;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width;
            double hgt = Bounds.Height;
            double side = Math.Min(w, hgt);
            if (side <= 0)
            {
                return;
            }

            double ox = (w - side) / 2;
            double oy = (hgt - side) / 2;

            // A faint tile of the author's own hue as the backdrop, so empty cells
            // still read as part of the avatar rather than the row background.
            Color bg = _icon.Foreground;
            IBrush bgBrush = new SolidColorBrush(Color.FromArgb(0x33, bg.R, bg.G, bg.B));
            context.DrawRectangle(bgBrush, null, new RoundedRect(new Rect(ox, oy, side, side), 3));

            IBrush fg = new SolidColorBrush(_icon.Foreground);
            double cell = side / 5.0;
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 5; c++)
                {
                    if (_icon.Cells[r, c])
                    {
                        context.FillRectangle(
                            fg,
                            new Rect(ox + (c * cell), oy + (r * cell), cell + 0.5, cell + 0.5));
                    }
                }
            }
        }
    }

    /// <summary>
    ///  Draws one row's slice of the commit DAG: colored lane lines (verticals
    ///  for pass-through lanes, diagonals for branch/merge edges) plus the node
    ///  dot for this commit. Geometry comes from <see cref="RevisionGraphSegment"/>s
    ///  computed by <see cref="RevisionService"/>.
    /// </summary>
    private sealed class RevisionGraphControl : Control
    {
        // The original's palette, entry for entry and in its order: AppColorDefaults
        // GraphBranch1..7 (GraphBranch8 is Color.Empty there, i.e. unused, so the
        // cycle is seven long — RevisionGraphLaneColor filters the empty ones out).
        // The port had invented eight much darker, more saturated colours of its own,
        // which is the first thing the eye notices next to a screenshot of the original.
        private static readonly Color[] LaneColors =
        {
            Color.FromRgb(0xF0, 0x64, 0xA0), // pink
            Color.FromRgb(0x78, 0xB4, 0xE6), // light blue
            Color.FromRgb(0x24, 0xC2, 0x21), // green
            Color.FromRgb(0xA0, 0x78, 0xF0), // violet
            Color.FromRgb(0xDD, 0x32, 0x28), // red
            Color.FromRgb(0x1A, 0xC6, 0xA6), // teal
            Color.FromRgb(0xE7, 0xB0, 0x0F), // amber
        };

        // GraphRenderer.LaneLineWidth / NodeDimension (2 and 10 at 100% scaling).
        private const double LaneLineWidth = 2;
        private const double NodeDimension = 10;

        private static readonly IBrush[] LaneBrushes =
            LaneColors.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();

        private readonly IReadOnlyList<RevisionGraphSegment> _segments;
        private readonly int _nodeLane;
        private readonly int _nodeColor;
        private readonly double _laneWidth;
        private readonly bool _artificialNode;
        private readonly bool _squareNode;
        private readonly bool _headNode;

        // Non-null only while "draw non-relatives gray" is on: the flag of each
        // segment (parallel to _segments), the node's own flag, and the gray brush
        // to use for the ones that are false — the port's counterpart of upstream
        // GraphRenderer.GetBrushForLaneInfo(…, isRelative, DrawNonRelativesGray).
        private readonly IReadOnlyList<bool>? _relativeSegments;
        private readonly bool _relativeNode;
        private readonly IBrush? _nonRelativeBrush;
        private bool _rowSelected;

        public RevisionGraphControl(
            IReadOnlyList<RevisionGraphSegment> segments,
            int nodeLane,
            double laneWidth,
            bool artificialNode = false,
            int nodeColor = -1,
            IReadOnlyList<bool>? relativeSegments = null,
            bool relativeNode = true,
            IBrush? nonRelativeBrush = null,
            bool squareNode = false,
            bool headNode = false)
        {
            _segments = segments;
            _nodeLane = nodeLane;
            _squareNode = squareNode;
            _headNode = headNode;

            // The node's palette entry is its edge identity, not its column (lanes are
            // recycled between unrelated branches). -1 keeps the old column-keyed
            // behaviour for callers that have no identity to give.
            _nodeColor = nodeColor >= 0 ? nodeColor : nodeLane;
            _laneWidth = laneWidth;
            _artificialNode = artificialNode;
            _relativeSegments = relativeSegments;
            _relativeNode = relativeNode;
            _nonRelativeBrush = nonRelativeBrush;

            // Custom-drawn Controls do NOT clip by default: lane lines/edges can
            // paint outside the row's bounds and smear into neighbours / the
            // panel below. Clip strictly to our own bounds.
            ClipToBounds = true;
        }

        /// <summary>
        ///  Set while the row is selected: the DAG is then drawn over a solid blue
        ///  fill, so the lane colours are lightened toward white (keeping their hue,
        ///  hence still telling the lanes apart) and the node gets a white ring.
        /// </summary>
        public bool RowSelected
        {
            get => _rowSelected;
            set
            {
                if (_rowSelected != value)
                {
                    _rowSelected = value;
                    InvalidateVisual();
                }
            }
        }

        /// <summary>
        ///  Upstream's <c>MulticolorBranches</c>: off, the whole DAG is drawn in one
        ///  colour. Static and process-wide because the palette is: the grid rebuilds
        ///  its rows when the setting is saved, so there is nothing to invalidate here.
        /// </summary>
        public static bool Multicolor { get; set; } = true;

        private static Color LaneColor(int lane)
            => Multicolor
                ? LaneColors[((lane % LaneColors.Length) + LaneColors.Length) % LaneColors.Length]
                : SingleColor;

        // The one colour used when Multicolor is off: the light blue of the palette,
        // which reads as a line rather than as a highlight in both themes.
        private static readonly Color SingleColor = Color.FromRgb(0x78, 0xB4, 0xE6);

        private IBrush Brush(int lane, bool relative = true)
        {
            // A non-relative lane loses its colour entirely and is drawn in the
            // themed dim brush, like the original's NonRelativeBrush.
            if (!relative && _nonRelativeBrush is not null)
            {
                return _rowSelected && _nonRelativeBrush is ISolidColorBrush solid
                    ? new SolidColorBrush(Lighten(solid.Color, 0.55))
                    : _nonRelativeBrush;
            }

            if (!Multicolor)
            {
                return new SolidColorBrush(_rowSelected ? Lighten(SingleColor, 0.55) : SingleColor);
            }

            int i = ((lane % LaneBrushes.Length) + LaneBrushes.Length) % LaneBrushes.Length;
            return _rowSelected
                ? new SolidColorBrush(Lighten(LaneColor(lane), 0.55))
                : LaneBrushes[i];
        }

        // Mixes a colour toward white by t (0 = unchanged, 1 = white).
        private static Color Lighten(Color c, double t)
            => Color.FromRgb(
                (byte)Math.Round(c.R + ((255 - c.R) * t)),
                (byte)Math.Round(c.G + ((255 - c.G) * t)),
                (byte)Math.Round(c.B + ((255 - c.B) * t)));

        public override void Render(DrawingContext context)
        {
            double h = Bounds.Height;
            if (h <= 0)
            {
                return;
            }

            double X(double lane) => (lane * _laneWidth) + (_laneWidth / 2);

            // Two passes, gray first: where a gray lane and a coloured one overlap the
            // coloured one has to win, which is why the original orders the segments
            // by IsRelative before drawing them (GraphRenderer.DrawItem).
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < _segments.Count; i++)
                {
                    RevisionGraphSegment s = _segments[i];
                    bool relative = _relativeSegments is null
                        || i >= _relativeSegments.Count
                        || _relativeSegments[i];
                    if (relative != (pass == 1))
                    {
                        continue;
                    }

                    Pen pen = new(Brush(s.ColorLane, relative), LaneLineWidth);
                    Point from = new(X(s.DrawFromLane), s.FromY * h);
                    Point to = new(X(s.DrawToLane), s.ToY * h);

                    if (from.X == to.X)
                    {
                        context.DrawLine(pen, from, to);
                        continue;
                    }

                    context.DrawGeometry(null, pen, Curve(from, to, s.FromY, s.ToY));
                }
            }

            IBrush nodeBrush = Brush(_nodeColor, _relativeNode);
            double cx = X(_nodeLane);
            double cy = h / 2;
            double half = NodeDimension / 2;

            if (_artificialNode)
            {
                // Artificial rows (working directory / commit index) get a distinct
                // node: a hollow square in the lane colour. The original tells them
                // apart in the message column instead (its own icons and text), which
                // this port does not draw, so the marker stays.
                Pen outline = new(nodeBrush, LaneLineWidth);
                context.DrawRectangle(null, outline, new Rect(cx - half, cy - half, half * 2, half * 2));
                return;
            }

            // Square node for a revision carrying refs, round otherwise, and a ring
            // around HEAD — GraphRenderer.DrawItem: `square = Refs.Count > 0`,
            // `hasOutline = ObjectId == headId`, outline drawn on the node rect
            // inflated by 1 with a 2px pen in the window text colour.
            IPen? ring = _rowSelected ? new Pen(Brushes.White, 1.5) : null;
            if (_squareNode)
            {
                context.DrawRectangle(nodeBrush, ring, new Rect(cx - half, cy - half, half * 2, half * 2));
            }
            else
            {
                context.DrawEllipse(nodeBrush, ring, new Point(cx, cy), half, half);
            }

            if (!_headNode)
            {
                return;
            }

            // SystemColors.WindowText upstream: the plain foreground, resolved from the
            // theme so the ring survives the dark palette.
            Pen headPen = new(_rowSelected ? Brushes.White : B("App.Text"), LaneLineWidth);
            double outer = half + 1;
            if (_squareNode)
            {
                context.DrawRectangle(null, headPen, new Rect(cx - outer, cy - outer, outer * 2, outer * 2));
            }
            else
            {
                context.DrawEllipse(null, headPen, new Point(cx, cy), outer, outer);
            }
        }

        /// <summary>
        ///  A lane change, drawn the way the original draws it: the line leaves (or
        ///  enters) the node vertically and eases into the straight diagonal that runs
        ///  on to the next node.
        ///
        ///  <para>The original renders segments with diagonals by default
        ///  (AppSettings.RenderGraphWithDiagonals), which is a short perpendicular stub
        ///  at the node plus a Bézier easing into the diagonal (SegmentRenderer). Here
        ///  the segment is half a row, and the straightening pass in RevisionService has
        ///  already put its outer end on the straight node-to-node line — so the outer
        ///  end's tangent IS the chord, and a single cubic with a vertical tangent at
        ///  the node end reproduces the same shape.</para>
        /// </summary>
        private static Geometry Curve(Point from, Point to, double fromY, double toY)
        {
            // Which end sits on the node (the row centre, y = 0.5): the vertical one.
            bool nodeAtStart = fromY >= 0.5 && toY > fromY;
            Point node = nodeAtStart ? from : to;
            Point edge = nodeAtStart ? to : from;

            // A quarter of the shift on each side: enough to leave the node upright
            // without flattening the diagonal before it reaches the row boundary.
            Point nodeControl = new(node.X, node.Y + ((edge.Y - node.Y) / 2));
            Point edgeControl = new(
                edge.X - ((edge.X - node.X) / 4),
                edge.Y - ((edge.Y - node.Y) / 4));

            StreamGeometry geometry = new();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(node, isFilled: false);
                ctx.CubicBezierTo(nodeControl, edgeControl, edge);
                ctx.EndFigure(isClosed: false);
            }

            return geometry;
        }
    }
}
