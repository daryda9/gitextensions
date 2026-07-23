using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitExtensions.Avalonia.Theming;

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

    // ---- File
    public event Action? OpenRepoRequested;
    public event Action<string>? OpenRecentRequested;
    public event Action? ExitRequested;

    // ---- Edit
    public event Action? CopyHashRequested;

    // ---- View
    public event Action? LightThemeRequested;
    public event Action? DarkThemeRequested;
    public event Action? RefreshRequested;

    // ---- Repository
    public event Action? FetchRequested;
    public event Action? PullRequested;
    public event Action? PushRequested;

    // ---- Commands
    public event Action? CommitRequested;
    public event Action? StashRequested;
    public event Action? NewBranchRequested;
    public event Action? NewTagRequested;

    // ---- Help
    public event Action? AboutRequested;

    public MainMenu()
    {
        IBrush toolbar = Brush("App.Toolbar", "#333337");
        IBrush text = Brush("App.Text", "#DCDCDC");

        Background = toolbar;

        _openRecent = new MenuItem { Header = "Open recent" };
        SetRecentRepositories(Array.Empty<string>());

        MenuItem file = new() { Header = "_File" };
        file.Items.Add(Item("Open repository…", "RepoOpen", () => OpenRepoRequested?.Invoke()));
        file.Items.Add(_openRecent);
        file.Items.Add(new Separator());
        file.Items.Add(Item("Exit", null, () => ExitRequested?.Invoke()));

        MenuItem edit = new() { Header = "_Edit" };
        edit.Items.Add(Item("Copy commit hash", "CommitSummary", () => CopyHashRequested?.Invoke()));

        MenuItem view = new() { Header = "_View" };
        view.Items.Add(Item("Light theme", null, () => LightThemeRequested?.Invoke()));
        view.Items.Add(Item("Dark theme", null, () => DarkThemeRequested?.Invoke()));
        view.Items.Add(new Separator());
        view.Items.Add(Item("Refresh", "ReloadRevisions", () => RefreshRequested?.Invoke()));

        MenuItem repository = new() { Header = "_Repository" };
        repository.Items.Add(Item("Fetch", "PullFetch", () => FetchRequested?.Invoke()));
        repository.Items.Add(Item("Pull", "Pull", () => PullRequested?.Invoke()));
        repository.Items.Add(Item("Push", "Push", () => PushRequested?.Invoke()));

        MenuItem commands = new() { Header = "_Commands" };
        commands.Items.Add(Item("Commit…", "CommitSummary", () => CommitRequested?.Invoke()));
        commands.Items.Add(Item("Stash", "stash", () => StashRequested?.Invoke()));
        commands.Items.Add(new Separator());
        commands.Items.Add(Item("New branch…", "BranchCreate", () => NewBranchRequested?.Invoke()));
        commands.Items.Add(Item("New tag…", "TagCreate", () => NewTagRequested?.Invoke()));

        MenuItem help = new() { Header = "_Help" };
        help.Items.Add(Item("About", null, () => AboutRequested?.Invoke()));

        Menu menu = new()
        {
            Background = toolbar,
            Foreground = text,
            Items = { file, edit, view, repository, commands, help },
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
