using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using GitExtensions.Avalonia.Theming;

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
    public event Action? PullRequested;
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

    // Submodules / worktrees split buttons. The toolbar itself performs no git
    // work: the host supplies a provider that lists the repo's submodules /
    // worktrees (off the UI thread), and choosing one raises
    // OpenRepositoryRequested with that path so the host opens it as the active
    // repository.
    public Func<Task<IReadOnlyList<RepoLink>>>? SubmodulesProvider { get; set; }
    public Func<Task<IReadOnlyList<RepoLink>>>? WorktreesProvider { get; set; }
    public event Action<string>? OpenRepositoryRequested;

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

        StackPanel bar = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
            Margin = new Thickness(6, 3),
        };

        bar.Children.Add(MakeButton("RepoOpen", "Open", "Open repository", () => OpenRepoRequested?.Invoke()));
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("PullFetch", "Fetch", "Fetch from remote", () => FetchRequested?.Invoke()));
        bar.Children.Add(MakeButton("Pull", "Pull", "Pull from remote", () => PullRequested?.Invoke()));
        bar.Children.Add(MakeButton("Push", "Push", "Push to remote", () => PushRequested?.Invoke()));
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("CommitSummary", "Commit", "Commit changes", () => CommitRequested?.Invoke()));
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("stash", "Stash", "Stash changes", () => StashRequested?.Invoke()));
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("ReloadRevisions", "Refresh", "Refresh", () => RefreshRequested?.Invoke()));
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("BranchCreate", "New branch", "Create a new branch", () => NewBranchRequested?.Invoke()));

        // ---- submodules / worktrees split buttons --------------------------------
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeRepoLinkButton("SubmodulesManage", "Submodules",
            "Open a submodule (or the parent super-project) as the active repository",
            () => SubmodulesProvider, border));
        bar.Children.Add(MakeRepoLinkButton("WorkTree", "Worktrees",
            "Open a worktree as the active repository",
            () => WorktreesProvider, border));

        // ---- view / layout group -------------------------------------------------
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("LayoutFooter", "Split view",
            "Toggle the Commit tab layout between side-by-side and stacked (detail + diff)",
            () => SplitViewToggleRequested?.Invoke()));
        bar.Children.Add(MakeMenuButton("LayoutSidebarLeft", "Commit info", "Commit-info position", new[]
        {
            ("LayoutFooter", "Below graph", (Action)(() => CommitInfoPositionChanged?.Invoke(CommitInfoPosition.BelowGraph))),
            ("LayoutSidebarTopLeft", "Left of graph", (Action)(() => CommitInfoPositionChanged?.Invoke(CommitInfoPosition.LeftOfGraph))),
            ("LayoutSidebarTopRight", "Right of graph", (Action)(() => CommitInfoPositionChanged?.Invoke(CommitInfoPosition.RightOfGraph))),
        }));

        // ---- external tools group ------------------------------------------------
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("BrowseFileExplorer", "File Explorer", "Open the repository in the file manager",
            () => FileExplorerRequested?.Invoke()));
        bar.Children.Add(MakeButton("Console", "Terminal", "Open a terminal in the repository directory",
            () => OpenTerminalRequested?.Invoke()));

        // Flat/borderless buttons with a subtle hover fill (the Fluent template
        // paints the button's chrome through its inner ContentPresenter, so we
        // style that part directly for both the resting and pointer-over states).
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

        Content = bar;
    }

    private Button MakeButton(string iconName, string label, string tooltip, Action onClick)
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

        // Show the label always when there's no icon, otherwise as a short caption.
        content.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
        });

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
        return button;
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

        MenuFlyout flyout = new();
        flyout.Opening += (_, _) => _ = PopulateRepoLinksAsync(flyout, iconName, provider());

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
            flyout.Items.Add(new MenuItem { Header = "(no repository open)", IsEnabled = false });
            return;
        }

        flyout.Items.Add(new MenuItem { Header = "Loading…", IsEnabled = false });

        IReadOnlyList<RepoLink> links;
        try
        {
            links = await provider();
        }
        catch
        {
            flyout.Items.Clear();
            flyout.Items.Add(new MenuItem { Header = "(unable to list)", IsEnabled = false });
            return;
        }

        flyout.Items.Clear();
        if (links.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        foreach (RepoLink link in links)
        {
            MenuItem item = new() { Header = link.Label };
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

    private static Control Separator(IBrush brush) => new Border
    {
        Width = 1,
        // Extra horizontal margin gives each button group some breathing room.
        Margin = new Thickness(6, 4),
        Background = brush,
    };

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
