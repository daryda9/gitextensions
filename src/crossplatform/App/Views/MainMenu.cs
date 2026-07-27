using Avalonia;
using Avalonia.Controls;
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
///  <see cref="Item(string?, string, string?, Action, bool)"/> with an explicit
///  XLIFF key of the form <c>"FormBrowse/&lt;designerName&gt;.Text"</c> — the very
///  id the upstream WinForms menu item carries — so the existing catalogues apply
///  verbatim. The whole menu is rebuilt (not restarted) when the language
///  changes.</para>
/// </summary>
public sealed class MainMenu : UserControl
{
    private MenuItem _openRecent = new();
    private MenuItem _favorites = new();
    private MenuItem _plugins = new();
    private MenuItem _pluginSettings = new();
    private MenuItem _language = new();

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

    // ---- Repository
    public event Action? FetchRequested;
    public event Action? PullRequested;
    public event Action? PushRequested;
    public event Action? FileExplorerRequested;
    public event Action? EditGitignoreRequested;
    public event Action? EditGitattributesRequested;
    public event Action? EditMailmapRequested;
    public event Action? EditInfoExcludeRequested;
    public event Action? RepoSettingsRequested;
    public event Action? GitMaintenanceRequested;
    public event Action? SparseCheckoutRequested;

    // ---- Commands
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

    // ---- Help
    public event Action? AboutRequested;
    public event Action? UserManualRequested;
    public event Action? ReportIssueRequested;
    public event Action? ChangelogRequested;
    public event Action? DonateRequested;

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
        start.Items.Add(Item("FormBrowse/openToolStripMenuItem.Text", "Open repository…", "RepoOpen", () => OpenRepoRequested?.Invoke()));
        start.Items.Add(Item("FormBrowse/cloneToolStripMenuItem.Text", "Clone repository…", "CloneRepoGit", () => CloneRequested?.Invoke()));
        start.Items.Add(Item("FormBrowse/initNewRepositoryToolStripMenuItem.Text", "Create new repository…", "RepoCreate", () => InitRequested?.Invoke()));
        start.Items.Add(_openRecent);
        start.Items.Add(new Separator());
        start.Items.Add(_favorites);
        start.Items.Add(Item(null, "Add current to favorites", null, () => AddFavoriteRequested?.Invoke()));
        start.Items.Add(new Separator());
        start.Items.Add(Item("FormBrowse/closeToolStripMenuItem.Text", "Close (go to Dashboard)", null, () => DashboardRequested?.Invoke()));
        start.Items.Add(new Separator());
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
        navigate.Items.Add(Item(null, "Copy commit hash", "CommitSummary", () => CopyHashRequested?.Invoke()));

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
        view.Items.Add(Item("FormBrowse/tsbtnAdvancedFilter.ToolTipText", "Advanced filter…", null, () => RevisionFilterRequested?.Invoke()));
        view.Items.Add(Item("FormBrowse/tsmiResetAllFilters.Text", "Reset revision filters", null, () => ResetRevisionFiltersRequested?.Invoke()));
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
        view.Items.Add(Item("FormBrowse/refreshToolStripMenuItem.Text", "Refresh", "ReloadRevisions", () => RefreshRequested?.Invoke()));

        MenuItem repository = new() { Header = T("FormBrowse/repositoryToolStripMenuItem.Text", "_Repository") };
        repository.Items.Add(Item("FormBrowse/fetchToolStripMenuItem.Text", "Fetch", "PullFetch", () => FetchRequested?.Invoke()));
        repository.Items.Add(Item("FormBrowse/toolStripButtonPull.Text", "Pull", "Pull", () => PullRequested?.Invoke()));
        repository.Items.Add(Item("FormBrowse/toolStripButtonPush.Text", "Push", "Push", () => PushRequested?.Invoke()));
        repository.Items.Add(new Separator());
        repository.Items.Add(Item("FormBrowse/fileExplorerToolStripMenuItem.Text", "File Explorer", "BrowseFileExplorer", () => FileExplorerRequested?.Invoke()));
        repository.Items.Add(new Separator());
        repository.Items.Add(Item("FormBrowse/editgitignoreToolStripMenuItem1.Text", "Edit .gitignore", "EditGitIgnore", () => EditGitignoreRequested?.Invoke()));
        repository.Items.Add(Item("FormBrowse/editGitAttributesToolStripMenuItem.Text", "Edit .gitattributes", null, () => EditGitattributesRequested?.Invoke()));
        repository.Items.Add(Item("FormBrowse/editmailmapToolStripMenuItem.Text", "Edit .mailmap", null, () => EditMailmapRequested?.Invoke()));
        repository.Items.Add(Item("FormBrowse/editgitinfoexcludeToolStripMenuItem.Text", "Edit .git/info/exclude", null, () => EditInfoExcludeRequested?.Invoke()));
        repository.Items.Add(new Separator());
        repository.Items.Add(Item("FormBrowse/menuitemSparse.Text", "Sparse working copy…", null, () => SparseCheckoutRequested?.Invoke()));
        repository.Items.Add(Item("FormBrowse/gitMaintenanceToolStripMenuItem.Text", "Git maintenance…", null, () => GitMaintenanceRequested?.Invoke()));
        repository.Items.Add(Item("FormBrowse/repoSettingsToolStripMenuItem.Text", "Repository settings…", "Settings", () => RepoSettingsRequested?.Invoke()));

        MenuItem commands = new() { Header = T("FormBrowse/commandsToolStripMenuItem.Text", "_Commands") };
        commands.Items.Add(Item("FormBrowse/commitToolStripMenuItem.Text", "Commit…", "CommitSummary", () => CommitRequested?.Invoke()));
        // Same slot as the original FormBrowse Commands menu (undoLastCommitToolStripMenuItem,
        // "&Undo last commit...", image ResetFileTo): directly after Commit. Pull/Push, which
        // follow it there, live in the toolbar/Repository menu in this port.
        commands.Items.Add(Item("FormBrowse/undoLastCommitToolStripMenuItem.Text", "Undo last commit…", "ResetFileTo", () => UndoLastCommitRequested?.Invoke()));
        commands.Items.Add(Item("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash", "stash", () => StashRequested?.Invoke()));
        // Same slot as the original FormBrowse Commands menu: the two destructive
        // working-directory actions sit right after Stash and before the separator
        // that starts the branch block.
        commands.Items.Add(Item("FormBrowse/resetToolStripMenuItem.Text", "Reset changes…", "ResetWorkingDirChanges", () => ResetChangesRequested?.Invoke()));
        commands.Items.Add(Item("FormBrowse/cleanupToolStripMenuItem.Text", "Clean working directory…", "CleanupRepo", () => CleanWorkingDirectoryRequested?.Invoke()));
        commands.Items.Add(new Separator());
        commands.Items.Add(Item("FormBrowse/branchToolStripMenuItem.Text", "New branch…", "BranchCreate", () => NewBranchRequested?.Invoke()));
        commands.Items.Add(Item("FormBrowse/tagToolStripMenuItem.Text", "New tag…", "TagCreate", () => NewTagRequested?.Invoke()));
        commands.Items.Add(new Separator());
        commands.Items.Add(Item("FormBrowse/formatPatchToolStripMenuItem.Text", "Format patch…", null, () => FormatPatchRequested?.Invoke()));
        commands.Items.Add(Item("FormBrowse/applyPatchToolStripMenuItem.Text", "Apply patch…", null, () => ApplyPatchRequested?.Invoke()));
        commands.Items.Add(Item("FormBrowse/patchToolStripMenuItem.Text", "View patch file…", null, () => ViewPatchRequested?.Invoke()));
        commands.Items.Add(new Separator());
        // toolStripMenuItemReflog belongs to the Commands menu upstream, not to
        // Navigate, where this port used to keep it.
        commands.Items.Add(Item("FormBrowse/toolStripMenuItemReflog.Text", "Show reflog…", null, () => ShowReflogRequested?.Invoke()));

        MenuItem tools = new() { Header = T("FormBrowse/toolsToolStripMenuItem.Text", "_Tools") };
        tools.Items.Add(Item("FormBrowse/gitBashToolStripMenuItem.Text", "Git bash", "GitForWindows", () => GitBashRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("FormBrowse/kGitToolStripMenuItem.Text", "GitK", null, () => GitKRequested?.Invoke()));
        tools.Items.Add(Item("FormBrowse/gitGUIToolStripMenuItem.Text", "Git GUI", null, () => GitGuiRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("FormBrowse/gitcommandLogToolStripMenuItem.Text", "Git command log", null, () => GitCommandLogRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("FormBrowse/settingsToolStripMenuItem.Text", "Settings…", "Settings", () => SettingsRequested?.Invoke()));

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
        help.Items.Add(Item("FormBrowse/userManualToolStripMenuItem.Text", "User manual", "GitExtensionsHelp", () => UserManualRequested?.Invoke()));
        help.Items.Add(Item("FormBrowse/reportAnIssueToolStripMenuItem.Text", "Report an issue", null, () => ReportIssueRequested?.Invoke()));
        help.Items.Add(Item("FormBrowse/changelogToolStripMenuItem.Text", "Changelog", null, () => ChangelogRequested?.Invoke()));
        help.Items.Add(Item("FormBrowse/donateToolStripMenuItem.Text", "Donate", null, () => DonateRequested?.Invoke()));
        help.Items.Add(new Separator());
        help.Items.Add(Item("FormBrowse/aboutToolStripMenuItem.Text", "About", null, () => AboutRequested?.Invoke()));

        Menu menu = new()
        {
            Background = toolbar,
            Foreground = text,
            Items = { start, repository, navigate, view, commands, github, _plugins, tools, help },
        };

        Content = menu;
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
            _plugins.Items.Add(Item(null, name, "Plugins", () => PluginRunRequested?.Invoke(captured), translate: false));

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

    // One non-checkable entry that runs a revision-grid command.
    private MenuItem GridItem(string id, string? key, string english, string? iconName = null)
        => Item(key, english, iconName, () => GridCommandRequested?.Invoke(id));

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
    /// </summary>
    private static MenuItem Item(string? key, string header, string? iconName, Action onClick, bool translate = true)
    {
        // Data headers (paths, plugin names) are escaped so an underscore in
        // "git_ext_mod" is shown, not swallowed as an access key.
        MenuItem item = new() { Header = translate ? T(key, header) : header.Replace("_", "__") };
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
