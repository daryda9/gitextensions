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

    // Row metrics — kept tight for a dense, GitExtensions-like log.
    private const double RowFontSize = 12;

    private readonly RevisionService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ContentControl _headerHost;
    private readonly TextBox _search;

    // "Go to ▾" bar button (holds the navigation flyout) and its hash entry box,
    // kept as fields so a keyboard shortcut (Ctrl+G) can open + focus them.
    private readonly Button _goToButton;
    private readonly TextBox _goToBox;

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

    // The two artificial rows shown ABOVE the commit list ("Working directory"
    // and "Commit index"), mirroring the original Git Extensions grid. These are
    // a fixed panel docked at the top of the grid — deliberately NOT part of the
    // RevisionRow model or the ListBox ItemsSource — so the commit list, its
    // selection, graph and range-diff stay untouched. Fed counts by MainWindow
    // via SetWorkingState; this view never queries git itself for them.
    private readonly StackPanel _topRows;
    private readonly TextBlock _wdCheck;
    private readonly TextBlock _wdCount;
    private readonly TextBlock _idxCheck;
    private readonly TextBlock _idxCount;

    // The full, graph-built revision set as loaded from git; filtering selects a
    // subset from this without re-running git or touching the underlying model.
    private IReadOnlyList<RevisionRow> _allRows = [];

    // The rows currently displayed, kept so BuildRow can compute a row's index
    // (for the subtle alternating-row background).
    private IReadOnlyList<RevisionRow> _rows = [];

    // True while a non-empty filter is applied. The DAG graph is drawn from
    // segments precomputed against ADJACENT rows in the full list, so showing an
    // arbitrary subset would leave lane lines/edges pointing at hidden neighbours
    // (a garbled graph). While filtering we therefore collapse the graph column
    // to zero width and skip drawing it, restoring it in full when the filter is
    // cleared. The underlying model (_allRows) is never mutated.
    private bool _filterActive;

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

    // Column visibility toggles (the graph + Subject columns always stay).
    private bool _showHash = true;
    private bool _showAvatar = true;   // offline identicon avatar; default ON
    private bool _showAuthor = true;
    private bool _showDate = true;

    // "View" toggles from the original grid. The first four change WHICH commits
    // the walk includes (or the walk order) and therefore reload via the existing
    // load path; the last two are render-time styles applied by RefreshView().
    private bool _showRemotes = true;   // include refs/remotes in the walk
    private bool _showTags = true;      // include refs/tags in the walk
    private bool _showStashes;          // include stash commits in the walk
    private bool _topoOrder;            // --topo-order vs default date order
    private bool _drawNonRelativesGray; // dim rows not reachable from/to HEAD
    private bool _highlightCurrentBranch; // emphasise the current branch's first-parent line

    // Reachability sets computed from the loaded rows whenever _allRows changes,
    // keyed by full hash. Ancestors ∪ descendants ∪ HEAD are the "relatives" of
    // the current branch; _currentBranchLine is HEAD's first-parent chain.
    private HashSet<string> _headRelatives = [];
    private HashSet<string> _currentBranchLine = [];

    // Palette pulled from the shared app resources (see App.cs).
    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Width of the graph column when NOT filtering; updated to fit the loaded
    // graph's lane count. While a filter is active the effective width is 0.
    private double _graphWidth = LaneWidth;

    // The column width actually used by the header/rows right now (0 while filtering).
    private double EffectiveGraphWidth => _filterActive ? 0 : _graphWidth;

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

    // Host-registered commit-targeted actions (checkout, cherry-pick, reset, …),
    // appended to each row's context menu. Each handler receives the full hash.
    private readonly List<(string Header, Action<string> Handler)> _commitCommands = [];

    /// <summary>
    ///  Registers an extra context-menu command shown on each commit row; the
    ///  handler is invoked with the row's full commit hash.
    /// </summary>
    public void AddCommitCommand(string header, Action<string> handler)
        => _commitCommands.Add((header, handler));

    public RevisionGridView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(10, 6, 10, 6),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            Background = B("App.Toolbar"),
            Padding = new Thickness(0, 2, 0, 2),
            Text = "No repository loaded.",
        };

        _headerHost = new ContentControl { Content = BuildHeader() };

        _search = new TextBox
        {
            Watermark = "Filter: author / message / hash",
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
        Button dateButton = MakeBarButton("Date ▾");
        dateButton.Flyout = BuildDateFlyout();

        Button columnsButton = MakeBarButton("Columns ▾");
        columnsButton.Flyout = BuildColumnsFlyout();

        // Compact commit-navigation control: first-parent / child jumps plus a
        // "go to commit" hash box. Also reachable via keyboard (Alt+↑ / Alt+↓ / Ctrl+G).
        _goToBox = new TextBox
        {
            Watermark = "hash (full or short)",
            Background = B("App.Window"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            MinWidth = 150,
            Padding = new Thickness(6, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _goToButton = MakeBarButton("Go to ▾");
        _goToButton.Flyout = BuildGoToFlyout();

        // Branch-scope control: All branches / Current branch only / Filtered.
        // Switching re-runs the log through the existing load path (Reload).
        Button branchesButton = MakeBarButton("Branches ▾");
        branchesButton.Flyout = BuildBranchesFlyout();

        // "View" control: remote/tag/stash inclusion, walk order, and the two
        // render-time highlight styles. Walk-affecting toggles reload; render-time
        // ones re-template via RefreshView().
        Button viewButton = MakeBarButton("View ▾");
        viewButton.Flyout = BuildViewFlyout();

        DockPanel bar = new();
        DockPanel.SetDock(dateButton, Dock.Right);
        DockPanel.SetDock(columnsButton, Dock.Right);
        DockPanel.SetDock(viewButton, Dock.Right);
        DockPanel.SetDock(branchesButton, Dock.Right);
        DockPanel.SetDock(_goToButton, Dock.Right);
        bar.Children.Add(columnsButton);
        bar.Children.Add(dateButton);
        bar.Children.Add(viewButton);
        bar.Children.Add(branchesButton);
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
            // Two rows selected => diff the range. The grid is newest-first, so the
            // row with the higher index in Items is the OLDER commit (= baseHash);
            // the lower index is the NEWER commit (= otherHash).
            if (_list.SelectedItems is { Count: 2 } sel
                && sel[0] is RevisionRow a && sel[1] is RevisionRow b)
            {
                int ia = _list.Items.IndexOf(a);
                int ib = _list.Items.IndexOf(b);
                RevisionRow older = ia >= ib ? a : b;
                RevisionRow newer = ia >= ib ? b : a;
                RangeSelected?.Invoke(older.Hash, newer.Hash);
            }
            else if (_list.SelectedItem is RevisionRow row)
            {
                RevisionSelected?.Invoke(row.Hash);
            }
        };

        // Keyboard: Ctrl+C copies the selected commit's hash; Alt+↑ jumps to the
        // first parent, Alt+↓ to the nearest child, Ctrl+G opens the "Go to" box.
        // (Plain Up/Down selection is handled by the ListBox and fires
        // RevisionSelected via SelectionChanged above.)
        _list.KeyDown += (_, e) =>
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool quickActive = _quickSearch.Length > 0;

            if (ctrl && e.Key == Key.C && _list.SelectedItem is RevisionRow row)
            {
                Copy(row.Hash);
                e.Handled = true;
            }
            else if (alt && e.Key == Key.Up)
            {
                GoToParent();
                e.Handled = true;
            }
            else if (alt && e.Key == Key.Down)
            {
                GoToChild();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.G)
            {
                OpenGoTo();
                e.Handled = true;
            }
            // --- quick-search navigation (only when the list itself is focused) ---
            else if (e.Key == Key.F3)
            {
                // F3 / Shift+F3 step to the next / previous match, wrapping.
                if (quickActive)
                {
                    QuickSearchStep(forward: !shift);
                    e.Handled = true;
                }
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
        };

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

        Panel listHost = new();
        listHost.Children.Add(_list);
        listHost.Children.Add(_quickSearchOverlay);

        // --- Artificial top rows ("Working directory" / "Commit index"). ---
        // A dumb, fixed two-row panel that sits directly above the commit list
        // and lines up with it visually. Counts + check glyphs are pushed in by
        // MainWindow through SetWorkingState; clicking a row raises an event.
        _wdCheck = TopRowGlyph();
        _wdCount = TopRowCount();
        _idxCheck = TopRowGlyph();
        _idxCount = TopRowCount();
        Border wdRow = BuildTopRow(_wdCheck, "Working directory", _wdCount,
            () => WorkingDirectorySelected?.Invoke());
        Border idxRow = BuildTopRow(_idxCheck, "Commit index", _idxCount,
            () => CommitIndexSelected?.Invoke());
        _topRows = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Background = B("App.Panel"),
        };
        _topRows.Children.Add(wdRow);
        _topRows.Children.Add(idxRow);
        // Start in the "clean" look until MainWindow reports the working state.
        SetWorkingState(0, 0);

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(searchBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_headerHost, Dock.Top);
        DockPanel.SetDock(_topRows, Dock.Top);
        root.Children.Add(searchBar);
        root.Children.Add(_status);
        root.Children.Add(_headerHost);
        root.Children.Add(_topRows);
        root.Children.Add(listHost);

        Content = root;
    }

    // Green check glyph for a top row; hidden/hollow when the row is clean.
    private static TextBlock TopRowGlyph() => new()
    {
        Text = "✔",
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };

    // Trailing count label for a top row (e.g. "3 modified" / "+2 staged").
    private static TextBlock TopRowCount() => new()
    {
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };

    // Builds one clickable artificial row: [check] label … count, styled to
    // match a grid row (App.Panel background, subtle border). ClipToBounds keeps
    // any glyph inside the row box.
    private Border BuildTopRow(TextBlock check, string label, TextBlock count, Action onClick)
    {
        DockPanel content = new() { Margin = new Thickness(10, 0, 10, 0) };
        TextBlock text = new()
        {
            Text = label,
            FontSize = 12,
            Foreground = B("App.Text"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(check, Dock.Left);
        DockPanel.SetDock(count, Dock.Right);
        content.Children.Add(check);
        content.Children.Add(count);
        content.Children.Add(text);

        Border row = new()
        {
            Background = B("App.Panel"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 4, 0, 4),
            MinHeight = 24,
            Cursor = new Cursor(StandardCursorType.Hand),
            ClipToBounds = true,
            Child = content,
        };
        row.PointerPressed += (_, _) => onClick();
        return row;
    }

    /// <summary>
    ///  Feeds the two artificial top rows their pending-work counts. When a count
    ///  is &gt; 0 the row shows a bright green check and a count label; when 0 it
    ///  takes on a dim, "clean" look. Fed by MainWindow (which already computes
    ///  these); this view never queries git for them itself.
    /// </summary>
    public void SetWorkingState(int unstaged, int staged)
    {
        bool wdDirty = unstaged > 0;
        _wdCheck.Foreground = wdDirty ? Brushes.MediumSeaGreen : B("App.TextDim");
        _wdCheck.Opacity = wdDirty ? 1.0 : 0.35;
        _wdCount.Text = wdDirty ? $"{unstaged} modified" : string.Empty;
        _wdCount.Foreground = B("App.TextDim");

        bool idxDirty = staged > 0;
        _idxCheck.Foreground = idxDirty ? Brushes.MediumSeaGreen : B("App.TextDim");
        _idxCheck.Opacity = idxDirty ? 1.0 : 0.35;
        _idxCount.Text = idxDirty ? $"+{staged} staged" : string.Empty;
        _idxCount.Foreground = idxDirty ? Brushes.MediumSeaGreen : B("App.TextDim");
    }

    /// <summary>
    ///  Loads and displays the recent revisions of the repository at
    ///  <paramref name="repoPath"/>. Heavy git work runs off the UI thread.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        Reload();
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

        string repoPath = _repoPath;
        BranchScope scope = _branchScope;
        bool showRemotes = _showRemotes;
        bool showTags = _showTags;
        bool showStashes = _showStashes;
        bool topoOrder = _topoOrder;

        _list.ItemsSource = null;
        _status.Text = "Loading…";

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<RevisionRow> rows = _service.LoadRevisions(
                    repoPath,
                    scope: scope,
                    showRemotes: showRemotes,
                    showTags: showTags,
                    showStashes: showStashes,
                    topoOrder: topoOrder);
                Dispatcher.UIThread.Post(() =>
                {
                    int laneCount = rows.Count > 0 ? rows[0].LaneCount : 1;
                    _graphWidth = Math.Max(1, laneCount) * LaneWidth;
                    _allRows = rows;
                    _repoLabel = repoPath;
                    // Recompute HEAD reachability for the relatives/highlight styles.
                    ComputeReachability();
                    // Re-apply any current filter text so a reload keeps the view consistent.
                    ApplyFilterCore(_search.Text);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = "Error: " + ex.Message);
            }
        });
    }

    // Human label for the current branch scope, shown in the status line so the
    // effect of the toggle (and the resulting commit count) is visible.
    private string ScopeLabel => _branchScope switch
    {
        BranchScope.AllBranches => "all branches",
        BranchScope.CurrentBranch => "current branch",
        BranchScope.Filtered => "filtered (current branch)",
        _ => "all branches",
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

    private void ApplyFilterCore(string? text)
    {
        string query = (text ?? string.Empty).Trim();
        bool wasFiltering = _filterActive;
        _filterActive = query.Length > 0;

        IReadOnlyList<RevisionRow> filtered;
        if (!_filterActive)
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

        _rows = filtered;

        // The graph column width changes with the filter state; rebuild the
        // header so its columns stay aligned with the (re-templated) rows.
        _headerHost.Content = BuildHeader();

        // Reassign the source so every visible row is rebuilt against the current
        // filter/graph state (and stale selection is dropped).
        _list.ItemsSource = null;
        _list.ItemsSource = filtered;

        if (_filterActive)
        {
            _status.Text = $"{_repoLabel}  —  {filtered.Count} of {_allRows.Count} commits  ({ScopeLabel}; filter: \"{query}\")";
        }
        else
        {
            _status.Text = _allRows.Count == 0
                ? "No repository loaded."
                : $"{_repoLabel}  —  {_allRows.Count} commits  ({ScopeLabel})";
        }

        _ = wasFiltering; // (state kept for clarity; no extra action needed)
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
        IReadOnlyList<RevisionRow> current = _rows;
        _list.ItemsSource = null;
        _list.ItemsSource = current;
    }

    // Recomputes, from the loaded rows, HEAD's reachability sets used by the two
    // render-time "View" highlight styles. Best-effort and bounded to the loaded
    // window: if HEAD is not among the loaded rows the sets stay empty and both
    // styles become no-ops. Ancestors (all parents) ∪ descendants (all children)
    // ∪ HEAD are the "relatives"; the current-branch line is HEAD's first-parent
    // chain. Uses only ParentHashes already carried on each row — no git.
    private void ComputeReachability()
    {
        _headRelatives = [];
        _currentBranchLine = [];

        RevisionRow? head = null;
        foreach (RevisionRow row in _allRows)
        {
            if (row.IsHead)
            {
                head = row;
                break;
            }
        }

        if (head is null)
        {
            return;
        }

        // Index by hash for O(1) parent/child lookups, and a parent -> children map.
        Dictionary<string, RevisionRow> byHash = new(_allRows.Count);
        Dictionary<string, List<string>> children = [];
        foreach (RevisionRow row in _allRows)
        {
            byHash[row.Hash] = row;
        }

        foreach (RevisionRow row in _allRows)
        {
            foreach (string parent in row.ParentHashes)
            {
                if (!children.TryGetValue(parent, out List<string>? kids))
                {
                    kids = [];
                    children[parent] = kids;
                }

                kids.Add(row.Hash);
            }
        }

        // Ancestors: walk parents from HEAD. Descendants: walk children from HEAD.
        HashSet<string> relatives = [head.Hash];
        Walk(head.Hash, relatives, h => byHash.TryGetValue(h, out RevisionRow? r) ? r.ParentHashes : []);
        Walk(head.Hash, relatives, h => children.TryGetValue(h, out List<string>? c) ? c : []);
        _headRelatives = relatives;

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

    // "View" menu: which refs the log walks (remotes / tags / stashes), the walk
    // order (date vs topological), and the two render-time highlight styles. The
    // first four reload via Reload() (preserving filter/notes/date/columns/DAG);
    // the last two only re-template via RefreshView().
    private Flyout BuildViewFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 210 };

        panel.Children.Add(SectionLabel("Show in log"));

        CheckBox remotes = MakeCheck("Remote branches", _showRemotes);
        remotes.IsCheckedChanged += (_, _) =>
        {
            _showRemotes = remotes.IsChecked == true;
            Reload();
        };

        CheckBox tags = MakeCheck("Tags", _showTags);
        tags.IsCheckedChanged += (_, _) =>
        {
            _showTags = tags.IsChecked == true;
            Reload();
        };

        CheckBox stashes = MakeCheck("Stashes", _showStashes);
        stashes.IsCheckedChanged += (_, _) =>
        {
            _showStashes = stashes.IsChecked == true;
            Reload();
        };

        panel.Children.Add(remotes);
        panel.Children.Add(tags);
        panel.Children.Add(stashes);

        panel.Children.Add(SectionLabel("Order"));
        RadioButton dateOrder = MakeRadio("Date order", "revOrder", !_topoOrder);
        RadioButton topoOrder = MakeRadio("Topo-order", "revOrder", _topoOrder);
        dateOrder.IsCheckedChanged += (_, _) =>
        {
            if (dateOrder.IsChecked == true && _topoOrder)
            {
                _topoOrder = false;
                Reload();
            }
        };
        topoOrder.IsCheckedChanged += (_, _) =>
        {
            if (topoOrder.IsChecked == true && !_topoOrder)
            {
                _topoOrder = true;
                Reload();
            }
        };
        panel.Children.Add(dateOrder);
        panel.Children.Add(topoOrder);

        panel.Children.Add(SectionLabel("Highlighting"));

        CheckBox nonRelatives = MakeCheck("Draw non-relatives gray", _drawNonRelativesGray);
        nonRelatives.IsCheckedChanged += (_, _) =>
        {
            _drawNonRelativesGray = nonRelatives.IsChecked == true;
            RefreshView();
        };

        CheckBox highlight = MakeCheck("Highlight current branch", _highlightCurrentBranch);
        highlight.IsCheckedChanged += (_, _) =>
        {
            _highlightCurrentBranch = highlight.IsChecked == true;
            RefreshView();
        };

        panel.Children.Add(nonRelatives);
        panel.Children.Add(highlight);

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
    private static string Relative(DateTime dt)
    {
        TimeSpan span = DateTime.Now - dt;
        if (span.Ticks < 0)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return "just now";
        }

        if (span.TotalMinutes < 60)
        {
            int m = (int)span.TotalMinutes;
            return $"{m} minute{(m == 1 ? "" : "s")} ago";
        }

        if (span.TotalHours < 24)
        {
            int h = (int)span.TotalHours;
            return $"{h} hour{(h == 1 ? "" : "s")} ago";
        }

        if (span.TotalDays < 30)
        {
            int d = (int)span.TotalDays;
            return $"{d} day{(d == 1 ? "" : "s")} ago";
        }

        if (span.TotalDays < 365)
        {
            int mo = (int)(span.TotalDays / 30);
            return $"{mo} month{(mo == 1 ? "" : "s")} ago";
        }

        int y = (int)(span.TotalDays / 365);
        return $"{y} year{(y == 1 ? "" : "s")} ago";
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

        panel.Children.Add(SectionLabel("Date shown"));
        RadioButton commit = MakeRadio("Commit date", "revDateSrc", _dateSource == DateSource.Commit);
        RadioButton author = MakeRadio("Author date", "revDateSrc", _dateSource == DateSource.Author);
        commit.IsCheckedChanged += (_, _) =>
        {
            if (commit.IsChecked == true)
            {
                _dateSource = DateSource.Commit;
                RefreshView();
            }
        };
        author.IsCheckedChanged += (_, _) =>
        {
            if (author.IsChecked == true)
            {
                _dateSource = DateSource.Author;
                RefreshView();
            }
        };
        panel.Children.Add(commit);
        panel.Children.Add(author);

        panel.Children.Add(SectionLabel("Format"));
        RadioButton absolute = MakeRadio("Absolute", "revDateFmt", !_relativeDates);
        RadioButton relative = MakeRadio("Relative", "revDateFmt", _relativeDates);
        absolute.IsCheckedChanged += (_, _) =>
        {
            if (absolute.IsChecked == true)
            {
                _relativeDates = false;
                RefreshView();
            }
        };
        relative.IsCheckedChanged += (_, _) =>
        {
            if (relative.IsChecked == true)
            {
                _relativeDates = true;
                RefreshView();
            }
        };
        panel.Children.Add(absolute);
        panel.Children.Add(relative);

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
        panel.Children.Add(SectionLabel("Show columns"));

        CheckBox hash = MakeCheck("Commit ID", _showHash);
        hash.IsCheckedChanged += (_, _) =>
        {
            _showHash = hash.IsChecked == true;
            RefreshView();
        };

        CheckBox avatar = MakeCheck("Avatar", _showAvatar);
        avatar.IsCheckedChanged += (_, _) =>
        {
            _showAvatar = avatar.IsChecked == true;
            RefreshView();
        };

        CheckBox author = MakeCheck("Author", _showAuthor);
        author.IsCheckedChanged += (_, _) =>
        {
            _showAuthor = author.IsChecked == true;
            RefreshView();
        };

        CheckBox date = MakeCheck("Date", _showDate);
        date.IsCheckedChanged += (_, _) =>
        {
            _showDate = date.IsChecked == true;
            RefreshView();
        };

        panel.Children.Add(hash);
        panel.Children.Add(avatar);
        panel.Children.Add(author);
        panel.Children.Add(date);

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

        panel.Children.Add(SectionLabel("Branches shown"));

        RadioButton all = MakeRadio("All branches", "revBranchScope", _branchScope == BranchScope.AllBranches);
        RadioButton current = MakeRadio("Current branch only", "revBranchScope", _branchScope == BranchScope.CurrentBranch);
        RadioButton filtered = MakeRadio("Filtered branches", "revBranchScope", _branchScope == BranchScope.Filtered);

        all.IsCheckedChanged += (_, _) => SelectScope(all, BranchScope.AllBranches);
        current.IsCheckedChanged += (_, _) => SelectScope(current, BranchScope.CurrentBranch);
        filtered.IsCheckedChanged += (_, _) => SelectScope(filtered, BranchScope.Filtered);

        panel.Children.Add(all);
        panel.Children.Add(current);
        panel.Children.Add(filtered);

        // "Filtered" has no selection UI yet, so it walks the current branch.
        panel.Children.Add(new TextBlock
        {
            Text = "Filtered walks the current branch until a ref picker is added.",
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

    // Applies a newly-checked branch-scope radio: updates the mode and re-runs the
    // log. Guarded so the uncheck half of a radio pair does nothing, and a no-op
    // re-selection of the same mode does not trigger a redundant reload.
    private void SelectScope(RadioButton radio, BranchScope scope)
    {
        // The uncheck half of a radio pair fires too; ignore it and defer to the
        // shared SetBranchScope so the header menu and the toolbar drive one path.
        if (radio.IsChecked != true)
        {
            return;
        }

        SetBranchScope(scope);
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
    }

    // "Go to" menu: buttons to jump to the first parent / nearest child of the
    // current selection, plus a hash box to select an arbitrary commit. All three
    // also work via keyboard (Alt+↑, Alt+↓, Ctrl+G).
    private Flyout BuildGoToFlyout()
    {
        StackPanel panel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 190 };

        Flyout flyout = new();

        panel.Children.Add(SectionLabel("Navigate"));

        Button parent = MakeMenuButton("↑  First parent   (Alt+↑)");
        parent.Click += (_, _) =>
        {
            flyout.Hide();
            GoToParent();
        };

        Button child = MakeMenuButton("↓  Nearest child   (Alt+↓)");
        child.Click += (_, _) =>
        {
            flyout.Hide();
            GoToChild();
        };

        panel.Children.Add(parent);
        panel.Children.Add(child);

        panel.Children.Add(SectionLabel("Go to commit"));
        panel.Children.Add(_goToBox);

        Button go = MakeMenuButton("Select commit");
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

    // Opens the "Go to" flyout and focuses the hash box (Ctrl+G).
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

    // Quick-search matches on the subject or author (not the hash — that is what
    // the "Go to commit" box is for), case-insensitively.
    private static bool QuickMatches(RevisionRow row, string query)
        => row.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Author.Contains(query, StringComparison.OrdinalIgnoreCase);

    // Shows/refreshes the transient adorner and (re)arms the idle-dismiss timer.
    private void ShowQuickSearch(bool found)
    {
        _quickSearchLabel.Text = found
            ? $"quick-search: {_quickSearch}…"
            : $"quick-search: {_quickSearch}…  (no match)";
        _quickSearchLabel.Foreground = found ? B("App.Text") : B("App.TextDim");
        _quickSearchOverlay.IsVisible = true;

        _quickSearchTimer.Stop();
        _quickSearchTimer.Start();
    }

    // Clears the buffer and hides the adorner (Esc, empty backspace, or idle).
    private void EndQuickSearch()
    {
        _quickSearchTimer.Stop();
        _quickSearch = string.Empty;
        _quickSearchOverlay.IsVisible = false;
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
            _status.Text = "No parent commit (root).";
            return;
        }

        if (!SelectByHash(row.ParentHashes[0]))
        {
            _status.Text = "Parent commit is not in the loaded history.";
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
            _status.Text = "No child commit in the loaded history.";
            return;
        }

        SelectRow(best);
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

        int index = FindIndex(_rows, query);
        if (index < 0 && _filterActive)
        {
            // Drop the filter (ApplyFilter resets _rows to _allRows) and retry.
            _search.Text = string.Empty;
            index = FindIndex(_rows, query);
        }

        if (index < 0)
        {
            _status.Text = $"No commit matching \"{query}\".";
            return;
        }

        SelectIndex(index);
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
            [ToolTip.TipProperty] = "This commit has a git note",
            Child = new TextBlock
            {
                Text = "note",
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

        // Columns: 0 graph, 1 hash, 2 avatar, 3 author, 4 date, 5 subject.
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{EffectiveGraphWidth},{hash},{avatar},{author},{date},*"),
        };
    }

    private Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);

        AddCell(grid, 0, string.Empty, B("App.TextDim"), bold: true);
        if (_showHash)
        {
            AddCell(grid, 1, "Commit ID", B("App.TextDim"), bold: true);
        }

        // Column 2 (avatar) has no textual header — the identicons speak for themselves.
        if (_showAuthor)
        {
            AddCell(grid, 3, "Author", B("App.TextDim"), bold: true);
        }

        if (_showDate)
        {
            AddCell(grid, 4, _relativeDates ? "Date (rel.)" : "Date", B("App.TextDim"), bold: true);
        }

        AddCell(grid, 5, "Subject", B("App.TextDim"), bold: true);

        return new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 3, 0, 3),
            Child = grid,
        };
    }

    private Control BuildRow(RevisionRow row)
    {
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
        if (!_filterActive)
        {
            RevisionGraphControl graph = new(row.GraphSegments, row.NodeLane, LaneWidth);
            Grid.SetColumn(graph, 0);
            grid.Children.Add(graph);
            view.TrackGraph(graph);
        }

        // Render-time "View" highlight styles (no reload):
        //  - highlight current branch: HEAD's first-parent line is emphasised
        //    (accent + bold), taking precedence over graying.
        //  - draw non-relatives gray: rows not reachable from/to HEAD are dimmed.
        //    Guarded on a non-empty relatives set so it is a no-op when HEAD is
        //    outside the loaded window.
        bool onBranch = _highlightCurrentBranch && _currentBranchLine.Contains(row.Hash);
        bool nonRelative = !onBranch && _drawNonRelativesGray
            && _headRelatives.Count > 0 && !_headRelatives.Contains(row.Hash);

        IBrush hashBrush = nonRelative ? B("App.TextDim") : B("App.Accent");
        IBrush subjectBrush = onBranch ? B("App.Accent") : nonRelative ? B("App.TextDim") : B("App.Text");

        // Hash: monospace + accent so it reads as a code identifier.
        if (_showHash)
        {
            view.TrackText(AddCell(grid, 1, row.ShortHash, hashBrush, bold: onBranch, monospace: true));
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
            view.TrackText(AddCell(grid, 3, row.Author, B("App.TextDim")), dim: true);
        }

        if (_showDate)
        {
            view.TrackText(AddCell(grid, 4, FormatDate(row), B("App.TextDim")), dim: true);
        }

        // Subject cell: an optional git-notes indicator, then ref badges, then the
        // subject text.
        StackPanel subject = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (row.HasNotes)
        {
            subject.Children.Add(BuildNotesBadge());
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
            subject.Children.Add(BuildRefBadge(refName, isCurrent, view));
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

        Grid.SetColumn(subject, 5);
        grid.Children.Add(subject);

        view.ContextMenu = BuildRowContextMenu(row);
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

    // Right-click menu: copy details of the row that was clicked.
    private ContextMenu BuildRowContextMenu(RevisionRow row)
    {
        MenuItem copyHash = new() { Header = "Copy commit hash" };
        copyHash.Click += (_, _) => Copy(row.Hash);

        MenuItem copySubject = new() { Header = "Copy subject" };
        copySubject.Click += (_, _) => Copy(row.Subject);

        MenuItem copyAuthor = new() { Header = "Copy author" };
        copyAuthor.Click += (_, _) => Copy(row.Author);

        ContextMenu menu = new()
        {
            Items =
            {
                copyHash,
                copySubject,
                copyAuthor,
            },
        };

        if (_commitCommands.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach ((string header, Action<string> handler) in _commitCommands)
            {
                MenuItem item = new() { Header = header };
                item.Click += (_, _) => handler(row.Hash);
                menu.Items.Add(item);
            }
        }

        return menu;
    }

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
        private bool _rowSelected;

        public RevisionGraphControl(IReadOnlyList<RevisionGraphSegment> segments, int nodeLane, double laneWidth)
        {
            _segments = segments;
            _nodeLane = nodeLane;
            _laneWidth = laneWidth;

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

        private IBrush Brush(int lane)
        {
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

            foreach (RevisionGraphSegment s in _segments)
            {
                Pen pen = new(Brush(s.ColorLane), 2);
                context.DrawLine(
                    pen,
                    new Point(X(s.FromLane), s.FromY * h),
                    new Point(X(s.ToLane), s.ToY * h));
            }

            IBrush nodeBrush = Brush(_nodeLane);
            IPen? ring = _rowSelected ? new Pen(Brushes.White, 1.5) : null;
            context.DrawEllipse(nodeBrush, ring, new Point(X(_nodeLane), h / 2), 4, 4);
        }
    }
}
