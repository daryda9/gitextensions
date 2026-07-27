using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A commit-list view (revision grid) for the Avalonia/Linux port. Loads the
///  recent history of a repository off the UI thread and renders it as a
///  multi-column list (DAG graph / Hash / Author / Date / Subject, with ref
///  names shown inline). Uses a <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) or theme
///  registration is required.
///
///  <para>The left-most column draws the commit DAG (colored lane lines + a
///  node dot per row, with branch/merge edges between adjacent rows), using the
///  lane layout computed by <see cref="RevisionService"/>.</para>
/// </summary>
public sealed class RevisionGridView : UserControl
{
    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AvatarWidth = 28;
    private const double AuthorWidth = 170;
    private const double DateWidth = 130;

    // Size of the identicon square drawn inside the avatar cell (centred).
    private const double AvatarSize = 18;

    // Graph rendering metrics.
    private const double LaneWidth = 14;

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
    private const string WorkTreeHash = "2222222222222222222222222222222222222222";
    private const string IndexHash = "1111111111111111111111111111111111111111";

    // Pending-work counts, pushed in by MainWindow via SetWorkingState; this view
    // never queries git for them itself.
    private int _unstaged;
    private int _staged;

    // Graph geometry for the artificial rows, recomputed whenever the displayed
    // set is rebuilt: the lane the artificial nodes live in (HEAD's lane) and the
    // displayed index of the HEAD row, so the lane line can be carried down to it.
    private int _artificialLane;
    private int _artificialCount;
    private int _headDisplayIndex = -1;

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

    // The accumulated pages exactly as git returned them, WITHOUT graph geometry.
    // The DAG is rebuilt from this whole list on every append (off the UI thread),
    // which is what keeps lanes, edges and the artificial rows correct as the
    // history grows.
    private IReadOnlyList<RevisionRow> _loaded = [];

    // Footer strip with the "load more" button, shown only while _hasMore.
    private readonly Border _moreBar;
    private readonly Button _moreButton;

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
    // Session-local; changing it re-runs the log via the existing load path.
    private BranchScope _branchScope = BranchScope.AllBranches;

    // Path of the repository last asked to load, so a scope change can re-run the
    // log without the caller re-supplying it (LoadRepository stores it here).
    private string _repoPath = string.Empty;

    // Column visibility toggles (the Subject column always stays — upstream has no
    // toggle for it either, it is the Fill column of the grid).
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

    // Set while RebindRows swaps ItemsSource and puts the selection back. That swap
    // raises SelectionChanged synchronously (empty, then re-selected), which is NOT a
    // user selection: the very same commits end up selected. Announcing it would
    // re-raise RevisionSelected/RangeSelected on every refresh — and, since the
    // author highlight now rebinds too, twice per click.
    private bool _rebinding;

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
    // segment list is built by WithHeadConnector / ArtificialSegments. Recomputed
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
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    // Raised when exactly two rows are selected (ctrl/shift multi-select). Carries
    // (baseHash, otherHash) = (older, newer) so a diff between the two can be shown.
    public event Action<string, string>? RangeSelected;

    /// <summary>Raised when the artificial "Working directory" row is clicked.</summary>
    public event Action? WorkingDirectorySelected;

    /// <summary>Raised when the artificial "Commit index" row is clicked.</summary>
    public event Action? CommitIndexSelected;

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
    public void AddCommitCommand(string header, Action<string> handler)
    {
        _commitCommands.Add((header, handler));

        // Items cannot be added to a live popup (it would not re-measure), so the
        // whole menu is discarded and rebuilt instead.
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

        _headerHost = new ContentControl { Content = BuildHeader() };

        _search = new TextBox
        {
            Watermark = T("Filter: author / message / hash"),
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
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
            _search.Focus();
        };
        _search.InnerRightContent = clearButton;

        // Live, in-memory filtering as the user types (no git re-run per keystroke).
        _search.TextChanged += (_, _) =>
        {
            clearButton.IsVisible = !string.IsNullOrEmpty(_search.Text);
            ApplyFilterCore(_search.Text);
        };

        // Esc clears the filter (and keeps focus in the box).
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _search.Text = string.Empty;
                e.Handled = true;
            }
        };

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
        bar.Children.Add(_search); // fills the remaining space

        Border searchBar = new()
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
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
            ItemTemplate = new FuncDataTemplate<RevisionRow>((row, _) => BuildRow(row), supportsRecycling: true),
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

            // The author of the selected revision is emphasised on every row that
            // shares it; a change of author re-templates the rows (see D7).
            UpdateAuthorHighlight();

            // An artificial row (working directory / commit index) is not a commit:
            // it never fires RevisionSelected and never takes part in a range diff.
            // Its own event is raised by an explicit click on the row (see
            // BuildArtificialRow), so merely arrowing past it does not open a dialog.
            if (_list.SelectedItems is { Count: 1 } one && one[0] is RevisionRow art && IsArtificial(art))
            {
                return;
            }

            // Two rows selected => diff the range. The grid is newest-first, so the
            // row with the higher index in Items is the OLDER commit (= baseHash);
            // the lower index is the NEWER commit (= otherHash).
            if (_list.SelectedItems is { Count: 2 } sel
                && sel[0] is RevisionRow a && sel[1] is RevisionRow b
                && !IsArtificial(a) && !IsArtificial(b))
            {
                int ia = _list.Items.IndexOf(a);
                int ib = _list.Items.IndexOf(b);
                RevisionRow older = ia >= ib ? a : b;
                RevisionRow newer = ia >= ib ? b : a;
                RangeSelected?.Invoke(older.Hash, newer.Hash);
            }
            else if (_list.SelectedItem is RevisionRow row && !IsArtificial(row))
            {
                RevisionSelected?.Invoke(row.Hash);
            }
        };

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
        _quickSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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

        // Footer: an explicit "load more" affordance next to the implicit
        // scroll-to-end one, shown only while the walk has more commits to give.
        _moreButton = MakeBarButton(string.Empty);
        _moreButton.Margin = new Thickness(0);
        _moreButton.HorizontalAlignment = HorizontalAlignment.Center;
        _moreButton.Click += (_, _) => LoadMore(userRequested: true);
        _moreBar = new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            IsVisible = false,
            Child = _moreButton,
        };
        UpdateMoreBar();

        Panel listHost = new();
        listHost.Children.Add(_list);
        listHost.Children.Add(_quickSearchOverlay);

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(searchBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_headerHost, Dock.Top);
        DockPanel.SetDock(_moreBar, Dock.Bottom);
        root.Children.Add(searchBar);
        root.Children.Add(_status);
        root.Children.Add(_headerHost);
        root.Children.Add(_moreBar);
        root.Children.Add(listHost);

        Content = root;

        // A language switch re-labels this view in place — no restart, and no
        // loss of filter / scope / selection (see Relabel).
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

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

        if (ctrl && !shift && !alt && e.Key == Key.C && _list.SelectedItem is RevisionRow row)
        {
            Copy(row.Hash);
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.C)
        {
            // SelectCurrentRevision.
            SelectCurrentRevision();
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.V)
        {
            // The quick-search buffer accepts a paste (QuickSearchProvider.cs:67-72).
            _ = PasteIntoQuickSearchAsync();
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && (e.Key == Key.P || e.Key == Key.Left))
        {
            // GoToParent (Ctrl+P) — Ctrl+← is upstream's GoToFirstParent, which in
            // this port is the same jump: the parent navigation always takes
            // ParentHashes[0].
            GoToParent();
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.N)
        {
            GoToChild();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.G)
        {
            OpenGoToCommit();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.K)
        {
            GoToMergeBase();
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.OemBackslash)
        {
            ToggleBetweenArtificialAndHeadCommits();
            e.Handled = true;
        }
        else if (ctrl && !shift && !alt && e.Key == Key.I)
        {
            // RevisionFilter: the real (git-side) filter dialog.
            _ = ShowFilterDialogAsync();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.I)
        {
            ResetAllFilters();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.B)
        {
            ToggleHighlightSelectedBranch();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.A)
        {
            ShowAllBranches();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.U)
        {
            ShowCurrentBranchOnly();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.T)
        {
            ShowFilteredBranches();
            e.Handled = true;
        }
        else if (ctrl && shift && !alt && e.Key == Key.R)
        {
            ToggleShowRemoteBranches();
            e.Handled = true;
        }
        else if (ctrl && alt && !shift && e.Key == Key.T)
        {
            ToggleShowTags();
            e.Handled = true;
        }
        else if (alt && !ctrl && e.Key == Key.Up)
        {
            // Quick-search previous (PrevQuickSearch).
            QuickSearchPrevious();
            e.Handled = true;
        }
        else if (alt && !ctrl && e.Key == Key.Down)
        {
            // Quick-search next (NextQuickSearch).
            QuickSearchNext();
            e.Handled = true;
        }
        else if (alt && !ctrl && e.Key == Key.Left)
        {
            // Navigation history — Alt+← / Alt+→ as in the original grid.
            NavigateBack();
            e.Handled = true;
        }
        else if (alt && !ctrl && e.Key == Key.Right)
        {
            NavigateForward();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !quickActive && !ctrl && !alt
            && _list.SelectedItem is RevisionRow activated)
        {
            // Enter activates the focused row, like a double click.
            Activate(activated);
            e.Handled = true;
        }
        // --- quick-search navigation (only when the list itself is focused) ---
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
    }

    // ---- translation ---------------------------------------------------------

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // Appends the drop-down chevron to a bar-button caption without making the
    // glyph part of the translatable string.
    private static string Chevron(string caption) => string.Format("{0} ▾", caption);

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Relabel);

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

        _search.Watermark = T("Filter: author / message / hash");
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
    private static bool IsArtificial(RevisionRow row)
        => row.Hash is WorkTreeHash or IndexHash;

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
    // the Date cell blank, and the parent list is empty so DAG navigation never
    // walks into (or out of) an artificial node — the lane line to HEAD is drawn
    // by the graph column instead.
    private static RevisionRow MakeArtificial(string hash, string subject, int lane, int laneCount)
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
            NodeLane = lane,
            LaneCount = laneCount,
        };

    // Prepends the artificial rows to the filtered commit rows, recording the graph
    // geometry (lane + HEAD position) the row builder needs to link them to HEAD.
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
        _artificialLane = 0;
        _headDisplayIndex = -1;

        bool wanted = _showArtificial
            && (_unstaged > 0 || _staged > 0) && !_quickFilterActive && commits.Count > 0;
        if (!wanted)
        {
            return commits;
        }

        // Anchor the nodes in HEAD's lane (falling back to the topmost row's lane
        // when HEAD is outside the loaded window), so the connector lands on the
        // checked-out commit exactly like the original grid.
        int lane = commits[0].NodeLane;
        int headIndex = -1;
        for (int i = 0; i < commits.Count; i++)
        {
            if (commits[i].IsHead)
            {
                headIndex = i;
                lane = commits[i].NodeLane;
                break;
            }
        }

        if (headIndex < 0 && GitFilterActive)
        {
            // The filter excluded the checked-out commit: nothing to hang them off.
            return commits;
        }

        int laneCount = commits[0].LaneCount;
        List<RevisionRow> display = [];
        if (_unstaged > 0)
        {
            display.Add(MakeArtificial(WorkTreeHash,
                T("TranslatedStrings/_workingDirectoryText.Text", "Working directory"), lane, laneCount));
        }

        if (_staged > 0)
        {
            display.Add(MakeArtificial(IndexHash,
                T("TranslatedStrings/_indexText.Text", "Commit index"), lane, laneCount));
        }

        _artificialCount = display.Count;
        _artificialLane = lane;
        _headDisplayIndex = headIndex >= 0 ? headIndex + _artificialCount : -1;

        display.AddRange(commits);
        return display;
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
        // always come back full and resurrect the "Load more" footer at the end of
        // a fully-walked history. The extra row is trimmed off in the merge.
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
        _list.ItemsSource = null;
        LoadPage(restart: true);

        // The context menu's predicates need the checked-out branch and the kind of
        // every ref; both are refreshed alongside the walk, off the UI thread.
        RefreshRefContext();
    }

    /// <summary>
    ///  Appends the next page of history. Called by the footer button and, silently,
    ///  when the list is scrolled to its end. A no-op while a page is already in
    ///  flight or when the walk is exhausted.
    /// </summary>
    private void LoadMore(bool userRequested)
    {
        if (_loadingPage || !_hasMore || string.IsNullOrEmpty(_repoPath))
        {
            return;
        }

        _ = userRequested; // (kept for readability at the call sites)
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
        bool showRemotes = _showRemotes;
        bool showTags = _showTags;
        bool showStashes = _showStashes;
        bool topoOrder = _topoOrder;
        bool authorDateOrder = _authorDateSort;
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

        int generation = _loadGeneration;
        _loadingPage = true;
        UpdateMoreBar();

        if (restart && !preserveView)
        {
            // A silent refresh keeps the real status line: flashing "Loading…" over
            // it would be the visible symptom the refresh is meant not to have.
            _status.Text = T("RevisionGridControl/_strLoading.Text", "Loading…");
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
                    showRemotes: showRemotes,
                    showTags: showTags,
                    showStashes: showStashes,
                    topoOrder: topoOrder,
                    filter: filter,
                    authorDateOrder: authorDateOrder);

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

                IReadOnlyList<RevisionRow> graphed = RevisionService.BuildRevisionGraph(merged);

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _loadGeneration)
                    {
                        // A reload happened while this page was loading: drop it.
                        return;
                    }

                    _loadingPage = false;
                    _loaded = merged;
                    _hasMore = hasMore;

                    int laneCount = graphed.Count > 0 ? graphed[0].LaneCount : 1;
                    _graphWidth = Math.Clamp(laneCount, 1, MaxGraphLanes) * LaneWidth;
                    _allRows = graphed;
                    // Display-only: _repoPath keeps the absolute path, the status
                    // line shows the same "~" form as the toolbar repo dropdown.
                    _repoLabel = CollapseHome(repoPath);
                    // Recompute HEAD reachability for the relatives/highlight styles.
                    ComputeReachability();
                    // Re-apply any current filter text so a reload keeps the view
                    // consistent. This is also what rebinds the rows, so it is the
                    // single place where scroll offset, selection and keyboard focus
                    // are carried across the rebind (see ApplyFilterCore).
                    ApplyFilterCore(_search.Text, keepViewport);
                    UpdateMoreBar();
                });
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
                    _hasMore = false;
                    UpdateMoreBar();
                    _status.Text = string.Format(T("Error: {0}"), ex.Message);
                });
            }
        });
    }

    // Shows/hides the footer and captions its button with the page size (or a
    // "loading" caption while a page is in flight).
    private void UpdateMoreBar()
    {
        _moreBar.IsVisible = _hasMore;
        _moreButton.IsEnabled = !_loadingPage;
        _moreButton.Content = _loadingPage
            ? T("RevisionGridControl/_strLoading.Text", "Loading…")
            : string.Format(T("Load {0} more commits"), _pageSize);
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
            LoadMore(userRequested: false);
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
        UpdateMoreBar();
        Reload();
    }

    // Path display (home collapsed to "~") is shared with the toolbar's repository
    // caption — see PathDisplay.CollapseHome.
    private static string CollapseHome(string path) => PathDisplay.CollapseHome(path);

    // Human label for the current branch scope, shown in the status line so the
    // effect of the toggle (and the resulting commit count) is visible.
    private string ScopeLabel => _branchScope switch
    {
        BranchScope.AllBranches => T("all branches"),
        BranchScope.CurrentBranch => T("current branch"),
        BranchScope.Filtered => T("filtered (current branch)"),
        _ => T("all branches"),
    };

    /// <summary>
    ///  Applies a case-insensitive substring filter over the already-loaded
    ///  revisions (author name, commit subject, and full/abbreviated hash).
    ///  Empty text shows everything. Runs purely in memory — no git per keystroke.
    ///
    ///  Public entry point for host-driven filtering (e.g. the toolbar's Filter
    ///  box). Drives the same path the grid's own search bar uses by routing
    ///  through the search TextBox, so both surfaces stay in sync.
    /// </summary>
    public void ApplyFilter(string text)
    {
        string value = text ?? string.Empty;
        if (_search.Text == value)
        {
            // Text unchanged (TextChanged would not fire) — apply directly.
            ApplyFilterCore(value);
            return;
        }

        // Setting the box text raises TextChanged, which calls ApplyFilterCore.
        _search.Text = value;
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
        _resetFilterButton.IsVisible = GitFilterActive;
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
        // The graph's relative/non-relative flags are per DISPLAY ROW, so they are
        // refreshed here — the single place every rebind goes through, whether the
        // rows, the filter, the anchor or a highlight toggle changed.
        ComputeGraphRelatives();

        List<string> selected = _list.SelectedItems is { Count: > 0 } items
            ? items.OfType<RevisionRow>().Select(r => r.Hash).ToList()
            : [];
        Vector offset = _scroll?.Offset ?? default;
        bool hadFocus = _list.IsKeyboardFocusWithin;

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
            _list.ItemsSource = null;
            _list.ItemsSource = new List<RevisionRow>(_rows);

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
                    _list.Focus();
                }
            },
            DispatcherPriority.Loaded);
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
        if (GitFilterActive && _repoLabel.Length > 0)
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

        // Flags of the lanes crossing the top edge of the current row.
        bool[] incoming = [];

        for (int i = 0; i < _rows.Count; i++)
        {
            RevisionRow row = _rows[i];
            bool artificial = IsArtificial(row);
            bool nodeRelative = artificial
                || _headRelatives.Count == 0
                || _headRelatives.Contains(row.Hash);
            int nodeLane = artificial ? _artificialLane : row.NodeLane;
            IReadOnlyList<RevisionGraphSegment> segments = artificial
                ? ArtificialSegments(i)
                : WithHeadConnector(row, i);

            bool[] flags = new bool[segments.Count];
            int maxLane = nodeLane;
            foreach (RevisionGraphSegment s in segments)
            {
                maxLane = Math.Max(maxLane, (int)Math.Round(Math.Max(s.FromLane, s.ToLane)));
            }

            bool[] outgoing = new bool[maxLane + 1];

            for (int s = 0; s < segments.Count; s++)
            {
                RevisionGraphSegment seg = segments[s];
                int fromLane = (int)Math.Round(seg.FromLane);
                int toLane = (int)Math.Round(seg.ToLane);
                bool bottomHalf = seg.FromY >= 0.5;
                bool relative = bottomHalf && fromLane == nodeLane
                    ? nodeRelative || Flag(incoming, toLane)
                    : Flag(incoming, fromLane);

                flags[s] = relative;
                if (bottomHalf && toLane < outgoing.Length)
                {
                    outgoing[toLane] |= relative;
                }
            }

            _graphRelatives.Add((nodeRelative, flags));
            incoming = outgoing;
        }

        static bool Flag(bool[] flags, int lane)
            => lane >= 0 && lane < flags.Length && flags[lane];
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
        RefreshView();
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
        panel.Children.Add(OptionCheck(
            OptShowArtificialCommits,
            T("Artificial commits"),
            ToggleShowArtificialCommits));
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
        Button anchorToHead = MakeBarButton(T("Highlight current branch's history"));
        anchorToHead.Margin = new Thickness(0, 3, 0, 0);
        anchorToHead.HorizontalAlignment = HorizontalAlignment.Stretch;
        anchorToHead.Click += (_, _) => HighlightBranchOf(null);

        panel.Children.Add(nonRelatives);
        panel.Children.Add(highlight);
        panel.Children.Add(anchorToHead);

        return new Flyout
        {
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
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
    private static Button MakeBarButton(string text)
        => new()
        {
            Content = text,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

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
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
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
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
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

        // "Filtered" has no selection UI yet, so it walks the current branch.
        panel.Children.Add(new TextBlock
        {
            Text = T("Filtered walks the current branch until a ref picker is added."),
            Foreground = B("App.TextDim"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new Flyout
        {
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
        };
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
    };

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
        Window dialog = new()
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
    // also work via keyboard (Ctrl+P, Ctrl+N, Ctrl+Shift+G).
    private Flyout BuildGoToFlyout()
    {
        StackPanel panel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 190 };

        Flyout flyout = new();

        panel.Children.Add(SectionLabel(T("FormBrowse/navigateToolStripMenuItem.Text", "Navigate")));

        Button parent = MakeMenuButton(string.Format(T("↑  {0}   (Ctrl+P)"),
            T("RevisionGrid/GotoFirstParentCommit.Text", "First parent")));
        parent.Click += (_, _) =>
        {
            flyout.Hide();
            GoToParent();
        };

        Button child = MakeMenuButton(string.Format(T("↓  {0}   (Ctrl+N)"),
            T("RevisionGrid/GotoChildCommit.Text", "Nearest child")));
        child.Click += (_, _) =>
        {
            flyout.Hide();
            GoToChild();
        };

        panel.Children.Add(parent);
        panel.Children.Add(child);

        // Navigation history: the two directions of the jump stack (Alt+← / Alt+→).
        Button back = MakeMenuButton(string.Format(T("←  {0}   (Alt+←)"),
            T("RevisionGrid/NavigateBackward.Text", "Backward")));
        back.Click += (_, _) =>
        {
            flyout.Hide();
            NavigateBack();
        };

        Button forward = MakeMenuButton(string.Format(T("→  {0}   (Alt+→)"),
            T("RevisionGrid/NavigateForward.Text", "Forward")));
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

        flyout.Content = new Border
        {
            Background = B("App.Panel"),
            Padding = new Thickness(2),
            Child = panel,
        };
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
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            MinWidth = 150,
            Padding = new Thickness(6, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    // A full-width, left-aligned button used inside the "Go to" flyout.
    private static Button MakeMenuButton(string text)
        => new()
        {
            Content = text,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

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
        _list.Focus();
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
    private static Border BuildNotesBadge()
        => new()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x4B, 0x2E)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = T("This commit has a git note"),
            Child = new TextBlock
            {
                Text = T("note"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0xCB, 0x95)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    private Grid MakeColumns()
    {
        // Hidden columns collapse to zero width; their content is simply not added
        // (see BuildHeader/BuildRow) so nothing overflows into the neighbouring cell.
        double hash = _showHash ? HashWidth : 0;
        double avatar = _showAvatar ? AvatarWidth : 0;
        double author = _showAuthor ? AuthorWidth : 0;
        double date = _showDate ? DateWidth : 0;

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

        return new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 3, 0, 3),
            Child = grid,
        };
    }

    // --- Graph geometry for the artificial nodes ------------------------------
    //
    // The artificial rows sit in HEAD's lane and are chained downward: each of
    // them draws the half-lane below its node (and, from the second one on, the
    // half above it), and the commit rows between the top of the list and HEAD
    // carry the same lane through, so the line reaches the HEAD node unbroken —
    // exactly the continuous lane the original Windows grid shows.
    private IReadOnlyList<RevisionGraphSegment> ArtificialSegments(int displayIndex)
    {
        List<RevisionGraphSegment> segments =
        [
            new(_artificialLane, 0.5, _artificialLane, 1.0, _artificialLane),
        ];
        if (displayIndex > 0)
        {
            segments.Add(new(_artificialLane, 0.0, _artificialLane, 0.5, _artificialLane));
        }

        return segments;
    }

    // Segments added to a COMMIT row so the artificial lane reaches HEAD: a full
    // pass-through above HEAD, and the upper half on the HEAD row itself.
    private IReadOnlyList<RevisionGraphSegment> WithHeadConnector(RevisionRow row, int displayIndex)
    {
        if (_artificialCount == 0 || _headDisplayIndex < 0 || displayIndex > _headDisplayIndex)
        {
            return row.GraphSegments;
        }

        List<RevisionGraphSegment> segments = [.. row.GraphSegments];
        double toY = displayIndex == _headDisplayIndex ? 0.5 : 1.0;
        segments.Add(new(_artificialLane, 0.0, _artificialLane, toY, _artificialLane));
        return segments;
    }

    private Control BuildRow(RevisionRow row)
    {
        if (IsArtificial(row))
        {
            return BuildArtificialRow(row);
        }

        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);
        grid.MinHeight = 20;

        // Subtle alternating-row background (App.Panel / App.PanelAlt). It lives on
        // the RevisionRowView wrapper (full row width, no margin) so the selection
        // fill can cover every column edge to edge, like the original grid.
        int index = _rows is List<RevisionRow> list ? list.IndexOf(row) : IndexOf(_rows, row);
        RevisionRowView view = new((index & 1) == 0 ? B("App.Panel") : B("App.PanelAlt"), grid);

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
                WithHeadConnector(row, index),
                row.NodeLane,
                LaneWidth,
                relativeSegments: flags?.Segments,
                relativeNode: flags?.Node ?? true,
                nonRelativeBrush: flags is null ? null : B("App.TextDim"));
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
        bool nonRelative = !onBranch && _drawNonRelativesGray
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
            bool sameAuthor = _highlightedAuthor.Length > 0
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
            // Respect the remote/tag "View" toggles: hide remote-tracking or tag
            // badges when the corresponding toggle is off, so badge display stays
            // consistent with what the walk includes. (Kind is the same '/'/version
            // heuristic used by RefColors, so it is best-effort.)
            if ((!_showRemotes && IsRemoteRef(refName)) || (!_showTags && IsTagRef(refName)))
            {
                continue;
            }

            // Best-effort current-branch emphasis: on the HEAD row, a local branch
            // ref (not remote, not tag) is the checked-out branch — render it bold
            // with a small green ▶ marker, echoing the original GitExtensions look.
            bool isCurrent = row.IsHead && !IsRemoteRef(refName) && !IsTagRef(refName);
            Badge(BuildRefBadge(refName, isCurrent, view));
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
        grid.MinHeight = 20;

        int index = IndexOf(_rows, row);
        RevisionRowView view = new((index & 1) == 0 ? B("App.Panel") : B("App.PanelAlt"), grid);

        if (_showGraph && !_quickFilterActive)
        {
            RevisionGraphControl graph = new(
                ArtificialSegments(index), _artificialLane, LaneWidth, artificialNode: true);
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

        // Clicking the row shows the pending work (the host opens the commit
        // dialog), exactly as the old fixed top panel did. Bound to the click and
        // not to selection, so keyboard navigation can pass over the row freely.
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
            InputElement.PointerReleasedEvent,
            (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    Raise();
                }
            },
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
    private static Control BuildRefBadge(string refName, bool isCurrent = false, RevisionRowView? view = null)
    {
        Color kind = RefColor(refName);

        Border pill = new()
        {
            Background = B("App.Panel"), // light/adaptive tint, readable in both themes
            BorderBrush = new SolidColorBrush(kind),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 0, 7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = refName,
                Foreground = new SolidColorBrush(kind),
                FontSize = 11,
                FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        // On a selected (solid blue) row the pill keeps its kind colour but swaps its
        // backdrop to opaque white, so branch/remote/tag pills stay readable instead
        // of drowning in the fill. The row view restores App.Panel when deselected.
        view?.TrackPill(pill);

        if (!isCurrent)
        {
            return pill;
        }

        // ▶ triangle marker in green before the current-branch pill.
        StackPanel wrap = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock marker = new()
        {
            Text = "▶",
            Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xAE, 0x5A)),
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view?.TrackMarker(marker);
        wrap.Children.Add(marker);
        wrap.Children.Add(pill);
        return wrap;
    }

    // Ref-kind heuristics (shared by badge coloring and the remote/tag toggles):
    // a "/" marks a remote-tracking ref (origin/main); a leading version-like
    // token (v1.2, 2.0) marks a tag. Local branches match neither.
    private static bool IsRemoteRef(string refName) => refName.Contains('/');

    private static bool IsTagRef(string refName)
        => !IsRemoteRef(refName) && Regex.IsMatch(refName, @"^v?\d");

    // Kind colour used for BOTH the outline border and the text of a ref pill.
    // Remote-tracking refs contain a "/" (e.g. origin/main); simple version-like
    // names (v1.2, 2.0) are treated as tags; everything else is a local branch.
    // Tuned toward the original GitExtensions palette and readable on the light
    // App.Panel background in both light and dark themes.
    private static Color RefColor(string refName)
    {
        if (refName.Contains('/'))
        {
            return Color.FromRgb(0xC0, 0x39, 0x2B); // remote-tracking: red/pink
        }

        if (Regex.IsMatch(refName, @"^v?\d"))
        {
            return Color.FromRgb(0xB8, 0x86, 0x0B); // tag: amber/olive
        }

        return Color.FromRgb(0x2E, 0x7D, 0x32); // local branch: green
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
        string CurrentBranch)
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
        AddRouted(other, "Bisect: mark good", ctx => ctx.SingleCommit);
        AddRouted(other, "Bisect: mark bad", ctx => ctx.SingleCommit);
        AddRouted(other, "Bisect: skip", ctx => ctx.SingleCommit);
        AddRouted(other, "Bisect: stop/reset", ctx => true);

        // Anything the host registered that this menu does not place explicitly
        // still has to appear (AddCommitCommand is the shell's only hook), so it
        // lands here rather than being dropped.
        foreach ((string header, Action<string> handler) in _commitCommands)
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

        return items;
    }

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

    // --- Menu wiring helpers ---------------------------------------------------

    private static string Strip(string caption) => RevisionFilterDialog.StripMnemonic(caption);

    private static MenuItem MakeItem(string header, Action action)
    {
        MenuItem item = new() { Header = Strip(header) };
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
    private MenuItem? Routed(string header, Func<MenuCtx, bool> visible, Func<MenuCtx, bool>? enabled = null)
    {
        foreach ((string registered, Action<string> handler) in _commitCommands)
        {
            if (!string.Equals(registered, header, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _routedCommands.Add(registered);
            MenuItem item = new() { Header = registered };
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

    private void AddRouted(MenuItem parent, string header, Func<MenuCtx, bool> enabled)
    {
        if (Routed(header, ctx => true, enabled) is { } item)
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
                item.Header = $"{baseCaption} '{name}'…";
                item.IsVisible = true;
                item.IsEnabled = enabled?.Invoke(ctx, name) ?? true;
            });

            slots[i] = item;
        }

        return slots;
    }

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
            char kind = _refKinds.TryGetValue(name, out char known)
                ? known
                : IsTagRef(name) ? 't' : IsRemoteRef(name) ? 'r' : 'b';

            switch (kind)
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
            _currentBranch);
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

        // Oldest first, matching the grid's own two-row selection behaviour.
        RangeSelected?.Invoke(rows[1].Hash, rows[0].Hash);
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

        RunRefOp(
            string.Format(T("Checking out {0}…"), name),
            repo => _branchTags.Checkout(repo, name, changes));
    }

    private async Task MergeRefAsync(string name)
    {
        if (_repoPath.Length == 0 || _currentBranch.Length == 0)
        {
            FlashStatus(T("TranslatedStrings/_errorCaptionNotOnBranch.Text", "Not on a branch"));
            return;
        }

        if (!await ConfirmAsync(string.Format(T("Merge '{0}' into '{1}'?"), name, _currentBranch)))
        {
            return;
        }

        RunRefOp(
            string.Format(T("Merging {0}…"), name),
            repo => _branchTags.MergeBranch(repo, name));
    }

    private async Task RebaseOnSelectedAsync()
    {
        if (_repoPath.Length == 0 || _menuRow is not { } row || IsArtificial(row))
        {
            return;
        }

        if (!await ConfirmAsync(T(
            "RevisionGridControl/_areYouSureRebase.Text",
            "Are you sure you want to rebase? This action will rewrite commit history.")))
        {
            return;
        }

        string target = row.Hash;
        RunRefOp(
            string.Format(T("Rebasing on {0}…"), row.ShortHash),
            repo => _branchTags.RebaseOnto(repo, target));
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

        if (!await ConfirmAsync(string.Format(
                T("Move branch '{0}' to commit {1}? {2}"),
                branch,
                row.ShortHash,
                T("TranslatedStrings/_cannotBeUndone.Text", "This action cannot be undone."))))
        {
            return;
        }

        string target = row.Hash;
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
        Reload();
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
                foreach (BranchTagRow branch in listing.Branches)
                {
                    kinds[branch.Name] = branch.IsRemote ? 'r' : 'b';
                }

                foreach (BranchTagRow tag in listing.Tags)
                {
                    kinds[tag.Name] = 't';
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _currentBranch = current;
                    _refKinds = kinds;
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
        Window dialog = new()
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
        Window dialog = new()
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
            block.FontFamily = new FontFamily("monospace,Consolas,Menlo");
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
    ///  the cells, ref pills and DAG lanes it tracks — whenever its
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
        private static readonly IBrush SelectedPillBg = new SolidColorBrush(Colors.White);
        private static readonly IBrush SelectedMarker = new SolidColorBrush(Color.FromRgb(0x9C, 0xF0, 0xB8));
        private static readonly IBrush FocusRect = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

        // Fallbacks used only when the themed brushes are missing/non-solid.
        private static readonly Color AccentFallback = Color.FromRgb(0x00, 0x7A, 0xCC);
        private static readonly Color SelectionFallback = Color.FromRgb(0x09, 0x47, 0x71);

        private readonly IBrush _normalBg;
        private readonly Border _focusRect;
        private readonly List<(TextBlock Block, IBrush Normal, IBrush Selected)> _texts = [];
        private readonly List<(Border Pill, IBrush Normal)> _pills = [];
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

        public void TrackPill(Border pill)
            => _pills.Add((pill, pill.Background ?? B("App.Panel")));

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

            Color accent = ColorOf("App.Accent", AccentFallback);
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
                Background = B("App.PanelAlt");
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

            foreach ((Border pill, IBrush normal) in _pills)
            {
                pill.Background = selected ? SelectedPillBg : normal;
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
        private static readonly Color[] LaneColors =
        {
            Color.FromRgb(0x22, 0x8B, 0x22), // green
            Color.FromRgb(0x1E, 0x90, 0xFF), // blue
            Color.FromRgb(0xFF, 0x8C, 0x00), // orange
            Color.FromRgb(0x93, 0x70, 0xDB), // purple
            Color.FromRgb(0xDC, 0x14, 0x3C), // crimson
            Color.FromRgb(0x00, 0x8B, 0x8B), // teal
            Color.FromRgb(0xB8, 0x86, 0x0B), // goldenrod
            Color.FromRgb(0xFF, 0x14, 0x93), // pink
        };

        private static readonly IBrush[] LaneBrushes =
            LaneColors.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();

        private readonly IReadOnlyList<RevisionGraphSegment> _segments;
        private readonly int _nodeLane;
        private readonly double _laneWidth;
        private readonly bool _artificialNode;

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
            IReadOnlyList<bool>? relativeSegments = null,
            bool relativeNode = true,
            IBrush? nonRelativeBrush = null)
        {
            _segments = segments;
            _nodeLane = nodeLane;
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

        private static Color LaneColor(int lane)
            => LaneColors[((lane % LaneColors.Length) + LaneColors.Length) % LaneColors.Length];

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

                    Pen pen = new(Brush(s.ColorLane, relative), 2);
                    context.DrawLine(
                        pen,
                        new Point(X(s.FromLane), s.FromY * h),
                        new Point(X(s.ToLane), s.ToY * h));
                }
            }

            IBrush nodeBrush = Brush(_nodeLane, _relativeNode);
            double cx = X(_nodeLane);
            double cy = h / 2;

            if (_artificialNode)
            {
                // Artificial rows (working directory / commit index) get a distinct
                // node: a hollow square in the lane colour, echoing the special
                // marker the original Windows grid uses for pending work.
                const double half = 4.0;
                Pen outline = new(nodeBrush, 2);
                context.DrawRectangle(null, outline, new Rect(cx - half, cy - half, half * 2, half * 2));
                return;
            }

            IPen? ring = _rowSelected ? new Pen(Brushes.White, 1.5) : null;
            context.DrawEllipse(nodeBrush, ring, new Point(cx, cy), 4, 4);
        }
    }
}
