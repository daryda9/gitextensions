using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Avalonia.Plugins;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Views;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Plugins;
using GitExtUtils;
using CommitInfoPosition = GitExtensions.Avalonia.Views.CommitInfoPosition;
using GitModule = GitCommands.GitModule;

namespace GitExtensions.Avalonia;

/// <summary>
///  Integrated main window modelled on the original GitExtensions FormBrowse:
///  a top toolbar, a left repository-objects tree (branches/remotes/tags/
///  stashes), the revision-grid DAG in the centre, a bottom detail/diff panel,
///  and a status bar. All views are self-contained
///  <see cref="UserControl"/>s driven over the reused core via <see cref="GitContext"/>.
/// </summary>
public sealed class MainWindow : Theming.ZoomWindow
{
    private static readonly object CoreWarmupGate = new();
    private static Task? s_coreWarmupTask;

    private readonly MainMenu _menu = new();
    private readonly MainToolbar _toolbar = new();

    // The client-side title bar and the resize strips that go with it: both exist only
    // while the menu is merged into the title bar (see ApplyWindowChrome).
    private TitleBar? _titleBar;
    private Panel? _grips;

    // The window's Content: the root dock with the resize strips layered over it.
    private Panel? _layered;
    private readonly StatusBarView _statusBar = new();
    private readonly RepoObjectsTree _tree = new();
    private readonly RevisionGridView _revisions = new();
    private readonly CommitDetailView _detail = new();
    private readonly DiffView _diff = new();
    private readonly FileTreeView _fileTree = new();
    private readonly GpgView _gpg = new();
    private readonly ConsoleView _console = new();
    private readonly OutputView _output = new();

    private readonly TabControl _bottom;
    private readonly TabItem _commitInfoTab;
    private readonly TabItem _diffTab;
    private readonly TabItem _fileTreeTab;
    private readonly TabItem _gpgTab;
    private readonly TabItem _consoleTab;
    private readonly TabItem _outputTab;
    private readonly TabItem _blameTab;
    private readonly BlameView _blame = new();
    // The commit commands the row menu of EVERY revision grid carries. Recorded here
    // because a grid can be born later: the file-history window builds one when it
    // opens, and it must offer the same menu as the repository's own grid.
    private readonly List<(string Header, Action<string> Handler)> _commitCommands = [];

    private readonly StashOpsService _stashOps = new();
    private readonly ExternalToolService _externalTools = new();
    private readonly BisectService _bisect = new();
    private readonly RepositoryNavigationSnapshotService _navigationSnapshots = new();

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

    // The strip of open repositories, under the toolbar. It owns WHICH repositories are
    // open and which one is active; this window owns what "active" means — everything
    // below the strip is still one single set of views, loaded with whatever the active
    // tab points at. That is the whole design: a tab is a bookmark plus the little bit of
    // per-repository state worth restoring (the row the user was on, the bottom tab),
    // not a second copy of the work area.
    private readonly Views.RepoTabStrip _repoTabs = new();

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

    // Watches the working tree and the git dir so a commit, checkout or pull made
    // in a terminal shows up here without the user pressing F5 (unit F2).
    private readonly RepositoryWatcherService _watcher = new();

    // Keyboard map (command → gesture) + the window-level dispatcher. Defaults are
    // upstream's FormBrowse hotkeys; see InstallHotkeys for what each one runs.
    // The one shared instance: the six per-control scopes are read by views that never
    // see this window (the grid, the diff, the tree, the commit dialog), and a second
    // instance would answer them from a stale copy of the same file.
    private readonly HotkeyService _hotkeys = HotkeyService.Shared;
    private readonly RepositoryStateService _repositoryState = new();
    private readonly Views.RepositoryProgressBanner _progressBanner = new();

    // Left-panel width remembered across a Ctrl+Alt+C collapse/expand.
    private double _treeWidthBeforeCollapse;

    // Native X11 drop receiver (see X11DropTarget): null off X11 or on failure.
    private X11DropTarget? _dropTarget;

    // Last known NORMAL (non-maximized) geometry: what gets persisted, so that
    // closing while maximized still restores sensible bounds afterwards.
    private PixelPoint? _normalPosition;
    private double _normalWidth;
    private double _normalHeight;

    private string? _repoPath;
    private int _repositoryEpoch;

    // The utility window currently open on THIS repository, if any (see
    // ShowRepositoryToolAsync). Only tools whose panel was handed a path once and cannot
    // be re-pointed are registered here; every other dialog either takes the path per
    // call or is short-lived enough not to care.
    private Window? _repositoryScopedWindow;
    private readonly object _activeNavigationGate = new();
    private string? _activeNavigationRepository;
    private Task<RepositoryNavigationSnapshot>? _activeNavigationSnapshot;
    private bool _activeNavigationLoadPending;
    private string? _lastSelectedHash;

    // True while the grid selection sits on an artificial row (working directory or
    // index): _lastSelectedHash still holds the previous real commit, so anything that
    // needs a start point must fall back to HEAD instead of using a stale hash.
    private bool _artificialRowSelected;

    // The sentinel hash of the artificial row currently selected (worktree or index),
    // used as the lazy-load key for the bottom tabs. Kept apart from
    // _lastSelectedHash, which must stay a real commit for every other consumer.
    private string? _artificialHash;

    // Which artificial side the selected row is (worktree or index), taken from the
    // grid event's kind rather than re-derived from the hash.
    private ArtificialDiff _artificialWhich;

    private const string DefaultTitle = "Git Extensions (Avalonia / Linux)";

    // Which commit each lazily-loaded bottom tab is currently showing; null = stale.
    private string? _detailLoadedFor;
    private string? _diffLoadedFor;

    // True while the Diff tab is showing a comparison the user asked for (a range,
    // or against the working directory) rather than the selected commit. Without it
    // the lazy tab loader would immediately overwrite it with `git show <commit>`.
    private bool _diffShowsRange;
    private string? _fileTreeLoadedFor;
    private string? _gpgLoadedFor;

    // The commit chosen as the "BASE" for the grid's Compare actions (single-select
    // grid, so BASE + "Compare to BASE" together stand in for a two-commit compare).
    private string? _compareBaseHash;

    public MainWindow()
    {
        // Load persisted UI state first, and apply the remembered theme and style
        // before any App.* brushes are read below, so the window opens in them.
        // Both dimensions go in one Apply call: the resources are rebuilt once, and
        // neither choice can be lost by a second call defaulting the other.
        _uiState = _uiStateService.Load();
        ApplyAppearance();

        // The interface text size, before this window is built: the resource is read as
        // each control is templated, so writing it first is what makes the window open at
        // the remembered size instead of at the baseline and then reflowing. Its own call,
        // not folded into ApplyAppearance: the size is a font size, not a palette (see
        // Theming/UiScaling).
        Theming.UiScaling.Apply(Theming.UiSizes.Parse(_uiState.UiSize));

        Title = DefaultTitle;
        Width = _uiState.WindowWidth;
        Height = _uiState.WindowHeight;
        _normalWidth = _uiState.WindowWidth;
        _normalHeight = _uiState.WindowHeight;

        // A remembered position is only honoured once it has been checked against
        // the screens present now (see RestoreWindowPlacement); until then let
        // Avalonia centre the window, which is the right answer when there is no
        // usable saved position.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        // Commit-info position is session-local (the original FormBrowse default:
        // below the graph). Split view IS persisted, so the bottom panel comes
        // back in the same shape the user left it in.
        _commitInfoPosition = Enum.TryParse(_uiState.CommitInfoPosition, out CommitInfoPosition restoredPosition)
            ? restoredPosition
            : CommitInfoPosition.BelowGraph;
        _splitHorizontal = _uiState.SplitView;

        // Detail/diff definitions are (re)created by RebuildRightRegion; seed them
        // here so PersistLayout has valid references before the first rebuild.
        // The persisted values are proportions of their split (each pair sums to 1),
        // which is exactly what a pair of star weights expresses — so the restored
        // split is the one the user dragged, whatever this window's size is.
        _detailRow = new RowDefinition(new GridLength(_uiState.DetailStar, GridUnitType.Star));
        _diffRow = new RowDefinition(new GridLength(_uiState.DiffStar, GridUnitType.Star));
        _revRow = new RowDefinition(new GridLength(_uiState.RevisionsStar, GridUnitType.Star));
        _bottomRow = new RowDefinition(new GridLength(_uiState.BottomStar, GridUnitType.Star));

        // ---- bottom panel: the original FormBrowse tab strip
        //   Commit · Diff · File tree · GPG · Console · Output
        // followed by the extra Avalonia panels
        //   Blame · File history.
        // Stash is NOT here: upstream opens FormStash as a window and so does the port
        // (StashWindow), reached from the toolbar's stash split button and the left tree.
        // The Commit tab shows the commit DETAIL; the diff moved out to its own
        // Diff tab so both are visible at once.
        _commitInfoTab = new TabItem();
        _diffTab = new TabItem { Content = _diff };
        _fileTreeTab = new TabItem { Content = _fileTree };
        _gpgTab = new TabItem { Content = _gpg };
        _consoleTab = new TabItem { Content = _console };
        _outputTab = new TabItem { Content = _output };
        _blameTab = new TabItem { Content = _blame };
        ApplyTabTranslations();

        // The Console tab's "Open terminal here" button reuses the external-tool
        // terminal launcher against the current repository.
        _console.OpenTerminalRequested += () => WithRepo(p => _externalTools.OpenTerminal(p));

        _bottom = new TabControl
        {
            // App.Panel, the surface every one of these panes paints: the strip used to
            // sit on App.Window, one step darker, so the row of tab labels was a band of
            // its own laid over the pane it belongs to. The selected tab is marked by
            // its accent bar, its ink and its weight (see ModernStyles.BuildTabItem),
            // none of which needs the strip to be a different colour from the body.
            Background = (IBrush)Application.Current!.Resources["App.Panel"]!,
            ClipToBounds = true,
            Items =
            {
                _commitInfoTab, _diffTab, _fileTreeTab, _gpgTab, _consoleTab, _outputTab,
                _blameTab,
            },
        };

        // Showing a lazily-loaded tab is what brings it up to date.
        //
        // The SOURCE check is not decoration. SelectionChanged is a bubbling routed
        // event, so the file list inside a tab raises one that arrives here as if the
        // TAB had changed — and it arrives from inside Avalonia's own selection update,
        // where reloading the tab reassigns ItemsSource on the very list being updated:
        // "Cannot change source while update is in progress", a hard crash. Reported
        // from a real session, then reproduced (Commit tab → pick another revision →
        // click File tree).
        _bottom.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _bottom))
            {
                LoadSelectedBottomTab();
            }
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
        _treeWidthBeforeCollapse = _uiState.TreeWidth;
        if (_uiState.LeftPanelCollapsed)
        {
            _tree.IsVisible = false;
            _treeCol.Width = new GridLength(0, GridUnitType.Pixel);
        }
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
        // Under the toolbar, above the work area: the toolbar acts on the window, the
        // strip decides what the window is looking at. (Docked before the status bar and
        // the fill child, because a DockPanel reads its children in order.)
        DockPanel.SetDock(_repoTabs, Dock.Top);
        root.Children.Add(_repoTabs);
        root.Children.Add(_statusBar);
        root.Children.Add(main);
        _root = root;

        // The window's own frame — the desktop's title bar, or the client-side one the
        // merged arrangement draws — is decided here and re-decided whenever the option
        // changes. It also assigns Content, because with the merged bar the content is
        // the root plus the resize strips layered over it.
        //
        // Driven off WindowChrome's own event rather than off SetAppearance: the choice
        // can equally be made in the Settings dialog, and one subscription covers both
        // doors.
        ApplyWindowChrome();
        Theming.WindowChrome.Changed += OnWindowChromeChanged;

        // The strip's option comes from the same state file as the chrome's, and like it
        // has to be in force before anything reads it (the first OpenRepository below
        // branches on it).
        Theming.RepoTabsOption.Apply(Theming.RepoTabsOption.Parse(_uiState.RepoTabs));
        WireRepoTabs();
        AddHandler(KeyDownEvent, OnRepoTabNavigationKey, RoutingStrategies.Tunnel);

        WireEvents();
        InstallHotkeys();
        WireDragAndDrop();
        WireWatcher();
        _toolbar.SetSplitView(_splitHorizontal);

        Opened += (_, _) =>
        {
            RestoreWindowPlacement();
            RestoreBottomTab();
            // The toolbar's toggles must reflect the state we restored above.
            _toolbar.SetLeftPanelVisible(_tree.IsVisible);
            _toolbar.SetCommitInfoPosition(_commitInfoPosition);
            InstallNativeDropTarget();

            // Populate View → Language. The catalogue itself was already parsed
            // before this window was constructed (Program.Main → BeginPreload →
            // App.OnFrameworkInitializationCompleted → WaitForPreload), so the
            // controls above were built translated; this only fills the picker,
            // and only re-parses if the pre-load did not run or was overtaken.
            _ = InitializeTranslationsAsync();

            // Restore the grid's view options before any repository is loaded, so the
            // first git log already runs with the user's page size and toggles.
            _revisions.RestoreViewOptions(_uiState.GridViewOptions, _uiState.GridPageSize);

            // CLI argument > cwd if it is a repo > last repo opened > dashboard.
            string? initial = FindRepositoryRoot(App.InitialRepoPath ?? Directory.GetCurrentDirectory())
                ?? (_uiState.LastRepoPath is string last ? FindRepositoryRoot(last) : null);

            // The saved tabs answer the same question as `initial` does — which
            // repository is on screen — so they are tried first and, when they answer,
            // the chain above is not consulted at all. A path on the COMMAND LINE is an
            // explicit instruction and outranks them: it is opened the usual way, and
            // simply becomes one more tab.
            if (App.InitialRepoPath is null && RestoreRepoTabs())
            {
                // The strip decided, and RestoreRepoTabs already loaded its active tab.
            }
            else if (initial is not null)
            {
                OpenRepository(initial);
            }
            else
            {
                ShowDashboard();
            }
        };

        // The tab strip is the only long-lived caption owned by this window, so a
        // language switch just re-labels it in place (the dialogs are rebuilt each
        // time they are opened and pick the new catalogue up on their own).
        TranslationService.LanguageChanged += () => Dispatcher.UIThread.Post(ApplyTabTranslations);

        // Track the restored geometry continuously: once the window is maximized
        // its own Position/Width/Height describe the maximized frame, so the values
        // worth saving have to be captured while it is still normal.
        PositionChanged += (_, _) => CaptureNormalPlacement();

        // The resize strips belong to a window that still has edges to drag.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateResizeGrips();

                // Maximising is what makes the frame overhang the screen, so the padding
                // that compensates for it has to be recomputed here.
                UpdateOffScreenMargin();
            }
        };
        SizeChanged += (_, _) =>
        {
            CaptureNormalPlacement();
            PublishMenuMaxHeight();
        };

        // The menu bar's own height is not known until it is laid out, and it changes
        // with the UI size option, so the ceiling is republished from both.
        _menu.SizeChanged += (_, _) => PublishMenuMaxHeight();
        Opened += (_, _) => PublishMenuMaxHeight();

        // Persist window size/position + splitter positions when the window closes.
        Closing += (_, _) =>
        {
            PersistLayout();
            Theming.WindowChrome.Changed -= OnWindowChromeChanged;
            _watcher.Dispose();
            _dropTarget?.Dispose();
        };
    }

    // Remembers the window's non-maximized bounds (see the fields' comment).
    private void CaptureNormalPlacement()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _normalPosition = Position;
        if (Width > 0 && !double.IsNaN(Width))
        {
            _normalWidth = Width;
        }

        if (Height > 0 && !double.IsNaN(Height))
        {
            _normalHeight = Height;
        }
    }

    /// <summary>
    ///  Applies the persisted position/size/maximized state, <b>clamped to the
    ///  screens that exist right now</b>. Without the clamp a window saved on a
    ///  larger or second monitor comes back partly (or entirely) off-screen, with
    ///  its title bar out of reach — the known defect this fixes.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        try
        {
            if (_uiState.WindowMaximized)
            {
                // Size/position stay as loaded: they are the restored bounds the
                // user gets back when un-maximizing.
                WindowState = WindowState.Maximized;
                return;
            }

            bool hasPosition = _uiState.WindowX is int && _uiState.WindowY is int;

            // Work out which screen to measure against: the one holding the saved
            // position, else the one the window opened on. The SIZE is clamped even
            // when no position was saved — a window restored larger than the screen
            // is centred, which pushes its title bar off the top.
            PixelPoint saved = new(_uiState.WindowX ?? 0, _uiState.WindowY ?? 0);
            global::Avalonia.Platform.Screen? screen =
                (hasPosition ? Screens.ScreenFromPoint(saved) : null)
                ?? Screens.ScreenFromWindow(this)
                ?? Screens.Primary;
            if (screen is null)
            {
                return;
            }

            PixelRect area = screen.WorkingArea;
            double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;

            // Position is in physical pixels, Width/Height in device-independent
            // ones; everything below is compared in physical pixels.
            int wanted = (int)Math.Round(_normalWidth * scale);
            int high = (int)Math.Round(_normalHeight * scale);
            int width = Math.Min(wanted, area.Width);
            int height = Math.Min(high, area.Height);

            if (width != wanted || height != high)
            {
                Width = width / scale;
                Height = height / scale;
                _normalWidth = Width;
                _normalHeight = Height;
            }

            if (!hasPosition)
            {
                // Nothing to place: the (now clamped) size is centred by Avalonia.
                return;
            }

            int x = Math.Clamp(saved.X, area.X, Math.Max(area.X, area.X + area.Width - width));
            int y = Math.Clamp(saved.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
            _normalPosition = Position;
        }
        catch
        {
            // Geometry restore is a convenience: a screen enumeration that fails
            // must leave the window where the toolkit put it, not stop start-up.
        }
    }

    // Re-selects the bottom panel tab the user was last on. Keyed by name because
    // the Diff tab leaves the strip while split view is on (SyncDiffTab).
    private void RestoreBottomTab() => SelectBottomTab(_uiState.BottomTab);

    // The same lookup, from any key: the repository tabs restore the pane their own
    // repository was last showing, which is not the one the session started on.
    private void SelectBottomTab(string? key)
    {
        try
        {
            TabItem? tab = key switch
            {
                "Diff" => _diffTab,
                "FileTree" => _fileTreeTab,
                "Gpg" => _gpgTab,
                "Console" => _consoleTab,
                "Output" => _outputTab,
                "Blame" => _blameTab,
                // "History" is what older files hold for the bottom tab this window
                // replaced (M113). It resolves to the commit tab, like any unknown key.
                _ => _commitInfoTab,
            };

            if (_bottom.Items.Contains(tab))
            {
                _bottom.SelectedItem = tab;
            }
        }
        catch
        {
            // Falls back to whatever the tab strip selected by default.
        }
    }

    private string CurrentBottomTabKey()
    {
        object? selected = _bottom.SelectedItem;
        if (ReferenceEquals(selected, _diffTab)) { return "Diff"; }
        if (ReferenceEquals(selected, _fileTreeTab)) { return "FileTree"; }
        if (ReferenceEquals(selected, _gpgTab)) { return "Gpg"; }
        if (ReferenceEquals(selected, _consoleTab)) { return "Console"; }
        if (ReferenceEquals(selected, _outputTab)) { return "Output"; }
        if (ReferenceEquals(selected, _blameTab)) { return "Blame"; }
        return "Commit";
    }

    // ---- drag & drop -------------------------------------------------------------

    /// <summary>
    ///  Lets a folder dropped on the window open as the repository, the way the
    ///  original does on its dashboard (<c>UserRepositoriesList.OnDragDrop</c>:
    ///  build a module, refuse it with a message when
    ///  <c>IsValidGitWorkingDir()</c> is false, otherwise switch to it).
    ///  A dropped file resolves to its containing directory, so dragging any file
    ///  out of a checkout opens that checkout.
    /// </summary>
    private void WireDragAndDrop()
    {
        DragDrop.SetAllowDrop(this, true);

        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        });

        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            string? dropped = FirstLocalPath(e.Data);
            if (dropped is not null)
            {
                HandleDroppedPath(dropped);
            }
        });
    }

    /// <summary>
    ///  Opens a dropped path as the repository, or explains why it cannot be one.
    ///  Shared by the managed Avalonia handler and the X11 receiver below, so both
    ///  routes behave identically. Must be called on the UI thread.
    /// </summary>
    private void HandleDroppedPath(string dropped)
    {
        try
        {
            // A file identifies its directory; a directory identifies itself.
            string? directory = Directory.Exists(dropped)
                ? dropped
                : File.Exists(dropped) ? Path.GetDirectoryName(dropped) : null;

            if (directory is null)
            {
                _statusBar.SetText(TF("Path no longer exists: {0}", dropped));
                return;
            }

            // Accept a subdirectory of a checkout too (FindRepositoryRoot walks
            // up), which is what a user dropping "src/" plainly means. Mirrors the
            // original's dashboard drop, which refuses anything that is not a valid
            // working directory and says so.
            string? root = FindRepositoryRoot(directory);
            if (root is null)
            {
                _statusBar.SetText(TF("{0} is not a valid git repository.", directory));
                return;
            }

            if (string.Equals(root, _repoPath, StringComparison.Ordinal))
            {
                _statusBar.SetText(TF("{0} is already open.", root));
                return;
            }

            OpenRepository(root);
            _statusBar.SetText(TF("Opened {0}", root));
        }
        catch (Exception ex)
        {
            // A malformed drop payload must not take the window down.
            _statusBar.SetText(TF("Could not open the dropped folder: {0}", ex.Message));
        }
    }

    /// <summary>
    ///  Installs the native X11 drop receiver. Avalonia 11.3's X11 backend does not
    ///  implement XDND at all (no <c>XdndAware</c>, no atoms — the support landed
    ///  upstream for 12.1 and was not backported), so on Linux the managed handlers
    ///  wired above are never reached and this is what makes a dropped folder open.
    ///  A no-op anywhere it cannot work.
    /// </summary>
    private void InstallNativeDropTarget()
    {
        try
        {
            IntPtr handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            _dropTarget = X11DropTarget.TryCreate(handle, paths =>
            {
                // Called on the receiver's own X thread.
                string first = paths[0];
                Dispatcher.UIThread.Post(() => HandleDroppedPath(first));
            });
        }
        catch
        {
            // No drag and drop, same as before this existed.
        }
    }

    // First dropped item that has a real local path (a drop can carry remote or
    // virtual items, which have none).
    private static string? FirstLocalPath(IDataObject data)
    {
        IEnumerable<IStorageItem>? items = data.GetFiles();
        if (items is null)
        {
            return null;
        }

        foreach (IStorageItem item in items)
        {
            string? path = item.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }

        return null;
    }

    // ---- automatic refresh -------------------------------------------------------

    /// <summary>
    ///  Connects <see cref="RepositoryWatcherService"/> to the window. The service
    ///  raises its events on a thread-pool thread and never runs git itself, so the
    ///  only thing done here is to hop onto the UI thread and reuse the ordinary
    ///  refresh path (whose panels each load their data in <c>Task.Run</c>).
    /// </summary>
    private void WireWatcher()
    {
        _watcher.Changed += _ => Dispatcher.UIThread.Post(AutoRefresh);

        // The in-progress banner suspends the watcher around its own git calls and
        // asks for a refresh once an operation ends (e.g. "Stop bisect").
        _progressBanner.SuspendWatcher = () => _watcher.Suspend();
        _progressBanner.RepositoryChanged += RefreshAll;

        // "More" on the bisect bar opens the full panel, exactly as upstream's own
        // bar does (InteractiveGitActionControl.cs:242-254).
        _progressBanner.BisectDetailsRequested += () => _ = ShowBisectDialogAsync();

        // "Resolve…" on the merge bar opens the conflict dialog, the port of
        // FormResolveConflicts. Without a subscriber the banner hides the button
        // rather than showing one that does nothing.
        _progressBanner.ResolveConflictsRequested += () => _ = ShowResolveConflictsDialogAsync();
        _watcher.Degraded += message => Dispatcher.UIThread.Post(() => _statusBar.SetText(message));
    }

    // The watcher's refresh: identical to F5 except that it stays quiet when there
    // is nothing to refresh, and never surfaces an error of its own.
    private void AutoRefresh()
    {
        try
        {
            if (_repoPath is null || _dashboardShowing)
            {
                return;
            }

            // Opt-in trace (GE_WATCH_TRACE=1): the only way to tell "one refresh
            // for the whole burst" from "one per file" from outside the process.
            if (Environment.GetEnvironmentVariable("GE_WATCH_TRACE") == "1")
            {
                Console.Error.WriteLine($"[watch] auto-refresh at {DateTime.Now:HH:mm:ss.fff}");
            }

            RefreshAll();
        }
        catch
        {
            // Never throw from a refresh path (HANDOFF §3).
        }
    }

    // Suppresses automatic refreshes while a git command started by the app runs:
    // its writes are the app's own, and every one of these paths ends with an
    // explicit RefreshAll. Returns a scope to dispose when the operation is done.
    private IDisposable SuspendWatcher() => _watcher.Suspend();

    /// <summary>
    ///  Populates View → Language with the catalogues shipped next to the
    ///  executable and applies the persisted choice. Falls back to English when the
    ///  remembered language no longer has an <c>.xlf</c> (e.g. after a partial
    ///  install). Never throws: a translation problem must not stop the shell.
    ///
    ///  <para>The common path does <b>no</b> work: the start-up pre-load already
    ///  discovered the languages and installed the catalogue, so this just hands the
    ///  list to the menu. The disk scan / parse below only happens when the pre-load
    ///  did not run (English) or did not produce what was asked for.</para>
    /// </summary>
    private async Task InitializeTranslationsAsync()
    {
        try
        {
            IReadOnlyList<string> languages = TranslationService.PreloadedLanguages
                ?? await Task.Run(TranslationService.AvailableLanguages);

            string wanted = _uiState.Language;
            if (!languages.Any(l => string.Equals(l, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                wanted = TranslationService.EnglishLanguage;
            }

            _menu.SetLanguages(languages, wanted);

            if (!string.Equals(wanted, TranslationService.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                // Only reached if the pre-load was skipped or failed. Raises
                // LanguageChanged → every wired view re-labels itself.
                await TranslationService.LoadAsync(wanted);
            }
        }
        catch
        {
            // English stays; nothing else to do.
        }
    }

    /// <summary>
    ///  Applies and persists a language chosen from View → Language. The catalogue
    ///  is parsed in the background and every wired view re-labels itself when
    ///  <see cref="TranslationService.LanguageChanged"/> fires, so no restart is
    ///  needed (unlike upstream, whose Appearance page says "restart required").
    /// </summary>
    private async Task ChangeLanguageAsync(string language)
    {
        try
        {
            _uiState.Language = language;
            _uiStateService.Save(_uiState);
            await TranslationService.LoadAsync(language);
        }
        catch
        {
            // A failed load leaves the previous catalogue in place.
        }
    }

    // Captures the current window size and splitter panel sizes and saves them.
    private void PersistLayout()
    {
        try
        {
            CaptureSplitStars();
            CaptureNormalPlacement();
            _uiState.SplitView = _splitHorizontal;

            // Save the RESTORED bounds, not the maximized frame, so un-maximizing
            // after a restart lands the window back where the user had it.
            _uiState.WindowWidth = _normalWidth;
            _uiState.WindowHeight = _normalHeight;
            _uiState.WindowX = _normalPosition?.X;
            _uiState.WindowY = _normalPosition?.Y;
            _uiState.WindowMaximized = WindowState == WindowState.Maximized;
            _uiState.BottomTab = CurrentBottomTabKey();
            // Save the pre-collapse width, not the collapsed 0: Sanitize() would clamp
            // that back to the default and the user's width would be lost.
            _uiState.LeftPanelCollapsed = !_tree.IsVisible;
            _uiState.TreeWidth = _tree.IsVisible ? _treeCol.Width.Value : _treeWidthBeforeCollapse;
            _uiState.CommitInfoPosition = _commitInfoPosition.ToString();

            // What the desktop preferred this run, so the next one can paint its first
            // window in that variant instead of waiting for the portal (see
            // Theming/SystemTheme). Saved whatever the theme setting is: it is an
            // observation, not a choice.
            _uiState.SystemThemeSeen = Theming.SystemTheme.LastSeenName;
            _uiState.LastRepoPath = _repoPath;
            SaveRepoTabs();
            // The two splits go out as the raw star weights the grid holds, which after a
            // GridSplitter drag are pixel magnitudes (Avalonia rewrites a dragged star
            // definition with its current extent). UiStateService normalizes each PAIR to
            // proportions summing to 1 before writing, so the restore below no longer
            // depends on the window size the split was dragged at.
            _uiState.RevisionsStar = _revRow.Height.Value;
            _uiState.BottomStar = _bottomRow.Height.Value;
            _uiState.DetailStar = _detailRow.Height.Value;
            _uiState.DiffStar = _diffRow.Height.Value;
            _uiState.GridViewOptions = new Dictionary<string, bool>(_revisions.PersistedViewOptions);
            _uiState.GridPageSize = _revisions.PageSize;
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
        Detach(_progressBanner);

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
        // Row 0 is the "a git operation is in progress" banner; it collapses to
        // nothing when the repository is idle.
        _right.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _right.RowDefinitions.Add(_revRow);
        _right.RowDefinitions.Add(new RowDefinition(new GridLength(4, GridUnitType.Pixel)));
        _right.RowDefinitions.Add(_bottomRow);

        Control top = detailBelow ? _revisions : BuildGraphWithSideDetail();

        GridSplitter rightSplit = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(_progressBanner, 0);
        Grid.SetRow(top, 1);
        Grid.SetRow(rightSplit, 2);
        Grid.SetRow(_bottom, 3);
        _right.Children.Add(_progressBanner);
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
            Text = T("Commit info is shown beside the graph. The diff is in the Diff tab."),
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
                ? T("Split view: commit detail and diff side by side")
                : T("Split view on (applies with the commit info below the graph)"))
            : T("Split view off: the diff is in its own tab"));
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
            CommitInfoPosition.LeftOfGraph => T("FormBrowse/commitInfoLeftwardMenuItem.Text", "Commit info: left of graph"),
            CommitInfoPosition.RightOfGraph => T("FormBrowse/commitInfoRightwardMenuItem.Text", "Commit info: right of graph"),
            _ => T("FormBrowse/commitInfoBelowMenuItem.Text", "Commit info: below graph"),
        });
    }

    private void WireEvents()
    {
        _revisions.RevisionSelected += OnRevisionSelected;
        _revisions.RangeSelected += OnRangeSelected;

        // Letting go of the selection puts the panes back to the state they are born
        // in — the same reset a repository switch performs, and for the same reason:
        // what they were describing is no longer selected.
        _revisions.SelectionCleared += ResetBottomPanes;
        // The two artificial top rows both open the commit dialog on the repo.
        _revisions.WorkingDirectorySelected += () =>
        {
            if (_repoPath is not null) _ = ShowCommitDialogAsync();
        };
        _revisions.CommitIndexSelected += () =>
        {
            if (_repoPath is not null) _ = ShowCommitDialogAsync();
        };
        // Double-click / Enter on a revision brings its details forward, the way
        // the original opens the commit-diff window. The artificial rows already
        // open the commit dialog on single click, so they are not re-raised here.
        _revisions.RevisionActivated += _ => Dispatcher.UIThread.Post(() => _bottom.SelectedItem = _commitInfoTab);

        // The two artificial rows are not commits, so RevisionSelected is not raised
        // for them. Since M64 the four tabs have real content for those rows: Diff and
        // File tree from the worktree/index modes of DiffService, Commit details and GPG
        // from a placeholder that names the row (they have no commit object). The
        // sentinel hash is the lazy-load key, so only the visible tab loads (item 1.13).
        _revisions.ArtificialRevisionSelected += (kind, hash) =>
        {
            _artificialRowSelected = true;
            // Which side comes from the event's KIND, never from the sentinel hash:
            // the grid's own WorkTreeHash/IndexHash constants are swapped with respect
            // to the core's ObjectId.WorkTreeId/IndexId, which is what DiffService
            // derives from, so mapping by hash showed the staged diff for the
            // working-directory row (seen on screen).
            _artificialWhich = kind == RevisionGridView.ArtificialRevision.Index
                ? ArtificialDiff.Index
                : ArtificialDiff.WorkTree;
            _artificialHash = hash;
            _diffShowsRange = false;
            _detailLoadedFor = null;
            _diffLoadedFor = null;
            _fileTreeLoadedFor = null;
            _gpgLoadedFor = null;
            LoadSelectedBottomTab();
        };
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
        // The blame view's own details panel navigates the grid the same way, and
        // "Show changes" on a blamed line selects that commit and brings its diff
        // forward (upstream opens FormCommitDiff; the port already shows the diff of
        // the selected revision in the bottom panel).
        _blame.CommitNavigated += h =>
        {
            if (_repoPath is not null)
            {
                _revisions.SelectCommit(h);
                OnRevisionSelected(h);
            }
        };
        _blame.ShowChangesRequested += h =>
        {
            if (_repoPath is not null)
            {
                _revisions.SelectCommit(h);
                OnRevisionSelected(h);
                Dispatcher.UIThread.Post(() => _bottom.SelectedItem = _diffTab);
            }
        };

        _tree.OperationCompleted += RefreshAll;
        _revisions.OperationCompleted += RefreshAll;
        _tree.RefSelected += OnRevisionSelected;
        _tree.OpenRepositoryRequested += OpenRepositoryPath;
        // A single click on a submodule or worktree: shown in the preview tab, which the
        // next single click replaces. With the strip turned off OpenRepository ignores a
        // preview outright, so the tree keeps its old "only a double click opens" feel.
        _tree.PreviewRepositoryRequested += path =>
        {
            if (Theming.RepoTabsOption.Enabled && Directory.Exists(path) && !SameRepositoryPath(path, _repoPath))
            {
                _statusBar.SetText(TF("Opening repository: {0}", path));
                OpenRepository(path, pinned: false);
            }
        };
        _tree.OpenRepositoryInNewInstanceRequested += path =>
        {
            // A submodule row that IS the current repository asks for a second window
            // (upstream's SubmoduleNode.OnDoubleClick). One case now means something
            // else: it is the row a single click just put in the PREVIEW tab, and the
            // double click that followed is the pin gesture — not a request for another
            // window. Anything else (a pinned tab, the strip turned off) still spawns
            // the instance.
            if (Theming.RepoTabsOption.Enabled
                && _repoTabs.Active is { Pinned: false } preview
                && SameRepositoryPath(path, preview.Path))
            {
                _repoTabs.Pin(preview);
                return;
            }

            _statusBar.SetText(TF("Opening repository in a new instance: {0}", path));
            _toolbar.OpenRepositoryInNewInstance(path);
        };
        _tree.FeedbackRequested += message => _statusBar.SetText(message);
        // The tree cannot open a window or run the streaming remote ops itself;
        // without these its stash and fetch-all entries stay disabled rather than dead.
        _tree.StashDialogRequested +=
            initialStash => _ = ShowStashDialogAsync(initialStash: initialStash);
        _tree.FetchAllRequested += () => RunRemoteOp(
            "Fetch all", (s, _, emit, creds) => s.FetchAllStreaming(_repoPath!, emit, creds));
        _tree.FetchAndPruneAllRequested += () => RunRemoteOp(
            "Fetch and prune all",
            (s, _, emit, creds) => s.FetchAndPruneAllStreaming(_repoPath!, emit, creds));
        _tree.CategoryOrder = _uiState.LeftPanelCategoryOrder;
        _tree.CategoryOrderChanged += () => _uiState.LeftPanelCategoryOrder = _tree.CategoryOrder;

        _dashboard.RepositorySelected += repo =>
        {
            if (Directory.Exists(repo))
            {
                OpenRepository(repo);
            }
            else
            {
                _statusBar.SetText(TF("Repository no longer exists: {0}", repo));
            }
        };
        _dashboard.OpenOtherRequested += () => _ = PickRepositoryAsync();
        // Assigning a category (or dropping a favorite) from the dashboard has to reach
        // the two other places that list favorites: the Start menu builds them eagerly,
        // the toolbar dropdown lazily through FavoriteReposProvider — so it only needs
        // the menu rebuilt here to stop showing a stale set.
        _dashboard.FavoritesChanged += () =>
        {
            _menu.SetFavoriteRepositories(_favoritesService.Load());
            if (_dashboardShowing)
            {
                _ = LoadDashboardAsync();
            }
        };

        _diff.BlameRequested += path => ShowInBottom(_blameTab, () => _blame.ShowBlame(_repoPath!, path));
        _diff.FileHistoryRequested += path => OpenFileHistoryWindow(path);
        // Safe to wire now that the grid guards against rebind re-entrancy (0.19).
        _diff.FilterFileInGridRequested +=
            path => _revisions.ApplyRevisionFilter(_revisions.CurrentFilter with { PathFilter = path });
        // Same two jumps from the file tree, now that it is a real tree.
        _fileTree.BlameRequested += path => ShowInBottom(_blameTab, () => _blame.ShowBlame(_repoPath!, path));
        _fileTree.FileHistoryRequested +=
            path => OpenFileHistoryWindow(path);

        // Toolbar actions.
        _toolbar.OpenRepoRequested += () => _ = PickRepositoryAsync();
        _toolbar.RefreshRequested += RefreshAll;
        _toolbar.CommitRequested += OpenCommitDialog;
        _toolbar.FetchRequested += () => RunRemoteOp("Fetch", (s, r, emit, creds) => s.FetchStreaming(_repoPath!, r, emit, creds));

        // Pull is a split button: the body runs the persisted default action, the
        // arrow menu picks one explicitly, and "Set default…" writes it back to the
        // UI state (the toolbar applies it to itself, the host only persists it).
        _toolbar.DefaultPullAction = Enum.TryParse(_uiState.DefaultPullAction, out GitPullAction restored)
            ? restored
            : GitPullAction.Merge;
        _toolbar.PullActionRequested += RunPullAction;
        _toolbar.OpenPullDialogRequested += OpenPullDialog;
        _toolbar.DefaultPullActionChanged += action =>
        {
            _uiState.DefaultPullAction = action.ToString();
            _uiStateService.Save(_uiState);
        };

        _toolbar.PushRequested += OpenPushDialog;
        _toolbar.StashRequested += () => RunOp("Stash", () => _stashOps.StashSave(_repoPath!, "WIP", StashOpsService.ManualStashUntracked()).Success);
        _toolbar.NewBranchRequested += () => _ = NewBranchAsync();

        // The toolbar shows the real gestures, not the defaults, so an override in
        // hotkeys.json is reflected in its tooltips.
        _toolbar.Hotkeys = _hotkeys;
        _menu.Hotkeys = _hotkeys;

        // Same reason for the commit-info panel: its "Add notes" entry advertises
        // Ctrl+Shift+N and must show an override rather than the shipped default.
        _detail.Hotkeys = _hotkeys;

        // Editing a binding in Settings must re-label the toolbar and the menu, or they
        // keep advertising the old gesture. Both setters rebuild only when the reference
        // changes, so bounce it — then push back the state a rebuild resets.
        _hotkeys.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _toolbar.Hotkeys = null;
            _toolbar.Hotkeys = _hotkeys;
            _menu.Hotkeys = null;
            _menu.Hotkeys = _hotkeys;

            // This setter re-labels on every assignment, so it needs no bounce.
            _detail.Hotkeys = _hotkeys;

            _toolbar.SetLeftPanelVisible(_tree.IsVisible);
            _toolbar.SetCommitInfoPosition(_commitInfoPosition);
            _toolbar.SetSplitView(_splitHorizontal);
            _menu.SetViewOptions(_revisions.ViewOptions);
            UpdateMenuRepositoryState();
            RefreshToolbarState();
        });
        _toolbar.ManageStashesRequested += () => _ = ShowStashDialogAsync();
        _toolbar.CreateStashRequested +=
            () => _ = ShowStashDialogAsync(manageStashes: false, create: true);
        _toolbar.StashStagedRequested +=
            () => RunOp("Stash staged", () => _stashOps.StashStaged(_repoPath!, "WIP").Success);
        _toolbar.StashPopRequested +=
            () => RunOp("Stash pop", () => _stashOps.StashPop(_repoPath!, "stash@{0}").Success);
        _toolbar.SettingsRequested += () => _ = OpenSettingsAsync();
        _toolbar.ToggleLeftPanelRequested += ToggleLeftPanel;
        _toolbar.CheckoutBranchRequested += () => _ = CheckoutBranchPickerAsync();
        _toolbar.ManageWorktreesRequested += () => _ = ShowWorktreesAsync();
        _toolbar.CreateWorktreeRequested += () => _ = ShowWorktreesAsync();
        _toolbar.PruneWorktreesRequested += () => RunOp(
            "Prune worktrees", () => new WorktreeService().PruneWorktrees(_repoPath!).Success);

        // View / layout + external-tool toolbar actions.
        _toolbar.SplitViewToggleRequested += ToggleSplitView;
        _toolbar.CommitInfoPositionChanged += SetCommitInfoPosition;
        _toolbar.FileExplorerRequested += () => WithRepo(p => _externalTools.OpenPath(p));
        _toolbar.OpenTerminalRequested += () => WithRepo(p => _externalTools.OpenTerminal(p));
        // The shell split button names the shell to launch; unwired it would fall back
        // to the default terminal, which is a worse but not wrong behaviour.
        _toolbar.OpenShellRequested += exe => WithRepo(p => _externalTools.OpenTerminal(p, exe));
        _toolbar.CloseRepositoryRequested += CloseActiveRepository;

        // The toolbar no longer carries a branch-scope menu or a filter box of its
        // own: upstream's ToolStripFilters lives, whole, on the revision grid's bar,
        // and the grid drives its own scope/filter logic from there. Nothing to wire.

        // Submodules / worktrees split-button dropdowns. Providers list off the UI
        // thread; choosing an entry opens that path as the active repository. The
        // submodules list is prefixed with a "level-up" entry when the current repo
        // is itself a submodule/subdir of a parent (super-project).
        _toolbar.SubmodulesProvider = async () =>
        {
            if (_dashboardShowing || _repoPath is not { Length: > 0 } repo)
            {
                return Array.Empty<RepoLink>();
            }

            int epoch = _repositoryEpoch;
            await EnsureCoreWarmupAsync(repo).ConfigureAwait(false);
            Task<RepositoryNavigationSnapshot>? active = GetOrReacquireNavigationAsync(repo, epoch);
            if (active is null)
            {
                return Array.Empty<RepoLink>();
            }

            RepositoryNavigationSnapshot snapshot = await active.ConfigureAwait(false);
            if (_dashboardShowing || !SameRepositoryPath(repo, _repoPath))
            {
                return Array.Empty<RepoLink>();
            }

            SubmoduleHierarchy hierarchy = snapshot.Submodules;
            List<RepoLink> links = [];
            if (hierarchy.ImmediateSuperprojectPath is { Length: > 0 } parent)
            {
                links.Add(new RepoLink($"⬆ Parent super-project ({Path.GetFileName(parent.TrimEnd('/', '\\'))})", parent, "NavigateUp"));
            }

            foreach (SubmoduleRow row in hierarchy.Nodes)
            {
                if (row.Exists && !row.IsCurrent && !SameRepositoryPath(row.AbsolutePath, hierarchy.ImmediateSuperprojectPath))
                {
                    string text = row.Path.Length == 0 ? Path.GetFileName(hierarchy.RootPath) : row.Display;
                    links.Add(new RepoLink(text, row.AbsolutePath, "FolderSubmodule"));
                }
            }

            return links;
        };
        _toolbar.WorktreesProvider = async () =>
        {
            if (_dashboardShowing || _repoPath is not { Length: > 0 } repo)
            {
                return Array.Empty<RepoLink>();
            }

            int epoch = _repositoryEpoch;
            await EnsureCoreWarmupAsync(repo).ConfigureAwait(false);
            Task<RepositoryNavigationSnapshot>? active = GetOrReacquireNavigationAsync(repo, epoch);
            if (active is null)
            {
                return Array.Empty<RepoLink>();
            }

            RepositoryNavigationSnapshot snapshot = await active.ConfigureAwait(false);
            if (_dashboardShowing || !SameRepositoryPath(repo, _repoPath))
            {
                return Array.Empty<RepoLink>();
            }

            List<RepoLink> links = [];
            foreach (WorktreeRow row in snapshot.Worktrees)
            {
                // The current worktree is ticked and inert; a prunable one is dimmed.
                bool current = row.IsSamePath(repo);
                links.Add(new RepoLink(
                    row.Display, row.Path, "WorkTree",
                    IsChecked: current,
                    IsEnabled: !current && !row.IsPrunable,
                    IsDim: row.IsPrunable));
            }

            return links;
        };
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
        _toolbar.BranchCheckoutRequested += name => _ = CheckoutBranchAsync(name);

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
                _statusBar.SetText(TF("Favorite no longer exists: {0}", repo));
            }
        };
        _menu.AddFavoriteRequested += AddCurrentToFavorites;
        _menu.DashboardRequested += ShowDashboard;
        _menu.ExitRequested += Close;
        _menu.RefreshRequested += RefreshAll;
        _menu.ShowReflogRequested += () => _ = ShowReflogAsync();
        _menu.BisectRequested += () => _ = ShowBisectDialogAsync();
        // Theme and style are orthogonal, so each handler changes only its own
        // dimension in _uiState and then re-applies BOTH from it: whichever of the
        // two the user did not touch keeps the value it had.
        _menu.SystemThemeRequested += () => SetAppearance(theme: Theming.SystemTheme.Name);
        _menu.LightThemeRequested += () => SetAppearance(theme: "Light");
        _menu.DarkThemeRequested += () => SetAppearance(theme: "Dark");
        _menu.ClassicStyleRequested += () => SetAppearance(style: "Classic");
        _menu.ModernStyleRequested += () => SetAppearance(style: "Modern");
        _menu.LanguageRequested += language => _ = ChangeLanguageAsync(language);
        _menu.FetchRequested += () => RunRemoteOp("Fetch", (s, r, emit, creds) => s.FetchStreaming(_repoPath!, r, emit, creds));
        // Upstream's Commands → Pull opens FormPull (DoPull with isSilent: false).
        _menu.PullRequested += OpenPullDialog;
        _menu.PushRequested += OpenPushDialog;
        _menu.CommitRequested += OpenCommitDialog;
        _menu.StashRequested += () => RunOp("Stash", () => _stashOps.StashSave(_repoPath!, "WIP", StashOpsService.ManualStashUntracked()).Success);
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

        // Tools → Scripts. The menu raises, the shell runs: only the shell knows the
        // repository and the selected revision the placeholders are filled from.
        _menu.UserScriptRequested += script => Async.Run(
            () => UserScriptRunner.RunAsync(this, script, ScriptContext(_lastSelectedHash)),
            "running a user script");

        // Plugins: run a plugin (off-thread) / open its settings editor.
        _menu.PluginRunRequested += plugin => RunPlugin(plugin);
        _menu.PluginSettingsRequested += plugin => _ = OpenPluginSettingsAsync(plugin);

        // Repository: file explorer + edit repo config files (created if absent).
        _menu.FileExplorerRequested += () => WithRepo(p => _externalTools.OpenPath(p));
        _menu.EditGitignoreRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".gitignore")));
        _menu.EditGitattributesRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".gitattributes")));
        _menu.EditMailmapRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".mailmap")));
        _menu.EditInfoExcludeRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".git", "info", "exclude")));
        _menu.RecoverLostObjectsRequested += () => _ = OpenVerifyAsync();
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
        _menu.RevisionFilterRequested += () => _ = _revisions.ShowFilterDialogAsync();

        // The Navigate/View menus are a mirror of the grid's own command surface: the
        // grid stays the single source of truth, and the menu's check marks follow it.
        _menu.GridCommandRequested += id => _revisions.ExecuteMenuCommand(id);
        _revisions.ViewOptionsChanged += options => _menu.SetViewOptions(options);
        _menu.SetViewOptions(_revisions.ViewOptions);
        _menu.ResetRevisionFiltersRequested += () => _revisions.ResetAllFilters();
        _menu.ChangelogRequested += () => Surface(_externalTools.OpenUrl("https://github.com/gitextensions/gitextensions/releases"));
        _menu.DonateRequested += () => Surface(_externalTools.OpenUrl("https://opencollective.com/gitextensions"));

        // ---- menu bar: GitHub. Fork-and-clone is the one entry that works with no
        // repository open — it is how you get one — so it is not gated on _repoPath.
        _menu.GitHubMenuOpening += () => _menu.SetGitHubState(
            _repoPath is { Length: > 0 } repo && new GitHubService().IsRelevantTo(repo));
        _menu.GitHubForkCloneRequested += () => _ = ShowGitHubForkCloneAsync();
        _menu.GitHubCreatePullRequestRequested += () => _ = ShowGitHubCreatePullRequestAsync();
        _menu.GitHubViewPullRequestsRequested += () => _ = ShowGitHubPullRequestsAsync();
        _menu.GitHubAddUpstreamRequested += () => _ = AddGitHubUpstreamAsync();

        // ---- menu bar: the Repository dialogs, Git maintenance and the state gating
        _menu.RemotesRequested += () => _ = ShowRemotesAsync();
        _menu.RemoteOperationsRequested += () => _ = ShowRemoteOperationsAsync();
        _menu.BranchTagWorkbenchRequested += () => _ = ShowBranchTagWorkbenchAsync();
        _menu.SubmodulesRequested += () => _ = ShowSubmodulesAsync();
        _menu.WorktreesRequested += () => _ = ShowWorktreesAsync();
        _menu.UpdateAllSubmodulesRequested += () => RunOp(
            T("FormBrowse/updateAllSubmodulesToolStripMenuItem.Text", "Update all submodules"),
            () => new SubmoduleService().UpdateAll(_repoPath!).Success);
        _menu.SynchronizeAllSubmodulesRequested += () => RunOp(
            T("FormBrowse/synchronizeAllSubmodulesToolStripMenuItem.Text", "Synchronize all submodules"),
            () => new SubmoduleService().SynchronizeAll(_repoPath!).Success);
        _menu.CompressDatabaseRequested += () => RunOp(
            T("FormBrowse/compressGitDatabaseToolStripMenuItem.Text", "Compress git database"),
            () => new MaintenanceService().CompressDatabase(_repoPath!).Success);
        _menu.DeleteIndexLockRequested += () => RunOp(
            T("FormBrowse/deleteIndexLockToolStripMenuItem.Text", "Delete index.lock"),
            () => new MaintenanceService().DeleteIndexLock(_repoPath!).Success);
        _menu.EditGitConfigRequested +=
            () => WithRepo(p => _externalTools.OpenOrCreateFile(new MaintenanceService().ResolveConfigPath(p)));
        _menu.DashboardRefreshRequested += () => _ = LoadDashboardAsync();

        // Upstream recomputes the selection-dependent Commands entries as the menu
        // drops down (CommandsToolStripMenuItem_DropDownOpening); same moment here.
        _menu.CommandsMenuOpening += () =>
        {
            (int count, bool allNonArtificial) = _revisions.SelectionSummary;
            _menu.SetSelectionState(count, allNonArtificial);
        };

        // Commit-targeted operations on the revision grid — registered on BOTH grids:
        // the repository one and the second instance inside the File history tab, whose
        // row menu is the same menu. Registering only on the first left its Reset /
        // Advanced / Compare / Bisect submenus empty in that tab (they are built from
        // these registrations), i.e. dead entries.
        //
        // The file-history view registers a few of these itself, before this runs, so
        // that its own file-aware handlers ("Save as", "Copy path") and its
        // host-or-local revert/cherry-pick contract keep priority: the grid keeps the
        // FIRST registration of a given header and drops later duplicates.
        void Register(string header, Action<string> handler)
        {
            _commitCommands.Add((header, handler));
            _revisions.AddCommitCommand(header, handler);
        }

        // The scripts marked "add to the revision grid context menu", refreshed whenever
        // the list is saved. Replaced rather than appended: a script renamed in Settings
        // must not leave its old name behind in the menu.
        void RefreshScriptCommands()
        {
            List<(string Header, Action<string> Handler)> commands = [];
            foreach (UserScript script in new UserScriptService().Load())
            {
                if (!script.Enabled || !script.AddToRevisionGridContextMenu)
                {
                    continue;
                }

                UserScript captured = script;
                commands.Add((
                    captured.Name is { Length: > 0 } name ? name : captured.Command,
                    hash => Async.Run(
                        () => UserScriptRunner.RunAsync(this, captured, ScriptContext(hash)),
                        "running a user script")));
            }

            _revisions.SetScriptCommands(commands);
        }

        RefreshScriptCommands();
        UserScriptService.Changed += () => Dispatcher.UIThread.Post(RefreshScriptCommands);

        Register("Checkout this commit", hash => _ = CheckoutBranchAsync(hash));
        Register("Cherry-pick",
            hash => RunOp("Cherry-pick", () => _stashOps.CherryPick(_repoPath!, hash).Success));
        Register("Reset (soft) to here",
            hash => RunOp("Reset soft", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Soft).Success));
        Register("Reset (mixed) to here",
            hash => RunOp("Reset mixed", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Mixed).Success));
        Register("Reset (HARD) to here…",
            hash => RunOp("Reset hard", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Hard).Success, confirm: true));
        Register("Create branch here…", hash => _ = CreateBranchHereAsync(hash));
        Register("Create tag here…", hash => _ = CreateTagHereAsync(hash));
        Register("Revert this commit…", RevertThisCommit);
        Register("Archive this commit…", hash => _ = ArchiveThisCommitAsync(hash));

        // History-rewriting commit edits on the current branch. Each is guarded by a
        // dirty-tree refusal + a confirm dialog, and rebase-backed paths abort cleanly
        // on failure so the repository is never left mid-rebase (see CommitEditService).
        Register("Reword commit…", hash => _ = RewordCommitAsync(hash));
        Register("Squash with previous…", hash => _ = SquashOrFixupAsync(hash, squash: true));
        Register("Fixup with previous…", hash => _ = SquashOrFixupAsync(hash, squash: false));

        // Compare actions. The grid is single-select, so we mirror the original's
        // two-commit compare with a remembered BASE + "Compare to BASE" pair, plus
        // a direct commit-vs-working-tree compare. Results drive the shared DiffView.
        Register("Select as BASE to compare", SelectCompareBase);
        Register("Compare to BASE", CompareToBase);
        Register("Compare to working directory", CompareToWorkingDirectory);
        Register("Compare to branch…", hash => _ = CompareToBranchAsync(hash));

        // Bisect. These four act on an ALREADY OPEN session and the grid disables
        // them when there is none (RevisionGridView.IsBisectInProgress, wired below,
        // mirroring RevisionGridControl.cs:2256-2261). They used to auto-start a
        // session when none was open — `git bisect start` with no prompt and nothing
        // said, so a misclick in a submenu detached HEAD and moved the work tree.
        // Starting is now its own explicit act, through the bisect panel.
        Register("Bisect: start…", hash => { Task ignored = ShowBisectDialogAsync(); });
        Register("Bisect: mark good",
            hash => RunBisect("Bisect good", () => _bisect.MarkGood(_repoPath!, hash)));
        Register("Bisect: mark bad",
            hash => RunBisect("Bisect bad", () => _bisect.MarkBad(_repoPath!, hash)));
        Register("Bisect: skip",
            hash => RunBisect("Bisect skip", () => _bisect.Skip(_repoPath!, hash)));
        Register("Bisect: stop/reset",
            _ => RunBisect("Bisect reset", () => _bisect.Reset(_repoPath!)));

        // The gate itself: one File.Exists on .git/BISECT_START, which is exactly
        // what upstream's Module.InTheMiddleOfBisect() does (GitModule.cs:1968-1971),
        // so it is safe to answer synchronously as the menu opens.
        _revisions.IsBisectInProgress = () => _repoPath is { Length: > 0 } repo
            && _bisect.InTheMiddleOfBisect(repo);
    }

    /// <summary>
    ///  Opens the bisect control panel — upstream's <c>FormBisect</c>, reached from
    ///  the Commands menu (<c>FormBrowse.BisectClick:1805-1813</c>), from the
    ///  notification bar's "More" button, and from the grid's "Start bisect…". The
    ///  grid selection is handed over so the panel can offer upstream's range seeding.
    /// </summary>
    // Opens the conflict-resolution dialog. Returns true when nothing is left
    // unmerged, which is what lets a caller go straight on to the commit dialog
    // (upstream's MergeConflictHandler does the same after solving conflicts).
    private async Task<bool> ShowResolveConflictsDialogAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return false;
        }

        bool resolved;
        using (IDisposable watch = SuspendWatcher())
        {
            resolved = await ResolveConflictsDialog.ShowAsync(this, _repoPath);
        }

        RefreshAll();
        return resolved;
    }

    private async Task ShowBisectDialogAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        BisectDialog dialog = new(_repoPath, _revisions.SelectedCommitHashes);

        using (IDisposable watch = SuspendWatcher())
        {
            await dialog.ShowDialog(this);
        }

        if (dialog.RepositoryChanged)
        {
            RefreshAll();
        }
    }

    // Opens the stash dialog, upstream's UICommands.StartStashDialog: a modal FormStash,
    // not a tab. Every stash surface of the port — the toolbar's split button, the
    // Commands menu, the left tree's "Open stash" and "Manage stashes…" — comes through
    // here, so they all land on the same window with the same two arguments.
    private async Task ShowStashDialogAsync(
        bool manageStashes = true, string? initialStash = null, bool create = false)
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        StashWindow window = new(repo, manageStashes, initialStash);
        if (create)
        {
            // The prompt is a modal of the stash window, so it can only be opened once
            // that window is up — Opened, not the constructor.
            window.Opened += (_, _) => window.BeginCreateStash();
        }

        await window.ShowDialog(this);

        if (window.Changed)
        {
            RefreshAll();
        }
    }

    // Opens the modal reflog browser; on a checkout from it, refreshes the main
    // view. Mirrors the other dialog-launch helpers (e.g. RemotesDialog).
    private async Task ShowReflogAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        ReflogWindow window = new(_repoPath);
        await window.ShowDialog(this);

        if (window.CheckedOut)
        {
            RefreshAll();
        }
    }

    // Runs one bisect step off the UI thread, then surfaces git's output (next commit
    // to test / first bad commit) in the status bar and refreshes the grid. Never
    // throws, and never starts a session on its own: the caller's entry is disabled
    // unless one is already open.
    private void RunBisect(string label, Func<BisectResult> op)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            using IDisposable watch = SuspendWatcher();
            _statusBar.SetText(TF("{0}…", label));
            BisectResult result;
            try
            {
                result = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _statusBar.SetText(TF("{0} failed: {1}", label, ex.Message));
                return;
            }

            RefreshAll();

            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText(result.Success
                ? (firstLine.Length > 0 ? TF("{0}: {1}", label, firstLine) : TF("{0} done.", label))
                : TF("{0} failed: {1}", label, firstLine));
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
            // The dialog's revision box is editable, so report what it actually archived
            // rather than the grid row we opened it on.
            string archived = dlg.ArchivedRevision ?? hash;
            string shortHash = archived.Length > 8 ? archived[..8] : archived;
            _statusBar.SetText(TF("Archived {0} → {1}", shortHash, path));
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
            _statusBar.SetText(TF("{0} failed: {1}", label, ex.Message));
            return false;
        }

        if (dirty)
        {
            _statusBar.SetText(TF("{0} refused: you have uncommitted changes. Commit or stash them first.", label));
            return false;
        }

        return await ConfirmAsync(T("This rewrites history on the current branch. Continue?"));
    }

    // Runs a commit-edit operation off the UI thread, then refreshes the grid and
    // surfaces success or the first line of git's output on failure. The service
    // already aborts a stuck rebase, so this never leaves a half-rebase behind.
    private async Task RunEditAsync(string label, Func<CommitEditResult> op)
    {
        using IDisposable watch = SuspendWatcher();
        _statusBar.SetText(TF("{0}…", label));
        CommitEditResult result;
        try
        {
            result = await Task.Run(op);
        }
        catch (Exception ex)
        {
            _statusBar.SetText(TF("{0} failed: {1}", label, ex.Message));
            return;
        }

        RefreshAll();
        if (result.Success)
        {
            _statusBar.SetText(TF("{0} done.", label));
        }
        else
        {
            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText(TF("{0} failed: {1}", label, firstLine));
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
            _statusBar.SetText(TF("{0} failed: {1}", T("Reword"), ex.Message));
            return;
        }

        string? message = await PromptAsync(T("Reword commit"), T("New commit message:"), current.Trim(), multiline: true);
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
            _statusBar.SetText(TF("{0} failed: {1}", label, ex.Message));
            return;
        }

        if (!hasParent)
        {
            _statusBar.SetText(TF("{0} not possible: the root commit has no previous commit to combine with.", label));
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
                _statusBar.SetText(TF("{0} failed: {1}", T("Squash"), ex.Message));
                return;
            }

            string? message = await PromptAsync(T("Squash with previous"), T("Combined commit message:"), combined.Trim(), multiline: true);
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

    // Checks out a branch (or a bare commit) through the F4 dialog: with a clean
    // working tree it runs straight away, otherwise the dialog asks what to do
    // with the pending changes (don't change / merge / reset / stash). A null
    // answer means the user cancelled.
    private async Task CheckoutBranchAsync(string name)
    {
        if (_repoPath is null)
        {
            return;
        }

        GitCommands.LocalChangesAction? action = await CheckoutBranchDialog.AskAsync(this, _repoPath, name);
        if (action is null)
        {
            return;
        }

        await RunRefProcessAsync(
            TF("Checkout {0}", name),
            () => RefProcessRunner.CheckoutAsync(this, _repoPath!, name, action.Value));
    }

    // Creates a branch at the selected commit through the F4 dialog (name +
    // "checkout after create", which is the normal case upstream).
    private async Task CreateBranchHereAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        CreateBranchRequest? request = await CreateBranchDialog.AskAsync(this, _repoPath, hash);
        if (request is null)
        {
            return;
        }

        await RunRefProcessAsync(
            TF("Create branch {0}", request.Name),
            () => RefProcessRunner.CreateBranchAsync(
                this, _repoPath!, request.Name, hash, request.Checkout));
    }

    // Creates a tag at the selected commit through the F4 dialog: lightweight or
    // annotated/signed, force, and an optional push right after creating it.
    private async Task CreateTagHereAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        CreateTagRequest? request = await CreateTagDialog.AskAsync(this, _repoPath, hash);
        if (request is null)
        {
            return;
        }

        RunOp($"Create tag {request.Name}",
            () => new BranchTagService().CreateTag(
                _repoPath!, request.Name, commit: hash, request.Message,
                request.Operation, request.SignKeyId, request.Force, request.PushToRemote).Success);
    }

    // Runs an external-tool action that needs the current repo, surfacing its
    // result (or a "no repository" note) in the status bar. Never throws: the
    // service catches launch failures and returns them as a failed result.
    private void WithRepo(Func<string, ExternalToolResult> action)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        // Off the UI thread: launching an external tool means starting a process, and
        // the terminal launcher deliberately waits a moment to see whether the terminal
        // it started survived (ExternalToolService.LaunchTerminal). On the UI thread
        // that wait is a visible freeze of the whole window.
        string repo = _repoPath;
        Async.OffUi(() => action(repo), Surface, "launching the external tool");
    }

    // Reflects an external-tool result in the status bar; failures are reported
    // as text rather than thrown, so a missing tool never crashes the UI.
    private void Surface(ExternalToolResult result) => _statusBar.SetText(result.Message);

    /// <summary>
    ///  What a user script run from the shell can substitute: the repository, the
    ///  and the revision the user has in hand. The branch is left to <c>{cBranch}</c>'s
    ///  own lookup (the shell holds it only as display text). Everything about the
    ///  revision beyond its hash is left to the script — <c>git show</c> on
    ///  <c>{sHash}</c> is one command, and reading it here would cost a git call per
    ///  menu opening.
    /// </summary>
    private UserScriptContext ScriptContext(string? hash) => new(
        _repoPath ?? string.Empty,
        SelectedHashes: hash is { Length: > 0 } ? [hash] : []);

    private void OnRevisionSelected(string commitHash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _lastSelectedHash = commitHash;
        _artificialRowSelected = false;
        _artificialHash = null;
        _diffShowsRange = false;
        _statusBar.SetText(string.Empty);

        // Upstream loads only the tab that is actually showing (FormBrowse.cs:1240,
        // 1251, 1306) and marks the others stale. Loading all four on every selection
        // fired four git chains — including --show-signature and a full ls-tree -r —
        // of which three were invisible.
        _detailLoadedFor = null;
        _diffLoadedFor = null;
        _fileTreeLoadedFor = null;
        _gpgLoadedFor = null;

        // Upstream refreshes whichever tab is showing and leaves it there. Forcing the
        // panel back to Commit made it impossible to watch Output, Diff or File tree
        // while browsing revisions. Double click still pulls the panel onto Commit —
        // that is RevisionActivated, wired separately.
        LoadSelectedBottomTab();
    }

    // Brings the visible bottom tab up to date with the current selection; the other
    // tabs stay stale until they are shown. Cheap and idempotent.
    private void LoadSelectedBottomTab()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        if (_artificialRowSelected)
        {
            LoadSelectedBottomTabForArtificial(repo);
            return;
        }

        if (_lastSelectedHash is not { Length: > 0 } hash)
        {
            return;
        }

        object? tab = _bottom.SelectedItem;
        if (ReferenceEquals(tab, _commitInfoTab) && _detailLoadedFor != hash)
        {
            _detailLoadedFor = hash;
            _detail.ShowCommit(repo, hash);
        }
        else if (ReferenceEquals(tab, _diffTab) && !_diffShowsRange && _diffLoadedFor != hash)
        {
            _diffLoadedFor = hash;
            _diff.ShowCommit(repo, hash);
        }
        else if (ReferenceEquals(tab, _fileTreeTab) && _fileTreeLoadedFor != hash)
        {
            _fileTreeLoadedFor = hash;
            _fileTree.ShowCommit(repo, hash);
        }
        else if (ReferenceEquals(tab, _gpgTab) && _gpgLoadedFor != hash)
        {
            _gpgLoadedFor = hash;
            _gpg.ShowCommit(repo, hash);
        }
    }

    // Same lazy dispatch for the artificial rows: the sentinel hash is the key, and
    // "which" is recovered from it so a stale event can never load the wrong side.
    private void LoadSelectedBottomTabForArtificial(string repo)
    {
        if (_artificialHash is not { Length: > 0 } hash)
        {
            return;
        }

        ArtificialDiff which = _artificialWhich;

        object? tab = _bottom.SelectedItem;
        if (ReferenceEquals(tab, _commitInfoTab) && _detailLoadedFor != hash)
        {
            _detailLoadedFor = hash;
            _detail.ShowArtificial(repo, which);
        }
        else if (ReferenceEquals(tab, _diffTab) && _diffLoadedFor != hash)
        {
            _diffLoadedFor = hash;
            _diff.ShowArtificial(repo, which);
        }
        else if (ReferenceEquals(tab, _fileTreeTab) && _fileTreeLoadedFor != hash)
        {
            _fileTreeLoadedFor = hash;
            _fileTree.ShowArtificial(repo, which);
        }
        else if (ReferenceEquals(tab, _gpgTab) && _gpgLoadedFor != hash)
        {
            _gpgLoadedFor = hash;
            _gpg.ShowArtificial(which);
        }
    }

    // Two or more commits selected in the grid: hand the WHOLE selection (newest
    // first) to the diff pane, which decides how many comparisons it stands for —
    // one range, a group per extra revision, or a merge base with a side each. The
    // status line keeps naming the two ends, which is what the user picked.
    private void OnRangeSelected(IReadOnlyList<string> revisions)
    {
        if (_repoPath is null || revisions.Count < 2)
        {
            return;
        }

        string baseHash = revisions[^1];
        string otherHash = revisions[0];

        _diffShowsRange = true;
        _diff.ShowRevisions(_repoPath, revisions);
        FocusDiff();
        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = otherHash.Length > 8 ? otherHash[..8] : otherHash;
        _statusBar.SetText(TF("Comparing {0}..{1}", shortBase, shortOther));
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
        _statusBar.SetText(TF("Selected {0} as compare BASE. Use \"Compare to BASE\" on another commit.", shortHash));
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
            _statusBar.SetText(T("No BASE selected. Right-click a commit and choose \"Select as BASE to compare\" first."));
            return;
        }

        _diffShowsRange = true;
        _diff.ShowRange(_repoPath, baseHash, hash);
        FocusDiff();

        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText(TF("Comparing {0} .. {1}", shortBase, shortOther));
    }

    // Diffs the selected commit against the current working tree (git diff <hash>)
    // and renders the result in the shared DiffView.
    private void CompareToWorkingDirectory(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _diffShowsRange = true;
        _diff.ShowAgainstWorkingDirectory(_repoPath, hash);
        FocusDiff();

        string shortHash = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText(TF("Comparing {0} .. working tree", shortHash));
    }

    // Lists local branches, lets the user pick one, then diffs that branch vs the
    // selected commit — git diff <branch> <selected> — reusing the shared DiffView
    // compare path (ShowRange), exactly like "Compare to BASE". The branch is the
    // "old" side, the selected commit the "new" side.
    private async Task CompareToBranchAsync(string hash)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
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
            _statusBar.SetText(TF("{0} failed: {1}", T("FormCompareToBranch/$this.Text", "Compare to branch"), ex.Message));
            return;
        }

        if (localBranches.Count == 0)
        {
            _statusBar.SetText(T("No local branches to compare against."));
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

        _diffShowsRange = true;
        _diff.ShowRange(_repoPath, baseRef, hash);
        FocusDiff();

        string shortOther = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText(TF("Comparing {0} .. {1}", chosen.Name, shortOther));
    }

    // Modal single-select branch picker; returns the chosen branch, or null on cancel.
    // The captions default to the "compare to branch" wording it was written for;
    // the checkout hotkey (Ctrl+.) reuses the same picker with its own.
    private async Task<BranchTagRow?> PickBranchAsync(
        IReadOnlyList<BranchTagRow> branches, string? title = null, string? okText = null, string? prompt = null)
    {
        ListBox list = new()
        {
            ItemsSource = branches.Select(b => b.Name).ToList(),
            Background = (IBrush)Application.Current!.Resources["App.Control"]!,
            Foreground = (IBrush)Application.Current!.Resources["App.Text"]!,
            SelectedIndex = 0,
            MinHeight = 220,
        };

        Button ok = new() { Content = okText ?? T("FormCompareToBranch/btnCompare.Text", "Compare"), MinWidth = 90 };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        Theming.ZoomWindow dlg = new()
        {
            Title = title ?? T("FormCompareToBranch/$this.Text", "Compare to branch"),
            Width = 420,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new DockPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = prompt ?? T("Diff against branch (branch .. selected commit):"), Margin = new Thickness(0, 0, 0, 6), [DockPanel.DockProperty] = Dock.Top },
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

    // ------------------------------------------------------------------ hotkeys

    /// <summary>
    ///  Binds an action to every keyboard command the port can actually perform and
    ///  installs the dispatcher (see <see cref="HotkeyService"/> for the map itself
    ///  and for why the handler tunnels).
    ///
    ///  <para>Every action here goes through the SAME method the menu or the toolbar
    ///  calls — nothing is reimplemented for the keyboard, so a hotkey inherits the
    ///  watcher suspension, the off-thread git execution and the refresh that path
    ///  already has.</para>
    ///
    ///  <para>Commands with no port equivalent are left unbound on purpose and are
    ///  inert: EditFile, the four difftool/temp-file gestures (they act on
    ///  the diff's file selection, which the diff view handles itself),
    ///  GoToChild, ManageWorkTrees, MergeBranches, Rebase,
    ///  ToggleBetweenArtificialAndHeadCommits, and FocusBuildServerStatus (this port
    ///  has no build-server tab).</para>
    /// </summary>
    private void InstallHotkeys()
    {
        void Bind(BrowseCommand command, Action action) => _hotkeys.Bind(command, action);

        // --- repository / remote operations (identical to the menu handlers)
        Bind(BrowseCommand.Refresh, RefreshAll);
        Bind(BrowseCommand.OpenRepo, () => _ = PickRepositoryAsync());
        Bind(BrowseCommand.CloseRepository, CloseActiveRepository);
        Bind(BrowseCommand.Commit, OpenCommitDialog);
        Bind(BrowseCommand.OpenSettings, () => _ = OpenSettingsAsync());
        Bind(BrowseCommand.GitBash, () => WithRepo(p => _externalTools.OpenTerminal(p)));

        // Push/pull. The port has no dialog-less push, so the "quick" variants land
        // on the same paths as their full counterparts rather than pretending.
        Bind(BrowseCommand.Push, OpenPushDialog);
        Bind(BrowseCommand.QuickPush, OpenPushDialog);
        // Upstream: Ctrl+Down (PullOrFetch) opens FormPull, F8 (QuickPullOrFetch) is
        // the toolbar button's own click, i.e. the persisted default action, and
        // QuickPull is a silent merge pull.
        Bind(BrowseCommand.PullOrFetch, OpenPullDialog);
        Bind(BrowseCommand.QuickPull, () => RunPullAction(GitPullAction.Merge));
        Bind(BrowseCommand.QuickFetch, Fetch);
        Bind(BrowseCommand.QuickPullOrFetch, () => RunPullAction(_toolbar.DefaultPullAction));

        // --- refs
        Bind(BrowseCommand.CheckoutBranch, () => _ = CheckoutBranchPickerAsync());
        // The menu now advertises Ctrl+Alt+W, so something has to answer it.
        Bind(BrowseCommand.ManageWorkTrees, () => _ = ShowWorktreesAsync());
        Bind(BrowseCommand.CreateBranch, () => _ = NewBranchAsync());
        Bind(BrowseCommand.CreateTag, () => _ = NewTagAsync());
        Bind(BrowseCommand.GoToParent, () => _ = GoToParentAsync());

        // --- git notes on the selected commit (upstream: FormBrowse.AddNotes, which
        // likewise delegates to the commit-info panel rather than reimplementing it).
        // HotkeyService already shipped Ctrl+Shift+N for this command, but nothing
        // answered it, so the gesture was inert and AddNotesDialog unreachable.
        Bind(BrowseCommand.AddNotes, _detail.EditNotes);

        // --- stash (same service calls as the toolbar/menu items)
        Bind(BrowseCommand.Stash,
            () => RunOp("Stash", () => _stashOps.StashSave(_repoPath!, "WIP", StashOpsService.ManualStashUntracked()).Success));
        Bind(BrowseCommand.StashPop,
            () => RunOp("Stash pop", () => _stashOps.StashPop(_repoPath!, "stash@{0}").Success));
        Bind(BrowseCommand.StashStaged,
            () => RunOp("Stash staged", () => _stashOps.StashStaged(_repoPath!, "WIP (staged)").Success));

        // --- search
        Bind(BrowseCommand.FindFileInSelectedCommit, () => _ = FindFileInCommitAsync());
        Bind(BrowseCommand.FindInDiff, FocusDiffSearch);

        // --- focus movement (upstream Ctrl+0…Ctrl+9 / Ctrl+Tab / Ctrl+E)
        Bind(BrowseCommand.FocusLeftPanel, () => FocusInto(_tree));
        Bind(BrowseCommand.FocusRevisionGrid, () => FocusInto(_revisions));
        Bind(BrowseCommand.FocusCommitInfo, () => FocusTab(_commitInfoTab, _detail));
        Bind(BrowseCommand.FocusDiff, () => { FocusDiff(); FocusLater(_diff); });
        Bind(BrowseCommand.FocusFileTree, () => FocusTab(_fileTreeTab, _fileTree));
        Bind(BrowseCommand.FocusGpgInfo, () => FocusTab(_gpgTab, _gpg));
        Bind(BrowseCommand.FocusGitConsole, () => FocusTab(_consoleTab, _console));
        Bind(BrowseCommand.FocusOutputHistoryAndToggleIfPanel, () => FocusTab(_outputTab, _output));
        Bind(BrowseCommand.FocusNextTab, () => StepBottomTab(forward: true));
        Bind(BrowseCommand.FocusPrevTab, () => StepBottomTab(forward: false));
        Bind(BrowseCommand.FocusFilter, FocusFilterBox);
        Bind(BrowseCommand.ToggleLeftPanel, ToggleLeftPanel);

        _hotkeys.Install(this, IsGestureOwnedByFocusedView);
    }

    /// <summary>
    ///  The priority rule: upstream's <c>FormBrowse.ProcessHotkey</c> hands a gesture
    ///  to the focused control first and only treats it as global if nothing claimed
    ///  it. The dispatcher here tunnels (it has to — inner controls swallow arrows
    ///  and Ctrl+Tab before a bubbling handler could see them), so the same priority
    ///  has to be stated explicitly: for the gestures a focused view handles itself,
    ///  this returns true and the window keeps its hands off.
    /// </summary>
    private bool IsGestureOwnedByFocusedView(BrowseCommand command, HotkeyGesture gesture)
    {
        // The Console tab is a real PTY: while it has the focus it must receive the
        // keystrokes, control characters included. Only refresh and the focus-moving
        // commands stay global — otherwise there would be no way back out with the
        // keyboard.
        if (_console.IsKeyboardFocusWithin)
        {
            // Ctrl+W (close repository) joins them: it is not a control character a
            // shell does anything useful with, and reserving it for the PTY made the
            // gesture dead until the grid got the focus back (queue item 0.35).
            return command is not (BrowseCommand.Refresh
                or BrowseCommand.CloseRepository
                or BrowseCommand.FocusLeftPanel or BrowseCommand.FocusRevisionGrid
                or BrowseCommand.FocusCommitInfo or BrowseCommand.FocusDiff
                or BrowseCommand.FocusFileTree or BrowseCommand.FocusGpgInfo
                or BrowseCommand.FocusGitConsole
                or BrowseCommand.FocusOutputHistoryAndToggleIfPanel
                or BrowseCommand.FocusNextTab or BrowseCommand.FocusPrevTab
                or BrowseCommand.FocusFilter);
        }

        // Everything else is now ASKED, not listed. This used to be three hard-coded key
        // lists — one per view — which said the same thing as those views' own key
        // handlers and could only drift away from them; and once the gestures became
        // configurable they would have been wrong the moment anyone rebound one. A view
        // owns exactly the gestures ITS scope binds.
        if (_blame.IsKeyboardFocusWithin || _diff.IsKeyboardFocusWithin)
        {
            return OwnedBy(HotkeyScope.FileViewer, gesture)
                || OwnedBy(HotkeyScope.RevisionDiff, gesture)

                // Copying the path/patch is the diff's own Ctrl+C: not a hotkey with a
                // table entry, so it has to be named here.
                || (gesture.Modifiers == KeyModifiers.Control && gesture.Key == Key.C);
        }

        if (_revisions.IsKeyboardFocusWithin)
        {
            return OwnedBy(HotkeyScope.RevisionGrid, gesture)
                || (gesture.Modifiers == KeyModifiers.Control && gesture.Key is Key.C or Key.V);
        }

        if (_tree.IsKeyboardFocusWithin)
        {
            return OwnedBy(HotkeyScope.RepoObjectsTree, gesture);
        }

        return false;
    }

    // Whether a scope binds this gesture to one of its commands.
    private bool OwnedBy(HotkeyScope scope, HotkeyGesture gesture)
    {
        foreach ((string _, HotkeyGesture? bound) in _hotkeys.ScopeBindings(scope))
        {
            if (bound == gesture)
            {
                return true;
            }
        }

        return false;
    }

    // Fetch / pull exactly as the menu and toolbar items do (streaming dialog,
    // credentials, off-thread execution).
    private void Fetch() => RunRemoteOp("Fetch", (s, r, emit, creds) => s.FetchStreaming(_repoPath!, r, emit, creds));

    // One of the six upstream pull-button actions, run without asking anything: the
    // three that pull go through PullOptions, the two "all remotes" fetches have no
    // remote to pick. Mirrors ToolStripSplitButton's body click.
    private void RunPullAction(GitPullAction action)
    {
        switch (action)
        {
            case GitPullAction.FetchAll:
                RunRemoteOp("Fetch all", (s, _, emit, creds) => s.FetchAllStreaming(_repoPath!, emit, creds));
                break;

            case GitPullAction.FetchPruneAll:
                RunRemoteOp("Fetch and prune all",
                    (s, _, emit, creds) => s.FetchAndPruneAllStreaming(_repoPath!, emit, creds));
                break;

            case GitPullAction.Fetch:
                RunRemoteOp("Fetch", (s, r, emit, creds) =>
                    s.PullStreaming(_repoPath!, new PullOptions(GitPullAction.Fetch, Remote: r), emit, creds));
                break;

            case GitPullAction.Rebase:
                RunRemoteOp("Pull - rebase", (s, r, emit, creds) =>
                    s.PullStreaming(_repoPath!, new PullOptions(GitPullAction.Rebase, Remote: r), emit, creds));
                break;

            default:
                RunRemoteOp("Pull - merge", (s, r, emit, creds) =>
                    s.PullStreaming(_repoPath!, new PullOptions(GitPullAction.Merge, Remote: r), emit, creds));
                break;
        }
    }

    // The full FormPull equivalent. The dialog runs the pull itself (process dialog +
    // credential retry), so the host only has to refresh once it closes.
    private void OpenPullDialog()
    {
        if (_repoPath is not null)
        {
            _ = OpenPullDialogAsync(_repoPath);
        }
    }

    private async Task OpenPullDialogAsync(string repoPath)
    {
        using IDisposable watch = SuspendWatcher();

        // solveConflicts was a parameter nobody passed, so the dialog's "Solve
        // conflicts" button fell back to a bare mergetool launch. It now opens the
        // real conflict dialog.
        PullOptions? options = await Views.PullDialog.ShowAsync(
            this,
            repoPath,
            _toolbar.DefaultPullAction,
            solveConflicts: () => ShowResolveConflictsDialogAsync());

        if (options is not null)
        {
            RefreshAll();
        }
    }

    // Focus helpers. A UserControl is not focusable itself, so "focus this view"
    // means "focus its content" — the grid's row list, the tree's tree, the output's
    // text box.
    //
    // The candidate is NOT simply the first focusable descendant: these views start
    // with a header of buttons ("Go to", "Filter…", the diff toolbar), and focusing
    // a Button would leave the panel one Space away from firing it — which is
    // exactly what happened when Ctrl+1 was followed by Ctrl+Space during the
    // verification of this unit. So the list/tree comes first, then a text box, and
    // a button only if the view has nothing else to offer.
    private static void FocusInto(Control root)
    {
        if (root.Focusable)
        {
            root.Focus();
            return;
        }

        List<Control> visible = root.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
            .ToList();

        // The view's main list or tree. Avalonia's ListBox/TreeView are NOT focusable
        // themselves — only their item containers are — so a list is entered through
        // its selected row; that is also what makes the arrow keys work straight away.
        ItemsControl? items = visible
            .OfType<ItemsControl>()
            .FirstOrDefault(c => c is not (MenuBase or ComboBox));
        if (items is not null)
        {
            if (items.Focusable && items.Focus())
            {
                return;
            }

            int index = items is SelectingItemsControl { SelectedIndex: >= 0 } selecting ? selecting.SelectedIndex : 0;
            if (items.ContainerFromIndex(index) is Control container && container.Focus())
            {
                return;
            }
        }

        List<Control> candidates = visible.Where(c => c.Focusable).ToList();
        Control? target = candidates.FirstOrDefault(c => c is TextBox)
            ?? candidates.FirstOrDefault(c => c is not Button)
            ?? candidates.FirstOrDefault();

        target?.Focus();
    }

    // Focus after the current layout pass: a tab's content is only realized once
    // the tab is selected, so focusing it in the same beat would find nothing.
    private static void FocusLater(Control root)
        => Dispatcher.UIThread.Post(() => FocusInto(root), DispatcherPriority.Background);

    private void FocusTab(TabItem tab, Control content)
    {
        if (_bottom.Items.Contains(tab))
        {
            _bottom.SelectedItem = tab;
        }

        FocusLater(content);
    }

    // Ctrl+Tab / Ctrl+Shift+Tab: cycle the bottom panel's tab strip (wrapping).
    private void StepBottomTab(bool forward)
    {
        int count = _bottom.Items.Count;
        if (count == 0)
        {
            return;
        }

        int index = _bottom.SelectedIndex < 0 ? 0 : _bottom.SelectedIndex;
        _bottom.SelectedIndex = ((index + (forward ? 1 : -1)) % count + count) % count;
    }

    // Ctrl+E (upstream: ToolStripFilters.SetFocus) — the revision filter box.
    //
    // There used to be two of them, and the toolbar's copy was preferred; now that
    // the strip carries no filter of its own there is exactly one, in the revision
    // grid's own header, which is where the whole of ToolStripFilters lives.
    private void FocusFilterBox()
    {
        TextBox? filter = VisibleTextBox(_revisions);
        if (filter is null)
        {
            _statusBar.SetText(T("No filter box is on screen."));
            return;
        }

        filter.Focus();
        filter.SelectAll();

        static TextBox? VisibleTextBox(Control root) => root.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled && t.Bounds.Width > 0);
    }

    // Ctrl+F from anywhere in the window: bring the diff up and open its find bar.
    private void FocusDiffSearch()
    {
        FocusDiff();
        Dispatcher.UIThread.Post(_diff.FocusSearch, DispatcherPriority.Background);
    }

    // Ctrl+Alt+C: collapse/expand the left repository-objects panel.
    private void ToggleLeftPanel()
    {
        if (_tree.IsVisible)
        {
            _treeWidthBeforeCollapse = _treeCol.Width.Value > 0 ? _treeCol.Width.Value : 260;
            _tree.IsVisible = false;
            _treeCol.Width = new GridLength(0, GridUnitType.Pixel);
        }
        else
        {
            _tree.IsVisible = true;
            _treeCol.Width = new GridLength(
                _treeWidthBeforeCollapse > 0 ? _treeWidthBeforeCollapse : 260, GridUnitType.Pixel);
        }

        // Here rather than in the toolbar's own handler, so the hotkey path updates
        // the button's checked state too.
        _toolbar.SetLeftPanelVisible(_tree.IsVisible);
    }

    // Ctrl+. — pick a local branch and check it out through the ordinary checkout
    // path (which asks what to do with local changes). Loading the refs is git work,
    // so it happens off the UI thread.
    // Upstream's AppTitleGenerator: "<repo> (<branch>) - Git Extensions", so several
    // open windows are told apart in the task bar. Cheap enough to call from the
    // toolbar refresh, which already knows the branch.
    private void UpdateWindowTitle(string? branch)
    {
        if (_dashboardShowing || _repoPath is not { Length: > 0 } repo)
        {
            Title = DefaultTitle;
            return;
        }

        string name = new DirectoryInfo(repo.TrimEnd('/')).Name;
        Title = string.IsNullOrEmpty(branch)
            ? $"{name} - {DefaultTitle}"
            : $"{name} ({branch}) - {DefaultTitle}";
    }

    /// <summary>
    ///  "Fork and clone…". The only GitHub entry that does not need a repository open:
    ///  a successful clone is loaded straight away, which is what upstream's
    ///  <c>gitModuleChanged</c> callback does for its own fork/clone form.
    /// </summary>
    private async Task ShowGitHubForkCloneAsync()
    {
        string? cloned = await Views.GitHubForkCloneWindow.ShowAsync(this, new GitHubService());
        if (cloned is { Length: > 0 })
        {
            LoadRepository(cloned);
        }
    }

    private async Task ShowGitHubCreatePullRequestAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        await Views.GitHubCreatePullRequestWindow.ShowAsync(this, new GitHubService(), repo);
    }

    /// <summary>
    ///  "View pull requests…". A refresh follows only when something was actually
    ///  fetched: the window is also used just to read a diff, and refreshing the grid
    ///  for that would throw away the user's place in it.
    /// </summary>
    private async Task ShowGitHubPullRequestsAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        if (await Views.GitHubPullRequestsWindow.ShowAsync(this, new GitHubService(), repo))
        {
            RefreshAll();
        }
    }

    /// <summary>
    ///  "Add \"upstream\" remote" — upstream's <c>AddUpstreamRemoteAsync</c>, which it
    ///  offers from the same menu. It is a no-op in three cases (no repository of mine
    ///  among the remotes, the repository is not a fork, the parent is already
    ///  configured); each of them is reported rather than passed over in silence, which
    ///  is what upstream does with its null return.
    /// </summary>
    private async Task AddGitHubUpstreamAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        GitHubService service = new();
        if (!await Views.GitHubDialogs.RequireTokenAsync(this, service))
        {
            return;
        }

        try
        {
            string? added = await service.AddUpstreamRemoteAsync(repo, CancellationToken.None);
            if (added is null)
            {
                _statusBar.SetText(T(
                    "Nothing to add: no fork of yours among the remotes, or its parent is already configured."));
                return;
            }

            _statusBar.SetText(string.Format(T("Added the remote \"{0}\"."), added));
            RefreshAll();
        }
        catch (Exception ex)
        {
            await Views.GitHubDialogs.ReportAsync(this, "GitHub", ex);
        }
    }

    // "Remote repositories..." (Repository menu). The dialog existed but could only be
    // reached from PullDialog/PushDialog and the left panel.
    private async Task ShowRemotesAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        Views.RemotesDialog dialog = new(repo);
        await dialog.ShowDialog(this);
        if (dialog.Changed)
        {
            RefreshAll();
        }
    }

    /// <summary>
    ///  "Remote operations…" (Repository menu, right under "Remote repositories…") and
    ///  "Branches and tags…" (Commands menu, closing the branch/tag block): the two
    ///  utility windows around <see cref="Views.RemotePanel"/> and
    ///  <see cref="Views.BranchTagPanel"/>. Each window's own doc comment argues its
    ///  slot; here is the host contract, which is the one every other panel-in-a-window
    ///  tool of this shell already follows (<see cref="ShowStashDialogAsync"/>): modal,
    ///  registered as the repository-scoped window while it is up, and one
    ///  <see cref="RefreshAll"/> on close if anything inside it succeeded.
    ///
    ///  <para>The refresh is not optional decoration: a checkout or a push made in
    ///  there moves HEAD or the tracking refs, and without it the grid behind would go
    ///  on showing the state the repository had before the window was opened.</para>
    /// </summary>
    private async Task ShowRemoteOperationsAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        Views.RemoteOperationsWindow window = new(repo);
        await ShowRepositoryToolAsync(window);
        if (window.Changed)
        {
            RefreshAll();
        }
    }

    private async Task ShowBranchTagWorkbenchAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        Views.BranchTagWindow window = new(repo);
        await ShowRepositoryToolAsync(window);
        if (window.Changed)
        {
            RefreshAll();
        }
    }

    /// <summary>
    ///  Shows a window whose content is pinned to the repository that was open when it
    ///  was created, and remembers it for the length of the wait so
    ///  <see cref="LoadRepository"/> can close it if the repository changes.
    /// </summary>
    /// <remarks>
    ///  Being modal already stops the user from switching repositories, so this is a
    ///  belt for a brace — but not a theoretical one: the native X11 drop receiver this
    ///  window installs bypasses Avalonia's modal disabling entirely, so a folder
    ///  dropped on the shell while such a tool is up would re-point the shell and leave
    ///  the tool acting on the repository the user just left. Closing beats following:
    ///  these panels are handed a path once, and a window that silently re-aimed itself
    ///  mid-operation would be worse than one that went away.
    /// </remarks>
    private async Task ShowRepositoryToolAsync(Window window)
    {
        Window? previous = _repositoryScopedWindow;
        _repositoryScopedWindow = window;
        try
        {
            await window.ShowDialog(this);
        }
        finally
        {
            // Restore rather than clear: these tools can be nested (the branch workbench
            // opens the merge dialog, which can open others), and the innermost one
            // finishing must not un-register the one still on screen behind it.
            _repositoryScopedWindow = previous;
        }
    }

    // "Manage submodules..." (Repository menu); same story as ShowRemotesAsync.
    private async Task ShowSubmodulesAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        Views.SubmodulesDialog dialog = new(repo);
        await dialog.ShowDialog(this);
        if (dialog.Changed)
        {
            RefreshAll();
        }
    }

    // Pushes into the menu bar the facts it needs to hide or grey entries the way
    // FormBrowse does (FormBrowse.cs:987-990, 1014-1034): whether a repository is on
    // screen, whether the dashboard is up, and whether the repository is bare. The
    // bare test runs git, so it goes through Task.Run; the immediate call keeps the
    // menu correct in the meantime.
    private void UpdateMenuRepositoryState()
    {
        string? repo = _repoPath;
        bool hasRepo = !_dashboardShowing && repo is { Length: > 0 };
        _menu.SetRepositoryState(hasRepo, isBare: false, isDashboard: _dashboardShowing);
        if (!hasRepo)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            bool bare = _repositoryState.IsBareRepository(repo!);
            Dispatcher.UIThread.Post(() =>
            {
                // Ignore the answer if the user moved on to another repository.
                if (_repoPath == repo && !_dashboardShowing)
                {
                    _menu.SetRepositoryState(true, bare, isDashboard: false);
                }
            });
        });
    }

    /// <summary>
    ///  Opens one file's history in its own window — the port of upstream's
    ///  <c>StartFileHistoryDialog</c> (see <see cref="Views.FileHistoryWindow"/>).
    ///
    ///  <para><b>Not modal, and not owned by a tab.</b> Upstream starts a separate
    ///  PROCESS for this so the browse window stays usable while the history is read;
    ///  a non-modal child window is the same bargain without the second process. The
    ///  bottom strip no longer carries a file-history tab: what it could show was only
    ///  ever the grid, and the file's diff, blob and blame had nowhere to go there.</para>
    ///
    ///  <para>The window's grid is given the same commit commands as the repository's
    ///  own — the list is recorded once (<see cref="_commitCommands"/>) precisely because
    ///  a grid can be born after the menu was built — plus the bisect gate, and its
    ///  revert / cherry-pick take the host path so they get the watcher suspension and
    ///  the refresh.</para>
    /// </summary>
    private void OpenFileHistoryWindow(string path, bool showBlame = false)
    {
        if (_repoPath is not { Length: > 0 } repo || string.IsNullOrEmpty(path))
        {
            return;
        }

        Views.FileHistoryWindow window = new(repo, path, showBlame);

        foreach ((string header, Action<string> handler) in _commitCommands)
        {
            window.History.AddCommitCommand(header, handler);
        }

        window.History.IsBisectInProgress = _revisions.IsBisectInProgress;
        window.History.RevertCommitRequested += RevertThisCommit;
        window.History.CherryPickCommitRequested +=
            hash => RunOp("Cherry-pick", () => _stashOps.CherryPick(_repoPath!, hash).Success);

        // Double click on a row selects that commit in the repository grid behind, which
        // is what the bottom tab used to do and the only link the two windows need.
        window.History.RevisionActivated += hash =>
        {
            if (_repoPath is not null)
            {
                _revisions.SelectCommit(hash);
                OnRevisionSelected(hash);
            }
        };

        window.Show(this);
    }

    // The worktree manager used to be reachable only from the left panel's tree;
    // the toolbar's split button needs it too.
    private async Task ShowWorktreesAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        Views.WorktreesDialog dialog = new(repo);
        await dialog.ShowDialog(this);
        if (dialog.Changed)
        {
            RefreshAll();
        }

        // The dialog offers to switch to a worktree it just created; only the host can
        // change the open repository, and only once the modal is gone.
        if (dialog.RepositoryToOpen is { Length: > 0 } created)
        {
            OpenRepository(created);
        }
    }

    private async Task CheckoutBranchPickerAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        CheckoutBranchChoice? choice;
        try
        {
            choice = await CheckoutBranchForm.AskAsync(this, _repoPath!);
        }
        catch (Exception ex)
        {
            _statusBar.SetText(TF("{0} failed: {1}", T("FormCheckoutBranch/$this.Text", "Checkout branch"), ex.Message));
            return;
        }

        if (choice is not { } c)
        {
            return;
        }

        // Upstream warns before a `-B` that is not a fast-forward: the commits between the
        // merge base and the current tip of the local branch would be dropped.
        if (c.NewBranchMode == CheckoutNewBranchMode.Reset && c.NewBranchName is { Length: > 0 } localName)
        {
            ResetFastForwardInfo info = await Task.Run(
                () => new BranchTagService().GetResetFastForwardInfo(_repoPath!, localName, c.BranchName));
            if (!info.IsFastForward && !await ConfirmAsync(TF(
                    "You are going to reset the \"{0}\" branch to a new location discarding ALL the commited changes since the {1} revision.\n\nAre you sure?",
                    localName, info.MergeBaseDisplay)))
            {
                return;
            }
        }

        RunOp(
            T("FormCheckoutBranch/$this.Text", "Checkout branch"),
            () => new BranchTagService().CheckoutBranch(
                _repoPath!, c.BranchName, c.IsRemote, c.LocalChanges, c.NewBranchMode, c.NewBranchName).Success);
    }

    // Ctrl+P — select the first parent of the current revision in the grid.
    private async Task GoToParentAsync()
    {
        if (_repoPath is not { Length: > 0 } repo || _lastSelectedHash is not { Length: > 0 } hash)
        {
            return;
        }

        string? parent = await Task.Run(() =>
        {
            try
            {
                GitModule module = GitContext.CreateModule(repo);
                GitArgumentBuilder args = new("rev-parse") { "--verify", "--quiet", $"{hash}^" };
                ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
                string text = result.StandardOutput.Trim();
                return result.ExitedSuccessfully && text.Length > 0 ? text : null;
            }
            catch
            {
                return null;
            }
        });

        if (parent is null)
        {
            _statusBar.SetText(T("RevisionGridControl/_noParentNoRevision.Text", "The selected commit has no parent."));
            return;
        }

        _revisions.SelectCommit(parent);
    }

    // Ctrl+Shift+F — list the files of the selected commit (git ls-tree, off the UI
    // thread), filter them as the user types, and open the chosen file's history in
    // the bottom panel. Upstream's FindFileInSelectedCommit lands in the file tree;
    // the port's file tree has no "reveal path" entry point, so file history — the
    // nearest per-file destination it does have — is what Enter opens.
    private async Task FindFileInCommitAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        string commit = _lastSelectedHash is { Length: > 0 } h ? h : "HEAD";

        List<string> files = await Task.Run(() =>
        {
            List<string> result = [];
            try
            {
                GitModule module = GitContext.CreateModule(repo);
                GitArgumentBuilder args = new("ls-tree") { "-r", "--name-only", commit };
                ExecutionResult execution = module.GitExecutable.Execute(args, throwOnErrorExit: false);
                if (execution.ExitedSuccessfully)
                {
                    foreach (string rawLine in execution.StandardOutput.Split('\n'))
                    {
                        string line = rawLine.Trim();
                        if (line.Length > 0)
                        {
                            result.Add(line);
                        }
                    }
                }
            }
            catch
            {
                // An unreadable tree just yields an empty picker.
            }

            return result;
        });

        if (files.Count == 0)
        {
            _statusBar.SetText(T("No files in the selected commit."));
            return;
        }

        string? path = await PickFileAsync(files);
        if (path is { Length: > 0 })
        {
            OpenFileHistoryWindow(path);
        }
    }

    // Modal incremental file picker used by Ctrl+Shift+F. Typing narrows the list;
    // Enter (or double click) accepts, Esc cancels.
    private async Task<string?> PickFileAsync(IReadOnlyList<string> files)
    {
        ListBox list = new()
        {
            ItemsSource = files.Take(2000).ToList(),
            Background = (IBrush)Application.Current!.Resources["App.Control"]!,
            Foreground = (IBrush)Application.Current!.Resources["App.Text"]!,
            SelectedIndex = 0,
        };

        TextBox search = new()
        {
            Watermark = T("FileStatusList/tsmiFindFile.Text", "Find file..."),
            Background = (IBrush)Application.Current!.Resources["App.Panel"]!,
            Foreground = (IBrush)Application.Current!.Resources["App.Text"]!,
            Padding = new Thickness(6, 3, 6, 3),
            [DockPanel.DockProperty] = Dock.Top,
        };

        search.TextChanged += (_, _) =>
        {
            string term = search.Text ?? string.Empty;
            list.ItemsSource = (term.Length == 0
                    ? files
                    : files.Where(f => f.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Take(2000)
                .ToList();
            list.SelectedIndex = 0;
        };

        string? result = null;
        Theming.ZoomWindow dlg = new()
        {
            Title = T("FileStatusList/tsmiFindFile.Text", "Find file..."),
            Width = 620,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = new DockPanel { Margin = new Thickness(12), Children = { search, list } },
        };

        void Accept()
        {
            result = list.SelectedItem as string;
            dlg.Close();
        }

        // Tunnelling, handledEventsToo: the ListBox consumes Enter and the arrows
        // before a bubbling handler would ever see them (the port has been bitten
        // by this before) — but the arrows must keep moving the selection, so only
        // Enter and Esc are intercepted here.
        dlg.AddHandler(
            KeyDownEvent,
            (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Return)
                {
                    Accept();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    dlg.Close();
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        list.DoubleTapped += (_, _) => Accept();
        dlg.Opened += (_, _) => search.Focus();
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
            _statusBar.SetText(T("No repository is open."));
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

        string repo = _repoPath;
        int epoch = ++_repositoryEpoch;
        BeginNavigationLoad();
        _ = LoadRepositoryAfterWarmupAsync(repo, epoch, refresh: true);
        // The continuation reloads the panels, then acknowledges the watcher so reads
        // performed by those loaders cannot schedule an endless refresh loop.
    }

    // Opens the dedicated modal commit window (mirroring the original Git
    // Extensions commit form). This is now the ONLY staging/commit surface:
    // the redundant bottom "Working directory" tab was dropped. No-op without
    // a repo.
    private void OpenCommitDialog()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        _ = ShowCommitDialogAsync();
    }

    // Runs the commit dialog with the watcher muted: staging/unstaging rewrites the
    // index continuously, and those are our own writes — the dialog already calls
    // back into RefreshAll when it is done.
    private async Task ShowCommitDialogAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        using IDisposable watch = SuspendWatcher();
        await CommitDialog.ShowAsync(this, repo, RefreshAll);
    }

    // Opens the Push configuration dialog (remote/branch/force + Pull/Push),
    // replacing the previous immediate push. Refreshes the UI once it closes.
    private void OpenPushDialog()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        _ = ShowPushDialogAsync();
    }

    // As ShowCommitDialogAsync: the push is ours, so the watcher stays muted until
    // the dialog is gone and the window has refreshed.
    private async Task ShowPushDialogAsync()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        using IDisposable watch = SuspendWatcher();
        await PushDialog.ShowAsync(this, repo);
        RefreshAll();
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

        int epoch = _repositoryEpoch;
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

            // Stash count, repo state and tracking: the toolbar cannot compute them.
            // Without this the displays degrade to "unknown" rather than lying.
            ToolbarRepoState probed = new ToolbarStateService().Probe(repoPath);

            int fStaged = staged, fUnstaged = unstaged;
            Dispatcher.UIThread.Post(() =>
            {
                if (epoch != _repositoryEpoch || !SameRepositoryPath(repoPath, _repoPath))
                {
                    return;
                }

                _toolbar.UpdateState(ahead, behind, fStaged, fUnstaged, repoPath, branch, probed);
                UpdateWindowTitle(branch);
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
            // A fetch/pull rewrites refs and can rewrite the whole work tree; every
            // byte of that is ours, and the RefreshAll at the end covers it.
            using IDisposable watch = SuspendWatcher();

            _statusBar.SetText(TF("{0}…", label));

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

            // A pull that stopped on conflicts asks the question upstream asks
            // (MergeConflictHandler), instead of leaving the user to discover the
            // state. No-op when the index is clean.
            if (await ConflictFlow.HandleAsync(this, repo) is { HadConflicts: true })
            {
                RefreshAll();
            }
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

        _statusBar.SetText(T("Checking the last commit…"));

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
            _statusBar.SetText(TF("{0} failed: {1}", UndoLastCommitCaption, ex.Message));
            return;
        }

        if (!head.Ok)
        {
            _statusBar.SetText(head.Error.Length > 0
                ? TF("{0} failed: {1}", UndoLastCommitCaption, head.Error)
                : TF("{0} failed: {1}", UndoLastCommitCaption, T("no commit to undo.")));
            return;
        }

        if (!head.HasParent)
        {
            await InfoAsync(
                UndoLastCommitCaption,
                T("The current commit has no parent (this is the very first commit on this branch), "
                  + "so it cannot be undone with a soft reset.\n\nNothing was changed."));
            _statusBar.SetText(TF("{0}: {1}", UndoLastCommitCaption, T("no previous commit.")));
            return;
        }

        bool confirmed = await ConfirmUndoLastCommitAsync(head.Summary);
        if (!confirmed)
        {
            _statusBar.SetText(TF("{0}: {1}", UndoLastCommitCaption, T("cancelled.")));
            return;
        }

        WorkingDirectoryService service = new();
        _statusBar.SetText(T("Undoing last commit…"));

        WorkingDirCommitResult? result = null;
        await Views.GitProcessDialog.RunAsync(
            this,
            TF("{0} (git reset --soft HEAD~1)", UndoLastCommitCaption),
            () =>
            {
                result = service.UndoLastCommit(repo);
                return new Views.GitProcessOutcome(result.Success, result.Output);
            });

        _statusBar.SetText(result is { Success: true }
            ? T("Last commit undone — its changes are staged in the working tree.")
            : TF("{0} — {1}", UndoLastCommitCaption, T("failed, see the process output.")));
        RefreshAll();
    }

    // Confirmation for the destructive part of "Undo last commit": the commit is
    // removed from the branch. Spells out what actually happens (soft reset, changes
    // kept) so the wording matches the service behaviour.
    private async Task<bool> ConfirmUndoLastCommitAsync(string headSummary)
    {
        Button undo = new() { Content = T("Undo commit"), MinWidth = 90 };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

        StackPanel panel = new()
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = T("Undo the last commit?") + "\n\n"
                        + T("This runs \"git reset --soft HEAD~1\": the commit is removed from the "
                            + "branch, but all of its changes are kept and left staged in the working "
                            + "tree, so nothing is lost. Anything already pushed would need a force "
                            + "push to match."),
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

        Theming.ZoomWindow dlg = new()
        {
            Title = UndoLastCommitCaption,
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
    // reset too, mirroring the former working-directory panel's reset (which always
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
            _statusBar.SetText(TF("{0}: {1}", ResetChangesCaption, T("cancelled.")));
            return;
        }

        bool staged = includeStaged.Value;
        WorkingDirectoryService service = new();
        _statusBar.SetText(T("Resetting tracked changes…"));

        WorkingDirCommitResult? result = null;
        await Views.GitProcessDialog.RunAsync(
            this,
            TF("{0} ({1})", ResetChangesCaption, staged ? T("worktree + index") : T("worktree only")),
            () =>
            {
                result = service.ResetChanges(repo, staged);
                return new Views.GitProcessOutcome(result.Success, result.Output);
            });

        _statusBar.SetText(result is { Success: true }
            ? T("Tracked changes discarded.")
            : TF("{0} — {1}", ResetChangesCaption, T("failed, see the process output.")));
        RefreshAll();
    }

    // Commands → "Clean working directory…" (FormBrowse cleanupToolStripMenuItem).
    // Same contract as the former working-directory panel's clean: a `git clean
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

        // Upstream's FormCleanupRepository: modes (all / non-ignored / ignored only),
        // directories, submodules, include/exclude filters, and a repeatable dry-run
        // preview. The old inline confirm could not reach `git clean -X` at all.
        Views.CleanupDialog dialog = new(repo);
        await dialog.ShowDialog(this);
        if (dialog.Cleaned)
        {
            _statusBar.SetText(T("Working directory cleaned."));
            RefreshAll();
        }
    }

    // Reset confirmation: returns the chosen includeStaged flag, or null on cancel.
    private async Task<bool?> ConfirmResetAsync()
    {
        CheckBox alsoStaged = new()
        {
            Content = T("Also discard staged changes (git reset --hard HEAD)"),
            IsChecked = true,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Button reset = new() { Content = T("FormResetChanges/btnReset.Text", "Reset"), MinWidth = 90 };
        Button cancel = new() { Content = T("FormResetChanges/btnCancel.Text", "Cancel"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };

        Theming.ZoomWindow dlg = new()
        {
            Title = ResetChangesCaption,
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
                        Text = T("Discard uncommitted changes to tracked files?") + " "
                            + T("TranslatedStrings/_cannotBeUndone.Text", "This action cannot be undone.") + "\n"
                            + T("Untracked files are left alone — use \"Clean working directory…\" for those."),
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


    // The RunOp equivalent for the ref mutations that upstream runs inside FormProcess
    // (create branch, checkout): the process dialog owns the label, the background
    // thread and the console, so all that is left here is what RunOp does AROUND the
    // work — muting the watcher, the status line, the refresh and the conflict prompt.
    // Unlike RunOp there is no Task.Run: RefProcessRunner opens a modal and moves git
    // off the UI thread itself.
    private async Task RunRefProcessAsync(string label, Func<Task<bool>> op)
    {
        if (_repoPath is null)
        {
            return;
        }

        bool ok;
        using (SuspendWatcher())
        {
            _statusBar.SetText(TF("{0}…", label));
            try
            {
                ok = await op();
            }
            catch (Exception ex)
            {
                _statusBar.SetText(TF("{0} failed: {1}", label, ex.Message));
                return;
            }
        }

        // Refreshed even when the verdict is false: a failed — or aborted — checkout
        // can still have moved HEAD or the working tree, and showing the repository as
        // it actually is now beats showing the state it had before.
        RefreshAll();
        if (!ok)
        {
            _statusBar.SetText(TF("{0} failed — see the panel output.", label));
        }

        if (await ConflictFlow.HandleAsync(this, _repoPath!) is { HadConflicts: true })
        {
            RefreshAll();
        }
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
            if (confirm && !await ConfirmAsync(TF("{0}? This may discard work.", label)))
            {
                return;
            }

            // The repository is about to be written by US: mute the watcher so the
            // resulting file storm is not mistaken for an outside change.
            using IDisposable watch = SuspendWatcher();

            _statusBar.SetText(TF("{0}…", label));
            bool ok;
            try
            {
                ok = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _statusBar.SetText(TF("{0} failed: {1}", label, ex.Message));
                return;
            }

            RefreshAll();
            if (!ok)
            {
                _statusBar.SetText(TF("{0} failed — see the panel output.", label));
            }

            // Cherry-pick and stash pop both merge, so both can stop on conflicts:
            // ask, as upstream does. No-op when the index is clean.
            if (await ConflictFlow.HandleAsync(this, _repoPath!) is { HadConflicts: true })
            {
                RefreshAll();
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
            using IDisposable watch = SuspendWatcher();
            _statusBar.SetText(TF("{0}…", label));
            RevertArchiveResult result;
            try
            {
                result = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _statusBar.SetText(TF("{0} failed: {1}", label, ex.Message));
                return;
            }

            RefreshAll();
            if (!result.Success)
            {
                string firstLine = result.Output.Split('\n')[0].Trim();
                _statusBar.SetText(TF("{0} stopped: {1} — see the panel output.", label, firstLine));
            }

            // A revert conflicts like any merge: ask instead of only reporting.
            if (await ConflictFlow.HandleAsync(this, _repoPath!) is { HadConflicts: true })
            {
                RefreshAll();
            }
        }
    }

    private async Task NewTagAsync()
    {
        if (_repoPath is null)
        {
            return;
        }

        // Upstream tags the SELECTED revision and only falls back to HEAD when there is
        // no selection (GitUICommands.cs:562). Passing "HEAD" unconditionally tagged the
        // wrong commit whenever the user had picked an older one.
        string startPoint = StartPointForRefCreation();
        CreateTagRequest? request = await CreateTagDialog.AskAsync(this, _repoPath, startPoint);
        if (request is null)
        {
            return;
        }

        RunOp($"Create tag {request.Name}",
            () => new BranchTagService().CreateTag(
                _repoPath!, request.Name, startPoint, request.Message,
                request.Operation, request.SignKeyId, request.Force, request.PushToRemote).Success);
    }

    private async Task NewBranchAsync()
    {
        if (_repoPath is null)
        {
            return;
        }

        string startPoint = StartPointForRefCreation();
        CreateBranchRequest? request = await CreateBranchDialog.AskAsync(this, _repoPath, startPoint);
        if (request is null)
        {
            return;
        }

        await RunRefProcessAsync(
            TF("Create branch {0}", request.Name),
            () => RefProcessRunner.CreateBranchAsync(
                this, _repoPath!, request.Name, startPoint, request.Checkout));
    }

    // The revision a new branch/tag should be anchored to: the selected commit when
    // there is one, HEAD otherwise (no selection, or an artificial working-directory /
    // index row, which has no commit of its own).
    private string StartPointForRefCreation()
        => !_artificialRowSelected && _lastSelectedHash is { Length: > 0 } hash ? hash : "HEAD";

    // ---- patch operations (format / apply / view) ----------------------------------

    // Prompts for a base ref, picks an output directory, then generates one patch
    // per commit in <base>..HEAD there (git format-patch). Reports the files written.
    private async Task FormatPatchAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        string? baseRef = await PromptAsync(
            FormatPatchCaption,
            T("Base ref/commit (patches are produced for <base>..HEAD):"),
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
                Title = T("Choose an output directory for the patch files"),
            });

        if (folders.Count == 0)
        {
            return;
        }

        string? outDir = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(outDir))
        {
            _statusBar.SetText(T("The selected folder has no local path."));
            return;
        }

        string trimmedBase = baseRef.Trim();
        _statusBar.SetText(T("Generating patches…"));

        PatchResult result;
        try
        {
            result = await Task.Run(() => new PatchService().FormatPatch(_repoPath!, trimmedBase, outDir));
        }
        catch (Exception ex)
        {
            _statusBar.SetText(TF("{0} failed: {1}", FormatPatchCaption, ex.Message));
            return;
        }

        if (result.Success)
        {
            _statusBar.SetText(result.Files.Count > 0
                ? TF("Wrote {0} patch file(s) to {1}", result.Files.Count, outDir)
                : TF("format-patch produced no patches for {0}..HEAD", trimmedBase));
        }
        else
        {
            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText(TF("{0} failed: {1}", FormatPatchCaption, firstLine));
            await new PatchOutputWindow(TF("{0} — {1}", FormatPatchCaption, T("failed")), result.Output).ShowDialog(this);
        }
    }

    // Opens the full git am dialog (upstream FormApplyPatch): file/directory choice, the
    // patch grid, and Resolved / Skip / Abort while a session is in progress. The old body
    // was a bare file picker + apply, which left a stopped `am` session unreachable.
    private async Task ApplyPatchAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        Views.ApplyPatchDialog dialog = new(_repoPath);
        await dialog.ShowDialog(this);

        if (dialog.RepositoryChanged)
        {
            RefreshAll();
        }
    }

    // Picks a .patch/.diff file, reads it, and shows it in the colour-rendered
    // read-only patch viewer (same colouring as DiffView).
    private async Task ViewPatchAsync()
    {
        string? file = await PickPatchFileAsync(T("Choose a patch file to view"));
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
            _statusBar.SetText(TF("Could not read {0}: {1}", Path.GetFileName(file), ex.Message));
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
                    new FilePickerFileType(T("FormApplyPatch/_selectPatchFileFilter.Text", "Patch files")) { Patterns = new[] { "*.patch", "*.diff" } },
                    new FilePickerFileType(T("All files")) { Patterns = new[] { "*" } },
                },
            });

        if (files.Count == 0)
        {
            return null;
        }

        string? path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            _statusBar.SetText(T("The selected file has no local path."));
            return null;
        }

        return path;
    }

    /// <summary>
    ///  Publishes how tall a menu drop-down may be, as <c>App.MenuMaxHeight</c>.
    ///
    ///  <para>A pop-up is its own window, so the positioner only ever clamps it to the
    ///  SCREEN: on a window smaller than the screen — which is the normal case — a menu
    ///  with thirty entries ran past the bottom edge of the app and floated over the
    ///  desktop. Nothing in the styling can know that height, because it is a property
    ///  of this window and of where its menu bar ended up; so the window measures it and
    ///  the style consumes it through a dynamic resource
    ///  (<c>Theming/ModernStyles</c>, menu placement).</para>
    ///
    ///  <para>The floor keeps a menu usable on a window squeezed to nothing: better a
    ///  scrolling stub that overhangs than a card two entries tall.</para>
    /// </summary>
    private void PublishMenuMaxHeight()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        // Translated into the window's own space, not read off Bounds: since the modern
        // style the menu can be a child of the title bar rather than of the root dock,
        // and Bounds is relative to whichever parent it currently has.
        double barBottom = _menu.TranslatePoint(new Point(0, _menu.Bounds.Height), this) is Point p
            ? p.Y
            : _menu.Bounds.Bottom;
        double available = Bounds.Height - barBottom - Theming.Metrics.Space.Sm;
        app.Resources["App.MenuMaxHeight"] = Math.Max(MinimumMenuHeight, available);
    }

    /// <summary>Never publish a ceiling lower than this: see <see cref="PublishMenuMaxHeight"/>.</summary>
    private const double MinimumMenuHeight = 160;

    // ---- appearance (theme + style) -------------------------------------------------

    // The two appearance dimensions live side by side in UiState as strings; these
    // turn them into what ThemeManager takes. Anything unrecognised falls back to the
    // same defaults UiStateService normalises to.
    //
    // The theme has THREE stored values, not two: "System" resolves to whatever the
    // desktop prefers right now (see Theming/SystemTheme), so the mapping is that
    // class's job and not a second copy of the strings here.
    private static ThemeVariant VariantOf(string theme)
        => Theming.SystemTheme.VariantOf(theme);

    private static Theming.AppStyle StyleOf(string style)
        => style == "Classic" ? Theming.AppStyle.Classic : Theming.AppStyle.Modern;

    // Applies both dimensions from the live UiState. Always both, never one: Apply
    // takes the pair, so passing a default for the untouched one would silently reset it.
    private void ApplyAppearance()
    {
        // Before the palette, not after: SetColoredIcons only invalidates the glyphs
        // already on screen, and at startup there are none — Apply is what the first
        // window is built under.
        Theming.ThemeManager.SetColoredIcons(_uiState.ColoredIcons);

        // Before the Apply too: Follow only arms the subscription, and the variant the
        // first window is built under is the one Apply below puts in place. The seed is
        // what makes that first variant right on a desktop whose preference reaches us
        // asynchronously (see Theming/SystemTheme).
        Theming.SystemTheme.Seed(_uiState.SystemThemeSeen);
        Theming.SystemTheme.Follow(_uiState.Theme == Theming.SystemTheme.Name);
        Theming.ThemeManager.Apply(VariantOf(_uiState.Theme), StyleOf(_uiState.Style));

        // Where the menu sits is the third Appearance dimension, orthogonal to the other
        // two. Pushed into the live holder here so the constructor's ApplyWindowChrome
        // reads the stored answer, and so a value changed anywhere else is honoured.
        Theming.WindowChrome.Apply(Theming.WindowChrome.Parse(_uiState.TitleBar));
    }

    // Changes one dimension (or both), applies the resulting pair, and persists it.
    private void SetAppearance(string? theme = null, string? style = null)
    {
        if (theme is not null)
        {
            _uiState.Theme = theme;
        }

        if (style is not null)
        {
            _uiState.Style = style;
        }

        ApplyAppearance();
        _uiStateService.Save(_uiState);
    }

    // ---- window chrome (the title bar) ----------------------------------------------

    // Posted for the reason RevisionGridView posts its own style handler: the event is
    // raised from inside the dialog's preview, and re-parenting the menu is not
    // something to do underneath a caller that is still running.
    private void OnWindowChromeChanged() => Dispatcher.UIThread.Post(ApplyWindowChrome);

    /// <summary>
    ///  Puts the window in the frame the <see cref="Theming.WindowChrome"/> option calls
    ///  for: the merged bar — no system frame at all, the menu sharing one row with the
    ///  caption and the window buttons (see <see cref="TitleBar"/>) — or the standard
    ///  one, the desktop's own title bar with the menu on the row below it.
    /// </summary>
    /// <remarks>
    ///  <para><b>Why the frame is dropped on X11 but only "extended into" on Windows.</b>
    ///  The X11 backend of Avalonia 11.3 ignores <c>ExtendClientAreaToDecorationsHint</c>
    ///  outright — measured on that desktop: the hint leaves
    ///  <c>IsExtendedIntoWindowDecorations</c> false and mutter goes on drawing its own
    ///  bar. <see cref="Window.SystemDecorations"/> is honoured, so there that is the
    ///  lever, and it takes the resize border away with the title bar;
    ///  <see cref="ResizeGrips"/> hands that back.</para>
    ///
    ///  <para>On Windows the same lever costs something X11 does not charge for.
    ///  Drag-to-the-top tiling (Aero Snap, Snap Layouts) is a NON-CLIENT behaviour: the
    ///  move loop only offers it to a window that still has <c>WS_CAPTION</c> and
    ///  <c>WS_THICKFRAME</c>, and <c>SystemDecorations.None</c> strips both. Measured
    ///  with the three arrangements side by side:</para>
    ///
    ///  <code>
    ///                 extendedIntoDecorations  caption  sizing   tiling
    ///  Full                             False     True    True      yes
    ///  None                             False    False   False       NO
    ///  ExtendClientArea                  True     True    True      yes
    ///  </code>
    ///
    ///  <para>So Windows keeps its frame and merely draws into it, which is what the
    ///  Win32 backend implements the hint for. The system then also keeps providing the
    ///  resize border, so the grips are not added there — overlaying them on a live
    ///  border would take the edges away from the thing that already handles them.</para>
    ///
    ///  <para>Nothing here looks at the visual style: the two are orthogonal, and the
    ///  merged bar takes its colours from the live palette like the rest of the chrome,
    ///  so it wears Classic's surface under Classic and Modern's under Modern.</para>
    ///
    ///  <para>It is re-runnable on purpose: the option is switchable without a restart
    ///  and this is what re-lays the window when it changes.</para>
    /// </remarks>
    private void ApplyWindowChrome()
    {
        bool clientSide = Theming.WindowChrome.Merged;

        // The Content is assigned ONCE, here, and never swapped afterwards: it is the
        // dock plus the layer the resize strips live on. Re-assigning it per arrangement
        // would mean handing a control that already has a parent to the presenter, and
        // it would also make the zoom host (Theming/UiScaling) rebuild for nothing.
        if (_layered is null)
        {
            _layered = new Panel();
            _layered.Children.Add(_root);
            Content = _layered;
        }
        else if (clientSide == (_titleBar is not null))
        {
            return;
        }

        // Always take the menu off its current parent first: it is ONE control that
        // moves between the two layouts, so the entries, their events and their state
        // are the same objects either way — nothing is rebuilt and nothing to re-wire.
        _root.Children.Remove(_menu);
        if (_titleBar is not null)
        {
            _titleBar.Detach();
            _root.Children.Remove(_titleBar);
            _titleBar = null;
        }

        if (clientSide)
        {
            _titleBar = new TitleBar(this, _menu);
            DockPanel.SetDock(_titleBar, Dock.Top);
            _root.Children.Insert(0, _titleBar);

            // Only where the system frame is actually gone. On Windows it is still
            // there, doing the resizing itself.
            if (!ExtendsIntoDecorations)
            {
                if (_grips is null)
                {
                    _grips = new Panel();
                    foreach (Control grip in ResizeGrips.Build(this))
                    {
                        _grips.Children.Add(grip);
                    }
                }

                if (!_layered.Children.Contains(_grips))
                {
                    _layered.Children.Add(_grips);
                }
            }
        }
        else
        {
            DockPanel.SetDock(_menu, Dock.Top);
            _root.Children.Insert(0, _menu);
        }

        if (!clientSide || ExtendsIntoDecorations)
        {
            if (_grips is not null)
            {
                _layered.Children.Remove(_grips);
            }
        }

        if (clientSide && ExtendsIntoDecorations)
        {
            // Keep the frame — and therefore the snap behaviour — and paint over it.
            SystemDecorations = SystemDecorations.Full;
            ExtendClientAreaToDecorationsHint = true;
            // Fully qualified: the property of the same name shadows the enum type here,
            // and this app's own namespace makes a bare "Avalonia." resolve inwards.
            ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;

            // -1 means "the whole window is client area"; the port draws every pixel of
            // the bar itself, so there is no system caption height to reserve.
            ExtendClientAreaTitleBarHeightHint = -1;
        }
        else
        {
            ExtendClientAreaToDecorationsHint = false;
            SystemDecorations = clientSide ? SystemDecorations.None : SystemDecorations.Full;
        }

        UpdateResizeGrips();
        UpdateOffScreenMargin();
    }

    /// <summary>
    ///  Whether the merged bar is obtained by drawing INTO the system frame rather than
    ///  by removing it. True only where the backend implements the hint, which of the
    ///  two this port runs on is Windows.
    /// </summary>
    private static bool ExtendsIntoDecorations => OperatingSystem.IsWindows();

    // The strips are only useful on a window that has an edge to drag.
    private void UpdateResizeGrips()
    {
        if (_grips is not null)
        {
            _grips.IsVisible = WindowState == WindowState.Normal;
        }
    }

    /// <summary>
    ///  Pads the content by <see cref="TopLevel.OffScreenMargin"/> while the client area
    ///  is extended into the frame.
    ///
    ///  <para>A maximised window with an extended client area is deliberately larger than
    ///  the work area — Windows oversizes it by the frame thickness on every side — so
    ///  without this the top of the title bar, its buttons included, sits off the screen.
    ///  The margin is zero in every other state and arrangement, so this is safe to run
    ///  unconditionally; it just has to run again whenever the state changes.</para>
    /// </summary>
    private void UpdateOffScreenMargin()
    {
        if (_layered is not null)
        {
            _layered.Margin = ExtendClientAreaToDecorationsHint ? OffScreenMargin : default;
        }
    }

    // Opens the modal Settings window over the main window, passing the current
    // repo path. Settings persists its own changes; afterwards we re-sync the
    // in-memory theme and style so PersistLayout() on close doesn't overwrite a
    // change the user made in the dialog.
    private async Task OpenSettingsAsync()
    {
        await SettingsWindow.ShowAsync(
            this,
            _repoPath,
            _toolbar.DefaultPullAction.ToString(),
            action =>
            {
                _uiState.DefaultPullAction = action;
                if (Enum.TryParse(action, out GitPullAction chosen))
                {
                    _toolbar.DefaultPullAction = chosen;
                }
            },
            blameOptionsChanged: () => _blame.ReloadBlameOptions(),
            currentAutoRefresh: _uiState.AutoRefresh,
            // Required: the single UiState instance is re-serialised in full on exit,
            // so a write from the dialog alone would be undone.
            autoRefreshChanged: on =>
            {
                _uiState.AutoRefresh = on;
                _watcher.Watch(on ? _repoPath : null);
            },
            hotkeys: _hotkeys);
        UiState saved = _uiStateService.Load();
        _uiState.Theme = saved.Theme;
        _uiState.Style = saved.Style;
        _uiState.TitleBar = saved.TitleBar;

        // Same reason as the two above: PersistLayout() writes this instance in full on
        // close, so a size chosen in the dialog would be undone by the exit save.
        _uiState.UiSize = saved.UiSize;
        _uiState.ColoredIcons = saved.ColoredIcons;
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
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        string name = plugin.Name ?? plugin.GetType().Name;
        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            _statusBar.SetText(TF("Running plugin '{0}'…", name));

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
                _statusBar.SetText(TF("Plugin '{0}' failed: {1}", name, ex.Message));
                return;
            }

            if (refresh)
            {
                RefreshAll();
            }

            string? output = (plugin as SampleGreetPlugin)?.LastResult;
            _statusBar.SetText(output is { Length: > 0 }
                ? output
                : TF("Plugin '{0}' finished{1}.", name, refresh ? " " + T("(view refreshed)") : string.Empty));
        }
    }

    // Opens the runtime-typed settings editor for a plugin, binding its settings
    // container to the open repository's effective settings first, then persisting
    // through the same source on Save.
    private async Task OpenPluginSettingsAsync(IGitPlugin plugin)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        AvaloniaGitUICommands commands = new(_repoPath);
        RegisterPlugin(plugin, commands);

        await PluginSettingsWindow.ShowAsync(this, plugin, commands.GetEffectiveSettings());
        _statusBar.SetText(TF("Closed settings for '{0}'.", plugin.Name ?? plugin.GetType().Name));
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
        Theming.ZoomWindow dlg = new()
        {
            Title = T("Open Git repository"),
            Width = 640,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = picker,
        };

        // Esc closes it, like every other dialog in the port.
        Views.DialogKeys.InstallEscapeClose(dlg);
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
            _statusBar.SetText(TF("Cloned into {0}", repo));
            OpenRepository(repo);
        }
    }

    // Picks a directory, runs git init in it off the UI thread, then opens the
    // new repository through OpenRepository (same as clone / picker).
    private async Task InitRepositoryAsync()
    {
        // Upstream's FormInit: a directory with history plus the repository type,
        // where "Central" means --bare --shared=all. The old code was a bare folder
        // picker followed by a plain `git init`.
        Views.InitDialog dialog = new();
        await dialog.ShowDialog(this);
        if (dialog.CreatedRepoPath is not { Length: > 0 } path)
        {
            return;
        }

        // A central (bare) repository has no working directory to open.
        if (dialog.IsCentral)
        {
            _statusBar.SetText(TF("Created central repository at {0}", path));
            return;
        }

        _statusBar.SetText(TF("Initialised repository at {0}", path));
        OpenRepository(path);
    }

    // Opens a submodule / worktree / super-project path as the active repository
    // (from the toolbar dropdowns or the tree's "Open" context items), guarding
    // against a path that has since disappeared.
    private void OpenRepositoryPath(string path)
    {
        if (Directory.Exists(path))
        {
            if (SameRepositoryPath(path, _repoPath))
            {
                // The double click that follows a preview's single click lands here: the
                // repository IS already open, and what the gesture asks for is not a
                // load but the promotion of its tab from preview to kept. Without this
                // the "already open" answer below would swallow the only way to pin a
                // tab from the tree.
                if (Theming.RepoTabsOption.Enabled && _repoTabs.Active is { Pinned: false } preview)
                {
                    _repoTabs.Pin(preview);
                    return;
                }

                bool pending;
                lock (_activeNavigationGate)
                {
                    pending = _activeNavigationLoadPending;
                }

                _statusBar.SetText(pending
                    ? TF("Opening repository: {0}", path)
                    : TF("Repository is already open: {0}", path));
                return;
            }

            // This is intentionally synchronous and precedes warm-up/discovery: the
            // pointer gesture must be visibly acknowledged even on a cold repository.
            _statusBar.SetText(TF("Opening repository: {0}", path));
            OpenRepository(path);
        }
        else
        {
            _statusBar.SetText(TF("Path no longer exists: {0}", path));
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

    // Every door into a repository — the picker, the dashboard, a clone, the tree's
    // "Open", a dropped folder, the command line — goes through here, and here is where
    // the tab strip is told about it. Opening always PINS: those are deliberate acts.
    // Only the tree's single click asks for a preview (OpenRepository(path, pinned:
    // false)), which is the one gesture cheap enough to be undone by the next one.
    private void OpenRepository(string repoPath) => OpenRepository(repoPath, pinned: true);

    private void OpenRepository(string repoPath, bool pinned)
    {
        if (!Theming.RepoTabsOption.Enabled)
        {
            // One repository at a time: the strip is not on screen and a preview open —
            // which only the single click produces — is not a thing the user asked for.
            if (pinned)
            {
                LoadRepository(repoPath);
            }

            return;
        }

        // Open() activates, and its Activated event is what loads: a path that is
        // already the active tab therefore does NOT reload — clicking the submodule you
        // are already looking at, or double-clicking the preview you just opened to pin
        // it, costs nothing.
        _repoTabs.Open(repoPath, pinned);
        UpdateRepoTabsVisibility();
    }

    // Loads a repository into the (single) set of views. Split out of OpenRepository so
    // that switching tabs can reuse it without going back through the strip.
    private void LoadRepository(string repoPath)
    {
        // The panes below the grid describe a COMMIT, and the commit they are describing
        // belongs to the repository being left. Emptying them is not a refresh: it is the
        // difference between "stale" and "wrong". And nothing would correct them on its
        // own — the tracking fields still said the panes were up to date for that hash,
        // and a repository whose grid lands with no selection never raises the event that
        // would reload them. So switching tabs used to leave the previous repository's
        // diff sitting under the new repository's history.
        ResetBottomPanes();

        // A repository-scoped tool (remote operations, the branch/tag workbench) holds
        // the OLD path and would go on fetching into, or checking out of, a repository
        // the user has just left. It closes with the repository it belongs to.
        if (_repositoryScopedWindow is { } scoped && !SameRepositoryPath(repoPath, _repoPath))
        {
            _repositoryScopedWindow = null;
            scoped.Close();
        }

        _repoPath = repoPath;
        int epoch = ++_repositoryEpoch;
        BeginNavigationLoad();
        _console.RepoPath = repoPath;
        _progressBanner.SetRepository(repoPath);
        ShowRepositoryView();
        _toolbar.SetSubmoduleNavigation(null);
        _ = LoadRepositoryAfterWarmupAsync(repoPath, epoch, refresh: false);
        _menu.SetFavoriteRepositories(_favoritesService.Load());
        _menu.SetPlugins(PluginService.Instance.Plugins);

        // Follow the new repository (or stop following anything, when the user
        // turned automatic refresh off in ui-state.json).
        _watcher.Watch(_uiState.AutoRefresh ? repoPath : null);
        _watcher.NotifyRefreshed();
        _ = RecordRecentAsync(repoPath);
        _ = PopulateRecentAsync();
        UpdateMenuRepositoryState();
    }

    // Empties every pane that describes a single commit, and forgets what they were
    // showing, so the next selection in the new repository is always loaded afresh.
    // The Console, Output and Blame panes are deliberately left alone: the first two are
    // logs of what the user did (and the console is re-pointed at the new repository by
    // OpenRepository itself), and blame is opened explicitly on a file rather than
    // following the grid's selection.
    private void ResetBottomPanes()
    {
        _lastSelectedHash = null;
        _artificialRowSelected = false;
        _artificialHash = null;
        _diffShowsRange = false;
        _detailLoadedFor = null;
        _diffLoadedFor = null;
        _fileTreeLoadedFor = null;
        _gpgLoadedFor = null;

        _detail.ClearCommit();
        _diff.Clear();
        _fileTree.Clear();
        _gpg.Clear();
    }

    // ---- repository tabs ---------------------------------------------------------

    private void WireRepoTabs()
    {
        // The strip decides WHICH repository is active; this is the only place that
        // turns that decision into a load, whoever made it (a click on a tab, an
        // Open(), a close picking the neighbour).
        _repoTabs.Activated += ShowRepoTab;

        // The last tab was closed: there is no repository to show any more, which is
        // exactly what the dashboard is for.
        _repoTabs.Emptied += () =>
        {
            ShowDashboard();
            UpdateRepoTabsVisibility();
        };

        // Clicking the active tab is normally a no-op, except when the dashboard took
        // the work area over ("Dashboard" in the menu): then it means "back to this
        // repository", and the tab the strip still calls active has to be re-loaded.
        _repoTabs.Picked += entry =>
        {
            if (_dashboardShowing)
            {
                ShowRepoTab(entry);
            }
        };

        _repoTabs.Changed += UpdateRepoTabsVisibility;

        // "Duplicate tab" copies the entry, and the entry only holds what was written into
        // it when its tab was last LEFT. The tab being duplicated is normally the one on
        // screen, whose live selection is newer than that — so flush it first, and the
        // copy inherits the commit and the bottom pane the user is actually looking at.
        // Any other tab already carries its own state and must not be overwritten with
        // the views of a repository that is not it.
        _repoTabs.Duplicating += source =>
        {
            if (ReferenceEquals(source, _loadedTab))
            {
                CaptureTabState(source);
            }
        };

        // Turning the option off must not strand the user on a hidden strip: the active
        // repository stays open, the others are simply no longer reachable until it is
        // turned back on (they are still in the saved state).
        Theming.RepoTabsOption.Changed += () => Dispatcher.UIThread.Post(UpdateRepoTabsVisibility);
    }

    // Puts one tab's repository on screen, with the little state that tab carries.
    // Every route into a tab ends here — a click on the strip, Ctrl+PageDown, an Open(),
    // the restore at start-up — which is why the OUTGOING tab is captured here and
    // nowhere else: the strip has already moved its own active entry by the time it
    // tells us, so the tab being left is the one this window last loaded, not the one
    // the strip now calls active.
    private void ShowRepoTab(Views.RepoTabEntry entry)
    {
        CaptureTabState(_loadedTab);
        _loadedTab = entry;
        // Asked for BEFORE the load: the grid can only honour it once the first page
        // lands, and it holds the request until then.
        _revisions.SelectCommitWhenLoaded(entry.SelectedCommit);
        LoadRepository(entry.Path);
        if (entry.BottomTab is { Length: > 0 } bottom)
        {
            SelectBottomTab(bottom);
        }
    }

    // The strip is on screen only when it has something to say: the option is on and at
    // least one repository is open. On the dashboard there is none, so it disappears
    // with the work area.
    private void UpdateRepoTabsVisibility()
        => _repoTabs.IsVisible = Theming.RepoTabsOption.Enabled && _repoTabs.Tabs.Count > 0;

    // The tab whose repository is currently loaded in the views, which is NOT always the
    // strip's active entry: between a click and the load, the strip has already moved on.
    private Views.RepoTabEntry? _loadedTab;

    // Writes the live view state into the tab that is about to lose it — the row the user
    // was on and the bottom pane they were reading — so coming back lands where they left
    // rather than at the top of the history. A tab that has since been closed, or a
    // dashboard with no repository behind it, has nothing worth keeping.
    private void CaptureTabState(Views.RepoTabEntry? entry)
    {
        if (entry is null || _dashboardShowing || !_repoTabs.Tabs.Contains(entry))
        {
            return;
        }

        entry.SelectedCommit = _revisions.SelectedCommitHashes.Count > 0
            ? _revisions.SelectedCommitHashes[0]
            : null;
        entry.BottomTab = CurrentBottomTabKey();
    }

    // Ctrl+W (BrowseCommand.CloseRepository) and the tab's own close affordance mean the
    // same thing once there are tabs: close THIS repository, not "leave every repository
    // and go to the dashboard". With the option off the old meaning is the only one.
    private void CloseActiveRepository()
    {
        if (Theming.RepoTabsOption.Enabled && _repoTabs.Active is { } active)
        {
            _repoTabs.Close(active);
            UpdateRepoTabsVisibility();
            return;
        }

        ShowDashboard();
    }

    // Ctrl+PageDown / Ctrl+PageUp, VS Code's own gesture for walking the open tabs.
    // Handled in the tunnel so a focused list or text box cannot swallow it first, and
    // wrapping at both ends because a strip of tabs is a ring, not a line.
    private void OnRepoTabNavigationKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control
            || (e.Key != Key.PageDown && e.Key != Key.PageUp)
            || !Theming.RepoTabsOption.Enabled
            || _repoTabs.Tabs.Count < 2
            || _repoTabs.Active is not { } active)
        {
            return;
        }

        IReadOnlyList<Views.RepoTabEntry> tabs = _repoTabs.Tabs;
        int index = -1;
        for (int i = 0; i < tabs.Count; i++)
        {
            if (ReferenceEquals(tabs[i], active))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        int step = e.Key == Key.PageDown ? 1 : -1;
        _repoTabs.Activate(tabs[(index + step + tabs.Count) % tabs.Count]);
        e.Handled = true;
    }

    // Start-up: the repositories that were open last time, minus the ones that have
    // since been moved or deleted — a saved path is a hint, exactly like LastRepoPath.
    // Returns whether anything was restored, so the caller can fall back to its usual
    // "CLI argument > cwd > last repository > dashboard" chain.
    private bool RestoreRepoTabs()
    {
        if (!Theming.RepoTabsOption.Enabled || _uiState.OpenRepoTabs.Count == 0)
        {
            return false;
        }

        List<Views.RepoTabEntry> restored = [];
        foreach (RepoTabState saved in _uiState.OpenRepoTabs)
        {
            if (FindRepositoryRoot(saved.Path) is null)
            {
                continue;
            }

            restored.Add(new Views.RepoTabEntry
            {
                // Carried over rather than regenerated, because ActiveRepoTab names one of
                // these ids. The sanitiser guarantees it is non-blank and unique, filling
                // it in for files written before tabs had an identity of their own.
                Id = saved.Id,
                Path = saved.Path,
                Pinned = saved.Pinned,
                SelectedCommit = saved.SelectedCommit,
                BottomTab = saved.BottomTab,
            });
        }

        if (restored.Count == 0)
        {
            return false;
        }

        // Restore() deliberately does not raise Activated — nothing has been loaded yet —
        // so the active tab is opened here, through the one door that loads.
        _repoTabs.Restore(restored, _uiState.ActiveRepoTab);
        UpdateRepoTabsVisibility();

        ShowRepoTab(_repoTabs.Active ?? restored[0]);
        return true;
    }

    private void SaveRepoTabs()
    {
        CaptureTabState(_loadedTab);
        _uiState.OpenRepoTabs = _repoTabs.Tabs
            .Select(t => new RepoTabState
            {
                Id = t.Id,
                Path = t.Path,
                Pinned = t.Pinned,
                SelectedCommit = t.SelectedCommit,
                BottomTab = t.BottomTab,
            })
            .ToList();
        // The id, not the path: several tabs may stand for the same repository, and a path
        // would name all of them or none.
        _uiState.ActiveRepoTab = _repoTabs.Active?.Id;
    }

    // Takes the repository, not the snapshot task: the snapshot is asked of the cache
    // HERE, so this method awaits work it started rather than a task handed in from
    // outside — the shape that deadlocks when the other party wants this thread. The
    // cache makes that free: both callers have just put their task in it, so the same
    // instance comes back and nothing is discovered twice. If a concurrent Invalidate
    // beat us to it we get the FRESHER snapshot, and the epoch check below still
    // decides whether it may touch the toolbar.
    private async Task RefreshSubmoduleNavigationAsync(string repoPath, int epoch)
    {
        // Held as a local as well as awaited: the identity check further down compares
        // it against _activeNavigationSnapshot to decide whether this result is still
        // the one the window is waiting for.
        Task<RepositoryNavigationSnapshot> navigation = _navigationSnapshots.GetAsync(repoPath);
        string? parent = null;
        bool failed = false;
        try
        {
            parent = (await navigation.ConfigureAwait(false)).Submodules.ImmediateSuperprojectPath;
        }
        catch
        {
            failed = true;
        }

        // This method is also started by provider retry continuations, which run on a
        // pool thread. Never rely on their captured context: both the final identity
        // check and the control update belong to Avalonia's dispatcher.
        Dispatcher.UIThread.Post(() =>
        {
            bool applies;
            lock (_activeNavigationGate)
            {
                applies = epoch == _repositoryEpoch
                    && !_dashboardShowing
                    && SameRepositoryPath(repoPath, _repoPath)
                    && SameRepositoryPath(repoPath, _activeNavigationRepository)
                    && ReferenceEquals(_activeNavigationSnapshot, navigation);
                if (applies && failed)
                {
                    _activeNavigationRepository = null;
                    _activeNavigationSnapshot = null;
                    _activeNavigationLoadPending = false;
                }
            }

            if (applies)
            {
                _toolbar.SetSubmoduleNavigation(parent);
            }
        });
    }

    // Git Extensions core owns process-global lazy state. Initialize it once on a
    // worker before panels fan out into concurrent reads. A rapid A -> B switch shares
    // the prerequisite, then the generation guard starts loaders for B alone.
    private async Task LoadRepositoryAfterWarmupAsync(string repoPath, int epoch, bool refresh)
    {
        await EnsureCoreWarmupAsync(repoPath).ConfigureAwait(true);
        if (epoch != _repositoryEpoch || _dashboardShowing || !SameRepositoryPath(repoPath, _repoPath))
        {
            return;
        }

        _navigationSnapshots.Invalidate(repoPath);
        Task<RepositoryNavigationSnapshot> navigation = _navigationSnapshots.GetAsync(repoPath);
        lock (_activeNavigationGate)
        {
            _activeNavigationRepository = repoPath;
            _activeNavigationSnapshot = navigation;
            _activeNavigationLoadPending = false;
        }
        _revisions.LoadRepository(repoPath);
        _tree.LoadRepository(repoPath, navigation);
        _statusBar.LoadRepository(repoPath);
        RefreshSubmoduleNavigationAsync(repoPath, epoch).Forget("refreshing the submodule navigation");
        RefreshToolbarState();

        if (refresh)
        {
            _progressBanner.Refresh();
            _watcher.NotifyRefreshed();
        }
    }

    private static Task EnsureCoreWarmupAsync(string repoPath)
    {
        lock (CoreWarmupGate)
        {
            s_coreWarmupTask ??= Task.Run(() =>
            {
                GitCommands.GitModule module = GitContext.CreateModule(repoPath);
                _ = module.GetCurrentCheckout();
                _ = new RevisionService().LoadRevisions(repoPath, 1);
            });
            return ObserveWarmupAsync(s_coreWarmupTask);
        }
    }

    private void BeginNavigationLoad()
    {
        lock (_activeNavigationGate)
        {
            _activeNavigationRepository = null;
            _activeNavigationSnapshot = null;
            _activeNavigationLoadPending = true;
        }
    }

    private Task<RepositoryNavigationSnapshot>? GetOrReacquireNavigationAsync(string repoPath, int epoch)
    {
        lock (_activeNavigationGate)
        {
            if (epoch != _repositoryEpoch || _dashboardShowing || !SameRepositoryPath(repoPath, _repoPath))
            {
                return null;
            }

            if (_activeNavigationSnapshot is { } active
                && SameRepositoryPath(repoPath, _activeNavigationRepository)
                && !active.IsFaulted
                && !active.IsCanceled)
            {
                return active;
            }

            // The repository-open continuation owns the first acquisition. Providers
            // must not race it and turn one switch into two discoveries.
            if (_activeNavigationLoadPending)
            {
                return null;
            }

            _navigationSnapshots.Invalidate(repoPath);
            Task<RepositoryNavigationSnapshot> replacement = _navigationSnapshots.GetAsync(repoPath);
            _activeNavigationRepository = repoPath;
            _activeNavigationSnapshot = replacement;
            RefreshSubmoduleNavigationAsync(repoPath, epoch).Forget("refreshing the submodule navigation");
            return replacement;
        }
    }

    // Returns a task that completes WITH the warm-up but never faults, so the callers
    // can await the prerequisite without having to guard it.
    //
    // A continuation rather than an await: this method exists precisely to observe a
    // task created elsewhere, and awaiting one of those can deadlock if it ever needs
    // the awaiting thread. The continuation carries no such dependency.
    private static Task ObserveWarmupAsync(Task warmup)
        => warmup.ContinueWith(
            finished =>
            {
                if (!finished.IsFaulted && !finished.IsCanceled)
                {
                    return;
                }

                // Reading Exception marks it observed; the panels have their own safe
                // error paths, and the next repository open retries rather than
                // poisoning the process-wide prerequisite.
                _ = finished.Exception;
                lock (CoreWarmupGate)
                {
                    if (ReferenceEquals(s_coreWarmupTask, warmup))
                    {
                        s_coreWarmupTask = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static bool SameRepositoryPath(string path, string? other)
    {
        if (string.IsNullOrWhiteSpace(other))
        {
            return false;
        }

        try
        {
            string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other));
            return string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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
        _repositoryEpoch++;
        if (!_dashboardShowing)
        {
            _root.Children.Remove(_repositoryArea);
            if (!_root.Children.Contains(_dashboard))
            {
                _root.Children.Add(_dashboard);
            }

            _dashboardShowing = true;
        }

        // No repository on screen → nothing to watch.
        _watcher.Stop();
        _repoPath = null;
        lock (_activeNavigationGate)
        {
            _activeNavigationRepository = null;
            _activeNavigationSnapshot = null;
            _activeNavigationLoadPending = false;
        }
        _menu.SetFavoriteRepositories(_favoritesService.Load());
        _ = LoadDashboardAsync();
        UpdateMenuRepositoryState();

        // No repository is open any more: the title and the toolbar must stop
        // advertising the one that was. RefreshToolbarState does not run here, so both
        // would otherwise keep showing the old path, branch and counters.
        UpdateWindowTitle(branch: null);
        _toolbar.UpdateState(0, 0, 0, 0, repoPath: string.Empty, branch: string.Empty);
        _toolbar.SetSubmoduleNavigation(null);
        _statusBar.SetText(T("FormBrowse/dashboardToolStripMenuItem.Text", "Dashboard"));
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
            _statusBar.SetText(T("No repository is open to add to favorites."));
            return;
        }

        IReadOnlyList<string> favorites = _favoritesService.Add(_repoPath);
        _menu.SetFavoriteRepositories(favorites);
        if (_dashboardShowing)
        {
            _dashboard.Load(favorites, Array.Empty<string>());
            _ = LoadDashboardAsync();
        }

        _statusBar.SetText(TF("Added to favorites: {0}", _repoPath));
    }

    // Repository → Git maintenance → "Recover lost objects…" (upstream's FormVerify).
    // VerifyDialog creates and deletes recovery tags and branches, so a change there
    // has to reach the graph and the ref tree.
    private async Task OpenVerifyAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText(T("No repository is open."));
            return;
        }

        if (await Views.VerifyDialog.ShowAsync(this, _repoPath))
        {
            RefreshAll();
        }
    }

    // Confirmation dialog (Yes/No).
    private Task<bool> ConfirmAsync(string message) => YesNoAsync(message);

    // Informational dialog with a single OK button (no choice to make).
    private async Task InfoAsync(string title, string message)
    {
        Button ok = new() { Content = T("OK"), MinWidth = 80 };
        Theming.ZoomWindow dlg = new()
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
        Button yes = new() { Content = T("TranslatedStrings/_yes.Text", "Yes"), MinWidth = 80 };
        Button no = new() { Content = T("TranslatedStrings/_no.Text", "No"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Theming.ZoomWindow dlg = new()
        {
            Title = T("Confirm"),
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
        Button ok = new() { Content = T("OK"), MinWidth = 80 };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Theming.ZoomWindow dlg = new()
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

    // ---- translation -----------------------------------------------------------------

    // Captions reused by a dialog title, its process-dialog banner and the matching
    // status-bar lines. Properties, not constants: the active catalogue can change
    // while the window is open, so each read must go through the service.
    private static string UndoLastCommitCaption => T("FormBrowse/_undoLastCommitCaption.Text", "Undo last commit");

    private static string ResetChangesCaption => T("FormResetChanges/$this.Text", "Reset changes");


    private static string FormatPatchCaption => T("FormFormatPatch/$this.Text", "Format patch");

    private static string ApplyPatchCaption => T("FormApplyPatch/$this.Text", "Apply patch");

    /// <summary>
    ///  (Re-)labels the bottom panel's tab strip. Called from the constructor and
    ///  again whenever the language changes — the tabs are long-lived controls, so
    ///  unlike the dialogs (rebuilt per use) they have to be re-labelled in place.
    /// </summary>
    private void ApplyTabTranslations()
    {
        // Icons match the original tab strip one for one: upstream's
        // FormBrowse.InitCommitDetails fills the tab control's ImageList with
        // CommitSummary / Diff / FileTree / Key / Console / GitCommandLog and assigns
        // those as the pages' ImageKey. The three port-only tabs reuse the icons their
        // own features already use elsewhere (stash, Blame, FileHistory).
        _commitInfoTab.Header = IconText.Header("CommitSummary", T("FormBrowse/CommitInfoTabPage.Text", "Commit"));
        _diffTab.Header = IconText.Header("Diff", T("FormBrowse/DiffTabPage.Text", "Diff"));
        _fileTreeTab.Header = IconText.Header("FileTree", T("FormBrowse/TreeTabPage.Text", "File tree"));
        _gpgTab.Header = IconText.Header("Key", T("FormBrowse/GpgInfoTabPage.Text", "GPG"));
        _consoleTab.Header = IconText.Header("Console", T("FormBrowse/_consoleTabCaption.Text", "Console"));
        _outputTab.Header = IconText.Header("GitCommandLog", T("FormBrowse/_outputHistoryTabCaption.Text", "Output"));
        _blameTab.Header = IconText.Header("Blame", T("FormFileHistory/BlameTab.Text", "Blame"));

        // No FormBrowse item for this one (the port has a tab where upstream has a
        // separate window); matched by source text instead.
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // Short alias for the composite-format overload: these call sites are dense.
    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(null, englishFormat, args);
}
