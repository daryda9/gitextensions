using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
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
/// </summary>
public readonly record struct RepoLink(string Label, string Path, string Icon);

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
    public event Action? StashRequested;
    public event Action? RefreshRequested;
    public event Action? NewBranchRequested;

    // View / layout controls (added to match the original FormBrowse toolbar).
    public event Action? SplitViewToggleRequested;
    public event Action<CommitInfoPosition>? CommitInfoPositionChanged;
    public event Action? FileExplorerRequested;
    public event Action? OpenTerminalRequested;

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
        IBrush hover = Brush("App.PanelAlt", "#2D2D30");
        IBrush pressed = Brush("App.Panel", "#252526");

        Background = toolbar;

        // A subtle 1px bottom rule separates the toolbar from the content below.
        BorderBrush = border;
        BorderThickness = new Thickness(0, 0, 0, 1);

        // Flat/borderless buttons with a subtle hover fill (the Fluent template
        // paints the button's chrome through its inner ContentPresenter, so we
        // style that part directly for both the resting and pointer-over states).
        // Added once, in the constructor: the styles live on the control itself and
        // survive the strip being rebuilt for a language change.
        Styles.Add(new Style(x => x.OfType<Button>().Class("toolbtn")
            .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent),
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

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        Build();

        // Replay whatever the host last told us, so badges/captions survive.
        if (_hasState)
        {
            UpdateState(_lastAhead, _lastBehind, _lastStaged, _lastUnstaged, _lastRepoPath, _lastBranch);
        }

        SetSplitView(_splitViewOn);
    });

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
            Spacing = 2,
            Margin = new Thickness(6, 3),
        };
        _bar = bar;

        bar.AddItem(MakeButton("RepoOpen", T("FormBrowse/openToolStripMenuItem.Text", "Open"),
            T("Dashboard/_openRepository.Text", "Open repository"), () => OpenRepoRequested?.Invoke()));

        // Inline repo-path + branch dropdowns near the left, echoing the original
        // FormBrowse toolbar (a repository-path selector and a current-branch
        // selector inline in the toolbar).
        bar.AddItem(Separator(border));
        bar.AddItem(MakeRepoPathButton(border));
        bar.AddItem(MakeBranchButton(border));

        bar.AddItem(Separator(border));
        bar.AddItem(MakeButton("PullFetch", T("FormBrowse/_pullFetch.Text", "Fetch"),
            T("FormBrowse/fetchToolStripMenuItem.ToolTipText", "Fetch from remote"), () => FetchRequested?.Invoke()));
        bar.AddItem(MakePullSplitButton(border));
        _pushButton = MakeButton("Push", T("FormBrowse/toolStripButtonPush.Text", "Push"),
            T("FormPush/_errorPushToRemoteCaption.Text", "Push to remote"), () => PushRequested?.Invoke(),
            out _pushCaption, out _pushIcon);
        bar.AddItem(_pushButton);
        bar.AddItem(Separator(border));
        _commitButton = MakeButton("CommitSummary", T("FormBrowse/toolStripButtonCommit.Text", "Commit"),
            T("Commit changes"), () => CommitRequested?.Invoke(),
            out _commitCaption, out _commitIcon);
        bar.AddItem(_commitButton);
        bar.AddItem(Separator(border));
        bar.AddItem(MakeButton("stash", T("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash"),
            T("FormBrowse/stashChangesToolStripMenuItem.ToolTipText", "Stash changes"), () => StashRequested?.Invoke()));
        bar.AddItem(Separator(border));
        bar.AddItem(MakeButton("ReloadRevisions", T("FormBrowse/RefreshButton.ToolTipText", "Refresh"),
            T("FormBrowse/RefreshButton.ToolTipText", "Refresh"), () => RefreshRequested?.Invoke()));
        bar.AddItem(Separator(border));
        // Upstream's wording for the same command is "Create branch"; the port's
        // shorter caption keeps the strip narrow but reuses that catalogue entry.
        bar.AddItem(MakeButton("BranchCreate", T("TranslatedStrings/_buttonCreateBranch.Text", "New branch"),
            T("FormCommit/createBranchToolStripButton.ToolTipText", "Create a new branch"), () => NewBranchRequested?.Invoke()));

        // ---- submodules / worktrees split buttons --------------------------------
        bar.AddItem(Separator(border));
        bar.AddItem(MakeRepoLinkButton("SubmodulesManage", T("TranslatedStrings/_submodulesText.Text", "Submodules"),
            T("Open a submodule (or the parent super-project) as the active repository"),
            () => SubmodulesProvider, border));
        bar.AddItem(MakeRepoLinkButton("WorkTree", T("TranslatedStrings/_worktreesText.Text", "Worktrees"),
            T("Open a worktree as the active repository"),
            () => WorktreesProvider, border));

        // ---- view / layout group -------------------------------------------------
        bar.AddItem(Separator(border));
        // Split view is a TOGGLE: the caption carries a check mark while it is on,
        // which also labels the entry the overflow menu builds from LiveCaption.
        _splitButton = MakeButton("LayoutFooter", T("Split view"),
            T("Show the commit detail and the diff side by side in the Commit tab"),
            () => SplitViewToggleRequested?.Invoke(),
            out _splitCaption, out _);
        bar.AddItem(_splitButton);
        bar.AddItem(MakeMenuButton("LayoutSidebarLeft", T("Commit info"),
            T("FormBrowse/menuCommitInfoPosition.ToolTipText", "Commit-info position"), new[]
        {
            ("LayoutFooter", T("FormBrowse/commitInfoBelowMenuItem.Text", "Below graph"), (Action)(() => CommitInfoPositionChanged?.Invoke(CommitInfoPosition.BelowGraph))),
            ("LayoutSidebarTopLeft", T("FormBrowse/commitInfoLeftwardMenuItem.Text", "Left of graph"), (Action)(() => CommitInfoPositionChanged?.Invoke(CommitInfoPosition.LeftOfGraph))),
            ("LayoutSidebarTopRight", T("FormBrowse/commitInfoRightwardMenuItem.Text", "Right of graph"), (Action)(() => CommitInfoPositionChanged?.Invoke(CommitInfoPosition.RightOfGraph))),
        }));

        // ---- external tools group ------------------------------------------------
        bar.AddItem(Separator(border));
        bar.AddItem(MakeButton("BrowseFileExplorer", T("FormBrowse/toolStripFileExplorer.ToolTipText", "File Explorer"),
            T("Open the repository in the file manager"),
            () => FileExplorerRequested?.Invoke()));
        bar.AddItem(MakeButton("Console", T("Terminal"), T("Open a terminal in the repository directory"),
            () => OpenTerminalRequested?.Invoke()));

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
            Watermark = T("author / message / hash"),
            Background = Brush("App.Panel", "#252526"),
            Foreground = Brush("App.Text", "#DCDCDC"),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(filterBox, T("Filter the revision grid (author / message / hash)"));
        filterBox.TextChanged += (_, _) => FilterChanged?.Invoke(filterBox.Text ?? string.Empty);
        bar.AddItem(filterBox);
        _overflow[filterBox] = new OverflowEntry
        {
            Kind = OverflowKind.Filter,
            Label = T("FormBrowse/ToolStripFilters.Text", "Filter"),
            Icon = "ViewFilter",
            FilterBox = filterBox,
        };

        Content = bar;
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
    {
        // Remembered so a language rebuild can replay it onto the fresh captions.
        _hasState = true;
        _lastAhead = ahead;
        _lastBehind = behind;
        _lastStaged = staged;
        _lastUnstaged = unstaged;
        _lastRepoPath = repoPath ?? string.Empty;
        _lastBranch = branch ?? string.Empty;

        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#8A8A8A");
        IBrush accent = Brush("App.Accent", "#007ACC");
        IBrush green = Brush("App.GraphGreen", "#3FB950");
        IBrush orange = new SolidColorBrush(Color.Parse("#E6A700"));

        // Push: light up with an "ahead" badge when there are commits to push.
        if (_pushCaption is not null)
        {
            bool lit = ahead > 0;
            _pushCaption.Text = lit
                ? string.Format(T("{0} ↑{1}"), T("FormBrowse/toolStripButtonPush.Text", "Push"), ahead)
                : T("FormBrowse/toolStripButtonPush.Text", "Push");
            _pushCaption.Foreground = lit ? accent : text;
            if (_pushIcon is not null)
            {
                _pushIcon.Opacity = lit ? 1.0 : 0.85;
            }
        }

        // Pull: light up with a "behind" badge when there are commits to pull.
        if (_pullCaption is not null)
        {
            bool lit = behind > 0;
            _pullCaption.Text = lit
                ? string.Format(T("{0} ↓{1}"), T("FormBrowse/toolStripButtonPull.Text", "Pull"), behind)
                : T("FormBrowse/toolStripButtonPull.Text", "Pull");
            _pullCaption.Foreground = lit ? accent : text;
            if (_pullIcon is not null)
            {
                _pullIcon.Opacity = lit ? 1.0 : 0.85;
            }
        }

        // Commit: colour by working-directory state and show a change count.
        if (_commitCaption is not null)
        {
            int changes = staged + unstaged;
            IBrush commitColour = staged > 0 ? green : unstaged > 0 ? orange : dim;
            _commitCaption.Text = changes > 0
                ? string.Format(T("{0} ({1})"), T("FormBrowse/toolStripButtonCommit.Text", "Commit"), changes)
                : T("FormBrowse/toolStripButtonCommit.Text", "Commit");
            _commitCaption.Foreground = commitColour;
            if (_commitIcon is not null)
            {
                _commitIcon.Opacity = changes > 0 ? 1.0 : 0.6;
            }
        }

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
        if (_splitButton is not null)
        {
            ToolTip.SetTip(_splitButton, on
                ? T("Split view on: commit detail and diff side by side in the Commit tab")
                : T("Show the commit detail and the diff side by side in the Commit tab"));
        }
    }

    private Button MakeButton(string iconName, string label, string tooltip, Action onClick)
        => MakeButton(iconName, label, tooltip, onClick, out _, out _);

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
            Padding = new Thickness(8, 4),
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
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        body.Classes.Add("toolbtn");
        body.Click += (_, _) => RaisePull(_defaultPullAction);
        _pullButton = body;

        Border divider = new()
        {
            Width = 1,
            Margin = new Thickness(0, 4),
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
            Padding = new Thickness(4, 4),
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
        if (_pullIcon is not null)
        {
            _pullIcon.Source = IconLoader.Load(PullIcon(_defaultPullAction));
        }

        if (_pullButton is not null)
        {
            string label = PullActions().First(a => a.Action == _defaultPullAction).Label;
            string gesture = DefaultPullGesture?.ToString() ?? string.Empty;
            ToolTip.SetTip(_pullButton, gesture.Length == 0
                ? label
                : string.Format(T("{0} ({1})"), label, gesture));
        }
    }

    // Gestures shown next to the drop-down entry and in the body's tooltip. They
    // are read from the hotkey DEFAULTS, which are upstream's own FormBrowse map:
    // F8 = pull with the default action, Ctrl+Down = open the pull dialog. A user
    // override of either binding is not reflected here (the toolbar has no
    // HotkeyService instance) — a display-only limitation.
    private static KeyGesture? DefaultPullGesture => GestureFor(BrowseCommand.QuickPullOrFetch);

    private static KeyGesture? OpenPullDialogGesture => GestureFor(BrowseCommand.PullOrFetch);

    private static KeyGesture? GestureFor(BrowseCommand command)
        => HotkeyService.Defaults.TryGetValue(command, out HotkeyGesture g)
            ? new KeyGesture(g.Key, g.Modifiers)
            : null;

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
            Padding = new Thickness(8, 4),
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
    private Button MakeRepoLinkButton(string iconName, string label, string tooltip,
        Func<Func<Task<IReadOnlyList<RepoLink>>>?> provider, IBrush border)
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
            Text = "▾",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 10,
        });

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
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, tooltip);
        button.Click += async (_, _) =>
        {
            await PopulateRepoLinksAsync(flyout, iconName, provider());
            flyout.ShowAt(button);
        };
        _overflow[button] = new OverflowEntry
        {
            Kind = OverflowKind.LazyMenu,
            Label = label,
            Icon = iconName,
            ShowMenu = async anchor =>
            {
                await PopulateRepoLinksAsync(flyout, iconName, provider());
                flyout.ShowAt(anchor);
            },
        };
        return button;
    }

    // Rebuilds a split-button flyout from the host provider. Shows a disabled
    // placeholder while the (off-thread) provider runs, then lists each entry;
    // never throws — a provider failure degrades to a disabled "(error)" item.
    private async Task PopulateRepoLinksAsync(MenuFlyout flyout, string fallbackIcon,
        Func<Task<IReadOnlyList<RepoLink>>>? provider)
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
            return;
        }

        foreach (RepoLink link in links)
        {
            // A string MenuItem header goes through Avalonia's access-key parser,
            // so an underscore in the data ("git_ext_mod") is escaped to survive.
            // (Captions rendered by a plain TextBlock must NOT be escaped — the
            // glyphs would show up doubled.)
            MenuItem item = new() { Header = link.Label.Replace("_", "__") };
            Image? mIcon = IconLoader.Image(string.IsNullOrEmpty(link.Icon) ? fallbackIcon : link.Icon, 16);
            if (mIcon is not null)
            {
                item.Icon = mIcon;
            }

            string path = link.Path;
            item.Click += (_, _) => OpenRepositoryRequested?.Invoke(path);
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
            Padding = new Thickness(8, 4),
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

    // Inline repo-path dropdown: icon + ~-collapsed current path + chevron. When a
    // RecentReposProvider is wired, the flyout lists recent repositories (choosing
    // one raises OpenRepositoryRequested). When no provider is wired the button
    // falls back to opening the repository picker via OpenRepoRequested on click.
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
            Padding = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("toolbtn");
        ToolTip.SetTip(button, T("FormBrowse/tsmiRecentRepositories.Text", "Open a recent repository"));
        button.Click += async (_, _) =>
        {
            if (RecentReposProvider is null)
            {
                // Fallback: no recent-repos source wired — open the picker instead.
                OpenRepoRequested?.Invoke();
                return;
            }

            await PopulateRepoLinksAsync(flyout, "RepoOpen", RecentReposProvider);
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
                if (RecentReposProvider is null)
                {
                    OpenRepoRequested?.Invoke();
                    return;
                }

                await PopulateRepoLinksAsync(flyout, "RepoOpen", RecentReposProvider);
                flyout.ShowAt(anchor);
            },
        };
        return button;
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
        if (branches.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        Image? currentIcon = IconLoader.Image("Branch", 16);
        foreach (string name in branches)
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
            // Extra horizontal margin gives each button group some breathing room.
            Margin = new Thickness(6, 4),
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
                // menu hosts a mirror that writes straight back into it — which in
                // turn raises FilterChanged exactly as inline typing does.
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
        public void AddItem(Control item) => Children.Insert(Children.Count - 1, item);

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
}
