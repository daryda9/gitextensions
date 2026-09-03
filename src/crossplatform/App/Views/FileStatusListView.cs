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
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Session-wide state of the changed-files lists, so the grouping a user picks
///  in one place is the grouping every list uses — the equivalent of the
///  upstream <c>DiffListSortService</c> singleton plus its
///  <c>AppSettings</c> entries.
/// </summary>
public sealed class FileStatusListOptions
{
    /// <summary>
    ///  The one instance shared by every <see cref="FileStatusListView"/> — and the
    ///  only one that is file-backed: it opens with the grouping the user last chose
    ///  (<see cref="FileListPrefs.Diff"/>) and writes it back on every change. An
    ///  instance a host builds for itself (the File-tree tab) is deliberately NOT
    ///  persisted: its grouping is part of what that surface IS, not a user choice.
    /// </summary>
    public static FileStatusListOptions Session { get; } = Restore();

    /// <summary>Which grouping the lists apply.</summary>
    public DiffFileGroupMode GroupMode { get; set; } = DiffFileGroupMode.None;

    /// <summary>Whether the path grouping nests its directories.</summary>
    public bool AsTree { get; set; } = true;

    // False on a host's own options object, so only the session choice reaches the file.
    private bool _persisted;

    /// <summary>
    ///  Saves the current grouping, when this is the session instance. Called by the
    ///  two places that change it (<c>SetGroupMode</c> and <c>ToggleTree</c>), through
    ///  <see cref="ViewPrefsService.Update"/> so it cannot revert a group another
    ///  surface wrote meanwhile.
    /// </summary>
    public void Remember()
    {
        if (!_persisted)
        {
            return;
        }

        FileListGrouping grouping = new() { Group = GroupMode, AsTree = AsTree };
        new ViewPrefsService().Update(p => p.FileList.Diff = grouping);
    }

    private static FileStatusListOptions Restore()
    {
        FileListGrouping stored = new ViewPrefsService().Load().FileList.Diff;
        return new FileStatusListOptions
        {
            GroupMode = stored.Group,
            AsTree = stored.AsTree,
            _persisted = true,
        };
    }
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
///
///  <para><b>Find in commit files (git grep).</b> A host that shows ONE revision sets
///  <see cref="CanFindInFiles"/>; the toolbar then offers upstream's
///  <c>btnFindInFilesGitGrep</c>, which opens an input box above the filter row and
///  reports every change of it through <see cref="FindInFilesRequested"/>. The
///  control runs no git of its own — the host answers with
///  <see cref="SetSearchResults"/>, and the hits become one extra section under the
///  diff's own. What upstream has here and this does not: the separate
///  <c>FormFindInCommitFilesGitGrep</c> window and the <c>tsmiFindUsingDialog</c> /
///  <c>tsmiFindUsingInputBox</c> / <c>tsmiFindUsingBoth</c> choice between the two
///  (the port has only the inline box, so there is nothing to choose), and the
///  free-text <c>tsmiFindUsingOptions</c> item that appends raw arguments to
///  <c>git grep</c> (a settings field, not a search control — and one whose value
///  would silently change every search made from here).</para>
/// </summary>
public sealed class FileStatusListView : UserControl
{
    // A property, not a field: a static field initialiser can run before the font
    // manager exists, which would cache the fallback for the life of the process.
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

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

    private readonly FileStatusListOptions _options;

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

    // ---- "Find in commit files using git-grep" (upstream btnFindInFilesGitGrep) ----
    private readonly Border _findSplit;
    private readonly Button _findButton;
    private readonly Button _findMenuButton;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly Button _findClearButton;
    private readonly DispatcherTimer _findDebounce;

    // The sections the list shows, in order — upstream's IReadOnlyList<
    // FileStatusWithDescription>. A list that is not a comparison (the file tree, the
    // commit dialog's staged/unstaged lists, which say what they are in their own
    // captions) is ONE section with an empty summary, which is why there is no second
    // path through this control: the single-section list is the multi-section one
    // with a single, unlabelled section.
    private IReadOnlyList<DiffFileGroup> _groups = [];

    // The sections the HOST supplied, and the search section, kept apart so that
    // reloading the diff does not drop the search results and re-running the search
    // does not disturb the diff — upstream's two independent halves of
    // FileStatusDiffCalculator.Calculate(refreshDiff:, refreshGrep:). _groups is
    // always the concatenation, search last.
    private IReadOnlyList<DiffFileGroup> _hostGroups = [];
    private DiffFileGroup? _searchGroup;

    // Every row of every section, flattened, for the hosts that ask what is loaded
    // and for the "shown of total" filter counter. Rebuilt with _groups, never alone.
    private IReadOnlyList<DiffFileRow> _files = [];

    /// <summary>Identity of a section header row; not a grouping key of the
    /// builder, so it cannot collide with a path/extension/status group.</summary>
    private const string SummaryKey = "\u0000summary";
    private DiffFileFilter _filter = DiffFileFilter.None;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    // The row that must stay selected across a rebuild (filter change, collapse,
    // reload), so re-grouping does not reload another diff. Held as the row OBJECT
    // and matched by reference, not by path: the same file legitimately appears in
    // several sections of a multi-revision comparison, and matching by name would
    // move the selection into whichever section happens to come first — silently
    // showing the patch of a comparison the user did not click on.
    private DiffFileRow? _selectedRow;
    private bool _suppressSelection;
    private bool _updatingGroupButtons;

    /// <summary>
    ///  Builds the list. <paramref name="options"/> is the grouping state the list
    ///  obeys: leave it out to share <see cref="FileStatusListOptions.Session"/>
    ///  with every other changed-files list (the diff pane, the commit dialog),
    ///  or pass an own instance for a list whose grouping is not the user's
    ///  session choice — the File-tree tab, which is always a path tree and
    ///  upstream shows no grouping toolbar at all
    ///  (<c>FileStatusList.Bind</c>, <c>Toolbar.Visible = false</c> when
    ///  <c>isFileTreeMode</c>).
    /// </summary>
    public FileStatusListView(FileStatusListOptions? options = null)
    {
        _options = options ?? FileStatusListOptions.Session;

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

        // The shared bar look (Theming/BarButtonStyles), not the private copy this
        // view carried: that copy hovered on App.PanelAlt and drew a checked toggle as
        // an App.Selection box outlined in App.Accent, so the three grouping buttons
        // were saturated blue while the identical buttons on the main toolbar were not.
        Theming.BarButtonStyles.Apply(Styles);

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
            Background = B("App.Rule"),
        });
        splitContent.Children.Add(_groupMenuButton);

        // No box around the pair: the main toolbar's own split buttons (MainToolbar's
        // shell and push hosts) are a transparent host holding body, divider and arrow,
        // and these two were the only ones drawing a permanent outlined rectangle —
        // two boxes in a row of otherwise bare icons.
        _asTreeSplit = new Border
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = splitContent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0),
        };

        // The git-grep split button: the body opens/closes the input box, the arrow
        // opens the two matching options — upstream's ToolStripSplitButton with
        // tsmiFindUsingMatchCase / tsmiFindUsingWholeWord under it.
        _findButton = IconButton("ViewFile", "⌕", ToggleFindBox);
        _findMenuButton = IconButton(null, "▾", () => ShowFindMenu(_findMenuButton!));
        _findMenuButton.Padding = new Thickness(2, 2);

        StackPanel findSplitContent = new() { Orientation = Orientation.Horizontal };
        findSplitContent.Children.Add(_findButton);
        findSplitContent.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(0, 4),
            Background = B("App.Rule"),
        });
        findSplitContent.Children.Add(_findMenuButton);

        _findSplit = new Border
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Child = findSplitContent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0),

            // Off until a host declares it can search — upstream's
            // CanUseFindInCommitFilesGitGrep, which is false for the lists that have
            // no single revision to grep (the commit dialog's staged/unstaged views).
            IsVisible = false,
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
        _toolbar.Children.Add(_findSplit);

        // No closing line of its own. The three bars stacked here — this one, the find
        // row and the filter row — all paint App.Toolbar, so a rule between them divides
        // one surface from itself; and the filter row's TextBox carries its own outline
        // 3 px below, which is what put two parallel hairlines a hair apart under this
        // strip. The filter row, which is always visible, closes the stack for all three.
        _toolbarBar = new Border
        {
            Background = B("App.Toolbar"),
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
            BorderBrush = B("App.Rule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = filterRow,
        };

        // ---- git-grep search row (above the filter row, as upstream stacks them) ----
        _findBox = new TextBox
        {
            FontSize = 12,
            MinHeight = 0,
            Padding = new Thickness(6, 2, 6, 2),
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,

            // Bold, like upstream's box: the search is a second, stronger filter over
            // the same list and must not be mistaken for the regex filter below it.
            FontWeight = FontWeight.Bold,
        };
        _findBox.TextChanged += (_, _) => RestartFindDebounce();
        _findBox.KeyDown += OnFindKeyDown;

        _findClearButton = IconButton("DeleteText", "✕", () =>
        {
            _findBox.Text = string.Empty;
            RunFind();
        });

        Grid findRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(4, 3, 4, 2),
        };
        Grid.SetColumn(_findBox, 0);
        Grid.SetColumn(_findClearButton, 1);
        findRow.Children.Add(_findBox);
        findRow.Children.Add(_findClearButton);

        // Same as _toolbarBar: no rule of its own, the filter row below closes the stack.
        _findBar = new Border
        {
            Background = B("App.Toolbar"),
            Child = findRow,
            IsVisible = false,
        };

        // Each keystroke restarts a git-grep over a whole revision, so the box waits
        // longer than the path filter above (which only re-groups rows already held).
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce.Stop();
            RunFind();
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
        DockPanel.SetDock(_findBar, Dock.Top);
        DockPanel.SetDock(filterBar, Dock.Top);
        root.Children.Add(_toolbarBar);
        root.Children.Add(_findBar);
        root.Children.Add(filterBar);
        root.Children.Add(_list);

        Content = root;

        SyncGroupButtons();
        ApplyTranslations();
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

    /// <summary>
    ///  Every selected file, in list order, with group headers skipped. The list is
    ///  single-selection today, so this yields at most one row; commands that operate
    ///  on "the selection" read it through here so they keep working unchanged if the
    ///  list is ever switched to <see cref="SelectionMode.Multiple"/>.
    /// </summary>
    public IReadOnlyList<DiffFileRow> SelectedFiles =>
        _list.SelectedItems is null
            ? []
            : [.. _list.SelectedItems.OfType<FileListFileNode>().Select(n => n.Row)];

    /// <summary>
    ///  Moves the selection to the next FILE below the current one, skipping group
    ///  headers and folder nodes, and returns whether it moved. Used by the diff pane's
    ///  continuous scroll, which walks the list from the bottom of a patch.
    ///
    ///  <para>Deliberately silent at the end of the list: the last file's patch is where
    ///  the walk stops, and wrapping round to the first file would send the reader back
    ///  to a patch they already scrolled through.</para>
    /// </summary>
    public bool SelectNextFile()
    {
        int from = _list.SelectedIndex;
        for (int i = from + 1; i < _list.ItemCount; i++)
        {
            if (_list.Items[i] is FileListFileNode)
            {
                _list.SelectedIndex = i;
                _list.ScrollIntoView(i);
                return true;
            }
        }

        return false;
    }

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

    // ------------------------------------------------- find in files (git grep)

    /// <summary>
    ///  Whether this list can search the revision it shows — upstream's
    ///  <c>CanUseFindInCommitFilesGitGrep</c>. Off by default: a list has to be about
    ///  ONE revision for <c>git grep</c> to have something to run over, which the
    ///  staged/unstaged lists of the commit dialog are not.
    ///
    ///  <para>Turning it on reveals the toolbar's search button and reopens the input
    ///  box if the user left it open last time (persisted in
    ///  <see cref="FindInFilesPrefs"/>); turning it off hides both and clears the
    ///  results, because nothing would be able to refresh them.</para>
    /// </summary>
    public bool CanFindInFiles
    {
        get => _findSplit.IsVisible;
        set
        {
            if (_findSplit.IsVisible == value)
            {
                return;
            }

            _findSplit.IsVisible = value;
            if (value)
            {
                ShowFindBox(FindPrefs.Show);
                return;
            }

            ShowFindBox(show: false, persist: false);
            SetSearchResults(null);
        }
    }

    /// <summary>
    ///  Raised whenever the search should be re-run: the user typed (debounced),
    ///  pressed Enter, toggled match-case or whole-word, or closed the box. The host
    ///  runs it off the UI thread and answers with <see cref="SetSearchResults"/>.
    ///
    ///  <para>An INACTIVE query (empty box, or the box closed) is reported too, and
    ///  means "drop the results": the caller does not have to distinguish "cleared"
    ///  from "never searched".</para>
    /// </summary>
    public event Action<GitGrepQuery>? FindInFilesRequested;

    /// <summary>What the search box currently asks for; inactive when it is closed.</summary>
    public GitGrepQuery FindQuery =>
        _findBar.IsVisible
            ? new GitGrepQuery(_findBox.Text ?? string.Empty, FindPrefs.MatchCase, FindPrefs.WholeWord)
            : GitGrepQuery.None;

    /// <summary>
    ///  Opens the search box and puts the caret in it — the port of upstream's
    ///  <c>tsmiShowFindInCommitFilesGitGrep</c> / the Ctrl+F route into the list,
    ///  for a host that offers the command from a menu of its own.
    /// </summary>
    public void FocusFindInFiles()
    {
        if (!CanFindInFiles)
        {
            return;
        }

        ShowFindBox(show: true);
        _findBox.Focus();
        _findBox.SelectAll();
    }

    /// <summary>
    ///  Replaces the loaded rows and selects the first one, raising
    ///  <see cref="SelectedFileChanged"/> exactly once for the new selection.
    /// </summary>
    public void SetFiles(IReadOnlyList<DiffFileRow> rows) => SetFiles(rows, summary: null);

    /// <summary>
    ///  As <see cref="SetFiles(IReadOnlyList{DiffFileRow})"/>, with the comparison
    ///  the rows come from shown as a header row above them — the port of upstream's
    ///  group header, which names the "A" side of every diff the pane shows
    ///  (<c>(N)  Diff with A 1a2b3c4d: subject</c>). Passing <see langword="null"/>
    ///  or an empty string leaves the list headerless, as it was.
    /// </summary>
    public void SetFiles(IReadOnlyList<DiffFileRow> rows, string? summary)
        => SetFiles([new DiffFileGroup(summary ?? string.Empty, rows)]);

    /// <summary>
    ///  Replaces the loaded rows with SEVERAL sections, each with its own collapsible
    ///  header — what a multi-revision selection produces
    ///  (<see cref="DiffService.GetSelectionDiffGroups"/>): "Diff with A …",
    ///  "Diff BASE with B …", "Diff BASE with A …". The single-section overloads are
    ///  this one with a one-element list, so there is exactly one way a list is
    ///  filled.
    ///
    ///  <para>Selection lands on the first file of the first section and is reported
    ///  once, as for a single section. Sections keep their own collapsed state: the
    ///  grouping keys of one cannot fold a same-named group in another.</para>
    /// </summary>
    public void SetFiles(IReadOnlyList<DiffFileGroup> groups)
    {
        _hostGroups = groups;
        ComposeSections();
        _collapsed.Clear();
        _selectedRow = null;
        ApplyInitialCollapse();
        Rebuild();
    }

    /// <summary>
    ///  Replaces the <c>git grep</c> section — the extra section the search box adds
    ///  BELOW whatever the host loaded — with <paramref name="group"/>, or removes it
    ///  when that is <see langword="null"/>. Its caption must start with
    ///  <see cref="GitGrepService.SummaryPrefix"/>; that prefix is what makes its rows
    ///  read as hits rather than as changes.
    ///
    ///  <para>Unlike <see cref="SetFiles(IReadOnlyList{DiffFileGroup})"/> this keeps
    ///  the selection and the folded groups: a search that finishes while the user is
    ///  reading a patch must not move them off it, and the two halves are independent
    ///  exactly as upstream's <c>refreshDiff</c> / <c>refreshGrep</c> are.</para>
    /// </summary>
    public void SetSearchResults(DiffFileGroup? group)
    {
        _searchGroup = group;
        ComposeSections();
        Rebuild();
    }

    // _groups (what the list draws) is always the host's sections followed by the
    // search section, and _files the flattening of both — so the filter counter and
    // the hosts that ask what is loaded see the hits too.
    private void ComposeSections()
    {
        _groups = _searchGroup is null ? _hostGroups : [.. _hostGroups, _searchGroup];
        _files = _groups.Count == 1 ? _groups[0].Rows : [.. _groups.SelectMany(g => g.Rows)];
    }

    /// <summary>
    ///  Whether a fresh set of rows starts with every group folded. Upstream only
    ///  auto-expands outside file-tree mode
    ///  (<c>expandIfFewFiles = !_isFileTreeMode || _filter is not null</c>), so a
    ///  whole-tree listing opens on its root folders.
    /// </summary>
    public bool CollapseGroupsOnLoad { get; set; }

    // Folds everything on load, unless a filter is active: filtering is a search,
    // and a search must show its hits (the upstream rule above).
    private void ApplyInitialCollapse()
    {
        if (!CollapseGroupsOnLoad)
        {
            return;
        }

        _collapsed.Clear();
        if (_filter.IsActive)
        {
            return;
        }

        foreach (string key in AllGroupKeys())
        {
            _collapsed.Add(key);
        }
    }

    /// <summary>
    ///  Empties the list (a repository was closed, a load failed). The search RESULTS
    ///  go with it — they describe a revision that is no longer on screen — while the
    ///  search BOX and its text stay, so the host can re-run them against whatever it
    ///  loads next.
    /// </summary>
    public void Clear()
    {
        _searchGroup = null;
        SetFiles(groups: []);
    }

    /// <summary>
    ///  Whether the grouping toolbar is shown. Upstream hides the whole toolbar in
    ///  file-tree mode (<c>FileStatusList.Bind</c>) and keeps only the filter box.
    /// </summary>
    public bool ShowToolbar
    {
        get => _toolbarBar.IsVisible;
        set => _toolbarBar.IsVisible = value;
    }

    /// <summary>
    ///  Whether a file row carries the coloured M/A/D/R/C status glyph. Off for a
    ///  list that shows a commit's whole tree, where there is no change to report.
    /// </summary>
    public bool ShowStatusGlyphs { get; set; } = true;

    /// <summary>Whether the current grouping produced any group header.</summary>
    public bool HasGroups => AllGroupKeys().Count > 0;

    /// <summary>Applies a grouping to this list (and to whatever shares its options).</summary>
    public void SetGrouping(DiffFileGroupMode mode, bool asTree)
    {
        _options.AsTree = asTree;
        SetGroupMode(mode);
    }

    /// <summary>Folds every group, at every level (the tree context menu's "Collapse all").</summary>
    public void CollapseAllGroups()
    {
        foreach (string key in AllGroupKeys())
        {
            _collapsed.Add(key);
        }

        Rebuild();
    }

    /// <summary>Unfolds every group (the tree context menu's "Expand all").</summary>
    public void ExpandAllGroups()
    {
        _collapsed.Clear();
        Rebuild();
    }

    /// <summary>
    ///  Folds the top-level folders only, leaving the state of their children
    ///  alone — the tree context menu's "Collapse root folders", which upstream
    ///  offers in file-tree mode only.
    /// </summary>
    public void CollapseRootFolders()
    {
        foreach (string key in AllGroupKeys(maxLevel: 0))
        {
            _collapsed.Add(key);
        }

        Rebuild();
    }

    // Every group key the current rows/filter/grouping produce, including the ones
    // hidden inside a folded parent: built from a throw-away expanded layout,
    // because the visible items only carry the keys that are on screen.
    private List<string> AllGroupKeys(int? maxLevel = null)
    {
        DiffFileGroupMode mode = _options.GroupMode;
        List<string> keys = [];

        for (int i = 0; i < _groups.Count; i++)
        {
            (List<object> items, _) = DiffFileListBuilder.Build(
                _groups[i].Rows,
                _filter,
                mode,
                _options.AsTree,
                GrouperFor(mode),
                new HashSet<string>(StringComparer.Ordinal),
                static (header, _) => header);

            foreach (object item in items)
            {
                if (item is FileListGroupNode group && (maxLevel is null || group.Level <= maxLevel))
                {
                    keys.Add(SectionPrefix(i) + group.Key);
                }
            }
        }

        return keys;

        // The section headers themselves are NOT in this list, and that is the point:
        // "collapse on load" and "collapse all groups" fold the file groups, while
        // folding the sections would hide the very captions that say what the list is
        // comparing — and, with a single unlabelled section, would empty the pane.
    }

    // Namespaces one section's grouping keys, so "src/" in the BASE→A section and
    // "src/" in the BASE→B section fold independently. NUL cannot occur in a path,
    // an extension or a translated status word, so no real key can forge a prefix.
    private static string SectionPrefix(int index) => $"\u0000s{index}\u0000";

    // ------------------------------------------------------------- translation

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    // The subscription follows the visual tree rather than being taken once in the
    // constructor: this control is created per pane and thrown away with it (a closed
    // tab, a rebuilt layout, a repository switch), and a list left hanging off the
    // static LanguageChanged event keeps its rows, its toolbar and its host alive for
    // the lifetime of the process.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TranslationService.LanguageChanged += OnLanguageChanged;

        // A language switch that happened while this list was detached raised no event
        // here, so the state is re-stated on the way back in — that is what makes
        // unsubscribing on detach safe rather than lossy.
        Relabel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TranslationService.LanguageChanged -= OnLanguageChanged;
    }

    // The group headers carry translated words (the status names) and the counts, so
    // they have to be rebuilt rather than re-labelled.
    private void Relabel()
    {
        ApplyTranslations();
        Rebuild();
    }

    // The rich per-row context menu is not built here: the hosts attach their own to
    // List (FileTreeView, DiffView, the commit dialog), and each of them translates
    // its items itself. The one menu this control owns is the group-by flyout, which
    // is rebuilt from scratch on every open by ShowGroupMenu — so its items only have
    // to go through T() at build time, and there is nothing for ApplyTranslations to
    // re-label.
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

        ToolTip.SetTip(_findButton, T(
            "FileStatusList/btnFindInFilesGitGrep.ToolTipText",
            "Toggle 'Find in commit files using git-grep'"));
        ToolTip.SetTip(_findMenuButton, T("FileStatusList/tsmiFindUsingOptions.Text", "Options"));
        _findBox.Watermark = T(
            "FileStatusList/cboFindInCommitFilesGitGrep.Watermark",
            "Find in commit files using git-grep regular expression...");
        ToolTip.SetTip(_findClearButton, T("Clear the filter"));

        UpdateFilterFeedback();
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Relabel);

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
    // In a plain tree (ShowStatusGlyphs off) there is no change to report — every
    // file of a commit's tree is simply tracked — so the glyph is left out rather
    // than shown as a meaningless "M".
    private Control BuildFileRow(FileListFileNode node)
    {
        // A search hit keeps the glyph COLUMN, so its path lines up with the changed
        // files above it, but says "found in" rather than claiming a modification —
        // upstream swaps the status icon for Images.ViewFile in the same place.
        if (node.IsSearchHit)
        {
            return BuildGlyphFileRow(node, "⌕", B("App.TextDim"));
        }

        if (!ShowStatusGlyphs)
        {
            return new TextBlock
            {
                Text = node.Display,
                Foreground = B("App.Text"),
                FontFamily = Monospace,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(node.Level * 12, 0, 0, 0),
            };
        }

        return BuildStatusFileRow(node);
    }

    // A path preceded by one monospace character in the status column.
    private static Control BuildGlyphFileRow(FileListFileNode node, string glyph, IBrush glyphBrush)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(node.Level * 12, 0, 0, 0),
        };

        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            Foreground = glyphBrush,
            FontFamily = Monospace,
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

    private static Control BuildStatusFileRow(FileListFileNode node)
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

        // Where the change was made, relative to the merge base of a two-branch
        // comparison. Upstream overlays a small A/B/=/≠ badge on the status icon; the
        // port has no such composed icons, so the same four states are written as one
        // monospace character in their own column — which also keeps the paths of the
        // three sections aligned with each other.
        if (BranchMarker(node.Row.BranchStatus) is (string marker, IBrush markerBrush))
        {
            panel.Children.Add(new TextBlock
            {
                Text = marker,
                Foreground = markerBrush,
                FontFamily = Monospace,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

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

    // The four upstream DiffBranchStatus values as a character and a colour, or null
    // for a row that is not part of a base-with-A/base-with-B comparison (every
    // ordinary list), which then looks exactly as it always did.
    private static (string Marker, IBrush Brush)? BranchMarker(DiffBranchStatus status) => status switch
    {
        DiffBranchStatus.SameChange => ("=", B("App.TextDim")),
        DiffBranchStatus.OnlyAChange => ("A", ModifiedGlyph),
        DiffBranchStatus.OnlyBChange => ("B", AddedGlyph),
        DiffBranchStatus.UnequalChange => ("≠", DeletedGlyph),
        _ => null,
    };

    // --------------------------------------------------------------- selection

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The event stops HERE. SelectionChanged bubbles, so this list's selection —
        // including the one Avalonia resets when Rebuild reassigns ItemsSource — reaches
        // every ancestor that listens for one, and an ancestor TabControl reads it as
        // "the tab changed". That is what crashed the app: the host reloaded the tab
        // from inside Avalonia's selection update, which reassigned the source of the
        // list being updated. Hosts hear about a file through SelectedFileChanged, which
        // is raised below and says what they actually want to know.
        e.Handled = true;

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

        _selectedRow = node.Row;
        SelectedFileChanged?.Invoke(node.Row);
    }

    // Rebuilds the view rows (always a new list instance) and puts the selection
    // back on the same file, so filtering or collapsing never reloads a diff.
    private void Rebuild()
    {
        // Belt to the braces above: assigning ItemsSource from inside an assignment of
        // ItemsSource throws, and the paths that can reach this method are many (attach,
        // language switch, filter, grouping, a host reload). A nested call is dropped
        // and re-run once the outer one has finished, so the last word still wins.
        if (_rebuilding)
        {
            _rebuildAgain = true;
            return;
        }

        _rebuilding = true;
        try
        {
            RebuildCore();
        }
        finally
        {
            _rebuilding = false;
        }

        if (_rebuildAgain)
        {
            _rebuildAgain = false;
            Rebuild();
        }
    }

    private bool _rebuilding;
    private bool _rebuildAgain;

    private void RebuildCore()
    {
        DiffFileGroupMode mode = _options.GroupMode;

        List<object> items = [];
        int fileCount = 0;

        for (int section = 0; section < _groups.Count; section++)
        {
            DiffFileGroup group = _groups[section];
            string prefix = SectionPrefix(section);

            // The builder knows nothing about sections, so it is asked with the
            // section's keys stripped back to their bare form and its answers are
            // re-prefixed below. That keeps the collapsed-state bookkeeping in ONE
            // set, keyed by what the user actually folded.
            HashSet<string> collapsedHere = new(StringComparer.Ordinal);
            foreach (string key in _collapsed)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    collapsedHere.Add(key[prefix.Length..]);
                }
            }

            (List<object> built, int count) = DiffFileListBuilder.Build(
                group.Rows,
                _filter,
                mode,
                _options.AsTree,
                GrouperFor(mode),
                collapsedHere,
                (header, n) => F("{0}  ({1})", header, n));

            fileCount += count;

            // A section with no caption contributes its rows and nothing else — that
            // is the plain list (file tree, commit dialog) and the reason those hosts
            // did not have to change.
            if (group.Summary.Length == 0)
            {
                items.AddRange(built);
                continue;
            }

            string sectionKey = prefix + SummaryKey;
            bool folded = _collapsed.Contains(sectionKey);
            items.Add(new FileListGroupNode
            {
                Key = sectionKey,
                Header = F("({0})  {1}", count, group.Summary),
                Count = count,
                IsCollapsed = folded,
            });

            if (folded)
            {
                continue;
            }

            // The search section's rows are hits, not changes: the flag travels here,
            // where the section is known, rather than being read off the row (which is
            // the same DiffFileRow shape a diff produces).
            bool isSearch = group.Summary.StartsWith(GitGrepService.SummaryPrefix, StringComparison.Ordinal);

            foreach (object item in built)
            {
                items.Add(item switch
                {
                    FileListGroupNode inner => new FileListGroupNode
                    {
                        Key = prefix + inner.Key,
                        Header = inner.Header,
                        Count = inner.Count,
                        IsCollapsed = inner.IsCollapsed,
                        Level = inner.Level,
                    },
                    FileListFileNode file when isSearch => new FileListFileNode
                    {
                        Row = file.Row,
                        Display = file.Display,
                        Level = file.Level,
                        IsSearchHit = true,
                    },
                    _ => item,
                });
            }
        }

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
            if (ReferenceEquals(file.Row, _selectedRow))
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
        if (!ReferenceEquals(target?.Row, _selectedRow))
        {
            _selectedRow = target?.Row;
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
            ? new DiffFileListBuilder.GroupLabel("\0none", T("(no extension)"))
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
        ApplyInitialCollapse();
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

    // ------------------------------------------------- find in files (git grep)

    // The three search options are process-wide and file-backed, exactly as the
    // grouping above is process-wide in FileStatusListOptions: two lists in two
    // windows must not disagree about what "match case" means. view-prefs.json rather
    // than ui-state.json for the reason written on ViewPrefsService — the host
    // reserialises ui-state.json on close and would revert whatever a pane wrote.
    private static readonly ViewPrefsService PrefsStore = new();
    private static FindInFilesPrefs? _findPrefs;

    private static FindInFilesPrefs FindPrefs => _findPrefs ??= PrefsStore.Load().FindInFiles;

    private static void UpdateFindPrefs(Action<FindInFilesPrefs> mutate)
    {
        FindInFilesPrefs prefs = FindPrefs;
        mutate(prefs);

        // Read-modify-write of the whole file, so a group written meanwhile by another
        // surface (the diff toolbar, the filter MRU) is not reverted by this one.
        PrefsStore.Update(p => p.FindInFiles = prefs);
    }

    private void ToggleFindBox() => ShowFindBox(!_findBar.IsVisible);

    // Opens or closes the input box. Closing EMPTIES it and asks for a re-run, which
    // is how the search results disappear with the box (upstream does the same in
    // SetFindInCommitFilesGitGrepVisibilityImpl); the caret goes back to the list, so
    // the keyboard is not left in a control that is no longer on screen.
    private void ShowFindBox(bool show, bool persist = true)
    {
        if (persist && FindPrefs.Show != show)
        {
            UpdateFindPrefs(p => p.Show = show);
        }

        if (_findBar.IsVisible == show)
        {
            return;
        }

        _findBar.IsVisible = show;
        _findDebounce.Stop();

        if (show)
        {
            _findBox.Focus();
            _findBox.SelectAll();
            RunFind();
            return;
        }

        bool hadText = (_findBox.Text ?? string.Empty).Length > 0;
        _findBox.Text = string.Empty;
        _list.Focus();

        if (hadText)
        {
            RunFind();
        }
    }

    private void RestartFindDebounce()
    {
        _findDebounce.Stop();
        _findDebounce.Start();
    }

    private void RunFind()
    {
        _findDebounce.Stop();
        FindInFilesRequested?.Invoke(FindQuery);
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            // Enter searches NOW rather than waiting out the debounce: the user has
            // said the pattern is complete.
            RunFind();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ShowFindBox(show: false);
            e.Handled = true;
        }
    }

    // The split button's drop-down. Only the two switches that map onto a git-grep
    // flag are here; see the class remarks for what upstream offers and this does not.
    private void ShowFindMenu(Control anchor)
    {
        MenuItem Option(string key, string english, Func<FindInFilesPrefs, bool> read, Action<FindInFilesPrefs, bool> write)
        {
            MenuItem item = new()
            {
                Header = T(key, english),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = read(FindPrefs),
            };

            item.Click += (_, _) =>
            {
                bool value = !read(FindPrefs);
                UpdateFindPrefs(p => write(p, value));

                // Only re-run when there is a search on screen to re-run: toggling an
                // option with an empty box must not spawn a git process.
                if (FindQuery.IsActive)
                {
                    RunFind();
                }
            };

            return item;
        }

        MenuFlyout flyout = new()
        {
            ItemsSource = new Control[]
            {
                Option("FileStatusList/tsmiFindUsingMatchCase.Text", "Match case",
                    static p => p.MatchCase, static (p, v) => p.MatchCase = v),
                Option("FileStatusList/tsmiFindUsingWholeWord.Text", "Match whole word",
                    static p => p.WholeWord, static (p, v) => p.WholeWord = v),
            },
            Placement = PlacementMode.BottomEdgeAlignedLeft,
        };

        flyout.ShowAt(anchor);
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
        _options.Remember();
        SyncGroupButtons();
        Rebuild();
    }

    private void SetGroupMode(DiffFileGroupMode mode)
    {
        _options.GroupMode = mode;
        _options.Remember();
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
        button.Classes.Add(Theming.BarButtonStyles.Class);
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
        button.Classes.Add(Theming.BarButtonStyles.Class);

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
        Background = B("App.Rule"),
    };
}
