using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using GitCommands.Git;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;
using GitExtensions.Extensibility.Git;

// The toolbar has its own Separator(IBrush) factory for the inline group rules,
// so the menu-level separator control is aliased to keep the two apart.
using MenuSeparator = Avalonia.Controls.Separator;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The main top toolbar for the shell, echoing the original
///  <c>FormBrowse</c> toolbar: a horizontal strip of flat, icon-first buttons
///  grouped with separators (Open repo | Fetch, Pull, Push | Commit | Stash |
///  Refresh | New branch).
///
///  The toolbar performs no git work itself: each button simply raises a public
///  event, and the host window wires those events to the existing services and
///  views. Icons are the reused Git Extensions PNGs loaded through
///  <see cref="IconLoader"/>; when an icon is missing the button degrades to its
///  text label.
/// </summary>
/// <summary>
///  Where the commit-info (commit detail) panel sits relative to the revision
///  grid, mirroring the original FormBrowse "Commit info position" toggle.
/// </summary>
public enum CommitInfoPosition
{
    BelowGraph,
    LeftOfGraph,
    RightOfGraph,
}

/// <summary>
///  One entry in a toolbar split-button dropdown (a submodule or worktree the
///  host can open as the active repository). <paramref name="Icon"/> names the
///  <see cref="IconLoader"/> icon to show; empty falls back to the button icon.
///
///  The three trailing flags exist for the worktrees drop-down, which upstream
///  renders with state (<c>toolStripWorktrees_DropDownOpening</c>): the worktree
///  the window is currently on is <paramref name="IsChecked"/> and disabled, and a
///  worktree git reports as prunable/deleted is disabled and greyed. They default
///  to "a plain, enabled entry", so callers that only pass the first three
///  arguments — the submodules and recent-repositories providers — are unaffected.
/// </summary>
/// <param name="IsChecked">Show a check mark (the current worktree).</param>
/// <param name="IsEnabled">False leaves the entry visible but inert.</param>
/// <param name="IsDim">Paint the label in the dim/disabled colour (a deleted worktree).</param>
/// <param name="Category">
///  The category a favorite repository is filed under, or <c>null</c> when it has
///  none. Only the favorites drop-down reads it (upstream groups that menu by
///  <c>Repository.Category</c>); every other provider leaves it null, which is why
///  it is last and optional.
/// </param>
public readonly record struct RepoLink(
    string Label,
    string Path,
    string Icon,
    bool IsChecked = false,
    bool IsEnabled = true,
    bool IsDim = false,
    string? Category = null);

public sealed class MainToolbar : UserControl
{
    public event Action? OpenRepoRequested;
    public event Action? FetchRequested;

    /// <summary>
    ///  Legacy single-action pull, kept so existing hosts keep working: it is raised
    ///  by the split button's body ONLY while nothing is subscribed to
    ///  <see cref="PullActionRequested"/>. Hosts should move to
    ///  <see cref="PullActionRequested"/> / <see cref="OpenPullDialogRequested"/>,
    ///  which carry the chosen action.
    /// </summary>
    public event Action? PullRequested;

    /// <summary>
    ///  A pull/fetch was requested with an explicit action: either picked from the
    ///  split button's drop-down, or the persisted default action when the button's
    ///  body was pressed. Never raised with
    ///  <see cref="GitPullAction.None"/>/<see cref="GitPullAction.Default"/>.
    /// </summary>
    public event Action<GitPullAction>? PullActionRequested;

    /// <summary>The drop-down's "Open pull dialog…" entry was chosen.</summary>
    public event Action? OpenPullDialogRequested;

    /// <summary>
    ///  The user changed which action the button's body performs (drop-down →
    ///  "Set default Pull button action"). The toolbar has already applied it to
    ///  itself; the host is responsible for persisting it in
    ///  <see cref="UiState.DefaultPullAction"/> — the toolbar deliberately does not
    ///  write the state file, because the host saves its own <see cref="UiState"/>
    ///  instance wholesale on close and would clobber a value written here.
    /// </summary>
    public event Action<GitPullAction>? DefaultPullActionChanged;
    public event Action? PushRequested;
    public event Action? CommitRequested;

    /// <summary>
    ///  Plain "Stash" — save the working directory to a new stash. Raised by the
    ///  stash split button's "Stash" drop-down entry (upstream
    ///  <c>stashChangesToolStripMenuItem</c>).
    /// </summary>
    public event Action? StashRequested;

    /// <summary>Stash the staged (index) changes only (upstream <c>stashStagedToolStripMenuItem</c>).</summary>
    public event Action? StashStagedRequested;

    /// <summary>Apply and drop the most recent stash (upstream <c>stashPopToolStripMenuItem</c>).</summary>
    public event Action? StashPopRequested;

    /// <summary>
    ///  Show the stash management surface. Raised by the split button's BODY and by
    ///  its "Manage stashes…" entry — upstream both call
    ///  <c>UICommands.StartStashDialog</c>. In the port the surface is the bottom
    ///  panel's Stash tab, so the host answers this by selecting that tab.
    /// </summary>
    public event Action? ManageStashesRequested;

    /// <summary>
    ///  Open the "create a stash" prompt (upstream <c>createAStashToolStripMenuItem</c>,
    ///  i.e. <c>StartStashDialog(this, false)</c>). In the port this is the Stash
    ///  panel's "Stash…" flow, which asks for a message and an untracked-files flag.
    /// </summary>
    public event Action? CreateStashRequested;

    public event Action? RefreshRequested;
    public event Action? NewBranchRequested;

    /// <summary>
    ///  Open the Settings window (upstream's <c>EditSettings</c> toolbar button, the
    ///  last item of the external-tools group).
    /// </summary>
    public event Action? SettingsRequested;

    /// <summary>
    ///  Show/hide the left (repository objects) panel — upstream's
    ///  <c>toggleLeftPanel</c>. The toolbar does not know the resulting state; the
    ///  host pushes it back through <see cref="SetLeftPanelVisible"/>.
    /// </summary>
    public event Action? ToggleLeftPanelRequested;

    /// <summary>
    ///  Open the "checkout branch" dialog: the branch drop-down's leading
    ///  "Checkout branch…" entry, and a right-click on the branch button itself
    ///  (upstream <c>branchSelect_MouseUp</c> → <c>CheckoutBranchToolStripMenuItemClick</c>).
    /// </summary>
    public event Action? CheckoutBranchRequested;

    /// <summary>
    ///  Open "Manage worktrees": the worktrees split button's BODY and its
    ///  "Manage worktrees…" entry (upstream <c>toolStripWorktrees_ButtonClick</c>).
    /// </summary>
    public event Action? ManageWorktreesRequested;

    /// <summary>Create a new worktree (upstream's <c>TranslatedStrings.CreateWorktree</c> entry).</summary>
    public event Action? CreateWorktreeRequested;

    /// <summary>Run <c>git worktree prune</c> (upstream's <c>PruneWorktrees</c> entry).</summary>
    public event Action? PruneWorktreesRequested;

    // View / layout controls (added to match the original FormBrowse toolbar).
    public event Action? SplitViewToggleRequested;
    public event Action<CommitInfoPosition>? CommitInfoPositionChanged;
    public event Action? FileExplorerRequested;
    public event Action? OpenTerminalRequested;

    /// <summary>
    ///  Start a specific shell (the argument is its executable) in the repository
    ///  directory — upstream's <c>userShell</c> split button. When no host handles
    ///  this the button degrades to <see cref="OpenTerminalRequested"/>, i.e. a
    ///  terminal running the login shell, so it is never a dead control.
    /// </summary>
    public event Action<string>? OpenShellRequested;

    // Right-side branch-scope + text filter, echoing the original FormBrowse
    // toolbar's "All branches ▾" scope dropdown and "Filter:" combo. The toolbar
    // performs no filtering itself: choosing a scope raises BranchScopeChanged
    // (0 = All branches, 1 = Current branch, 2 = Filtered) and typing in the
    // filter box raises FilterChanged; the host drives the revision grid.
    public event Action<int>? BranchScopeChanged;
    public event Action<string>? FilterChanged;

    // Submodules / worktrees split buttons. The toolbar itself performs no git
    // work: the host supplies a provider that lists the repo's submodules /
    // worktrees (off the UI thread), and choosing one raises
    // OpenRepositoryRequested with that path so the host opens it as the active
    // repository.
    public Func<Task<IReadOnlyList<RepoLink>>>? SubmodulesProvider { get; set; }
    public Func<Task<IReadOnlyList<RepoLink>>>? WorktreesProvider { get; set; }
    public event Action<string>? OpenRepositoryRequested;

    // Inline branch dropdown: the host supplies a provider that lists the local
    // branch names (off the UI thread); choosing one raises BranchCheckoutRequested
    // so the host performs the checkout. The button caption shows the current
    // branch (kept current through UpdateState).
    public Func<Task<IReadOnlyList<string>>>? BranchesProvider { get; set; }
    public event Action<string>? BranchCheckoutRequested;

    // Inline repo-path dropdown: the host supplies a provider that lists RECENT
    // repositories (off the UI thread); choosing one raises OpenRepositoryRequested.
    // The button caption shows the current repository path (home collapsed to ~).
    public Func<Task<IReadOnlyList<RepoLink>>>? RecentReposProvider { get; set; }

    // Favorite repositories for the same drop-down (upstream's categorised-repos
    // submenu, flat here). Optional: with no provider the toolbar reads the shell's
    // own favorites.json, so the group is populated without any host wiring.
    public Func<Task<IReadOnlyList<RepoLink>>>? FavoriteReposProvider { get; set; }

    /// <summary>
    ///  "Close (go to Dashboard)" from the working-directory drop-down — upstream's
    ///  <c>_tsmiCloseRepo</c>. Unwired, the entry is shown disabled.
    /// </summary>
    public event Action? CloseRepositoryRequested;

    /// <summary>
    ///  "Configure this menu..." from the working-directory drop-down — upstream
    ///  opens <c>FormRecentReposSettings</c>. Unwired, the entry is shown disabled.
    /// </summary>
    public event Action? ConfigureRecentReposRequested;

    // ---- stateful controls kept for UpdateState() ---------------------------
    // References to the Push / Pull / Commit buttons and their caption TextBlocks
    // (and icon Images, so we can tint them) so UpdateState() can refresh badges
    // and colours in place without rebuilding the toolbar.
    private Button? _pushButton;
    private TextBlock? _pushCaption;
    private Image? _pushIcon;
    // Pull is a SPLIT button (as upstream's toolStripButtonPull is): _pullButton is
    // its body (runs the default action), next to a separate arrow button that opens
    // the actions menu. The body's tooltip names the current default action.
    private Button? _pullButton;
    private TextBlock? _pullCaption;
    private Image? _pullIcon;
    private GitPullAction _defaultPullAction = GitPullAction.Merge;
    private Button? _commitButton;
    private TextBlock? _commitCaption;
    private Image? _commitIcon;

    // The "Split view" toggle and its caption, so SetSplitView can reflect the
    // host's current layout state (checked caption + highlighted chrome).
    private Button? _splitButton;
    private TextBlock? _splitCaption;
    private Image? _splitIcon;

    // Left-panel toggle (upstream toggleLeftPanel): a checked/pressed button whose
    // state mirrors whether the host's left panel is showing.
    private Button? _leftPanelButton;
    private TextBlock? _leftPanelCaption;
    private Image? _leftPanelIcon;
    private bool _leftPanelVisible = true;

    // Commit-info position split button (upstream menuCommitInfoPosition): the body
    // cycles the three positions, and both icon and tooltip are copied from the
    // entry matching the active position.
    private Button? _commitInfoButton;
    private Image? _commitInfoIcon;
    private CommitInfoPosition _commitInfoPosition = CommitInfoPosition.BelowGraph;

    // Stash split button: its caption carries the "(n)" stash count.
    private TextBlock? _stashCaption;
    private Border? _stashHost;

    // Worktrees split button: hidden entirely while the repository has a single
    // worktree, exactly as upstream's UpdateWorktreeToolStripVisibility does.
    private Control? _worktreesHost;
    private Control? _submodulesHost;
    private Image? _submodulesIcon;
    private Button? _submodulesBody;
    private Button? _submodulesArrow;
    private string? _immediateSuperprojectPath;

    // Widest the Commit button has ever been, applied as a MinWidth so the strip
    // does not shuffle sideways every time the change count gains or loses a digit
    // (upstream freezes the button's Width for the same reason).
    private double _commitMinWidth;

    /// <summary>
    ///  The window's live hotkey map, used ONLY to label menu entries and tooltips
    ///  with the gesture actually in force. Optional: while it is null the labels
    ///  fall back to <see cref="HotkeyService.Defaults"/>, which is upstream's own
    ///  FormBrowse map but ignores user overrides — so a host with a hotkey service
    ///  should assign this.
    ///
    ///  Assigning it rebuilds the strip, because the tooltips baked in by
    ///  <see cref="Build"/> (which runs from the constructor, before a host can set
    ///  this) would otherwise keep showing the default gestures forever.
    /// </summary>
    public HotkeyService? Hotkeys
    {
        get => _hotkeys;
        set
        {
            if (ReferenceEquals(_hotkeys, value))
            {
                return;
            }

            _hotkeys = value;
            Rebuild();
        }
    }

    private HotkeyService? _hotkeys;

    /// <summary>
    ///  Repository-wide facts the toolbar cannot compute itself (stash count, repo
    ///  state, worktree/remote counts, upstream tracking). Refreshed by
    ///  <see cref="UpdateState(int, int, int, int, string, string, ToolbarRepoState?)"/>
    ///  and replayed after a language rebuild.
    /// </summary>
    private ToolbarRepoState _state = new();

    // Far-right working-directory indicator (repo name + ~-collapsed path); created
    // lazily on the first UpdateState() call and reused thereafter.
    private TextBlock? _repoIndicator;

    // Inline branch dropdown (button + its caption) and repo-path dropdown (button
    // + its caption), placed near the left of the toolbar to mirror the original
    // FormBrowse repo-path / branch selectors. Captions are refreshed in place by
    // UpdateState so they never stack.
    private TextBlock? _branchCaption;
    private TextBlock? _repoPathCaption;
    // Last-known current branch, so the branch flyout can mark/bold it.
    private string _currentBranch = string.Empty;

    // Rebuilt wholesale when the language changes, so neither is readonly.
    // Shell split button (upstream userShell): the installed shells, the pick in
    // force, and the controls whose icon/caption/tooltip follow that pick.
    private Control? _shellHost;
    private Button? _shellBody;
    private TextBlock? _shellCaption;
    private Image? _shellIcon;
    private ShellDescriptor? _currentShell;
    private IReadOnlyList<ShellDescriptor> _shells = Array.Empty<ShellDescriptor>();

    private OverflowPanel _bar = null!;

    // Overflow ("»") button + its flyout, and the per-item descriptors used to
    // rebuild that flyout from the items the panel could not fit.
    private Button _overflowButton = null!;
    private readonly MenuFlyout _overflowFlyout = new();
    private readonly Dictionary<Control, OverflowEntry> _overflow = new();

    // ---- state re-applied after a language rebuild ---------------------------
    // Build() re-creates every caption from scratch, so the last values the host
    // pushed in are remembered here and replayed onto the fresh controls. Without
    // this a language switch would blank the push/pull badges, the commit count,
    // the repo/branch captions and the split-view check mark until the next
    // refresh.
    private bool _hasState;
    private int _lastAhead;
    private int _lastBehind;
    private int _lastStaged;
    private int _lastUnstaged;
    private string _lastRepoPath = string.Empty;
    private string _lastBranch = string.Empty;
    private bool _splitViewOn;

    public MainToolbar()
    {
        IBrush toolbar = Brush("App.Toolbar", "#333337");
        IBrush border = Brush("App.Border", "#3F3F46");
        // App.Hover / App.Pressed, not App.PanelAlt / App.Panel: those two are DARKER
        // than the toolbar, so a button under the pointer read as a hole punched in the
        // strip instead of a lift, and in the modern dark palette the hover was within
        // a hair of the strip itself.
        IBrush hover = Brush("App.Hover", "#444448");
        IBrush pressed = Brush("App.Pressed", "#555558");

        Background = toolbar;

        // A subtle 1px bottom rule separates the toolbar from the content below.
        BorderBrush = border;
        BorderThickness = new Thickness(0, 0, 0, 1);

        // Flat/borderless buttons with a subtle hover fill (the Fluent template
        // paints the button's chrome through its inner ContentPresenter, so we
        // style that part directly for both the resting and pointer-over states).
        // Added once, in the constructor: the styles live on the control itself and
        // survive the strip being rebuilt for a language change.
        // The resting fill is the strip's own colour AT ALPHA 0 — invisible, exactly
        // like Brushes.Transparent, so a checked button's own Background still shows
        // through — and that is not a detail. Brushes.Transparent is #00FFFFFF,
        // transparent WHITE, and the modern style cross-fades this very property
        // (ModernStyles.PresenterTransitions): interpolating from transparent white to
        // the hover fill walks through half-opaque WHITE, which is the flash the strip
        // blinked on every hover — measured on screen, it peaked at #78787D over a
        // #2F3038 toolbar before settling. Fading in from the HOVER colour at alpha 0
        // makes the cross-fade a pure opacity ramp: no third colour is ever on screen,
        // in either theme (the light theme dipped to #BEBEC3 when the ramp started from
        // the toolbar's own hue instead).
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbtn")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Fade(hover)),

                // No cross-fade on a toolbar button: it is what made the flash visible
                // in the first place, and a strip of small buttons under a moving pointer
                // reads better switching cleanly than smearing between two fills. The
                // modern style puts a Background/BorderBrush transition on every
                // ContentPresenter (ModernStyles.PresenterTransitions); an empty
                // Transitions here wins, because a style declared on this control is
                // nearer than one declared on the Application.
                new Setter(Animatable.TransitionsProperty, new Transitions()),
                new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(3)),
            },
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbtn").Class(":pointerover")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, hover),
                new Setter(ContentPresenter.BorderBrushProperty, border),
            },
        });
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbtn").Class(":pressed")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, pressed),
                new Setter(ContentPresenter.BorderBrushProperty, border),
            },
        });

        Build();

        // A language switch rebuilds the strip in place — no restart. Posting the
        // rebuild keeps it out of the loader's continuation (same pattern as MainMenu).
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Rebuild);

    /// <summary>
    ///  Re-creates the strip and replays every piece of state the host has pushed in,
    ///  so neither a language switch nor a late <see cref="Hotkeys"/> assignment can
    ///  blank a badge or leave a stale gesture in a tooltip.
    /// </summary>
    private void Rebuild()
    {
        Build();

        // Replay whatever the host last told us, so badges/captions survive.
        if (_hasState)
        {
            UpdateState(_lastAhead, _lastBehind, _lastStaged, _lastUnstaged, _lastRepoPath, _lastBranch, _state);
        }
        else
        {
            // Even without a host update the fresh controls must not lie about
            // visibility (worktrees) or enablement (stash).
            ApplyRepoState();
        }

        SetSplitView(_splitViewOn);
        SetLeftPanelVisible(_leftPanelVisible);
        SetCommitInfoPosition(_commitInfoPosition);
        SetSubmoduleNavigation(_immediateSuperprojectPath);
    }

    /// <summary>
    ///  (Re-)creates the whole toolbar strip. Called from the constructor and again
    ///  whenever the language changes; every caption is produced through
    ///  <see cref="T(string?, string)"/> so the fresh strip speaks the new language.
    ///  Provider properties and public events live on the control (not on the
    ///  rebuilt children) and therefore survive untouched.
    /// </summary>
    private void Build()
    {
        IBrush border = Brush("App.Border", "#3F3F46");

        // Stale descriptors would keep the discarded controls (and their captions)
        // alive in the overflow menu.
        _overflow.Clear();
        _repoIndicator = null;

        // The "»" overflow button: shown by OverflowPanel only when the strip is
        // too narrow for every item, and dropping a menu with the items left out.
        _overflowButton = MakeOverflowButton();

        OverflowPanel bar = new(_overflowButton)
        {
            VerticalAlignment = VerticalAlignment.Center,
            // Upstream's ToolStripMain has Padding(0) and leaves its items on the
            // default ToolStripButton Margin(0,1,0,2): NO horizontal gap between
            // neighbours at all, the groups being told apart by the separators alone.
            // The 4px left/right is toolPanel.TopToolStripPanel.Padding(4,0,4,0), and
            // there is no vertical margin — upstream's strip is 25px tall in total.
            Spacing = 0,
            Margin = new Thickness(4, 0),
        };
        _bar = bar;

        // The item order below follows ToolStripMain.Items.AddRange in
        // FormBrowse.Designer.cs:205-224 group by group, including where its FIVE
        // separators fall. Only two port-only commands are folded in (Open
        // repository, New branch), each inside an existing group rather than earning
        // a separator of its own.

        // ---- refresh (upstream group 1: RefreshButton alone) ---------------------
        bar.AddItem(IconOnly(MakeButton("RepoOpen", T("FormBrowse/openToolStripMenuItem.Text", "Open"),
            T("Dashboard/_openRepository.Text", "Open repository"), () => OpenRepoRequested?.Invoke())));
        bar.AddItem(IconOnly(MakeButton("ReloadRevisions", T("FormBrowse/RefreshButton.ToolTipText", "Refresh"),
            T("FormBrowse/RefreshButton.ToolTipText", "Refresh"), () => RefreshRequested?.Invoke())));

        // ---- view / layout group (upstream group 2) ------------------------------
        bar.AddItem(Separator(border));

        // Toggle left panel — upstream's toggleLeftPanel, which sits immediately
        // before the split-view toggle and carries a pressed/checked state bound to
        // whether the panel is showing (FormBrowse.RefreshLayoutToggleButtonStates).
        _leftPanelButton = IconOnly(MakeButton("LayoutSidebarLeft", T("Left panel"),
            TipWithGesture(T("FormBrowse/toggleLeftPanel.ToolTipText", "Toggle left panel"),
                BrowseCommand.ToggleLeftPanel),
            () => ToggleLeftPanelRequested?.Invoke(),
            out _leftPanelCaption, out _leftPanelIcon));
        bar.AddItem(_leftPanelButton);

        // Split view is a TOGGLE: icon-only as upstream, so the "on" state now shows
        // as a checked background on the button (upstream's ToolStripButton.Checked)
        // instead of a ✓ in a caption. The ✓ stays in the collapsed caption because
        // that is what labels the entry in the overflow menu.
        _splitButton = IconOnly(MakeButton("LayoutFooter", T("Split view"),
            T("Show the commit detail and the diff side by side in the Commit tab"),
            () => SplitViewToggleRequested?.Invoke(),
            out _splitCaption, out _splitIcon));
        bar.AddItem(_splitButton);
        bar.AddItem(MakeCommitInfoSplitButton(border));

        // ---- submodules / worktrees / working dir / branch (upstream group 3) ----
        bar.AddItem(Separator(border));
        _submodulesHost = MakeRepoLinkButton("SubmodulesManage", T("TranslatedStrings/_submodulesText.Text", "Submodules"),
            T("Open a submodule (or the parent super-project) as the active repository"),
            () => SubmodulesProvider, border,
            primaryPath: () => _immediateSuperprojectPath,
            showLabel: false,
            captureIcon: icon => _submodulesIcon = icon,
            captureSplitButtons: (body, arrow) => (_submodulesBody, _submodulesArrow) = (body, arrow));
        bar.AddItem(_submodulesHost);

        // Worktrees is a real split button: the body opens "Manage worktrees" (as
        // upstream's toolStripWorktrees_ButtonClick does) and the drop-down lists the
        // worktrees plus the create/prune/manage commands. ApplyRepoState() hides the
        // whole thing while the repository has a single worktree.
        _worktreesHost = MakeRepoLinkButton("WorkTree", T("TranslatedStrings/_worktreesText.Text", "Worktrees"),
            T("FormBrowse/toolStripWorktrees.ToolTipText", "Worktrees"),
            () => WorktreesProvider, border,
            bodyAction: () => ManageWorktreesRequested?.Invoke(),
            extraItems: WorktreeExtraItems);
        bar.AddItem(_worktreesHost);

        // Inline repo-path + branch dropdowns, in upstream's place: _NO_TRANSLATE_WorkingDir
        // then branchSelect, closing the same group as the submodule/worktree buttons.
        // These two and Commit are the only items upstream shows with a caption
        // (DisplayStyle stays at its ImageAndText default); everything else is
        // Image-only and speaks through its tooltip.
        bar.AddItem(MakeRepoPathButton(border));
        bar.AddItem(MakeBranchButton(border));

        // Upstream's wording for the same command is "Create branch"; the port's
        // shorter caption keeps the strip narrow but reuses that catalogue entry.
        // Not in upstream's toolbar at all — folded in next to the branch selector.
        bar.AddItem(IconOnly(MakeButton("BranchCreate", T("TranslatedStrings/_buttonCreateBranch.Text", "New branch"),
            T("FormCommit/createBranchToolStripButton.ToolTipText", "Create a new branch"), () => NewBranchRequested?.Invoke())));

        // ---- remote / commit group (upstream group 4) ----------------------------
        // Fetch sits immediately before Pull, where FormBrowse.InitMenusAndToolbars
        // inserts its fetch/pull shortcut buttons; then Pull, Push, Commit, Stash.
        bar.AddItem(Separator(border));
        bar.AddItem(IconOnly(MakeButton("PullFetch", T("FormBrowse/_pullFetch.Text", "Fetch"),
            T("FormBrowse/fetchToolStripMenuItem.ToolTipText", "Fetch from remote"), () => FetchRequested?.Invoke())));
        bar.AddItem(MakePullSplitButton(border));
        _pushButton = IconOnly(MakeButton("Push", T("FormBrowse/toolStripButtonPush.Text", "Push"),
            T("FormPush/_errorPushToRemoteCaption.Text", "Push to remote"), () => PushRequested?.Invoke(),
            out _pushCaption, out _pushIcon));
        bar.AddItem(_pushButton);
        _commitButton = MakeButton("CommitSummary", T("FormBrowse/toolStripButtonCommit.Text", "Commit"),
            T("Commit changes"), () => CommitRequested?.Invoke(),
            out _commitCaption, out _commitIcon);
        bar.AddItem(_commitButton);
        bar.AddItem(MakeStashSplitButton(border));

        // ---- external tools group (upstream group 5) ----------------------------
        bar.AddItem(Separator(border));
        bar.AddItem(IconOnly(MakeButton("BrowseFileExplorer", T("FormBrowse/toolStripFileExplorer.ToolTipText", "File Explorer"),
            T("Open the repository in the file manager"),
            () => FileExplorerRequested?.Invoke())));
        bar.AddItem(MakeShellSplitButton(border));

        // Settings closes the external-tools group, exactly as upstream's
        // EditSettings closes ToolStripMain.
        bar.AddItem(IconOnly(MakeButton("Settings", T("FormBrowse/EditSettings.ToolTipText", "Settings"),
            TipWithGesture(T("FormBrowse/EditSettings.ToolTipText", "Settings"), BrowseCommand.OpenSettings),
            () => SettingsRequested?.Invoke())));

        // ---- branch-scope + filter group (right side) ---------------------------
        // Mirrors the original FormBrowse "All branches ▾" scope dropdown and the
        // "Filter:" combo. Placed after the buttons and before the (lazily-added)
        // repo indicator so the two selectors read on the right of the strip.
        bar.AddItem(Separator(border));
        bar.AddItem(MakeMenuButton("Branch", T("FormBrowse/tssbtnShowBranches.Text", "All branches"),
            T("Which branches the revision grid shows"), new[]
        {
            ("Branch", T("FormBrowse/tssbtnShowBranches.Text", "All branches"), (Action)(() => BranchScopeChanged?.Invoke(0))),
            ("Branch", T("Current branch"), (Action)(() => BranchScopeChanged?.Invoke(1))),
            ("Branch", T("Filtered"), (Action)(() => BranchScopeChanged?.Invoke(2))),
        }));

        TextBlock filterLabel = new()
        {
            // The colon is punctuation, not part of the translatable noun.
            Text = string.Format("{0}:", T("FormBrowse/ToolStripFilters.Text", "Filter")),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.TextDim", "#8A8A8A"),
            FontSize = 12,
            Margin = new Thickness(8, 0, 4, 0),
        };
        bar.AddItem(filterLabel);
        _overflow[filterLabel] = new OverflowEntry { Kind = OverflowKind.Skip };

        TextBox filterBox = new()
        {
            Width = 180,
            Watermark = T("search term, then Enter"),
            Background = Brush("App.Panel", "#252526"),
            Foreground = Brush("App.Text", "#DCDCDC"),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(filterBox, T("Type a term and press Enter to search the whole history with git"));

        // ENTER submits, exactly as upstream's FilterToolBar does
        // (FilterToolBar.cs:386-434) — NOT every keystroke.
        //
        // The box used to raise FilterChanged on TextChanged, and the host answered
        // it with an in-memory sieve over the rows already loaded: a term living in
        // a commit that had not been paged in was simply never found, and every
        // keystroke re-sifted the list. Submitting on Enter is what lets the same
        // event carry the term all the way into `git log` (the grid's ApplyFilter
        // now applies it to the field chosen in its "Filter type" dropdown), and it
        // is also what makes a git-side filter affordable at all.
        filterBox.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                FilterChanged?.Invoke(filterBox.Text ?? string.Empty);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                filterBox.Text = string.Empty;
                FilterChanged?.Invoke(string.Empty);
                e.Handled = true;
            }
        };
        bar.AddItem(filterBox);
        _overflow[filterBox] = new OverflowEntry
        {
            Kind = OverflowKind.Filter,
            Label = T("FormBrowse/ToolStripFilters.Text", "Filter"),
            Icon = "FunnelPencil",
            FilterBox = filterBox,
        };

        Content = bar;
    }

    public void SetSubmoduleNavigation(string? immediateSuperprojectPath)
    {
        _immediateSuperprojectPath = immediateSuperprojectPath;
        bool canGoUp = !string.IsNullOrWhiteSpace(immediateSuperprojectPath);
        Image? replacement = IconLoader.Image(canGoUp ? "NavigateUp" : "SubmodulesManage", 16);
        if (_submodulesIcon is not null && replacement is not null)
        {
            _submodulesIcon.Source = replacement.Source;
        }

        if (_submodulesHost is not null)
        {
            string tooltip = canGoUp
                ? T("Go to superproject")
                : T("Open a submodule as the active repository");
            ToolTip.SetTip(_submodulesHost, tooltip);
            if (_submodulesBody is not null) ToolTip.SetTip(_submodulesBody, tooltip);
            if (_submodulesArrow is not null) ToolTip.SetTip(_submodulesArrow, tooltip);
        }
    }

    /// <summary>
    ///  Refreshes the toolbar's live indicators from the current repository state.
    ///  Call on every refresh, from the UI thread. Idempotent: repeated calls
    ///  update the same captions / indicator text in place — badges never stack.
    /// </summary>
    /// <param name="ahead">Commits the local branch is ahead of its upstream.</param>
    /// <param name="behind">Commits the local branch is behind its upstream.</param>
    /// <param name="staged">Number of staged (index) changes.</param>
    /// <param name="unstaged">Number of unstaged working-tree changes.</param>
    /// <param name="repoPath">Absolute path of the active repository (may be empty).</param>
    /// <param name="branch">Current branch name (may be empty).</param>
    public void UpdateState(int ahead, int behind, int staged, int unstaged, string repoPath, string branch)
        => UpdateState(ahead, behind, staged, unstaged, repoPath, branch, state: null);

    /// <summary>
    ///  As <see cref="UpdateState(int, int, int, int, string, string)"/>, plus the
    ///  repository-wide facts the toolbar cannot read itself (see
    ///  <see cref="ToolbarRepoState"/>): the stash count, the working-directory
    ///  state driving the Commit icon, the worktree and remote counts, and whether
    ///  the branch's upstream is tracked / gone. Produce it off the UI thread with
    ///  <see cref="ToolbarStateService.Probe"/> and pass it here on the UI thread.
    ///  Passing <c>null</c> keeps the previously supplied state.
    /// </summary>
    public void UpdateState(int ahead, int behind, int staged, int unstaged, string repoPath, string branch,
        ToolbarRepoState? state)
    {
        // Remembered so a language rebuild can replay it onto the fresh captions.
        _hasState = true;
        _lastAhead = ahead;
        _lastBehind = behind;
        _lastStaged = staged;
        _lastUnstaged = unstaged;
        _lastRepoPath = repoPath ?? string.Empty;
        _lastBranch = branch ?? string.Empty;
        if (state is not null)
        {
            _state = state;
        }

        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#8A8A8A");
        IBrush accent = Brush("App.Accent", "#007ACC");

        // Push: mirror upstream's ToolStripPushButton — the caption is
        // AheadBehindData.ToDisplay() ("1↑ 2↓", "0↑↓", or "✗" for a gone upstream),
        // the icon becomes Images.Unstage while the branch is behind, and the tooltip
        // spells out both halves.
        if (_pushCaption is not null)
        {
            ApplyPushState(ahead, behind, text, accent);
        }

        // Pull: light up with a "behind" badge when there are commits to pull.
        if (_pullCaption is not null)
        {
            bool lit = behind > 0;
            _pullCaption.Text = lit
                ? string.Format(T("{0} ↓{1}"), T("FormBrowse/toolStripButtonPull.Text", "Pull"), behind)
                : T("FormBrowse/toolStripButtonPull.Text", "Pull");
            _pullCaption.Foreground = lit ? accent : text;
            ShowCaption(_pullCaption, lit);
            if (_pullIcon is not null)
            {
                _pullIcon.Opacity = lit ? 1.0 : 0.85;
            }
        }

        // Commit: icon from the repository state (upstream's RepoStateVisualiser),
        // caption "Commit (n)" and a state-matched colour.
        if (_commitCaption is not null)
        {
            ApplyCommitState(staged, unstaged);
        }

        // Everything driven purely by the repo-wide state (stash count + enablement,
        // worktrees visibility) — also called on its own after a language rebuild.
        ApplyRepoState();

        // Inline branch dropdown caption: current branch (or "(no branch)").
        _currentBranch = branch ?? string.Empty;
        if (_branchCaption is not null)
        {
            _branchCaption.Text = string.IsNullOrWhiteSpace(branch) ? NoBranchCaption() : branch;
            _branchCaption.Foreground = text;
        }

        // Inline repo-path dropdown caption: current repo path, home collapsed to ~.
        if (_repoPathCaption is not null)
        {
            _repoPathCaption.Text = string.IsNullOrWhiteSpace(repoPath)
                ? T("(no repository)")
                : CollapseHome(repoPath);
        }

        // Working-directory indicator, created lazily and updated in place.
        if (_repoIndicator is null)
        {
            _repoIndicator = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = dim,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 480,
                Margin = new Thickness(16, 0, 4, 0),
            };
            _bar.AddItem(_repoIndicator);
            _overflow[_repoIndicator] = new OverflowEntry
            {
                Kind = OverflowKind.Text,
                TextSource = _repoIndicator,
            };
        }

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            _repoIndicator.Text = T("(no repository)");
            _repoIndicator.Foreground = dim;
            ToolTip.SetTip(_repoIndicator, null);
        }
        else
        {
            string name = System.IO.Path.GetFileName(repoPath.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(name))
            {
                name = repoPath;
            }

            string shown = CollapseHome(repoPath);
            string label = string.IsNullOrWhiteSpace(branch)
                ? string.Format(T("{0} — {1}"), name, shown)
                : string.Format(T("{0} — {1} ({2})"), name, shown, branch);

            _repoIndicator.Text = label;
            _repoIndicator.Foreground = dim;
            ToolTip.SetTip(_repoIndicator, label);
        }
    }

    // Path display (home collapsed to "~") is shared with the revision grid's
    // status line — see PathDisplay.CollapseHome.
    private static string CollapseHome(string path) => PathDisplay.CollapseHome(path);

    /// <summary>
    ///  Reflects the host's split-view state on the toggle: a checked, accented
    ///  caption while the commit detail and the diff are shown side by side. The
    ///  overflow menu picks the same caption up through the entry's LiveCaption.
    /// </summary>
    public void SetSplitView(bool on)
    {
        _splitViewOn = on;
        if (_splitCaption is null)
        {
            return;
        }

        _splitCaption.Text = on ? string.Format(T("{0} ✓"), T("Split view")) : T("Split view");
        _splitCaption.Foreground = on ? Brush("App.Accent", "#3399FF") : Brush("App.Text", "#DCDCDC");
        SetChecked(_splitButton, _splitIcon, on);
        if (_splitButton is not null)
        {
            ToolTip.SetTip(_splitButton, on
                ? T("Split view on: commit detail and diff side by side in the Commit tab")
                : T("Show the commit detail and the diff side by side in the Commit tab"));
        }
    }

    /// <summary>
    ///  Reflects whether the host's left (repository objects) panel is showing, as
    ///  upstream's <c>RefreshLayoutToggleButtonStates</c> does with
    ///  <c>toggleLeftPanel.Checked = !MainSplitContainer.Panel1Collapsed</c>. The
    ///  toolbar only displays the state — the host owns it, and should call this
    ///  both at start-up and after every toggle (including the hotkey path, which
    ///  does not go through the button).
    /// </summary>
    public void SetLeftPanelVisible(bool visible)
    {
        _leftPanelVisible = visible;
        if (_leftPanelCaption is null)
        {
            return;
        }

        // Same "checked" idiom as the split-view toggle: a ✓ in the caption (which
        // the overflow menu picks up through LiveCaption) plus an accented colour.
        _leftPanelCaption.Text = visible ? string.Format(T("{0} ✓"), T("Left panel")) : T("Left panel");
        _leftPanelCaption.Foreground = visible ? Brush("App.Accent", "#3399FF") : Brush("App.Text", "#DCDCDC");
        SetChecked(_leftPanelButton, _leftPanelIcon, visible);
    }

    /// <summary>
    ///  Reflects the active commit-info position WITHOUT raising
    ///  <see cref="CommitInfoPositionChanged"/>, so the host can push the value it
    ///  restored from its persisted layout. Copies the icon and the tooltip from the
    ///  entry matching <paramref name="position"/>, exactly as upstream's
    ///  <c>RefreshLayoutToggleButtonStates</c> copies them out of
    ///  <c>menuCommitInfoPosition.DropDownItems[(int)position]</c>.
    /// </summary>
    public void SetCommitInfoPosition(CommitInfoPosition position)
    {
        _commitInfoPosition = position;

        (string icon, string label) = CommitInfoEntry(position);
        IconLoader.Retarget(_commitInfoIcon, icon);

        if (_commitInfoButton is not null)
        {
            ToolTip.SetTip(_commitInfoButton, label);
        }
    }

    // ---- Push button (upstream ToolStripPushButton) ---------------------------

    // Renders the ahead/behind pair through the core's own AheadBehindData.ToDisplay(),
    // so the port cannot drift from upstream's formatting rules ("0↑↓" when in sync,
    // "2↑ 1↓" when diverged, "✗" when the upstream ref is gone). Reuses upstream's
    // two tooltip sentences, and swaps in Images.Unstage while the branch is behind —
    // the visual warning that a plain push will be rejected.
    private void ApplyPushState(int ahead, int behind, IBrush text, IBrush accent)
    {
        string push = T("FormBrowse/toolStripButtonPush.Text", "Push");

        // Unknown tracking state (no host probe yet) is inferred from the counts: a
        // non-zero ahead/behind can only come from a tracked branch.
        bool hasUpstream = _state.HasUpstream ?? (ahead > 0 || behind > 0);

        if (!hasUpstream)
        {
            // No upstream configured: nothing meaningful to count, so the button
            // stays in its plain resting state rather than claiming "0↑↓".
            _pushCaption!.Text = push;
            _pushCaption.Foreground = text;
            // Icon-only in the resting state, as upstream's DisplayStyle = Image.
            ShowCaption(_pushCaption, show: false);
            SetPushIcon("Push", lit: false);
            SetPushTip(T("FormPush/_errorPushToRemoteCaption.Text", "Push to remote"));
            return;
        }

        AheadBehindData data = _state.UpstreamGone
            ? new AheadBehindData(_lastBranch, string.Empty, AheadBehindData.Gone, string.Empty)
            : new AheadBehindData(
                _lastBranch,
                string.Empty,
                ahead.ToString(),
                behind > 0 ? behind.ToString() : string.Empty);

        _pushCaption!.Text = string.Format(T("{0} {1}"), push, data.ToDisplay());
        bool lit = ahead > 0 || behind > 0 || _state.UpstreamGone;
        _pushCaption.Foreground = lit ? accent : text;

        // The ahead/behind badge is the one thing worth widening the button for —
        // upstream turns AutoSize on for exactly this (ToolStripPushButton.cs:31-33).
        // "0↑↓" says nothing the icon does not, so it stays collapsed.
        ShowCaption(_pushCaption, lit);

        // Upstream: "if (!string.IsNullOrEmpty(data.BehindCount)) Image = Images.Unstage".
        SetPushIcon(behind > 0 ? "Unstage" : "Push", lit);

        SetPushTip(PushTooltip(ahead, behind));
    }

    // The Push button only exists between Build() calls, so the tip is set defensively.
    private void SetPushTip(string tooltip)
    {
        if (_pushButton is not null)
        {
            ToolTip.SetTip(_pushButton, tooltip);
        }
    }

    private void SetPushIcon(string iconName, bool lit)
    {
        if (_pushIcon is null)
        {
            return;
        }

        IconLoader.Retarget(_pushIcon, iconName);
        _pushIcon.Opacity = lit ? 1.0 : 0.85;
    }

    // Upstream's ToolStripPushButton.GetToolTipText, with its two translated
    // sentences joined by a newline when the branch has diverged both ways.
    private string PushTooltip(int ahead, int behind)
    {
        if (_state.UpstreamGone)
        {
            return T("The upstream branch is gone");
        }

        List<string> lines = [];
        if (ahead > 0)
        {
            lines.Add(string.Format(
                T("ToolStripPushButton/_aheadCommitsToPush.Text", "{0} new commit(s) will be pushed"), ahead));
        }

        if (behind > 0)
        {
            lines.Add(string.Format(
                T("ToolStripPushButton/_behindCommitsTointegrateOrForcePush.Text",
                    "{0} commit(s) should be integrated (or will be lost if force pushed)"), behind));
        }

        return lines.Count == 0
            ? T("FormPush/_errorPushToRemoteCaption.Text", "Push to remote")
            : string.Join(Environment.NewLine, lines);
    }

    // ---- Commit button (upstream UpdateCommitButtonAndGetBrush) ---------------

    // Icon straight from the repository state (the upstream RepoState*.png set) and
    // "Commit (n)" for the change count. The button's MinWidth only ever grows, which
    // is the port's equivalent of upstream freezing Width so the strip stops jittering
    // as the count changes width.
    private void ApplyCommitState(int staged, int unstaged)
    {
        // Prefer the probed state; with no probe yet, derive the obvious cases from
        // the counts the host always supplies so the icon is never simply wrong.
        RepoState state = _state.State != RepoState.Unknown
            ? _state.State
            : (staged, unstaged) switch
            {
                (0, 0) => RepoState.Clean,
                (0, _) => RepoState.Dirty,
                (_, 0) => RepoState.Staged,
                _ => RepoState.Mixed,
            };

        int changes = _state.State != RepoState.Unknown && _state.ChangeCount > 0
            ? _state.ChangeCount
            : staged + unstaged;

        _commitCaption!.Text = changes > 0
            ? string.Format(T("{0} ({1})"), T("FormBrowse/toolStripButtonCommit.Text", "Commit"), changes)
            : T("FormBrowse/toolStripButtonCommit.Text", "Commit");
        _commitCaption.Foreground = CommitStateBrush(state);

        if (_commitIcon is not null)
        {
            // Upstream ships seven different raster icons for the seven repo states.
            // Here the state is already spoken by the CAPTION's colour, so the icon
            // stays one glyph and takes the same state key as its tint: one shape,
            // one colour, said once — and it survives a theme switch, which seven
            // baked bitmaps could not.
            // Classic keeps upstream's seven per-state bitmaps: there the state is said
            // by the ICON, not by a tint, so "the earlier look" is a different picture
            // per state — and "Commit" is the one glyph of the ninety with no PNG.
            IconLoader.Retarget(
                _commitIcon, "Commit", CommitStateKey(state), ToolbarStateService.IconFor(state));

            // The state icons are meaningful in their own right, so unlike the old
            // fixed icon they are never dimmed.
            _commitIcon.Opacity = 1.0;
        }

        FreezeCommitWidth();
    }

    // The palette key that carries a state's colour, for the icon tint. The brush
    // itself comes from CommitStateBrush below, which also holds the upstream
    // fallbacks for a theme that does not register the key.
    private static string CommitStateKey(RepoState state) => state switch
    {
        RepoState.Clean => "App.RepoStateClean",
        RepoState.Dirty => "App.RepoStateDirty",
        RepoState.DirtySubmodules => "App.RepoStateDirtySubmodules",
        RepoState.Mixed => "App.RepoStateMixed",
        RepoState.Staged => "App.RepoStateStaged",
        RepoState.UntrackedOnly => "App.RepoStateUntrackedOnly",
        _ => "App.TextDim",
    };

    // Upstream's RepoStateVisualiser pairs each state with a colour; those colours are
    // offered as theme keys first (so a theme can override them) and fall back to the
    // upstream values.
    private IBrush CommitStateBrush(RepoState state) => state switch
    {
        RepoState.Clean => Brush("App.RepoStateClean", "#8A8A8A"),
        RepoState.Dirty => Brush("App.RepoStateDirty", "#FFA07A"),
        RepoState.DirtySubmodules => Brush("App.RepoStateDirtySubmodules", "#FFA500"),
        RepoState.Mixed => Brush("App.RepoStateMixed", "#E6A700"),
        RepoState.Staged => Brush("App.RepoStateStaged", "#87CEFA"),
        RepoState.UntrackedOnly => Brush("App.RepoStateUntrackedOnly", "#8A63D2"),
        _ => Brush("App.TextDim", "#8A8A8A"),
    };

    // Grows (never shrinks) the Commit button's MinWidth to the widest it has been
    // measured at, so gaining/losing a digit does not shift the whole strip.
    private void FreezeCommitWidth()
    {
        if (_commitButton is null)
        {
            return;
        }

        // Measured width is only known after a layout pass, so re-check next beat.
        Dispatcher.UIThread.Post(() =>
        {
            if (_commitButton is null)
            {
                return;
            }

            double width = _commitButton.Bounds.Width;
            if (width > _commitMinWidth)
            {
                _commitMinWidth = width;
                _commitButton.MinWidth = width;
            }
        }, DispatcherPriority.Background);
    }

    // ---- repository-wide state (stash count, enablement, visibility) ----------

    // Applies everything that depends only on _state: the stash caption's "(n)" and
    // its enablement, and whether the worktrees button exists on the strip at all.
    private void ApplyRepoState()
    {
        bool canStash = _state.IsValidWorkingDir && !_state.IsBare;

        if (_stashCaption is not null)
        {
            string stash = T("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash");

            // Upstream puts the bare count in the button's Text ("(3)"); the port keeps
            // the word too, because the same string labels the overflow-menu entry
            // through LiveCaption — but the caption only shows while there is a count.
            _stashCaption.Text = _state.StashCount > 0
                ? string.Format(T("{0} ({1})"), stash, _state.StashCount)
                : stash;
            ShowCaption(_stashCaption, _state.StashCount > 0);
            _stashCaption.Foreground = canStash
                ? Brush("App.Text", "#DCDCDC")
                : Brush("App.TextDim", "#8A8A8A");
        }

        if (_stashHost is not null)
        {
            // Disabling the Border disables both halves of the split button.
            _stashHost.IsEnabled = canStash;
        }

        if (_worktreesHost is not null)
        {
            // Upstream: toolStripWorktrees.Visible = worktrees.Count > 1, and false
            // outright when the directory is not a valid working dir. The button is
            // removed from / restored to the strip rather than merely hidden, so the
            // OverflowPanel does not reserve room for an invisible item and the
            // overflow menu does not list it either.
            // A negative count means "nobody has probed yet", and the button stays.
            bool show = _state.IsValidWorkingDir && (_state.WorktreeCount < 0 || _state.WorktreeCount > 1);
            SetItemPresent(_worktreesHost, show);
        }
    }

    // Adds/removes a built toolbar item without rebuilding the strip, keeping its
    // original position so the button does not jump to the end when it comes back.
    private void SetItemPresent(Control item, bool present)
    {
        if (present == _bar.Contains(item))
        {
            return;
        }

        if (present)
        {
            _bar.RestoreItem(item);
        }
        else
        {
            _bar.RemoveItem(item);
        }
    }

    private Button MakeButton(string iconName, string label, string tooltip, Action onClick)
        => MakeButton(iconName, label, tooltip, onClick, out _, out _);

    /// <summary>
    ///  Collapses a button's caption so it renders as icon-only, which is what
    ///  fourteen of upstream's nineteen ToolStripMain items do (DisplayStyle = Image,
    ///  or the ImageAndText default with no Text): a 16px image in a 23x22 cell, the
    ///  wording living in the tooltip. The caption is hidden rather than never built,
    ///  because it is still what the overflow menu labels its entry with
    ///  (<see cref="OverflowEntry.LiveCaption"/>) and what the state updates write to.
    ///  A button whose icon failed to load keeps its caption — it would otherwise be
    ///  an empty cell.
    /// </summary>
    private static Button IconOnly(Button button)
    {
        if (button.Content is StackPanel { Children.Count: > 1 } content
            && content.Children[^1] is TextBlock caption)
        {
            caption.IsVisible = false;
        }

        return button;
    }

    // Upstream tells a toggle's "on" state with ToolStripButton.Checked, which the
    // renderer paints as a filled cell. The port has no Checked, so the two toggles
    // paint the same way themselves: an accent-tinted fill and edge on the button.
    // Set on the instance, not through a style, so no Theming file is involved.
    private void SetChecked(Button? button, Image? icon, bool on)
    {
        if (button is null)
        {
            return;
        }

        button.Background = on ? Brush("App.AccentFill", "#264F78") : Fade(Brush("App.Toolbar", "#333337"));
        button.BorderBrush = on ? Brush("App.Accent", "#007ACC") : Brushes.Transparent;
        if (icon is not null)
        {
            icon.Opacity = on ? 1.0 : 0.85;
        }
    }

    // Shows or hides an icon-only button's caption: the port's equivalent of
    // upstream's ToolStripPushButton flipping AutoSize on when it has an ahead/behind
    // badge to show, and back off when it has not.
    private static void ShowCaption(TextBlock? caption, bool show)
    {
        if (caption is not null)
        {
            caption.IsVisible = show;
        }
    }

    // Variant that hands back the caption TextBlock and (optional) icon Image so
    // callers can keep references for later restyling (see UpdateState).
    private Button MakeButton(string iconName, string label, string tooltip, Action onClick,
        out TextBlock caption, out Image? icon)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        icon = IconLoader.Image(iconName, 16);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
        }

        // Show the label always when there's no icon, otherwise as a short caption.
        caption = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
        };
        content.Children.Add(caption);

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            // A 1px (resting-transparent) border keeps layout stable while the
            // hover/pressed styles paint a visible edge in the same space.
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        _overflow[button] = new OverflowEntry
        {
            Kind = OverflowKind.Command,
            Label = label,
            Icon = iconName,
            Invoke = onClick,
            LiveCaption = caption,
        };
        return button;
    }

    // ---- Stash split button (upstream toolStripSplitStash) --------------------

    // Body = "Manage stashes" (upstream's ToolStripSplitStashButtonClick calls
    // StartStashDialog, the same command as the "Manage stashes…" entry); arrow drops
    // Stash / Stash staged / Stash pop / — / Manage stashes… / Create a stash…, in
    // upstream's order. The caption carries the "(n)" stash count (UpdateStashCount).
    // ---- Shell split button (upstream userShell) ------------------------------

    // Upstream's userShell is a ToolStripSplitButton (FormBrowse.Designer.cs) filled
    // by FillUserShells: the arrow lists every shell whose executable is actually
    // present, each with its icon and its name as the tooltip; the body starts the
    // selected one; the button's own icon and tooltip are those of that shell; and
    // the whole button is hidden when no shell exists at all.
    //
    // The port enumerates the shells off the UI thread (ExternalToolService.GetShells
    // probes PATH) and restores the last pick from disk, because a toolbar must not
    // block on filesystem probing while it is being built.
    private Control MakeShellSplitButton(IBrush border)
    {
        StackPanel bodyContent = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        _shellIcon = IconLoader.Image("Console", 16);
        if (_shellIcon is not null)
        {
            _shellIcon.VerticalAlignment = VerticalAlignment.Center;
            bodyContent.Children.Add(_shellIcon);
        }

        _shellCaption = new TextBlock
        {
            Text = T("Terminal"),
            // Upstream's userShell shows its icon only (no Text); the shell's name is
            // in the tooltip, and this caption survives to label the overflow entry.
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
        };
        bodyContent.Children.Add(_shellCaption);

        _shellBody = new Button
        {
            Content = bodyContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _shellBody.Classes.Add("toolbtn");
        ToolTip.SetTip(_shellBody, T("Open a terminal in the repository directory"));
        _shellBody.Click += (_, _) => LaunchCurrentShell();

        Border divider = new()
        {
            Width = 1,
            Margin = new Thickness(0, 3),
            Background = border,
        };

        Button arrow = new()
        {
            Content = new TextBlock
            {
                Text = "▾",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 10,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        arrow.Classes.Add("toolbtn");
        ToolTip.SetTip(arrow, T("Choose the shell to open"));

        // Populated BEFORE ShowAt, never inside Opening: Avalonia 11.3.x measures a
        // MenuFlyout's content once, when the popup opens, and never re-measures it —
        // items added later leave a thin empty sliver instead of a menu.
        MenuFlyout flyout = new();
        arrow.Click += (_, _) =>
        {
            BuildShellMenu(flyout);
            flyout.ShowAt(arrow);
        };

        _shellHost = new Border
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _shellBody, divider, arrow },
            },
        };

        _overflow[_shellHost] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = T("Terminal"),
            Icon = "Console",
            LiveCaption = _shellCaption,
            ShowMenu = anchor =>
            {
                BuildShellMenu(flyout);
                flyout.ShowAt(anchor);
                return Task.CompletedTask;
            },
        };

        LoadShellsAsync();
        return _shellHost;
    }

    // Probes PATH and reads the stored preference on a worker, then adopts the
    // result on the UI thread. Rebuild() calls MakeShellSplitButton again (language
    // switch), so this may run more than once — it is idempotent.
    private void LoadShellsAsync()
    {
        _ = Task.Run(() =>
        {
            IReadOnlyList<ShellDescriptor> shells;
            string? preferred;
            try
            {
                shells = ExternalToolService.GetShells();
                preferred = ExternalToolService.LoadPreferredShell();
            }
            catch
            {
                shells = Array.Empty<ShellDescriptor>();
                preferred = null;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _shells = shells;

                // Upstream: when the configured shell is no longer available, fall
                // back to the first one that is.
                _currentShell = shells.FirstOrDefault(s =>
                                    string.Equals(s.Executable, preferred, StringComparison.Ordinal))
                                ?? shells.FirstOrDefault();
                ApplyShellAppearance();

                // "userShell.Visible = userShell.DropDownItems.Count > 0". Removing the
                // item (rather than hiding it) keeps the OverflowPanel from reserving
                // space for it and keeps it out of the "»" menu too.
                if (_shellHost is not null)
                {
                    SetItemPresent(_shellHost, shells.Count > 0);
                }
            });
        });
    }

    // Mirrors upstream's "userShell.Image / ToolTipText / Tag = selected shell".
    private void ApplyShellAppearance()
    {
        if (_currentShell is not { } shell)
        {
            return;
        }

        if (_shellCaption is not null)
        {
            _shellCaption.Text = shell.Name;
        }

        IconLoader.Retarget(_shellIcon, shell.IconName);

        if (_shellBody is not null)
        {
            ToolTip.SetTip(_shellBody, string.Format(
                T("Open {0} in the repository directory"), shell.Name));
        }

        // The overflow ("»") entry picks the caption up on its own through
        // LiveCaption, so it shows the current shell's name without extra work. Its
        // icon stays the generic "Console" — OverflowEntry.Icon is init-only, and
        // every Unix shell maps to that icon anyway.
    }

    // One entry per installed shell, the current one marked — upstream shows the
    // shell name as both caption and tooltip.
    private void BuildShellMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();

        if (_shells.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = T("(no shell found)"), IsEnabled = false });
            return;
        }

        foreach (ShellDescriptor shell in _shells)
        {
            bool isCurrent = _currentShell is { } current
                             && string.Equals(current.Executable, shell.Executable, StringComparison.Ordinal);

            MenuItem item = new()
            {
                Header = new TextBlock
                {
                    Text = shell.Name,
                    FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
                },
                Icon = IconLoader.Image(shell.IconName, 16),
            };
            ToolTip.SetTip(item, shell.Name);

            ShellDescriptor picked = shell;
            item.Click += (_, _) =>
            {
                _currentShell = picked;
                ApplyShellAppearance();

                // Persisting is a two-byte file write; still, keep it off the UI thread.
                _ = Task.Run(() => ExternalToolService.SavePreferredShell(picked.Executable));
                LaunchCurrentShell();
            };
            flyout.Items.Add(item);
        }
    }

    // The body click, and the tail of a dropdown pick: upstream's userShell_Click
    // starts the selected shell in the repository directory. With no host handler
    // wired we still open a terminal (login shell) rather than doing nothing.
    private void LaunchCurrentShell()
    {
        if (_currentShell is { } shell && OpenShellRequested is not null)
        {
            OpenShellRequested.Invoke(shell.Executable);
            return;
        }

        OpenTerminalRequested?.Invoke();
    }

    private Control MakeStashSplitButton(IBrush border)
    {
        StackPanel bodyContent = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        Image? icon = IconLoader.Image("stash", 16);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            bodyContent.Children.Add(icon);
        }

        _stashCaption = new TextBlock
        {
            Text = T("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash"),
            // Upstream's toolStripSplitStash carries no Text at all; the port shows
            // the caption only while there is a stash count worth reporting.
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
        };
        bodyContent.Children.Add(_stashCaption);

        Button body = new()
        {
            Content = bodyContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        body.Classes.Add("toolbtn");
        ToolTip.SetTip(body, T("FormBrowse/toolStripSplitStash.ToolTipText", "Manage stashes"));
        body.Click += (_, _) => ManageStashesRequested?.Invoke();

        Border divider = new()
        {
            Width = 1,
            Margin = new Thickness(0, 3),
            Background = border,
        };

        Button arrow = new()
        {
            Content = new TextBlock
            {
                Text = "▾",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 10,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        arrow.Classes.Add("toolbtn");
        ToolTip.SetTip(arrow, T("FormBrowse/stashChangesToolStripMenuItem.ToolTipText", "Stash changes"));

        // Populated BEFORE ShowAt and rebuilt on every click — Avalonia 11.3.x
        // measures a MenuFlyout's content when the popup opens and never re-measures,
        // so items added later would leave a thin empty sliver. Rebuilding also keeps
        // "Stash staged" in step with the probed git-version support.
        MenuFlyout flyout = new();
        arrow.Click += (_, _) =>
        {
            BuildStashMenu(flyout);
            flyout.ShowAt(arrow);
        };

        _stashHost = new Border
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { body, divider, arrow },
            },
        };

        _overflow[_stashHost] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = T("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash"),
            Icon = "stash",
            LiveCaption = _stashCaption,
            ShowMenu = anchor =>
            {
                BuildStashMenu(flyout);
                flyout.ShowAt(anchor);
                return Task.CompletedTask;
            },
        };

        return _stashHost;
    }

    // Upstream's toolStripSplitStash.DropDownItems, in order.
    private void BuildStashMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();

        flyout.Items.Add(StashItem(
            T("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash"),
            T("FormBrowse/stashChangesToolStripMenuItem.ToolTipText", "Stash changes"),
            "stash", BrowseCommand.Stash, () => StashRequested?.Invoke()));

        // Upstream gates this on Module.GitVersion.SupportStashStaged (git 2.35+
        // introduced "git stash push --staged"), and so does the port's probe.
        if (_state.SupportsStashStaged)
        {
            flyout.Items.Add(StashItem(
                T("FormBrowse/stashStagedToolStripMenuItem.Text", "Stash staged"),
                T("FormBrowse/stashStagedToolStripMenuItem.ToolTipText", "Stash staged changes"),
                "stash", BrowseCommand.StashStaged, () => StashStagedRequested?.Invoke()));
        }

        flyout.Items.Add(StashItem(
            T("FormBrowse/stashPopToolStripMenuItem.Text", "Stash pop"),
            T("FormBrowse/stashPopToolStripMenuItem.ToolTipText", "Apply and drop single stash"),
            "stash", BrowseCommand.StashPop, () => StashPopRequested?.Invoke()));

        flyout.Items.Add(new MenuSeparator());

        flyout.Items.Add(StashItem(
            T("FormBrowse/manageStashesToolStripMenuItem.Text", "Manage stashes…"),
            T("FormBrowse/manageStashesToolStripMenuItem.ToolTipText", "Manage stashes"),
            "stash", command: null, () => ManageStashesRequested?.Invoke()));

        flyout.Items.Add(StashItem(
            T("FormBrowse/createAStashToolStripMenuItem.Text", "Create a stash…"),
            tooltip: null, "stash", command: null, () => CreateStashRequested?.Invoke()));
    }

    // One stash drop-down entry. Entries whose event nobody wired are shown disabled
    // rather than silently doing nothing when clicked.
    private MenuItem StashItem(string header, string? tooltip, string icon,
        BrowseCommand? command, Action onClick)
    {
        MenuItem item = new()
        {
            Header = header,
            Icon = IconLoader.Image(icon, 16),
        };

        if (command is { } c && GestureFor(c) is { } gesture)
        {
            // Display only: the window-level HotkeyService owns the real binding.
            item.InputGesture = gesture;
        }

        if (!string.IsNullOrEmpty(tooltip))
        {
            ToolTip.SetTip(item, tooltip);
        }

        item.Click += (_, _) => onClick();
        return item;
    }

    // ---- Commit-info position split button (upstream menuCommitInfoPosition) ---

    // Body cycles the three positions ((pos + 1) % 3, upstream CommitInfoPositionClick);
    // the arrow drops the three entries with a radio mark on the active one. Both the
    // icon and the tooltip track the active position through SetCommitInfoPosition.
    private Control MakeCommitInfoSplitButton(IBrush border)
    {
        (string activeIcon, string activeLabel) = CommitInfoEntry(_commitInfoPosition);

        StackPanel bodyContent = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        _commitInfoIcon = IconLoader.Image(activeIcon, 16);
        if (_commitInfoIcon is not null)
        {
            _commitInfoIcon.VerticalAlignment = VerticalAlignment.Center;
            bodyContent.Children.Add(_commitInfoIcon);
        }

        bodyContent.Children.Add(new TextBlock
        {
            Text = T("Commit info"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
        });

        Button body = new()
        {
            Content = bodyContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        body.Classes.Add("toolbtn");
        ToolTip.SetTip(body, activeLabel);
        body.Click += (_, _) => CycleCommitInfoPosition();
        _commitInfoButton = body;

        Border divider = new()
        {
            Width = 1,
            Margin = new Thickness(0, 3),
            Background = border,
        };

        Button arrow = new()
        {
            Content = new TextBlock
            {
                Text = "▾",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 10,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        arrow.Classes.Add("toolbtn");
        ToolTip.SetTip(arrow, T("FormBrowse/menuCommitInfoPosition.ToolTipText", "Commit info position"));

        MenuFlyout flyout = new();
        arrow.Click += (_, _) =>
        {
            BuildCommitInfoMenu(flyout);
            flyout.ShowAt(arrow);
        };

        Border host = new()
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { body, divider, arrow },
            },
        };

        _overflow[host] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = T("Commit info"),
            Icon = activeIcon,
            ShowMenu = anchor =>
            {
                BuildCommitInfoMenu(flyout);
                flyout.ShowAt(anchor);
                return Task.CompletedTask;
            },
        };

        return host;
    }

    // Upstream: SetCommitInfoPosition((CommitInfoPosition)(((int)current + 1) % 3)).
    private void CycleCommitInfoPosition()
    {
        CommitInfoPosition next = (CommitInfoPosition)(
            ((int)_commitInfoPosition + 1) % Enum.GetValues<CommitInfoPosition>().Length);

        // Reflect it immediately so the icon follows the click even if the host is
        // slow (or declines) to echo the new position back.
        SetCommitInfoPosition(next);
        CommitInfoPositionChanged?.Invoke(next);
    }

    private void BuildCommitInfoMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        foreach (CommitInfoPosition position in Enum.GetValues<CommitInfoPosition>())
        {
            (string icon, string label) = CommitInfoEntry(position);
            CommitInfoPosition captured = position;
            MenuItem item = new()
            {
                Header = label,
                Icon = IconLoader.Image(icon, 16),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = position == _commitInfoPosition,
            };
            item.Click += (_, _) =>
            {
                SetCommitInfoPosition(captured);
                CommitInfoPositionChanged?.Invoke(captured);
            };
            flyout.Items.Add(item);
        }
    }

    // The icon + menu text upstream pairs with each position. The enum order matches
    // upstream's AppSettings.CommitInfoPosition (BelowList/Leftward/Rightward), which
    // is what makes the "index the drop-down by the enum value" trick work there.
    private static (string Icon, string Label) CommitInfoEntry(CommitInfoPosition position) => position switch
    {
        CommitInfoPosition.LeftOfGraph => ("LayoutSidebarTopLeft",
            T("FormBrowse/commitInfoLeftwardMenuItem.Text", "Commit info left of graph")),
        CommitInfoPosition.RightOfGraph => ("LayoutSidebarTopRight",
            T("FormBrowse/commitInfoRightwardMenuItem.Text", "Commit info right of graph")),
        _ => ("LayoutFooterTab", T("FormBrowse/commitInfoBelowMenuItem.Text", "Commit info below graph")),
    };

    // ---- Pull split button ---------------------------------------------------

    /// <summary>
    ///  Which action the Pull button's body performs. Setting it re-labels the body
    ///  (icon + tooltip) but raises no event, so the host can push the value it
    ///  restored from <see cref="UiState.DefaultPullAction"/> at start-up without
    ///  echoing it back. <see cref="GitPullAction.None"/> and
    ///  <see cref="GitPullAction.Default"/> are normalised to
    ///  <see cref="GitPullAction.Merge"/>, exactly as <c>FormPull</c> does.
    /// </summary>
    public GitPullAction DefaultPullAction
    {
        get => _defaultPullAction;
        set
        {
            _defaultPullAction = Normalize(value);
            ApplyDefaultPullAction();
        }
    }

    private static GitPullAction Normalize(GitPullAction action)
        => action is GitPullAction.None or GitPullAction.Default ? GitPullAction.Merge : action;

    // The Pull split button: a body that runs the default action and, separated by a
    // hairline, an arrow that drops the actions menu — the port's stand-in for the
    // original ToolStripSplitButton (FormBrowse.Designer.cs, toolStripButtonPull).
    //
    // The two halves are real Buttons inside one Border so each keeps its own hover
    // feedback (like the Windows control, where body and arrow highlight
    // separately) while the pair still reads as a single item — including for the
    // overflow menu, which sees the Border as one entry.
    private Control MakePullSplitButton(IBrush border)
    {
        StackPanel bodyContent = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        _pullIcon = IconLoader.Image(PullIcon(_defaultPullAction), 16);
        if (_pullIcon is not null)
        {
            _pullIcon.VerticalAlignment = VerticalAlignment.Center;
            bodyContent.Children.Add(_pullIcon);
        }

        _pullCaption = new TextBlock
        {
            Text = T("FormBrowse/toolStripButtonPull.Text", "Pull"),
            // Upstream's toolStripButtonPull is DisplayStyle = Image: the caption only
            // surfaces to carry the "behind" badge (see UpdateState).
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
        };
        bodyContent.Children.Add(_pullCaption);

        Button body = new()
        {
            Content = bodyContent,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        body.Classes.Add("toolbtn");
        body.Click += (_, _) => RaisePull(_defaultPullAction);
        _pullButton = body;

        Border divider = new()
        {
            Width = 1,
            Margin = new Thickness(0, 3),
            Background = border,
        };

        Button arrow = new()
        {
            Content = new TextBlock
            {
                Text = "▾",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 10,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        arrow.Classes.Add("toolbtn");
        ToolTip.SetTip(arrow, T("FormBrowse/pullToolStripMenuItem.Text", "Pull / Fetch"));

        // Populated BEFORE ShowAt, never inside Opening: Avalonia 11.3.x measures a
        // MenuFlyout's content when the popup opens and does not re-measure it
        // afterwards, so items added later leave a thin, empty sliver on screen.
        // Rebuilding on every click also keeps the radio marks of the
        // "Set default Pull button action" submenu current.
        MenuFlyout flyout = new();
        arrow.Click += (_, _) =>
        {
            BuildPullMenu(flyout);
            flyout.ShowAt(arrow);
        };

        Border host = new()
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { body, divider, arrow },
            },
        };

        _overflow[host] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = T("FormBrowse/toolStripButtonPull.Text", "Pull"),
            Icon = PullIcon(_defaultPullAction),
            LiveCaption = _pullCaption,
            ShowMenu = anchor =>
            {
                BuildPullMenu(flyout);
                flyout.ShowAt(anchor);
                return Task.CompletedTask;
            },
        };

        ApplyDefaultPullAction();
        return host;
    }

    // Fills the split button's drop-down, in upstream's order:
    //   Open pull dialog…  (Ctrl+Down)
    //   Pull - merge / Pull - rebase / Fetch / Fetch all / Fetch and prune all
    //   ---
    //   Set default Pull button action ▸ (the same five, as radio items)
    // Called immediately before ShowAt — see the note in MakePullSplitButton.
    private void BuildPullMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();

        MenuItem openDialog = new()
        {
            Header = T("FormBrowse/_pullOpenDialog.Text", "Open pull dialog…"),
            Icon = IconLoader.Image("Pull", 16),
            // Display only (the window-level HotkeyService owns the real binding).
            InputGesture = OpenPullDialogGesture,
            // Nothing wired yet → shown, but inert rather than misleading.
            IsEnabled = OpenPullDialogRequested is not null,
        };
        openDialog.Click += (_, _) => OpenPullDialogRequested?.Invoke();
        flyout.Items.Add(openDialog);
        flyout.Items.Add(new MenuSeparator());

        foreach ((GitPullAction action, string label) in PullActions())
        {
            if (IsHiddenPullAction(action))
            {
                continue;
            }

            MenuItem item = new()
            {
                Header = label,
                Icon = IconLoader.Image(PullIcon(action), 16),
            };
            GitPullAction captured = action;
            item.Click += (_, _) => RaisePull(captured);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuSeparator());

        MenuItem setDefault = new()
        {
            Header = T("FormBrowse/setDefaultPullButtonActionToolStripMenuItem.Text", "Set default Pull button action"),
        };
        foreach ((GitPullAction action, string label) in PullActions())
        {
            if (IsHiddenPullAction(action))
            {
                continue;
            }

            GitPullAction captured = action;
            MenuItem item = new()
            {
                Header = label,
                Icon = IconLoader.Image(PullIcon(action), 16),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = action == _defaultPullAction,
            };
            item.Click += (_, _) =>
            {
                if (captured == _defaultPullAction)
                {
                    return;
                }

                _defaultPullAction = captured;
                ApplyDefaultPullAction();
                DefaultPullActionChanged?.Invoke(captured);
            };
            setDefault.Items.Add(item);
        }

        flyout.Items.Add(setDefault);
    }

    /// <summary>
    ///  Upstream's <c>UpdateFetchAllVisibility</c>: with a single remote, "Fetch all"
    ///  is redundant with plain "Fetch", so it is dropped both from the drop-down and
    ///  from the "Set default Pull button action" submenu. Note upstream hides ONLY
    ///  <c>fetchAllToolStripMenuItem</c> — "Fetch and prune all" stays, because prune
    ///  is still meaningful against one remote.
    /// </summary>
    private bool IsHiddenPullAction(GitPullAction action)
        => action == GitPullAction.FetchAll
            && _state.IsValidWorkingDir
            && _state.RemoteCount is >= 0 and <= 1;

    // The five actions the split button offers, with their upstream captions.
    private static (GitPullAction Action, string Label)[] PullActions() =>
    [
        (GitPullAction.Merge, T("FormBrowse/_pullMerge.Text", "Pull - merge")),
        (GitPullAction.Rebase, T("FormBrowse/_pullRebase.Text", "Pull - rebase")),
        (GitPullAction.Fetch, T("FormBrowse/_pullFetch.Text", "Fetch")),
        (GitPullAction.FetchAll, T("FormBrowse/_pullFetchAll.Text", "Fetch all")),
        (GitPullAction.FetchPruneAll, T("FormBrowse/_pullFetchPruneAll.Text", "Fetch and prune all")),
    ];

    // Upstream has a distinct icon per pull action (the toolbar button swaps its
    // image with the default action); the port reuses the very same PNGs.
    private static string PullIcon(GitPullAction action) => action switch
    {
        GitPullAction.Rebase => "PullRebase",
        GitPullAction.Fetch => "PullFetch",
        GitPullAction.FetchAll => "PullFetchAll",
        GitPullAction.FetchPruneAll => "PullFetchPruneAll",
        _ => "PullMerge",
    };

    // Re-labels the body for the current default action: matching icon plus the
    // "Pull - merge (F8)" style tooltip of the original split button. The caption
    // itself stays "Pull" (+ the ↓n badge UpdateState maintains), because the strip
    // has no room for the full action name.
    private void ApplyDefaultPullAction()
    {
        IconLoader.Retarget(_pullIcon, PullIcon(_defaultPullAction));

        if (_pullButton is not null)
        {
            string label = PullActions().First(a => a.Action == _defaultPullAction).Label;
            string gesture = DefaultPullGesture?.ToString() ?? string.Empty;
            ToolTip.SetTip(_pullButton, gesture.Length == 0
                ? label
                : string.Format(T("{0} ({1})"), label, gesture));
        }
    }

    // Gestures shown next to a drop-down entry and appended to tooltips.
    private KeyGesture? DefaultPullGesture => GestureFor(BrowseCommand.QuickPullOrFetch);

    private KeyGesture? OpenPullDialogGesture => GestureFor(BrowseCommand.PullOrFetch);

    /// <summary>
    ///  The gesture actually in force for <paramref name="command"/>. Read from the
    ///  host's live <see cref="Hotkeys"/> service when one was assigned, so a user
    ///  override is shown rather than the shipped default — with overrides active the
    ///  <see cref="HotkeyService.Defaults"/> labels would simply lie. A command the
    ///  user cleared yields <c>null</c> and no gesture is shown at all.
    /// </summary>
    private KeyGesture? GestureFor(BrowseCommand command)
    {
        if (Hotkeys is { } service)
        {
            return service.GestureFor(command) is { } bound
                ? new KeyGesture(bound.Key, bound.Modifiers)
                : null;
        }

        return HotkeyService.Defaults.TryGetValue(command, out HotkeyGesture g)
            ? new KeyGesture(g.Key, g.Modifiers)
            : null;
    }

    // "Toggle left panel (Ctrl+Alt+C)" — upstream's UpdateTooltipWithShortcut, which
    // suffixes a button's tooltip with its shortcut.
    private string TipWithGesture(string tooltip, BrowseCommand command)
    {
        string gesture = GestureFor(command)?.ToString() ?? string.Empty;
        return gesture.Length == 0 ? tooltip : string.Format(T("{0} ({1})"), tooltip, gesture);
    }

    // Raises the explicit-action event, falling back to the legacy parameterless
    // PullRequested while no host has wired the new one, so a not-yet-updated host
    // keeps pulling instead of silently doing nothing.
    private void RaisePull(GitPullAction action)
    {
        if (PullActionRequested is not null)
        {
            PullActionRequested(Normalize(action));
            return;
        }

        PullRequested?.Invoke();
    }

    // A flat toolbar button that drops a menu (icon + caption + a small chevron),
    // used for the commit-info-position selector. Each entry is an icon name, its
    // menu text, and the action to run when chosen.
    private Button MakeMenuButton(string iconName, string label, string tooltip,
        (string Icon, string Text, Action OnClick)[] items)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        Image? icon = IconLoader.Image(iconName, 16);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
        }

        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
        });
        content.Children.Add(new TextBlock
        {
            Text = "▾", // ▾ chevron hints at the drop-down.
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 10,
        });

        MenuFlyout flyout = new();
        foreach ((string ic, string text, Action onClick) in items)
        {
            MenuItem menuItem = new() { Header = text };
            Image? mIcon = IconLoader.Image(ic, 16);
            if (mIcon is not null)
            {
                menuItem.Icon = mIcon;
            }

            menuItem.Click += (_, _) => onClick();
            flyout.Items.Add(menuItem);
        }

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Flyout = flyout,
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, tooltip);
        _overflow[button] = new OverflowEntry
        {
            Kind = OverflowKind.Menu,
            Label = label,
            Icon = iconName,
            SubItems = items,
        };
        return button;
    }

    // A split button (icon + caption + chevron) whose drop-down is populated on
    // demand from a host-supplied provider (which does its git work off the UI
    // thread). Each entry opens that path as the active repository via
    // OpenRepositoryRequested. The provider is read lazily through
    // <paramref name="provider"/> so the host can wire it after construction.
    /// <param name="bodyAction">
    ///  When set, the button becomes a true split button: this runs on the body and
    ///  only the chevron opens the drop-down (upstream's worktrees button, whose body
    ///  opens "Manage worktrees"). When null the whole button opens the drop-down,
    ///  which is what the submodules button does.
    /// </param>
    /// <param name="extraItems">
    ///  Appended after the provider's entries, behind a separator — the worktrees
    ///  drop-down's Create / Prune / Manage commands.
    /// </param>
    private Control MakeRepoLinkButton(string iconName, string label, string tooltip,
        Func<Func<Task<IReadOnlyList<RepoLink>>>?> provider, IBrush border,
        Action? bodyAction = null,
        Func<(string Icon, string Text, Action OnClick)[]>? extraItems = null,
        Func<string?>? primaryPath = null,
        bool showLabel = true,
        Action<Image?>? captureIcon = null,
        Action<Button, Button>? captureSplitButtons = null)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        Image? icon = IconLoader.Image(iconName, 16);
        captureIcon?.Invoke(icon);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
        }

        if (showLabel)
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 12,
            });
        }

        // With a body action the chevron becomes its own button (see below), so it is
        // not part of the body's content.
        if (bodyAction is null && primaryPath is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "▾",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 10,
            });
        }

        // NOTE: we deliberately do NOT assign this flyout to button.Flyout and
        // populate it lazily via the Opening event. Under Avalonia 11.3.x the
        // MenuFlyout presenter measures its content when the popup is shown, and
        // mutating flyout.Items during/after Opening does not re-measure the
        // already-visible popup — so it collapses to a thin, empty sliver.
        // Instead we handle Click ourselves: populate the flyout FIRST (awaiting
        // the off-thread provider), then ShowAt the button so the popup measures
        // with its real content already in place.
        MenuFlyout flyout = new();

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, tooltip);

        async Task ShowLinksAsync(Control anchor)
        {
            Func<Task<IReadOnlyList<RepoLink>>>? loadLinks = provider();
            (string Icon, string Text, Action OnClick)[]? trailing = extraItems?.Invoke();
            if (loadLinks is null)
            {
                await PopulateRepoLinksAsync(flyout, iconName, null, trailing);
                flyout.ShowAt(anchor);
                return;
            }

            Task<IReadOnlyList<RepoLink>> load;
            try
            {
                load = loadLinks();
            }
            catch
            {
                await PopulateRepoLinksAsync(
                    flyout,
                    iconName,
                    () => Task.FromException<IReadOnlyList<RepoLink>>(new InvalidOperationException()),
                    trailing);
                flyout.ShowAt(anchor);
                return;
            }

            if (load.IsCompleted)
            {
                await PopulateRepoLinksAsync(flyout, iconName, () => load, trailing);
                flyout.ShowAt(anchor);
                return;
            }

            // The popup itself must respond to the click immediately. Its provider is
            // already running from repository-open prefetch; a later click consumes the
            // now-cached result without starting Git again. Do not mutate this visible
            // MenuFlyout: Avalonia 11.3 does not remeasure it after ShowAt.
            PopulateRepoLinksLoading(flyout, trailing);
            flyout.ShowAt(anchor);
            try
            {
                await load;
            }
            catch
            {
                // The provider evicts failed snapshots, so the next click can retry.
            }
        }

        // Click handlers return void, so an unobserved exception here would take the
        // process down: a drop-down that cannot be listed must never do that.
        void ShowLinks(Control anchor) => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ShowLinksAsync(anchor);
            }
            catch
            {
                // PopulateRepoLinksAsync already degrades to "(unable to list)"; this
                // catches anything the flyout itself might throw.
            }
        });

        if (bodyAction is null && primaryPath is null)
        {
            button.Click += (_, _) => ShowLinks(button);
            _overflow[button] = new OverflowEntry
            {
                Kind = OverflowKind.LazyMenu,
                Label = label,
                Icon = iconName,
                ShowMenu = ShowLinksAsync,
            };
            return button;
        }

        // Split form: the body runs the primary command, a hairline-separated arrow
        // drops the list. Same two-real-Buttons-in-one-Border shape as the Pull and
        // Stash split buttons, so each half keeps its own hover feedback while the
        // overflow menu still sees a single item.
        button.Click += (_, _) =>
        {
            if (primaryPath?.Invoke() is { Length: > 0 } path)
            {
                OpenRepositoryRequested?.Invoke(path);
            }
            else if (bodyAction is not null)
            {
                bodyAction();
            }
            else
            {
                ShowLinks(button);
            }
        };

        Border divider = new()
        {
            Width = 1,
            Margin = new Thickness(0, 3),
            Background = border,
        };

        Button arrow = new()
        {
            Content = new TextBlock
            {
                Text = "▾",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 10,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        arrow.Classes.Add("toolbtn");
        ToolTip.SetTip(arrow, tooltip);
        arrow.Click += (_, _) => ShowLinks(arrow);
        captureSplitButtons?.Invoke(button, arrow);

        Border host = new()
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { button, divider, arrow },
            },
        };

        _overflow[host] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = label,
            Icon = iconName,
            ShowMenu = ShowLinksAsync,
        };

        return host;
    }

    // The worktrees drop-down's trailing commands, after the worktree list and a
    // separator — upstream's Create worktree… / Prune worktrees / Manage worktrees…
    // (toolStripWorktrees_DropDownOpening).
    private (string Icon, string Text, Action OnClick)[] WorktreeExtraItems() =>
    [
        ("WorkTree", T("TranslatedStrings/_createWorktree.Text", "Create worktree..."),
            () => CreateWorktreeRequested?.Invoke()),
        ("WorkTree", T("TranslatedStrings/_pruneWorktrees.Text", "Prune worktrees"),
            () => PruneWorktreesRequested?.Invoke()),
        ("WorkTree", T("TranslatedStrings/_manageWorktrees.Text", "Manage worktrees..."),
            () => ManageWorktreesRequested?.Invoke()),
    ];

    // Rebuilds a split-button flyout from the host provider. Shows a disabled
    // placeholder while the (off-thread) provider runs, then lists each entry;
    // never throws — a provider failure degrades to a disabled "(error)" item.
    private async Task PopulateRepoLinksAsync(MenuFlyout flyout, string fallbackIcon,
        Func<Task<IReadOnlyList<RepoLink>>>? provider,
        (string Icon, string Text, Action OnClick)[]? extraItems = null)
    {
        flyout.Items.Clear();
        if (provider is null)
        {
            flyout.Items.Add(new MenuItem { Header = T("(no repository open)"), IsEnabled = false });
            return;
        }

        flyout.Items.Add(new MenuItem { Header = T("RevisionGridControl/_strLoading.Text", "Loading…"), IsEnabled = false });

        IReadOnlyList<RepoLink> links;
        try
        {
            links = await provider();
        }
        catch
        {
            flyout.Items.Clear();
            flyout.Items.Add(new MenuItem { Header = T("(unable to list)"), IsEnabled = false });
            return;
        }

        flyout.Items.Clear();
        if (links.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = T("(none)"), IsEnabled = false });
        }

        foreach (RepoLink link in links)
        {
            MenuItem item = new()
            {
                // A string MenuItem header goes through Avalonia's access-key parser,
                // so an underscore in the data ("git_ext_mod") is escaped to survive.
                // (Captions rendered by a plain TextBlock must NOT be escaped — the
                // glyphs would show up doubled.)
                Header = link.IsDim
                    ? new TextBlock
                    {
                        // A deleted/prunable worktree is greyed out, upstream's
                        // item.ForeColor = SystemColors.GrayText.
                        Text = link.Label,
                        Foreground = Brush("App.TextDim", "#8A8A8A"),
                    }
                    : link.Label.Replace("_", "__"),
                IsEnabled = link.IsEnabled,
            };

            if (link.IsChecked)
            {
                // Upstream marks the current worktree Checked (and disables it, since
                // "switching" to where you already are is a no-op).
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = true;
            }
            else
            {
                Image? mIcon = IconLoader.Image(string.IsNullOrEmpty(link.Icon) ? fallbackIcon : link.Icon, 16);
                if (mIcon is not null)
                {
                    item.Icon = mIcon;
                }
            }

            string path = link.Path;
            item.Click += (_, _) => OpenRepositoryRequested?.Invoke(path);
            flyout.Items.Add(item);
        }

        AppendRepoLinkExtraItems(flyout, extraItems);
    }

    private void PopulateRepoLinksLoading(MenuFlyout flyout,
        (string Icon, string Text, Action OnClick)[]? extraItems)
    {
        flyout.Items.Clear();
        flyout.Items.Add(new MenuItem
        {
            Header = T("RevisionGridControl/_strLoading.Text", "Loading…"),
            IsEnabled = false,
        });
        AppendRepoLinkExtraItems(flyout, extraItems);
    }

    private void AppendRepoLinkExtraItems(MenuFlyout flyout,
        (string Icon, string Text, Action OnClick)[]? extraItems)
    {
        if (extraItems is not { Length: > 0 })
        {
            return;
        }

        flyout.Items.Add(new MenuSeparator());
        foreach ((string ic, string text, Action onClick) in extraItems)
        {
            MenuItem item = new() { Header = text, Icon = IconLoader.Image(ic, 16) };
            item.Click += (_, _) => onClick();
            flyout.Items.Add(item);
        }
    }

    // Inline branch dropdown: icon + current-branch caption + chevron. The flyout
    // is populated on demand from BranchesProvider (off the UI thread) using the
    // same populate-BEFORE-ShowAt pattern as MakeRepoLinkButton, so the popup never
    // renders empty. Choosing a branch raises BranchCheckoutRequested.
    private Button MakeBranchButton(IBrush border)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        Image? icon = IconLoader.Image("Branch", 16);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
        }

        _branchCaption = new TextBlock
        {
            Text = NoBranchCaption(),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        };
        content.Children.Add(_branchCaption);
        content.Children.Add(new TextBlock
        {
            Text = "▾",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 10,
        });

        MenuFlyout flyout = new();

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, T("TranslatedStrings/_buttonCheckoutBranch.Text", "Checkout a local branch"));
        button.Click += async (_, _) =>
        {
            await PopulateBranchesAsync(flyout, BranchesProvider);
            flyout.ShowAt(button);
        };

        // Upstream's branchSelect_MouseUp: a RIGHT click on the button skips the list
        // and opens the checkout dialog straight away. Handled on the tunnelling
        // (preview) event so the Button's own press handling cannot swallow it.
        button.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right)
            {
                e.Handled = true;
                CheckoutBranchRequested?.Invoke();
            }
        }, RoutingStrategies.Tunnel);
        _overflow[button] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = T("TranslatedStrings/_branchText.Text", "Branch"),
            Icon = "Branch",
            LiveCaption = _branchCaption,
            ShowMenu = async anchor =>
            {
                await PopulateBranchesAsync(flyout, BranchesProvider);
                flyout.ShowAt(anchor);
            },
        };
        return button;
    }

    // Inline repo-path dropdown: icon + ~-collapsed current path + chevron, the port
    // of upstream's WorkingDirectoryToolStripSplitButton. The drop-down carries, in
    // upstream's order: a live search box, the favorite repositories, the recent
    // ones, "Open repository" / "Close repository" with their gestures, and
    // "Configure this menu...". Right-clicking the button starts the open dialog and
    // Ctrl+click on an entry opens it in a new instance — both documented in the
    // tooltip, exactly as upstream words it.
    private Button MakeRepoPathButton(IBrush border)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 4,
        };

        Image? icon = IconLoader.Image("RepoOpen", 16);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(icon);
        }

        _repoPathCaption = new TextBlock
        {
            Text = T("(no repository)"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
        };
        content.Children.Add(_repoPathCaption);
        content.Children.Add(new TextBlock
        {
            Text = "▾",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 10,
        });

        MenuFlyout flyout = new();

        Button button = new()
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, WorkingDirTooltip());

        // Upstream's MouseUpHandler: the right button starts the "Open repository"
        // dialog instead of dropping the menu. Tunnelling, because the Button's own
        // press handling swallows the bubbling event.
        button.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right)
            {
                e.Handled = true;
                OpenRepoRequested?.Invoke();
            }
        }, RoutingStrategies.Tunnel);

        button.Click += async (_, _) =>
        {
            await BuildWorkingDirMenuAsync(flyout);
            flyout.ShowAt(button);
        };
        _overflow[button] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = T("Repository"),
            Icon = "RepoOpen",
            LiveCaption = _repoPathCaption,
            ShowMenu = async anchor =>
            {
                await BuildWorkingDirMenuAsync(flyout);
                flyout.ShowAt(anchor);
            },
        };
        return button;
    }

    // Upstream's four-line tooltip for the working-directory button, verbatim in
    // meaning: what the button is, what each mouse button does, and the Ctrl
    // modifier. The two gestures come from the live Hotkeys service.
    private string WorkingDirTooltip()
        => string.Join('\n',
            T("FormBrowse/_workingDirectory.ToolTipText", "Change working directory"),
            T("Left click opens the drop-down menu."),
            T("Then hold Ctrl in order to open the selected repository in a new instance."),
            T("Right click starts the \"Open repository\" dialog."));

    // Builds the working-directory drop-down in upstream's order: search box,
    // separator, favorites, recents, separator, Open / Close repository, separator,
    // "Configure this menu...".
    //
    // Everything is added BEFORE ShowAt and rebuilt on every open: Avalonia 11.3.x
    // measures a MenuFlyout's content when the popup opens and never re-measures it,
    // so anything added afterwards would collapse the popup to a thin sliver.
    // Filtering therefore only toggles IsVisible on items that already exist — which
    // is what upstream's TextChanged handler does too.
    private async Task BuildWorkingDirMenuAsync(MenuFlyout flyout)
    {
        flyout.Items.Clear();

        // Both lists are read off the UI thread; a failure degrades to an empty
        // group rather than an exception out of a click handler.
        Task<IReadOnlyList<RepoLink>> favoritesTask = LoadFavoriteReposAsync();
        Task<IReadOnlyList<RepoLink>> recentTask = LoadRecentReposAsync();
        IReadOnlyList<RepoLink> favorites = await favoritesTask;
        IReadOnlyList<RepoLink> recent = await recentTask;

        // Repo entries only: the fixed commands and the group headers are excluded
        // from filtering, like upstream's _excludeFromFilterMarker.
        List<MenuItem> filterable = [];

        TextBox filterBox = new()
        {
            Watermark = T("Search repositories..."),
            MinWidth = 240,
            Margin = new Thickness(0, 2),
            Background = Brush("App.Panel", "#252526"),
            Foreground = Brush("App.Text", "#DCDCDC"),
            BorderBrush = Brush("App.Border", "#3F3F46"),
            FontSize = 12,
        };
        filterBox.TextChanged += (_, _) =>
        {
            string text = filterBox.Text ?? string.Empty;
            foreach (MenuItem item in filterable)
            {
                item.IsVisible = text.Length == 0
                    || (item.Tag as string ?? string.Empty)
                        .Contains(text, StringComparison.CurrentCultureIgnoreCase);
            }
        };

        // StaysOpenOnClick: clicking into the box must not dismiss the menu.
        flyout.Items.Add(new MenuItem { Header = filterBox, StaysOpenOnClick = true });
        flyout.Items.Add(new MenuSeparator());

        // Favorites, grouped by category into one submenu, as upstream does
        // (RepositoryHistoryUIService.PopulateFavouriteRepositoriesMenu groups by
        // Repository.Category and gives each category its own submenu; the split
        // button then adds that single root item only when it has children —
        // WorkingDirectoryToolStripSplitButton.FillDropDown).
        //
        // The caption is deliberately the *same* key and English text as the Start
        // menu entry (MainMenu.cs): upstream builds this group by reusing
        // StartToolStripMenuItem.FavouriteRepositoriesMenuItem.Text verbatim
        // (WorkingDirectoryToolStripSplitButton.cs:131), and that text is spelled the
        // American way ("&Favorite repositories", StartToolStripMenuItem.Designer.cs:71)
        // even though upstream's identifiers are British. The dropdown used to read
        // "Favourite" here while the menu and the dashboard read "Favorite".
        if (favorites.Count > 0)
        {
            flyout.Items.Add(BuildFavoritesGroup(favorites));
            flyout.Items.Add(new MenuSeparator());
        }

        if (recent.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = T("(none)"), IsEnabled = false });
        }
        else
        {
            foreach (RepoLink link in recent)
            {
                filterable.Add(AddRepoEntry(flyout, link, "RepoOpen"));
            }
        }

        flyout.Items.Add(new MenuSeparator());

        MenuItem open = new()
        {
            Header = T("FormBrowse/openToolStripMenuItem.Text", "Open repository…"),
            Icon = IconLoader.Image("RepoOpen", 16),
            InputGesture = GestureFor(BrowseCommand.OpenRepo),
        };
        open.Click += (_, _) => OpenRepoRequested?.Invoke();
        flyout.Items.Add(open);

        // Both remaining commands are omitted outright when no host has wired them,
        // instead of being shown permanently greyed. A row that can never do anything
        // is worse than an absent one: it advertises a feature and then refuses it.
        if (CloseRepositoryRequested is not null)
        {
            MenuItem close = new()
            {
                Header = T("FormBrowse/closeToolStripMenuItem.Text", "Close (go to Dashboard)"),
                Icon = IconLoader.Image("DashboardFolderGit", 16),
                InputGesture = GestureFor(BrowseCommand.CloseRepository),
            };
            close.Click += (_, _) => CloseRepositoryRequested.Invoke();
            flyout.Items.Add(close);
        }

        // Upstream's "Configure this menu..." opens FormRecentReposSettings, which is
        // entirely about the *recent* list (shortening strategy, how many top/recent
        // entries, sorting) and touches neither favorites nor categories. The port has
        // no such settings page, so unless a host claims the event there is nothing
        // behind the row and it is not offered.
        if (ConfigureRecentReposRequested is not null)
        {
            flyout.Items.Add(new MenuSeparator());

            MenuItem configure = new()
            {
                Header = T("Configure this menu..."),
            };
            configure.Click += (_, _) => ConfigureRecentReposRequested.Invoke();
            flyout.Items.Add(configure);
        }

        // Give the search box the keyboard as soon as the popup is up, so typing
        // filters straight away instead of going to the menu's own type-ahead.
        Dispatcher.UIThread.Post(() => filterBox.Focus(), DispatcherPriority.Input);
    }

    // The "Favorite repositories" root for the working-directory drop-down: one
    // submenu per category, in upstream's order.
    //
    // Two deliberate departures from upstream, both forced by the data:
    //
    //  * Upstream groups *every* favorite, so an uncategorised one lands in a submenu
    //    whose Text is null — a blank, unlabelled parent row. Upstream can afford
    //    that because assigning a blank category un-favorites the repository, so the
    //    case only arises from legacy-migrated data. In this port the pre-category
    //    favorites.json is a plain list of paths, so *every* existing favorite is
    //    uncategorised: hiding them all behind a nameless submenu would be a
    //    regression for every current user. Uncategorised favorites therefore sit
    //    directly under the root, ahead of the category submenus — which also keeps
    //    upstream's "null category sorts first" ordering.
    //
    //  * The favorites are not registered for filtering. That matches upstream, which
    //    marks this whole subtree with _excludeFromFilterMarker and only filters the
    //    top-level recent entries; it is also the only sane behaviour once the rows
    //    live inside submenus, where toggling IsVisible would leave empty categories
    //    behind.
    private MenuItem BuildFavoritesGroup(IReadOnlyList<RepoLink> favorites)
    {
        MenuItem root = new()
        {
            Header = T("FormBrowse/tsmiFavouriteRepositories.Text", "Favorite repositories"),
            Icon = IconLoader.Image("star", 16),
        };

        List<Control> items = [];

        // Uncategorised first, keeping the stored order.
        int number = 0;
        foreach (RepoLink link in favorites)
        {
            if (string.IsNullOrWhiteSpace(link.Category))
            {
                AddRepoEntry(items, link, "RepoOpen", NumberPrefix(++number));
            }
        }

        // Then a submenu per category, categories ordered by name and the
        // repositories inside each keeping the stored order. The numbering restarts
        // inside every category, as upstream's does.
        foreach (IGrouping<string, RepoLink> group in favorites
            .Where(l => !string.IsNullOrWhiteSpace(l.Category))
            .GroupBy(l => l.Category!, StringComparer.CurrentCulture)
            .OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            MenuItem category = new()
            {
                // A category name is user data, so its underscores need escaping too.
                Header = group.Key.Replace("_", "__"),
                Icon = IconLoader.Image("star", 16),
            };

            List<Control> children = [];
            int inCategory = 0;
            foreach (RepoLink link in group)
            {
                AddRepoEntry(children, link, "RepoOpen", NumberPrefix(++inCategory));
            }

            category.ItemsSource = children;
            items.Add(category);
        }

        root.ItemsSource = items;
        return root;
    }

    // Upstream's accelerator scheme for repository rows: "&1:" … "&9:", "1&0:" for the
    // tenth, and no accelerator past that (RepositoryHistoryUIService.AddRecentRepositories).
    // "_" is Avalonia's access-key marker where WinForms uses "&".
    private static string NumberPrefix(int number) => number switch
    {
        < 10 => $"_{number}: ",
        10 => "1_0: ",
        _ => $"{number}: ",
    };

    // One repository row: caption + full path as the tooltip, Ctrl-aware activation.
    // Returns the item so the caller can register it for filtering; Tag carries the
    // text the filter matches against (label and path, as upstream matches on Text).
    private MenuItem AddRepoEntry(MenuFlyout flyout, RepoLink link, string iconName)
        => AddRepoEntry(flyout.Items, link, iconName);

    // Same row, but appended to an arbitrary item list, so a category submenu can
    // hold repositories too (the favorites group builds one list per category).
    private MenuItem AddRepoEntry(System.Collections.IList target, RepoLink link, string iconName,
        string? numberPrefix = null)
    {
        MenuItem item = new()
        {
            // A string header goes through Avalonia's access-key parser, so an
            // underscore in the data ("git_ext_mod") has to be escaped to survive.
            // The number prefix is added afterwards precisely so its own single
            // underscore survives as an access key, exactly as upstream's "&1:" is.
            Header = (numberPrefix ?? string.Empty) + link.Label.Replace("_", "__"),
            Icon = IconLoader.Image(link.Icon is { Length: > 0 } i ? i : iconName, 16),
            Tag = link.Label + " " + link.Path,
        };
        ToolTip.SetTip(item, link.Path);

        // MenuItem.Click carries no modifier state, so remember what was held down
        // when the row was pressed. Reset per item, so a keyboard activation after a
        // Ctrl+click elsewhere cannot inherit a stale modifier.
        KeyModifiers modifiers = KeyModifiers.None;
        item.AddHandler(PointerPressedEvent, (_, e) => modifiers = e.KeyModifiers,
            RoutingStrategies.Tunnel);

        string path = link.Path;
        item.Click += (_, _) =>
        {
            bool newInstance = modifiers.HasFlag(KeyModifiers.Control);
            modifiers = KeyModifiers.None;
            OpenRepoLink(path, newInstance);
        };

        target.Add(item);
        return item;
    }

    // Plain click opens the repository in place (the host's OpenRepositoryRequested);
    // Ctrl+click starts another copy of this application on that path, which
    // Program.Main already understands ("first argument that is an existing directory
    // becomes the initial repo"). If we cannot work out how we were launched, opening
    // in place is a better outcome than doing nothing.
    private void OpenRepoLink(string path, bool newInstance)
    {
        if (!newInstance)
        {
            OpenRepositoryRequested?.Invoke(path);
            return;
        }

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            OpenRepositoryRequested?.Invoke(path);
            return;
        }

        List<string> args = [];

        // "dotnet GitExtensions.Avalonia.dll <path>" when running without an apphost.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.Ordinal))
        {
            string? assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assembly))
            {
                OpenRepositoryRequested?.Invoke(path);
                return;
            }

            args.Add(assembly);
        }

        args.Add(path);
        _ = Task.Run(() => new ExternalToolService().LaunchDetached(exe, args, workingDir: path));
    }

    /// <summary>Starts a separate application instance browsing <paramref name="path"/>.</summary>
    public void OpenRepositoryInNewInstance(string path) => OpenRepoLink(path, newInstance: true);

    // Favorites for the working-directory drop-down. The host may supply its own
    // provider; otherwise the toolbar reads the same favorites.json the rest of the
    // shell uses, so the entry needs no wiring to be real.
    private Task<IReadOnlyList<RepoLink>> LoadFavoriteReposAsync()
    {
        if (FavoriteReposProvider is { } provider)
        {
            return SafeLinksAsync(provider);
        }

        return SafeLinksAsync(() => Task.Run<IReadOnlyList<RepoLink>>(() =>
            new FavoritesService().LoadEntries().Select(ToRepoLink).ToList()));
    }

    // A favorite carries its category through to the menu; the label is the same
    // tilde/basename treatment every other repo link gets.
    private static RepoLink ToRepoLink(FavoriteRepo favorite)
        => ToRepoLink(favorite.Path) with { Category = favorite.Category };

    private Task<IReadOnlyList<RepoLink>> LoadRecentReposAsync()
        => RecentReposProvider is { } provider
            ? SafeLinksAsync(provider)
            : SafeLinksAsync(() => Task.Run<IReadOnlyList<RepoLink>>(async () =>
                (await new RecentRepositoriesService().LoadAsync()).Select(ToRepoLink).ToList()));

    private static RepoLink ToRepoLink(string path)
        => new(Path.GetFileName(path) is { Length: > 0 } name ? name : path, path, "RepoOpen");

    // A drop-down that cannot list one of its groups must still show the rest.
    private static async Task<IReadOnlyList<RepoLink>> SafeLinksAsync(
        Func<Task<IReadOnlyList<RepoLink>>> provider)
    {
        try
        {
            return await provider();
        }
        catch
        {
            return Array.Empty<RepoLink>();
        }
    }

    // Rebuilds the branch flyout from the host provider using the same
    // populate-before-ShowAt discipline as PopulateRepoLinksAsync (Avalonia 11.3.x
    // does not re-measure an already-visible MenuFlyout). Marks the current branch
    // (bold) and never throws — a provider failure degrades to "(unable to list)".
    private async Task PopulateBranchesAsync(MenuFlyout flyout,
        Func<Task<IReadOnlyList<string>>>? provider)
    {
        flyout.Items.Clear();
        if (provider is null)
        {
            flyout.Items.Add(new MenuItem { Header = T("(no repository)"), IsEnabled = false });
            return;
        }

        flyout.Items.Add(new MenuItem { Header = "Loading…", IsEnabled = false });

        IReadOnlyList<string> branches;
        try
        {
            branches = await provider();
        }
        catch
        {
            flyout.Items.Clear();
            flyout.Items.Add(new MenuItem { Header = "(unable to list)", IsEnabled = false });
            return;
        }

        flyout.Items.Clear();

        // Upstream's CurrentBranchDropDownOpening leads with "Checkout branch..." and
        // a separator before the branch list.
        MenuItem checkout = new()
        {
            Header = T("FormBrowse/checkoutBranchToolStripMenuItem.Text", "Checkout branch..."),
            Icon = IconLoader.Image("BranchCheckout", 16),
            InputGesture = GestureFor(BrowseCommand.CheckoutBranch),
            // Nothing wired yet → shown, but inert rather than misleading.
            IsEnabled = CheckoutBranchRequested is not null,
        };
        checkout.Click += (_, _) => CheckoutBranchRequested?.Invoke();
        flyout.Items.Add(checkout);
        flyout.Items.Add(new MenuSeparator());

        if (branches.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        Image? currentIcon = IconLoader.Image("Branch", 16);

        // Upstream caps the list at 100 refs: "Git Extensions will hang when the drop
        // down is too large".
        foreach (string name in branches.Take(100))
        {
            bool isCurrent = string.Equals(name, _currentBranch, StringComparison.Ordinal);
            MenuItem item = new()
            {
                Header = new TextBlock
                {
                    Text = name,
                    FontWeight = isCurrent ? FontWeight.Bold : FontWeight.Normal,
                },
            };
            if (isCurrent && currentIcon is not null)
            {
                item.Icon = currentIcon;
            }

            string branch = name;
            item.Click += (_, _) => BranchCheckoutRequested?.Invoke(branch);
            flyout.Items.Add(item);
        }
    }

    private Control Separator(IBrush brush)
    {
        Border sep = new()
        {
            Width = 1,
            // Upstream's ToolStripSeparator is 6px wide in total (its Margin is 0),
            // so the rule gets 1px of ink and 2.5px of air on either side — not the
            // 13px the port used to spend on every group boundary.
            Margin = new Thickness(2.5, 3),
            Background = brush,
            Tag = OverflowPanel.SeparatorTag,
        };
        _overflow[sep] = new OverflowEntry { Kind = OverflowKind.Separator };
        return sep;
    }

    // ---- overflow ("»") ------------------------------------------------------

    private enum OverflowKind
    {
        /// <summary>Plain command: a menu item that runs the button's action.</summary>
        Command,

        /// <summary>Group rule: rendered as a menu separator.</summary>
        Separator,

        /// <summary>Static drop-down: rendered as a submenu with fixed entries.</summary>
        Menu,

        /// <summary>Provider-backed drop-down: re-shows the button's own flyout.</summary>
        LazyMenu,

        /// <summary>The revision filter box, mirrored into the menu.</summary>
        Filter,

        /// <summary>A read-only indicator, rendered as a disabled menu item.</summary>
        Text,

        /// <summary>Decoration that carries no meaning on its own (e.g. a caption).</summary>
        Skip,
    }

    /// <summary>
    ///  How one toolbar item is represented inside the overflow menu when the
    ///  strip is too narrow to show it inline.
    /// </summary>
    private sealed class OverflowEntry
    {
        public OverflowKind Kind { get; init; } = OverflowKind.Command;
        public string Label { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public Action? Invoke { get; init; }
        public (string Icon, string Text, Action OnClick)[]? SubItems { get; init; }
        public Func<Control, Task>? ShowMenu { get; init; }
        public TextBlock? TextSource { get; init; }
        public TextBox? FilterBox { get; init; }

        /// <summary>Caption TextBlock to read the live label from, when set.</summary>
        public TextBlock? LiveCaption { get; init; }
    }

    // The "»" button. Its flyout is rebuilt (populated BEFORE ShowAt — Avalonia
    // 11.3.x does not re-measure an already-visible MenuFlyout) from whatever the
    // panel had to leave out, so nothing ever becomes unreachable.
    private Button MakeOverflowButton()
    {
        Button button = new()
        {
            Content = new TextBlock
            {
                Text = "»",
                Foreground = Brush("App.Text", "#DCDCDC"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, T("More toolbar commands"));
        button.Click += (_, _) =>
        {
            BuildOverflowMenu();
            _overflowFlyout.ShowAt(button);
        };
        return button;
    }

    // Fills _overflowFlyout with one entry per item the panel could not fit,
    // in toolbar order. Called immediately before ShowAt.
    private void BuildOverflowMenu()
    {
        _overflowFlyout.Items.Clear();

        bool lastWasSeparator = true; // suppress a leading separator
        foreach (Control item in _bar.HiddenItems)
        {
            if (!_overflow.TryGetValue(item, out OverflowEntry? entry) || entry.Kind == OverflowKind.Skip)
            {
                continue;
            }

            if (entry.Kind == OverflowKind.Separator)
            {
                if (!lastWasSeparator)
                {
                    _overflowFlyout.Items.Add(new MenuSeparator());
                    lastWasSeparator = true;
                }

                continue;
            }

            object? menuItem = MakeOverflowItem(entry);
            if (menuItem is null)
            {
                continue;
            }

            _overflowFlyout.Items.Add(menuItem);
            lastWasSeparator = false;
        }

        // Drop a dangling trailing separator.
        while (_overflowFlyout.Items.Count > 0 && _overflowFlyout.Items[^1] is MenuSeparator)
        {
            _overflowFlyout.Items.RemoveAt(_overflowFlyout.Items.Count - 1);
        }

        if (_overflowFlyout.Items.Count == 0)
        {
            _overflowFlyout.Items.Add(new MenuItem { Header = T("(nothing hidden)"), IsEnabled = false });
        }
    }

    private object? MakeOverflowItem(OverflowEntry entry)
    {
        string label = entry.LiveCaption?.Text is { Length: > 0 } live ? live : entry.Label;

        switch (entry.Kind)
        {
            case OverflowKind.Text:
                return new MenuItem
                {
                    Header = entry.TextSource?.Text ?? label,
                    IsEnabled = false,
                };

            case OverflowKind.Filter:
            {
                // The real (hidden) TextBox cannot live in two visual trees, so the
                // menu hosts a mirror that writes straight back into it, and submits
                // on Enter exactly as the inline box does.
                TextBox? source = entry.FilterBox;
                TextBox mirror = new()
                {
                    Width = 200,
                    Text = source?.Text ?? string.Empty,
                    Watermark = source?.Watermark,
                    Background = Brush("App.Panel", "#252526"),
                    Foreground = Brush("App.Text", "#DCDCDC"),
                    BorderBrush = Brush("App.Border", "#3F3F46"),
                    BorderThickness = new Thickness(1),
                    FontSize = 12,
                    Padding = new Thickness(6, 2, 4, 2),
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                if (source is not null)
                {
                    mirror.TextChanged += (_, _) => source.Text = mirror.Text ?? string.Empty;
                }

                mirror.KeyDown += (_, e) =>
                {
                    if (e.Key is Key.Enter or Key.Return)
                    {
                        FilterChanged?.Invoke(mirror.Text ?? string.Empty);
                        e.Handled = true;
                        return;
                    }

                    if (e.Key == Key.Escape)
                    {
                        mirror.Text = string.Empty;
                        FilterChanged?.Invoke(string.Empty);
                        e.Handled = true;
                    }
                };

                StackPanel host = new()
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = string.Format("{0}:", T("FormBrowse/ToolStripFilters.Text", "Filter")),
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brush("App.TextDim", "#8A8A8A"),
                            FontSize = 12,
                        },
                        mirror,
                    },
                };

                // StaysOpenOnClick keeps the menu up while the user types.
                MenuItem filterItem = new() { Header = host, StaysOpenOnClick = true };
                return filterItem;
            }

            case OverflowKind.Menu:
            {
                MenuItem parent = new() { Header = label, Icon = IconLoader.Image(entry.Icon, 16) };
                foreach ((string ic, string text, Action onClick) in entry.SubItems ?? [])
                {
                    MenuItem child = new() { Header = text, Icon = IconLoader.Image(ic, 16) };
                    child.Click += (_, _) => onClick();
                    parent.Items.Add(child);
                }

                return parent;
            }

            case OverflowKind.LazyMenu:
            {
                // The entries come from an off-thread provider, so we do not build a
                // submenu here: choosing this item closes the overflow menu and then
                // re-opens the item's own (freshly populated) flyout at the "»"
                // button — the same populate-before-ShowAt discipline used inline.
                MenuItem lazy = new() { Header = label + " …", Icon = IconLoader.Image(entry.Icon, 16) };
                Func<Control, Task>? show = entry.ShowMenu;
                if (show is not null)
                {
                    lazy.Click += (_, _) => Dispatcher.UIThread.Post(async () =>
                    {
                        try
                        {
                            await show(_overflowButton);
                        }
                        catch
                        {
                            // A drop-down that cannot be listed must never break the toolbar.
                        }
                    });
                }

                return lazy;
            }

            default:
            {
                MenuItem command = new() { Header = label, Icon = IconLoader.Image(entry.Icon, 16) };
                Action? invoke = entry.Invoke;
                if (invoke is not null)
                {
                    command.Click += (_, _) => invoke();
                }

                return command;
            }
        }
    }

    /// <summary>
    ///  A single-line toolbar strip that lays its items out left to right and,
    ///  when they do not all fit, keeps as many as will fit and parks the rest
    ///  off-screen, pinning an overflow button at the right edge instead — the
    ///  behaviour of the original Windows toolbar's "»" chevron.
    ///
    ///  Items are never hidden through <c>IsVisible</c> (mutating visibility from
    ///  a measure pass re-invalidates layout); they are arranged outside the
    ///  panel's clip rectangle, which is cheap and cannot loop.
    /// </summary>
    private sealed class OverflowPanel : Panel
    {
        internal const string SeparatorTag = "toolbar-separator";

        private readonly Control _overflowButton;

        // Insertion rank per item, so an item removed by SetItemPresent can be put
        // back at its original place on the strip.
        private readonly Dictionary<Control, int> _order = new();
        private int _visibleCount;

        public OverflowPanel(Control overflowButton)
        {
            _overflowButton = overflowButton;
            ClipToBounds = true;
            Children.Add(overflowButton);
        }

        /// <summary>Gap between adjacent items, matching the old StackPanel spacing.</summary>
        public double Spacing { get; set; }

        /// <summary>True while some items are parked in the overflow menu.</summary>
        public bool IsOverflowing { get; private set; }

        /// <summary>The toolbar items, in order, excluding the overflow button.</summary>
        public IEnumerable<Control> Items
            => Children.Where(c => !ReferenceEquals(c, _overflowButton));

        /// <summary>The items the last layout pass could not fit, in order.</summary>
        public IEnumerable<Control> HiddenItems => Items.Skip(_visibleCount);

        /// <summary>Appends a toolbar item, keeping the overflow button last.</summary>
        public void AddItem(Control item)
        {
            _order[item] = _order.Count;
            Children.Insert(Children.Count - 1, item);
        }

        /// <summary>True while <paramref name="item"/> is on the strip.</summary>
        public bool Contains(Control item) => Children.Contains(item);

        /// <summary>
        ///  Takes an item off the strip. Its original position is remembered, so
        ///  <see cref="RestoreItem"/> puts it back where it belongs instead of at the
        ///  end. Removal (rather than <c>IsVisible = false</c>) is deliberate: a
        ///  collapsed-but-present child would still be measured, would still consume
        ///  overflow budget, and would still be listed in the overflow menu.
        /// </summary>
        public void RemoveItem(Control item) => Children.Remove(item);

        /// <summary>Puts a previously removed item back at its original index.</summary>
        public void RestoreItem(Control item)
        {
            if (Children.Contains(item) || !_order.TryGetValue(item, out int rank))
            {
                return;
            }

            // Insert before the first present item that was added after this one; the
            // overflow button is always last, so the fallback lands just before it.
            int at = Children.Count - 1;
            for (int i = 0; i < Children.Count - 1; i++)
            {
                if (Children[i] is Control sibling
                    && _order.TryGetValue(sibling, out int siblingRank)
                    && siblingRank > rank)
                {
                    at = i;
                    break;
                }
            }

            Children.Insert(at, item);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double height = 0;
            foreach (Control child in Children)
            {
                child.Measure(Size.Infinity);
                height = Math.Max(height, child.DesiredSize.Height);
            }

            List<Control> items = Items.ToList();
            double total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                total += items[i].DesiredSize.Width + (i > 0 ? Spacing : 0);
            }

            double available = availableSize.Width;
            if (double.IsInfinity(available) || double.IsNaN(available) || total <= available)
            {
                _visibleCount = items.Count;
                IsOverflowing = false;
                return new Size(total, height);
            }

            // Reserve room for the "»" button, then keep items from the left while
            // they fit; the remainder goes to the overflow menu.
            double budget = Math.Max(0, available - _overflowButton.DesiredSize.Width - Spacing);
            double used = 0;
            int fitting = 0;
            for (int i = 0; i < items.Count; i++)
            {
                double step = items[i].DesiredSize.Width + (i > 0 ? Spacing : 0);
                if (used + step > budget)
                {
                    break;
                }

                used += step;
                fitting++;
            }

            // Never end the visible run on a group rule.
            while (fitting > 0 && IsSeparator(items[fitting - 1]))
            {
                fitting--;
            }

            _visibleCount = fitting;
            IsOverflowing = true;
            return new Size(available, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Parked items go far off to the left; the panel clips to its bounds,
            // so they are neither drawn nor hit-testable.
            const double Parked = -10000;

            double x = 0;
            int index = 0;
            foreach (Control item in Items)
            {
                Size desired = item.DesiredSize;
                if (index < _visibleCount)
                {
                    item.Arrange(new Rect(x, Center(finalSize.Height, desired.Height), desired.Width, desired.Height));
                    x += desired.Width + Spacing;
                }
                else
                {
                    item.Arrange(new Rect(Parked, 0, desired.Width, desired.Height));
                }

                index++;
            }

            Size overflowSize = _overflowButton.DesiredSize;
            if (IsOverflowing)
            {
                double ox = Math.Max(x, finalSize.Width - overflowSize.Width);
                _overflowButton.Arrange(new Rect(
                    ox, Center(finalSize.Height, overflowSize.Height), overflowSize.Width, overflowSize.Height));
            }
            else
            {
                _overflowButton.Arrange(new Rect(Parked, 0, overflowSize.Width, overflowSize.Height));
            }

            return finalSize;
        }

        private static double Center(double outer, double inner) => Math.Max(0, (outer - inner) / 2);

        private static bool IsSeparator(Control item)
            => item.Tag as string == SeparatorTag;
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // "(no branch)" is the port's parenthesised form of the upstream noun.
    private static string NoBranchCaption()
        => string.Format("({0})", T("TranslatedStrings/_noBranch.Text", "no branch"));

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));

    // A brush's own colour at alpha 0: invisible, but the right STARTING POINT for a
    // cross-fade. Brushes.Transparent is transparent white, so fading from it to any
    // dark fill passes through half-opaque white — see the toolbtn styles.
    private static IBrush Fade(IBrush brush)
        => brush is ISolidColorBrush s
            ? new SolidColorBrush(Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B))
            : Brushes.Transparent;
}
