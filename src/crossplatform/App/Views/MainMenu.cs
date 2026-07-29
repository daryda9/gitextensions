using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;
using GitExtensions.Extensibility.Plugins;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The top main-menu bar for the shell, echoing the original
///  <c>FormBrowse</c> menu (File / Edit / View / Repository / Commands / Help).
///
///  Like <see cref="MainToolbar"/>, this control performs no git work itself:
///  each item simply raises a public event, and the host window wires those
///  events to the existing services and views. Where a sensible icon exists it
///  is loaded through <see cref="IconLoader"/>; a missing icon degrades to the
///  text label only.
///
///  <para>This is also the first view wired to <see cref="TranslationService"/>
///  (unit T1 of the translation work). Every caption goes through
///  <see cref="Item(string?, string, string?, Action, bool, BrowseCommand?)"/> with
///  an explicit XLIFF key of the form <c>"FormBrowse/&lt;designerName&gt;.Text"</c> —
///  the very id the upstream WinForms menu item carries — so the existing catalogues
///  apply verbatim. The whole menu is rebuilt (not restarted) when the language
///  changes.</para>
///
///  <para><b>State.</b> The menu owns none of it. Which entries exist is fixed at
///  <see cref="Build"/> time; which are <i>visible</i> or <i>enabled</i> comes from
///  the host through <see cref="SetRepositoryState"/> and
///  <see cref="SetSelectionState"/>, and the shortcut each entry displays comes from
///  the host's live <see cref="Hotkeys"/> map. That is the same discipline the View
///  menu already follows for its check marks: render, never decide.</para>
/// </summary>
public sealed class MainMenu : UserControl
{
    // Ceiling for a menu popup. High enough that no caption of any shipped
    // translation is clipped, low enough that a pathological entry still cannot
    // grow the popup past a normal window.
    private const double MenuPopupMaxWidth = 900d;

    private MenuItem _openRecent = new();
    private MenuItem _favorites = new();
    private MenuItem _plugins = new();
    private MenuItem _pluginSettings = new();
    private MenuItem _language = new();

    // The three top-level menus upstream hides outright when there is no valid
    // repository (FormBrowse.HideVariableMainMenuItems / RefreshSplitViewLayout,
    // FormBrowse.cs:926-929 and 987-990), plus the Dashboard menu, which exists only
    // while the dashboard is up. Kept as fields so SetRepositoryState can flip
    // IsVisible without rebuilding.
    private MenuItem _dashboard = new();
    private MenuItem _repository = new();
    private MenuItem _commands = new();

    // Every entry that can be greyed out, keyed by the *upstream designer name* of
    // the WinForms item it mirrors ("manageSubmodulesToolStripMenuItem"). Using
    // upstream's own names keeps the gating tables below diffable by eye against
    // FormBrowse.cs. Refilled by every Build().
    private readonly Dictionary<string, MenuItem> _gated = new(StringComparer.Ordinal);

    // Last values pushed in by the host window: kept so a language switch can
    // rebuild the menu without the host having to re-supply them.
    private IReadOnlyList<string> _recentRepositories = [];
    private IReadOnlyList<string> _favoriteRepositories = [];
    private IReadOnlyList<IGitPlugin> _pluginList = [];
    private IReadOnlyList<string> _languages = [TranslationService.EnglishLanguage];
    private string _currentLanguage = TranslationService.EnglishLanguage;

    // State of the revision grid's checkable "View" options, pushed in by the host
    // from RevisionGridView.ViewOptionsChanged. The menu NEVER owns this state — it
    // only renders the check marks — which is what keeps it in step with the grid's
    // own header flyouts and with the keyboard shortcuts.
    private IReadOnlyDictionary<string, bool> _viewOptions = new Dictionary<string, bool>();

    // Repository state pushed in by the host (SetRepositoryState) and grid selection
    // state (SetSelectionState). Kept so a language switch — which rebuilds the whole
    // menu — restores the visibility and the greying without the host re-supplying it.
    private bool _hasRepository = true;
    private bool _isBare;
    private bool _isDashboard;
    private int _selectedCount = 1;
    private bool _selectionIsNormal = true;

    // The checkable items built for those options, by id, so SetViewOptions can
    // re-tick them without rebuilding the menu. Refilled by every Build().
    private readonly Dictionary<string, MenuItem> _checkables = new(StringComparer.Ordinal);

    // ---- File
    public event Action? OpenRepoRequested;
    public event Action? CloneRequested;
    public event Action? InitRequested;
    public event Action<string>? OpenRecentRequested;
    public event Action? AddFavoriteRequested;
    public event Action<string>? OpenFavoriteRequested;
    public event Action? DashboardRequested;
    public event Action? ExitRequested;

    // ---- Edit
    public event Action? CopyHashRequested;
    public event Action? SettingsRequested;

    // ---- Navigate + View: the revision grid's own commands
    //
    // Both menus are generated from the grid's command ids (the upstream
    // MenuCommand.Name values) instead of one event per entry, mirroring how
    // RevisionGridMenuCommands drives the original menus: the id travels to
    // RevisionGridView.ExecuteMenuCommand, and the check marks come back through
    // SetViewOptions. That is also why there is a single event here — an item added
    // to either menu needs no new plumbing in the host window.
    public event Action<string>? GridCommandRequested;

    // ---- View
    public event Action? LightThemeRequested;
    public event Action? DarkThemeRequested;
    public event Action<string>? LanguageRequested;
    public event Action? RefreshRequested;
    public event Action? RevisionFilterRequested;
    public event Action? ResetRevisionFiltersRequested;
    public event Action? ShowReflogRequested;

    /// <summary>
    ///  Raised by the Commands menu's "Bisect…" entry; the host answers by opening the
    ///  bisect control panel (upstream <c>FormBrowse.BisectClick</c>).
    /// </summary>
    public event Action? BisectRequested;

    // ---- Dashboard (top-level, shown only while the dashboard is up)
    public event Action? DashboardRefreshRequested;

    // ---- Repository
    public event Action? FileExplorerRequested;
    public event Action? RemotesRequested;
    public event Action? SubmodulesRequested;
    public event Action? UpdateAllSubmodulesRequested;
    public event Action? SynchronizeAllSubmodulesRequested;
    public event Action? WorktreesRequested;
    public event Action? EditGitignoreRequested;
    public event Action? EditGitattributesRequested;
    public event Action? EditMailmapRequested;
    public event Action? EditInfoExcludeRequested;
    public event Action? RepoSettingsRequested;
    public event Action? SparseCheckoutRequested;

    // ---- Repository → Git maintenance (the four upstream entries)
    public event Action? CompressDatabaseRequested;
    public event Action? DeleteIndexLockRequested;
    public event Action? EditGitConfigRequested;

    /// <summary>
    ///  "Recover lost objects…". Upstream opens <c>FormVerify</c>, a dedicated
    ///  dangling-object browser this port does not have; the entry therefore opens
    ///  the port's existing <c>MaintenanceDialog</c>, whose "Verify database" button
    ///  runs the same <c>git fsck</c> and shows its output. See the class remarks.
    /// </summary>
    public event Action? GitMaintenanceRequested;

    // ---- Commands
    public event Action? FetchRequested;
    public event Action? PullRequested;
    public event Action? PushRequested;
    public event Action? CommitRequested;
    public event Action? UndoLastCommitRequested;
    public event Action? StashRequested;
    public event Action? ResetChangesRequested;
    public event Action? CleanWorkingDirectoryRequested;
    public event Action? NewBranchRequested;
    public event Action? NewTagRequested;
    public event Action? FormatPatchRequested;
    public event Action? ApplyPatchRequested;
    public event Action? ViewPatchRequested;

    // ---- Tools
    public event Action? GitBashRequested;
    public event Action? GitKRequested;
    public event Action? GitGuiRequested;
    public event Action? GitCommandLogRequested;

    // ---- Plugins
    public event Action<IGitPlugin>? PluginRunRequested;
    public event Action<IGitPlugin>? PluginSettingsRequested;

    /// <summary>
    ///  Raised just before the Commands menu drops down, so the host can refresh the
    ///  grid selection it last pushed through <see cref="SetSelectionState"/>. Wiring
    ///  it is optional: a host that already calls <see cref="SetSelectionState"/> on
    ///  every selection change needs nothing here.
    /// </summary>
    public event Action? CommandsMenuOpening;

    // ---- Help
    public event Action? AboutRequested;
    public event Action? UserManualRequested;
    public event Action? ReportIssueRequested;
    public event Action? ChangelogRequested;
    public event Action? DonateRequested;

    /// <summary>
    ///  The window's live hotkey map, used ONLY to label the entries with the
    ///  gesture actually in force. Same contract as <see cref="MainToolbar.Hotkeys"/>:
    ///  while it is null the labels fall back to <see cref="HotkeyService.Defaults"/>
    ///  — upstream's FormBrowse map, which ignores the user's <c>hotkeys.json</c>
    ///  overrides and would therefore make the menu lie — so a host that owns a
    ///  hotkey service must assign this. Assigning it rebuilds the menu, because
    ///  <see cref="Build"/> runs from the constructor, before a host can set it.
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
            Build();
        }
    }

    private HotkeyService? _hotkeys;

    public MainMenu()
    {
        Build();

        // A language switch re-labels the menu in place — no restart. The handler
        // is posted so the rebuild never runs inside the loader's continuation.
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        _currentLanguage = TranslationService.CurrentLanguage;
        Dispatcher.UIThread.Post(Build);
    }

    private void Build()
    {
        IBrush toolbar = Brush("App.Toolbar", "#333337");
        IBrush text = Brush("App.Text", "#DCDCDC");

        Background = toolbar;

        _openRecent = new MenuItem { Header = T("FormBrowse/tsmiRecentRepositories.Text", "Open recent") };
        BuildRecentRepositories();
        _favorites = new MenuItem { Header = T("FormBrowse/tsmiFavouriteRepositories.Text", "Favorite repositories") };
        BuildFavoriteRepositories();

        MenuItem start = new() { Header = T("FormBrowse/fileToolStripMenuItem.Text", "_Start") };
        start.Items.Add(Item("FormBrowse/openToolStripMenuItem.Text", "Open repository…", "RepoOpen", () => OpenRepoRequested?.Invoke(), gesture: BrowseCommand.OpenRepo));
        start.Items.Add(Item("FormBrowse/cloneToolStripMenuItem.Text", "Clone repository…", "CloneRepoGit", () => CloneRequested?.Invoke()));
        start.Items.Add(Item("FormBrowse/initNewRepositoryToolStripMenuItem.Text", "Create new repository…", "RepoCreate", () => InitRequested?.Invoke()));
        start.Items.Add(_openRecent);
        start.Items.Add(new Separator());
        start.Items.Add(_favorites);
        start.Items.Add(Item(null, "Add current to favorites", null, () => AddFavoriteRequested?.Invoke()));
        start.Items.Add(new Separator());
        // "Close (go to Dashboard)" used to sit here; upstream it is the LAST entry of
        // the Repository menu (FormBrowse.Designer.cs:790-792 declares it, :843 places
        // it), where it now is.
        start.Items.Add(Item("FormBrowse/exitToolStripMenuItem.Text", "Exit", null, () => ExitRequested?.Invoke()));

        // Navigate: the revision grid's navigation commands, in the exact order of
        // the original (RevisionGridMenuCommands.cs:91-198). "Show reflog…" used to
        // sit here; upstream it belongs to the Commands menu, where it now is.
        //
        // Two upstream entries are deliberately absent rather than dead:
        //  * "Go to last parent commit" — the port has no last-parent navigation
        //    (its parent jump always takes ParentHashes[0]).
        //  * "Go to first parent commit" — that IS the port's parent jump, so a
        //    second entry would run the same action under a different name.
        _checkables.Clear();

        MenuItem navigate = new() { Header = T("FormBrowse/navigateToolStripMenuItem.Text", "_Navigate") };
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdToggleArtificialAndHead,
            "RevisionGridMenuCommands/ToggleBetweenArtificialAndHeadCommits.Text",
            "Toggle between artificial and HEAD commits",
            "WorkingDirChanges"));
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdGoToCurrentRevision,
            "RevisionGrid/GotoCurrentRevision.Text",
            "Go to current revision"));
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdGoToCommit,
            "FormGoToCommit/$this.Text",
            "Go to commit…"));
        navigate.Items.Add(new Separator());
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdGoToChildCommit,
            "RevisionGrid/GotoChildCommit.Text",
            "Go to child commit"));
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdGoToParentCommit,
            "RevisionGrid/GotoParentCommit.Text",
            "Go to parent commit"));
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdGoToMergeBase,
            "RevisionGrid/GotoMergeBaseCommit.Text",
            "Go to common ancestor (merge base)"));
        navigate.Items.Add(new Separator());
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdNavigateBackward,
            "RevisionGrid/NavigateBackward.Text",
            "Navigate backward"));
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdNavigateForward,
            "RevisionGrid/NavigateForward.Text",
            "Navigate forward"));
        navigate.Items.Add(new Separator());
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdQuickSearchHelp,
            "RevisionGrid/QuickSearch.Text",
            "Quick search"));
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdQuickSearchPrevious,
            "RevisionGrid/PrevQuickSearch.Text",
            "Quick search previous"));
        navigate.Items.Add(GridItem(
            RevisionGridView.CmdQuickSearchNext,
            "RevisionGrid/NextQuickSearch.Text",
            "Quick search next"));

        // Port extra: the shell's "copy the selected commit's hash" action, which
        // upstream only offers from the grid's context menu.
        navigate.Items.Add(new Separator());
        MenuItem copyHash = Item(null, "Copy commit hash", "CommitSummary", () => CopyHashRequested?.Invoke());
        copyHash.InputGesture = Literal("Ctrl+C");
        navigate.Items.Add(copyHash);

        _language = new MenuItem { Header = T("AppearanceSettingsPage/gbLanguages.Text", "Language") };
        BuildLanguages();

        // View: the revision grid's display options, in the original's order and with
        // its group headers (RevisionGridMenuCommands.cs:235-494). Every entry marked
        // checkable there is checkable here, and its tick comes from the grid itself
        // (SetViewOptions), so the menu, the grid's header flyouts and the keyboard
        // shortcuts can never disagree.
        //
        // Deferred, and therefore ABSENT rather than inert (see the report):
        //  * "Show reflog references" — the walk has no --reflog mode;
        //  * "Show session checkpoints" — the port has no session refs;
        //  * the three superproject label entries — no SuperProjectInfo equivalent;
        //  * "Show build status icon/text" — no CI integration in the port;
        //  * "Show commit message body" and "Show Git notes column" — RevisionRow
        //    carries neither the body nor the note text;
        //  * "Save current view settings as default" — depends on toggle persistence,
        //    which is a separate unit (its whole group header is omitted with it).
        MenuItem view = new() { Header = T("FormBrowse/viewToolStripMenuItem.Text", "_View") };

        view.Items.Add(GroupHeader(T("TranslatedStrings/_branchesText.Text", "Branches")));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowAllBranches,
            "RevisionGrid/ShowAllBranches.Text",
            "Show all branches"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowCurrentBranchOnly,
            "RevisionGrid/ShowCurrentBranchOnly.Text",
            "Show current branch only"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowFilteredBranches,
            "RevisionGrid/ShowFilteredBranches.Text",
            "Show filtered branches"));
        view.Items.Add(new Separator());
        MenuItem advancedFilter = Item("FormBrowse/tsbtnAdvancedFilter.ToolTipText", "Advanced filter…", null, () => RevisionFilterRequested?.Invoke());
        advancedFilter.InputGesture = Literal("Ctrl+I");
        view.Items.Add(advancedFilter);
        MenuItem resetFilters = Item("FormBrowse/tsmiResetAllFilters.Text", "Reset revision filters", null, () => ResetRevisionFiltersRequested?.Invoke());
        resetFilters.InputGesture = Literal("Ctrl+Shift+I");
        view.Items.Add(resetFilters);
        view.Items.Add(new Separator());
        view.Items.Add(GridCheck(
            RevisionGridView.OptDrawNonRelativesGray,
            "RevisionGrid/drawNonrelativesGrayToolStripMenuItem.Text",
            "Draw non relatives gray"));
        view.Items.Add(GridItem(
            RevisionGridView.CmdHighlightSelectedBranch,
            "RevisionGrid/HighlightSelectedBranch.Text",
            "Highlight selected branch (until refresh)"));
        view.Items.Add(new Separator());

        view.Items.Add(GroupHeader(T("CommitInfo/_plusCommits.Text", "Commits")));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowArtificialCommits,
            "RevisionGrid/ShowArtificialCommits.Text",
            "Show artificial commits"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowStashes,
            "RevisionGrid/ShowStashes.Text",
            "Show stashes"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowGitNotes,
            "RevisionGridControl/showGitNotesToolStripMenuItem.Text",
            "Show git notes"));
        view.Items.Add(new Separator());

        view.Items.Add(GroupHeader(T(null, "Grid labels")));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowRemoteBranches,
            "RevisionGrid/ShowRemoteBranches.Text",
            "Show remote branches"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptShowTags,
            "RevisionGridControl/showTagsToolStripMenuItem.Text",
            "Show tags"));
        view.Items.Add(new Separator());

        view.Items.Add(GroupHeader(T(null, "Grid info")));
        view.Items.Add(GridCheck(
            RevisionGridView.OptAuthorDate,
            "RevisionGridControl/showAuthorDateToolStripMenuItem.Text",
            "Show author date"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptRelativeDate,
            "RevisionGridControl/showRelativeDateToolStripMenuItem.Text",
            "Show relative date"));
        view.Items.Add(new Separator());

        view.Items.Add(GroupHeader(T("RevisionGrid/ColumnsToolStripMenuItem.Text", "Columns")));
        view.Items.Add(GridCheck(
            RevisionGridView.OptGraphColumn,
            "RevisionGridControl/showRevisionGraphColumnToolStripMenuItem.Text",
            "Show revision graph column"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptAvatarColumn,
            "RevisionGridControl/showAuthorAvatarColumnToolStripMenuItem.Text",
            "Show author avatar column"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptAuthorColumn,
            "RevisionGridControl/showAuthorNameColumnToolStripMenuItem.Text",
            "Show author name column"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptDateColumn,
            "RevisionGridControl/showDateColumnToolStripMenuItem.Text",
            "Show date column"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptIdColumn,
            "RevisionGridControl/showIdColumnToolStripMenuItem.Text",
            "Show SHA-1 column"));
        view.Items.Add(new Separator());

        view.Items.Add(GroupHeader(T("RevisionGrid/SortingToolStripMenuItem.Text", "Sorting")));
        view.Items.Add(GridCheck(
            RevisionGridView.OptOrderAuthorDate,
            "RevisionGrid/AuthorDateSort.Text",
            "Sort commits by author date"));
        view.Items.Add(GridCheck(
            RevisionGridView.OptOrderTopo,
            "RevisionGrid/TopoOrder.Text",
            "Arrange commits by topo order (ancestor order)"));
        view.Items.Add(new Separator());

        // Port-specific appearance block. Upstream keeps the theme and
        // Settings.Language in FormSettings → Appearance; this port has always
        // surfaced the theme here, so the language chooser sits next to it.
        // Upstream's label is "Language (restart required)"; here it is not, so the
        // group-box caption ("&Language") is the honest key to reuse.
        view.Items.Add(GroupHeader(T("AppearanceSettingsPage/$this.Text", "Appearance")));
        view.Items.Add(Item(null, "Light theme", null, () => LightThemeRequested?.Invoke()));
        view.Items.Add(Item(null, "Dark theme", null, () => DarkThemeRequested?.Invoke()));
        view.Items.Add(_language);
        view.Items.Add(new Separator());
        view.Items.Add(Item("FormBrowse/refreshToolStripMenuItem.Text", "Refresh", "ReloadRevisions", () => RefreshRequested?.Invoke(), gesture: BrowseCommand.Refresh));

        // Repository, in the exact order of FormBrowse.Designer.cs:823-843. Three
        // corrections to what this port had: Fetch/Pull/Push are NOT here upstream
        // (they belong to Commands, :1061-1071); "Edit .git/info/exclude" is the
        // second of the edit block, not the last; "Sparse Working Copy" closes the
        // edit block instead of sitting with the maintenance entries; and
        // "Close (go to Dashboard)" is the last entry of this menu, not of Start.
        _repository = new MenuItem { Header = T("FormBrowse/repositoryToolStripMenuItem.Text", "_Repository") };
        _repository.Items.Add(Item("FormBrowse/refreshToolStripMenuItem.Text", "Refresh", "ReloadRevisions", () => RefreshRequested?.Invoke(), gesture: BrowseCommand.Refresh));
        _repository.Items.Add(Item("FormBrowse/fileExplorerToolStripMenuItem.Text", "File Explorer", "BrowseFileExplorer", () => FileExplorerRequested?.Invoke()));
        _repository.Items.Add(new Separator());
        _repository.Items.Add(Item("FormBrowse/manageRemoteRepositoriesToolStripMenuItem1.Text", "Remote repositories…", "Remotes", () => RemotesRequested?.Invoke()));
        _repository.Items.Add(new Separator());
        _repository.Items.Add(Gated("manageSubmodules", Item("FormBrowse/manageSubmodulesToolStripMenuItem.Text", "Manage submodules…", "SubmodulesManage", () => SubmodulesRequested?.Invoke())));
        _repository.Items.Add(Gated("updateAllSubmodules", Item("FormBrowse/updateAllSubmodulesToolStripMenuItem.Text", "Update all submodules", "SubmodulesUpdate", () => UpdateAllSubmodulesRequested?.Invoke())));
        _repository.Items.Add(Gated("synchronizeAllSubmodules", Item("FormBrowse/synchronizeAllSubmodulesToolStripMenuItem.Text", "Synchronize all submodules", "SubmodulesSync", () => SynchronizeAllSubmodulesRequested?.Invoke())));
        _repository.Items.Add(new Separator());
        _repository.Items.Add(Item("FormBrowse/manageWorktreeToolStripMenuItem.Text", "Manage worktrees…", "WorkTree", () => WorktreesRequested?.Invoke(), gesture: BrowseCommand.ManageWorkTrees));
        _repository.Items.Add(new Separator());
        _repository.Items.Add(Gated("editgitignore", Item("FormBrowse/editgitignoreToolStripMenuItem1.Text", "Edit .gitignore", "EditGitIgnore", () => EditGitignoreRequested?.Invoke())));
        _repository.Items.Add(Item("FormBrowse/editgitinfoexcludeToolStripMenuItem.Text", "Edit .git/info/exclude", null, () => EditInfoExcludeRequested?.Invoke()));
        _repository.Items.Add(Gated("editGitAttributes", Item("FormBrowse/editGitAttributesToolStripMenuItem.Text", "Edit .gitattributes", null, () => EditGitattributesRequested?.Invoke())));
        _repository.Items.Add(Gated("editmailmap", Item("FormBrowse/editmailmapToolStripMenuItem.Text", "Edit .mailmap", null, () => EditMailmapRequested?.Invoke())));
        _repository.Items.Add(Item("FormBrowse/menuitemSparse.Text", "Sparse Working Copy", null, () => SparseCheckoutRequested?.Invoke()));
        _repository.Items.Add(new Separator());
        _repository.Items.Add(BuildGitMaintenance());
        _repository.Items.Add(Item("FormBrowse/repoSettingsToolStripMenuItem.Text", "Repository settings…", "Settings", () => RepoSettingsRequested?.Invoke()));
        _repository.Items.Add(new Separator());
        _repository.Items.Add(Item("FormBrowse/closeToolStripMenuItem.Text", "Close (go to Dashboard)", "DashboardFolderGit", () => DashboardRequested?.Invoke(), gesture: BrowseCommand.CloseRepository));

        _commands = new MenuItem { Header = T("FormBrowse/commandsToolStripMenuItem.Text", "_Commands") };
        _commands.Items.Add(Gated("commit", Item("FormBrowse/commitToolStripMenuItem.Text", "Commit…", "CommitSummary", () => CommitRequested?.Invoke(), gesture: BrowseCommand.Commit)));
        // Same slot as the original FormBrowse Commands menu (undoLastCommitToolStripMenuItem,
        // "&Undo last commit...", image ResetFileTo): directly after Commit, followed by
        // Pull/Fetch and Push (FormBrowse.Designer.cs:1061-1071) — which this port used
        // to keep in the Repository menu.
        _commands.Items.Add(Gated("undoLastCommit", Item("FormBrowse/undoLastCommitToolStripMenuItem.Text", "Undo last commit…", "ResetFileTo", () => UndoLastCommitRequested?.Invoke())));
        // Port extra: upstream has a single "Pull/Fetch..." entry (its dialog offers
        // both), while this port also has a dialog-less fetch, bound to QuickFetch. It
        // is placed immediately before Pull rather than invented a slot of its own.
        _commands.Items.Add(Item("FormBrowse/fetchToolStripMenuItem.Text", "Fetch", "PullFetch", () => FetchRequested?.Invoke(), gesture: BrowseCommand.QuickFetch));
        _commands.Items.Add(Gated("pull", Item("FormBrowse/pullToolStripMenuItem.Text", "Pull/Fetch…", "Pull", () => PullRequested?.Invoke(), gesture: BrowseCommand.PullOrFetch)));
        _commands.Items.Add(Item("FormBrowse/pushToolStripMenuItem.Text", "Push…", "Push", () => PushRequested?.Invoke(), gesture: BrowseCommand.Push));
        _commands.Items.Add(new Separator());
        _commands.Items.Add(Gated("stash", Item("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash", "stash", () => StashRequested?.Invoke(), gesture: BrowseCommand.Stash)));
        // Same slot as the original FormBrowse Commands menu: the two destructive
        // working-directory actions sit right after Stash and before the separator
        // that starts the branch block.
        _commands.Items.Add(Gated("reset", Item("FormBrowse/resetToolStripMenuItem.Text", "Reset changes…", "ResetWorkingDirChanges", () => ResetChangesRequested?.Invoke())));
        _commands.Items.Add(Gated("cleanup", Item("FormBrowse/cleanupToolStripMenuItem.Text", "Clean working directory…", "CleanupRepo", () => CleanWorkingDirectoryRequested?.Invoke())));
        _commands.Items.Add(new Separator());
        _commands.Items.Add(Gated("branch", Item("FormBrowse/branchToolStripMenuItem.Text", "New branch…", "BranchCreate", () => NewBranchRequested?.Invoke(), gesture: BrowseCommand.CreateBranch)));
        _commands.Items.Add(Gated("tag", Item("FormBrowse/tagToolStripMenuItem.Text", "New tag…", "TagCreate", () => NewTagRequested?.Invoke(), gesture: BrowseCommand.CreateTag)));
        _commands.Items.Add(new Separator());
        // Upstream's bisectToolStripMenuItem — "B&isect...", Images.Bisect, in this
        // same slot of the Commands dropdown (FormBrowse.Designer.cs:1206-1212, added
        // at :1032) and opening FormBisect (FormBrowse.BisectClick:1805-1813). It was
        // missing here, which left the port with no menu route to a bisect at all.
        _commands.Items.Add(Gated("bisect", Item("FormBrowse/bisectToolStripMenuItem.Text", "Bisect…", "Bisect", () => BisectRequested?.Invoke())));
        _commands.Items.Add(new Separator());
        _commands.Items.Add(Item("FormBrowse/formatPatchToolStripMenuItem.Text", "Format patch…", null, () => FormatPatchRequested?.Invoke()));
        _commands.Items.Add(Gated("applyPatch", Item("FormBrowse/applyPatchToolStripMenuItem.Text", "Apply patch…", null, () => ApplyPatchRequested?.Invoke())));
        _commands.Items.Add(Item("FormBrowse/patchToolStripMenuItem.Text", "View patch file…", null, () => ViewPatchRequested?.Invoke()));
        _commands.Items.Add(new Separator());
        // toolStripMenuItemReflog belongs to the Commands menu upstream, not to
        // Navigate, where this port used to keep it.
        _commands.Items.Add(Gated("reflog", Item("FormBrowse/toolStripMenuItemReflog.Text", "Show reflog…", null, () => ShowReflogRequested?.Invoke())));

        // Upstream re-evaluates the selection-dependent entries every time the menu
        // drops down (CommandsToolStripMenuItem_DropDownOpening, FormBrowse.cs:2330).
        // Only IsEnabled is touched here — no item is added — so the popup still
        // measures the same content it was given before ShowAt.
        _commands.SubmenuOpened += (_, _) =>
        {
            CommandsMenuOpening?.Invoke();
            ApplyRepositoryState();
        };

        MenuItem tools = new() { Header = T("FormBrowse/toolsToolStripMenuItem.Text", "_Tools") };
        tools.Items.Add(Item("FormBrowse/gitBashToolStripMenuItem.Text", "Git bash", "GitForWindows", () => GitBashRequested?.Invoke(), gesture: BrowseCommand.GitBash));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("FormBrowse/kGitToolStripMenuItem.Text", "GitK", null, () => GitKRequested?.Invoke()));
        tools.Items.Add(Item("FormBrowse/gitGUIToolStripMenuItem.Text", "Git GUI", null, () => GitGuiRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("FormBrowse/gitcommandLogToolStripMenuItem.Text", "Git command log", null, () => GitCommandLogRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("FormBrowse/settingsToolStripMenuItem.Text", "Settings…", "Settings", () => SettingsRequested?.Invoke(), gesture: BrowseCommand.OpenSettings));

        // GitHub: repository-host integration is out of scope for the Linux port,
        // so this is a disabled placeholder kept for visual parity only.
        MenuItem github = new() { Header = "_GitHub" };
        github.Items.Add(new MenuItem
        {
            Header = T("FormBrowse/_noReposHostPluginLoaded.Text", "No repository host plugin loaded."),
            IsEnabled = false,
        });

        _pluginSettings = new MenuItem { Header = T("FormBrowse/pluginSettingsToolStripMenuItem.Text", "Plugin settings") };
        _plugins = new MenuItem { Header = T("FormBrowse/pluginsToolStripMenuItem.Text", "_Plugins") };
        BuildPlugins();

        MenuItem help = new() { Header = T("FormBrowse/helpToolStripMenuItem.Text", "_Help") };
        help.Items.Add(Item("FormBrowse/userManualToolStripMenuItem.Text", "User manual", null, () => UserManualRequested?.Invoke()));
        help.Items.Add(Item("FormBrowse/reportAnIssueToolStripMenuItem.Text", "Report an issue", null, () => ReportIssueRequested?.Invoke()));
        help.Items.Add(Item("FormBrowse/changelogToolStripMenuItem.Text", "Changelog", null, () => ChangelogRequested?.Invoke()));
        help.Items.Add(Item("FormBrowse/donateToolStripMenuItem.Text", "Donate", null, () => DonateRequested?.Invoke()));
        help.Items.Add(new Separator());
        help.Items.Add(Item("FormBrowse/aboutToolStripMenuItem.Text", "About", null, () => AboutRequested?.Invoke()));

        // Dashboard: a single "&Refresh" entry, exactly as upstream
        // (FormBrowse.Designer.cs:1295-1301 with :806-809). Upstream shows it only
        // while the dashboard is up and hides it as soon as a repository is browsed
        // (FormBrowse.cs:987), which is what SetRepositoryState does here.
        _dashboard = new MenuItem { Header = T("FormBrowse/dashboardToolStripMenuItem.Text", "_Dashboard") };
        _dashboard.Items.Add(Item(
            "FormBrowse/refreshDashboardToolStripMenuItem.Text",
            "Refresh",
            "ReloadRevisions",
            () => DashboardRefreshRequested?.Invoke(),
            gesture: BrowseCommand.Refresh));

        Menu menu = new()
        {
            Background = toolbar,
            Foreground = text,
            Items = { start, _dashboard, _repository, navigate, view, _commands, github, _plugins, tools, help },
        };

        // Fluent caps every flyout — a menu popup included — at FlyoutThemeMaxWidth
        // (456px), and the item template clips rather than ellipsises what does not
        // fit. Several View entries are longer than that ("Highlight selected branch
        // (until refresh)", "Arrange commits by topo order (ancestors first)"), so
        // they were being cut mid-word. WinForms menus size to their content, so the
        // popup is given the room it asks for instead. The override lives on this
        // control's resources, not on the application's, so it only widens menus and
        // leaves every other flyout at the theme's default.
        Resources["FlyoutThemeMaxWidth"] = MenuPopupMaxWidth;

        Content = menu;

        // Visibility and greying always follow the last state the host pushed in, so
        // a language rebuild cannot resurrect a menu that should be hidden.
        ApplyRepositoryState();
    }

    // Repository → Git maintenance. Upstream is a submenu of four entries
    // (FormBrowse.Designer.cs:952-999), not the single "Git maintenance…" entry this
    // port had; the three that map onto MaintenanceService are direct actions here.
    //
    // "Recover lost objects…" is the exception: upstream opens FormVerify, a browser
    // of dangling objects with per-object restore, which this port does not have. It
    // is neither omitted nor left dead — it opens the port's MaintenanceDialog, whose
    // "Verify database" button runs the very same `git fsck` and prints the list of
    // dangling objects. That is a strict subset of FormVerify (no restore), but it is
    // the honest closest thing and it is the only way this dialog stays reachable now
    // that the flat entry is gone.
    private MenuItem BuildGitMaintenance()
    {
        MenuItem maintenance = new() { Header = T("FormBrowse/gitMaintenanceToolStripMenuItem.Text", "Git maintenance") };
        if (IconLoader.Image("Maintenance", 16) is { } icon)
        {
            maintenance.Icon = icon;
        }

        maintenance.Items.Add(Item("FormBrowse/compressGitDatabaseToolStripMenuItem.Text", "Compress git database", "CompressGitDatabase", () => CompressDatabaseRequested?.Invoke()));
        maintenance.Items.Add(Item("FormBrowse/recoverLostObjectsToolStripMenuItem.Text", "Recover lost objects…", "RecoverLostObjects", () => GitMaintenanceRequested?.Invoke()));
        maintenance.Items.Add(Item("FormBrowse/deleteIndexLockToolStripMenuItem.Text", "Delete index.lock", "DeleteIndexLock", () => DeleteIndexLockRequested?.Invoke()));
        maintenance.Items.Add(Item("FormBrowse/editLocalGitConfigToolStripMenuItem.Text", "Edit .git/config", "EditGitConfig", () => EditGitConfigRequested?.Invoke()));
        return maintenance;
    }

    /// <summary>
    ///  Rebuilds the "Open recent" submenu from the given list (most-recent
    ///  first). Each entry raises <see cref="OpenRecentRequested"/> with its
    ///  path; an empty list shows a disabled "(none)" placeholder.
    /// </summary>
    public void SetRecentRepositories(IReadOnlyList<string> repos)
    {
        _recentRepositories = repos ?? [];
        BuildRecentRepositories();
    }

    /// <summary>
    ///  Rebuilds the "Favorite repositories" submenu from the given list. Each
    ///  entry raises <see cref="OpenFavoriteRequested"/> with its path; an empty
    ///  list shows a disabled "(none)" placeholder.
    /// </summary>
    public void SetFavoriteRepositories(IReadOnlyList<string> repos)
    {
        _favoriteRepositories = repos ?? [];
        BuildFavoriteRepositories();
    }

    /// <summary>
    ///  Fills the View → Language submenu with the catalogues found next to the
    ///  executable and ticks <paramref name="current"/>. Choosing an entry raises
    ///  <see cref="LanguageRequested"/>; the host loads it off the UI thread and
    ///  the menu re-labels itself when <see cref="TranslationService.LanguageChanged"/>
    ///  fires — no restart. Items are filled here, never inside <c>Opening</c>.
    /// </summary>
    public void SetLanguages(IReadOnlyList<string> languages, string current)
    {
        _languages = languages is { Count: > 0 } ? languages : [TranslationService.EnglishLanguage];
        _currentLanguage = string.IsNullOrWhiteSpace(current) ? TranslationService.EnglishLanguage : current;
        BuildLanguages();
    }

    /// <summary>
    ///  Rebuilds the "Plugins" menu from the loaded plugin list: one run entry per
    ///  plugin (raising <see cref="PluginRunRequested"/>), plus a "Plugin settings"
    ///  submenu with one entry per plugin (raising <see cref="PluginSettingsRequested"/>).
    ///  An empty list shows a disabled "(none)" placeholder. Mirrors the recent /
    ///  favorite repository builders.
    /// </summary>
    public void SetPlugins(IReadOnlyList<IGitPlugin> plugins)
    {
        _pluginList = plugins ?? [];
        BuildPlugins();
    }

    private void BuildRecentRepositories()
    {
        _openRecent.Items.Clear();

        if (_recentRepositories.Count == 0)
        {
            _openRecent.Items.Add(None());
            return;
        }

        foreach (string repo in _recentRepositories)
        {
            string path = repo;
            _openRecent.Items.Add(Item(null, path, "RepoOpen", () => OpenRecentRequested?.Invoke(path), translate: false));
        }
    }

    private void BuildFavoriteRepositories()
    {
        _favorites.Items.Clear();

        if (_favoriteRepositories.Count == 0)
        {
            _favorites.Items.Add(None());
            return;
        }

        foreach (string repo in _favoriteRepositories)
        {
            string path = repo;
            _favorites.Items.Add(Item(null, path, "RepoOpen", () => OpenFavoriteRequested?.Invoke(path), translate: false));
        }
    }

    private void BuildLanguages()
    {
        _language.Items.Clear();

        foreach (string name in _languages)
        {
            string captured = name;
            MenuItem item = new()
            {
                // Catalogue names are data, not captions: "fa_IR" must not lose its
                // underscore to the access-key parser (doubling escapes it).
                Header = captured.Replace("_", "__"),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = string.Equals(captured, _currentLanguage, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => LanguageRequested?.Invoke(captured);
            _language.Items.Add(item);
        }

        if (_languages.Count <= 1)
        {
            _language.Items.Add(new Separator());
            _language.Items.Add(new MenuItem
            {
                Header = "(no translation catalogue found next to the executable)",
                IsEnabled = false,
            });
        }
    }

    private void BuildPlugins()
    {
        _plugins.Items.Clear();
        _pluginSettings.Items.Clear();

        if (_pluginList.Count == 0)
        {
            _plugins.Items.Add(None());
            return;
        }

        foreach (IGitPlugin plugin in _pluginList)
        {
            IGitPlugin captured = plugin;
            string name = plugin.Name ?? plugin.GetType().Name;
            _plugins.Items.Add(Item(null, name, "plugin", () => PluginRunRequested?.Invoke(captured), translate: false));

            if (plugin.HasSettings)
            {
                _pluginSettings.Items.Add(Item(null, name, "Settings", () => PluginSettingsRequested?.Invoke(captured), translate: false));
            }
        }

        _plugins.Items.Add(new Separator());
        if (_pluginSettings.Items.Count > 0)
        {
            _plugins.Items.Add(_pluginSettings);
        }
        else
        {
            _plugins.Items.Add(new MenuItem
            {
                Header = T("FormBrowse/pluginSettingsToolStripMenuItem.Text", "Plugin settings"),
                IsEnabled = false,
            });
        }
    }

    /// <summary>
    ///  Applies the revision grid's current "View" option state to the checkable
    ///  entries of the View menu. Called by the host once at start-up and then on
    ///  every <c>RevisionGridView.ViewOptionsChanged</c>, so an option flipped from
    ///  the grid's own header flyouts — or from a keyboard shortcut — shows up here
    ///  as well. The snapshot is kept so a language switch (which rebuilds the whole
    ///  menu) restores the ticks without the host re-supplying them.
    /// </summary>
    public void SetViewOptions(IReadOnlyDictionary<string, bool> options)
    {
        _viewOptions = options ?? new Dictionary<string, bool>();
        ApplyViewOptions();
    }

    private void ApplyViewOptions()
    {
        foreach ((string id, MenuItem item) in _checkables)
        {
            item.IsChecked = _viewOptions.TryGetValue(id, out bool value) && value;
        }
    }

    /// <summary>
    ///  Pushes in what the shell currently has open. Mirrors upstream
    ///  <c>FormBrowse.HideVariableMainMenuItems</c> / the visibility block at
    ///  <c>FormBrowse.cs:987-990</c> and the bare-repository block at
    ///  <c>:1014-1034</c>:
    ///  <list type="bullet">
    ///   <item><description>no valid repository → Repository, Commands and Plugins
    ///     disappear entirely (they are not merely greyed);</description></item>
    ///   <item><description>the Dashboard menu exists only while the dashboard is
    ///     up;</description></item>
    ///   <item><description>a bare repository greys out everything that needs a work
    ///     tree.</description></item>
    ///  </list>
    ///  <paramref name="isBare"/> comes from
    ///  <see cref="RepositoryStateService.IsBareRepository"/>, which the host must
    ///  compute off the UI thread.
    /// </summary>
    public void SetRepositoryState(bool hasRepository, bool isBare, bool isDashboard)
    {
        _hasRepository = hasRepository;
        _isBare = hasRepository && isBare;
        _isDashboard = isDashboard;
        ApplyRepositoryState();
    }

    /// <summary>
    ///  Pushes in the revision grid's selection, for the entries upstream re-evaluates
    ///  in <c>CommandsToolStripMenuItem_DropDownOpening</c> (FormBrowse.cs:2330-2366):
    ///  creating a branch or a tag needs exactly one real commit to hang it on — or no
    ///  selection at all, in which case the action falls back to HEAD (see
    ///  <see cref="ApplyRepositoryState"/>).
    ///  <paramref name="allNonArtificial"/> is false as soon as the selection contains
    ///  a work-tree / index row.
    /// </summary>
    public void SetSelectionState(int selectedCount, bool allNonArtificial)
    {
        _selectedCount = selectedCount;
        _selectionIsNormal = allNonArtificial;
        ApplyRepositoryState();
    }

    private void ApplyRepositoryState()
    {
        _dashboard.IsVisible = _isDashboard;
        _repository.IsVisible = _hasRepository;
        _commands.IsVisible = _hasRepository;
        _plugins.IsVisible = _hasRepository;

        // FormBrowse.cs:1014-1019 — needs a work tree.
        bool live = !_isBare;
        Enable("manageSubmodules", live);
        Enable("updateAllSubmodules", live);
        Enable("synchronizeAllSubmodules", live);
        Enable("editgitignore", live);
        Enable("editGitAttributes", live);
        Enable("editmailmap", live);

        // FormBrowse.cs:1025-1034 and the "not operating on selected revision" block
        // of CommandsToolStripMenuItem_DropDownOpening (:2359-2366).
        Enable("commit", live);
        Enable("undoLastCommit", live);
        Enable("pull", live);
        Enable("stash", live);
        Enable("reset", live);
        Enable("cleanup", live);
        Enable("applyPatch", live);
        Enable("reflog", live);

        // :2338-2352 — one real (non-artificial) commit to operate on. "New branch"
        // additionally needs a work tree to check the branch out into; "New tag" does
        // not, and upstream indeed leaves tagToolStripMenuItem out of the bare block.
        bool singleNormalCommit = _selectedCount == 1 && _selectionIsNormal;

        // ...with one deliberate departure: an *empty* selection still enables both.
        // The strict count == 1 test left them dead after any refresh that dropped the
        // selection, with nothing the user could do about it but click a row. Upstream's
        // creation dialogs anchor to HEAD exactly for this case — FormCreateBranch.cs:46-49
        // replaces a zero ObjectId with Module.GetCurrentCheckout(), and the hotkey path
        // (GitUICommands.cs:1590) reaches StartCreateBranchDialog() with no revision at
        // all on purpose — so an unselected grid means "branch/tag off HEAD", not "no".
        // A selection containing an artificial (work-tree / index) row still disables
        // them, which is upstream's rule and unchanged.
        bool noSelection = _selectedCount == 0;
        Enable("branch", (singleNormalCommit || noSelection) && live);
        Enable("tag", singleNormalCommit || noSelection);

        // FormBrowse.cs:2347-2349 — bisectToolStripMenuItem shares that block's
        // singleNormalCommit && !IsBareRepository() test. An empty selection is
        // allowed here for the same reason branch/tag allow it: the panel does not
        // need a revision to open, and with two selected it offers range seeding, so
        // upstream's strict count == 1 would only make the entry dead after a refresh
        // that dropped the selection.
        Enable("bisect", (_selectionIsNormal || noSelection) && live);
    }

    private void Enable(string name, bool enabled)
    {
        if (_gated.TryGetValue(name, out MenuItem? item))
        {
            item.IsEnabled = enabled;
        }
    }

    // Registers an entry under the (shortened) name of the upstream WinForms item it
    // mirrors, so ApplyRepositoryState can grey it out later.
    private MenuItem Gated(string name, MenuItem item)
    {
        _gated[name] = item;
        return item;
    }

    /// <summary>
    ///  The gestures the revision grid handles itself, mirrored from
    ///  <c>RevisionGridView.OnListKeyDown</c>. They are hard-coded there rather than
    ///  registered with <see cref="HotkeyService"/> — the grid is not part of the
    ///  FormBrowse hotkey scope in this port yet — so these labels are read from that
    ///  same (fixed) table instead of from the service: a <c>hotkeys.json</c> override
    ///  of, say, <c>GoToParent</c> does not change what the grid does, and quoting the
    ///  service here would be the lie the toolbar was careful to avoid.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GridGestures =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RevisionGridView.CmdToggleArtificialAndHead] = "Ctrl+OemBackslash",
            [RevisionGridView.CmdGoToCurrentRevision] = "Ctrl+Shift+C",
            [RevisionGridView.CmdGoToCommit] = "Ctrl+Shift+G",
            [RevisionGridView.CmdGoToChildCommit] = "Ctrl+N",
            [RevisionGridView.CmdGoToParentCommit] = "Ctrl+P",
            [RevisionGridView.CmdGoToMergeBase] = "Ctrl+Shift+K",
            [RevisionGridView.CmdNavigateBackward] = "Alt+Left",
            [RevisionGridView.CmdNavigateForward] = "Alt+Right",
            [RevisionGridView.CmdQuickSearchPrevious] = "Alt+Up",
            [RevisionGridView.CmdQuickSearchNext] = "Alt+Down",
            [RevisionGridView.CmdHighlightSelectedBranch] = "Ctrl+Shift+B",
            [RevisionGridView.OptShowAllBranches] = "Ctrl+Shift+A",
            [RevisionGridView.OptShowCurrentBranchOnly] = "Ctrl+Shift+U",
            [RevisionGridView.OptShowFilteredBranches] = "Ctrl+Shift+T",
            [RevisionGridView.OptShowRemoteBranches] = "Ctrl+Shift+R",
            [RevisionGridView.OptShowTags] = "Ctrl+Alt+T",

            // "Quick search" has no gesture at all: it starts by simply typing.
        };

    /// <summary>
    ///  The gesture actually in force for <paramref name="command"/>. Read from the
    ///  host's live <see cref="Hotkeys"/> service when one was assigned, so a user
    ///  override is shown rather than the shipped default; a command the user cleared
    ///  yields null and the entry shows no gesture. Identical to
    ///  <c>MainToolbar.GestureFor</c>.
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

    private static KeyGesture? Literal(string? text)
        => HotkeyGesture.TryParse(text, out HotkeyGesture g) ? new KeyGesture(g.Key, g.Modifiers) : null;

    // One non-checkable entry that runs a revision-grid command.
    private MenuItem GridItem(string id, string? key, string english, string? iconName = null)
    {
        MenuItem item = Item(key, english, iconName, () => GridCommandRequested?.Invoke(id));
        item.InputGesture = Literal(GridGestures.GetValueOrDefault(id));
        return item;
    }

    // One CHECKABLE entry that runs a revision-grid option toggle. The tick is not
    // owned here: the click only sends the id, and the grid answers with a fresh
    // snapshot through SetViewOptions.
    private MenuItem GridCheck(string id, string? key, string english)
    {
        MenuItem item = new()
        {
            Header = T(key, english),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _viewOptions.TryGetValue(id, out bool value) && value,
            InputGesture = Literal(GridGestures.GetValueOrDefault(id)),
        };
        item.Click += (_, _) => GridCommandRequested?.Invoke(id);
        _checkables[id] = item;
        return item;
    }

    // A disabled, bold caption introducing a block of related entries — the
    // Avalonia counterpart of MenuCommand.CreateGroupHeader (which produces a
    // disabled ToolStripMenuItem carrying the group's name).
    private static MenuItem GroupHeader(string text)
        => new()
        {
            Header = text,
            IsEnabled = false,
            FontWeight = FontWeight.Bold,
        };

    private static MenuItem None() => new() { Header = "(none)", IsEnabled = false };

    /// <summary>
    ///  Builds one menu entry. <paramref name="key"/> is the XLIFF id
    ///  (<c>"FormBrowse/exitToolStripMenuItem.Text"</c>) when the upstream WinForms
    ///  menu has a matching item; pass null to fall back to matching by English
    ///  source text. <paramref name="translate"/> is false for data (repository
    ///  paths, plugin names), which must never be looked up.
    ///  <paramref name="gesture"/> names the <see cref="BrowseCommand"/> whose
    ///  shortcut the entry should display; the text comes from the live hotkey map
    ///  (see <see cref="GestureFor"/>), never from a hard-coded string.
    /// </summary>
    private MenuItem Item(
        string? key,
        string header,
        string? iconName,
        Action onClick,
        bool translate = true,
        BrowseCommand? gesture = null)
    {
        // Data headers (paths, plugin names) are escaped so an underscore in
        // "git_ext_mod" is shown, not swallowed as an access key.
        MenuItem item = new() { Header = translate ? T(key, header) : header.Replace("_", "__") };
        if (gesture is { } command)
        {
            item.InputGesture = GestureFor(command);
        }

        if (iconName is not null)
        {
            Image? icon = IconLoader.Image(iconName, 16);
            if (icon is not null)
            {
                item.Icon = icon;
            }
        }

        item.Click += (_, _) => onClick();
        return item;
    }

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
