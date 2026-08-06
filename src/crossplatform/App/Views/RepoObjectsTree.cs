using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Left-hand repository-objects tree for the Avalonia port, mirroring the
///  original <c>GitUI/LeftPanel/RepoObjectsTree</c>: a single <see cref="TreeView"/>
///  with top-level category nodes — Branches (local, current marked), Remotes
///  (remote branches grouped by remote), Tags and Stashes — each showing an icon
///  and a count. Double-click / Enter on a local branch checks it out; right-click
///  context menus offer checkout / merge / rebase / delete on branches, delete on
///  tags and apply / pop / drop on stashes. All git work runs off the UI thread
///  via <see cref="Task.Run"/> and marshals back with <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class RepoObjectsTree : UserControl
{
    // "<category> (<count>)" — a format, not a concatenation, so a translation is
    // free to place the count differently.
    private const string CategoryCountFormat = "{0} ({1})";

    private readonly BranchTagService _branchTagService = new();
    private readonly StashOpsService _stashService = new();
    private readonly SubmoduleService _submoduleService = new();
    private readonly RemoteService _remoteService = new();
    private readonly WorktreeService _worktreeService = new();
    private readonly RepositoryStateService _repositoryStateService = new();

    private readonly TreeView _tree;

    // --- Toolbar / search chrome (mirrors the original leftPanelToolStrip +
    // branchSearchPanel above the tree) ----------------------------------------
    private readonly TextBox _search;

    // Per-category visibility, driven by the toolbar toggles exactly like
    // upstream's tsbShow* buttons (which add/remove the whole root subtree).
    // Since M69 these are persisted (view-prefs.json → LeftPanel), the port's stand-in
    // for upstream's AppSettings.RepoObjectsTreeShow* family: RestoreFilterPrefs()
    // seeds them before the toolbar is built and PersistFilterPrefs() writes them back.
    private bool _showBranches = true;
    private bool _showRemotes = true;
    private bool _showWorktrees = true;
    private bool _showTags = true;
    private bool _showSubmodules = true;
    private bool _showStashes = true;

    // Incremental tree filter. Empty means "no filter" and the tree is shown whole.
    private string _filter = string.Empty;

    // Searchable text per node, recorded while the tree is built: category nodes
    // match on their plain name (without the count suffix), ref leaves on the full
    // ref name the way upstream matches BaseRevisionNode.FullPath rather than the
    // shortened label. Rebuilt from scratch on every BuildTree.
    private readonly Dictionary<TreeViewItem, string> _nodeText = new();
    private readonly Dictionary<TreeViewItem, TreeViewItem?> _nodeParent = new();

    // --- Expand / selection state that must survive a rebuild ------------------
    // BuildTree throws every TreeViewItem away and reassigns ItemsSource, so without
    // this every Refresh() — after each checkout, merge, stash, and on every
    // OperationCompleted — would re-collapse the whole tree and drop the selection.
    // Upstream keeps the same two things across a reload: Tree.FillTreeViewNode
    // snapshots GetExpandedNodesState() before repopulating, restores the selection
    // from originalSelectedNodeFullNamePath, then RestoreExpandedNodesState() and
    // ExpandPathToSelectedNode() + EnsureVerticallyVisible() (LeftPanel/Tree.cs:140-201).
    //
    // The state is keyed by a stable path built while the tree is created — the port's
    // equivalent of upstream's TreeNode.GetFullNamePath() — because the item objects
    // themselves are new on every build.
    private readonly Dictionary<TreeViewItem, string> _nodeKey = new();
    private HashSet<string> _expandedKeys = new(StringComparer.Ordinal);
    private string? _selectedKey;

    // Set while the tree, not the user, moves the selection (a rebuild restoring the
    // previous selection, or a right-click landing on another node). Suppresses the
    // RefSelected notification only — the tree's own selection really does move.
    // Without it a plain refresh would re-drive the revision grid, and (M51) opening a
    // context menu would scroll the bottom panel away while the menu was still opening.
    private bool _suppressSelectionNotify;

    // First build of a repository: nothing to restore, so the Branches node is opened
    // the way upstream's LocalBranchTree.PostFillTreeViewNode(firstTime) expands it.
    private bool _firstBuild = true;

    // Nodes whose own text matches the current filter, in breadth-first order, plus
    // the rotating cursor used by the magnifier button / Enter to cycle through them.
    private readonly List<TreeViewItem> _matches = [];
    private int _matchIndex = -1;

    private List<TreeViewItem> _roots = [];

    private string? _repoPath;

    // Set ONLY while a git MUTATION started from this control is running (checkout,
    // rebase, branch/tag create, delete, …). This is what the "Another Git operation is
    // still running" notice reports on, so it must never be held for anything but a live
    // git command: it used to double as the reload guard below, which meant the ~1.4 s
    // post-checkout tree reload kept refusing the next checkout even though git had
    // already exited. Two checkouts in a row therefore hit the modal.
    private bool _busy;

    // Reload state, deliberately separate from _busy: a reload is read-only, may overlap
    // a mutation, and must never make the UI refuse a command.
    //   _refreshEpoch  — bumped by every Refresh(); a background pass whose epoch is no
    //                    longer the current one is stale and its snapshot is dropped.
    //   _refreshing    — a background pass is in flight.
    //   _refreshQueued — a Refresh() arrived while one was in flight; the newer state has
    //                    to win, so the in-flight (older) snapshot is discarded and a new
    //                    pass is started when it lands. Coalesced, so N clicks cost one
    //                    extra pass, not N.
    private int _refreshEpoch;
    private bool _refreshing;
    private bool _refreshQueued;
    private string? _refreshingRepository;
    private Task<RepositoryNavigationSnapshot>? _navigationSnapshotTask;
    private string? _expandedSubmoduleCurrentPath;

    // A repository switch rebuilds the tree and restores a still-valid absolute
    // submodule selection. Avalonia's BringIntoView then reveals the complete (often
    // very long) header horizontally. Keep ordinary refreshes at the user's offset,
    // but return a newly opened repository to the left edge once its layout is ready.
    private string? _horizontalHomeRepository;
    private ScrollViewer? _treeScrollViewer;

    // Guards NotifyBusy against stacking one refusal modal on top of another.
    private bool _busyNoticeOpen;

    // --- Session-local ref ordering state --------------------------------
    // All sorting/reordering below is view-only: it reorders the displayed
    // child nodes for the current session and never touches git. The last
    // loaded snapshot is retained so a re-sort can rebuild the tree without
    // re-listing refs.
    private enum RefSortKey { Name, CommitDate }
    private enum RefSortOrder { Ascending, Descending }

    private RefSortKey _sortKey = RefSortKey.Name;
    private RefSortOrder _sortOrder = RefSortOrder.Ascending;

    private RepoSnapshot? _snapshot;

    // Commit dates resolved lazily (only when the user picks "sort by commit
    // date"), keyed by full ObjectId, cached for the session.
    private readonly Dictionary<string, DateTime> _commitDates = new(StringComparer.Ordinal);
    private bool _resolvingDates;

    // --- Category order (upstream's Move Up / Move Down) ----------------------
    // Upstream's mnubtnMoveUp / mnubtnMoveDown are visible only when the selected node
    // is a category ROOT (ContextActions.cs:61-68 — `selectedNode is Tree`) and they
    // reorder the categories, persisting the new indices. The port used to put the same
    // two items on a single local BRANCH, reordering branches inside their category:
    // a different feature under upstream's name. This is the upstream one; see
    // CategoryOrder for how the loop persists it.
    private static readonly string[] DefaultCategoryOrder =
        ["branches", "remotes", "worktrees", "tags", "submodules", "stashes"];

    private List<string> _categoryOrder = [.. DefaultCategoryOrder];

    /// <summary>
    ///  Raised on the UI thread when a branch or tag node is selected, carrying the
    ///  full ObjectId / hash of the ref so the host can highlight it in the
    ///  revision grid. Not raised for nodes without a resolvable ObjectId.
    /// </summary>
    public event Action<string>? RefSelected;

    /// <summary>
    ///  Raised on the UI thread after any successful mutating operation (checkout,
    ///  merge, rebase, delete, stash apply / pop / drop) so the host can refresh.
    /// </summary>
    public event Action? OperationCompleted;

    /// <summary>
    ///  Raised on the UI thread when the user chooses "Open" on a submodule or
    ///  worktree node, carrying the absolute path to open as the active
    ///  repository. The host (MainWindow) subscribes and routes it to its own
    ///  OpenRepository — the tree never references MainWindow directly.
    /// </summary>
    public event Action<string>? OpenRepositoryRequested;

    /// <summary>Requests a separate application instance for the current submodule.</summary>
    public event Action<string>? OpenRepositoryInNewInstanceRequested;

    /// <summary>Raised when an activation cannot navigate and needs immediate user feedback.</summary>
    public event Action<string>? FeedbackRequested;

    /// <summary>
    ///  Raised on the UI thread when the Stashes root's "Manage stashes…" or a stash
    ///  node's "Open stash" / double-click wants the stash dialog — upstream's
    ///  <c>StartStashDialog(manageStashes: true, initialStash)</c>. Carries the stash to
    ///  select ("stash@{2}"), or <see langword="null"/> for none. Only the host can open
    ///  a window, so the tree asks; the menu items behind it are shown disabled while
    ///  nothing is subscribed, so they are never dead.
    /// </summary>
    public event Action<string?>? StashDialogRequested;

    /// <summary>
    ///  Raised on the UI thread by the Remotes root's "Fetch all" — upstream's
    ///  <c>mnuBtnFetchAllRemotes</c>. The host runs it, because a fetch needs the
    ///  streaming process dialog, the credential retry and the watcher suspension that
    ///  only the host owns (MainWindow.RunRemoteOp). Shown disabled while nothing is
    ///  subscribed.
    /// </summary>
    public event Action? FetchAllRequested;

    /// <summary>
    ///  Raised on the UI thread by the Remotes root's "Fetch and prune all" —
    ///  upstream's <c>mnuBtnPruneAllRemotes</c>. Routed to the host for the same
    ///  reasons as <see cref="FetchAllRequested"/>.
    /// </summary>
    public event Action? FetchAndPruneAllRequested;

    /// <summary>
    ///  Raised on the UI thread after "Move Up" / "Move Down" reordered the category
    ///  roots, so the host can persist <see cref="CategoryOrder"/> into
    ///  <c>UiState.LeftPanelCategoryOrder</c>. Reordering works in-session without a
    ///  subscriber; only the persistence needs one.
    /// </summary>
    public event Action? CategoryOrderChanged;

    /// <summary>
    ///  The order of the category root nodes, as the comma-separated id list stored in
    ///  <c>UiState.LeftPanelCategoryOrder</c>. Assigning an incomplete or unknown list
    ///  is safe: known ids are taken in the given order and every category missing from
    ///  it is appended in its default position, so no category can be lost.
    /// </summary>
    public string CategoryOrder
    {
        get => string.Join(',', _categoryOrder);
        set
        {
            List<string> order = [];
            foreach (string id in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = id.Trim();
                if (DefaultCategoryOrder.Contains(trimmed, StringComparer.Ordinal)
                    && !order.Contains(trimmed, StringComparer.Ordinal))
                {
                    order.Add(trimmed);
                }
            }

            foreach (string id in DefaultCategoryOrder)
            {
                if (!order.Contains(id, StringComparer.Ordinal))
                {
                    order.Add(id);
                }
            }

            _categoryOrder = order;
            if (_snapshot is { } snapshot)
            {
                BuildTree(snapshot);
            }
        }
    }

    public RepoObjectsTree()
    {
        // Before BuildToolbar() below: the six category toggles take their IsChecked
        // from these fields as they are created, and the sort menu reads the two sort
        // fields when it opens, so seeding them here is all the restore needs.
        RestoreFilterPrefs();

        _tree = new TreeView
        {
            Background = Brushes.Transparent,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        _tree.SelectionChanged += (_, _) => OnSelectionChanged();
        // The hotkeys of upstream's "RepoObjectsTree" scope (RepoObjectsTree.Command.cs +
        // HotkeySettingsManager.cs:267-271): Delete deletes the selected node, F2 renames
        // it, F3 jumps to the next search hit. Enter is the port's own activation key and
        // stays. Delete/F2 route to exactly the same handlers as the context-menu items,
        // so the gating (no deleting the current branch, no renaming a tag) is shared.
        _tree.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    OnActivate();
                    e.Handled = true;
                    break;

                case Key.Delete:
                    e.Handled = OnDeleteSelected();
                    break;

                case Key.F2:
                    e.Handled = OnRenameSelected();
                    break;

                case Key.F3:
                    SelectNextMatch();
                    e.Handled = true;
                    break;
            }
        };

        // A right-click on a TreeView moves the selection before the context menu opens.
        // That is wanted (Delete / F2 act on the selection, and the menu should point at
        // what was clicked), but the selection notification is not: it re-drove the
        // revision grid and scrolled the bottom panel out from under the opening menu
        // (M51). Tunnelling so it runs before the TreeView moves the selection; the flag
        // is dropped again on the next dispatcher pass, i.e. after that one selection
        // change has been delivered.
        _tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        _search = new TextBox
        {
            Watermark = T("RepoObjectsTree/btnSearch.toolTip", "Search"),
            MinWidth = 40,
            Padding = StyleDensity.BarButton,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush("App.Control", Brushes.Transparent),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            // App.BorderStrong: the filter box is delimited by its outline alone, and
            // App.Border measures 1.23:1 on the panel it sits on.
            BorderBrush = Brush("App.BorderStrong", new SolidColorBrush(Color.Parse("#88898F"))),
        };
        _search.TextChanged += (_, _) => OnFilterChanged();

        // Tunnelling with handledEventsToo: a TextBox handles most keys itself, so a
        // bubbling handler would never see Enter and could be beaten to Escape.
        _search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        Grid searchRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(3, 0, 3, 3),
        };
        searchRow.Children.Add(_search);
        // "Search", not upstream's "Preview" icon name: the button walks the matches of
        // the filter, and the 2015 PNG behind that name happened to be a magnifier. Once
        // the name had a real vector glyph it became an EYE — right for a preview,
        // wrong for a search. The name is ours to choose here; the tooltip already
        // said Search.
        Button searchButton = IconButton("Search", T("RepoObjectsTree/btnSearch.toolTip", "Search"), SelectNextMatch);
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);

        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Control toolbar = BuildToolbar();
        layout.Children.Add(toolbar);
        Grid.SetRow(searchRow, 1);
        layout.Children.Add(searchRow);
        Grid.SetRow(_tree, 2);
        layout.Children.Add(_tree);

        Background = Brush("App.Panel", Brushes.Transparent);
        Content = layout;

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // --- Toolbar ----------------------------------------------------------

    // The original's leftPanelToolStrip: "collapse all" followed by one toggle per
    // category, icon-only with a tooltip, in upstream's own strip order (worktrees
    // third, stashes last). A WrapPanel rather than a fixed-width horizontal strip so
    // a narrow left column wraps the buttons onto a second line instead of clipping.
    private Control BuildToolbar()
    {
        WrapPanel bar = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2),
        };

        bar.Children.Add(IconButton("CollapseAll", T("RepoObjectsTree/mnubtnCollapse.ToolTipText", "Collapse all subnodes"), CollapseAll));
        bar.Children.Add(CategoryToggle("LocalBranchRoot", T("RepoObjectsTree/tsbShowBranches.ToolTipText", "Branches"), _showBranches, v => _showBranches = v));
        bar.Children.Add(CategoryToggle("RemoteBranchRoot", T("RepoObjectsTree/tsbShowRemotes.ToolTipText", "Remotes"), _showRemotes, v => _showRemotes = v));
        bar.Children.Add(CategoryToggle("WorkTree", T("RepoObjectsTree/tsbShowWorktrees.ToolTipText", "Worktrees"), _showWorktrees, v => _showWorktrees = v));
        bar.Children.Add(CategoryToggle("TagHorizontal", T("RepoObjectsTree/tsbShowTags.ToolTipText", "Tags"), _showTags, v => _showTags = v));
        bar.Children.Add(CategoryToggle("FolderSubmodule", T("RepoObjectsTree/tsbShowSubmodules.ToolTipText", "Submodules"), _showSubmodules, v => _showSubmodules = v));
        bar.Children.Add(CategoryToggle("stash", T("RepoObjectsTree/tsbShowStashes.ToolTipText", "Stashes"), _showStashes, v => _showStashes = v));

        return new Border
        {
            Background = Brush("App.Toolbar", Brushes.Transparent),
            Child = bar,
        };
    }

    private Button IconButton(string icon, string tip, Action onClick)
    {
        Button button = new()
        {
            Content = IconLoader.Image(icon),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 3),
            Margin = new Thickness(0, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // A category toggle: checked = the category's root node is in the tree. Reads the
    // state back off the control after the click (Click runs post-toggle), so there is
    // a single source of truth.
    private ToggleButton CategoryToggle(string icon, string tip, bool initial, Action<bool> apply)
    {
        ToggleButton toggle = new()
        {
            Content = IconLoader.Image(icon),
            IsChecked = initial,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 3),
            Margin = new Thickness(0, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(toggle, tip);
        toggle.Click += (_, _) =>
        {
            apply(toggle.IsChecked == true);
            PersistFilterPrefs();
            if (_snapshot is { } snapshot)
            {
                BuildTree(snapshot);
            }
        };
        return toggle;
    }

    // ------------------------------------------- persisted filters (view-prefs.json)

    // The panel's WIDTH, its collapsed flag and its category ORDER are NOT here: those
    // are layout owned by the host window, which already saves them into UiState from
    // MainWindow.PersistLayout(). Only what the toolbar toggles and the sort menu change
    // lives in this file — see ViewPrefsService for why a second file exists at all.
    private static readonly ViewPrefsService PrefsService = new();

    private void RestoreFilterPrefs()
    {
        try
        {
            LeftPanelPrefs prefs = PrefsService.Load().LeftPanel;
            _showBranches = prefs.ShowBranches;
            _showRemotes = prefs.ShowRemotes;
            _showWorktrees = prefs.ShowWorktrees;
            _showTags = prefs.ShowTags;
            _showSubmodules = prefs.ShowSubmodules;
            _showStashes = prefs.ShowStashes;
            _sortKey = prefs.SortKey == "CommitDate" ? RefSortKey.CommitDate : RefSortKey.Name;
            _sortOrder = prefs.SortOrder == "Descending" ? RefSortOrder.Descending : RefSortOrder.Ascending;
        }
        catch
        {
            // Never let a preference file stop the panel from being built; the fields
            // keep the defaults they were declared with.
        }
    }

    // Update(), not Save(): the file also carries the diff options, the file-history
    // switches and the revision-filter MRU.
    private void PersistFilterPrefs() =>
        PrefsService.Update(prefs => prefs.LeftPanel = new LeftPanelPrefs
        {
            ShowBranches = _showBranches,
            ShowRemotes = _showRemotes,
            ShowWorktrees = _showWorktrees,
            ShowTags = _showTags,
            ShowSubmodules = _showSubmodules,
            ShowStashes = _showStashes,
            SortKey = _sortKey.ToString(),
            SortOrder = _sortOrder.ToString(),
        });

    private void CollapseAll()
    {
        foreach (TreeViewItem root in _roots)
        {
            Collapse(root);
        }

        static void Collapse(TreeViewItem node)
        {
            node.IsExpanded = false;
            foreach (TreeViewItem child in node.Items.OfType<TreeViewItem>())
            {
                Collapse(child);
            }
        }
    }

    // --- Search / filter --------------------------------------------------

    private void OnFilterChanged()
    {
        string text = (_search.Text ?? string.Empty).Trim();
        if (string.Equals(text, _filter, StringComparison.Ordinal))
        {
            return;
        }

        _filter = text;
        if (_snapshot is { } snapshot)
        {
            BuildTree(snapshot);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Escape clears the box (and so restores the whole tree); with an already
            // empty box it is left alone so it can still reach the window.
            if ((_search.Text ?? string.Empty).Length > 0)
            {
                _search.Text = string.Empty;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter)
        {
            SelectNextMatch();
            e.Handled = true;
        }
    }

    // Cycles the selection through the matching nodes, upstream's rotating-queue
    // behaviour (Enter / the magnifier button jump to the next match and wrap around).
    private void SelectNextMatch()
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _matchIndex = (_matchIndex + 1) % _matches.Count;
        TreeViewItem node = _matches[_matchIndex];

        for (TreeViewItem? parent = ParentOf(node); parent is not null; parent = ParentOf(parent))
        {
            parent.IsExpanded = true;
        }

        SelectOnly(node);
        node.BringIntoView();
        ScrollTreeToHorizontalHome();
        Dispatcher.UIThread.Post(ScrollTreeToHorizontalHome, DispatcherPriority.Background);
    }

    private TreeViewItem? ParentOf(TreeViewItem node)
        => _nodeParent.TryGetValue(node, out TreeViewItem? parent) ? parent : null;

    private bool MatchesFilter(TreeViewItem node)
        => _filter.Length > 0
           && _nodeText.TryGetValue(node, out string? text)
           && text.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    // Prunes a freshly built subtree down to the nodes matching the filter, keeping
    // every match's ancestors so the hierarchy still reads (Branches › feature/x).
    // A node that matches itself keeps its whole subtree untouched. Returns whether
    // the node survives. Upstream only tints matches and never hides anything; the
    // port filters, which is what makes the left panel usable on a big repo.
    private bool ApplyFilter(TreeViewItem node)
    {
        if (MatchesFilter(node))
        {
            return true;
        }

        List<TreeViewItem> kept = node.Items.OfType<TreeViewItem>().Where(ApplyFilter).ToList();
        if (kept.Count == 0)
        {
            return false;
        }

        node.Items.Clear();
        foreach (TreeViewItem child in kept)
        {
            node.Items.Add(child);
        }

        // Ancestors of a match are expanded, otherwise the match stays out of sight.
        node.IsExpanded = true;
        return true;
    }

    // Selects one node and makes sure it is the only selected one. A container's
    // IsSelected only reaches the TreeView's selection model once the tree has realised
    // that container under its parent; assigning it on a node inside a subtree that was
    // not laid out yet sets the flag behind the model's back, and the next real selection
    // then deselects only the node the model knows about — the stale ones keep their blue
    // fill. That is what leaves a whole chain of submodule and folder rows highlighted
    // after opening a submodule and clicking through the rows it just revealed. Every
    // selection goes through here, so the flag is cleared everywhere else first.
    private void SelectOnly(TreeViewItem node)
    {
        // Snapshot: assigning IsSelected raises SelectionChanged, and a handler that
        // rebuilds the tree would otherwise invalidate the dictionary mid-iteration.
        foreach (TreeViewItem other in _nodeParent.Keys.ToArray())
        {
            if (!ReferenceEquals(other, node) && other.IsSelected)
            {
                other.IsSelected = false;
            }
        }

        node.IsSelected = true;
    }

    // Records the parent links and the breadth-first match list for the visible tree.
    private void IndexNodes(IReadOnlyList<TreeViewItem> roots)
    {
        _nodeParent.Clear();
        _matches.Clear();
        _matchIndex = -1;

        Queue<(TreeViewItem Node, TreeViewItem? Parent)> queue = new();
        foreach (TreeViewItem root in roots)
        {
            queue.Enqueue((root, null));
        }

        while (queue.Count > 0)
        {
            (TreeViewItem node, TreeViewItem? parent) = queue.Dequeue();
            _nodeParent[node] = parent;
            if (MatchesFilter(node))
            {
                _matches.Add(node);
            }

            foreach (TreeViewItem child in node.Items.OfType<TreeViewItem>())
            {
                queue.Enqueue((child, node));
            }
        }
    }

    // A language switch re-labels the tree from the snapshot already in memory —
    // no git, no I/O, so this is safe to do straight on the UI thread once posted.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_snapshot is { } snapshot)
        {
            BuildTree(snapshot);
        }
    });

    /// <summary>
    ///  Points the tree at <paramref name="repoPath"/> and loads its objects.
    /// </summary>
    public void LoadRepository(string repoPath)
        => LoadRepository(repoPath, navigationSnapshot: null);

    /// <summary>
    /// Points the tree at a repository while sharing the host's prefetched navigation
    /// snapshot with toolbar and super-project navigation.
    /// </summary>
    public void LoadRepository(string repoPath, Task<RepositoryNavigationSnapshot>? navigationSnapshot)
    {
        bool changed = !IsSameRepositoryPath(_repoPath, repoPath);
        if (changed)
        {
            _horizontalHomeRepository = NormalizeRepositoryPath(repoPath);
        }

        _repoPath = repoPath;
        _navigationSnapshotTask = navigationSnapshot;
        Refresh();
    }

    /// <summary>
    ///  Reloads all categories for the current repository.
    /// </summary>
    public void Refresh()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _tree.ItemsSource = null;
            return;
        }

        // Move the current-branch marker NOW, from HEAD on disk. The rebuild below is
        // ~1.4 s of sequential git on a large repository, and until M-fast-mark the bold
        // marker only moved when it finished, so a checkout left the old branch bold for
        // a visible moment. This is a re-label of nodes that already exist, not a fake:
        // BuildTree reconciles against the same HEAD when the reload lands, so the two
        // cannot disagree. Deliberately ahead of the reload guard — a reload that is
        // coalesced away because one is already in flight must still show the right marker.
        ApplyCurrentBranchMarker();

        // Any pass already running was started against an older repository state.
        _refreshEpoch++;

        if (_refreshing && IsSameRepositoryPath(_refreshingRepository, repo))
        {
            // Do not start a second concurrent set of git processes; the pass in flight
            // will notice it has been superseded and re-run with the current state.
            _refreshQueued = true;
            return;
        }

        _refreshing = true;
        _refreshingRepository = repo;
        StartRefresh(repo, _refreshEpoch);
    }

    // One background reload pass. Only the pass whose epoch is still current is allowed
    // to paint; a superseded one is thrown away and immediately replaced, so overlapping
    // refreshes can neither interleave inside BuildTree (it only ever runs on the UI
    // thread) nor leave the older result on screen.
    private void StartRefresh(string repo, int epoch)
    {
        Task<RepositoryNavigationSnapshot>? navigationForPass = _navigationSnapshotTask;
        _ = Task.Run(async () =>
        {
            RepoSnapshot? snapshot = null;
            string? error = null;
            try
            {
                // A bare repository has no work tree, so `git stash list` and
                // `git submodule status` both refuse to run ("this operation must be
                // run in a work tree"). Asking anyway threw the whole panel away and
                // replaced it with git's raw error; upstream simply has nothing to
                // show in those two categories on a bare repo (FormBrowse greys out
                // every work-tree command for the same reason, FormBrowse.cs:1014-1034).
                // Refs, remotes, tags and worktrees are all perfectly readable on a
                // bare repo and keep being listed.
                bool bare = _repositoryStateService.IsBareRepository(repo);

                // The four listings are independent read-only git invocations against the
                // same repository and were run one after the other, which made a reload
                // cost their SUM (~1.4 s here, of which `git submodule status` alone is
                // ~1.0-1.3 s). Run concurrently it costs the slowest one instead. Nothing
                // in BuildTree depends on the ORDER the four ran in — it only reads the
                // finished lists out of the snapshot, and each service builds its own
                // GitModule per call, so there is no shared state between them.
                Task<BranchTagListing> refs = Task.Run(() => _branchTagService.LoadRefs(repo));
                Task<IReadOnlyList<StashRow>> stashes = bare
                    ? Task.FromResult<IReadOnlyList<StashRow>>([])
                    : Task.Run(() => _stashService.ListStashes(repo));
                Task<RepositoryNavigationSnapshot> navigation = bare
                    ? Task.FromResult(new RepositoryNavigationSnapshot(
                        repo,
                        new SubmoduleHierarchy(repo, repo, null, []),
                        []))
                    : navigationForPass ?? Task.Run(() => new RepositoryNavigationSnapshot(
                        repo,
                        _submoduleService.DiscoverHierarchy(repo),
                        _worktreeService.ListWorktrees(repo)));

                await Task.WhenAll(refs, stashes, navigation).ConfigureAwait(false);
                RepositoryNavigationSnapshot navigationSnapshot = await navigation.ConfigureAwait(false);
                snapshot = new RepoSnapshot(
                    await refs.ConfigureAwait(false),
                    await stashes.ConfigureAwait(false),
                    navigationSnapshot.Submodules,
                    navigationSnapshot.Worktrees);
            }
            catch (Exception ex)
            {
                // await rethrows the first inner exception, so this is the real git message.
                error = ex.Message;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (epoch != _refreshEpoch)
                {
                    // A repository switch starts its own pass immediately. Only a
                    // queued refresh for THIS same repository waits for this pass.
                    if (_refreshQueued
                        && IsSameRepositoryPath(repo, _repoPath)
                        && IsSameRepositoryPath(repo, _refreshingRepository))
                    {
                        _refreshQueued = false;
                        if (_repoPath is { Length: > 0 } current)
                        {
                            StartRefresh(current, _refreshEpoch);
                        }
                    }

                    return;
                }

                _refreshing = false;
                _refreshingRepository = null;

                if (_refreshQueued)
                {
                    // Superseded: this snapshot describes a state the repository has
                    // already left. Drop it (the synchronous HEAD-derived marker keeps the
                    // visible tree honest meanwhile) and reload against the current state.
                    _refreshQueued = false;
                    if (_repoPath is { Length: > 0 } current)
                    {
                        _refreshing = true;
                        _refreshingRepository = current;
                        StartRefresh(current, _refreshEpoch);
                    }

                    // If there is no repository any more, Refresh() has already emptied the
                    // tree; either way this stale snapshot is never painted.
                    return;
                }

                if (snapshot is not null)
                {
                    BuildTree(snapshot);
                }
                else
                {
                    _tree.ItemsSource = new[]
                    {
                        Category(string.Format(CultureInfo.CurrentCulture, "{0}: {1}", T("TranslatedStrings/_error.Text", "Error"), error), null, null, "error"),
                    };
                }
            });
        });
    }

    private void BuildTree(RepoSnapshot snapshot)
    {
        _snapshot = snapshot;

        // Everything below throws the live TreeViewItems away, so record what has to
        // survive first (upstream Tree.cs:162 snapshots GetExpandedNodesState() at the
        // same point, before Nodes.FillTreeViewNode repopulates).
        HarvestState();

        _nodeText.Clear();
        _nodeKey.Clear();

        List<BranchTagRow> local = [];
        List<BranchTagRow> remote = [];
        foreach (BranchTagRow row in snapshot.Refs.Branches)
        {
            (row.IsRemote ? remote : local).Add(row);
        }

        IReadOnlyList<BranchTagRow> tags = snapshot.Refs.Tags;
        IReadOnlyList<StashRow> stashes = snapshot.Stashes;
        SubmoduleHierarchy submodules = snapshot.Submodules;
        IReadOnlyList<WorktreeRow> worktrees = snapshot.Worktrees;

        // Built by id, emitted in _categoryOrder further down (see CategoryOrder).
        Dictionary<string, TreeViewItem> categories = new(StringComparer.Ordinal);

        // Branches (local), as a folder hierarchy: "feature/a" hangs off a collapsible
        // "feature" node, exactly like upstream's BranchPathNode chain.
        TreeViewItem branchesNode = Category(T("RepoObjectsTree/tsbShowBranches.ToolTipText", "Branches"), "Branch", local.Count, "branches");
        branchesNode.ContextMenu = RefSortMenu(BranchesRootItems());
        List<BranchTagRow> orderedLocal = OrderLocalBranches(local).ToList();
        AddRefsWithFolders(
            branchesNode,
            "branches",
            orderedLocal,
            static r => r.Name,
            (row, name) =>
            {
                TreeViewItem leaf = Leaf(row.IsCurrent ? $"✓ {name}" : name, "BranchLocal", row, row.IsCurrent, "branches/" + row.Name);
                leaf.ContextMenu = BranchMenu(row);
                return leaf;
            },
            folderMenu: path => BranchFolderMenu(path, orderedLocal));
        categories["branches"] = branchesNode;

        // Remotes: one group node per remote, and inside it the same folder hierarchy —
        // upstream builds path nodes below a remote too, so "origin/feature/x" is the
        // leaf "x" under "feature" under "origin", not a flat "feature/x".
        // The root count is the number of *remotes*, not of remote branches: a single
        // "origin" holding four branches has to read "Remotes (1)" with "origin (4)"
        // below it. Every other category counts its own kind of item already
        // (branches, tags, stashes, submodules, worktrees), so only this one was off.
        List<IGrouping<string, BranchTagRow>> remoteGroups = remote
            .GroupBy(RemoteName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        TreeViewItem remotesNode = Category(T("RepoObjectsTree/tsbShowRemotes.ToolTipText", "Remotes"), "Remotes", remoteGroups.Count, "remotes");
        remotesNode.ContextMenu = RemotesRootMenu();
        foreach (IGrouping<string, BranchTagRow> group in remoteGroups)
        {
            string groupKey = "remotes/" + group.Key;
            TreeViewItem groupNode = Category(group.Key, "Remote", group.Count(), groupKey);
            groupNode.ContextMenu = RemoteGroupMenu(group.Key);
            AddRefsWithFolders(
                groupNode,
                groupKey,
                SortRefs(group),
                row => ShortRemoteName(row.Name, group.Key),
                (row, name) =>
                {
                    TreeViewItem leaf = Leaf(name, "BranchRemote", row, isCurrent: false, "remotes/" + row.Name);
                    leaf.ContextMenu = BranchMenu(row);
                    return leaf;
                },
                folderMenu: null);

            remotesNode.Items.Add(groupNode);
        }

        categories["remotes"] = remotesNode;

        // Tags, likewise foldered: upstream gives a tag whose name contains a '/' a
        // BasePathNode parent, so "rel/1.2.0" is "1.2.0" under "rel".
        TreeViewItem tagsNode = Category(T("RepoObjectsTree/tsbShowTags.ToolTipText", "Tags"), "Tag", tags.Count, "tags");
        tagsNode.ContextMenu = RefSortMenu(TagsRootItems());
        AddRefsWithFolders(
            tagsNode,
            "tags",
            SortRefs(tags),
            static r => r.Name,
            (row, name) =>
            {
                TreeViewItem leaf = Leaf(name, "Tag", row, isCurrent: false, "tags/" + row.Name);
                leaf.ContextMenu = TagMenu(row);
                return leaf;
            },
            folderMenu: null);
        categories["tags"] = tagsNode;

        // Stashes. The root node carries Stash / Stash staged / Manage stashes…
        // (upstream's mnubtnStashAllFromRootNode & co.), each leaf Open / Apply / Pop / Drop.
        TreeViewItem stashesNode = Category(T("RepoObjectsTree/tsbShowStashes.ToolTipText", "Stashes"), "stash", stashes.Count, "stashes");
        stashesNode.ContextMenu = StashRootMenu();
        foreach (StashRow row in stashes)
        {
            TreeViewItem leaf = Leaf($"{row.Name}: {row.Message}", "stash", row, isCurrent: false, "stashes/" + row.Name);
            leaf.ContextMenu = StashMenu(row);
            stashesNode.Items.Add(leaf);
        }

        categories["stashes"] = stashesNode;

        // Submodules. The root node carries "Update all"; each leaf carries
        // "Open" (open the submodule as the active repository, via
        // OpenRepositoryRequested) plus "Update" for its own path.
        int submoduleCount = Math.Max(0, submodules.Nodes.Count - 1);
        TreeViewItem submodulesNode = Category(T("RepoObjectsTree/tsbShowSubmodules.ToolTipText", "Submodules"), "SubmodulesManage", submoduleCount, "submodules");
        submodulesNode.ContextMenu = SubmoduleRootMenu();
        AddSubmodulesWithFolders(submodulesNode, submodules);

        categories["submodules"] = submodulesNode;

        // Worktrees. The root node carries "Add…" and "Prune"; each leaf carries
        // "Open" (open the worktree as the active repository, via
        // OpenRepositoryRequested) plus "Remove" for its own path.
        TreeViewItem worktreesNode = Category(T("RepoObjectsTree/tsbShowWorktrees.ToolTipText", "Worktrees"), "WorkTree", worktrees.Count, "worktrees");
        worktreesNode.ContextMenu = WorktreeRootMenu();
        foreach (WorktreeRow row in worktrees)
        {
            // The worktree the app currently has open is the "current" one: upstream
            // marks it bold and refuses to open or delete it; a stale entry (its folder
            // deleted by hand, so `git worktree prune` would drop it) is greyed out.
            bool isCurrent = row.IsSamePath(_repoPath);
            TreeViewItem leaf = Leaf(row.Display, "WorkTree", row, isCurrent, "worktrees/" + row.Path);
            if (row.IsPrunable)
            {
                leaf.Foreground = Brush("App.TextDim", Brushes.Gray);
            }

            ToolTip.SetTip(leaf, WorktreeTooltip(row, isCurrent));
            leaf.ContextMenu = WorktreeMenu(row, isCurrent);
            worktreesNode.Items.Add(leaf);
        }

        categories["worktrees"] = worktreesNode;

        List<TreeViewItem> roots = [];
        foreach (string id in _categoryOrder)
        {
            if (IsCategoryShown(id) && categories.TryGetValue(id, out TreeViewItem? node))
            {
                roots.Add(node);
            }
        }

        if (_filter.Length > 0)
        {
            // Filtered: only the matches and their ancestors survive, expanded.
            roots = roots.Where(ApplyFilter).ToList();
        }
        else if (_firstBuild)
        {
            // Nothing to restore yet — open Branches, as upstream's
            // LocalBranchTree.PostFillTreeViewNode does on its first fill.
            branchesNode.IsExpanded = true;
        }

        // Expand / Collapse and Move Up / Move Down are appended once the tree is
        // complete: the first pair is only shown for a node that turned out to have
        // children, the second only for a category root and only towards a sibling that
        // exists — both gates need the finished shape.
        AddStructuralMenuItems(roots);

        _roots = roots;
        IndexNodes(roots);
        _tree.ItemsSource = roots;

        RestoreState(roots);
        ScheduleHorizontalHomeAfterRepositoryChange();
        _firstBuild = false;

        // Final reconciliation against the real HEAD. In the normal case this is a
        // no-op — the IsCurrent flags above come from the same refs listing — but a
        // rebuild that was already in flight when HEAD moved would otherwise land with
        // a stale marker and silently undo the fast path in Refresh().
        ApplyCurrentBranchMarker();
    }

    /// <summary>
    ///  Reads the checked-out branch straight from <c>&lt;git dir&gt;/HEAD</c>, the way
    ///  upstream's <c>GetSelectedBranchFast</c> does (Commands.Execution.cs:100-130), and
    ///  re-applies the bold/✓ current-branch marker to the local-branch leaves already in
    ///  the tree. Costs one small file read instead of the ~1.4 s a full <see cref="Refresh"/>
    ///  needs (five sequential git invocations, of which <c>git submodule status</c> alone
    ///  is ~1 s on a large repository), so the marker moves as soon as a ref operation
    ///  finishes and the full reload lands afterwards without visibly changing anything.
    ///  <para>Returns without touching anything when HEAD cannot be read — the marker
    ///  always reflects what is really on disk, never the requested branch name. A detached
    ///  HEAD reads as "no branch" and correctly leaves every branch unbold.</para>
    /// </summary>
    private void ApplyCurrentBranchMarker()
    {
        if (_repoPath is not { Length: > 0 } repo || ReadHeadBranch(repo) is not { } head)
        {
            return;
        }

        List<BranchTagRow> changed = [];

        foreach ((TreeViewItem item, string key) in _nodeKey.ToList())
        {
            if (item.Tag is not BranchTagRow row || row.IsRemote || row.IsTag
                || !key.StartsWith("branches/", StringComparison.Ordinal))
            {
                continue;
            }

            bool isCurrent = head.Length > 0 && string.Equals(row.Name, head, StringComparison.Ordinal);
            if (isCurrent == row.IsCurrent)
            {
                continue;
            }

            BranchTagRow updated = row with { IsCurrent = isCurrent };
            item.Tag = updated;

            // The menu gates Checkout/Merge/Rebase/Reset/Delete on IsCurrent
            // (BranchMenu), so it has to be regenerated with the new flag.
            item.ContextMenu = BranchMenu(updated);
            SetLeafCurrent(item, isCurrent);
            changed.Add(updated);
        }

        if (changed.Count == 0)
        {
            return;
        }

        // Keep the retained snapshot in step: a re-sort or a language switch rebuilds the
        // tree from it (OnLanguageChanged, RefSortMenu) and would otherwise put the stale
        // marker back.
        if (_snapshot is { } snapshot)
        {
            Dictionary<string, BranchTagRow> byName = changed.ToDictionary(static r => r.Name, StringComparer.Ordinal);
            List<BranchTagRow> branches = snapshot.Refs.Branches
                .Select(r => !r.IsRemote && !r.IsTag && byName.TryGetValue(r.Name, out BranchTagRow? u) ? u : r)
                .ToList();
            _snapshot = snapshot with { Refs = snapshot.Refs with { Branches = branches } };
        }
    }

    // Toggles the "✓ " prefix and the bold weight on an existing leaf, in place. The
    // label is the leaf's own last path segment ("x" under "feature"), so it is adjusted
    // rather than rebuilt from the full ref name.
    private static void SetLeafCurrent(TreeViewItem item, bool isCurrent)
    {
        if (item.Header is not Panel panel)
        {
            return;
        }

        const string Marker = "✓ ";
        foreach (TextBlock label in panel.Children.OfType<TextBlock>())
        {
            string text = label.Text ?? string.Empty;
            bool marked = text.StartsWith(Marker, StringComparison.Ordinal);

            label.Text = isCurrent
                ? (marked ? text : Marker + text)
                : (marked ? text[Marker.Length..] : text);
            label.FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal;
        }
    }

    // "ref: refs/heads/&lt;name&gt;" → the branch name; a raw sha (detached HEAD) → "";
    // unreadable, or a HEAD pointing outside refs/heads → null, meaning "leave the
    // marker alone and let the full reload decide".
    private static string? ReadHeadBranch(string repoPath)
    {
        try
        {
            if (RepositoryWatcherService.ResolveGitDir(repoPath) is not { } gitDir)
            {
                return null;
            }

            string headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile))
            {
                return null;
            }

            string text = File.ReadAllText(headFile).Trim();
            const string Prefix = "ref: refs/heads/";
            return text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..].Trim()
                : text.StartsWith("ref: ", StringComparison.Ordinal) ? null
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool IsCategoryShown(string id) => id switch
    {
        "branches" => _showBranches,
        "remotes" => _showRemotes,
        "worktrees" => _showWorktrees,
        "tags" => _showTags,
        "submodules" => _showSubmodules,
        "stashes" => _showStashes,
        _ => false,
    };

    /// <summary>
    ///  Inserts <paramref name="rows"/> under <paramref name="parent"/>, creating a
    ///  collapsible folder node for every '/'-separated segment of the path returned by
    ///  <paramref name="pathOf"/> — the port's equivalent of upstream's
    ///  <c>BaseRevisionNode.CreateRootNode</c> walking up <c>ParentPath</c> and creating
    ///  a <c>BranchPathNode</c> / <c>BasePathNode</c> per level (BaseRevisionNode.cs:81-105).
    ///  <para>Folders are created at the position of their first child, so with the rows
    ///  already in display order the folders end up interleaved with the plain leaves in
    ///  that same order — "docs", "feature", "main" for the branch set of the original's
    ///  screenshot. <paramref name="makeLeaf"/> receives the row and the leaf's own last
    ///  path segment, which is what gets displayed (upstream's <c>Name</c>).</para>
    /// </summary>
    private void AddRefsWithFolders(
        TreeViewItem parent,
        string parentKey,
        IReadOnlyList<BranchTagRow> rows,
        Func<BranchTagRow, string> pathOf,
        Func<BranchTagRow, string, TreeViewItem> makeLeaf,
        Func<string, ContextMenu>? folderMenu)
    {
        Dictionary<string, TreeViewItem> folders = new(StringComparer.Ordinal);

        foreach (BranchTagRow row in rows)
        {
            string path = pathOf(row);
            int slash = path.LastIndexOf('/');
            TreeViewItem host = slash < 0 ? parent : Folder(path[..slash]);
            host.Items.Add(makeLeaf(row, slash < 0 ? path : path[(slash + 1)..]));
        }

        return;

        // Creates the folder chain for a path on demand, deepest last, memoised so the
        // second "feature/…" ref reuses the "feature" node instead of adding another.
        TreeViewItem Folder(string folderPath)
        {
            if (folders.TryGetValue(folderPath, out TreeViewItem? existing))
            {
                return existing;
            }

            int slash = folderPath.LastIndexOf('/');
            TreeViewItem host = slash < 0 ? parent : Folder(folderPath[..slash]);

            TreeViewItem node = new()
            {
                Header = HeaderPanel(slash < 0 ? folderPath : folderPath[(slash + 1)..], "BranchFolder", bold: false),
                Foreground = Brush("App.Text", Brushes.Gainsboro),
            };

            // Searchable on the whole path, so the filter still finds "feature" when
            // only its leaves are typed and vice versa.
            _nodeText[node] = folderPath;
            _nodeKey[node] = parentKey + "/" + folderPath;
            node.ContextMenu = folderMenu?.Invoke(folderPath);

            folders[folderPath] = node;
            host.Items.Add(node);
            return node;
        }
    }

    /// <summary>
    ///  Inserts the submodule rows as a HIERARCHY: a submodule of a submodule hangs off
    ///  its own super-project's node, and a path segment that is only a plain directory
    ///  (<c>core</c>, <c>graphs</c>) becomes a folder node in between — the same shape as
    ///  upstream's <c>SubmoduleTree.AddTopAndNodesToTree</c>, which builds a
    ///  <c>SubmoduleFolderNode</c> for every path part that is not itself a submodule.
    ///  <para>The rows arrive sorted so that a super-project always precedes its
    ///  submodules, hence a child always finds its host node already built.</para>
    /// </summary>
    private void AddSubmodulesWithFolders(TreeViewItem parent, SubmoduleHierarchy hierarchy)
    {
        SubmoduleRow? rootRow = hierarchy.Nodes.FirstOrDefault(row => row.Path.Length == 0);
        if (rootRow is null)
        {
            return;
        }

        string rootLabel = System.IO.Path.GetFileName(hierarchy.RootPath.TrimEnd('/', '\\'));
        TreeViewItem root = SubmoduleLeaf(rootRow, rootLabel, "submodules/root");
        parent.Items.Add(root);

        Dictionary<string, TreeViewItem> repositories = new(PathComparer)
        {
            [hierarchy.RootPath] = root,
        };
        Dictionary<string, TreeViewItem> folders = new(PathComparer);
        Dictionary<TreeViewItem, TreeViewItem> hierarchyParents = new()
        {
            [root] = parent,
        };

        foreach (SubmoduleRow row in hierarchy.Nodes.Where(row => row.Path.Length > 0))
        {
            if (!repositories.TryGetValue(row.ParentRepositoryPath, out TreeViewItem? host))
            {
                host = root;
            }

            string[] parts = row.PathInParent.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string folderPath = row.ParentRepositoryPath;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                folderPath = System.IO.Path.Combine(folderPath, parts[i]);
                if (!folders.TryGetValue(folderPath, out TreeViewItem? folder))
                {
                    folder = new TreeViewItem
                    {
                        Header = HeaderPanel(parts[i], "FolderClosed", bold: false),
                        Foreground = Brush("App.Text", Brushes.Gainsboro),
                    };
                    _nodeText[folder] = row.Path;
                    _nodeKey[folder] = "submodules/folder/" + folderPath;
                    folders[folderPath] = folder;
                    host.Items.Add(folder);
                    hierarchyParents[folder] = host;
                }

                host = folder;
            }

            TreeViewItem leaf = SubmoduleLeaf(row, row.Name, "submodules/" + row.AbsolutePath);
            host.Items.Add(leaf);
            hierarchyParents[leaf] = host;
            repositories[row.AbsolutePath] = leaf;
        }

        bool activeRepositoryChanged = !PathComparer.Equals(_expandedSubmoduleCurrentPath, hierarchy.CurrentPath);
        _expandedSubmoduleCurrentPath = hierarchy.CurrentPath;
        if (activeRepositoryChanged)
        {
            TreeViewItem? chainNode = hierarchy.Nodes.FirstOrDefault(row => row.IsCurrent) is { } currentRow
                && repositories.TryGetValue(currentRow.AbsolutePath, out TreeViewItem? currentNode)
                    ? currentNode
                    : null;
            while (chainNode is not null)
            {
                chainNode.IsExpanded = true;
                chainNode = hierarchyParents.TryGetValue(chainNode, out TreeViewItem? chainParent)
                    ? chainParent
                    : null;
            }
        }

        TreeViewItem SubmoduleLeaf(SubmoduleRow row, string name, string key)
        {
            string label = name;
            if (row.Status != SubmoduleState.NotInitialized)
            {
                label += row.Branch.Length > 0 ? $" ({row.Branch})" : $" ({T("no branch")})";
            }

            label = row.Status switch
            {
                SubmoduleState.NotInitialized => TF("{0} (not initialized)", label),
                SubmoduleState.OutOfDate => TF("{0} (out of date)", label),
                _ when !row.Exists => TF("{0} (missing)", label),
                _ => label,
            };
            if (row.IsCurrent)
            {
                label = "▶ " + label;
            }

            TreeViewItem leaf = Leaf(label, "FolderSubmodule", row, row.IsCurrent, key);
            if (row.IsCurrent)
            {
                leaf.Foreground = Brush("App.Accent", Brushes.DodgerBlue);
            }
            else if (!row.Exists)
            {
                leaf.Foreground = Brush("App.TextDim", Brushes.Gray);
            }

            leaf.ContextMenu = SubmoduleMenu(row);
            ToolTip.SetTip(leaf, row.ShortSha.Length > 0 ? $"{row.AbsolutePath} @ {row.ShortSha}" : row.AbsolutePath);
            _nodeText[leaf] = row.Path.Length > 0 ? row.Path : hierarchy.RootPath;
            return leaf;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    // --- Expand / selection state across a rebuild (R1) --------------------

    // Records which nodes are open and which one is selected, from the tree that is
    // about to be discarded.
    private void HarvestState()
    {
        if (_tree.SelectedItem is TreeViewItem selected && _nodeKey.TryGetValue(selected, out string? selectedKey))
        {
            _selectedKey = selectedKey;
        }

        // While a filter is active ApplyFilter force-expands every ancestor of a match,
        // so harvesting then would record expansion the user never asked for and make
        // clearing the box leave the tree wide open. The last unfiltered snapshot is
        // kept instead.
        if (_filter.Length > 0 || _roots.Count == 0)
        {
            return;
        }

        HashSet<string> expanded = new(StringComparer.Ordinal);
        Walk(_roots);
        _expandedKeys = expanded;

        void Walk(IEnumerable<TreeViewItem> nodes)
        {
            foreach (TreeViewItem node in nodes)
            {
                if (node.IsExpanded && _nodeKey.TryGetValue(node, out string? key))
                {
                    expanded.Add(key);
                }

                Walk(node.Items.OfType<TreeViewItem>());
            }
        }
    }

    // Re-opens the nodes that were open and re-selects the node that was selected,
    // scrolling it into view — upstream's RestoreExpandedNodesState + the selection
    // restore + ExpandPathToSelectedNode/EnsureVerticallyVisible (Tree.cs:168-201).
    // A node that no longer exists (its branch was deleted, its remote removed) simply
    // has no match, so nothing is restored for it and its key is dropped on the next
    // harvest.
    private void RestoreState(IReadOnlyList<TreeViewItem> roots)
    {
        TreeViewItem? selection = null;

        Walk(roots);

        if (selection is null)
        {
            return;
        }

        // Opening the ancestors is what makes the restored selection actually visible;
        // it is deliberately not recorded as user expansion — the next harvest will pick
        // it up only because it really is open now, which is also upstream's behaviour.
        for (TreeViewItem? parent = ParentOf(selection); parent is not null; parent = ParentOf(parent))
        {
            parent.IsExpanded = true;
        }

        _suppressSelectionNotify = true;
        SelectOnly(selection);
        TreeViewItem target = selection;

        // The item has no realised layout yet on the pass that assigned ItemsSource, so
        // BringIntoView has to wait for it (Loaded runs after the layout pass).
        Dispatcher.UIThread.Post(
            () =>
            {
                try
                {
                    target.BringIntoView();
                }
                catch
                {
                    // A tree rebuilt again in the meantime: nothing to scroll to.
                }

                _suppressSelectionNotify = false;
            },
            DispatcherPriority.Loaded);

        void Walk(IEnumerable<TreeViewItem> nodes)
        {
            foreach (TreeViewItem node in nodes)
            {
                if (_nodeKey.TryGetValue(node, out string? key))
                {
                    if (_expandedKeys.Contains(key))
                    {
                        node.IsExpanded = true;
                    }

                    if (selection is null && key == _selectedKey)
                    {
                        selection = node;
                    }
                }

                Walk(node.Items.OfType<TreeViewItem>());
            }
        }
    }

    private void ScheduleHorizontalHomeAfterRepositoryChange()
    {
        if (_horizontalHomeRepository is not { } repository
            || !IsSameRepositoryPath(repository, _repoPath))
        {
            return;
        }

        int epoch = _refreshEpoch;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (epoch != _refreshEpoch
                    || !IsSameRepositoryPath(repository, _repoPath)
                    || !PathComparer.Equals(repository, _horizontalHomeRepository))
                {
                    return;
                }

                // RestoreState queued its BringIntoView at Loaded before this callback.
                // Apply once after it and once more after the remaining layout work.
                ScrollTreeToHorizontalHome();
                Dispatcher.UIThread.Post(() =>
                {
                    if (epoch == _refreshEpoch
                        && IsSameRepositoryPath(repository, _repoPath)
                        && PathComparer.Equals(repository, _horizontalHomeRepository))
                    {
                        ScrollTreeToHorizontalHome();
                        _horizontalHomeRepository = null;
                    }
                }, DispatcherPriority.Background);
            },
            DispatcherPriority.Loaded);
    }

    private void ScrollTreeToHorizontalHome()
    {
        // The TreeView owns one template ScrollViewer. Resolve lazily because its
        // template is not realised in the constructor, then retain it for subsequent
        // searches/rebuilds rather than walking the visual tree on every interaction.
        if (_treeScrollViewer?.GetVisualRoot() != _tree.GetVisualRoot())
        {
            _treeScrollViewer = _tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        }

        if (_treeScrollViewer is { } scroll)
        {
            scroll.Offset = HorizontalHomeOffset(scroll.Offset);
        }
    }

    internal static Vector HorizontalHomeOffset(Vector offset) => new(0, offset.Y);

    private static bool IsSameRepositoryPath(string? left, string? right)
        => left is null || right is null
            ? left == right
            : PathComparer.Equals(NormalizeRepositoryPath(left), NormalizeRepositoryPath(right));

    private static string NormalizeRepositoryPath(string path)
    {
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch
        {
            return System.IO.Path.TrimEndingDirectorySeparator(path);
        }
    }

    // --- Expand / Collapse and Move Up / Move Down (appended post-build) ---

    // Upstream shows Expand / Collapse only for a node that has children, with Expand
    // enabled while it is closed and Collapse while it is open
    // (ContextActions.cs:53-59), and Move Up / Move Down only for a category root, each
    // enabled only when a sibling exists in that direction (:61-68). Both pairs are
    // therefore appended here, with the finished tree in hand.
    private void AddStructuralMenuItems(IReadOnlyList<TreeViewItem> roots)
    {
        for (int i = 0; i < roots.Count; i++)
        {
            TreeViewItem root = roots[i];
            ContextMenu menu = EnsureMenu(root);
            menu.Items.Add(new Separator());

            MenuItem up = MenuItem(T("RepoObjectsTree/mnubtnMoveUp.Text", "Move Up"), "ArrowUp", () => MoveCategory(root, up: true));
            up.IsEnabled = i > 0;
            ToolTip.SetTip(up, T("RepoObjectsTree/mnubtnMoveUp.ToolTipText", "Move node up"));
            menu.Items.Add(up);

            MenuItem down = MenuItem(T("RepoObjectsTree/mnubtnMoveDown.Text", "Move Down"), "ArrowDown", () => MoveCategory(root, up: false));
            down.IsEnabled = i < roots.Count - 1;
            ToolTip.SetTip(down, T("RepoObjectsTree/mnubtnMoveDown.ToolTipText", "Move node down"));
            menu.Items.Add(down);
        }

        AddExpandCollapse(roots);

        void AddExpandCollapse(IEnumerable<TreeViewItem> nodes)
        {
            foreach (TreeViewItem node in nodes)
            {
                List<TreeViewItem> children = node.Items.OfType<TreeViewItem>().ToList();
                if (children.Count > 0)
                {
                    ContextMenu menu = EnsureMenu(node);
                    menu.Items.Add(new Separator());

                    // Expand is upstream's ExpandAll: the whole subtree, not one level.
                    MenuItem expand = MenuItem(T("RepoObjectsTree/mnubtnExpand.Text", "Expand"), "ExpandAll", () => ExpandAll(node));
                    ToolTip.SetTip(expand, T("RepoObjectsTree/mnubtnExpand.ToolTipText", "Expand all subnodes"));
                    menu.Items.Add(expand);

                    MenuItem collapse = MenuItem(T("RepoObjectsTree/mnubtnCollapse.Text", "Collapse"), "CollapseAll", () => CollapseSubtree(node));
                    ToolTip.SetTip(collapse, T("RepoObjectsTree/mnubtnCollapse.ToolTipText", "Collapse all subnodes"));
                    menu.Items.Add(collapse);

                    // Enabled-ness depends on whether the node is open, which changes
                    // between builds, so it is decided as the menu opens rather than now.
                    menu.Opening += (_, _) =>
                    {
                        expand.IsEnabled = !node.IsExpanded;
                        collapse.IsEnabled = node.IsExpanded;
                    };
                }

                AddExpandCollapse(children);
            }
        }

        static ContextMenu EnsureMenu(TreeViewItem node)
            => (ContextMenu)(node.ContextMenu ??= new ContextMenu());
    }

    private static void ExpandAll(TreeViewItem node)
    {
        node.IsExpanded = true;
        foreach (TreeViewItem child in node.Items.OfType<TreeViewItem>())
        {
            ExpandAll(child);
        }
    }

    private static void CollapseSubtree(TreeViewItem node)
    {
        node.IsExpanded = false;
        foreach (TreeViewItem child in node.Items.OfType<TreeViewItem>())
        {
            CollapseSubtree(child);
        }
    }

    // Moves a category root one place up or down and rebuilds. The order is session
    // state here; the host persists it through CategoryOrder / CategoryOrderChanged.
    private void MoveCategory(TreeViewItem root, bool up)
    {
        if (!_nodeKey.TryGetValue(root, out string? id) || _snapshot is not { } snapshot)
        {
            return;
        }

        int index = _categoryOrder.IndexOf(id);
        if (index < 0)
        {
            return;
        }

        // Neighbour in the ORDER, skipping the categories currently hidden by the
        // toolbar toggles: swapping with a category that is not on screen would look
        // like nothing happened.
        int target = index;
        do
        {
            target += up ? -1 : 1;
        }
        while (target >= 0 && target < _categoryOrder.Count && !IsCategoryShown(_categoryOrder[target]));

        if (target < 0 || target >= _categoryOrder.Count)
        {
            return;
        }

        (_categoryOrder[index], _categoryOrder[target]) = (_categoryOrder[target], _categoryOrder[index]);
        CategoryOrderChanged?.Invoke();
        BuildTree(snapshot);
    }

    private static string RemoteName(BranchTagRow row)
    {
        int slash = row.Name.IndexOf('/');
        return slash > 0 ? row.Name[..slash] : "remote";
    }

    private static string ShortRemoteName(string name, string remote)
        => name.StartsWith(remote + "/", StringComparison.Ordinal) ? name[(remote.Length + 1)..] : name;

    // --- Node construction ------------------------------------------------

    private TreeViewItem Category(string text, string? icon, int? count, string key)
    {
        string header = count is { } c ? string.Format(CultureInfo.CurrentCulture, CategoryCountFormat, text, c) : text;
        TreeViewItem item = new()
        {
            Header = HeaderPanel(header, icon, bold: true),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        // Searchable on the bare name: the count suffix is chrome, and matching it
        // would let a digit in the filter hit every category at once.
        _nodeText[item] = text;

        // The count is chrome for the key too — it changes on every operation, and a key
        // that changed with it would lose the node's expand state on every refresh.
        _nodeKey[item] = key;
        return item;
    }

    private TreeViewItem Leaf(string text, string? icon, object tag, bool isCurrent, string key)
    {
        TreeViewItem item = new()
        {
            Header = HeaderPanel(text, icon, bold: isCurrent),
            Tag = tag,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        _nodeKey[item] = key;

        // Refs match on their full name (origin/feature/x), like upstream matching
        // BaseRevisionNode.FullPath, so a filter still finds a leaf whose displayed
        // label was shortened by its remote group.
        _nodeText[item] = tag is BranchTagRow row ? row.Name : text;
        return item;
    }

    private static Control HeaderPanel(string text, string? icon, bool bold)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (icon is not null && IconLoader.Image(icon) is { } img)
        {
            img.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(img);
        }

        panel.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        });

        return panel;
    }

    // --- Context menus ----------------------------------------------------

    // Upstream generates a ref's menu from the five IGitRefActions slots in a fixed
    // order — Checkout, Merge, Rebase, Create branch, Reset — then Rename and Delete
    // (MenuItemKey + MenuItemsGenerator.Generate). Local branches, remote branches and
    // tags differ only in the wording of those slots and in which of them apply, so all
    // three menus are built here from the same sequence.
    private ContextMenu BranchMenu(BranchTagRow row)
    {
        ContextMenu menu = new();

        MenuItem checkout = row.IsRemote
            ? MenuItem(T("RemoteBranchMenuItemsStrings/Checkout.Text", "Checkout remote branch…"), "BranchCheckout", () => DoCheckout(row))
            : MenuItem(T("BranchMenuItemsStrings/Checkout.Text", "Checkout branch…"), "BranchCheckout", () => DoCheckout(row));
        menu.Items.Add(checkout);

        MenuItem merge = MenuItem(T("MenuItemsStrings/Merge.Text", "Merge into current branch…"), "Merge", () => _ = DoMergeAsync(row.Name));
        menu.Items.Add(merge);

        MenuItem rebase = MenuItem(
            row.IsRemote
                ? T("RemoteBranchMenuItemsStrings/Rebase.Text", "Rebase current branch on this remote branch…")
                : T("BranchMenuItemsStrings/Rebase.Text", "Rebase current branch on this branch…"),
            "Rebase",
            () => _ = DoRebaseAsync(row.Name));
        menu.Items.Add(rebase);

        // "Create branch" from this ref — upstream's GitRefCreateBranch slot, which
        // opens FormCreateBranch with the ref as the start point.
        MenuItem create = MenuItem(T("MenuItemsStrings/CreateBranch.Text", "Create branch…"), "BranchCreate", () => _ = DoCreateBranchAsync(row.Name, prefix: string.Empty));
        menu.Items.Add(create);

        MenuItem reset = ResetCurrentBranchItem(row.Name);
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Name)));
        menu.Items.Add(new Separator());

        if (row.IsRemote)
        {
            menu.Items.Add(MenuItem(T("RemoteBranchMenuItemsStrings/Delete.Text", "Delete remote branch…"), "BranchDelete", () => _ = DoDeleteRemoteBranchAsync(row)));
            return menu;
        }

        MenuItem rename = MenuItem(T("MenuItemsStrings/Rename.Text", "Rename branch…"), "Renamed", () => _ = DoRenameBranchAsync(row));
        menu.Items.Add(rename);
        MenuItem delete = MenuItem(T("BranchMenuItemsStrings/Delete.Text", "Delete branch…"), "BranchDelete", () => _ = DoDeleteBranchAsync(row));
        menu.Items.Add(delete);

        // The checked-out branch: upstream leaves every item VISIBLE but enables only
        // Create branch and Rename (LocalBranchMenuItems.CurrentBranchItemKeys, applied
        // in ContextActions.cs:252-266). Checking out, merging or rebasing the branch you
        // are already on is a no-op or an error, and it cannot be deleted at all — the
        // port used to offer all four.
        if (row.IsCurrent)
        {
            checkout.IsEnabled = false;
            merge.IsEnabled = false;
            rebase.IsEnabled = false;
            reset.IsEnabled = false;
            delete.IsEnabled = false;
        }

        return menu;
    }

    private ContextMenu TagMenu(BranchTagRow row)
    {
        ContextMenu menu = new();

        // Checkout the tag ref: a plain `git checkout <tag>` lands on a detached
        // HEAD, which is the expected "checkout tag revision" behaviour. Reuses
        // the same BranchTagService.Checkout used for branches/revisions.
        menu.Items.Add(MenuItem(T("TagMenuItemsStrings/Checkout.Text", "Checkout tag revision…"), "BranchCheckout", () => DoCheckout(row)));
        menu.Items.Add(MenuItem(T("MenuItemsStrings/Merge.Text", "Merge into current branch…"), "Merge", () => _ = DoMergeAsync(row.Name)));
        menu.Items.Add(MenuItem(T("TagMenuItemsStrings/Rebase.Text", "Rebase current branch on this tag revision…"), "Rebase", () => _ = DoRebaseAsync(row.Name)));
        menu.Items.Add(MenuItem(T("MenuItemsStrings/CreateBranch.Text", "Create branch…"), "BranchCreate", () => _ = DoCreateBranchAsync(row.Name, prefix: string.Empty)));
        menu.Items.Add(ResetCurrentBranchItem(row.Name));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("TagMenuItemsStrings/Delete.Text", "Delete tag…"), "TagDelete", () => _ = DoDeleteTagAsync(row)));
        return menu;
    }

    // A folder node below Branches — upstream's BranchPathNode, whose menu is exactly
    // these two items (Designer.cs, mnubtnCreateBranch + mnubtnDeleteAllBranches):
    // create a branch with the folder as name prefix, and delete every branch under it.
    private ContextMenu BranchFolderMenu(string folderPath, IReadOnlyList<BranchTagRow> localBranches)
    {
        ContextMenu menu = new();

        MenuItem create = MenuItem(T("RepoObjectsTree/mnubtnCreateBranch.Text", "Create Branch…"), "BranchCreate", () => _ = DoCreateBranchAsync("HEAD", prefix: folderPath + "/"));
        ToolTip.SetTip(create, T("RepoObjectsTree/mnubtnCreateBranch.ToolTipText", "Create a local branch"));
        menu.Items.Add(create);

        MenuItem deleteAll = MenuItem(T("RepoObjectsTree/mnubtnDeleteAllBranches.Text", "Delete All"), "BranchDelete", () => _ = DoDeleteAllBranchesAsync(folderPath, localBranches));
        ToolTip.SetTip(deleteAll, T("RepoObjectsTree/mnubtnDeleteAllBranches.ToolTipText", "Delete all child branches, which must all be fully merged in its upstream branch or in HEAD"));
        menu.Items.Add(deleteAll);

        return menu;
    }

    // Upstream's GitRefReset slot opens FormResetCurrentBranch, whose three radios are
    // soft / mixed / hard; the port already exposes the same three as a submenu on the
    // revision grid, so the tree uses that shape too. Hard is confirmed, since it throws
    // the working tree away.
    private MenuItem ResetCurrentBranchItem(string target)
    {
        MenuItem reset = new()
        {
            Header = T("MenuItemsStrings/Reset.Text", "Reset current branch to here…"),
        };

        if (IconLoader.Image("ResetCurrentBranchToHere") is { } img)
        {
            reset.Icon = img;
        }

        reset.Items.Add(MenuItem(T("Reset (soft) to here"), null, () => RunStash(() => _stashService.Reset(_repoPath!, target, StashResetMode.Soft))));
        reset.Items.Add(MenuItem(T("Reset (mixed) to here"), null, () => RunStash(() => _stashService.Reset(_repoPath!, target, StashResetMode.Mixed))));
        reset.Items.Add(MenuItem(T("Reset (HARD) to here…"), null, () => _ = DoResetHardAsync(target)));
        return reset;
    }

    private ContextMenu WorktreeRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnManageWorktreesFromRootNode.Text", "Manage worktrees…"), "WorkTree", () => _ = DoManageWorktreesAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnCreateWorktreeFromRootNode.Text", "Add…"), "WorkTree", () => _ = DoAddWorktreeAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnPruneWorktreesFromRootNode.Text", "Prune"), "CleanupRepo", () => RunWorktree(() => _worktreeService.PruneWorktrees(_repoPath!))));
        return menu;
    }

    // Upstream keeps every worktree item visible and disables the ones that cannot
    // apply (ContextActions.cs:99-114): Open and Delete are refused for the worktree the
    // app currently has open and for one whose folder is gone, and "Show in folder"
    // needs the folder to still exist. The port had no gating at all, so "Remove" could
    // be asked for on the checked-out worktree.
    private ContextMenu WorktreeMenu(WorktreeRow row, bool isCurrent)
    {
        bool canAct = !isCurrent && !row.IsPrunable;
        bool pathExists = row.Path.Length > 0 && System.IO.Directory.Exists(row.Path);

        ContextMenu menu = new();

        // "Open" makes the worktree the active repository, routed to the host via
        // OpenRepositoryRequested (the tree never references MainWindow directly).
        MenuItem open = MenuItem(T("RepoObjectsTree/mnubtnOpenWorktree.Text", "Open worktree"), "FolderOpen", () => OpenRepositoryRequested?.Invoke(row.Path));
        open.IsEnabled = canAct;
        ToolTip.SetTip(open, T("RepoObjectsTree/mnubtnOpenWorktree.ToolTipText", "Open this worktree"));
        menu.Items.Add(open);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Branch.Length > 0 ? row.Branch : System.IO.Path.GetFileName(row.Path.TrimEnd('/', '\\')))));

        MenuItem copyPath = MenuItem(T("RepoObjectsTree/mnubtnCopyWorktreePath.Text", "Copy path"), "CopyToClipboard", () => CopyText(row.Path));
        ToolTip.SetTip(copyPath, T("RepoObjectsTree/mnubtnCopyWorktreePath.ToolTipText", "Copy the worktree path to clipboard"));
        menu.Items.Add(copyPath);

        MenuItem showInFolder = MenuItem(T("RepoObjectsTree/mnubtnShowWorktreeInFolder.Text", "Show in folder"), "BrowseFileExplorer", () => ShowInFolder(row.Path));
        showInFolder.IsEnabled = pathExists;
        ToolTip.SetTip(showInFolder, T("RepoObjectsTree/mnubtnShowWorktreeInFolder.ToolTipText", "Show the worktree in the file manager"));
        menu.Items.Add(showInFolder);

        menu.Items.Add(new Separator());

        MenuItem delete = MenuItem(T("RepoObjectsTree/mnubtnDeleteWorktree.Text", "Delete worktree…"), "DeleteFile", () => _ = DoRemoveWorktreeAsync(row));
        delete.IsEnabled = canAct;
        ToolTip.SetTip(delete, T("RepoObjectsTree/mnubtnDeleteWorktree.ToolTipText", "Delete this worktree"));
        menu.Items.Add(delete);

        return menu;
    }

    // Upstream's WorktreeNode.GetToolTipText: the path with a "(current)" / "(deleted)"
    // marker, then the branch (or "bare" / "detached at <sha>"), then the short HEAD.
    private static string WorktreeTooltip(WorktreeRow row, bool isCurrent)
    {
        string shortSha = row.Head.Length >= 7 ? row.Head[..7] : row.Head;

        // Current wins over deleted, as upstream orders the two checks.
        string status = isCurrent ? T(" (current)")
            : row.IsPrunable ? T(" (deleted)")
            : string.Empty;

        string branchLine = row.IsBare ? T("bare")
            : row.IsDetached ? TF("detached at {0}", shortSha)
            : row.Branch.Length > 0 ? row.Branch
            : T("unknown");

        string text = row.Path + status
            + Environment.NewLine + TF("Branch: {0}", branchLine);

        return shortSha.Length > 0
            ? text + Environment.NewLine + TF("HEAD: {0}", shortSha)
            : text;
    }

    // The Stashes root node, which had no menu at all in the port: upstream's
    // mnubtnStashAllFromRootNode / mnubtnStashStagedFromRootNode /
    // mnubtnManageStashFromRootNode.
    private ContextMenu StashRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnStashAllFromRootNode.Text", "Stash"), "stash", () => RunStash(() => _stashService.StashSave(_repoPath!, string.Empty, includeUntracked: true))));
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnStashStagedFromRootNode.Text", "Stash staged"), "stash", () => RunStash(() => _stashService.StashStaged(_repoPath!, string.Empty))));
        menu.Items.Add(new Separator());
        menu.Items.Add(ManageStashesItem());
        return menu;
    }

    private ContextMenu StashMenu(StashRow row)
    {
        ContextMenu menu = new();
        menu.Items.Add(OpenStashItem(row));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnApplyStash.Text", "Apply stash"), null, () => RunStash(() => _stashService.StashApply(_repoPath!, row.Name))));
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnPopStash.Text", "Pop stash"), null, () => RunStash(() => _stashService.StashPop(_repoPath!, row.Name))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnDropStash.Text", "Drop stash…"), null, () => _ = DoDropStashAsync(row)));
        return menu;
    }

    // Upstream's mnubtnOpenStash and "Manage stashes…" both open FormStash, the one
    // opening on the stash that was clicked (StashNode.OpenStash passes its
    // ReflogSelector). Only the host owns windows, hence StashDialogRequested; disabled
    // while nothing is listening, so the item is never dead.
    private MenuItem OpenStashItem(StashRow? row)
    {
        MenuItem item = MenuItem(T("RepoObjectsTree/mnubtnOpenStash.Text", "Open stash"), "stash", () => StashDialogRequested?.Invoke(row?.Name));
        item.IsEnabled = StashDialogRequested is not null;
        ToolTip.SetTip(item, T("RepoObjectsTree/mnubtnOpenStash.ToolTipText", "Open this stash"));
        return item;
    }

    private MenuItem ManageStashesItem()
    {
        MenuItem item = MenuItem(T("RepoObjectsTree/mnubtnManageStashFromRootNode.Text", "Manage stashes…"), "stash", () => StashDialogRequested?.Invoke(null));
        item.IsEnabled = StashDialogRequested is not null;
        return item;
    }

    private ContextMenu SubmoduleMenu(SubmoduleRow row)
    {
        ContextMenu menu = new();
        // "Open" makes the submodule the active repository, routed to the host via
        // OpenRepositoryRequested (the tree never references MainWindow directly).
        string openText = row.IsCurrent ? T("Open in new instance") : T("RepoObjectsTree/mnubtnOpenSubmodule.Text", "Open");
        MenuItem open = MenuItem(openText, "RepoOpen", () =>
        {
            if (row.IsCurrent)
            {
                OpenRepositoryInNewInstanceRequested?.Invoke(SubmoduleFullPath(row));
            }
            else
            {
                OpenRepositoryRequested?.Invoke(SubmoduleFullPath(row));
            }
        });
        open.IsEnabled = row.Exists;
        menu.Items.Add(open);
        if (row.Path.Length > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnUpdateSubmodule.Text", "Update"), "SubmodulesUpdate", () => RunSubmodule(() => _submoduleService.Update(_repoPath!, row))));
            menu.Items.Add(MenuItem(T("Update (merge)…"), "Merge", () => _ = DoMergeSubmoduleAsync(row)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Path)));
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnCopyWorktreePath.Text", "Copy path"), "CopyToClipboard", () => CopyText(SubmoduleFullPath(row))));
        return menu;
    }

    private ContextMenu SubmoduleRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("FormBrowse/manageSubmodulesToolStripMenuItem.Text", "Manage submodules…"), "SubmodulesManage", () => _ = DoManageSubmodulesAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("FormBrowse/updateAllSubmodulesToolStripMenuItem.Text", "Update all"), "SubmodulesSync", () => RunSubmodule(() => _submoduleService.UpdateAll(_repoPath!))));
        return menu;
    }

    private ContextMenu RemotesRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnuBtnManageRemotesFromRootNode.ToolTipText", "Manage remotes…"), "Remotes", () => _ = DoManageRemotesAsync()));
        menu.Items.Add(new Separator());

        // Upstream's mnuBtnFetchAllRemotes / mnuBtnPruneAllRemotes. Handed to the host:
        // a fetch needs the streaming process dialog, the credential retry and the
        // file-watcher suspension that MainWindow.RunRemoteOp owns, and running it here
        // would give the tree a second, weaker fetch path.
        MenuItem fetchAll = MenuItem(T("RepoObjectsTree/mnuBtnFetchAllRemotes.Text", "Fetch all"), "Pull", () => FetchAllRequested?.Invoke());
        fetchAll.IsEnabled = FetchAllRequested is not null;
        menu.Items.Add(fetchAll);

        MenuItem pruneAll = MenuItem(T("RepoObjectsTree/mnuBtnPruneAllRemotes.Text", "Fetch and prune all"), "Pull", () => FetchAndPruneAllRequested?.Invoke());
        pruneAll.IsEnabled = FetchAndPruneAllRequested is not null;
        menu.Items.Add(pruneAll);

        return menu;
    }

    private ContextMenu RemoteGroupMenu(string remote)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(remote)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Edit URL…"), "Remote", () => _ = DoEditRemoteUrlAsync(remote)));
        menu.Items.Add(MenuItem(T("TranslatedStrings/_actionRename.Text", "Rename…"), "Remote", () => _ = DoRenameRemoteAsync(remote)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("FormSubmodules/RemoveSubmodule.Text", "Remove"), "RemoteDelete", () => _ = DoRemoveRemoteAsync(remote)));
        return menu;
    }

    private static MenuItem MenuItem(string text, string? icon, Action onClick)
    {
        MenuItem item = new() { Header = text };
        if (icon is not null && IconLoader.Image(icon) is { } img)
        {
            item.Icon = img;
        }

        item.Click += (_, _) => onClick();
        return item;
    }

    // --- Ref sorting / reordering (view-only) -----------------------------

    // Sort submenu attached to the Branches and Tags root nodes. Rebuilt on
    // every BuildTree so the ✓ markers reflect the current session settings.
    private ContextMenu RefSortMenu(IReadOnlyList<Control>? leadingItems = null)
    {
        ContextMenu menu = new();
        if (leadingItems is { Count: > 0 })
        {
            foreach (Control item in leadingItems)
            {
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
        }

        menu.Items.Add(SortKeyItem(T("Sort by name"), RefSortKey.Name));
        menu.Items.Add(SortKeyItem(T("Sort by commit date"), RefSortKey.CommitDate));
        menu.Items.Add(new Separator());
        menu.Items.Add(SortOrderItem(T("Ascending"), RefSortOrder.Ascending));
        menu.Items.Add(SortOrderItem(T("Descending"), RefSortOrder.Descending));
        return menu;
    }

    // "Create branch…" / "Create tag…" on the Branches / Tags root nodes: the
    // proper modals (name + checkout-after-create, name + kind/force/push),
    // not the bare text prompts.
    private IReadOnlyList<Control> BranchesRootItems()
        => [MenuItem(T("FormCreateBranch/$this.Text", "Create branch") + "…", "BranchCreate", () => _ = DoCreateBranchAsync())];

    private IReadOnlyList<Control> TagsRootItems()
        => [MenuItem(T("FormCreateTag/$this.Text", "Create tag") + "…", "TagCreate", () => _ = DoCreateTagAsync())];

    private MenuItem SortKeyItem(string text, RefSortKey key)
        => MenuItem(_sortKey == key ? "✓ " + text : "    " + text, null, () => SetSort(key, _sortOrder));

    private MenuItem SortOrderItem(string text, RefSortOrder order)
        => MenuItem(_sortOrder == order ? "✓ " + text : "    " + text, null, () => SetSort(_sortKey, order));

    // Applies new session-local sort settings and rebuilds the tree from the
    // retained snapshot.
    private void SetSort(RefSortKey key, RefSortOrder order)
    {
        _sortKey = key;
        _sortOrder = order;
        PersistFilterPrefs();

        if (_snapshot is not { } snapshot)
        {
            return;
        }

        if (key == RefSortKey.CommitDate)
        {
            EnsureDatesThenRebuild(snapshot);
        }
        else
        {
            BuildTree(snapshot);
        }
    }

    // Resolves any missing commit dates off the UI thread (via the reused core
    // module), caches them for the session, then rebuilds. Read-only work; a
    // reentrancy flag avoids overlapping resolves.
    private void EnsureDatesThenRebuild(RepoSnapshot snapshot)
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            BuildTree(snapshot);
            return;
        }

        List<string> missing = snapshot.Refs.Branches
            .Concat(snapshot.Refs.Tags)
            .Select(r => r.ObjectId)
            .Where(oid => oid.Length > 0 && !_commitDates.ContainsKey(oid))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0 || _resolvingDates)
        {
            BuildTree(snapshot);
            return;
        }

        _resolvingDates = true;
        _ = Task.Run(() =>
        {
            Dictionary<string, DateTime> resolved = new(StringComparer.Ordinal);
            try
            {
                GitModule module = GitContext.CreateModule(repo);
                foreach (string oid in missing)
                {
                    try
                    {
                        resolved[oid] = module.GetRevision(ObjectId.Parse(oid), shortFormat: true).CommitDate;
                    }
                    catch
                    {
                        // Unresolvable ref (e.g. annotated-tag object): leave it
                        // out so DateFor falls back to DateTime.MinValue.
                    }
                }
            }
            catch
            {
                // Module creation failed; rebuild with whatever is cached.
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach ((string oid, DateTime date) in resolved)
                {
                    _commitDates[oid] = date;
                }

                _resolvingDates = false;
                BuildTree(snapshot);
            });
        });
    }

    // Local-branch order: the current sort settings. There is no manual per-branch order
    // any more — see the DefaultCategoryOrder comment for why "Move Up / Move Down" moved
    // from the branches to the categories.
    private IEnumerable<BranchTagRow> OrderLocalBranches(IReadOnlyList<BranchTagRow> local)
        => SortRefs(local);

    private List<BranchTagRow> SortRefs(IEnumerable<BranchTagRow> rows)
    {
        bool asc = _sortOrder == RefSortOrder.Ascending;
        if (_sortKey == RefSortKey.CommitDate)
        {
            return (asc
                    ? rows.OrderBy(DateFor).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(DateFor).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return (asc
                ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private DateTime DateFor(BranchTagRow row)
        => row.ObjectId.Length > 0 && _commitDates.TryGetValue(row.ObjectId, out DateTime d) ? d : DateTime.MinValue;

    // Fire-and-forget copy to the system clipboard via the Avalonia TopLevel.
    private void CopyText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        }
    }

    // Absolute filesystem path of a submodule (its Path is repo-relative).
    private string SubmoduleFullPath(SubmoduleRow row)
        => row.AbsolutePath.Length > 0
            ? row.AbsolutePath
            : _repoPath is { Length: > 0 } repo
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(repo, row.Path))
            : row.Path;

    // --- Interactions -----------------------------------------------------

    private void OnSelectionChanged()
    {
        if (_suppressSelectionNotify)
        {
            return;
        }

        if (_tree.SelectedItem is TreeViewItem { Tag: BranchTagRow row } && row.ObjectId.Length > 0)
        {
            RefSelected?.Invoke(row.ObjectId);
        }
    }

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint(_tree).Properties;
        if (properties.IsLeftButtonPressed && TryFindTreeItem(e.Source, out TreeViewItem item))
        {
            // The chevron owns a BAND, not just its own glyph: the column it sits in,
            // over the full height of the row. Hitting the arrow of a 12 px glyph — and
            // knowing whether one hit it — was a coin toss, and a miss ran the row's
            // activation instead (a checkout, a repository switch). Inside the band the
            // press only folds the node and never touches the selection, the way a tree
            // control's +/- box behaves.
            if (IsInExpanderBand(item, e))
            {
                item.IsExpanded = !item.IsExpanded;
                e.Handled = true;
                return;
            }

            // Outside the band the row belongs to the label. TreeViewItem's class handler
            // would toggle IsExpanded for a press anywhere in the header, so the press is
            // handled here in the tunnel instead: selection stays immediate and nothing
            // folds. Activating on the second press also keeps the exact pointer row and
            // avoids a DoubleTapped event whose visual source may disappear mid-navigation.
            SelectOnly(item);
            item.Focus();
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                OnActivate(item);
            }

            return;
        }

        if (!properties.IsRightButtonPressed || _suppressSelectionNotify)
        {
            return;
        }

        _suppressSelectionNotify = true;
        Dispatcher.UIThread.Post(() => _suppressSelectionNotify = false, DispatcherPriority.Background);
    }

    private static bool TryFindTreeItem(object? source, out TreeViewItem item)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TreeViewItem treeItem)
            {
                item = treeItem;
                return true;
            }
        }

        item = null!;
        return false;
    }

    // Whether the press landed in <paramref name="item"/>'s chevron band: the horizontal
    // slice of the row the chevron occupies, plus a little slack on each side, at any
    // height. A node with nothing to expand has no chevron and therefore no band, so its
    // whole row belongs to the label.
    private static bool IsInExpanderBand(TreeViewItem item, PointerPressedEventArgs e)
    {
        // Slack on each side of the glyph, so the band is a comfortable target without
        // reaching the icon that follows it.
        const double Slack = 3;

        if (OwnChevron(item) is not { IsVisible: true } chevron || chevron.Bounds.Width <= 0)
        {
            return false;
        }

        double x = e.GetPosition(chevron).X;
        return x >= -Slack && x <= chevron.Bounds.Width + Slack;
    }

    // The item's OWN expand/collapse toggle. The search does not descend into nested
    // TreeViewItems: their chevrons live in this item's visual tree too, and the first
    // one found by a plain descendant walk can belong to a child row.
    private static ToggleButton? OwnChevron(TreeViewItem item)
    {
        Queue<Visual> queue = new();
        foreach (Visual child in item.GetVisualChildren())
        {
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            Visual visual = queue.Dequeue();
            if (visual is ToggleButton toggle)
            {
                return toggle;
            }

            if (visual is TreeViewItem)
            {
                continue;
            }

            foreach (Visual child in visual.GetVisualChildren())
            {
                queue.Enqueue(child);
            }
        }

        return null;
    }

    // Double-click / Enter. Upstream gives each node type its own OnDoubleClick:
    // a local branch is checked out (LocalBranchNode), a REMOTE branch is checked out too
    // (RemoteBranchNode.cs:73-76 — the port did nothing at all), a TAG creates a branch
    // from it (TagNode.cs:28-31), a STASH opens the stash (StashNode.cs:33-36), and a
    // worktree is opened unless it is the current or a deleted one (WorktreeNode).
    private void OnActivate(TreeViewItem? activationItem = null)
    {
        switch (activationItem ?? _tree.SelectedItem)
        {
            case TreeViewItem { Tag: BranchTagRow { IsTag: true } tag }:
                _ = DoCreateBranchAsync(tag.Name, prefix: string.Empty);
                break;

            case TreeViewItem { Tag: BranchTagRow row }:
                DoCheckout(row);
                break;

            case TreeViewItem { Tag: StashRow stash }:
                StashDialogRequested?.Invoke(stash.Name);
                break;

            case TreeViewItem { Tag: WorktreeRow worktree }:
                if (!worktree.IsSamePath(_repoPath) && !worktree.IsPrunable)
                {
                    OpenRepositoryRequested?.Invoke(worktree.Path);
                }

                break;

            // Upstream's SubmoduleNode.OnDoubleClick opens the submodule as the browsed
            // repository (SubmoduleNode.cs:112-121, via SetWorkingDir), which here is the
            // same route as the node's own "Open" menu item. A submodule that was never
            // initialized has no repository to open: its directory is empty, so opening it
            // would only swap the window onto a non-repo path.
            case TreeViewItem { Tag: SubmoduleRow submodule }:
                if (submodule.Exists)
                {
                    if (submodule.IsCurrent)
                    {
                        OpenRepositoryInNewInstanceRequested?.Invoke(SubmoduleFullPath(submodule));
                    }
                    else
                    {
                        OpenRepositoryRequested?.Invoke(SubmoduleFullPath(submodule));
                    }
                }
                else
                {
                    FeedbackRequested?.Invoke(TF("Submodule is not initialized or is missing: {0}", SubmoduleFullPath(submodule)));
                }

                break;
        }
    }

    // Del — upstream's RepoObjectsTree.Command.Delete, dispatched to the selected node's
    // OnDelete. Returns whether the key was consumed, so a node with nothing to delete
    // leaves Del to the rest of the window.
    private bool OnDeleteSelected()
    {
        switch (_tree.SelectedItem)
        {
            case TreeViewItem { Tag: BranchTagRow { IsTag: true } tag }:
                _ = DoDeleteTagAsync(tag);
                return true;

            case TreeViewItem { Tag: BranchTagRow { IsRemote: true } remote }:
                _ = DoDeleteRemoteBranchAsync(remote);
                return true;

            case TreeViewItem { Tag: BranchTagRow branch }:
                if (branch.IsCurrent)
                {
                    // The checked-out branch cannot be deleted; upstream disables the item.
                    return true;
                }

                _ = DoDeleteBranchAsync(branch);
                return true;

            case TreeViewItem { Tag: StashRow stash }:
                _ = DoDropStashAsync(stash);
                return true;

            case TreeViewItem { Tag: WorktreeRow worktree }:
                if (!worktree.IsSamePath(_repoPath) && !worktree.IsPrunable)
                {
                    _ = DoRemoveWorktreeAsync(worktree);
                }

                return true;

            default:
                return false;
        }
    }

    // F2 — upstream's RepoObjectsTree.Command.Rename. Only a local branch implements
    // ICanRename, so that is the only node that answers.
    private bool OnRenameSelected()
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: BranchTagRow { IsTag: false, IsRemote: false } row })
        {
            _ = DoRenameBranchAsync(row);
            return true;
        }

        return false;
    }

    private void ShowInFolder(string path)
    {
        // Off the UI thread: the launcher spawns a process and can block briefly.
        _ = Task.Run(() =>
        {
            try
            {
                new ExternalToolService().ShowInFolder(path);
            }
            catch
            {
                // Nothing to surface from a tree node; the file manager simply does not open.
            }
        });
    }

    private void DoCheckout(BranchTagRow row) => _ = DoCheckoutAsync(row);

    // Checkout with the upstream "local changes" semantics: a clean working tree
    // checks out straight away (no dialog), a dirty one first asks what to do with
    // the pending changes (don't change / merge / reset / stash). The probe and the
    // git work both run off the UI thread — the services block on async work.
    private async Task DoCheckoutAsync(BranchTagRow row)
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (_busy)
            {
                await NotifyBusyAsync();
                return;
            }

            // A remote branch goes through the full FormCheckoutBranch port: upstream's
            // RemoteBranchNode.Checkout opens that dialog with remote:true
            // (LeftPanel/RemoteBranchNode.cs:63-66), because "check out origin/x" has to
            // be answered first — new local branch with a custom name, reset of the
            // tracking branch, or a detached HEAD. A plain `git checkout origin/x`
            // always detaches.
            if (row is { IsRemote: true, IsTag: false })
            {
                await CheckoutRemoteWithFormAsync(repo, row.Name);
                return;
            }

            LocalChangesAction? action = await CheckoutBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, row.Name);

            if (action is not { } changesAction)
            {
                return;
            }

            // The checkout itself runs inside the process dialog (upstream's
            // FormCheckoutBranch goes through FormProcess, FormCheckoutBranch.cs:357), so
            // there is NO RunMutation wrapper here — that would check out a second time.
            // This is the path a plain double click takes, and the one that used to be
            // completely silent on a clean working tree, where AskAsync answers
            // DontChange without showing anything (CheckoutBranchDialog.cs:196-223).
            try
            {
                _busy = true;
                await RefProcessRunner.CheckoutAsync(
                    TopLevel.GetTopLevel(this) as Window,
                    repo,
                    row.Name,
                    changesAction,
                    service: _branchTagService);
            }
            finally
            {
                // git has exited, so the guard is released HERE — before the reload. The
                // reload has its own epoch-based guard (_refreshing), so the next checkout
                // is accepted immediately instead of waiting out the ~1.4 s tree rebuild.
                _busy = false;
            }

            // Reloaded on failure and on Abort too: an interrupted checkout can already
            // have moved HEAD, and the tree (bold current branch) must show what the
            // repository is now.
            OperationCompleted?.Invoke();
            Refresh();
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    /// <summary>
    ///  Checkout of a REMOTE branch with upstream's own semantics: the full
    ///  <see cref="CheckoutBranchForm"/>, then — for a <c>checkout -B</c> that is not a
    ///  fast-forward — the confirmation upstream shows before discarding commits
    ///  (<c>FormCheckoutBranch.cs:293-317</c>), and finally the core command through
    ///  <see cref="BranchTagService.CheckoutBranch"/>.
    /// </summary>
    private async Task CheckoutRemoteWithFormAsync(string repo, string remoteBranch)
    {
        CheckoutBranchChoice? choice = await CheckoutBranchForm.AskAsync(
            TopLevel.GetTopLevel(this) as Window, repo, remoteBranch, remote: true);

        if (choice is not { } c)
        {
            return;
        }

        if (c.NewBranchMode == CheckoutNewBranchMode.Reset && c.NewBranchName is { Length: > 0 } localName)
        {
            ResetFastForwardInfo info = await Task.Run(
                () => _branchTagService.GetResetFastForwardInfo(repo, localName, c.BranchName));

            if (!info.IsFastForward)
            {
                bool go = await ConfirmAsync(TF(
                    "You are going to reset the “{0}” branch to a new location discarding ALL the commited changes since the {1} revision.\n\nAre you sure?",
                    localName,
                    info.MergeBaseDisplay));

                if (!go)
                {
                    return;
                }
            }
        }

        // Same as the local path: the process dialog runs git, so no RunMutation wrapper.
        // The non-fast-forward confirmation above stays here — the helper never asks it.
        try
        {
            _busy = true;
            await RefProcessRunner.CheckoutBranchAsync(
                TopLevel.GetTopLevel(this) as Window,
                repo,
                c.BranchName,
                c.IsRemote,
                c.LocalChanges,
                c.NewBranchMode,
                c.NewBranchName,
                service: _branchTagService);
        }
        finally
        {
            _busy = false;
        }

        OperationCompleted?.Invoke();
        Refresh();
    }

    // --- Create branch / tag ----------------------------------------------

    /// <param name="startPoint">
    ///  The revision the branch is created at: "HEAD" from the Branches root and from a
    ///  folder node, or a ref name when the item sits on a branch or tag (upstream's
    ///  GitRefCreateBranch slot passes the node's ObjectId).
    /// </param>
    /// <param name="prefix">
    ///  Name prefix the dialog starts with — a folder node offers "feature/" already
    ///  filled in, as upstream's BranchPathNode.CreateBranch passes
    ///  <c>newBranchNamePrefix</c> (BranchPathNode.cs:24-28).
    /// </param>
    // "Merge into current branch…" opens the merge configuration dialog (the port of
    // FormMergeBranch) instead of merging straight away with hard-wired options. The
    // dialog runs `git merge` itself through the process dialog, so this must NOT be
    // wrapped in RunMutation — that helper would run git a second time.
    private async Task DoMergeAsync(string name)
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (_busy)
            {
                await NotifyBusyAsync();
                return;
            }

            Window owner = (TopLevel.GetTopLevel(this) as Window)!;
            MergeDialogResult? result = await MergeDialog.ShowAsync(owner, repo, name);

            if (result is not null)
            {
                OperationCompleted?.Invoke();

                // A merge that stopped on conflicts asks the question upstream asks
                // (MergeConflictHandler), instead of leaving the state to be
                // discovered by opening the commit dialog.
                await ConflictFlow.HandleAsync(owner, repo);
                OperationCompleted?.Invoke();
            }
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    // A rebase stops on the first conflict, and now that the banner can continue,
    // skip or abort one, it is worth asking straight away — the same question
    // upstream asks. Done here rather than through RunMutation because the ask has
    // to wait for git to finish.
    private async Task DoRebaseAsync(string name)
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo
                || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            if (_busy)
            {
                await NotifyBusyAsync();
                return;
            }

            _busy = true;
            try
            {
                await Task.Run(() => _branchTagService.RebaseOnto(repo, name));
            }
            catch
            {
                // The result is surfaced by the refresh and the banner below.
            }
            finally
            {
                _busy = false;
            }

            OperationCompleted?.Invoke();
            Refresh();

            if (await ConflictFlow.HandleAsync(owner, repo) is { HadConflicts: true })
            {
                OperationCompleted?.Invoke();
                Refresh();
            }
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    private async Task DoCreateBranchAsync(string startPoint = "HEAD", string prefix = "")
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            // 13.1: this used to be a mute `return`, which is the one remaining
            // explanation for a "Create branch…" click that appears to do nothing.
            if (_busy)
            {
                await NotifyBusyAsync();
                return;
            }

            string display = startPoint is "HEAD" or "" ? T("HEAD (current revision)") : startPoint;
            CreateBranchRequest? request = await CreateBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, display, prefix);

            if (request is not { } r)
            {
                return;
            }

            // Created inside the process dialog, as upstream does through FormProcess
            // (FormCreateBranch.cs:163). No RunMutation wrapper: it would run git twice.
            try
            {
                _busy = true;
                await RefProcessRunner.CreateBranchAsync(
                    TopLevel.GetTopLevel(this) as Window,
                    repo,
                    r.Name,
                    startPoint,
                    r.Checkout,
                    service: _branchTagService);
            }
            finally
            {
                _busy = false;
            }

            // Refreshed on both outcomes, for the same reason as the checkout: a
            // "create and checkout" that failed halfway may still have created the ref.
            OperationCompleted?.Invoke();
            Refresh();
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    // "Delete All" on a branch folder node (upstream mnubtnDeleteAllBranches). The
    // confirmation lists exactly what is about to go, because one click here can delete
    // a dozen branches; upstream instead opens FormDeleteBranch with the list preloaded.
    private async Task DoDeleteAllBranchesAsync(string folderPath, IReadOnlyList<BranchTagRow> localBranches)
    {
        try
        {
            string prefix = folderPath + "/";
            List<BranchTagRow> victims = localBranches
                .Where(r => !r.IsCurrent && r.Name.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (victims.Count == 0)
            {
                return;
            }

            string list = string.Join(Environment.NewLine, victims.Select(r => "  " + r.Name));
            if (!await ConfirmAsync(
                TF("Delete all {0} branches under '{1}'?", victims.Count, folderPath)
                + Environment.NewLine + Environment.NewLine + list))
            {
                return;
            }

            // One git call per branch, all off the UI thread, stopping at the first
            // failure (an unmerged branch) so the reason stays attributable.
            RunMutation(() =>
            {
                BranchTagResult last = new(true, string.Empty);
                foreach (BranchTagRow victim in victims)
                {
                    last = _branchTagService.DeleteBranch(_repoPath!, victim.Name, force: false);
                    if (!last.Success)
                    {
                        break;
                    }
                }

                return last;
            });
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    // `git reset --hard` throws away every uncommitted change, so it is confirmed —
    // the same treatment the host gives its own "Reset (HARD) to here".
    private async Task DoResetHardAsync(string target)
    {
        try
        {
            if (await ConfirmAsync(
                TF("Reset the current branch to '{0}', discarding all local changes?", target)
                + Environment.NewLine + T("TranslatedStrings/_resetHardWarning.Text", "This will delete all changes to your working directory and cannot be undone.")))
            {
                RunStash(() => _stashService.Reset(_repoPath!, target, StashResetMode.Hard));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoCreateTagAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (_busy)
            {
                await NotifyBusyAsync();
                return;
            }

            CreateTagRequest? request = await CreateTagDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, T("HEAD (current revision)"));

            if (request is not { } r)
            {
                return;
            }

            RunMutation(() => _branchTagService.CreateTag(
                repo, r.Name, commit: "HEAD", r.Message, r.Operation, r.SignKeyId, r.Force, r.PushToRemote));
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    private async Task DoRenameBranchAsync(BranchTagRow row)
    {
        try
        {
            if (row.IsTag || row.IsRemote)
            {
                return;
            }

            string? newName = await PromptAsync(TF("Rename branch '{0}' to:", row.Name), row.Name, T("FormRenameBranch/$this.Text", "Rename branch"));
            if (newName is { Length: > 0 } target
                && !string.Equals(target, row.Name, StringComparison.Ordinal))
            {
                RunMutation(() => _branchTagService.RenameBranch(_repoPath!, row.Name, target));
            }
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoDeleteBranchAsync(BranchTagRow row)
    {
        try
        {
            if (row.IsCurrent || _repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (_busy)
            {
                await NotifyBusyAsync();
                return;
            }

            if (!await ConfirmAsync(TF("Delete branch '{0}'?", row.Name)))
            {
                return;
            }

            // Upstream deletes with --force ALWAYS (FormDeleteBranch.cs:118) and instead
            // warns first when the branch is not fully merged (:90-116). The port used to
            // delete without --force and say nothing when git refused, so the single most
            // common case — a branch with commits of its own — silently did nothing.
            bool force = true;
            if (!_branchTagService.IsBranchMerged(repo, row.Name))
            {
                force = await ConfirmAsync(
                    T("The branch you are about to delete is not fully merged. Are you sure you want to delete it?")
                    + "\n" + T("Deleted branches can be recovered using the reflog for a while."));
                if (!force)
                {
                    return;
                }
            }

            // Deleted inside the process dialog, like create branch and checkout: git's
            // own refusal has to be readable. No RunMutation wrapper — it would delete twice.
            try
            {
                _busy = true;
                await RefProcessRunner.DeleteBranchAsync(
                    TopLevel.GetTopLevel(this) as Window, repo, row.Name, force, _branchTagService);
            }
            finally
            {
                // Cleared before the refresh: the reload has its own guard, not _busy.
                _busy = false;
            }

            // On failure too: git may have deleted some refs before stopping, and a stale
            // tree after a failed delete is its own bug report.
            OperationCompleted?.Invoke();
            Refresh();
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoDeleteRemoteBranchAsync(BranchTagRow row)
    {
        try
        {
            if (!row.IsRemote || _repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (_busy)
            {
                await NotifyBusyAsync();
                return;
            }

            string remote = RemoteName(row);
            string branch = ShortRemoteName(row.Name, remote);
            if (remote.Length == 0 || branch.Length == 0)
            {
                return;
            }

            // Destructive and affects the remote: confirm before pushing the delete.
            if (!await ConfirmAsync(TF("Delete branch '{0}' on remote '{1}'?", branch, remote)
                + "\n" + T("Deleting a branch on the remote cannot be undone.")))
            {
                return;
            }

            // Same route as the local delete: the push talks to the network and can fail
            // for a dozen server-side reasons, all of which used to vanish.
            try
            {
                _busy = true;
                await RefProcessRunner.DeleteRemoteBranchAsync(
                    TopLevel.GetTopLevel(this) as Window, repo, remote, branch, _branchTagService);
            }
            finally
            {
                _busy = false;
            }

            OperationCompleted?.Invoke();
            Refresh();
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoMergeSubmoduleAsync(SubmoduleRow row)
    {
        try
        {
            if (await ConfirmAsync(TF("Update submodule '{0}' to its remote branch and merge into the current checkout?", row.Path)))
            {
                RunSubmodule(() => _submoduleService.UpdateMerge(_repoPath!, row));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoDeleteTagAsync(BranchTagRow row)
    {
        try
        {
            if (await ConfirmAsync(TF("Delete tag '{0}'?", row.Name)))
            {
                RunMutation(() => _branchTagService.DeleteTag(_repoPath!, row.Name));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoDropStashAsync(StashRow row)
    {
        try
        {
            if (await ConfirmAsync(TF("Drop stash '{0}'?", row.Name)))
            {
                RunStash(() => _stashService.StashDrop(_repoPath!, row.Name));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoManageRemotesAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            RemotesDialog dialog = new(repo);
            await dialog.ShowDialog(owner);
            if (dialog.Changed)
            {
                OperationCompleted?.Invoke();
                Refresh();
            }
        }
        catch
        {
            // No status surface on this control; the dialog simply closes.
        }
    }

    private async Task DoManageSubmodulesAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            SubmodulesDialog dialog = new(repo);
            await dialog.ShowDialog(owner);
            if (dialog.Changed)
            {
                OperationCompleted?.Invoke();
                Refresh();
            }
        }
        catch
        {
            // No status surface on this control; the dialog simply closes.
        }
    }

    private async Task DoEditRemoteUrlAsync(string remote)
    {
        try
        {
            // Off the UI thread: the lookup shells out to git (see RemoteService).
            string current = await Task.Run(() => FindRemoteUrl(remote));
            string? url = await PromptAsync(TF("URL for remote '{0}':", remote), current, T("Edit URL"));
            if (url is { Length: > 0 } target && !string.Equals(target, current, StringComparison.Ordinal))
            {
                RunRemote(() => _remoteService.SetRemoteUrl(_repoPath!, remote, target));
            }
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoRenameRemoteAsync(string remote)
    {
        try
        {
            string? name = await PromptAsync(TF("Rename remote '{0}' to:", remote), remote, T("TranslatedStrings/_actionRename.Text", "Rename"));
            if (name is { Length: > 0 } target && !string.Equals(target, remote, StringComparison.Ordinal))
            {
                RunRemote(() => _remoteService.RenameRemote(_repoPath!, remote, target));
            }
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoRemoveRemoteAsync(string remote)
    {
        try
        {
            if (await ConfirmAsync(TF("Remove remote '{0}'?", remote)))
            {
                RunRemote(() => _remoteService.RemoveRemote(_repoPath!, remote));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoManageWorktreesAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            WorktreesDialog dialog = new(repo);
            await dialog.ShowDialog(owner);
            if (dialog.Changed)
            {
                OperationCompleted?.Invoke();
                Refresh();
            }
        }
        catch
        {
            // No status surface on this control; the dialog simply closes.
        }
    }

    private async Task DoAddWorktreeAsync()
    {
        try
        {
            string? path = await PromptAsync(T("FormCreateWorktree/lblNewWorktreeFolder.Text", "New worktree path:"), string.Empty, T("FormCreateWorktree/$this.Text", "Create a new worktree"));
            if (path is not { Length: > 0 } target)
            {
                return;
            }

            // Branch is optional: empty lets git create a branch named after the path.
            string? branch = await PromptAsync(TF("Branch/revision for '{0}' (blank = new branch):", target), string.Empty, T("FormCreateWorktree/$this.Text", "Create a new worktree"));
            RunWorktree(() => _worktreeService.AddWorktree(_repoPath!, target, branch ?? string.Empty));
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoRemoveWorktreeAsync(WorktreeRow row)
    {
        try
        {
            if (await ConfirmAsync(TF("Remove worktree '{0}'?", row.Path)))
            {
                RunWorktree(() => _worktreeService.RemoveWorktree(_repoPath!, row.Path));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    // Best-effort lookup of a remote's fetch URL to prefill the edit prompt;
    // returns empty when unavailable (the prompt then starts blank).
    private string FindRemoteUrl(string remote)
    {
        try
        {
            return _repoPath is { Length: > 0 } repo
                ? _remoteService.ListRemotes(repo).FirstOrDefault(r => r.Name == remote)?.FetchUrl ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // --- Mutation plumbing ------------------------------------------------

    private void RunMutation(Func<BranchTagResult> work)
    {
        if (_repoPath is not { Length: > 0 })
        {
            return;
        }

        if (_busy)
        {
            NotifyBusy();
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            bool success;
            try
            {
                success = work().Success;
            }
            catch
            {
                success = false;
            }
            finally
            {
                // Cleared as soon as the work returns rather than only inside the Post
                // below: when that Post was the sole reset, anything that stopped it from
                // being delivered left _busy stuck at true, and from then on EVERY entry
                // of the tree's context menu was refused in silence.
                _busy = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (success)
                {
                    OperationCompleted?.Invoke();
                    Refresh();
                }
            });
        });
    }

    private void RunStash(Func<StashOpResult> work)
    {
        if (_repoPath is not { Length: > 0 })
        {
            return;
        }

        if (_busy)
        {
            NotifyBusy();
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            bool success;
            try
            {
                success = work().Success;
            }
            catch
            {
                success = false;
            }
            finally
            {
                // Cleared as soon as the work returns rather than only inside the Post
                // below: when that Post was the sole reset, anything that stopped it from
                // being delivered left _busy stuck at true, and from then on EVERY entry
                // of the tree's context menu was refused in silence.
                _busy = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (success)
                {
                    OperationCompleted?.Invoke();
                    Refresh();
                }

                // `stash apply`/`pop` merges, so it can stop on conflicts.
                _ = AskAboutStashConflictsAsync();
            });
        });
    }

    private async Task AskAboutStashConflictsAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo
                || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            if (await ConflictFlow.HandleAsync(owner, repo) is { HadConflicts: true })
            {
                OperationCompleted?.Invoke();
                Refresh();
            }
        }
        catch
        {
            // Never throw out of an interaction handler.
        }
    }

    private void RunRemote(Func<RemoteOpResult> work)
    {
        if (_repoPath is not { Length: > 0 })
        {
            return;
        }

        if (_busy)
        {
            NotifyBusy();
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            bool success;
            try
            {
                success = work().Success;
            }
            catch
            {
                success = false;
            }
            finally
            {
                // Cleared as soon as the work returns rather than only inside the Post
                // below: when that Post was the sole reset, anything that stopped it from
                // being delivered left _busy stuck at true, and from then on EVERY entry
                // of the tree's context menu was refused in silence.
                _busy = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (success)
                {
                    OperationCompleted?.Invoke();
                    Refresh();
                }
            });
        });
    }

    private void RunSubmodule(Func<SubmoduleOpResult> work)
    {
        if (_repoPath is not { Length: > 0 })
        {
            return;
        }

        if (_busy)
        {
            NotifyBusy();
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            bool success;
            try
            {
                success = work().Success;
            }
            catch
            {
                success = false;
            }
            finally
            {
                // Cleared as soon as the work returns rather than only inside the Post
                // below: when that Post was the sole reset, anything that stopped it from
                // being delivered left _busy stuck at true, and from then on EVERY entry
                // of the tree's context menu was refused in silence.
                _busy = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (success)
                {
                    OperationCompleted?.Invoke();
                    Refresh();
                }
            });
        });
    }

    private void RunWorktree(Func<WorktreeOpResult> work)
    {
        if (_repoPath is not { Length: > 0 })
        {
            return;
        }

        if (_busy)
        {
            NotifyBusy();
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            bool success;
            try
            {
                success = work().Success;
            }
            catch
            {
                success = false;
            }
            finally
            {
                // Cleared as soon as the work returns rather than only inside the Post
                // below: when that Post was the sole reset, anything that stopped it from
                // being delivered left _busy stuck at true, and from then on EVERY entry
                // of the tree's context menu was refused in silence.
                _busy = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (success)
                {
                    OperationCompleted?.Invoke();
                    Refresh();
                }
            });
        });
    }

    /// <summary>
    ///  Tells the user that the action was refused because another git operation is still
    ///  running. Every <c>_busy</c> guard in this control used to be a bare
    ///  <c>return</c>, so a menu click, a double click or an F-key simply did nothing and
    ///  looked broken — no action of the user's may end in the void.
    ///  <para>Why a modal and not a status line: this control has no status surface at all
    ///  (hence the recurring "No status surface on this control" comments), and disabling
    ///  the menu items instead is not usable either — the context menu is rebuilt per
    ///  click, so the greyed-out entries would carry no explanation. The small modal is
    ///  the surface the tree already uses to talk to the user, in
    ///  <see cref="ConfirmAsync"/> and <see cref="PromptAsync"/>, so the refusal reuses
    ///  it verbatim.</para>
    /// </summary>
    private void NotifyBusy() => _ = NotifyBusyAsync();

    private async Task NotifyBusyAsync()
    {
        try
        {
            // Only ever one notice at a time: an impatient double click on a blocked menu
            // entry must not stack modals on top of each other.
            if (_busyNoticeOpen || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            _busyNoticeOpen = true;
            try
            {
                Button ok = new() { Content = T("OK"), HorizontalAlignment = HorizontalAlignment.Right };
                Theming.ZoomWindow dialog = new()
                {
                    Title = T("TranslatedStrings/_error.Text", "Error"),
                    Width = 340,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = Brush("App.Panel", Brushes.DimGray),
                };
                ok.Click += (_, _) => dialog.Close();

                StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
                content.Children.Add(new TextBlock
                {
                    Text = T("Another Git operation is still running. Please wait for it to finish and try again."),
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(ok);
                dialog.Content = content;

                await dialog.ShowDialog(owner);
            }
            finally
            {
                _busyNoticeOpen = false;
            }
        }
        catch
        {
            // Never throw out of a refusal notice.
        }
    }

    // Minimal modal yes/no confirmation; allows the action when no owner window
    // is available (e.g. headless).
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return true;
        }

        TaskCompletionSource<bool> tcs = new();

        // Yes/No rather than the former Confirm/Cancel: both words have a real
        // trans-unit upstream, and it matches the shell's own confirmation dialog.
        Button yes = new() { Content = T("TranslatedStrings/_yes.Text", "Yes"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("TranslatedStrings/_no.Text", "No") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Confirm"),
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    // Minimal modal text prompt mirroring ConfirmAsync; returns the entered text,
    // or null when cancelled / no owner window is available (e.g. headless).
    // <paramref name="title"/> is the window caption: it used to be hard-coded to
    // "Rename", which was wrong for the worktree and remote-URL prompts and had no
    // sensible translation key either.
    private async Task<string?> PromptAsync(string message, string initial, string title)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        TaskCompletionSource<string?> tcs = new();

        TextBox input = new() { Text = initial };
        Button ok = new() { Content = T("OK"), Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            Title = title,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
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
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // Short alias for the composite-format overload; data (branch, remote, stash
    // names) always goes through a placeholder, never string concatenation.
    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(null, englishFormat, args);

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private sealed record RepoSnapshot(BranchTagListing Refs, IReadOnlyList<StashRow> Stashes, SubmoduleHierarchy Submodules, IReadOnlyList<WorktreeRow> Worktrees);
}
