using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Avalonia.Plugins;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Views;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Plugins;
using GitExtUtils;

namespace GitExtensions.Avalonia;

/// <summary>A minimal ICommand for wiring key bindings to void actions.</summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

/// <summary>
///  Integrated main window modelled on the original GitExtensions FormBrowse:
///  a top toolbar, a left repository-objects tree (branches/remotes/tags/
///  stashes), the revision-grid DAG in the centre, a bottom detail/diff panel,
///  and a status bar. All views are self-contained
///  <see cref="UserControl"/>s driven over the reused core via <see cref="GitContext"/>.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MainMenu _menu = new();
    private readonly MainToolbar _toolbar = new();
    private readonly StatusBarView _statusBar = new();
    private readonly RepoObjectsTree _tree = new();
    private readonly RevisionGridView _revisions = new();
    private readonly CommitDetailView _detail = new();
    private readonly DiffView _diff = new();
    private readonly FileTreeView _fileTree = new();
    private readonly GpgView _gpg = new();
    private readonly ConsoleView _console = new();
    private readonly OutputView _output = new();
    // No longer a bottom tab (the original FormBrowse has no "Working directory"
    // tab and the stage/commit flow lives in the modal commit form). It survives
    // as an on-demand utility window for the things the commit dialog does not
    // cover: merge-conflict resolution, clean, per-file discard/.gitignore.
    private readonly WorkingDirectoryView _workingDir = new();
    private Window? _workingDirWindow;

    private readonly TabControl _bottom;
    private readonly TabItem _commitInfoTab;
    private readonly TabItem _diffTab;
    private readonly TabItem _fileTreeTab;
    private readonly TabItem _gpgTab;
    private readonly TabItem _consoleTab;
    private readonly TabItem _outputTab;
    private readonly TabItem _stashTab;
    private readonly TabItem _blameTab;
    private readonly TabItem _historyTab;
    private readonly BlameView _blame = new();
    private readonly FileHistoryView _fileHistory = new();
    private readonly StashPanel _stash = new();

    private readonly StashOpsService _stashOps = new();
    private readonly ExternalToolService _externalTools = new();
    private readonly BisectService _bisect = new();

    private readonly UiStateService _uiStateService = new();
    private readonly UiState _uiState;
    private readonly FavoritesService _favoritesService = new();
    private readonly DashboardView _dashboard = new();

    // The repository work area (tree | revision grid | detail/diff), and the root
    // dock panel it lives in. The window swaps between this and the dashboard by
    // replacing the dock panel's fill child.
    private Grid _repositoryArea = null!;
    private DockPanel _root = null!;
    private bool _dashboardShowing;

    // Splitter-driven definitions we persist/restore. The revision/bottom and
    // detail/diff definitions are recreated whenever the layout is rebuilt (split
    // orientation / commit-info position changes), so they are not readonly.
    private readonly ColumnDefinition _treeCol;
    private RowDefinition _revRow;
    private RowDefinition _bottomRow;
    private RowDefinition _detailRow;
    private RowDefinition _diffRow;

    // While split view is on the detail/diff pair is laid out in COLUMNS, so the
    // live sizes come from these definitions instead of the rows above; they are
    // folded back into _detailRow/_diffRow (the persistence carriers) whenever the
    // layout is rebuilt or saved. Null while split view is off.
    private ColumnDefinition? _detailCol;
    private ColumnDefinition? _diffCol;

    // The rebuildable right-hand region (revision grid + bottom panel, with the
    // commit-info panel positioned relative to the grid) and its layout state.
    private readonly Grid _right = new();
    private CommitInfoPosition _commitInfoPosition;
    private bool _splitHorizontal;

    private string? _repoPath;
    private string? _lastSelectedHash;

    // The commit chosen as the "BASE" for the grid's Compare actions (single-select
    // grid, so BASE + "Compare to BASE" together stand in for a two-commit compare).
    private string? _compareBaseHash;

    public MainWindow()
    {
        // Load persisted UI state first, and apply the remembered theme before
        // any App.* brushes are read below, so the window opens in that theme.
        _uiState = _uiStateService.Load();
        Theming.ThemeManager.Apply(_uiState.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);

        Title = "Git Extensions (Avalonia / Linux)";
        Width = _uiState.WindowWidth;
        Height = _uiState.WindowHeight;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        // Commit-info position is session-local (the original FormBrowse default:
        // below the graph). Split view IS persisted, so the bottom panel comes
        // back in the same shape the user left it in.
        _commitInfoPosition = CommitInfoPosition.BelowGraph;
        _splitHorizontal = _uiState.SplitView;

        // Detail/diff definitions are (re)created by RebuildRightRegion; seed them
        // here so PersistLayout has valid references before the first rebuild.
        _detailRow = new RowDefinition(new GridLength(_uiState.DetailStar, GridUnitType.Star));
        _diffRow = new RowDefinition(new GridLength(_uiState.DiffStar, GridUnitType.Star));
        _revRow = new RowDefinition(new GridLength(_uiState.RevisionsStar, GridUnitType.Star));
        _bottomRow = new RowDefinition(new GridLength(_uiState.BottomStar, GridUnitType.Star));

        // ---- bottom panel: the original FormBrowse tab strip
        //   Commit · Diff · File tree · GPG · Console · Output
        // followed by the extra Avalonia panels
        //   Stash · Blame · File history.
        // The Commit tab shows the commit DETAIL; the diff moved out to its own
        // Diff tab so both are visible at once.
        _commitInfoTab = new TabItem { Header = "Commit" };
        _diffTab = new TabItem { Header = "Diff", Content = _diff };
        _fileTreeTab = new TabItem { Header = "File tree", Content = _fileTree };
        _gpgTab = new TabItem { Header = "GPG", Content = _gpg };
        _consoleTab = new TabItem { Header = "Console", Content = _console };
        _outputTab = new TabItem { Header = "Output", Content = _output };
        _stashTab = new TabItem { Header = "Stash", Content = _stash };
        _blameTab = new TabItem { Header = "Blame", Content = _blame };
        _historyTab = new TabItem { Header = "File history", Content = _fileHistory };

        // The Console tab's "Open terminal here" button reuses the external-tool
        // terminal launcher against the current repository.
        _console.OpenTerminalRequested += () => WithRepo(p => _externalTools.OpenTerminal(p));

        _bottom = new TabControl
        {
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            ClipToBounds = true,
            Items =
            {
                _commitInfoTab, _diffTab, _fileTreeTab, _gpgTab, _consoleTab, _outputTab,
                _stashTab, _blameTab, _historyTab,
            },
        };

        // ---- right side: revision grid + bottom panel, with the commit-info panel
        // positioned relative to the grid (below / left / right). Built dynamically
        // so the split-view and commit-info-position toggles can rearrange it live.
        _right.ClipToBounds = true;
        _right.Background = (IBrush)Application.Current!.Resources["App.Window"]!;
        RebuildRightRegion();
        Grid right = _right;

        // ---- main area: left tree | right side
        _treeCol = new ColumnDefinition(new GridLength(_uiState.TreeWidth, GridUnitType.Pixel));
        Grid main = new()
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                _treeCol,
                new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
        };
        GridSplitter treeSplit = new() { Width = 4, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(_tree, 0);
        Grid.SetColumn(treeSplit, 1);
        Grid.SetColumn(right, 2);
        main.Children.Add(_tree);
        main.Children.Add(treeSplit);
        main.Children.Add(right);
        _repositoryArea = main;

        DockPanel root = new() { Background = (IBrush)Application.Current!.Resources["App.Window"]! };
        DockPanel.SetDock(_menu, Dock.Top);
        DockPanel.SetDock(_toolbar, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        root.Children.Add(_menu);
        root.Children.Add(_toolbar);
        root.Children.Add(_statusBar);
        root.Children.Add(main);
        _root = root;
        Content = root;

        // Global shortcuts: F5 refresh, Ctrl+O open.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5), Command = new RelayCommand(RefreshAll) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control), Command = new RelayCommand(() => _ = PickRepositoryAsync()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, KeyModifiers.Control | KeyModifiers.Shift), Command = new RelayCommand(OpenWorkingDirectoryWindow) });

        // The working-directory panel is not a tab any more; expose it from the
        // Commands menu, next to Commit, the way the original keeps "Reset
        // changes…" / "Clean working directory…" there.
        AddWorkingDirectoryMenuEntry();

        WireEvents();
        _toolbar.SetSplitView(_splitHorizontal);

        Opened += (_, _) =>
        {
            string? initial = FindRepositoryRoot(App.InitialRepoPath ?? Directory.GetCurrentDirectory());
            if (initial is not null)
            {
                OpenRepository(initial);
            }
            else
            {
                ShowDashboard();
            }
        };

        // Persist window size + splitter positions when the window closes.
        Closing += (_, _) => PersistLayout();
    }

    // Captures the current window size and splitter panel sizes and saves them.
    private void PersistLayout()
    {
        try
        {
            CaptureSplitStars();
            _uiState.SplitView = _splitHorizontal;
            _uiState.WindowWidth = Width;
            _uiState.WindowHeight = Height;
            _uiState.TreeWidth = _treeCol.Width.Value;
            _uiState.RevisionsStar = _revRow.Height.Value;
            _uiState.BottomStar = _bottomRow.Height.Value;
            _uiState.DetailStar = _detailRow.Height.Value;
            _uiState.DiffStar = _diffRow.Height.Value;
            _uiStateService.Save(_uiState);
        }
        catch
        {
            // Best-effort; never block window close on a persistence failure.
        }
    }

    // Rebuilds the right-hand region (revision grid + bottom panel) for the current
    // commit-info position and split orientation. Reuses the shared views, detaching
    // them from their previous parents first so they can be safely re-hosted.
    private void RebuildRightRegion()
    {
        CaptureSplitStars();
        Detach(_revisions);
        Detach(_detail);
        Detach(_diff);
        Detach(_bottom);

        // The Commit tab hosts the commit detail only when the commit info sits
        // below the graph; otherwise the detail moves beside the grid and the tab
        // shows a hint. With split view ON (and the detail below the graph) the
        // Commit tab hosts detail AND diff side by side, so the diff is pulled out
        // of its own tab for as long as that lasts.
        bool detailBelow = _commitInfoPosition == CommitInfoPosition.BelowGraph;
        _commitInfoTab.Content = BuildCommitTabContent(detailBelow);
        SyncDiffTab();

        _right.Children.Clear();
        _right.ColumnDefinitions.Clear();
        _right.RowDefinitions.Clear();

        // Preserve the current star sizes across the rebuild.
        _revRow = new RowDefinition(new GridLength(_revRow.Height.Value, GridUnitType.Star));
        _bottomRow = new RowDefinition(new GridLength(_bottomRow.Height.Value, GridUnitType.Star));
        _right.RowDefinitions.Add(_revRow);
        _right.RowDefinitions.Add(new RowDefinition(new GridLength(4, GridUnitType.Pixel)));
        _right.RowDefinitions.Add(_bottomRow);

        Control top = detailBelow ? _revisions : BuildGraphWithSideDetail();

        GridSplitter rightSplit = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(top, 0);
        Grid.SetRow(rightSplit, 1);
        Grid.SetRow(_bottom, 2);
        _right.Children.Add(top);
        _right.Children.Add(rightSplit);
        _right.Children.Add(_bottom);
    }

    // Places the revision grid and the commit-detail panel side by side, with the
    // detail on the left or right of the grid per the current position.
    private Control BuildGraphWithSideDetail()
    {
        bool detailLeft = _commitInfoPosition == CommitInfoPosition.LeftOfGraph;
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(new GridLength(detailLeft ? 1 : 2, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)),
                new ColumnDefinition(new GridLength(detailLeft ? 2 : 1, GridUnitType.Star)),
            },
            ClipToBounds = true,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
        };
        GridSplitter split = new() { Width = 4, VerticalAlignment = VerticalAlignment.Stretch };
        Control first = detailLeft ? _detail : _revisions;
        Control second = detailLeft ? _revisions : _detail;
        Grid.SetColumn(first, 0);
        Grid.SetColumn(split, 1);
        Grid.SetColumn(second, 2);
        grid.Children.Add(first);
        grid.Children.Add(split);
        grid.Children.Add(second);
        return grid;
    }

    // Builds the Commit tab body:
    //  * split view ON  (and the detail below the graph) → commit detail and diff
    //    side by side, separated by a draggable GridSplitter (the diff tab is
    //    removed while this lasts, see SyncDiffTab);
    //  * split view OFF → just the commit detail, the diff in its own tab;
    //  * detail beside the graph → a short hint (nothing to split here).
    // The detail/diff definitions are recreated from the current sizes so the
    // persisted split stays valid across a rebuild.
    private Control BuildCommitTabContent(bool includeDetail)
    {
        _detailRow = new RowDefinition(new GridLength(_detailRow.Height.Value, GridUnitType.Star));
        _diffRow = new RowDefinition(new GridLength(_diffRow.Height.Value, GridUnitType.Star));
        _detailCol = null;
        _diffCol = null;

        if (includeDetail && _splitHorizontal)
        {
            _detailCol = new ColumnDefinition(new GridLength(_detailRow.Height.Value, GridUnitType.Star));
            _diffCol = new ColumnDefinition(new GridLength(_diffRow.Height.Value, GridUnitType.Star));

            Grid split = new()
            {
                ColumnDefinitions = new ColumnDefinitions
                {
                    _detailCol,
                    new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)),
                    _diffCol,
                },
                ClipToBounds = true,
                Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            };
            GridSplitter bar = new() { Width = 4, VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetColumn(_detail, 0);
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(_diff, 2);
            split.Children.Add(_detail);
            split.Children.Add(bar);
            split.Children.Add(_diff);
            return split;
        }

        if (includeDetail)
        {
            return _detail;
        }

        return new TextBlock
        {
            Text = "Commit info is shown beside the graph. The diff is in the Diff tab.",
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (IBrush)Application.Current!.Resources["App.TextDim"]!,
        };
    }

    // True when the diff currently lives in the Commit tab's side-by-side split
    // rather than in its own Diff tab.
    private bool DiffInSplit => _detailCol is not null;

    // Adds or removes the Diff tab to match the current layout: while the diff is
    // shown next to the detail it must not also be a tab (one visual parent), and
    // when it goes back to being a tab the tab is restored in its original slot
    // (right after Commit). Every other tab is left untouched.
    private void SyncDiffTab()
    {
        if (DiffInSplit)
        {
            _diffTab.Content = null;
            if (_bottom.Items.Contains(_diffTab))
            {
                bool wasSelected = ReferenceEquals(_bottom.SelectedItem, _diffTab);
                _bottom.Items.Remove(_diffTab);
                if (wasSelected)
                {
                    _bottom.SelectedItem = _commitInfoTab;
                }
            }

            return;
        }

        _diffTab.Content = _diff;
        if (!_bottom.Items.Contains(_diffTab))
        {
            int at = _bottom.Items.IndexOf(_commitInfoTab);
            _bottom.Items.Insert(at < 0 ? 0 : at + 1, _diffTab);
        }
    }

    // Brings the diff into view: its own tab normally, the Commit tab when the
    // diff is sharing that tab with the commit detail in split view.
    private void FocusDiff() => _bottom.SelectedItem = DiffInSplit ? _commitInfoTab : _diffTab;

    // Folds the live column sizes of an active split back into the row definitions
    // that PersistLayout saves, so a drag of the split bar survives a rebuild and
    // the next app start.
    private void CaptureSplitStars()
    {
        if (_detailCol is not null)
        {
            _detailRow = new RowDefinition(new GridLength(_detailCol.Width.Value, GridUnitType.Star));
        }

        if (_diffCol is not null)
        {
            _diffRow = new RowDefinition(new GridLength(_diffCol.Width.Value, GridUnitType.Star));
        }
    }

    // Detaches a control from its current parent so it can be re-hosted elsewhere
    // (Avalonia forbids adding a control that still has a visual parent).
    private static void Detach(Control c)
    {
        switch (c.Parent)
        {
            case Panel p:
                p.Children.Remove(c);
                break;
            case ContentControl cc:
                cc.Content = null;
                break;
            case Decorator d:
                d.Child = null;
                break;
        }
    }

    // Flips the bottom panel between "detail and diff side by side in the Commit
    // tab" and "diff in its own tab". The choice is persisted (UiState.SplitView)
    // and restored on the next start.
    private void ToggleSplitView()
    {
        _splitHorizontal = !_splitHorizontal;
        RebuildRightRegion();
        _bottom.SelectedItem = _commitInfoTab;
        _toolbar.SetSplitView(_splitHorizontal);
        _uiState.SplitView = _splitHorizontal;
        _statusBar.SetText(_splitHorizontal
            ? (DiffInSplit
                ? "Split view: commit detail and diff side by side"
                : "Split view on (applies with the commit info below the graph)")
            : "Split view off: the diff is in its own tab");
    }

    // Repositions the commit-info (detail) panel relative to the revision grid.
    private void SetCommitInfoPosition(CommitInfoPosition position)
    {
        if (_commitInfoPosition == position)
        {
            return;
        }

        _commitInfoPosition = position;
        RebuildRightRegion();
        _statusBar.SetText(position switch
        {
            CommitInfoPosition.LeftOfGraph => "Commit info: left of graph",
            CommitInfoPosition.RightOfGraph => "Commit info: right of graph",
            _ => "Commit info: below graph",
        });
    }

    private void WireEvents()
    {
        _revisions.RevisionSelected += OnRevisionSelected;
        _revisions.RangeSelected += OnRangeSelected;
        // The two artificial top rows both open the commit dialog on the repo.
        _revisions.WorkingDirectorySelected += () =>
        {
            if (_repoPath is not null) _ = CommitDialog.ShowAsync(this, _repoPath, RefreshAll);
        };
        _revisions.CommitIndexSelected += () =>
        {
            if (_repoPath is not null) _ = CommitDialog.ShowAsync(this, _repoPath, RefreshAll);
        };
        _fileHistory.RevisionSelected += OnRevisionSelected;
        // Parent/child hash links in the commit detail navigate the grid: select the
        // target row (best-effort) and refresh detail/diff/filetree/gpg for it.
        _detail.CommitNavigated += h =>
        {
            if (_repoPath is not null)
            {
                _revisions.SelectCommit(h);
                OnRevisionSelected(h);
            }
        };
        _workingDir.Committed += RefreshAll;
        _stash.OperationCompleted += RefreshAll;
        _tree.OperationCompleted += RefreshAll;
        _tree.RefSelected += OnRevisionSelected;
        _tree.OpenRepositoryRequested += OpenRepositoryPath;

        _dashboard.RepositorySelected += repo =>
        {
            if (Directory.Exists(repo))
            {
                OpenRepository(repo);
            }
            else
            {
                _statusBar.SetText($"Repository no longer exists: {repo}");
            }
        };
        _dashboard.OpenOtherRequested += () => _ = PickRepositoryAsync();

        _diff.BlameRequested += path => ShowInBottom(_blameTab, () => _blame.ShowBlame(_repoPath!, path));
        _diff.FileHistoryRequested += path => ShowInBottom(_historyTab, () => _fileHistory.ShowHistory(_repoPath!, path));

        // Toolbar actions.
        _toolbar.OpenRepoRequested += () => _ = PickRepositoryAsync();
        _toolbar.RefreshRequested += RefreshAll;
        _toolbar.CommitRequested += OpenCommitDialog;
        _toolbar.FetchRequested += () => RunRemoteOp("Fetch", (s, r, emit, creds) => s.FetchStreaming(_repoPath!, r, emit, creds));
        _toolbar.PullRequested += () => RunRemoteOp("Pull", (s, r, emit, creds) => s.PullStreaming(_repoPath!, r, rebase: false, emit, creds));
        _toolbar.PushRequested += OpenPushDialog;
        _toolbar.StashRequested += () => RunOp("Stash", () => _stashOps.StashSave(_repoPath!, "WIP", includeUntracked: false).Success);
        _toolbar.NewBranchRequested += () => _ = NewBranchAsync();

        // View / layout + external-tool toolbar actions.
        _toolbar.SplitViewToggleRequested += ToggleSplitView;
        _toolbar.CommitInfoPositionChanged += SetCommitInfoPosition;
        _toolbar.FileExplorerRequested += () => WithRepo(p => _externalTools.OpenPath(p));
        _toolbar.OpenTerminalRequested += () => WithRepo(p => _externalTools.OpenTerminal(p));

        // Right-side branch-scope + filter selectors: drive the revision grid's own
        // scope/filter logic (the grid's header menu keeps working independently).
        _toolbar.BranchScopeChanged += i => { if (_repoPath is not null) _revisions.SetBranchScope((BranchScope)i); };
        _toolbar.FilterChanged += t => { if (_repoPath is not null) _revisions.ApplyFilter(t); };

        // Submodules / worktrees split-button dropdowns. Providers list off the UI
        // thread; choosing an entry opens that path as the active repository. The
        // submodules list is prefixed with a "level-up" entry when the current repo
        // is itself a submodule/subdir of a parent (super-project).
        _toolbar.SubmodulesProvider = () => Task.Run<IReadOnlyList<RepoLink>>(() =>
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return Array.Empty<RepoLink>();
            }

            List<RepoLink> links = [];
            if (FindSuperproject(repo) is { Length: > 0 } parent)
            {
                links.Add(new RepoLink($"⬆ Parent super-project ({Path.GetFileName(parent.TrimEnd('/', '\\'))})", parent, "NavigateUp"));
            }

            foreach (SubmoduleRow row in new SubmoduleService().ListSubmodules(repo))
            {
                string full = Path.GetFullPath(Path.Combine(repo, row.Path));
                links.Add(new RepoLink(row.Display, full, "FolderSubmodule"));
            }

            return links;
        });
        _toolbar.WorktreesProvider = () => Task.Run<IReadOnlyList<RepoLink>>(() =>
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return Array.Empty<RepoLink>();
            }

            List<RepoLink> links = [];
            foreach (WorktreeRow row in new WorktreeService().ListWorktrees(repo))
            {
                links.Add(new RepoLink(row.Display, row.Path, "WorkTree"));
            }

            return links;
        });
        _toolbar.OpenRepositoryRequested += OpenRepositoryPath;

        // Inline branch dropdown: list the local branch names off the UI thread;
        // choosing one checks it out (off the UI thread) and refreshes.
        _toolbar.BranchesProvider = () => Task.Run<IReadOnlyList<string>>(() =>
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return Array.Empty<string>();
            }

            BranchTagListing refs = new BranchTagService().LoadRefs(repo);
            return refs.Branches.Where(b => !b.IsRemote).Select(b => b.Name).ToList();
        });
        _toolbar.BranchCheckoutRequested += name =>
        {
            if (_repoPath is not null)
            {
                RunOp($"Checkout {name}", () => new BranchTagService().Checkout(_repoPath!, name).Success);
            }
        };

        // Inline repo-path dropdown: list recent repositories off the UI thread;
        // choosing one opens it as the active repository. The service already
        // normalises, de-duplicates and prunes dead/ephemeral entries.
        _toolbar.RecentReposProvider = () => Task.Run<IReadOnlyList<RepoLink>>(async () =>
        {
            IReadOnlyList<string> recent = await new RecentRepositoriesService().LoadAsync();
            return recent
                .Select(p => new RepoLink(Path.GetFileName(p) is { Length: > 0 } name ? name : p, p, "RepoOpen"))
                .ToList();
        });

        // Menu actions (mirror the toolbar + menu-only entries).
        _menu.OpenRepoRequested += () => _ = PickRepositoryAsync();
        _menu.CloneRequested += () => _ = CloneRepositoryAsync();
        _menu.InitRequested += () => _ = InitRepositoryAsync();
        _menu.OpenRecentRequested += repo => { if (Directory.Exists(repo)) OpenRepository(repo); };
        _menu.OpenFavoriteRequested += repo =>
        {
            if (Directory.Exists(repo))
            {
                OpenRepository(repo);
            }
            else
            {
                _statusBar.SetText($"Favorite no longer exists: {repo}");
            }
        };
        _menu.AddFavoriteRequested += AddCurrentToFavorites;
        _menu.DashboardRequested += ShowDashboard;
        _menu.ExitRequested += Close;
        _menu.RefreshRequested += RefreshAll;
        _menu.ShowReflogRequested += () => _ = ShowReflogAsync();
        _menu.LightThemeRequested += () =>
        {
            Theming.ThemeManager.Apply(ThemeVariant.Light);
            _uiState.Theme = "Light";
            _uiStateService.Save(_uiState);
        };
        _menu.DarkThemeRequested += () =>
        {
            Theming.ThemeManager.Apply(ThemeVariant.Dark);
            _uiState.Theme = "Dark";
            _uiStateService.Save(_uiState);
        };
        _menu.FetchRequested += () => RunRemoteOp("Fetch", (s, r, emit, creds) => s.FetchStreaming(_repoPath!, r, emit, creds));
        _menu.PullRequested += () => RunRemoteOp("Pull", (s, r, emit, creds) => s.PullStreaming(_repoPath!, r, rebase: false, emit, creds));
        _menu.PushRequested += OpenPushDialog;
        _menu.CommitRequested += OpenCommitDialog;
        _menu.StashRequested += () => RunOp("Stash", () => _stashOps.StashSave(_repoPath!, "WIP", includeUntracked: false).Success);
        _menu.UndoLastCommitRequested += () => _ = UndoLastCommitAsync();
        _menu.ResetChangesRequested += () => _ = ResetChangesAsync();
        _menu.CleanWorkingDirectoryRequested += () => _ = CleanWorkingDirectoryAsync();
        _menu.NewBranchRequested += () => _ = NewBranchAsync();
        _menu.NewTagRequested += () => _ = NewTagAsync();
        _menu.FormatPatchRequested += () => _ = FormatPatchAsync();
        _menu.ApplyPatchRequested += () => _ = ApplyPatchAsync();
        _menu.ViewPatchRequested += () => _ = ViewPatchAsync();
        _menu.CopyHashRequested += () =>
        {
            if (_lastSelectedHash is { Length: > 0 } h)
            {
                _ = Clipboard?.SetTextAsync(h);
            }
        };
        _menu.AboutRequested += () => _ = AboutDialog.ShowAsync(this);
        _menu.SettingsRequested += () => _ = OpenSettingsAsync();

        // Plugins: run a plugin (off-thread) / open its settings editor.
        _menu.PluginRunRequested += plugin => RunPlugin(plugin);
        _menu.PluginSettingsRequested += plugin => _ = OpenPluginSettingsAsync(plugin);

        // Repository: file explorer + edit repo config files (created if absent).
        _menu.FileExplorerRequested += () => WithRepo(p => _externalTools.OpenPath(p));
        _menu.EditGitignoreRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".gitignore")));
        _menu.EditGitattributesRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".gitattributes")));
        _menu.EditMailmapRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".mailmap")));
        _menu.EditInfoExcludeRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".git", "info", "exclude")));
        _menu.GitMaintenanceRequested += () => _ = OpenMaintenanceAsync();
        _menu.SparseCheckoutRequested += () => _ = OpenSparseAsync();
        _menu.RepoSettingsRequested += () => _ = OpenSettingsAsync();

        // Tools: terminal + external git GUIs, launched detached in the repo dir.
        _menu.GitBashRequested += () => WithRepo(p => _externalTools.OpenTerminal(p));
        _menu.GitKRequested += () => WithRepo(p => _externalTools.LaunchDetached("gitk", Array.Empty<string>(), p, "Launched gitk"));
        _menu.GitGuiRequested += () => WithRepo(p => _externalTools.LaunchDetached("git", new[] { "gui" }, p, "Launched git gui"));
        _menu.GitCommandLogRequested += () => _ = ShowCommandLogAsync();

        // Help: external documentation / project links (no repo required).
        _menu.UserManualRequested += () => Surface(_externalTools.OpenUrl("https://git-extensions-documentation.readthedocs.io/"));
        _menu.ReportIssueRequested += () => Surface(_externalTools.OpenUrl("https://github.com/gitextensions/gitextensions/issues"));
        _menu.ChangelogRequested += () => Surface(_externalTools.OpenUrl("https://github.com/gitextensions/gitextensions/releases"));
        _menu.DonateRequested += () => Surface(_externalTools.OpenUrl("https://opencollective.com/gitextensions"));

        // Commit-targeted operations on the revision grid.
        _revisions.AddCommitCommand("Checkout this commit",
            hash => RunOp("Checkout", () => new BranchTagService().Checkout(_repoPath!, hash).Success));
        _revisions.AddCommitCommand("Cherry-pick",
            hash => RunOp("Cherry-pick", () => _stashOps.CherryPick(_repoPath!, hash).Success));
        _revisions.AddCommitCommand("Reset (soft) to here",
            hash => RunOp("Reset soft", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Soft).Success));
        _revisions.AddCommitCommand("Reset (mixed) to here",
            hash => RunOp("Reset mixed", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Mixed).Success));
        _revisions.AddCommitCommand("Reset (HARD) to here…",
            hash => RunOp("Reset hard", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Hard).Success, confirm: true));
        _revisions.AddCommitCommand("Create branch here…", hash => _ = CreateBranchHereAsync(hash));
        _revisions.AddCommitCommand("Create tag here…", hash => _ = CreateTagHereAsync(hash));
        _revisions.AddCommitCommand("Revert this commit…", RevertThisCommit);
        _revisions.AddCommitCommand("Archive this commit…", hash => _ = ArchiveThisCommitAsync(hash));

        // History-rewriting commit edits on the current branch. Each is guarded by a
        // dirty-tree refusal + a confirm dialog, and rebase-backed paths abort cleanly
        // on failure so the repository is never left mid-rebase (see CommitEditService).
        _revisions.AddCommitCommand("Reword commit…", hash => _ = RewordCommitAsync(hash));
        _revisions.AddCommitCommand("Squash with previous…", hash => _ = SquashOrFixupAsync(hash, squash: true));
        _revisions.AddCommitCommand("Fixup with previous…", hash => _ = SquashOrFixupAsync(hash, squash: false));

        // Compare actions. The grid is single-select, so we mirror the original's
        // two-commit compare with a remembered BASE + "Compare to BASE" pair, plus
        // a direct commit-vs-working-tree compare. Results drive the shared DiffView.
        _revisions.AddCommitCommand("Select as BASE to compare", SelectCompareBase);
        _revisions.AddCommitCommand("Compare to BASE", CompareToBase);
        _revisions.AddCommitCommand("Compare to working directory", CompareToWorkingDirectory);
        _revisions.AddCommitCommand("Compare to branch…", hash => _ = CompareToBranchAsync(hash));

        // Bisect: mark the selected commit good/bad/skip (auto-starting a session
        // if none is in progress), plus a stop/reset entry. Each surfaces git's
        // output — the next commit to test, or the final "first bad commit".
        _revisions.AddCommitCommand("Bisect: mark good",
            hash => RunBisect("Bisect good", () => _bisect.MarkGood(_repoPath!, hash), ensureStarted: true));
        _revisions.AddCommitCommand("Bisect: mark bad",
            hash => RunBisect("Bisect bad", () => _bisect.MarkBad(_repoPath!, hash), ensureStarted: true));
        _revisions.AddCommitCommand("Bisect: skip",
            hash => RunBisect("Bisect skip", () => _bisect.Skip(_repoPath!, hash), ensureStarted: true));
        _revisions.AddCommitCommand("Bisect: stop/reset",
            _ => RunBisect("Bisect reset", () => _bisect.Reset(_repoPath!), ensureStarted: false));
    }

    // Opens the modal reflog browser; on a checkout from it, refreshes the main
    // view. Mirrors the other dialog-launch helpers (e.g. RemotesDialog).
    private async Task ShowReflogAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        ReflogWindow window = new(_repoPath);
        await window.ShowDialog(this);

        if (window.CheckedOut)
        {
            RefreshAll();
        }
    }

    // Runs a bisect step off the UI thread, optionally auto-starting a session
    // first, then surfaces git's output (next commit to test / first bad commit)
    // in the status bar and refreshes the grid. Never throws.
    private void RunBisect(string label, Func<BisectResult> op, bool ensureStarted)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            _statusBar.SetText($"{label}…");
            BisectResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    if (ensureStarted && !_bisect.IsInProgress(_repoPath!))
                    {
                        BisectResult start = _bisect.Start(_repoPath!);
                        if (!start.Success)
                        {
                            return start;
                        }
                    }

                    return op();
                });
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"{label} failed: {ex.Message}");
                return;
            }

            RefreshAll();

            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText(result.Success
                ? (firstLine.Length > 0 ? $"{label}: {firstLine}" : $"{label} done.")
                : $"{label} failed: {firstLine}");
        }
    }

    // Reverts the selected commit on the current branch (git revert --no-edit).
    // Reuses the RunOp refresh pattern via the output-surfacing overload so a
    // revert that stops on a conflict shows the git output instead of crashing.
    private void RevertThisCommit(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        RunOp("Revert", () => new RevertArchiveService().Revert(_repoPath!, hash));
    }

    // Opens the archive dialog for the selected commit; on success reports the
    // written path in the status bar.
    private async Task ArchiveThisCommitAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        ArchiveDialog dlg = new(_repoPath, hash);
        await dlg.ShowDialog(this);

        if (dlg.ArchivedPath is { Length: > 0 } path)
        {
            string shortHash = hash.Length > 8 ? hash[..8] : hash;
            _statusBar.SetText($"Archived {shortHash} → {path}");
        }
    }

    // ---- commit editing (reword / squash / fixup) ----------------------------------

    // Shared safety gate for every history-rewriting edit: refuses when the working
    // tree is dirty (with a clear message) and otherwise asks the user to confirm the
    // rewrite. Returns true only when it is safe to proceed.
    private async Task<bool> GuardRewriteAsync(string label)
    {
        CommitEditService svc = new();
        bool dirty;
        try
        {
            dirty = await Task.Run(() => svc.IsWorkingTreeDirty(_repoPath!));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"{label} failed: {ex.Message}");
            return false;
        }

        if (dirty)
        {
            _statusBar.SetText($"{label} refused: you have uncommitted changes. Commit or stash them first.");
            return false;
        }

        return await ConfirmAsync("This rewrites history on the current branch. Continue?");
    }

    // Runs a commit-edit operation off the UI thread, then refreshes the grid and
    // surfaces success or the first line of git's output on failure. The service
    // already aborts a stuck rebase, so this never leaves a half-rebase behind.
    private async Task RunEditAsync(string label, Func<CommitEditResult> op)
    {
        _statusBar.SetText($"{label}…");
        CommitEditResult result;
        try
        {
            result = await Task.Run(op);
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"{label} failed: {ex.Message}");
            return;
        }

        RefreshAll();
        if (result.Success)
        {
            _statusBar.SetText($"{label} done.");
        }
        else
        {
            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText($"{label} failed: {firstLine}");
        }
    }

    // Rewords the selected commit. HEAD is a plain `git commit --amend -m`; an older
    // commit uses a scripted non-interactive reword rebase. Prefills the current
    // message in a multi-line prompt.
    private async Task RewordCommitAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        if (!await GuardRewriteAsync("Reword"))
        {
            return;
        }

        CommitEditService svc = new();
        bool isHead;
        string current;
        try
        {
            isHead = await Task.Run(() => svc.IsHead(_repoPath!, hash));
            current = await Task.Run(() => svc.GetCommitMessage(_repoPath!, hash));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Reword failed: {ex.Message}");
            return;
        }

        string? message = await PromptAsync("Reword commit", "New commit message:", current.Trim(), multiline: true);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await RunEditAsync("Reword",
            () => isHead ? svc.AmendHead(_repoPath!, message) : svc.Reword(_repoPath!, hash, message));
    }

    // Squashes (prompting for a combined message) or fixes up (discarding the message)
    // the selected commit into its parent. Refuses on the root commit, which has no
    // previous commit to combine with.
    private async Task SquashOrFixupAsync(string hash, bool squash)
    {
        if (_repoPath is null)
        {
            return;
        }

        string label = squash ? "Squash" : "Fixup";
        CommitEditService svc = new();

        bool hasParent;
        try
        {
            hasParent = await Task.Run(() => svc.HasParent(_repoPath!, hash));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"{label} failed: {ex.Message}");
            return;
        }

        if (!hasParent)
        {
            _statusBar.SetText($"{label} not possible: the root commit has no previous commit to combine with.");
            return;
        }

        if (!await GuardRewriteAsync(label))
        {
            return;
        }

        if (squash)
        {
            string combined;
            try
            {
                combined = await Task.Run(() => svc.GetCombinedMessage(_repoPath!, hash));
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"Squash failed: {ex.Message}");
                return;
            }

            string? message = await PromptAsync("Squash with previous", "Combined commit message:", combined.Trim(), multiline: true);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            await RunEditAsync("Squash", () => svc.Squash(_repoPath!, hash, message));
        }
        else
        {
            await RunEditAsync("Fixup", () => svc.Fixup(_repoPath!, hash));
        }
    }

    // Prompts for a branch name and creates it at the selected commit.
    private async Task CreateBranchHereAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("Create branch", "Branch name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        RunOp($"Create branch {name}",
            () => new BranchTagService().CreateBranch(_repoPath!, name.Trim(), startPoint: hash, checkout: false).Success);
    }

    // Prompts for a tag name (and optional message) and creates it at the commit.
    private async Task CreateTagHereAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("Create tag", "Tag name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string? message = await PromptAsync("Create tag", "Message (leave blank for a lightweight tag):");

        RunOp($"Create tag {name}",
            () => new BranchTagService().CreateTag(_repoPath!, name.Trim(), commit: hash, message?.Trim() ?? string.Empty).Success);
    }

    // Runs an external-tool action that needs the current repo, surfacing its
    // result (or a "no repository" note) in the status bar. Never throws: the
    // service catches launch failures and returns them as a failed result.
    private void WithRepo(Func<string, ExternalToolResult> action)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        Surface(action(_repoPath));
    }

    // Reflects an external-tool result in the status bar; failures are reported
    // as text rather than thrown, so a missing tool never crashes the UI.
    private void Surface(ExternalToolResult result) => _statusBar.SetText(result.Message);

    private void OnRevisionSelected(string commitHash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _lastSelectedHash = commitHash;
        _detail.ShowCommit(_repoPath, commitHash);
        _diff.ShowCommit(_repoPath, commitHash);
        _fileTree.ShowCommit(_repoPath, commitHash);
        _gpg.ShowCommit(_repoPath, commitHash);
        _bottom.SelectedItem = _commitInfoTab;
    }

    // Two commits selected in the grid: show the diff between them (baseHash is the
    // older side, otherHash the newer) in the shared DiffView and reveal the tab.
    private void OnRangeSelected(string baseHash, string otherHash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _diff.ShowRange(_repoPath, baseHash, otherHash);
        FocusDiff();
        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = otherHash.Length > 8 ? otherHash[..8] : otherHash;
        _statusBar.SetText($"Comparing {shortBase}..{shortOther}");
    }

    // Remembers the chosen commit as the comparison BASE (the "old" side of a
    // later "Compare to BASE"). Reported in the status bar as a short hash.
    private void SelectCompareBase(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _compareBaseHash = hash;
        string shortHash = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText($"Selected {shortHash} as compare BASE. Use \"Compare to BASE\" on another commit.");
    }

    // Diffs BASE..selected (BASE the "old" side) and renders the changed files +
    // per-file diffs in the shared DiffView. Hints in the status bar if no BASE set.
    private void CompareToBase(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        if (_compareBaseHash is not { Length: > 0 } baseHash)
        {
            _statusBar.SetText("No BASE selected. Right-click a commit and choose \"Select as BASE to compare\" first.");
            return;
        }

        _diff.ShowRange(_repoPath, baseHash, hash);
        FocusDiff();

        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText($"Comparing {shortBase} .. {shortOther}");
    }

    // Diffs the selected commit against the current working tree (git diff <hash>)
    // and renders the result in the shared DiffView.
    private void CompareToWorkingDirectory(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _diff.ShowAgainstWorkingDirectory(_repoPath, hash);
        FocusDiff();

        string shortHash = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText($"Comparing {shortHash} .. working tree");
    }

    // Lists local branches, lets the user pick one, then diffs that branch vs the
    // selected commit — git diff <branch> <selected> — reusing the shared DiffView
    // compare path (ShowRange), exactly like "Compare to BASE". The branch is the
    // "old" side, the selected commit the "new" side.
    private async Task CompareToBranchAsync(string hash)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        IReadOnlyList<BranchTagRow> localBranches;
        try
        {
            localBranches = await Task.Run(() => new BranchTagService().LoadRefs(_repoPath!)
                .Branches.Where(b => !b.IsRemote).ToList());
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Compare to branch failed: {ex.Message}");
            return;
        }

        if (localBranches.Count == 0)
        {
            _statusBar.SetText("No local branches to compare against.");
            return;
        }

        BranchTagRow? chosen = await PickBranchAsync(localBranches);
        if (chosen is null)
        {
            return;
        }

        // Prefer the branch's resolved ObjectId (so DiffView can parse it), falling
        // back to its name if the ref carried no object id.
        string baseRef = chosen.ObjectId is { Length: > 0 } oid ? oid : chosen.Name;

        _diff.ShowRange(_repoPath, baseRef, hash);
        FocusDiff();

        string shortOther = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText($"Comparing {chosen.Name} .. {shortOther}");
    }

    // Modal single-select branch picker; returns the chosen branch, or null on cancel.
    private async Task<BranchTagRow?> PickBranchAsync(IReadOnlyList<BranchTagRow> branches)
    {
        ListBox list = new()
        {
            ItemsSource = branches.Select(b => b.Name).ToList(),
            Background = (IBrush)Application.Current!.Resources["App.Control"]!,
            Foreground = (IBrush)Application.Current!.Resources["App.Text"]!,
            SelectedIndex = 0,
            MinHeight = 220,
        };

        Button ok = new() { Content = "Compare", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        Window dlg = new()
        {
            Title = "Compare to branch",
            Width = 420,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new DockPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = "Diff against branch (branch .. selected commit):", Margin = new Thickness(0, 0, 0, 6), [DockPanel.DockProperty] = Dock.Top },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { ok, cancel },
                        [DockPanel.DockProperty] = Dock.Bottom,
                    },
                    list,
                },
            },
        };

        BranchTagRow? result = null;
        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is string name)
            {
                result = branches.FirstOrDefault(b => b.Name == name);
            }

            dlg.Close();
        };
        list.DoubleTapped += (_, _) => ok.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // Opens the modal git command-log viewer (reads the process-global core log).
    private async Task ShowCommandLogAsync()
    {
        CommandLogWindow window = new();
        await window.ShowDialog(this);
    }

    // Opens the modal sparse-working-copy dialog; refreshes the main view if a
    // sparse operation changed the working tree.
    private async Task OpenSparseAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        SparseDialog dlg = new(_repoPath);
        await dlg.ShowDialog(this);

        if (dlg.Changed)
        {
            RefreshAll();
        }
    }

    private void ShowInBottom(TabItem tab, Action show)
    {
        if (_repoPath is not null)
        {
            show();
            _bottom.SelectedItem = tab;
        }
    }

    private void RefreshAll()
    {
        // When the dashboard is showing, "Refresh" (menu / F5) reloads its lists.
        if (_dashboardShowing)
        {
            ShowDashboard();
            return;
        }

        if (_repoPath is null)
        {
            return;
        }

        WarmUpCore(_repoPath);
        _revisions.LoadRepository(_repoPath);
        if (_workingDirWindow is not null)
        {
            _workingDir.LoadRepository(_repoPath);
        }

        _stash.LoadRepository(_repoPath);
        _tree.LoadRepository(_repoPath);
        _statusBar.LoadRepository(_repoPath);
        RefreshToolbarState();
    }

    // Opens the dedicated modal commit window (mirroring the original Git
    // Extensions commit form). This is now the ONLY staging/commit surface:
    // the redundant bottom "Working directory" tab was dropped. No-op without
    // a repo.
    private void OpenCommitDialog()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        _ = CommitDialog.ShowAsync(this, _repoPath, RefreshAll);
    }

    // Appends "Working directory…" to the existing Commands menu (found by
    // header so MainMenu itself stays untouched). If the menu is ever
    // restructured the lookup simply no-ops and the Ctrl+Shift+W shortcut
    // remains.
    private void AddWorkingDirectoryMenuEntry()
    {
        if (_menu.Content is not Menu bar)
        {
            return;
        }

        foreach (object? item in bar.Items)
        {
            if (item is MenuItem top && top.Header as string == "_Commands")
            {
                MenuItem entry = new() { Header = "Working directory…" };
                entry.Click += (_, _) => OpenWorkingDirectoryWindow();
                top.Items.Add(new Separator());
                top.Items.Add(entry);
                return;
            }
        }
    }

    // Shows the working-directory panel in an on-demand utility window. It is
    // NOT a second commit surface for its own sake: it carries the operations
    // the commit dialog does not implement (merge-conflict resolution via
    // mergetool / take ours / take theirs / mark resolved, clean untracked
    // files, per-file discard and .gitignore entries, undo last commit).
    private void OpenWorkingDirectoryWindow()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        if (_workingDirWindow is not null)
        {
            _workingDirWindow.Activate();
            _workingDir.LoadRepository(_repoPath);
            return;
        }

        Window window = new()
        {
            Title = "Working directory",
            Width = 1000,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = _workingDir,
        };
        window.Closed += (_, _) =>
        {
            // Detach so the panel can be re-hosted the next time it is opened.
            window.Content = null;
            _workingDirWindow = null;
        };

        _workingDirWindow = window;
        _workingDir.LoadRepository(_repoPath);
        window.Show(this);
    }

    // Opens the Push configuration dialog (remote/branch/force + Pull/Push),
    // replacing the previous immediate push. Refreshes the UI once it closes.
    private void OpenPushDialog()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        _ = PushDialog.ShowAsync(this, _repoPath)
            .ContinueWith(_ => Dispatcher.UIThread.Post(RefreshAll));
    }

    // Recomputes the dynamic toolbar state (ahead/behind, staged/unstaged) off
    // the UI thread, then pushes it to the toolbar on the UI thread. Fire-and-
    // forget so it never blocks a refresh; git errors are swallowed.
    private void RefreshToolbarState()
    {
        if (_repoPath is not { Length: > 0 } repoPath)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            int ahead = 0, behind = 0, staged = 0, unstaged = 0;
            string branch = string.Empty;
            try
            {
                // Mirrors StatusBarView.Compute (StatusBarView.cs lines ~87-104):
                //   branch   = module.GetSelectedBranch(emptyIfDetached: true)
                //   upstream = module.GetRemoteBranch(branch)
                //   ahead    = module.GetCommitCount("HEAD", upstream, throwOnErrorExit: false)
                //   behind   = module.GetCommitCount(upstream, "HEAD", throwOnErrorExit: false)
                GitCommands.GitModule module = GitContext.CreateModule(repoPath);
                branch = module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
                if (!string.IsNullOrEmpty(branch))
                {
                    string upstream = module.GetRemoteBranch(branch);
                    if (!string.IsNullOrEmpty(upstream))
                    {
                        ahead = module.GetCommitCount("HEAD", upstream, throwOnErrorExit: false) ?? 0;
                        behind = module.GetCommitCount(upstream, "HEAD", throwOnErrorExit: false) ?? 0;
                    }
                }

                WorkingDirStatus status = new WorkingDirectoryService().LoadStatus(repoPath);
                staged = status.Staged.Count;
                unstaged = status.Unstaged.Count;
            }
            catch
            {
                // Never throw out of a refresh.
            }

            int fStaged = staged, fUnstaged = unstaged;
            Dispatcher.UIThread.Post(() =>
            {
                _toolbar.UpdateState(ahead, behind, fStaged, fUnstaged, repoPath, branch);
                // Feed the artificial "Working directory" / "Commit index" rows
                // atop the revision grid the same pending-work counts.
                _revisions.SetWorkingState(fUnstaged, fStaged);
            });
        });
    }

    // Picks the remote (first configured, or "origin") and runs a remote op inside
    // a modal GitProcessDialog that shows the git command(s) + output + result,
    // then refreshes. Mirrors the original FormProcess behaviour.
    private void RunRemoteOp(string label, Func<RemoteService, string, Action<string>, GitCredentials?, RemoteOpResult> op)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            _statusBar.SetText($"{label}…");

            RemoteService svc = new();

            // Off the UI thread: ListRemotes shells out to git. Calling it here
            // froze the whole window before the process dialog even appeared.
            string repo = _repoPath;
            string remote = await Task.Run(() =>
            {
                var remotes = svc.ListRemotes(repo);
                return remotes.Count > 0 ? remotes[0].Name : "origin";
            });

            RemoteOpResult? res = null;
            await Views.GitProcessDialog.RunStreamingAsync(this, label, emit =>
            {
                res = op(svc, remote, emit, null);
                return new Views.GitProcessOutcome(res.Success, res.Output);
            }, closeOnAuthFailure: true);

            // Git ran non-interactively; on an auth failure ask for credentials
            // in-app and retry the SAME op feeding them through a transient
            // credential helper — never prompt on the launching terminal.
            if (res is { AuthFailed: true })
            {
                GitCredentials? creds = await CredentialsDialog.ShowAsync(this);
                if (creds is not null)
                {
                    await Views.GitProcessDialog.RunStreamingAsync(this, $"{label} (retry)", emit =>
                    {
                        RemoteOpResult r = op(svc, remote, emit, creds);
                        return new Views.GitProcessOutcome(r.Success, r.Output);
                    });
                }
            }

            RefreshAll();
        }
    }

    // Commands → "Undo last commit…" (FormBrowse undoLastCommitToolStripMenuItem).
    // WorkingDirectoryService.UndoLastCommit runs `git reset --soft HEAD~1`: the commit
    // disappears from history but every change it carried stays staged in the working
    // tree. Still destructive for history, so it is confirmed first. The parent check
    // and the HEAD summary are read in Task.Run — the service blocks on async work and
    // would deadlock the UI thread (M43) — and the reset itself runs in the process
    // dialog, i.e. on a background thread.
    private async Task UndoLastCommitAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _statusBar.SetText("Checking the last commit…");

        (bool Ok, bool HasParent, string Summary, string Error) head;
        try
        {
            head = await Task.Run(() =>
            {
                GitCommands.GitModule module = GitContext.CreateModule(repo);

                // %x20 rather than a literal space: GitArgumentBuilder tokenizes each
                // string it is given on whitespace, which would split the format.
                GitArgumentBuilder logArgs = new("log") { "-1", "--pretty=format:%h%x20%s" };
                ExecutionResult log = GitCommands.ExecutableExtensions.Execute(module.GitExecutable, logArgs, throwOnErrorExit: false);
                if (!log.ExitedSuccessfully)
                {
                    return (false, false, string.Empty, log.AllOutput.Trim());
                }

                GitArgumentBuilder parentArgs = new("rev-parse") { "--verify", "--quiet", "HEAD~1" };
                ExecutionResult parent = GitCommands.ExecutableExtensions.Execute(module.GitExecutable, parentArgs, throwOnErrorExit: false);
                return (true, parent.ExitedSuccessfully, log.StandardOutput.Trim(), string.Empty);
            });
        }
        catch (Exception ex)
        {
            _statusBar.SetText("Undo last commit failed: " + ex.Message);
            return;
        }

        if (!head.Ok)
        {
            _statusBar.SetText(head.Error.Length > 0
                ? "Undo last commit failed: " + head.Error
                : "Undo last commit failed: no commit to undo.");
            return;
        }

        if (!head.HasParent)
        {
            await InfoAsync(
                "Undo last commit",
                "The current commit has no parent (this is the very first commit on this branch), "
                + "so it cannot be undone with a soft reset.\n\nNothing was changed.");
            _statusBar.SetText("Undo last commit: no previous commit.");
            return;
        }

        bool confirmed = await ConfirmUndoLastCommitAsync(head.Summary);
        if (!confirmed)
        {
            _statusBar.SetText("Undo last commit cancelled.");
            return;
        }

        WorkingDirectoryService service = new();
        _statusBar.SetText("Undoing last commit…");

        WorkingDirCommitResult? result = null;
        await Views.GitProcessDialog.RunAsync(
            this,
            "Undo last commit (git reset --soft HEAD~1)",
            () =>
            {
                result = service.UndoLastCommit(repo);
                return new Views.GitProcessOutcome(result.Success, result.Output);
            });

        _statusBar.SetText(result is { Success: true }
            ? "Last commit undone — its changes are staged in the working tree."
            : "Undo last commit failed — see the process output.");
        RefreshAll();
    }

    // Confirmation for the destructive part of "Undo last commit": the commit is
    // removed from the branch. Spells out what actually happens (soft reset, changes
    // kept) so the wording matches the service behaviour.
    private async Task<bool> ConfirmUndoLastCommitAsync(string headSummary)
    {
        Button undo = new() { Content = "Undo commit", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

        StackPanel panel = new()
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = "Undo the last commit?\n\n"
                        + "This runs \"git reset --soft HEAD~1\": the commit is removed from the "
                        + "branch, but all of its changes are kept and left staged in the working "
                        + "tree, so nothing is lost. Anything already pushed would need a force "
                        + "push to match.",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        if (headSummary.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = headSummary,
                FontFamily = new FontFamily("monospace"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
                Background = (IBrush)Application.Current!.Resources["App.Panel"]!,
                Padding = new Thickness(8),
            });
        }

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { undo, cancel },
        });

        Window dlg = new()
        {
            Title = "Undo last commit",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = panel,
        };

        bool result = false;
        undo.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // Commands → "Reset changes…" (FormBrowse resetToolStripMenuItem). Destructive:
    // asks for explicit confirmation and lets the user choose whether the index is
    // reset too, mirroring WorkingDirectoryView.ResetChangesAsync (which always
    // passes includeStaged: true). The git work runs inside the process dialog, i.e.
    // on a background thread — WorkingDirectoryService blocks on async work and would
    // deadlock the UI thread (M43).
    private async Task ResetChangesAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        bool? includeStaged = await ConfirmResetAsync();
        if (includeStaged is null)
        {
            _statusBar.SetText("Reset cancelled.");
            return;
        }

        bool staged = includeStaged.Value;
        WorkingDirectoryService service = new();
        _statusBar.SetText("Resetting tracked changes…");

        WorkingDirCommitResult? result = null;
        await Views.GitProcessDialog.RunAsync(
            this,
            staged ? "Reset changes (worktree + index)" : "Reset changes (worktree only)",
            () =>
            {
                result = service.ResetChanges(repo, staged);
                return new Views.GitProcessOutcome(result.Success, result.Output);
            });

        _statusBar.SetText(result is { Success: true }
            ? "Tracked changes discarded."
            : "Reset failed — see the process output.");
        RefreshAll();
    }

    // Commands → "Clean working directory…" (FormBrowse cleanupToolStripMenuItem).
    // Same contract as WorkingDirectoryView.CleanWorkingDirectoryAsync: a `git clean
    // -nd` PREVIEW first, explicit confirmation, then the real clean. Both previews
    // (with and without ignored files) are computed up-front in Task.Run so toggling
    // the "include ignored files" box in the dialog never touches git from the UI
    // thread.
    private async Task CleanWorkingDirectoryAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _statusBar.SetText("Previewing clean…");
        WorkingDirectoryService service = new();

        (bool Ok, string Error, string Plain, string WithIgnored) preview;
        try
        {
            preview = await Task.Run(() =>
            {
                WorkingDirCommitResult plain = service.CleanDryRun(repo, includeIgnored: false);
                if (!plain.Success)
                {
                    return (false, plain.Output.Trim(), string.Empty, string.Empty);
                }

                WorkingDirCommitResult ignored = service.CleanDryRun(repo, includeIgnored: true);
                return (true, string.Empty, plain.Output.Trim(), (ignored.Success ? ignored.Output : plain.Output).Trim());
            });
        }
        catch (Exception ex)
        {
            _statusBar.SetText("Clean preview failed: " + ex.Message);
            return;
        }

        if (!preview.Ok)
        {
            _statusBar.SetText("Clean preview failed: " + preview.Error);
            return;
        }

        if (preview.Plain.Length == 0 && preview.WithIgnored.Length == 0)
        {
            _statusBar.SetText("Nothing to clean (no untracked files).");
            return;
        }

        bool? includeIgnored = await ConfirmCleanAsync(preview.Plain, preview.WithIgnored);
        if (includeIgnored is null)
        {
            _statusBar.SetText("Clean cancelled.");
            return;
        }

        // Live output: git clean prints one "Removing <path>" line per entry, so the
        // streaming runner (stdout+stderr, unbuffered) is worth it here.
        string args = includeIgnored.Value ? "clean -f -d -x" : "clean -f -d";
        int exitCode = -1;
        await Views.GitProcessDialog.RunStreamingAsync(this, "Clean working directory", emit =>
        {
            exitCode = GitStreamRunner.Run(repo, args, emit);
            return new Views.GitProcessOutcome(exitCode == 0, exitCode == 0 ? string.Empty : $"git clean exited with code {exitCode}.");
        });

        _statusBar.SetText(exitCode == 0 ? "Working directory cleaned." : "Clean failed — see the process output.");
        RefreshAll();
    }

    // Reset confirmation: returns the chosen includeStaged flag, or null on cancel.
    private async Task<bool?> ConfirmResetAsync()
    {
        CheckBox alsoStaged = new()
        {
            Content = "Also discard staged changes (git reset --hard HEAD)",
            IsChecked = true,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Button reset = new() { Content = "Reset", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

        Window dlg = new()
        {
            Title = "Reset changes",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Discard uncommitted changes to tracked files? This cannot be undone.\n"
                            + "Untracked files are left alone — use \"Clean working directory…\" for those.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    alsoStaged,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { reset, cancel },
                    },
                },
            },
        };

        bool? result = null;
        reset.Click += (_, _) => { result = alsoStaged.IsChecked == true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // Clean preview + confirmation: shows what `git clean -nd` (or -ndx) reported and
    // returns the chosen includeIgnored flag, or null on cancel. Both texts are
    // pre-computed, so the checkbox only swaps already-loaded strings.
    private async Task<bool?> ConfirmCleanAsync(string plain, string withIgnored)
    {
        TextBlock header = new() { TextWrapping = TextWrapping.Wrap };
        TextBlock list = new()
        {
            FontFamily = new FontFamily("monospace"),
            TextWrapping = TextWrapping.NoWrap,
        };
        CheckBox ignored = new()
        {
            Content = "Include ignored files (git clean -x)",
            IsChecked = false,
            Margin = new Thickness(0, 10, 0, 0),
        };

        Button clean = new() { Content = "Clean", MinWidth = 90 };
        Button cancel = new() { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

        ScrollViewer scroll = new()
        {
            Content = list,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 10, 0, 0),
            Background = (IBrush)Application.Current!.Resources["App.Panel"]!,
            Padding = new Thickness(8),
        };

        // The height is set explicitly from the line count: inside a StackPanel the
        // ScrollViewer otherwise settles on a single text line, hiding the rest of
        // the preview behind a scrollbar the user has no reason to look for.
        void Update()
        {
            string text = ignored.IsChecked == true ? withIgnored : plain;
            int count = text.Length == 0
                ? 0
                : text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            header.Text = count == 0
                ? "Nothing would be removed with these options."
                : $"The following {count} untracked file(s)/directory(ies) will be permanently removed. This cannot be undone.";
            list.Text = text.Length > 0 ? text : "(nothing to remove)";
            scroll.Height = Math.Clamp((Math.Max(count, 1) * 19) + 18, 40, 280);
        }

        ignored.IsCheckedChanged += (_, _) => Update();
        Update();

        Window dlg = new()
        {
            Title = "Clean working directory",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    header,
                    scroll,
                    ignored,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { clean, cancel },
                    },
                },
            },
        };

        bool? result = null;
        clean.Click += (_, _) => { result = ignored.IsChecked == true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    private void RunOp(string label, Func<bool> op, bool confirm = false)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            if (confirm && !await ConfirmAsync($"{label}? This may discard work."))
            {
                return;
            }

            _statusBar.SetText($"{label}…");
            bool ok;
            try
            {
                ok = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"{label} failed: {ex.Message}");
                return;
            }

            RefreshAll();
            if (!ok)
            {
                _statusBar.SetText($"{label} failed — see the panel output.");
            }
        }
    }

    // Output-surfacing variant of RunOp: mirrors the same status→run→refresh
    // structure, but on failure shows the first line of the git output (e.g. a
    // revert conflict) rather than a generic message. Never crashes on conflict.
    private void RunOp(string label, Func<RevertArchiveResult> op)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            _statusBar.SetText($"{label}…");
            RevertArchiveResult result;
            try
            {
                result = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"{label} failed: {ex.Message}");
                return;
            }

            RefreshAll();
            if (!result.Success)
            {
                string firstLine = result.Output.Split('\n')[0].Trim();
                _statusBar.SetText($"{label} stopped: {firstLine} — see the panel output.");
            }
        }
    }

    private async Task NewTagAsync()
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("New tag", "Tag name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        RunOp($"Create tag {name}",
            () => new BranchTagService().CreateTag(_repoPath!, name.Trim(), "HEAD", string.Empty).Success);
    }

    private async Task NewBranchAsync()
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("New branch", "Branch name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        RunOp($"Create branch {name}",
            () => new BranchTagService().CreateBranch(_repoPath!, name.Trim(), startPoint: "HEAD", checkout: true).Success);
    }

    // ---- patch operations (format / apply / view) ----------------------------------

    // Prompts for a base ref, picks an output directory, then generates one patch
    // per commit in <base>..HEAD there (git format-patch). Reports the files written.
    private async Task FormatPatchAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        string? baseRef = await PromptAsync(
            "Format patch",
            "Base ref/commit (patches are produced for <base>..HEAD):",
            "HEAD~1");
        if (string.IsNullOrWhiteSpace(baseRef))
        {
            return;
        }

        TopLevel? top = GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders =
            await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Choose an output directory for the patch files",
            });

        if (folders.Count == 0)
        {
            return;
        }

        string? outDir = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(outDir))
        {
            _statusBar.SetText("The selected folder has no local path.");
            return;
        }

        string trimmedBase = baseRef.Trim();
        _statusBar.SetText("Generating patches…");

        PatchResult result;
        try
        {
            result = await Task.Run(() => new PatchService().FormatPatch(_repoPath!, trimmedBase, outDir));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Format patch failed: {ex.Message}");
            return;
        }

        if (result.Success)
        {
            _statusBar.SetText(result.Files.Count > 0
                ? $"Wrote {result.Files.Count} patch file(s) to {outDir}"
                : $"format-patch produced no patches for {trimmedBase}..HEAD");
        }
        else
        {
            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText($"Format patch failed: {firstLine}");
            await new PatchOutputWindow("Format patch — failed", result.Output).ShowDialog(this);
        }
    }

    // Picks a .patch/.diff file and applies it (git am, falling back to git apply).
    // Surfaces git's output; on failure shows the full message in a modal.
    private async Task ApplyPatchAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        string? file = await PickPatchFileAsync("Choose a patch file to apply");
        if (file is null)
        {
            return;
        }

        _statusBar.SetText("Applying patch…");

        PatchResult result;
        try
        {
            result = await Task.Run(() => new PatchService().ApplyPatch(_repoPath!, file));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Apply patch failed: {ex.Message}");
            return;
        }

        RefreshAll();

        if (result.Success)
        {
            _statusBar.SetText($"Applied patch {Path.GetFileName(file)}");
        }
        else
        {
            _statusBar.SetText($"Apply patch failed for {Path.GetFileName(file)} — see output.");
            await new PatchOutputWindow("Apply patch — failed", result.Output).ShowDialog(this);
        }
    }

    // Picks a .patch/.diff file, reads it, and shows it in the colour-rendered
    // read-only patch viewer (same colouring as DiffView).
    private async Task ViewPatchAsync()
    {
        string? file = await PickPatchFileAsync("Choose a patch file to view");
        if (file is null)
        {
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file);
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Could not read {Path.GetFileName(file)}: {ex.Message}");
            return;
        }

        await new PatchViewerWindow(Path.GetFileName(file), text).ShowDialog(this);
    }

    // Shared open-file picker for patch files, filtered to .patch/.diff (with an
    // "all files" fallback). Returns the local path, or null if cancelled.
    private async Task<string?> PickPatchFileAsync(string title)
    {
        TopLevel? top = GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files =
            await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = title,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Patch files") { Patterns = new[] { "*.patch", "*.diff" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*" } },
                },
            });

        if (files.Count == 0)
        {
            return null;
        }

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            _statusBar.SetText("The selected file has no local path.");
            return null;
        }

        return path;
    }

    // Opens the modal Settings window over the main window, passing the current
    // repo path. Settings persists its own changes; afterwards we re-sync the
    // in-memory theme so PersistLayout() on close doesn't overwrite a change
    // the user made in the dialog.
    private async Task OpenSettingsAsync()
    {
        await SettingsWindow.ShowAsync(this, _repoPath);
        _uiState.Theme = _uiStateService.Load().Theme;
    }

    // ---- plugins --------------------------------------------------------------------

    // Runs a plugin off the UI thread against the open repository, then — if it
    // returns true — refreshes the whole view (mirroring the WinForms host's
    // "Execute → RefreshAll" contract). The plugin's result message (for the sample
    // plugin, exposed via SampleGreetPlugin.LastResult) is surfaced in the status
    // bar, standing in for the MessageBox portable plugins would otherwise show.
    private void RunPlugin(IGitPlugin plugin)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        string name = plugin.Name ?? plugin.GetType().Name;
        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            _statusBar.SetText($"Running plugin '{name}'…");

            bool refresh;
            try
            {
                refresh = await Task.Run(() =>
                {
                    // Build a minimal host + event args (OwnerForm = null) and register
                    // the plugin so its settings source is bound before Execute reads it.
                    AvaloniaGitUICommands commands = new(_repoPath!);
                    RegisterPlugin(plugin, commands);
                    GitUIEventArgs args = new(ownerForm: null, gitUICommands: commands);
                    return plugin.Execute(args);
                });
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"Plugin '{name}' failed: {ex.Message}");
                return;
            }

            if (refresh)
            {
                RefreshAll();
            }

            string? output = (plugin as SampleGreetPlugin)?.LastResult;
            _statusBar.SetText(output is { Length: > 0 }
                ? output
                : $"Plugin '{name}' finished{(refresh ? " (view refreshed)" : string.Empty)}.");
        }
    }

    // Opens the runtime-typed settings editor for a plugin, binding its settings
    // container to the open repository's effective settings first, then persisting
    // through the same source on Save.
    private async Task OpenPluginSettingsAsync(IGitPlugin plugin)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        AvaloniaGitUICommands commands = new(_repoPath);
        RegisterPlugin(plugin, commands);

        await PluginSettingsWindow.ShowAsync(this, plugin, commands.GetEffectiveSettings());
        _statusBar.SetText($"Closed settings for '{plugin.Name ?? plugin.GetType().Name}'.");
    }

    // Ensures the plugin has a settings container and binds it to the repository's
    // effective settings via Register (GitPluginBase.Register sets the source).
    private static void RegisterPlugin(IGitPlugin plugin, AvaloniaGitUICommands commands)
    {
        plugin.SettingsContainer ??= new AvaloniaSettingsContainer();
        try
        {
            plugin.Register(commands);
        }
        catch
        {
            // If a plugin's Register touches unsupported host surface, fall back to
            // wiring the settings source directly so its settings still work.
            plugin.SettingsContainer.SetSettingsSource(commands.GetEffectiveSettings());
        }
    }

    private async Task PickRepositoryAsync()
    {
        RepositoryPickerView picker = new();
        Window dlg = new()
        {
            Title = "Open Git repository",
            Width = 640,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = picker,
        };
        picker.RepositorySelected += repo =>
        {
            dlg.Close();
            OpenRepository(repo);
        };
        await dlg.ShowDialog(this);
    }

    // Shows the clone dialog; on success opens the freshly cloned repository
    // through the same path the picker uses (OpenRepository).
    private async Task CloneRepositoryAsync()
    {
        CloneDialog dlg = new();
        await dlg.ShowDialog(this);

        if (dlg.ClonedRepoPath is { Length: > 0 } repo && Directory.Exists(repo))
        {
            _statusBar.SetText($"Cloned into {repo}");
            OpenRepository(repo);
        }
    }

    // Picks a directory, runs git init in it off the UI thread, then opens the
    // new repository through OpenRepository (same as clone / picker).
    private async Task InitRepositoryAsync()
    {
        TopLevel? top = GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders =
            await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Choose a directory for the new repository",
            });

        if (folders.Count == 0)
        {
            return;
        }

        string? dir = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(dir))
        {
            _statusBar.SetText("The selected folder has no local path.");
            return;
        }

        _statusBar.SetText("Initialising repository…");

        CloneInitResult result;
        try
        {
            result = await Task.Run(() => new CloneInitService().Init(dir));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Init failed: {ex.Message}");
            return;
        }

        if (result.Success && result.RepoPath is not null)
        {
            _statusBar.SetText($"Initialised repository at {result.RepoPath}");
            OpenRepository(result.RepoPath);
        }
        else
        {
            _statusBar.SetText("Init failed — see output: " + result.Output);
        }
    }

    // Opens a submodule / worktree / super-project path as the active repository
    // (from the toolbar dropdowns or the tree's "Open" context items), guarding
    // against a path that has since disappeared.
    private void OpenRepositoryPath(string path)
    {
        if (Directory.Exists(path))
        {
            OpenRepository(path);
        }
        else
        {
            _statusBar.SetText($"Path no longer exists: {path}");
        }
    }

    // Resolves the parent super-project of a repository (the "level-up" target),
    // or null when the repo is standalone. Prefers git's own answer
    // (`git rev-parse --show-superproject-working-tree`, which is empty for a
    // normal repo), and falls back to walking up from the parent directory to the
    // nearest enclosing git repository. Runs synchronously — call off the UI thread.
    private static string? FindSuperproject(string repo)
    {
        try
        {
            ProcessStartInfo psi = new("git", "rev-parse --show-superproject-working-tree")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? proc = Process.Start(psi);
            if (proc is not null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                string line = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
                if (line.Length > 0 && Directory.Exists(line))
                {
                    return Path.GetFullPath(line);
                }
            }
        }
        catch
        {
            // Fall through to the directory-walk fallback below.
        }

        try
        {
            DirectoryInfo? dir = Directory.GetParent(repo.TrimEnd('/', '\\'));
            while (dir is not null)
            {
                if (GitService.IsGitRepository(dir.FullName))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // No parent repo discoverable.
        }

        return null;
    }

    private void OpenRepository(string repoPath)
    {
        _repoPath = repoPath;
        ShowRepositoryView();
        WarmUpCore(repoPath);

        _revisions.LoadRepository(repoPath);
        if (_workingDirWindow is not null)
        {
            _workingDir.LoadRepository(repoPath);
        }

        _stash.LoadRepository(repoPath);
        _tree.LoadRepository(repoPath);
        _statusBar.LoadRepository(repoPath);
        RefreshToolbarState();
        _menu.SetFavoriteRepositories(_favoritesService.Load());
        _menu.SetPlugins(PluginService.Instance.Plugins);
        _ = RecordRecentAsync(repoPath);
        _ = PopulateRecentAsync();
    }

    // Records the opened repository in the core MRU so it appears in "Open recent"
    // and on the dashboard next time. Best-effort; never blocks the open.
    private static async Task RecordRecentAsync(string repoPath)
    {
        try
        {
            await new RecentRepositoriesService().AddAsync(repoPath);
        }
        catch
        {
            // Non-fatal.
        }
    }

    private async Task PopulateRecentAsync()
    {
        try
        {
            IReadOnlyList<string> recent = await new RecentRepositoriesService().LoadAsync();
            _menu.SetRecentRepositories(recent);
        }
        catch
        {
            // Non-fatal: the menu just shows "(none)".
        }
    }

    // ---- dashboard + favorites -------------------------------------------------------

    // Swaps the repository work area out for the dashboard landing view, and loads
    // it with the current favorite + recent lists. Reachable from "Close (go to
    // Dashboard)" and on startup when no repository is found.
    private void ShowDashboard()
    {
        if (!_dashboardShowing)
        {
            _root.Children.Remove(_repositoryArea);
            if (!_root.Children.Contains(_dashboard))
            {
                _root.Children.Add(_dashboard);
            }

            _dashboardShowing = true;
        }

        _menu.SetFavoriteRepositories(_favoritesService.Load());
        _ = LoadDashboardAsync();
        _statusBar.SetText("Dashboard");
    }

    // Restores the repository work area (used whenever a repository is opened).
    private void ShowRepositoryView()
    {
        if (_dashboardShowing)
        {
            _root.Children.Remove(_dashboard);
            if (!_root.Children.Contains(_repositoryArea))
            {
                _root.Children.Add(_repositoryArea);
            }

            _dashboardShowing = false;
        }
    }

    // Loads the dashboard's favorite + recent lists (recent off the UI thread).
    private async Task LoadDashboardAsync()
    {
        IReadOnlyList<string> favorites = _favoritesService.Load();
        IReadOnlyList<string> recent;
        try
        {
            recent = await new RecentRepositoriesService().LoadAsync();
        }
        catch
        {
            recent = Array.Empty<string>();
        }

        _dashboard.Load(favorites, recent);
    }

    // Marks the currently open repository as a favorite and refreshes the submenu
    // (and the dashboard, if it happens to be visible).
    private void AddCurrentToFavorites()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open to add to favorites.");
            return;
        }

        IReadOnlyList<string> favorites = _favoritesService.Add(_repoPath);
        _menu.SetFavoriteRepositories(favorites);
        if (_dashboardShowing)
        {
            _dashboard.Load(favorites, Array.Empty<string>());
            _ = LoadDashboardAsync();
        }

        _statusBar.SetText($"Added to favorites: {_repoPath}");
    }

    // Opens the Git maintenance dialog for the current repository.
    private async Task OpenMaintenanceAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        await MaintenanceDialog.ShowAsync(this, _repoPath);
        RefreshAll();
    }

    // Confirmation dialog (Yes/No).
    private Task<bool> ConfirmAsync(string message) => YesNoAsync(message);

    // Informational dialog with a single OK button (no choice to make).
    private async Task InfoAsync(string title, string message)
    {
        Button ok = new() { Content = "OK", MinWidth = 80 };
        Window dlg = new()
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
    }

    private async Task<bool> YesNoAsync(string message)
    {
        Button yes = new() { Content = "Yes", MinWidth = 80 };
        Button no = new() { Content = "No", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Window dlg = new()
        {
            Title = "Confirm",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { yes, no },
                    },
                },
            },
        };
        bool result = false;
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // Text prompt; returns null on cancel. Optionally prefills an initial value and,
    // for multi-line input (e.g. commit messages), grows into a wrapping text area.
    private async Task<string?> PromptAsync(string title, string label, string? initial = null, bool multiline = false)
    {
        TextBox input = new() { Watermark = label, Text = initial };
        if (multiline)
        {
            input.AcceptsReturn = true;
            input.TextWrapping = TextWrapping.Wrap;
            input.MinHeight = 120;
        }
        Button ok = new() { Content = "OK", MinWidth = 80 };
        Button cancel = new() { Content = "Cancel", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Window dlg = new()
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { ok, cancel },
                    },
                },
            },
        };
        string? result = null;
        ok.Click += (_, _) => { result = input.Text; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // Touches the core's main read paths once, sequentially, so shared
    // process-global lazy state initializes before concurrent panel loads.
    private static void WarmUpCore(string repoPath)
    {
        try
        {
            GitCommands.GitModule module = GitContext.CreateModule(repoPath);
            _ = module.GetCurrentCheckout();
            _ = new RevisionService().LoadRevisions(repoPath, 1);
        }
        catch
        {
            // Best-effort; the panels report their own errors.
        }
    }

    // Accept a subdirectory: walk up until a git working dir is found.
    private static string? FindRepositoryRoot(string path)
    {
        try
        {
            DirectoryInfo? dir = new(path);
            while (dir is not null)
            {
                if (GitService.IsGitRepository(dir.FullName))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }
}
