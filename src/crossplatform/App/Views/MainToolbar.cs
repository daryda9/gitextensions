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
    private Button? _pullButton;
    private TextBlock? _pullCaption;
    private Image? _pullIcon;
    private Button? _commitButton;
    private TextBlock? _commitCaption;
    private Image? _commitIcon;

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

    private readonly StackPanel _bar;

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
        _bar = bar;

        bar.Children.Add(MakeButton("RepoOpen", "Open", "Open repository", () => OpenRepoRequested?.Invoke()));

        // Inline repo-path + branch dropdowns near the left, echoing the original
        // FormBrowse toolbar (a repository-path selector and a current-branch
        // selector inline in the toolbar).
        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeRepoPathButton(border));
        bar.Children.Add(MakeBranchButton(border));

        bar.Children.Add(Separator(border));
        bar.Children.Add(MakeButton("PullFetch", "Fetch", "Fetch from remote", () => FetchRequested?.Invoke()));
        _pullButton = MakeButton("Pull", "Pull", "Pull from remote", () => PullRequested?.Invoke(),
            out _pullCaption, out _pullIcon);
        bar.Children.Add(_pullButton);
        _pushButton = MakeButton("Push", "Push", "Push to remote", () => PushRequested?.Invoke(),
            out _pushCaption, out _pushIcon);
        bar.Children.Add(_pushButton);
        bar.Children.Add(Separator(border));
        _commitButton = MakeButton("CommitSummary", "Commit", "Commit changes", () => CommitRequested?.Invoke(),
            out _commitCaption, out _commitIcon);
        bar.Children.Add(_commitButton);
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
        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#8A8A8A");
        IBrush accent = Brush("App.Accent", "#007ACC");
        IBrush green = Brush("App.GraphGreen", "#3FB950");
        IBrush orange = new SolidColorBrush(Color.Parse("#E6A700"));

        // Push: light up with an "ahead" badge when there are commits to push.
        if (_pushCaption is not null)
        {
            bool lit = ahead > 0;
            _pushCaption.Text = lit ? $"Push ↑{ahead}" : "Push";
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
            _pullCaption.Text = lit ? $"Pull ↓{behind}" : "Pull";
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
            _commitCaption.Text = changes > 0 ? $"Commit ({changes})" : "Commit";
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
            _branchCaption.Text = string.IsNullOrWhiteSpace(branch) ? "(no branch)" : branch;
            _branchCaption.Foreground = text;
        }

        // Inline repo-path dropdown caption: current repo path, home collapsed to ~.
        if (_repoPathCaption is not null)
        {
            _repoPathCaption.Text = string.IsNullOrWhiteSpace(repoPath)
                ? "(no repository)"
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
            _bar.Children.Add(_repoIndicator);
        }

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            _repoIndicator.Text = "(no repository)";
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
            string label = $"{name} — {shown}";
            if (!string.IsNullOrWhiteSpace(branch))
            {
                label += $" ({branch})";
            }

            _repoIndicator.Text = label;
            _repoIndicator.Foreground = dim;
            ToolTip.SetTip(_repoIndicator, label);
        }
    }

    // Replaces a leading user-home prefix with "~" for a compact path display.
    private static string CollapseHome(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal))
        {
            return "~" + path.Substring(home.Length);
        }

        return path;
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
            Text = "(no branch)",
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
        ToolTip.SetTip(button, "Checkout a local branch");
        button.Click += async (_, _) =>
        {
            await PopulateBranchesAsync(flyout, BranchesProvider);
            flyout.ShowAt(button);
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
            Text = "(no repository)",
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
        ToolTip.SetTip(button, "Open a recent repository");
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
            flyout.Items.Add(new MenuItem { Header = "(no repository)", IsEnabled = false });
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
