using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
/// </summary>
public sealed class MainMenu : UserControl
{
    private readonly MenuItem _openRecent;
    private readonly MenuItem _favorites;
    private readonly MenuItem _plugins;
    private readonly MenuItem _pluginSettings;

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

    // ---- View
    public event Action? LightThemeRequested;
    public event Action? DarkThemeRequested;
    public event Action? RefreshRequested;
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
    public event Action? StashRequested;
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
        IBrush toolbar = Brush("App.Toolbar", "#333337");
        IBrush text = Brush("App.Text", "#DCDCDC");

        Background = toolbar;

        _openRecent = new MenuItem { Header = "Open recent" };
        SetRecentRepositories(Array.Empty<string>());
        _favorites = new MenuItem { Header = "Favorite repositories" };
        SetFavoriteRepositories(Array.Empty<string>());

        MenuItem start = new() { Header = "_Start" };
        start.Items.Add(Item("Open repository…", "RepoOpen", () => OpenRepoRequested?.Invoke()));
        start.Items.Add(Item("Clone repository…", "CloneRepoGit", () => CloneRequested?.Invoke()));
        start.Items.Add(Item("Create new repository…", "RepoCreate", () => InitRequested?.Invoke()));
        start.Items.Add(_openRecent);
        start.Items.Add(new Separator());
        start.Items.Add(_favorites);
        start.Items.Add(Item("Add current to favorites", null, () => AddFavoriteRequested?.Invoke()));
        start.Items.Add(new Separator());
        start.Items.Add(Item("Close (go to Dashboard)", null, () => DashboardRequested?.Invoke()));
        start.Items.Add(new Separator());
        start.Items.Add(Item("Exit", null, () => ExitRequested?.Invoke()));

        // Navigate: navigation-ish items moved here (Copy commit hash from Edit,
        // Show reflog from View), plus a Refresh entry.
        MenuItem navigate = new() { Header = "_Navigate" };
        navigate.Items.Add(Item("Copy commit hash", "CommitSummary", () => CopyHashRequested?.Invoke()));
        navigate.Items.Add(new Separator());
        navigate.Items.Add(Item("Show reflog…", null, () => ShowReflogRequested?.Invoke()));
        navigate.Items.Add(Item("Refresh", "ReloadRevisions", () => RefreshRequested?.Invoke()));

        MenuItem view = new() { Header = "_View" };
        view.Items.Add(Item("Light theme", null, () => LightThemeRequested?.Invoke()));
        view.Items.Add(Item("Dark theme", null, () => DarkThemeRequested?.Invoke()));
        view.Items.Add(new Separator());
        view.Items.Add(Item("Refresh", "ReloadRevisions", () => RefreshRequested?.Invoke()));

        MenuItem repository = new() { Header = "_Repository" };
        repository.Items.Add(Item("Fetch", "PullFetch", () => FetchRequested?.Invoke()));
        repository.Items.Add(Item("Pull", "Pull", () => PullRequested?.Invoke()));
        repository.Items.Add(Item("Push", "Push", () => PushRequested?.Invoke()));
        repository.Items.Add(new Separator());
        repository.Items.Add(Item("File Explorer", "BrowseFileExplorer", () => FileExplorerRequested?.Invoke()));
        repository.Items.Add(new Separator());
        repository.Items.Add(Item("Edit .gitignore", "EditGitIgnore", () => EditGitignoreRequested?.Invoke()));
        repository.Items.Add(Item("Edit .gitattributes", null, () => EditGitattributesRequested?.Invoke()));
        repository.Items.Add(Item("Edit .mailmap", null, () => EditMailmapRequested?.Invoke()));
        repository.Items.Add(Item("Edit .git/info/exclude", null, () => EditInfoExcludeRequested?.Invoke()));
        repository.Items.Add(new Separator());
        repository.Items.Add(Item("Sparse working copy…", null, () => SparseCheckoutRequested?.Invoke()));
        repository.Items.Add(Item("Git maintenance…", null, () => GitMaintenanceRequested?.Invoke()));
        repository.Items.Add(Item("Repository settings…", "Settings", () => RepoSettingsRequested?.Invoke()));

        MenuItem commands = new() { Header = "_Commands" };
        commands.Items.Add(Item("Commit…", "CommitSummary", () => CommitRequested?.Invoke()));
        commands.Items.Add(Item("Stash", "stash", () => StashRequested?.Invoke()));
        commands.Items.Add(new Separator());
        commands.Items.Add(Item("New branch…", "BranchCreate", () => NewBranchRequested?.Invoke()));
        commands.Items.Add(Item("New tag…", "TagCreate", () => NewTagRequested?.Invoke()));
        commands.Items.Add(new Separator());
        commands.Items.Add(Item("Format patch…", null, () => FormatPatchRequested?.Invoke()));
        commands.Items.Add(Item("Apply patch…", null, () => ApplyPatchRequested?.Invoke()));
        commands.Items.Add(Item("View patch file…", null, () => ViewPatchRequested?.Invoke()));

        MenuItem tools = new() { Header = "_Tools" };
        tools.Items.Add(Item("Git bash", "GitForWindows", () => GitBashRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("GitK", null, () => GitKRequested?.Invoke()));
        tools.Items.Add(Item("Git GUI", null, () => GitGuiRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("Git command log", null, () => GitCommandLogRequested?.Invoke()));
        tools.Items.Add(new Separator());
        tools.Items.Add(Item("Settings…", "Settings", () => SettingsRequested?.Invoke()));

        // GitHub: repository-host integration is out of scope for the Linux port,
        // so this is a disabled placeholder kept for visual parity only.
        MenuItem github = new() { Header = "_GitHub" };
        github.Items.Add(new MenuItem { Header = "Repository host (GitHub) — non incluso nel port Linux", IsEnabled = false });

        _pluginSettings = new MenuItem { Header = "Plugin settings" };
        _plugins = new MenuItem { Header = "_Plugins" };
        SetPlugins(Array.Empty<IGitPlugin>());

        MenuItem help = new() { Header = "_Help" };
        help.Items.Add(Item("User manual", "GitExtensionsHelp", () => UserManualRequested?.Invoke()));
        help.Items.Add(Item("Report an issue", null, () => ReportIssueRequested?.Invoke()));
        help.Items.Add(Item("Changelog", null, () => ChangelogRequested?.Invoke()));
        help.Items.Add(Item("Donate", null, () => DonateRequested?.Invoke()));
        help.Items.Add(new Separator());
        help.Items.Add(Item("About", null, () => AboutRequested?.Invoke()));

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
        _openRecent.Items.Clear();

        if (repos is null || repos.Count == 0)
        {
            _openRecent.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        foreach (string repo in repos)
        {
            string path = repo;
            _openRecent.Items.Add(Item(path, "RepoOpen", () => OpenRecentRequested?.Invoke(path)));
        }
    }

    /// <summary>
    ///  Rebuilds the "Favorite repositories" submenu from the given list. Each
    ///  entry raises <see cref="OpenFavoriteRequested"/> with its path; an empty
    ///  list shows a disabled "(none)" placeholder.
    /// </summary>
    public void SetFavoriteRepositories(IReadOnlyList<string> repos)
    {
        _favorites.Items.Clear();

        if (repos is null || repos.Count == 0)
        {
            _favorites.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        foreach (string repo in repos)
        {
            string path = repo;
            _favorites.Items.Add(Item(path, "RepoOpen", () => OpenFavoriteRequested?.Invoke(path)));
        }
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
        _plugins.Items.Clear();
        _pluginSettings.Items.Clear();

        if (plugins is null || plugins.Count == 0)
        {
            _plugins.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        foreach (IGitPlugin plugin in plugins)
        {
            IGitPlugin captured = plugin;
            string name = plugin.Name ?? plugin.GetType().Name;
            _plugins.Items.Add(Item(name, "Plugins", () => PluginRunRequested?.Invoke(captured)));

            if (plugin.HasSettings)
            {
                _pluginSettings.Items.Add(Item(name, "Settings", () => PluginSettingsRequested?.Invoke(captured)));
            }
        }

        _plugins.Items.Add(new Separator());
        if (_pluginSettings.Items.Count > 0)
        {
            _plugins.Items.Add(_pluginSettings);
        }
        else
        {
            _plugins.Items.Add(new MenuItem { Header = "Plugin settings", IsEnabled = false });
        }
    }

    private static MenuItem Item(string header, string? iconName, Action onClick)
    {
        MenuItem item = new() { Header = header };
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

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
