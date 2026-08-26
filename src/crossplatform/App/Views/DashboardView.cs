using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The "Dashboard" landing view for the Avalonia / Linux port, shown when no
///  repository is open (or after "Close (go to Dashboard)").
///
///  <para>It merges the two upstream controls of
///  <c>BrowseDialog/DashboardControl</c>: <c>Dashboard</c> supplies the branding
///  strip and the "Start" links, <c>UserRepositoriesList</c> the search box, the
///  repository tiles (name, path, current branch) and their context menu.</para>
///
///  <para>The view owns the little state it needs to stay useful on its own: it
///  can reload itself (F5) and remove MRU entries without the host lifting a
///  finger, because a landing page that cannot refresh after you delete a
///  project from it is worse than no landing page. Opening a repository is still
///  the host's business and travels through <see cref="RepositorySelected"/>,
///  exactly as before.</para>
///
///  <para>Every git/filesystem touch — probing whether a path still exists,
///  reading the checked-out branch — happens on a worker; the UI thread only
///  ever receives the answers.</para>
/// </summary>
public sealed class DashboardView : UserControl
{
    private const string FavoritesGroup = "favorites";
    private const string RecentGroup = "recent";

    private readonly FavoritesService _favoritesService = new();
    private readonly RecentRepositoriesService _recentService = new();
    private readonly ExternalToolService _externalTools = new();

    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _status;

    private readonly MenuItem _showInFolderItem;
    private readonly MenuItem _categoriesItem;
    private readonly MenuItem _removeItem;
    private readonly MenuItem _removeMissingItem;

    // Category of each favorite, by normalised path. Read together with the list so
    // the rows can be grouped by category and the context menu can grey out the
    // category a repository is already filed under.
    private readonly Dictionary<string, string?> _categoryMap =
        new(StringComparer.OrdinalIgnoreCase);

    // The full, unfiltered model. Rebuilt on Load/refresh, never mutated in place.
    private readonly List<RepoEntry> _entries = [];

    // Branch labels currently on screen, so the background warm-up can fill them
    // in as answers arrive. Keyed by the row, not the path: the same repository
    // can appear both as a favorite and as a recent entry.
    private readonly Dictionary<RepoEntry, TextBlock> _branchLabels = [];

    // The three labels of each visible row, so the selected row can be re-inked
    // without rebuilding it (a rebuild would drop the selection it is reacting to).
    private readonly Dictionary<RepoEntry, TextBlock[]> _rowLabels = [];

    // Branch names already resolved, so switching the filter (which rebuilds
    // every row) does not re-read HEAD for repositories we have already seen.
    private readonly Dictionary<string, string?> _branchCache = new(StringComparer.Ordinal);

    private RepoEntry? _menuTarget;
    private int _generation;

    /// <summary>Raised with a repository path when the user activates an entry.</summary>
    public event Action<string>? RepositorySelected;

    /// <summary>Raised when the user picks "Open repository…".</summary>
    public event Action? OpenOtherRequested;

    /// <summary>
    ///  Raised after the favorites list has been changed from this view (a category
    ///  assigned, or a favorite removed), so a host showing the same list elsewhere —
    ///  the toolbar's working-directory dropdown groups favorites by category — can
    ///  reload it. The view has already persisted and redrawn itself when this fires.
    /// </summary>
    public event Action? FavoritesChanged;

    /// <summary>
    ///  Raised when the user picks "Create new repository". When nothing is
    ///  subscribed the view runs the flow itself (folder picker + <c>git init</c>)
    ///  and reports the result through <see cref="RepositorySelected"/>, so the
    ///  link is never a dead end.
    /// </summary>
    public event Action? CreateRepositoryRequested;

    /// <summary>
    ///  Raised when the user picks "Clone repository". Same fallback as
    ///  <see cref="CreateRepositoryRequested"/>: unsubscribed, the view opens the
    ///  port's own clone dialog.
    /// </summary>
    public event Action? CloneRepositoryRequested;

    public DashboardView()
    {
        Background = B("App.Window");
        Focusable = true;

        _search = new TextBox
        {
            Watermark = T("Search repositories…"),
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 14, 0, 8),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 320,
        };
        _search.TextChanged += (_, _) => Rebuild();
        _search.KeyDown += OnSearchKeyDown;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        // A selected row has to swap to the palette's plain ink. The Fluent
        // ListBoxItem theme paints the selected row with the framework accent —
        // measured #92C2E8 in the light theme — and this row's ink was never chosen
        // for that background: the repository name (App.Accent) measured 2.39:1 on it
        // and the path (App.TextDim) 2.86:1, so selecting a row made it HARDER to
        // read than leaving it alone. Right-clicking selects, so the context menu put
        // its own target into that state.
        //
        // App.Text on that same selection measures 8.82:1, so the fix is the ink, not
        // the background: no new tint, and nothing to unpick in the theme (a style
        // targeting the theme's own template child loses the precedence contest —
        // verified on screen, the background stayed #92C2E8).
        _list.SelectionChanged += (_, _) => ApplySelectionInk();

        // Tunnelling: the ListBox's own arrow-key navigation is a class handler on
        // the bubbling phase, and it would have moved the selection onto a group
        // caption before this handler ever saw the key.
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);

        // A group caption is a label, not a choice: its container must not take the
        // pointer either, or a click on the caption would select it.
        _list.ContainerPrepared += (_, e) =>
        {
            object? item = e.Container is ContentControl content ? content.Content : e.Container;
            bool selectable = EntryOf(item) is not null;
            e.Container.Focusable = selectable;
            e.Container.IsHitTestVisible = selectable;
        };

        _showInFolderItem = new MenuItem { Header = T("UserRepositoriesList/tsmiOpenFolder.Text", "Show in folder") };
        _showInFolderItem.Click += (_, _) => ShowTargetInFolder();
        _categoriesItem = new MenuItem
        {
            Header = T("UserRepositoriesList/tsmiCategories.Text", "Categories"),
        };
        _removeItem = new MenuItem
        {
            Header = T("UserRepositoriesList/tsmiRemoveFromList.Text", "Remove project from the list"),
        };
        _removeItem.Click += (_, _) => _ = RemoveTargetAsync();
        _removeMissingItem = new MenuItem
        {
            Header = T(
                "UserRepositoriesList/tsmiRemoveMissingReposFromList.Text",
                "Remove missing projects from the list"),
        };
        _removeMissingItem.Click += (_, _) => _ = RemoveMissingAsync();

        // Built once and only toggled on opening: rebuilding Items from the
        // Opening handler leaves the popup mis-measured. Same order as upstream's
        // contextMenuStripRepository (UserRepositoriesList.Designer.cs:162-168):
        // Show in folder / — / Categories / — / Remove / Remove missing.
        // The Categories SUBMENU is the one exception: its content is the set of
        // categories in use, which changes, so it is refilled from the root menu's
        // Opening handler — before the root popup is shown, and long before the
        // submenu's own popup measures itself.
        ContextMenu menu = new()
        {
            ItemsSource = new Control[]
            {
                _showInFolderItem,
                new Separator(),
                _categoriesItem,
                new Separator(),
                _removeItem,
                _removeMissingItem,
            },
        };
        menu.Opening += (_, _) => UpdateMenuState();
        _list.ContextMenu = menu;
        _list.AddHandler(ContextRequestedEvent, OnListContextRequested, RoutingStrategies.Tunnel);

        _status = new TextBlock
        {
            Foreground = B("App.TextDim"),
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
        };

        StackPanel content = new() { Margin = new Thickness(28, 20, 28, 24), Spacing = 0 };
        content.Children.Add(Branding());
        content.Children.Add(StartLinks());
        content.Children.Add(SectionHeader(T("Repositories")));
        content.Children.Add(_search);
        content.Children.Add(_list);
        content.Children.Add(_status);

        Content = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // F5 must work wherever the focus sits, search box included.
        AddHandler(KeyDownEvent, OnDashboardKeyDown, RoutingStrategies.Tunnel);

        Rebuild();
    }

    /// <summary>
    ///  Repopulates the dashboard from the given favorite and recent lists
    ///  (most-relevant first). Existence and branch names are resolved afterwards,
    ///  off the UI thread.
    /// </summary>
    public void Load(IReadOnlyList<string> favorites, IReadOnlyList<string> recent)
    {
        _entries.Clear();
        ReloadCategoryMap();

        HashSet<string> seen = new(StringComparer.Ordinal);

        // Favorites are grouped by category, the way upstream's list does it, so
        // that filing a repository is actually visible on the page that files it.
        // Uncategorised ones keep the plain "Favorite repositories" caption and come
        // first; the categories follow in name order. Within a group the stored
        // order (most recently favorited first) is preserved.
        List<RepoEntry> favoriteEntries = new();
        foreach (string path in favorites ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                favoriteEntries.Add(new RepoEntry(path, FavoritesGroup)
                {
                    Category = CategoryFor(path),
                });
            }
        }

        _entries.AddRange(favoriteEntries
            .OrderBy(e => e.Category is { Length: > 0 } ? 1 : 0)
            .ThenBy(e => e.Category ?? string.Empty, StringComparer.CurrentCulture));

        // SortRecentRepos: alphabetically by the folder name the row leads with, not by
        // the whole path — sorting by path would group by parent directory, which is
        // not what someone looking for a repository by name is scanning for.
        AppPreferences prefs = new SettingsService().Load();
        IEnumerable<string> ordered = recent ?? [];
        if (prefs.SortRecentRepos)
        {
            ordered = ordered.OrderBy(FolderName, StringComparer.CurrentCultureIgnoreCase);
        }

        foreach (string path in ordered)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                _entries.Add(new RepoEntry(path, RecentGroup));
            }
        }

        Rebuild();
        StartProbe();
    }

    /// <summary>
    ///  Reloads favorites and recent repositories from their stores (upstream's
    ///  F5 on the repository list) and re-resolves every branch name.
    /// </summary>
    public async Task RefreshAsync()
    {
        _branchCache.Clear();

        IReadOnlyList<string> favorites;
        IReadOnlyList<RecentRepositoriesService.RecentRepositoryEntry> recent;
        try
        {
            // Both stores hit the disk (and the recent one probes every path):
            // never on the UI thread.
            favorites = await Task.Run(() => _favoritesService.Load());
            recent = await _recentService.LoadEntriesAsync();
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(T("Error: {0}"), ex.Message);
            return;
        }

        Load(favorites, recent.Select(e => e.Path).ToList());
    }

    // ---- rendering -----------------------------------------------------------------

    // Rebuilds the visible rows from _entries and the current search text.
    // A brand-new list instance is assigned every time: handing the SAME
    // IList back to ItemsSource does not recreate the containers, so the rows
    // would keep their previous content.
    private void Rebuild()
    {
        string filter = _search.Text?.Trim() ?? string.Empty;
        List<RepoEntry> visible = _entries
            .Where(e => Matches(e, filter))
            .ToList();

        _branchLabels.Clear();
        _rowLabels.Clear();

        // The group caption is its own row rather than the first child of the tile
        // below it. Folded into the tile it became part of that tile's selection, so
        // arrowing into the list highlighted "Recent repositories" together with the
        // first repository — upstream's caption is a separate, unselectable label
        // (UserRepositoriesList.cs:679-700). GroupHeader() marks it so the keyboard
        // navigation and the container hook below can both recognise it.
        List<Control> rows = new(visible.Count);
        string? group = null;
        foreach (RepoEntry entry in visible)
        {
            if (GroupKeyOf(entry) != group)
            {
                group = GroupKeyOf(entry);
                rows.Add(GroupHeader(entry));
            }

            rows.Add(Row(entry));
        }

        _list.ItemsSource = rows;
        _list.IsVisible = rows.Count > 0;

        _status.Text = rows.Count > 0
            ? string.Empty
            : _entries.Count == 0
                ? T("No repositories yet — open, create or clone one to get started.")
                : T("No repository matches the search.");
    }

    private static bool Matches(RepoEntry entry, string filter)
        => filter.Length == 0
        || entry.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);

    // One repository tile: optional group heading, the folder name, the full
    // path and (once known) the checked-out branch.
    // One caption per group: the two fixed ones, plus one per category in use. A
    // category caption shows the category's own name, which is how the user sees
    // that "Categories ▸ …" did something.
    private static string GroupKeyOf(RepoEntry entry)
        => entry.Group == FavoritesGroup && entry.Category is { Length: > 0 } category
            ? FavoritesGroup + ":" + category
            : entry.Group;

    private Control GroupHeader(RepoEntry entry) => new TextBlock
    {
        Text = entry.Group == RecentGroup
            ? T("Dashboard/_recentRepositories.Text", "Recent repositories")
            : entry.Category is { Length: > 0 } category
                ? category
                : T("Dashboard/_favouriteRepositories.Text", "Favorite repositories"),
        Foreground = B("App.TextDim"),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 10, 0, 4),
    };

    /// <summary>
    ///  The second line of a row, shortened per
    ///  <see cref="AppPreferences.ShorteningRecentRepoPathStrategy"/>: the whole path,
    ///  the repository folder alone (upstream's MostSignDir), or the middle elided
    ///  (MiddleDots). The home prefix is collapsed to ~ in every case, as before.
    ///
    ///  <para>Read per row rather than cached: the list is rebuilt whenever it changes,
    ///  and a dashboard has tens of rows, not thousands.</para>
    /// </summary>
    private static string ShortenPath(string path)
    {
        string display = PathDisplay.CollapseHome(path);
        return new SettingsService().Load().ShorteningRecentRepoPathStrategy switch
        {
            "MostSignDir" => FolderName(path),
            "MiddleDots" => MiddleDots(display),
            _ => display,
        };
    }

    // Keeps the first and the last two segments and elides what is between them. Short
    // paths are returned untouched: eliding two segments to insert an ellipsis of the
    // same width would be a net loss.
    private static string MiddleDots(string display)
    {
        string[] parts = display.Split('/');
        if (parts.Length <= 4)
        {
            return display;
        }

        return string.Join('/', parts[0], "…", parts[^2], parts[^1]);
    }

    private Control Row(RepoEntry entry)
    {
        StackPanel outer = new() { Spacing = 0 };

        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 1, 0, 1),
        };

        // Upstream marks a broken entry by swapping the tile icon
        // (DashboardFolderGit → DashboardFolderError); both are already linked
        // by the csproj icon glob, so the port can say the same thing.
        Image? icon = IconLoader.Image(entry.Exists ? "DashboardFolderGit" : "DashboardFolderError");
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        StackPanel texts = new() { Spacing = 0 };
        Grid.SetColumn(texts, 1);

        TextBlock name = new()
        {
            Text = FolderName(entry.Path),
            // App.Link, not App.Accent: the row is click-to-open with a hand cursor and
            // the name stands for a repository path, so this is link ink. The accent is
            // calibrated as a fill and fell as low as 3.04:1 on the classic-dark tile.
            Foreground = entry.Exists ? B("App.Link") : B("App.TextDim"),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        TextBlock path = new()
        {
            Text = entry.Exists ? ShortenPath(entry.Path) : string.Format(T("{0} (missing)"), ShortenPath(entry.Path)),
            Foreground = B("App.TextDim"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        texts.Children.Add(name);
        texts.Children.Add(path);
        grid.Children.Add(texts);

        TextBlock branch = new()
        {
            Text = BranchText(entry),
            Foreground = B("App.GraphGreen"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
        };
        Grid.SetColumn(branch, 2);
        grid.Children.Add(branch);
        _branchLabels[entry] = branch;
        _rowLabels[entry] = [name, path, branch];

        // Upstream opens on a single click; the right button belongs to the
        // context menu, which the tunnelling handler has already dealt with.
        grid.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                Open(entry);
            }
        };

        // The path line is ellipsised to fit the tile, so the only way to read a
        // long path is the tooltip — upstream's UserRepositoriesList does the same.
        ToolTip.SetTip(grid, entry.Path);

        grid.Tag = entry;
        outer.Children.Add(grid);
        outer.Tag = entry;
        return outer;
    }

    // Re-inks the rows for the current selection: the selected one gets App.Text
    // (8.82:1 on the theme's selection fill, against 2.39:1 for the accent-coloured
    // name and 2.86:1 for the dim path), everything else goes back to its own colour.
    private void ApplySelectionInk()
    {
        RepoEntry? selected = EntryOf(_list.SelectedItem);

        foreach ((RepoEntry entry, TextBlock[] labels) in _rowLabels)
        {
            bool isSelected = ReferenceEquals(entry, selected);
            labels[0].Foreground = isSelected
                ? B("App.Text")
                // Same link ink as the initial render above, so deselecting a row puts
                // back exactly what BuildRow painted.
                : entry.Exists ? B("App.Link") : B("App.TextDim");
            labels[1].Foreground = isSelected ? B("App.Text") : B("App.TextDim");
            labels[2].Foreground = isSelected ? B("App.Text") : B("App.GraphGreen");
        }
    }

    private string BranchText(RepoEntry entry)
        => _branchCache.TryGetValue(entry.Path, out string? branch) && !string.IsNullOrEmpty(branch)
            ? branch
            : string.Empty;

    private static string FolderName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // ---- background resolution -----------------------------------------------------

    // Resolves "does it still exist" and "which branch is checked out" for every
    // entry on a worker, then folds the answers back in on the UI thread.
    // Upstream warms the same two values in parallel from a cache
    // (RepositoryHistoryUIService); the port reads .git/HEAD instead of running
    // a git process per row, so a single pass is cheap enough.
    private void StartProbe()
    {
        int generation = ++_generation;
        RepoEntry[] pending = _entries.Where(e => !_branchCache.ContainsKey(e.Path)).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (RepoEntry entry in pending)
            {
                bool exists = Directory.Exists(entry.Path);
                string? branch = exists ? RecentRepositoriesService.ReadCurrentBranch(entry.Path) : null;

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _generation)
                    {
                        return;
                    }

                    _branchCache[entry.Path] = branch;
                    if (entry.Exists != exists)
                    {
                        entry.Exists = exists;

                        // The icon and the "(missing)" suffix live in the row, so
                        // the row has to be rebuilt for this one.
                        Rebuild();
                        return;
                    }

                    if (_branchLabels.TryGetValue(entry, out TextBlock? label))
                    {
                        label.Text = branch ?? string.Empty;
                    }
                });
            }
        });
    }

    // ---- commands ------------------------------------------------------------------

    private void Open(RepoEntry entry)
    {
        if (!entry.Exists)
        {
            _status.Text = string.Format(
                T("{0} no longer exists. Use the context menu to remove it from the list."), entry.Path);
            return;
        }

        RepositorySelected?.Invoke(entry.Path);
    }

    private void ShowTargetInFolder()
    {
        if (_menuTarget is not { } entry)
        {
            return;
        }

        string path = entry.Path;

        // Shells out (D-Bus / xdg-open): keep it off the UI thread.
        _ = Task.Run(() => _externalTools.ShowInFolder(path));
    }

    private async Task RemoveTargetAsync()
    {
        if (_menuTarget is not { } entry)
        {
            return;
        }

        if (entry.Group == FavoritesGroup)
        {
            _favoritesService.Remove(entry.Path);
        }

        await _recentService.RemoveAsync(entry.Path);

        // After the reload, never before: rebuilding the list resets the status.
        await RefreshAsync();
        _status.Text = string.Format(T("Removed from the list: {0}"), entry.Path);
    }

    // ---- categories ----------------------------------------------------------------

    // Refills "Categories ▸" for the row that was right-clicked.
    //
    // Upstream's submenu is "(none)", the categories in use, a separator, then
    // "Add new..." (UserRepositoriesList.cs:762-802), and it marks the category the
    // repository is already in by DISABLING it rather than ticking it — there is no
    // checkmark anywhere in that file. Both of those are kept.
    //
    // The one deliberate departure is "(none)". Upstream wires it to
    // AssignCategoryAsync(repo, null), and there — exactly as in this port's
    // FavoritesService.AssignCategory — a blank category does not merely un-file the
    // repository, it DELETES the favorite, because the category *is* the favorite
    // flag (LocalRepositoryManager.cs:126-167, verified). A menu entry reading
    // "(none)" that silently drops the repository out of the favorites list is a
    // trap, so this port spells the consequence out instead: the item says "Remove
    // from favorites", sits last behind its own separator (away from the category
    // names, so a mis-click costs nothing), and is only offered when the row really
    // is a favorite. The underlying call is the same one.
    private void RebuildCategoryMenu()
    {
        List<Control> items = new();

        if (_menuTarget is { } target)
        {
            string? current = CategoryFor(target.Path);
            bool isFavorite = target.Group == FavoritesGroup;

            foreach (string category in CategoriesInUse())
            {
                MenuItem item = new()
                {
                    Header = category,

                    // Already filed here: nothing to do, so it is greyed out.
                    IsEnabled = !string.Equals(category, current, StringComparison.CurrentCulture),
                };
                item.Click += (_, _) => _ = AssignCategoryAsync(target, category);
                items.Add(item);
            }

            if (items.Count > 0)
            {
                items.Add(new Separator());
            }

            MenuItem add = new()
            {
                Header = T("UserRepositoriesList/tsmiCategoryAdd.Text", "Add new…"),
            };
            add.Click += (_, _) => _ = AddCategoryAsync(target);
            items.Add(add);

            if (isFavorite)
            {
                items.Add(new Separator());
                MenuItem remove = new()
                {
                    Header = T("Remove from favorites"),
                };
                remove.Click += (_, _) => _ = RemoveFavoriteAsync(target);
                items.Add(remove);
            }
        }

        // A brand-new list every time: handing back the same instance would leave the
        // already-realised menu containers showing the previous categories.
        _categoriesItem.ItemsSource = items;
    }

    // Files the row under an existing category. On a row that is only "recent" this
    // also makes it a favorite, which is upstream's behaviour: assigning a real
    // category to a non-favorite adds it to the favorites.
    private async Task AssignCategoryAsync(RepoEntry entry, string category)
    {
        _favoritesService.AssignCategory(entry.Path, category);
        FavoritesChanged?.Invoke();
        await RefreshAsync();
        _status.Text = string.Format(T("Filed under “{0}”: {1}"), category, entry.Path);
    }

    private async Task AddCategoryAsync(RepoEntry entry)
    {
        string? category = await PromptCategoryNameAsync(CategoriesInUse());
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        await AssignCategoryAsync(entry, category.Trim());
    }

    // The honest form of upstream's "(none)": same call, name that matches the effect.
    private async Task RemoveFavoriteAsync(RepoEntry entry)
    {
        _favoritesService.Remove(entry.Path);
        FavoritesChanged?.Invoke();
        await RefreshAsync();
        _status.Text = string.Format(T("Removed from favorites: {0}"), entry.Path);
    }

    // Upstream's FormDashboardCategoryTitle: a caption prompt that rejects an empty
    // name and a duplicate one. It reports both with a blocking message box; this
    // shows the message inline and keeps the dialog open, which needs no second
    // modal on top of a modal.
    private async Task<string?> PromptCategoryNameAsync(IReadOnlyList<string> existing)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        TaskCompletionSource<string?> tcs = new();

        TextBox input = new()
        {
            Background = B("App.Control"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
        };

        TextBlock error = new()
        {
            Foreground = B("App.DiffRemoved"),
            FontSize = 12,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap,
        };

        Theming.ZoomWindow dialog = new()
        {
            Title = T("FormDashboardCategoryTitle/$this.Text", "Enter Caption"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = B("App.Window"),
        };

        void Accept()
        {
            string name = input.Text?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                error.Text = T("Category name is required");
                error.IsVisible = true;
                return;
            }

            if (existing.Any(e => string.Equals(e, name, StringComparison.CurrentCulture)))
            {
                error.Text = T("Category name already exists");
                error.IsVisible = true;
                return;
            }

            tcs.TrySetResult(name);
            dialog.Close();
        }

        Button ok = new() { Content = T("OK"), IsDefault = true, MinWidth = 84 };
        Button cancel = new() { Content = T("Cancel"), IsCancel = true, MinWidth = 84 };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(null);
            dialog.Close();
        };
        input.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;
                Accept();
            }
        };

        // Closing by the window button / Escape must resolve the wait, not hang it.
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = T("FormDashboardCategoryTitle/lblCategoryName.Text", "Category name"),
            Foreground = B("App.Text"),
        });
        content.Children.Add(input);
        content.Children.Add(error);
        content.Children.Add(buttons);
        dialog.Content = content;

        input.AttachedToVisualTree += (_, _) => input.Focus();

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    // The categories currently in use, name-ordered, straight from the store.
    //
    // Read synchronously, on purpose: the menu must show the set that exists at the
    // moment it opens, and a cache warmed in the background would occasionally paint
    // a stale submenu. It is one small JSON file, and this view (RemoveTargetAsync)
    // and MainWindow (LoadDashboardAsync) already read it the same way.
    private IReadOnlyList<string> CategoriesInUse() => _favoritesService.Categories();

    private void ReloadCategoryMap()
    {
        _categoryMap.Clear();
        foreach (FavoriteRepo entry in _favoritesService.LoadEntries())
        {
            _categoryMap[entry.Path] = entry.HasCategory ? entry.Category : null;
        }
    }

    private string? CategoryFor(string path)
        => _categoryMap.TryGetValue(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            out string? category)
            ? category
            : null;

    private async Task RemoveMissingAsync()
    {
        int removed = await _recentService.RemoveMissingAsync();
        await RefreshAsync();
        _status.Text = removed > 0
            ? string.Format(T("Removed {0} missing project(s) from the list."), removed)
            : T("No missing projects to remove.");
    }

    // ---- input ---------------------------------------------------------------------

    private void OnDashboardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = RefreshAsync();
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Upstream opens the first entry of the filtered list.
            if (FirstVisibleEntry() is { } first)
            {
                e.Handled = true;
                Open(first);
            }
        }
        else if (e.Key == Key.Down && NextEntryIndex(-1, 1) is int first)
        {
            // Down out of the search box enters the list at its first repository
            // (upstream UserRepositoriesList.cs:721-734).
            e.Handled = true;
            SelectAndFocus(first);
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key is Key.Down or Key.Up)
        {
            // Own the arrow keys outright. The built-in navigation walks the raw item
            // list, group captions included, and stops on them; this walks repository
            // rows only and moves the FOCUS with the selection, which is what was
            // missing — the caret used to stay behind in the search box.
            e.Handled = true;
            int step = e.Key == Key.Down ? 1 : -1;
            if (NextEntryIndex(_list.SelectedIndex, step) is int next)
            {
                SelectAndFocus(next);
            }
            else if (step < 0)
            {
                // Above the first row the focus goes back to the search box
                // (upstream UserRepositoriesList.cs:679-700).
                _list.SelectedIndex = -1;
                _search.Focus();
            }
        }
        else if (e.Key is Key.Enter or Key.Return && EntryOf(_list.SelectedItem) is { } entry)
        {
            e.Handled = true;
            Open(entry);
        }
    }

    // The next index in <paramref name="step"/> direction that holds a repository
    // rather than a group caption, or null when there is none left that way.
    private int? NextEntryIndex(int from, int step)
    {
        for (int i = from + step; i >= 0 && i < _list.ItemCount; i += step)
        {
            if (EntryOf(ItemAt(i)) is not null)
            {
                return i;
            }
        }

        return null;
    }

    private object? ItemAt(int index)
        => _list.ItemsSource?.Cast<object>().ElementAtOrDefault(index);

    // Selects a row AND puts the keyboard focus on its container. Focusing the
    // ListBox itself is not enough: the focus stayed in the search box, so every
    // further arrow key went back to the text editor instead of the list.
    private void SelectAndFocus(int index)
    {
        _list.SelectedIndex = index;
        _list.ScrollIntoView(index);

        if (_list.ContainerFromIndex(index) is { } container)
        {
            container.Focus(NavigationMethod.Directional);
            return;
        }

        // The container may not be realised yet right after a rebuild.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_list.ContainerFromIndex(index) is { } late)
                {
                    late.Focus(NavigationMethod.Directional);
                }
            },
            DispatcherPriority.Loaded);
    }

    // Resolves the row the pointer is over before the popup appears, so the
    // entries act on what was right-clicked rather than on the selection.
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _menuTarget = null;
        if (!e.TryGetPosition(_list, out Point position))
        {
            return;
        }

        Visual? hit = _list.InputHitTest(position) as Visual;
        while (hit is not null)
        {
            if (hit is Control { Tag: RepoEntry entry })
            {
                _menuTarget = entry;
                return;
            }

            hit = hit.GetVisualParent();
        }
    }

    private void UpdateMenuState()
    {
        bool hasTarget = _menuTarget is not null;
        _showInFolderItem.IsEnabled = hasTarget && _menuTarget!.Exists;
        _removeItem.IsEnabled = hasTarget;

        // Upstream hides the submenu outright when nothing is selected
        // (UserRepositoriesList.cs:566-575).
        _categoriesItem.IsVisible = hasTarget;
        RebuildCategoryMenu();

        // Upstream only shows the bulk entry when there is something to clean up.
        _removeMissingItem.IsVisible = _entries.Any(entry => !entry.Exists);
    }

    private RepoEntry? FirstVisibleEntry()
        => _list.ItemsSource?.Cast<object>().Select(EntryOf).FirstOrDefault(e => e is not null);

    private static RepoEntry? EntryOf(object? item)
        => item switch
        {
            RepoEntry entry => entry,
            Control { Tag: RepoEntry tagged } => tagged,
            _ => null,
        };

    // ---- branding + start links ----------------------------------------------------

    // Upstream draws a wide GitExtensionsLogoWide bitmap here (Dashboard.Designer.cs:
    // 94-113). It cannot be reused under a name of its own: the bitmap IS the words
    // "Git Extensions". So the strip is now the product mark beside the product name
    // as text, which also retires a workaround this method used to need — the wide
    // wordmark's glyphs are pure white on transparent, unreadable on the light
    // theme's #ECECEC strip (measured at 1.18:1), so the bitmap and a text fallback
    // had to be built together and swapped on ActualThemeVariantChanged. The mark is
    // legible on both themes (it is drawn, not stencilled in white), and the text is
    // App.Text on App.PanelAlt: 14.11:1.
    private Control Branding()
    {
        StackPanel panel = new() { Spacing = 2 };

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (IconLoader.Load("GitNext") is { } mark)
        {
            row.Children.Add(new Image
            {
                Source = mark,
                Height = 44,
                Width = 44,
                Stretch = Stretch.Uniform,
            });
        }

        // The product name is never translated: a brand read in another language
        // stops identifying the program (upstream marks its own copy _NO_TRANSLATE_).
        row.Children.Add(new TextBlock
        {
            Text = "gitNext",
            Foreground = B("App.Text"),
            FontSize = 26,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        panel.Children.Add(row);

        panel.Children.Add(new TextBlock
        {
            Text = T("Open a repository to get started."),
            Foreground = B("App.TextDim"),
            FontSize = 13,
        });

        return new Border
        {
            Background = B("App.PanelAlt"),
            BorderBrush = B("App.Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 14, 16, 14),
            Child = panel,
        };
    }

    // Upstream's "Start" group: open / create / clone (Dashboard.cs:115-126).
    private Control StartLinks()
    {
        StackPanel links = new() { Spacing = 2, Margin = new Thickness(16, 12, 0, 0) };
        links.Children.Add(StartLink(
            T("FormBrowse/openToolStripMenuItem.Text", "Open repository…"),
            "RepoOpen",
            () => OpenOtherRequested?.Invoke()));
        links.Children.Add(StartLink(
            T("FormBrowse/initNewRepositoryToolStripMenuItem.Text", "Create new repository…"),
            "RepoCreate",
            () =>
            {
                if (CreateRepositoryRequested is { } handler)
                {
                    handler();
                }
                else
                {
                    _ = CreateRepositoryAsync();
                }
            }));
        links.Children.Add(StartLink(
            T("FormBrowse/cloneToolStripMenuItem.Text", "Clone repository…"),
            "CloneRepoGit",
            () =>
            {
                if (CloneRepositoryRequested is { } handler)
                {
                    handler();
                }
                else
                {
                    _ = CloneRepositoryAsync();
                }
            }));

        StackPanel panel = new() { Spacing = 0 };
        panel.Children.Add(SectionHeader(T("Dashboard/_develop.Text", "Start")));
        panel.Children.Add(links);
        return panel;
    }

    private Control StartLink(string text, string iconName, Action action)
    {
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        if (IconLoader.Image(iconName) is { } icon)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(icon);
        }

        row.Children.Add(new TextBlock
        {
            Text = text,
            // A "Start" entry is a link in everything but the underline: hand cursor and
            // a click that runs an action. It takes the text-grade blue.
            Foreground = B("App.Link"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                action();
            }
        };

        return row;
    }

    // Fallbacks used when the host has not claimed the two links. They mirror
    // MainWindow's own flows and finish by handing the new repository to
    // RepositorySelected, which is the only way this view knows to open one.
    private async Task CreateRepositoryAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders =
            await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = T("Choose a directory for the new repository"),
            });

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { Length: > 0 } dir)
        {
            return;
        }

        CloneInitResult result = await Task.Run(() => new CloneInitService().Init(dir));
        if (!result.Success)
        {
            _status.Text = result.Output;
            return;
        }

        RepositorySelected?.Invoke(result.RepoPath ?? dir);
    }

    private async Task CloneRepositoryAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        CloneDialog dialog = new();
        await dialog.ShowDialog(owner);

        if (dialog.ClonedRepoPath is { Length: > 0 } repo && Directory.Exists(repo))
        {
            RepositorySelected?.Invoke(repo);
        }
    }

    // ---- helpers -------------------------------------------------------------------

    private Control SectionHeader(string text) => new TextBlock
    {
        Text = text,
        Foreground = B("App.Text"),
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(16, 18, 0, 2),
    };

    private static IBrush B(string key)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    /// <summary>A single dashboard tile: a repository path and which list it came from.</summary>
    private sealed class RepoEntry(string path, string group)
    {
        public string Path { get; } = path;

        public string Group { get; } = group;

        /// <summary>
        ///  Whether the directory is still there. Assumed true until the
        ///  background probe says otherwise, so the list paints immediately.
        /// </summary>
        public bool Exists { get; set; } = true;

        /// <summary>
        ///  The category this favorite is filed under, or <c>null</c> for an
        ///  uncategorised favorite / a merely recent entry. Drives the group caption.
        /// </summary>
        public string? Category { get; set; }
    }
}
