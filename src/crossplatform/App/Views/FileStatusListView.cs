using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Session-wide state of the changed-files lists, so the grouping a user picks
///  in one place is the grouping every list uses — the equivalent of the
///  upstream <c>DiffListSortService</c> singleton plus its
///  <c>AppSettings</c> entries.
/// </summary>
public sealed class FileStatusListOptions
{
    /// <summary>The one instance shared by every <see cref="FileStatusListView"/>.</summary>
    public static FileStatusListOptions Session { get; } = new();

    /// <summary>Which grouping the lists apply.</summary>
    public DiffFileGroupMode GroupMode { get; set; } = DiffFileGroupMode.None;

    /// <summary>Whether the path grouping nests its directories.</summary>
    public bool AsTree { get; set; } = true;
}

/// <summary>
///  The changed-files list of the port: the upstream <c>FileStatusList</c>'s
///  toolbar (collapse groups, refresh, flat/tree split button, group by
///  path / extension / status) over its regular-expression filter box, over the
///  list itself.
///
///  <para>Shared on purpose: the same control backs the Diff pane, and is meant
///  to back the File-tree tab and the stash view, which in the original are the
///  same <c>FileStatusList</c>. The host supplies the rows
///  (<see cref="SetFiles"/>), reads the selection
///  (<see cref="SelectedFile"/>/<see cref="SelectedFileChanged"/>) and may add
///  its own toolbar items (<see cref="AddToolbarItem"/>) — everything that needs
///  git runs in the host, this control never touches a repository.</para>
///
///  <para>Captions come from the upstream <c>FileStatusList</c> XLIFF category
///  and are re-applied on <see cref="TranslationService.LanguageChanged"/>.</para>
/// </summary>
public sealed class FileStatusListView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    // File-status glyph colours: modified=accent, added=green, deleted=red.
    private static readonly IBrush ModifiedGlyph = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xD6));
    private static readonly IBrush AddedGlyph = new SolidColorBrush(Color.FromRgb(0x6A, 0xC7, 0x76));
    private static readonly IBrush DeletedGlyph = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));

    // A regex that does not compile tints the box and its counter, rather than
    // throwing or blanking the list (the text then filters as a literal). It is
    // the background, not the border: the Fluent TextBox paints a focus border of
    // its own, which would hide the signal exactly while the user is typing —
    // upstream colours the BackColor for the same reason.
    private static readonly IBrush InvalidFilterBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));
    private static readonly IBrush InvalidFilterBackground =
        new SolidColorBrush(Color.FromRgb(0x4A, 0x24, 0x24));

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private readonly FileStatusListOptions _options = FileStatusListOptions.Session;

    private readonly ListBox _list;
    private readonly WrapPanel _toolbar;
    private readonly TextBox _filterBox;
    private readonly Button _filterClearButton;
    private readonly TextBlock _filterCount;
    private readonly DispatcherTimer _filterDebounce;

    private readonly Button _collapseGroupsButton;
    private readonly Button _refreshButton;
    private readonly Border _asTreeSplit;
    private readonly Button _asTreeButton;
    private readonly Button _groupMenuButton;
    private readonly ToggleButton _byPathButton;
    private readonly ToggleButton _byExtensionButton;
    private readonly ToggleButton _byStatusButton;
    private readonly Border _toolbarBar;
    private Image? _asTreeIcon;

    private IReadOnlyList<DiffFileRow> _files = [];
    private DiffFileFilter _filter = DiffFileFilter.None;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    // The path of the row that must stay selected across a rebuild (filter
    // change, collapse, reload), so re-grouping does not reload another diff.
    private string? _selectedName;
    private bool _suppressSelection;
    private bool _updatingGroupButtons;

    public FileStatusListView()
    {
        _list = new ListBox
        {
            FontFamily = Monospace,
            FontSize = 12,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderThickness = new Thickness(0),

            // No recycling: the rows are built by hand (they do not bind to the
            // DataContext), and the list mixes group headers with file rows, so a
            // recycled container would keep the wrong visual.
            ItemTemplate = new FuncDataTemplate<object>(
                (item, _) => BuildRow(item),
                supportsRecycling: false),
        };
        _list.SelectionChanged += OnSelectionChanged;

        // Tight rows + an App.Selection highlight, matching the revision grid.
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 1, 8, 1)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            },
        });
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":pointerover")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.PanelAlt")) },
        });
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.Selection")) },
        });

        AddToolbarStyles();

        // ---- toolbar ----
        _collapseGroupsButton = IconButton("CollapseAll", "⊟", CollapseOrExpandGroups);
        _refreshButton = IconButton("ReloadRevisions", "⟳", () => RefreshRequested?.Invoke());
        _refreshButton.IsVisible = false;   // only when the host asks for it

        _asTreeButton = IconButton("FileTree", "☰", ToggleTree);
        _asTreeIcon = _asTreeButton.Content as Image;
        // The lambda only runs on a click, long after the field is assigned.
        _groupMenuButton = IconButton(null, "▾", () => ShowGroupMenu(_groupMenuButton!));
        _groupMenuButton.Padding = new Thickness(2, 2);

        // Body + arrow inside one Border, like the main toolbar's Pull button:
        // the upstream item is a ToolStripSplitButton.
        StackPanel splitContent = new() { Orientation = Orientation.Horizontal };
        splitContent.Children.Add(_asTreeButton);
        splitContent.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(0, 4),
            Background = B("App.Border"),
        });
        splitContent.Children.Add(_groupMenuButton);

        _asTreeSplit = new Border
        {
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = splitContent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0),
        };

        _byPathButton = IconToggle("FolderClosed", "/", DiffFileGroupMode.Path);
        _byExtensionButton = IconToggle("File", ".*", DiffFileGroupMode.Extension);
        _byStatusButton = IconToggle("FileStatusModified", "M", DiffFileGroupMode.Status);

        _toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(4, 2, 4, 2),
        };
        _toolbar.Children.Add(_collapseGroupsButton);
        _toolbar.Children.Add(_refreshButton);
        _toolbar.Children.Add(ToolSeparator());
        _toolbar.Children.Add(_asTreeSplit);
        _toolbar.Children.Add(_byPathButton);
        _toolbar.Children.Add(_byExtensionButton);
        _toolbar.Children.Add(_byStatusButton);

        _toolbarBar = new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _toolbar,
        };

        // ---- filter row ----
        _filterBox = new TextBox
        {
            FontSize = 12,
            MinHeight = 0,
            Padding = new Thickness(6, 2, 6, 2),
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _filterBox.TextChanged += (_, _) => RestartFilterDebounce();
        _filterBox.KeyDown += OnFilterKeyDown;

        _filterClearButton = IconButton("DeleteText", "✕", () =>
        {
            _filterBox.Text = string.Empty;
            ApplyFilter();
        });

        _filterCount = new TextBlock
        {
            FontSize = 11,
            Foreground = B("App.TextDim"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 2, 0),
        };

        Grid filterRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(4, 2, 4, 3),
        };
        Grid.SetColumn(_filterBox, 0);
        Grid.SetColumn(_filterCount, 1);
        Grid.SetColumn(_filterClearButton, 2);
        filterRow.Children.Add(_filterBox);
        filterRow.Children.Add(_filterCount);
        filterRow.Children.Add(_filterClearButton);

        Border filterBar = new()
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = filterRow,
        };

        // Re-filtering rebuilds the list, so typing must not do it per keystroke
        // (the upstream box throttles by 250 ms).
        _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            ApplyFilter();
        };

        DockPanel root = new() { Background = B("App.Panel") };
        DockPanel.SetDock(_toolbarBar, Dock.Top);
        DockPanel.SetDock(filterBar, Dock.Top);
        root.Children.Add(_toolbarBar);
        root.Children.Add(filterBar);
        root.Children.Add(_list);

        Content = root;

        SyncGroupButtons();
        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------ public surface

    /// <summary>
    ///  The list itself, so the host can attach a context menu or ask about focus.
    ///  Its items are <see cref="FileListNode"/>s, not <see cref="DiffFileRow"/>s:
    ///  read the selection through <see cref="SelectedFile"/>.
    /// </summary>
    public ListBox List => _list;

    /// <summary>All rows currently loaded, before filtering and grouping.</summary>
    public IReadOnlyList<DiffFileRow> Files => _files;

    /// <summary>The selected file, or <see langword="null"/> (no selection, or a group header).</summary>
    public DiffFileRow? SelectedFile => (_list.SelectedItem as FileListFileNode)?.Row;

    /// <summary>Raised when the selected file changes (never for a group header).</summary>
    public event Action<DiffFileRow?>? SelectedFileChanged;

    /// <summary>Raised by the toolbar's refresh button (only shown when <see cref="ShowRefreshButton"/>).</summary>
    public event Action? RefreshRequested;

    /// <summary>Whether the toolbar offers a refresh button.</summary>
    public bool ShowRefreshButton
    {
        get => _refreshButton.IsVisible;
        set => _refreshButton.IsVisible = value;
    }

    /// <summary>The filter text, as typed (a regular expression).</summary>
    public string FilterText
    {
        get => _filterBox.Text ?? string.Empty;
        set
        {
            _filterBox.Text = value;
            ApplyFilter();
        }
    }

    /// <summary>Adds a host-specific item at the end of the toolbar.</summary>
    public void AddToolbarItem(Control item) => _toolbar.Children.Add(item);

    /// <summary>Adds a separator at the end of the toolbar.</summary>
    public void AddToolbarSeparator() => _toolbar.Children.Add(ToolSeparator());

    /// <summary>Puts the caret in the filter box.</summary>
    public void FocusFilter()
    {
        _filterBox.Focus();
        _filterBox.SelectAll();
    }

    /// <summary>
    ///  Replaces the loaded rows and selects the first one, raising
    ///  <see cref="SelectedFileChanged"/> exactly once for the new selection.
    /// </summary>
    public void SetFiles(IReadOnlyList<DiffFileRow> rows)
    {
        _files = rows;
        _collapsed.Clear();
        _selectedName = null;
        Rebuild();
    }

    /// <summary>Empties the list (a repository was closed, a load failed).</summary>
    public void Clear() => SetFiles([]);

    // ------------------------------------------------------------- translation

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private void ApplyTranslations()
    {
        ToolTip.SetTip(_collapseGroupsButton, T(
            "FileStatusList/btnCollapseGroups.ToolTipText",
            "Collapse all groups, otherwise expand the selected group"));
        ToolTip.SetTip(_refreshButton, T("FileStatusList/btnRefresh.ToolTipText", "Refresh artificial commit"));
        ToolTip.SetTip(_asTreeButton, T("FileStatusList/btnAsTree.ToolTipText", "Toggle flat list / tree"));
        ToolTip.SetTip(_groupMenuButton, T("FileStatusList/_sortByContextMenu.Text", "Sort and group by"));
        ToolTip.SetTip(_byPathButton, T("FileStatusList/btnByPath.ToolTipText", "Group by file path"));
        ToolTip.SetTip(_byExtensionButton,
            T("FileStatusList/btnByExtension.ToolTipText", "Group by file type (extension)"));
        ToolTip.SetTip(_byStatusButton, T("FileStatusList/btnByStatus.ToolTipText", "Group by diff status"));

        _filterBox.Watermark = T(
            "FileStatusList/cboFilterComboBox.Watermark", "Filter files using a regular expression...");
        ToolTip.SetTip(_filterClearButton, T("Clear the filter"));

        UpdateFilterFeedback();
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        ApplyTranslations();

        // The group headers carry translated words (the status names, the counts),
        // so they have to be rebuilt rather than re-labelled.
        Rebuild();
    });

    // --------------------------------------------------------------- rows

    private Control BuildRow(object? item) => item switch
    {
        FileListGroupNode group => BuildGroupRow(group),
        FileListFileNode file => BuildFileRow(file),
        _ => new TextBlock(),
    };

    private static Control BuildGroupRow(FileListGroupNode group)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(group.Level * 12, 0, 0, 0),
        };

        panel.Children.Add(new TextBlock
        {
            Text = group.IsCollapsed ? "▸" : "▾",
            FontSize = 10,
            Foreground = B("App.TextDim"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = group.Header,
            FontWeight = FontWeight.Bold,
            Foreground = B("App.TextDim"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    // A changed-file row: a coloured status glyph (M/A/D/R/C) followed by the path.
    private static Control BuildFileRow(FileListFileNode node)
    {
        (char glyph, IBrush glyphBrush) = node.Row.Kind switch
        {
            DiffChangeKind.Added => ('A', AddedGlyph),
            DiffChangeKind.Deleted => ('D', DeletedGlyph),
            DiffChangeKind.Renamed => ('R', ModifiedGlyph),
            DiffChangeKind.Copied => ('C', ModifiedGlyph),
            _ => ('M', ModifiedGlyph),
        };

        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(node.Level * 12, 0, 0, 0),
        };
        panel.Children.Add(new TextBlock
        {
            Text = glyph.ToString(),
            Foreground = glyphBrush,
            FontFamily = Monospace,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = node.Display,
            Foreground = B("App.Text"),
            FontFamily = Monospace,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    // --------------------------------------------------------------- selection

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (_list.SelectedItem is FileListGroupNode group)
        {
            // A header is a control, not a destination: clicking it folds the group
            // and the file selection (and the loaded diff) stays where it was.
            if (!_collapsed.Remove(group.Key))
            {
                _collapsed.Add(group.Key);
            }

            Rebuild();
            return;
        }

        if (_list.SelectedItem is not FileListFileNode node)
        {
            return;
        }

        _selectedName = node.Row.Name;
        SelectedFileChanged?.Invoke(node.Row);
    }

    // Rebuilds the view rows (always a new list instance) and puts the selection
    // back on the same file, so filtering or collapsing never reloads a diff.
    private void Rebuild()
    {
        DiffFileGroupMode mode = _options.GroupMode;

        (List<object> items, int fileCount) = DiffFileListBuilder.Build(
            _files,
            _filter,
            mode,
            _options.AsTree,
            GrouperFor(mode),
            _collapsed,
            (header, count) => F("{0}  ({1})", header, count));

        _suppressSelection = true;
        _list.ItemsSource = items;

        FileListFileNode? target = null;
        FileListFileNode? first = null;
        foreach (object item in items)
        {
            if (item is not FileListFileNode file)
            {
                continue;
            }

            first ??= file;
            if (file.Row.Name == _selectedName)
            {
                target = file;
                break;
            }
        }

        target ??= first;
        _list.SelectedItem = target;
        _suppressSelection = false;

        UpdateFilterCount(fileCount);
        UpdateToolbarState(items);

        // Only tell the host when the selection really moved: re-grouping or
        // filtering must not re-run the diff of the same file.
        string? newName = target?.Row.Name;
        if (newName != _selectedName)
        {
            _selectedName = newName;
            SelectedFileChanged?.Invoke(target?.Row);
        }
    }

    private static Func<DiffFileRow, DiffFileListBuilder.GroupLabel>? GrouperFor(DiffFileGroupMode mode) => mode switch
    {
        DiffFileGroupMode.Path => DiffFileListBuilder.PathGroupLabel,
        DiffFileGroupMode.Extension => ExtensionLabel,
        DiffFileGroupMode.Status => StatusLabel,
        _ => null,
    };

    private static DiffFileListBuilder.GroupLabel ExtensionLabel(DiffFileRow row)
    {
        int dot = row.Name.LastIndexOf('.');
        int slash = row.Name.LastIndexOf('/');

        // "no extension" is its own group, as upstream: a dot in a directory name
        // (or a leading dot, i.e. a dotfile) is not an extension.
        string ext = dot > slash + 1 ? row.Name[dot..] : string.Empty;

        return ext.Length == 0
            ? new DiffFileListBuilder.GroupLabel(" none", T("(no extension)"))
            : new DiffFileListBuilder.GroupLabel(ext.ToLowerInvariant(), "*" + ext.ToLowerInvariant());
    }

    private static DiffFileListBuilder.GroupLabel StatusLabel(DiffFileRow row)
    {
        // The catalogues have no id for the bare status words (upstream draws them
        // as icons), so these go through the source-text lookup and fall back to
        // English. The key prefix keeps the groups in change-kind order.
        (string key, string label) = row.Kind switch
        {
            DiffChangeKind.Added => ("1added", T("Added")),
            DiffChangeKind.Deleted => ("3deleted", T("Deleted")),
            DiffChangeKind.Renamed => ("4renamed", T("Renamed")),
            DiffChangeKind.Copied => ("5copied", T("Copied")),
            _ => ("2modified", T("Modified")),
        };

        return new DiffFileListBuilder.GroupLabel(key, label);
    }

    // --------------------------------------------------------------- filtering

    private void RestartFilterDebounce()
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            _filterDebounce.Stop();
            ApplyFilter();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && (_filterBox.Text ?? string.Empty).Length > 0)
        {
            _filterBox.Text = string.Empty;
            _filterDebounce.Stop();
            ApplyFilter();
            e.Handled = true;
        }
    }

    private void ApplyFilter()
    {
        DiffFileFilter parsed = DiffFileFilter.Parse(_filterBox.Text);
        _filter = parsed;
        UpdateFilterFeedback();
        Rebuild();
    }

    // A bad pattern is reported on the box itself (red border + the parser's
    // message as a tooltip); the text still filters, as a literal.
    private void UpdateFilterFeedback()
    {
        if (_filter.Error is null)
        {
            _filterBox.Background = B("App.Panel");
            _filterCount.Foreground = B("App.TextDim");
            ToolTip.SetTip(_filterBox, T(
                "FileStatusList/cboFilterComboBox.Watermark", "Filter files using a regular expression..."));
            return;
        }

        _filterBox.Background = InvalidFilterBackground;
        _filterCount.Foreground = InvalidFilterBrush;
        ToolTip.SetTip(_filterBox, F(
            "{0}: {1}\n{2}",
            T("FileStatusList/FilterToolTip.ToolTipTitle", "RegEx"),
            _filter.Error,
            T("Filtering as plain text instead.")));
    }

    private void UpdateFilterCount(int shown)
    {
        if (!_filter.IsActive)
        {
            _filterCount.Text = _files.Count == 0
                ? string.Empty
                : F("{0}", _files.Count);
            return;
        }

        // The warning sign says "this is not a valid regex, you are getting a
        // literal match"; the message itself is the box's tooltip.
        _filterCount.Text = _filter.Error is null
            ? F("{0}/{1}", shown, _files.Count)
            : F("⚠ {0}/{1}", shown, _files.Count);
    }

    // --------------------------------------------------------------- toolbar

    private void UpdateToolbarState(List<object> items)
    {
        bool grouped = _options.GroupMode != DiffFileGroupMode.None;
        bool hasGroups = grouped && items.Exists(static i => i is FileListGroupNode);

        // Upstream hides the collapse button (and the flat/tree switch is only
        // meaningful for the path grouping) when there is nothing to fold.
        _collapseGroupsButton.IsEnabled = hasGroups;
        _asTreeButton.IsEnabled = _options.GroupMode == DiffFileGroupMode.Path;
    }

    private void CollapseOrExpandGroups()
    {
        // Dual-purpose, like the upstream button: fold everything, or unfold
        // everything when nothing is left to fold.
        if (_list.ItemsSource is not IEnumerable<object> items)
        {
            return;
        }

        List<string> keys = [];
        bool anyExpanded = false;
        foreach (object item in items)
        {
            if (item is FileListGroupNode group)
            {
                keys.Add(group.Key);
                anyExpanded |= !group.IsCollapsed;
            }
        }

        if (anyExpanded)
        {
            foreach (string key in keys)
            {
                _collapsed.Add(key);
            }
        }
        else
        {
            _collapsed.Clear();
        }

        Rebuild();
    }

    private void ToggleTree()
    {
        _options.AsTree = !_options.AsTree;
        SyncGroupButtons();
        Rebuild();
    }

    private void SetGroupMode(DiffFileGroupMode mode)
    {
        _options.GroupMode = mode;
        _collapsed.Clear();
        SyncGroupButtons();
        Rebuild();
    }

    // Mirrors the option state onto the three radio-like toggles and the split
    // button's icon, without letting IsChecked feed back into the handlers.
    private void SyncGroupButtons()
    {
        _updatingGroupButtons = true;
        _byPathButton.IsChecked = _options.GroupMode == DiffFileGroupMode.Path;
        _byExtensionButton.IsChecked = _options.GroupMode == DiffFileGroupMode.Extension;
        _byStatusButton.IsChecked = _options.GroupMode == DiffFileGroupMode.Status;
        _updatingGroupButtons = false;

        // The upstream split button swaps its own image between the tree and the
        // flat-list icon; ours does the same when both icons resolve.
        Image? icon = IconLoader.Image(_options.AsTree ? "FileTree" : "DocumentTree", 16);
        if (icon is not null && _asTreeIcon is not null)
        {
            _asTreeButton.Content = icon;
            _asTreeIcon = icon;
        }
    }

    // The group-by dropdown: the six upstream sort types plus "no grouping",
    // which the port needs because its default list is a plain path list.
    // Items are built in full before ShowAt (a flyout mutated from Opening
    // mis-measures).
    private void ShowGroupMenu(Control anchor)
    {
        MenuItem Mode(string key, string english, DiffFileGroupMode mode, bool tree)
        {
            MenuItem item = new()
            {
                Header = T(key, english),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _options.GroupMode == mode &&
                            (mode != DiffFileGroupMode.Path || _options.AsTree == tree),
            };

            item.Click += (_, _) =>
            {
                _options.AsTree = tree;
                SetGroupMode(mode);
            };

            return item;
        }

        MenuItem none = new()
        {
            Header = T("No grouping"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.GroupMode == DiffFileGroupMode.None,
        };
        none.Click += (_, _) => SetGroupMode(DiffFileGroupMode.None);

        MenuFlyout flyout = new()
        {
            ItemsSource = new Control[]
            {
                none,
                new Separator(),
                Mode("FileStatusList/tsmiGroupByFilePathTree.Text", "Group by file path - tree",
                    DiffFileGroupMode.Path, tree: true),
                Mode("FileStatusList/tsmiGroupByFilePathFlat.Text", "Group by file path - flat",
                    DiffFileGroupMode.Path, tree: false),
                Mode("FileStatusList/tsmiGroupByFileExtensionFlat.Text", "Group by file extension - flat",
                    DiffFileGroupMode.Extension, tree: false),
                Mode("FileStatusList/tsmiGroupByFileStatusFlat.Text", "Group by file status - flat",
                    DiffFileGroupMode.Status, tree: false),
            },
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        flyout.ShowAt(anchor);
    }

    // ----------------------------------------------------------- toolbar chrome

    // Flat toolbar chrome: the Fluent templates paint a button's background
    // through their inner ContentPresenter, so style that part directly.
    private void AddToolbarStyles()
    {
        IBrush hover = B("App.PanelAlt");
        IBrush border = B("App.Border");
        IBrush selection = B("App.Selection");

        void Chrome<T>(string[] pseudo, IBrush background, IBrush stroke)
            where T : TemplatedControl =>
            Styles.Add(new Style(x =>
            {
                Selector s = x.OfType<T>().Class("filestatustool");
                foreach (string cls in pseudo)
                {
                    s = s.Class(cls);
                }

                return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, background),
                    new Setter(ContentPresenter.BorderBrushProperty, stroke),
                    new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(3)),
                },
            });

        Chrome<Button>([], Brushes.Transparent, Brushes.Transparent);
        Chrome<Button>([":pointerover"], hover, border);
        Chrome<ToggleButton>([], Brushes.Transparent, Brushes.Transparent);
        Chrome<ToggleButton>([":pointerover"], hover, border);
        Chrome<ToggleButton>([":checked"], selection, B("App.Accent"));
        Chrome<ToggleButton>([":checked", ":pointerover"], selection, B("App.Accent"));
    }

    // An icon button that degrades to a glyph when the icon is missing from the
    // reused Windows resources.
    private Button IconButton(string? icon, string glyph, Action onClick)
    {
        Button button = new()
        {
            Content = Face(icon, glyph),
            Padding = new Thickness(4, 2),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("filestatustool");
        button.Click += (_, _) => onClick();

        return button;
    }

    private ToggleButton IconToggle(string? icon, string glyph, DiffFileGroupMode mode)
    {
        ToggleButton button = new()
        {
            Content = Face(icon, glyph),
            Padding = new Thickness(4, 2),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("filestatustool");

        button.IsCheckedChanged += (_, _) =>
        {
            if (_updatingGroupButtons)
            {
                return;
            }

            // Radio-like: checking one clears the others, unchecking the active
            // one goes back to the plain list.
            SetGroupMode(button.IsChecked == true ? mode : DiffFileGroupMode.None);
        };

        return button;
    }

    private static Control Face(string? icon, string glyph)
    {
        if (icon is not null && IconLoader.Image(icon, 16) is Image image)
        {
            image.VerticalAlignment = VerticalAlignment.Center;
            return image;
        }

        return new TextBlock
        {
            Text = glyph,
            FontSize = 12,
            Foreground = B("App.Text"),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Control ToolSeparator() => new Border
    {
        Width = 1,
        Margin = new Thickness(3, 4),
        Background = B("App.Border"),
    };
}
