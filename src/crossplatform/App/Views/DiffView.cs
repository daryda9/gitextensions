using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A two-pane view of a single commit's diff: the changed-files list on the
///  left, the unified diff of the selected file on the right. Heavy git work is
///  performed off the UI thread, matching <c>MainWindow</c>.
///
///  <para>Captions go through <see cref="TranslationService"/>. The XLIFF ids
///  come from the two upstream controls this view merges: <c>FileStatusList</c>
///  (the changed-files list and its context menu) and <c>FileViewer</c> (the
///  diff pane's toolbar strip and its settings menu). Strings with no upstream
///  equivalent — the zoom commands, the encoding tooltip, the status line —
///  use the source-text overload and simply stay English when a catalogue has
///  no match. The view re-labels itself in place on
///  <see cref="TranslationService.LanguageChanged"/>; it is never rebuilt, so
///  the loaded diff and the scroll position survive a language switch.</para>
///
///  <para>Besides a commit the view can show a range (<see cref="ShowRange"/>), a
///  commit against the working tree (<see cref="ShowAgainstWorkingDirectory"/>) and
///  the two artificial revision rows (<see cref="ShowArtificial"/>).</para>
///
///  <para><b>Read-only by design.</b> Nothing here writes to the repository: there
///  is no stage/unstage/reset from the diff, not even for the artificial rows where
///  upstream offers it, because those commands belong to the staging UI the port
///  already has (<c>CommitDialog</c>, and <c>Commands ▸ Reset changes…</c>). Menu
///  entries for what the view cannot do are not created at all, and the commands
///  that need a real commit object are disabled while an artificial row is shown
///  (see <c>UpdateFileMenuState</c>).</para>
/// </summary>
public sealed class DiffView : UserControl
{
    // A property, not a field: a static field initialiser can run before the font
    // manager exists, which would cache the fallback for the life of the process.
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // The pane's colours now live in DiffPalette, next to the two colorizing
    // transformers that are their only consumers.

    // Hard cap on the match list, so an incremental search for "e" on a huge patch
    // cannot allocate without bound. The old, much lower caps on HIGHLIGHTING are
    // gone: the highlight is a per-visible-line transformer, so it costs the same
    // whether it has ten hits or ten thousand.
    private const int MaxSearchMatches = 20_000;

    // Which comparison the currently loaded file list represents, so file
    // selection loads the matching per-file diff.
    private enum CompareMode
    {
        Commit,       // a single commit vs its first parent
        Range,        // BASE..other (two commits)
        WorkingTree,  // a commit vs the current working tree
        WorkTree,     // the "Working directory" row: worktree vs index (git diff)
        Index,        // the "Commit index" row: index vs HEAD (git diff --cached)
    }

    private readonly FileStatusListView _files;
    private readonly TextEditor _editor;

    // Continuous scroll: the setting, its delay, and the moment the patch first
    // reached its end (null = not at the end).
    // The last read of app-settings.json, refreshed by ApplyViewerPreferences on every
    // file-list load: the patch loader needs the two git flags without touching disk
    // once per clicked file.
    private AppPreferences _viewerPrefs = new();

    private bool _continuousScroll;
    private TimeSpan _continuousScrollDelay;
    private DateTime? _atEndSince;

    // TWO spinners, because this view runs two loads that are not one wait: the
    // changed-file list (a new selection in the grid, the refresh button) and the patch
    // of the selected file (a click in the list, any toolbar toggle that maps onto a git
    // argument). A single shared overlay would either veil the pane nobody is waiting
    // for, or come down on the first of the two to land while the other is still
    // running — and a spinner that lies about which pane is stale is worse than none.
    private readonly BusyOverlay _filesBusy = new();
    private readonly BusyOverlay _patchBusy = new();
    private readonly DiffLineColorizer _colorizer = new();
    private readonly DiffSearchColorizer _searchColorizer = new();
    private readonly TextBlock _status;

    // Diff-toolbar state (session-persisted in DiffTextService.Session).
    private readonly DiffDisplayOptions _options = DiffTextService.Session;

    // The options the port was missing, which live outside DiffDisplayOptions.
    private readonly DiffViewerOptions _extras = DiffViewerOptions.Session;

    private readonly ToggleButton _ignoreWhitespaceButton;
    private readonly ToggleButton _ignoreWhitespaceEolButton;
    private readonly ToggleButton _ignoreWhitespaceChangeButton;
    private readonly ToggleButton _nonPrintingButton;
    private readonly ToggleButton _wordDiffButton;
    private readonly ToggleButton _syntaxButton;
    private readonly ComboBox _encodingBox;

    // Kept so a language switch can re-label them in place (see ApplyTranslations).
    private readonly CopyPathsMenuItem _copyPathItem;
    private readonly MenuItem _blameItem;
    private readonly MenuItem _historyItem;
    private readonly MenuItem _difftoolItem;
    private readonly MenuItem _compareWorkingDirItem;
    private readonly MenuItem _copyDiffItem;
    private readonly MenuItem _selectAllCopyItem;
    private readonly MenuItem _openWorkingFileItem;
    private readonly MenuItem _openRevisionFileItem;
    private readonly MenuItem _showInFolderItem;
    private readonly MenuItem _filterFileInGridItem;
    private readonly MenuItem _saveAsItem;
    private readonly MenuItem _copyPatchItem;
    private readonly MenuItem _copyNewVersionItem;
    private readonly MenuItem _copyOldVersionItem;
    private readonly Button _prevChangeButton;
    private readonly Button _nextChangeButton;
    private readonly Button _zoomInButton;
    private readonly Button _zoomOutButton;
    private readonly Button _findButton;
    private readonly Button _moreContextButton;
    private readonly Button _lessContextButton;
    private readonly ToggleButton _entireFileButton;
    private readonly Button _settingsButton;

    // ---- incremental search ("find bar") ----
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBox _gotoBox;
    private readonly TextBlock _matchCounter;
    private readonly Button _findPrevButton;
    private readonly Button _findNextButton;
    private readonly Button _findCloseButton;
    private readonly DispatcherTimer _findDebounce;

    // The term currently highlighted and every occurrence of it in the document.
    // The list is what both the counter and the search colorizer read.
    private string _searchTerm = string.Empty;
    private readonly List<DiffSearchMatch> _searchMatches = [];
    private int _matchIndex = -1;

    // Set when the match list hit MaxSearchMatches and stopped growing, so the
    // counter can say that "m" is a floor rather than a total.
    private bool _matchesTruncated;

    // Launches the external editor / file manager for the file context menu.
    private readonly ExternalToolService _tools = new();

    // False while the view shows its "nothing loaded yet" placeholder, so a
    // language switch can re-translate that placeholder without clobbering a
    // real status message (a command line, an error) with a stale one.
    private bool _hasCommit;

    // 1-based document line numbers of each hunk header, and where the ▲/▼
    // navigation currently sits.
    private readonly List<int> _hunkLines = [];
    private int _hunkIndex = -1;

    // Re-runs the load that produced the current file list (the refresh button).
    private Action? _reload;

    private string? _repoPath;
    private string? _commitHash;   // the (right/"new") commit; also the "other" side in Range mode
    private string? _baseHash;     // the ("old"/left) commit in Range mode
    private CompareMode _mode = CompareMode.Commit;
    private CancellationTokenSource? _diffCts;

    // ---- "Find in commit files using git-grep" ----

    // What the list's search box last asked for. Kept because the search has to be
    // re-run whenever the pane loads a different revision (upstream recomputes the
    // grep group in the same Calculate that recomputes the diff), and because the
    // patch pane needs the pattern again to list the matching lines of one file.
    private GitGrepQuery _grepQuery = GitGrepQuery.None;

    // Its own cancellation, separate from _diffCts: typing in the box supersedes the
    // previous git-grep, and must not cancel the patch the user is reading.
    private CancellationTokenSource? _grepCts;

    // The rows of the current search section, by REFERENCE. DiffFileRow is a record,
    // so a hit and a changed file with the same path compare EQUAL by value; identity
    // is the only thing that says which section the clicked row came from, and that is
    // what decides whether the pane shows a patch or the matching lines.
    private readonly HashSet<DiffFileRow> _grepRows = new(RowByReference.Instance);

    // Whether the last file diff was loaded as "commit vs working tree" through
    // the context-menu command, so a toggle re-runs the same comparison.
    private bool _forceWorkingTreeCompare;

    // The raw unified-diff text currently displayed. Kept alongside the editor's
    // document because "Copy diff" must hand over the patch even when the pane is
    // showing a placeholder ("Loading diff…", an error).
    private string _currentDiffText = string.Empty;

    // Path of the file the displayed patch belongs to: the syntax highlighter
    // picks its language from the extension.
    private string? _diffPath;

    public DiffView()
    {
        // Before anything below reads an option: the two option singletons are aliased
        // in this class's FIELD initialisers (which have already run) but their VALUES
        // are only read from here on — to seed the font size, the encoding combo and
        // every toggle button's checked state — so this is where the persisted strip
        // has to be restored. Idempotent: only the first view in the process reads the
        // file (see DiffViewerOptions.EnsureRestored).
        DiffViewerOptions.EnsureRestored();

        // The changed-files list, its toolbar and its regex filter box all live in
        // the shared control (the original's FileStatusList, which also backs the
        // file-tree and stash views): this view only reacts to the selection.
        _files = new FileStatusListView { ShowRefreshButton = true };
        _files.SelectedFileChanged += _ => OnFileSelected();
        _files.RefreshRequested += ReloadFileList;

        // Every comparison this pane shows is about ONE revision to grep — a commit, a
        // range's "other" end, or one of the two artificial rows (git grep understands
        // the index and the worktree as well) — so the search button is always offered
        // here, which is upstream's CanUseFindInCommitFilesGitGrep for the Diff tab.
        _files.CanFindInFiles = true;
        _files.FindInFilesRequested += OnFindInFilesRequested;

        _copyPathItem = new CopyPathsMenuItem(
            () => _files.SelectedFiles.Select(r => r.Name),
            () => _repoPath,
            CopyToClipboard);
        _blameItem = new MenuItem();
        _blameItem.Click += (_, _) => RaiseFileAction(BlameRequested);
        _historyItem = new MenuItem();
        _historyItem.Click += (_, _) => RaiseFileAction(FileHistoryRequested);
        _difftoolItem = new MenuItem();
        _difftoolItem.Click += (_, _) => OpenSelectedInExternalDiffTool();
        _compareWorkingDirItem = new MenuItem();
        _compareWorkingDirItem.Click += (_, _) => CompareSelectedToWorkingDirectory();
        _openWorkingFileItem = new MenuItem();
        _openWorkingFileItem.Click += (_, _) => OpenSelectedWorkingFile();
        _openRevisionFileItem = new MenuItem();
        _openRevisionFileItem.Click += (_, _) => OpenSelectedRevisionFile();
        _showInFolderItem = new MenuItem();
        _showInFolderItem.Click += (_, _) => ShowSelectedInFolder();
        _filterFileInGridItem = new MenuItem();
        _filterFileInGridItem.Click += (_, _) => FilterSelectedFileInGrid();
        _saveAsItem = new MenuItem();
        _saveAsItem.Click += (_, _) => SaveSelectedAs();
        _copyPatchItem = new MenuItem();
        _copyPatchItem.Click += (_, _) => CopyDiffText();

        // Items are built in full here; the Opening handler below only flips
        // IsEnabled (mutating Items from Opening leaves the popup mis-measured).
        ContextMenu fileMenu = new()
        {
            ItemsSource = new Control[]
            {
                _openWorkingFileItem,
                _openRevisionFileItem,
                _showInFolderItem,
                new Separator(),
                _copyPathItem,
                _copyPatchItem,
                _saveAsItem,
                new Separator(),
                _filterFileInGridItem,
                _blameItem,
                _historyItem,
                new Separator(),
                _difftoolItem,
                _compareWorkingDirItem,
            },
        };
        fileMenu.Opening += (_, _) => UpdateFileMenuState();
        _files.List.ContextMenu = fileMenu;

        // The patch pane. AvaloniaEdit ships its own control theme inside the
        // package; it is pulled into THIS control's styles rather than the
        // application's, so the dependency stays where it is used.
        Styles.Add(new StyleInclude(new Uri("avares://GitExtensions.Avalonia/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });

        _editor = new TextEditor
        {
            FontFamily = Monospace,
            FontSize = _options.FontSize,
            Foreground = B("App.Text"),
            Background = B("App.Window"),
            Padding = new Thickness(12, 10, 12, 12),
            IsReadOnly = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // The pane shows a patch, not source it can navigate: the editor's URL
        // detection would turn every http:// in a diff into a click target, and
        // scrolling past the last line only makes "go to line" land oddly.
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.AllowScrollBelowDocument = false;
        _editor.Options.HighlightCurrentLine = false;
        ApplyViewerPreferences();
        ApplyNonPrintingOption();

        // Order matters: the search wash is added last so it paints over the
        // added/removed tint rather than under it.
        _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        _editor.TextArea.TextView.LineTransformers.Add(_searchColorizer);

        // The palette brushes are mutated in place by ThemeManager, but nothing
        // tells a text-view line that its brush changed colour, so a switch has to
        // ask for a repaint. ActualThemeVariantChanged covers dark/light and is an
        // instance event (no unsubscribe needed); StyleChanged covers modern/classic
        // and is static, hence the attach/detach dance below.
        _editor.ActualThemeVariantChanged += (_, _) => RedrawDiff();

        _copyDiffItem = new MenuItem();
        _copyDiffItem.Click += (_, _) => CopyDiffText();
        _selectAllCopyItem = new MenuItem();
        _selectAllCopyItem.Click += (_, _) => SelectAllAndCopy();

        // The original's "Copy new/old version" copy the file, not the patch, so
        // they read the blob rather than filtering the +/- lines of the diff: that
        // way they also work with -w, --word-diff or a partial context.
        _copyNewVersionItem = new MenuItem();
        _copyNewVersionItem.Click += (_, _) => CopyFileVersion(newVersion: true);
        _copyOldVersionItem = new MenuItem();
        _copyOldVersionItem.Click += (_, _) => CopyFileVersion(newVersion: false);

        ContextMenu diffMenu = new()
        {
            ItemsSource = new Control[]
            {
                _copyDiffItem,
                _selectAllCopyItem,
                new Separator(),
                _copyNewVersionItem,
                _copyOldVersionItem,
            },
        };
        diffMenu.Opening += (_, _) =>
        {
            bool hasFile = _files.SelectedFile is not null && _repoPath is not null && _commitHash is not null;
            _copyNewVersionItem.IsEnabled = hasFile;
            _copyOldVersionItem.IsEnabled = hasFile;
        };
        // On the TextArea, not on the TextEditor: the editor's own template puts a
        // ScrollViewer in between, and a menu on the outer control never sees the
        // right-click that lands on the text.
        _editor.TextArea.ContextMenu = diffMenu;

        // Tunnelling, so the decision is made BEFORE the ScrollViewer consumes the
        // notch: at the bottom of the document it consumes it and reports nothing,
        // which is exactly the notch continuous scroll has to act on.
        _editor.AddHandler(PointerWheelChangedEvent, OnDiffWheel, RoutingStrategies.Tunnel);

        // ---- diff toolbar (mirrors the Windows diff viewer's right-hand strip) ----
        AddToolbarStyles();

        // Tooltips are not passed here: every one of them is (re-)applied by
        // ApplyTranslations, which also runs on a language switch.
        _prevChangeButton = ToolButton("▲", GoToPreviousChange);
        _nextChangeButton = ToolButton("▼", GoToNextChange);
        _zoomInButton = ToolButton("A+", () => Zoom(+1));
        _zoomOutButton = ToolButton("A−", () => Zoom(-1));
        _findButton = ToolButton("⌕", () => OpenFindBar(focusGoto: false));
        _moreContextButton = ToolButton("U+", () => ChangeContext(+1));
        _lessContextButton = ToolButton("U−", () => ChangeContext(-1));

        _entireFileButton = ToggleTool(
            "≡", _options.ShowEntireFile,
            v =>
            {
                _options.ShowEntireFile = v;
                DiffViewerOptions.Persist();
                ReloadDiff();
            });

        _ignoreWhitespaceButton = ToggleTool(
            "-w", _options.IgnoreWhitespace,
            v =>
            {
                _options.IgnoreWhitespace = v;
                DiffViewerOptions.Persist();
                ReloadDiff();
            });

        _nonPrintingButton = ToggleTool(
            "¶", _options.ShowNonPrinting,
            v =>
            {
                _options.ShowNonPrinting = v;
                DiffViewerOptions.Persist();
                ApplyNonPrintingOption();
            });

        _ignoreWhitespaceEolButton = ToggleTool(
            "-eol", _extras.IgnoreWhitespaceAtEol,
            v =>
            {
                _extras.IgnoreWhitespaceAtEol = v;
                DiffViewerOptions.Persist();
                ReloadDiff();
            });

        _ignoreWhitespaceChangeButton = ToggleTool(
            "-b", _extras.IgnoreWhitespaceChange,
            v =>
            {
                _extras.IgnoreWhitespaceChange = v;
                DiffViewerOptions.Persist();
                ReloadDiff();
            });

        _wordDiffButton = ToggleTool(
            "<div>", _options.WordDiff,
            v =>
            {
                _options.WordDiff = v;
                DiffViewerOptions.Persist();
                ReloadDiff();
            });

        // Display-only: the patch is already loaded, so this just re-colours the
        // visible lines instead of re-running git.
        _syntaxButton = ToggleTool(
            "{;}", _extras.SyntaxHighlighting,
            v =>
            {
                _extras.SyntaxHighlighting = v;
                DiffViewerOptions.Persist();
                ApplySyntaxLanguage();
            },
            icon: "SyntaxHighlighting");

        _encodingBox = new ComboBox
        {
            ItemsSource = DiffTextService.EncodingNames,
            SelectedItem = DiffTextService.EncodingNames.Contains(_options.EncodingName)
                ? _options.EncodingName
                : DiffTextService.DefaultEncodingName,
            Width = 190,
            FontSize = 12,
            Padding = new Thickness(6, 1, 4, 1),
            MinHeight = 0,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            // Same reasoning as FindTextBox: an editable box on the toolbar strip whose
            // only boundary is this line, so it needs App.BorderStrong's 3:1.
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _encodingBox.SelectionChanged += (_, _) =>
        {
            if (_encodingBox.SelectedItem is not string name)
            {
                return;
            }

            _options.EncodingName = name;
            DiffViewerOptions.Persist();
            ReloadDiff();
        };

        _settingsButton = ToolButton("⚙", null);
        _settingsButton.Click += (_, _) => ShowSettingsMenu(_settingsButton);

        // A WrapPanel, not a horizontal StackPanel: the strip carries enough items
        // (and one 190 px combo box) to be wider than the pane on a narrow window,
        // and a StackPanel would push the encoding box and the gear off the right
        // edge instead of moving them to a second row. Spacing comes from each
        // item's own margin, which WrapPanel honours.
        WrapPanel toolbar = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 2, 6, 2),
        };
        toolbar.Children.Add(_findButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_nextChangeButton);
        toolbar.Children.Add(_prevChangeButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_lessContextButton);
        toolbar.Children.Add(_moreContextButton);
        toolbar.Children.Add(_entireFileButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_zoomInButton);
        toolbar.Children.Add(_zoomOutButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_ignoreWhitespaceEolButton);
        toolbar.Children.Add(_ignoreWhitespaceChangeButton);
        toolbar.Children.Add(_ignoreWhitespaceButton);
        toolbar.Children.Add(_nonPrintingButton);
        toolbar.Children.Add(_syntaxButton);
        toolbar.Children.Add(_wordDiffButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_encodingBox);
        toolbar.Children.Add(_settingsButton);

        Border toolbarBar = new()
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbar,
        };

        // ---- find bar (Ctrl+F), hidden until asked for ----
        _findBox = FindTextBox(240);
        _findBox.TextChanged += (_, _) => RestartFindDebounce();

        _gotoBox = FindTextBox(110);
        _gotoBox.Margin = new Thickness(12, 0, 0, 0);

        _matchCounter = new TextBlock
        {
            FontSize = 12,
            Foreground = B("App.TextDim"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            MinWidth = 70,
        };

        _findPrevButton = ToolButton("▲", () => StepMatch(-1));
        _findNextButton = ToolButton("▼", () => StepMatch(+1));
        _findCloseButton = ToolButton("✕", CloseFindBar);

        // A WrapPanel, not a StackPanel: the go-to-line watermark and the
        // "n of m" counter are translated and grow noticeably in Italian, and a
        // horizontal strip would push the close button off the right edge.
        WrapPanel findPanel = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 3, 6, 3),
        };
        findPanel.Children.Add(_findBox);
        findPanel.Children.Add(_findPrevButton);
        findPanel.Children.Add(_findNextButton);
        findPanel.Children.Add(_matchCounter);
        findPanel.Children.Add(_gotoBox);
        findPanel.Children.Add(_findCloseButton);

        _findBar = new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = findPanel,
            IsVisible = false,
        };

        // Re-highlighting re-renders the whole diff, so an incremental search
        // must not do it on every keystroke.
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce.Stop();
            ApplySearchTerm(_findBox.Text ?? string.Empty);
        };

        // Over the PATCH only, not over the pane: the option strip above it is exactly
        // what the user reaches for when a diff is not the one they wanted (-w, -b,
        // --word-diff, the encoding, ± context), and every one of those buttons re-runs
        // this very load. Veiling them would make the slow load the one moment its own
        // controls are out of reach. The find bar stays live for the same reason — the
        // term survives the reload and re-highlights when the new patch lands.
        Panel patchHost = new();
        patchHost.Children.Add(_editor);
        patchHost.Children.Add(_patchBusy);

        DockPanel diffPane = new();
        DockPanel.SetDock(toolbarBar, Dock.Top);
        DockPanel.SetDock(_findBar, Dock.Top);
        diffPane.Children.Add(toolbarBar);
        diffPane.Children.Add(_findBar);
        diffPane.Children.Add(patchHost);

        _status = new TextBlock
        {
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("App.Toolbar"),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The starting width belongs to the COLUMN, not to the file list: a
        // GridSplitter resizes the column, so a child carrying its own fixed Width
        // stops growing with it and leaves a dead strip between its right edge and the
        // splitter — the pane no longer sticks to the width the user dragged to.
        Grid split = new()
        {
            Background = B("App.Panel"),
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(320, GridUnitType.Pixel) { MinWidth = 120 },
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star) { MinWidth = 120 },
            },
        };

        // The list's overlay covers the shared control WHOLE, toolbar and filter box
        // included — unlike the patch's, which spares its strip. Two reasons: the list
        // host lives inside FileStatusListView, which three other views share and which
        // this view must not reshape for its own spinner; and nothing on that strip acts
        // on anything except the rows that are being replaced — refresh would re-run the
        // load already in flight, and the grouping toggles would regroup rows about to be
        // thrown away. There is no live control under this veil to be sorry about.
        Panel filesHost = new();
        filesHost.Children.Add(_files);
        filesHost.Children.Add(_filesBusy);
        Grid.SetColumn(filesHost, 0);

        GridSplitter splitter = new()
        {
            Width = 4,
            Background = B("App.Border"),
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, 1);

        Grid.SetColumn(diffPane, 2);

        split.Children.Add(filesHost);
        split.Children.Add(splitter);
        split.Children.Add(diffPane);

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(split);

        Content = root;

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;

        // Ctrl+C: copy the file path when the file list is focused, otherwise the diff.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.StyleChanged += RedrawDiff;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // StyleChanged is a STATIC event: a view that forgets to unsubscribe grows
        // its invocation list for the lifetime of the process.
        ThemeManager.StyleChanged -= RedrawDiff;
        base.OnDetachedFromVisualTree(e);
    }

    // Repaints the visible lines with whatever the palette brushes now hold.
    private void RedrawDiff() => _editor.TextArea.TextView.Redraw();

    // The ¶ toggle. This used to rewrite the diff text (spaces to "·", tabs to
    // "→   "), which put the mangled text in the clipboard whenever the user
    // selected any of it and misaligned every tab; the editor draws the marks
    // instead, over the real characters and only for the visible lines.
    private void ApplyNonPrintingOption()
    {
        bool on = _options.ShowNonPrinting;
        _editor.Options.ShowSpaces = on;
        _editor.Options.ShowTabs = on;
        _editor.Options.ShowEndOfLine = on;
    }

    // The three viewer settings that are pure editor configuration: the column rule,
    // the shape of the end-of-line mark, and whether an over-scroll walks on to the
    // next file. Re-read on every load rather than cached, so the Settings dialog is
    // felt on the next selected file instead of the next start.
    private void ApplyViewerPreferences()
    {
        AppPreferences prefs = new SettingsService().Load();
        _viewerPrefs = prefs;

        // The monospace size, when set, wins over the pane's own zoom default — but not
        // over a zoom the user applied since: _options.FontSize is what the +/- buttons
        // write, and it starts from this.
        if (prefs.MonospaceFontSize > 0 && _options.FontSize == DiffDisplayOptions.DefaultFontSize)
        {
            _options.FontSize = prefs.MonospaceFontSize;
            _editor.FontSize = prefs.MonospaceFontSize;
        }

        // AvaloniaEdit takes a LIST of ruler columns; upstream has one position, and 0
        // means none — which is expressed by showing no ruler at all rather than by a
        // ruler at column zero, where it would sit on top of the first character.
        _editor.Options.ColumnRulerPositions = [Math.Max(1, prefs.DiffVerticalRulerPosition)];
        _editor.Options.ShowColumnRulers = prefs.DiffVerticalRulerPosition > 0;

        // Upstream's EolMarkerStyle.Glyph vs .Text (FileViewer.cs:1397). AvaloniaEdit
        // has no such enum: it draws whatever STRING each of the three properties
        // holds, so "as text" is spelled by putting the words there. Its own defaults
        // are the glyphs, restored explicitly so the two paths cannot drift.
        _editor.Options.EndOfLineCRLFGlyph = prefs.ShowEolMarkerAsGlyph ? "¶" : "CRLF";
        _editor.Options.EndOfLineLFGlyph = prefs.ShowEolMarkerAsGlyph ? "¶" : "LF";
        _editor.Options.EndOfLineCRGlyph = prefs.ShowEolMarkerAsGlyph ? "¶" : "CR";

        _continuousScroll = prefs.DiffContinuousScroll;
        _continuousScrollDelay = TimeSpan.FromMilliseconds(prefs.DiffContinuousScrollDelay);
    }

    /// <summary>
    ///  Upstream's <c>AutomaticContinuousScroll</c>: a wheel notch at the very bottom of
    ///  the patch moves to the NEXT changed file, once the patch has been sitting at its
    ///  end for <c>AutomaticContinuousScrollDelay</c>.
    ///
    ///  <para>The delay is measured from the moment the end was first reached and not
    ///  between notches: without it, the flick that scrolls the last screen also jumps
    ///  the file, and the user never sees the lines they scrolled to. Reaching the
    ///  bottom therefore ARMS the jump; the next notch after the delay takes it.</para>
    /// </summary>
    private void OnDiffWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_continuousScroll || e.Delta.Y >= 0)
        {
            return;
        }

        // A patch shorter than the viewport is already "at the end" the moment it
        // loads, and walking off it on the first notch would make the wheel unusable.
        ScrollViewer? scroll = _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null || scroll.Extent.Height <= scroll.Viewport.Height)
        {
            return;
        }

        if (scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 1)
        {
            _atEndSince = null;
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (_atEndSince is null)
        {
            _atEndSince = now;
            return;
        }

        if (now - _atEndSince.Value < _continuousScrollDelay)
        {
            return;
        }

        _atEndSince = null;
        if (_files.SelectNextFile())
        {
            e.Handled = true;
        }
    }

    // Hands the colorizer the language of the loaded patch (or nothing, when the
    // toggle is off or the extension is unknown) and repaints.
    private void ApplySyntaxLanguage()
    {
        _colorizer.Language = _extras.SyntaxHighlighting ? DiffSyntaxHighlighter.Detect(_diffPath) : null;
        RedrawDiff();
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>The catalogue's word for "Error", used to prefix a raw git message.</summary>
    private static string ErrorWord() => T("TranslatedStrings/_error.Text", "Error");

    // The load-time labelling pass, re-run whenever the language changes. It
    // touches captions and tooltips only, never the loaded content, so a switch
    // costs nothing and loses nothing.
    private void ApplyTranslations()
    {
        _copyPathItem.ApplyTranslations();
        _blameItem.Header = T("FileStatusList/tsmiBlame.Text", "Blame");
        _historyItem.Header = T("FileStatusList/tsmiFileHistory.Text", "File history");
        _difftoolItem.Header = T("FileStatusList/tsmiOpenWithDifftool.Text", "Open in external difftool");
        _compareWorkingDirItem.Header = T(
            "RevisionGridControl/compareToWorkingDirectoryMenuItem.Text", "Compare file to working directory");

        // No usable upstream id: FileViewer's "Copy &patch" is mistranslated as
        // "copy and apply" in at least one catalogue, so these two stay on the
        // source-text lookup and fall back to English.
        _copyDiffItem.Header = T("Copy diff");
        _selectAllCopyItem.Header = T("Select all + copy");

        _openWorkingFileItem.Header = T(
            "FileStatusList/tsmiOpenWorkingDirectoryFile.Text", "Open working directory file");
        _openRevisionFileItem.Header = T(
            "FileStatusList/tsmiOpenRevisionFile.Text", "Open this revision (temp file)");
        _showInFolderItem.Header = T("FileStatusList/tsmiShowInFolder.Text", "Show in folder");
        _filterFileInGridItem.Header = T("Filter file in grid");
        _saveAsItem.Header = T("FileStatusList/tsmiSaveAs.Text", "Save selected as...");
        _copyPatchItem.Header = T("FileViewer/copyPatchToolStripMenuItem.Text", "Copy patch");
        _copyNewVersionItem.Header = T("FileViewer/copyNewVersionToolStripMenuItem.Text", "Copy new version");
        _copyOldVersionItem.Header = T("FileViewer/copyOldVersionToolStripMenuItem.Text", "Copy old version");

        ToolTip.SetTip(_prevChangeButton, T("FileViewer/previousChangeButton.ToolTipText", "Previous change"));
        ToolTip.SetTip(_nextChangeButton, T("FileViewer/nextChangeButton.ToolTipText", "Next change"));
        ToolTip.SetTip(_zoomInButton, T("Increase text size"));
        ToolTip.SetTip(_zoomOutButton, T("Decrease text size"));

        // The shortcut is appended outside the translated sentence: key names are
        // written the same way in every catalogue we ship.
        ToolTip.SetTip(_findButton,
            F("{0}  ({1})", T("FileViewer/findToolStripMenuItem.Text", "Find..."), "Ctrl+F"));
        ToolTip.SetTip(_moreContextButton, F("{0}  ({1})",
            T("FileViewer/increaseNumberOfLines.ToolTipText", "Increase the number of lines of context"), "-U"));
        ToolTip.SetTip(_lessContextButton, F("{0}  ({1})",
            T("FileViewer/decreaseNumberOfLines.ToolTipText", "Decrease the number of lines of context"), "-U"));
        ToolTip.SetTip(_entireFileButton,
            T("FileViewer/showEntireFileButton.ToolTipText", "Show entire file"));

        _findBox.Watermark = T("FileViewer/findToolStripMenuItem.Text", "Find...");
        _gotoBox.Watermark = T("FileViewer/goToLineToolStripMenuItem.Text", "Go to line");
        ToolTip.SetTip(_findPrevButton, F("{0}  ({1})", T("Previous match"), "Shift+F3"));
        ToolTip.SetTip(_findNextButton, F("{0}  ({1})", T("Next match"), "F3"));
        ToolTip.SetTip(_findCloseButton, F("{0}  ({1})", T("Close the search bar"), "Esc"));
        UpdateMatchCounter();

        // The git flag is appended outside the translated sentence: it is a
        // command-line token, identical in every language.
        ToolTip.SetTip(_ignoreWhitespaceButton,
            F("{0}  ({1})", T("FileViewer/ignoreAllWhitespaces.ToolTipText", "Ignore all whitespace changes"), "git diff -w"));
        ToolTip.SetTip(_nonPrintingButton,
            T("FileViewer/showNonPrintChars.ToolTipText", "Show nonprinting characters"));
        ToolTip.SetTip(_wordDiffButton,
            F("{0}  ({1})", T("FileViewer/showGitWordColoringToolStripMenuItem.Text", "Word diff"), "git diff --word-diff"));
        ToolTip.SetTip(_ignoreWhitespaceEolButton, F("{0}  ({1})",
            T("FileViewer/ignoreWhitespaceAtEol.ToolTipText", "Ignore whitespace changes at end of line"),
            "git diff --ignore-space-at-eol"));
        ToolTip.SetTip(_ignoreWhitespaceChangeButton, F("{0}  ({1})",
            T("FileViewer/ignoreWhiteSpaces.ToolTipText", "Ignore changes in amount of whitespace"),
            "git diff -b"));
        ToolTip.SetTip(_syntaxButton,
            T("FileViewer/showSyntaxHighlighting.ToolTipText", "Show syntax highlighting"));

        ToolTip.SetTip(_encodingBox, T("Encoding used to decode the diff text"));
        ToolTip.SetTip(_settingsButton, T("FileViewer/settingsButton.ToolTipText", "Settings"));

        if (!_hasCommit)
        {
            _status.Text = T("No commit selected.");
        }
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // TWO scopes meet in this view, exactly as upstream: the changed-file LIST is
        // RevisionDiffControl and the patch pane is FileViewer, and the same F3 means
        // different things in each. Which one answers is decided by the focus, which is
        // upstream's rule too — there the two are separate controls in separate forms.
        if (_files.IsKeyboardFocusWithin
            && HotkeyService.Shared.Command(HotkeyScope.RevisionDiff, e) is { } fileCommand)
        {
            switch (fileCommand)
            {
                case "Blame":
                    RaiseFileAction(BlameRequested);
                    e.Handled = true;
                    return;

                case "ShowHistory":
                    RaiseFileAction(FileHistoryRequested);
                    e.Handled = true;
                    return;

                case "FilterFileInGrid":
                    RaiseFileAction(FilterFileInGridRequested);
                    e.Handled = true;
                    return;

                case "OpenWithDifftool":
                    OpenSelectedInExternalDiffTool();
                    e.Handled = true;
                    return;
            }
        }

        switch (HotkeyService.Shared.Command(HotkeyScope.FileViewer, e))
        {
            case "Find":
                OpenFindBar(focusGoto: false);
                e.Handled = true;
                return;

            case "GoToLine":
                OpenFindBar(focusGoto: true);
                e.Handled = true;
                return;

            case "FindNextOrOpenWithDifftool":
                StepMatch(+1);
                e.Handled = true;
                return;

            case "FindPrevious":
                StepMatch(-1);
                e.Handled = true;
                return;

            case "NextChange":
                GoToNextChange();
                e.Handled = true;
                return;

            case "PreviousChange":
                GoToPreviousChange();
                e.Handled = true;
                return;

            case "IncreaseNumberOfVisibleLines":
                ChangeContext(+1);
                e.Handled = true;
                return;

            case "DecreaseNumberOfVisibleLines":
                ChangeContext(-1);
                e.Handled = true;
                return;

            case "ShowEntireFile":
                _entireFileButton.IsChecked = !_options.ShowEntireFile;
                e.Handled = true;
                return;
        }

        if (_findBar.IsVisible && e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }

        if (_findBar.IsVisible && e.Key is Key.Enter or Key.Return)
        {
            if (_gotoBox.IsKeyboardFocusWithin)
            {
                GoToLineFromBox();
                e.Handled = true;
                return;
            }

            if (_findBox.IsKeyboardFocusWithin)
            {
                // The debounce may still be pending on the very first Enter.
                if (_findDebounce.IsEnabled)
                {
                    _findDebounce.Stop();
                    ApplySearchTerm(_findBox.Text ?? string.Empty);
                }
                else
                {
                    StepMatch(shift ? -1 : +1);
                }

                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_files.IsKeyboardFocusWithin)
            {
                CopySelectedFilePath();
                e.Handled = true;
                return;
            }

            // A live text selection belongs to the editor: swallowing Ctrl+C here
            // would copy the whole patch over what the user had just selected with
            // the mouse. With nothing selected the old meaning stands.
            if (_editor.SelectionLength > 0 && _editor.IsKeyboardFocusWithin)
            {
                return;
            }

            CopyDiffText();
            e.Handled = true;
        }
    }

    /// <summary>Raised (with the repo-relative file path) to blame the selected file.</summary>
    public event Action<string>? BlameRequested;

    /// <summary>Raised (with the repo-relative file path) to show the selected file's history.</summary>
    public event Action<string>? FileHistoryRequested;

    /// <summary>
    ///  Raised with a ready-to-use path filter when the user picks "Filter file in
    ///  grid": the host is expected to feed it to
    ///  <c>RevisionFilter.PathFilter</c> and reload the revision grid, the way
    ///  upstream's <c>RevisionDiffControl.FilterFileInGrid</c> calls
    ///  <c>FormBrowse.SetPathFilter</c>. The value is already quoted when needed,
    ///  so it can be assigned verbatim.
    /// </summary>
    public event Action<string>? FilterFileInGridRequested;

    /// <summary>
    ///  Emits the selected file's repo-relative path as a revision-grid path
    ///  filter. Paths are posix-separated and quoted only when they contain
    ///  whitespace, because <c>RevisionFilter.BuildPathArgument</c> splits an
    ///  unquoted value on spaces into several paths.
    /// </summary>
    private void FilterSelectedFileInGrid()
    {
        if (_files.SelectedFile is not DiffFileRow row)
        {
            return;
        }

        string path = row.Name.Replace('\\', '/');
        if (path.Length == 0)
        {
            return;
        }

        string filter = path.Any(char.IsWhiteSpace) && !path.Contains('"') ? $"\"{path}\"" : path;

        // Posted rather than invoked inline: the handler reloads the revision
        // grid, and that is not work to start while the context menu's pointer
        // event is still unwinding.
        //
        // This used to be the entry that killed the app with "Cannot change source
        // while update is in progress" (the whole inner stack was the grid's own:
        // Reload → ItemsSource → its SelectionChanged handler → RefreshView →
        // RebindRows → ItemsSource again, and posting did NOT help). The grid now
        // owns a real re-entrancy guard — SetListItems is its only ItemsSource
        // writer and raises it, RebindRows coalesces re-entrant requests — so the
        // whole path is exercised end to end and verified against `git log -- <path>`.
        Dispatcher.UIThread.Post(() => FilterFileInGridRequested?.Invoke(filter));
    }

    private void RaiseFileAction(Action<string>? handler)
    {
        if (_files.SelectedFile is DiffFileRow row)
        {
            handler?.Invoke(row.Name);
        }
    }

    // Fire-and-forget: launch the configured external difftool for the selected
    // file. The launch itself runs off the UI thread and the core runs the tool
    // detached, so neither call blocks; only a config error is surfaced (status).
    private void OpenSelectedInExternalDiffTool()
    {
        if (_files.SelectedFile is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        string repoPath = _repoPath;
        string commitHash = _commitHash;

        _ = Task.Run(() =>
        {
            try
            {
                string? message = DiffService.LaunchExternalDiffTool(repoPath, commitHash, row);
                if (!string.IsNullOrEmpty(message))
                {
                    Dispatcher.UIThread.Post(() => _status.Text = message);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = F(T("Difftool error: {0}"), ex.Message));
            }
        });
    }

    // Loads the diff of the selected file's committed version against the current
    // working-tree version and renders it in the shared coloured diff pane.
    private void CompareSelectedToWorkingDirectory()
    {
        if (_files.SelectedFile is not DiffFileRow || _repoPath is null || _commitHash is null)
        {
            return;
        }

        // Sticky, so a toolbar toggle re-runs the same comparison.
        _forceWorkingTreeCompare = true;
        LoadSelectedFileDiff();
    }

    // Ctrl+C over the file list copies the absolute native path — the flavour the
    // context menu shows in bold, and the one upstream binds the same key to
    // (copyFullPathsNativeToolStripMenuItem.ShortcutKeys).
    private void CopySelectedFilePath()
        => _copyPathItem.Copy(CopyPathsMenuItem.PathFlavour.FullNative);

    private void CopyDiffText() => CopyToClipboard(_currentDiffText);

    /// <summary>
    ///  The original's "Copy new version" / "Copy old version": the whole file as
    ///  it is on one side of the comparison, not the patch. Which revision that is
    ///  depends on the comparison shown — the working tree has no revision, and the
    ///  "old" side of a single commit is its first parent.
    /// </summary>
    private void CopyFileVersion(bool newVersion)
    {
        if (_files.SelectedFile is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        bool workingTree = _forceWorkingTreeCompare || _mode == CompareMode.WorkingTree;

        string? rev;
        string path;

        // The artificial rows have real sides, they are just not commits:
        // "Working directory" is (index -> disk), "Commit index" is (HEAD -> index).
        // ":" is git's own name for the index copy of a path (":<path>").
        if (!_forceWorkingTreeCompare && IsArtificialMode)
        {
            bool index = _mode == CompareMode.Index;
            rev = newVersion
                ? (index ? ":" : null)
                : (index ? "HEAD" : ":");
            path = newVersion ? row.Name : row.OldName ?? row.Name;
        }
        else if (newVersion)
        {
            rev = workingTree ? null : _commitHash;
            path = row.Name;
        }
        else
        {
            rev = _mode == CompareMode.Range
                ? _baseHash ?? _commitHash
                : workingTree
                    ? _commitHash
                    : _commitHash + "^";

            // A rename's old side lives under its old path.
            path = row.OldName ?? row.Name;
        }

        string repoPath = _repoPath;
        string encoding = _options.EncodingName;

        _status.Text = F(T("Reading {0}…"), path);

        _ = Task.Run(async () =>
        {
            try
            {
                string text = await ExtendedDiffTextService
                    .GetFileTextAsync(repoPath, rev, path, encoding)
                    .ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    CopyToClipboard(text);
                    _status.Text = F(
                        T("Copied {0} ({1} characters)"),
                        rev is null ? path : rev.EndsWith(':') ? rev + path : rev + ":" + path,
                        text.Length);
                });
            }
            catch (Exception ex)
            {
                // A deleted file has no new version, a root commit no old one: git
                // says so and the status line repeats it.
                Dispatcher.UIThread.Post(() => _status.Text = F("{0}: {1}", ErrorWord(), ex.Message));
            }
        });
    }

    private void SelectAllAndCopy()
    {
        _editor.SelectAll();
        CopyToClipboard(_currentDiffText);
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>
    ///  Opens the find bar and puts the caret in it — the same thing Ctrl+F does
    ///  from inside the view, exposed so the window can reach it when the focus is
    ///  somewhere else entirely (the view's own Ctrl+F is a tunnelling handler on
    ///  this control, so it only ever fires once the focus is already in here).
    /// </summary>
    public void FocusSearch() => OpenFindBar(focusGoto: false);

    /// <summary>
    ///  Loads the changed-files list for <paramref name="commitHash"/> in the
    ///  repository at <paramref name="repoPath"/>. Selecting a file loads its diff.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
        => ShowCommit(repoPath, commitHash, preselectPath: null);

    /// <summary>
    ///  As <see cref="ShowCommit(string, string)"/>, but lands on
    ///  <paramref name="preselectPath"/> instead of on the first changed file —
    ///  what a file-scoped host (the file-history window) needs, since upstream's
    ///  FormFileHistory Diff tab shows exactly one file of the selected commit.
    ///
    ///  <para>A path that the commit does not contain is not an error: a rename
    ///  means the file is recorded under its historic name, so the list simply
    ///  keeps its own first-row selection.</para>
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash, string? preselectPath)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _baseHash = null;
        _mode = CompareMode.Commit;

        // The setting is read HERE, per load, and not cached in a field: it is changed
        // in the Settings dialog while this view stays alive, and the next selected
        // commit must already obey the new answer.
        bool allParents = new SettingsService().Load().ShowDiffForAllParents;

        // A merge shown per parent has one caption PER GROUP, which is why it cannot go
        // through LoadFileList's single-summary shape. A non-merge comes back as one
        // unnamed group, and then the pane's own "Diff with …" header still applies.
        LoadFileGroups(
            () =>
            {
                IReadOnlyList<DiffFileGroup> groups =
                    DiffService.GetCommitFileGroups(repoPath, commitHash, allParents);
                return groups.Count == 1 && groups[0].Summary.Length == 0
                    ? [new DiffFileGroup(
                        DiffWithHeader(repoPath, DiffService.FirstParentOf(repoPath, commitHash)),
                        groups[0].Rows)]
                    : groups;
            },
            count => F(ChangedFilesFormat(), commitHash, count),
            F(LoadingFilesFormat(), commitHash),
            preselectPath);
    }

    /// <summary>
    ///  Loads the changed-files list and per-file diffs for the range
    ///  <paramref name="baseHash"/>..<paramref name="otherHash"/>
    ///  (i.e. <c>git diff &lt;base&gt; &lt;other&gt;</c>).
    /// </summary>
    /// <summary>
    ///  Empties the pane: no file list, no patch, and the "No commit selected." line the
    ///  view is born with.
    ///
    ///  <para>Called when the window changes REPOSITORY (a tab switch), where leaving the
    ///  previous repository's diff on screen is not staleness but a wrong answer: the
    ///  commit it describes is not in the repository now being shown, and if the new one
    ///  has no selection yet nothing would ever overwrite it.</para>
    /// </summary>
    public void Clear()
    {
        _diffCts?.Cancel();

        // The search is about the revision that is going away, so it goes with it —
        // its results would name files of another repository. The BOX stays open (and
        // keeps its text): the next revision loaded here re-runs it through RunGrep.
        _grepCts?.Cancel();
        _grepRows.Clear();

        // Both spinners come down here, and this is the ONE place that has to do it by
        // hand: a cancelled patch load returns off the UI thread without touching the
        // pane (that is what the token check below the await is for), so the overlay it
        // put up has no other owner left. A repo switch also abandons the file-list load
        // in flight, whose result Clear has just made meaningless.
        _filesBusy.Hide();
        _patchBusy.Hide();

        _files.Clear();
        ShowPlaceholder(string.Empty);
        _currentDiffText = string.Empty;
        _hasCommit = false;
        _status.Text = T("No commit selected.");
    }

    public void ShowRange(string repoPath, string baseHash, string otherHash)
        => ShowRange(repoPath, baseHash, otherHash, preselectPath: null);

    /// <summary>
    ///  As <see cref="ShowRange(string, string, string)"/>, but lands on
    ///  <paramref name="preselectPath"/> — what the file-history window needs, where
    ///  the range the user picked is about one file and the pane must open on it
    ///  rather than on whatever sorts first.
    /// </summary>
    public void ShowRange(string repoPath, string baseHash, string otherHash, string? preselectPath)
    {
        _repoPath = repoPath;
        _commitHash = otherHash;
        _baseHash = baseHash;
        _mode = CompareMode.Range;

        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = otherHash.Length > 8 ? otherHash[..8] : otherHash;

        string range = F("{0} .. {1}", shortBase, shortOther);

        LoadFileList(
            () => DiffService.GetDiffFilesBetween(repoPath, baseHash, otherHash),
            count => F(ChangedFilesFormat(), range, count),
            F(LoadingFilesFormat(), range),
            preselectPath,
            () => DiffWithHeader(repoPath, baseHash));
    }

    /// <summary>
    ///  Shows what a selection of SEVERAL revisions in the grid compares — upstream's
    ///  <c>FileStatusDiffCalculator</c> answer to a multi-row selection, which is more
    ///  than one comparison: "Diff with A …" always, plus either one group per
    ///  further selected revision, or the two "Diff BASE with A/B …" groups when the
    ///  selection really does span two branches.
    ///  <paramref name="revisions"/> is newest first, as the grid announces it.
    ///
    ///  <para>Two selected revisions are not a special case here — they are the
    ///  ordinary shape of this call, and the reason
    ///  <see cref="ShowRange(string, string, string)"/> is left to the callers that
    ///  genuinely mean ONE comparison ("Compare to BASE", the file-history window).</para>
    /// </summary>
    public void ShowRevisions(string repoPath, IReadOnlyList<string> revisions)
    {
        if (revisions.Count < 2)
        {
            return;
        }

        _repoPath = repoPath;

        // The extremes still describe the pane as a whole: they are what the commands
        // that act on "the comparison on screen" (open a revision, the external
        // difftool) use, and what a row without a pair of its own falls back to.
        _commitHash = revisions[0];
        _baseHash = revisions[^1];
        _mode = CompareMode.Range;

        string shortBase = Short(revisions[^1]);
        string shortOther = Short(revisions[0]);
        string range = F("{0} .. {1}", shortBase, shortOther);

        LoadFileGroups(
            () => DiffService.GetSelectionDiffGroups(repoPath, revisions),
            count => F(ChangedFilesFormat(), range, count),
            F(LoadingFilesFormat(), range));
    }

    private static string Short(string hash) => hash.Length > 8 ? hash[..8] : hash;

    /// <summary>
    ///  Loads the changed-files list and per-file diffs comparing
    ///  <paramref name="commitHash"/> against the current working tree
    ///  (i.e. <c>git diff &lt;commit&gt;</c>).
    /// </summary>
    public void ShowAgainstWorkingDirectory(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _baseHash = null;
        _mode = CompareMode.WorkingTree;

        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;

        string range = F("{0} .. {1}", shortHash, T("TranslatedStrings/_workingDirectoryText.Text", "working directory"));

        LoadFileList(
            () => DiffService.GetChangedFilesAgainstWorkingTree(repoPath, commitHash),
            count => F(ChangedFilesFormat(), range, count),
            F(LoadingFilesFormat(), range),
            preselectPath: null,
            () => DiffWithHeader(repoPath, commitHash));
    }

    /// <summary>
    ///  Loads the changed-files list of one of the two <b>artificial</b> revision
    ///  rows — <see cref="ArtificialDiff.WorkTree"/> ("Working directory":
    ///  <c>git diff</c>, worktree vs index, untracked files included) or
    ///  <see cref="ArtificialDiff.Index"/> ("Commit index": <c>git diff --cached</c>,
    ///  index vs HEAD). Selecting a file shows its patch, exactly as for a commit.
    ///
    ///  <para>This is the Diff half of the
    ///  <c>RevisionGridView.ArtificialRevisionSelected</c> contract; the host calls
    ///  it instead of <see cref="ShowCommit"/> when the selection lands on one of
    ///  those rows.</para>
    ///
    ///  <para>The comparison has no commit object, so the file commands that only
    ///  make sense for one ("Open this revision", "Save selected as", the external
    ///  difftool, "Compare with working directory") are <b>disabled</b> rather than
    ///  offered and left to fail; stage/unstage from here is deliberately absent
    ///  (see the class remarks).</para>
    /// </summary>
    public void ShowArtificial(string repoPath, ArtificialDiff which)
    {
        _repoPath = repoPath;

        // The sentinel hash is carried so the "is something loaded" guards keep
        // working; nothing ever passes it to git as a revision (see the CompareMode
        // switches — the artificial modes emit options, not revisions).
        _commitHash = which == ArtificialDiff.Index ? DiffService.IndexHash : DiffService.WorkTreeHash;
        _baseHash = null;
        _mode = which == ArtificialDiff.Index ? CompareMode.Index : CompareMode.WorkTree;
        _forceWorkingTreeCompare = false;

        string name = ArtificialRevisionName.Of(which);

        LoadFileList(
            () => DiffService.GetArtificialChangedFiles(repoPath, which),
            count => F(ChangedFilesFormat(), name, count),
            F(LoadingFilesFormat(), name));
    }

    /// <summary>
    ///  The header the changed-file list shows above its rows: upstream's
    ///  <c>"Diff with A " + DescribeRevision(a)</c>
    ///  (<c>FileStatusDiffCalculator</c>), i.e. the "A" side every row is a diff
    ///  AGAINST — the selected commit's first parent, or the older end of a range.
    ///
    ///  <para>Empty for a root commit, whose rows are diffed against the empty tree:
    ///  there is no revision to name, and the list then has no header rather than a
    ///  header naming nothing. Runs a git call, so it is only ever invoked from the
    ///  background thread of a load.</para>
    /// </summary>
    private static string DiffWithHeader(string repoPath, string? sideA)
    {
        string described = DiffService.DescribeRevision(repoPath, sideA);
        return described.Length > 0 ? T("Diff with A ") + described : string.Empty;
    }

    // Composed status texts are single formats with placeholders, never
    // assembled from translated fragments: {0} is the comparison being shown
    // (a hash or a range) and {1} the file count.
    private static string ChangedFilesFormat() => T("{0}  —  {1} changed file(s)");

    private static string LoadingFilesFormat() => T("Loading changed files for {0}…");

    // The one word every pane of this app now waits with — the same string the revision
    // grid and the left tree hand to their own BusyOverlay. The spinner says "wait", the
    // status line says what for; splitting the two that way is what stops each pane from
    // inventing its own vocabulary for the same idea.
    private static string LoadingCaption() => T("RevisionGridControl/_strLoading.Text", "Loading…");

    // Shared changed-file-list loader: clears the panes, loads the file rows off
    // the UI thread, then hands them to the list, which selects the first row and
    // reports it back through SelectedFileChanged (which dispatches on _mode).
    private void LoadFileList(
        Func<IReadOnlyList<DiffFileRow>> load,
        Func<int, string> statusFor,
        string loadingText,
        string? preselectPath = null,
        Func<string>? summaryFor = null)
        => LoadFileGroups(
            () => [new DiffFileGroup(summaryFor?.Invoke() ?? string.Empty, load())],
            statusFor,
            loadingText,
            preselectPath);

    // The one loader. A single-comparison list is one section with (at most) a
    // caption, a multi-revision selection is several — so there is no second code
    // path for the panes that show one diff, only a thinner call above.
    private void LoadFileGroups(
        Func<IReadOnlyList<DiffFileGroup>> load,
        Func<int, string> statusFor,
        string loadingText,
        string? preselectPath = null)
    {
        // Remembered so the toolbar's refresh button can re-run exactly this load.
        _reload = () => LoadFileGroups(load, statusFor, loadingText);

        // One read of the settings file per list load, not per patch: everything below
        // (and every patch loaded from the list that follows) reads the snapshot.
        ApplyViewerPreferences();

        // The file to land on travels WITH this load, not in a field of the view.
        // One user gesture can produce two loads (a Ctrl-click on the grid raises
        // SelectionChanged twice), and a shared field made the second one consume
        // what the first had already used up: the second list came back with no
        // preselection and put the selection on its first row, so the pane showed a
        // file the user never asked for.
        string? wanted = string.IsNullOrEmpty(preselectPath) ? null : preselectPath;

        _files.Clear();
        ShowPlaceholder(string.Empty);
        _currentDiffText = string.Empty;

        // The patch pane has just been emptied, so whatever patch load was in flight is
        // no longer the one on screen and its spinner would be veiling a blank editor.
        _patchBusy.Hide();

        // loadingText is KEPT, unlike the patch pane's "Loading diff…" below: it names
        // the comparison being read (a hash, a range, "Working directory"), which the
        // spinner cannot, and this status line is shared by both panes — it is the only
        // place that can say WHICH of the two the wait belongs to.
        _status.Text = loadingText;
        _hasCommit = true;
        _filesBusy.Show(LoadingCaption());

        _ = Task.Run(() =>
        {
            try
            {
                // Everything git — the diffs, the merge base, and the naming of each
                // revision in the captions — happens on THIS thread; the UI thread
                // only ever receives finished rows.
                IReadOnlyList<DiffFileGroup> groups = load();
                int total = groups.Sum(g => g.Rows.Count);

                Dispatcher.UIThread.Post(() =>
                {
                    // Before the rows go in, not after: Preselect drives a selection that
                    // starts the PATCH load, and that load's own spinner must not go up
                    // behind a veil this one has not taken down yet.
                    _filesBusy.Hide();
                    _files.SetFiles(groups);
                    Preselect(wanted);
                    _status.Text = statusFor(total);

                    // The diff half has just been replaced; the search half is
                    // independent and is re-run for the revision now on screen, so an
                    // open search box keeps answering after a refresh or a new
                    // selection instead of leaving stale hits (or none) behind. This is
                    // upstream's second pass in ReloadFileStatus, which recomputes the
                    // grep group after the diff group is already visible.
                    RunGrep();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _filesBusy.Hide();
                    _status.Text = F("{0}: {1}", ErrorWord(), ex.Message);
                });
            }
        });
    }

    // Moves the selection onto the file a caller asked for, once the rows are in.
    // The list has no "select by name" of its own, so the row nodes it just built
    // are searched and the pick is handed to the ListBox — which reports it back
    // through SelectedFileChanged, exactly as a click would, so the diff loads.
    private void Preselect(string? wanted)
    {
        if (wanted is null || _files.List.ItemsSource is not IEnumerable<object> items)
        {
            return;
        }

        foreach (object item in items)
        {
            if (item is FileListFileNode node && SamePath(node.Row.Name, wanted))
            {
                _files.List.SelectedItem = node;
                return;
            }
        }
    }

    // Path equality for the preselection only. Git speaks forward slashes and
    // repository-relative names, but the callers of ShowCommit hand on whatever
    // the grid or a command line gave them, so a Windows-style or "./"-prefixed
    // spelling of the same file must still find its row.
    private static bool SamePath(string rowName, string wanted)
        => string.Equals(rowName, wanted, StringComparison.Ordinal)
           || string.Equals(NormalisePath(rowName), NormalisePath(wanted), StringComparison.Ordinal);

    private static string NormalisePath(string path)
    {
        string slashed = path.Replace('\\', '/');
        return slashed.StartsWith("./", StringComparison.Ordinal) ? slashed[2..] : slashed;
    }

    // The toolbar's refresh button: re-reads the changed-file list of whatever
    // comparison is on screen (the working-tree one is the one that goes stale).
    private void ReloadFileList() => _reload?.Invoke();

    // ------------------------------------------- find in commit files (git grep)

    // The list's search box changed (typing, Enter, an option, the box closing).
    private void OnFindInFilesRequested(GitGrepQuery query)
    {
        _grepQuery = query;
        RunGrep();
    }

    // Runs the current query against the revision the pane is showing and hands the
    // result to the list as its extra section. An inactive query (empty box, closed
    // box) removes the section instead of running anything.
    private void RunGrep()
    {
        // Whatever was running is no longer the answer to the question on screen:
        // superseded searches are cancelled, not awaited.
        _grepCts?.Cancel();
        _grepCts?.Dispose();
        _grepCts = null;

        if (!_grepQuery.IsActive || _repoPath is null || _commitHash is null)
        {
            _grepRows.Clear();
            _files.SetSearchResults(null);
            return;
        }

        _grepCts = new CancellationTokenSource();
        CancellationToken token = _grepCts.Token;

        // Snapshot everything the background thread reads: the pane's revision can
        // change under it, and a search must answer for the revision it started on.
        string repoPath = _repoPath;
        string commitHash = _commitHash;
        GitGrepQuery query = _grepQuery;

        _ = Task.Run(() =>
        {
            try
            {
                DiffFileGroup? group = GitGrepService.SearchGroup(repoPath, commitHash, query, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    // Re-checked on the UI thread: the query may have been superseded
                    // between the git call returning and this post being pumped, and
                    // the newer search's results must not be overwritten by an older
                    // one's.
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    _grepRows.Clear();
                    if (group is not null)
                    {
                        foreach (DiffFileRow row in group.Rows)
                        {
                            _grepRows.Add(row);
                        }
                    }

                    _files.SetSearchResults(group);
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another search; ignore.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        // A failed search leaves the diff sections alone and says so on
                        // the shared status line — the pane is not broken, the search is.
                        _status.Text = F("{0}: {1}", ErrorWord(), ex.Message);
                    }
                });
            }
        });
    }

    // Shows the matching lines of a search hit in the patch pane. A hit has no patch:
    // the file merely CONTAINS the pattern at this revision, so what belongs here is
    // git grep's own listing (upstream: GitUIExtensions.ViewChangesAsync routes a
    // status carrying a GrepString to GetGrepFileAsync rather than to a diff).
    private void LoadSelectedGrepMatches(DiffFileRow row, string repoPath, string commitHash)
    {
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        GitGrepQuery query = _grepQuery;
        int contextLines = _options.ShowEntireFile ? 100_000 : _options.ContextLines;

        _diffPath = row.Name;
        ShowPlaceholder(string.Empty);
        _patchBusy.Show(LoadingCaption());

        _ = Task.Run(async () =>
        {
            try
            {
                string text = await GitGrepService
                    .GetMatchesAsync(repoPath, commitHash, row.Name, query, contextLines, token)
                    .ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _patchBusy.Hide();

                        // RenderDiff, not ShowPlaceholder: the text is content (it is
                        // what "Copy diff" should hand over, and the syntax highlighter
                        // can colour it by the file's extension), even though no line
                        // of it is a +/- one.
                        RenderDiff(text);
                        _status.Text = F("{0}  —  {1}", row.Name, query.Text);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another selection.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _patchBusy.Hide();
                        ShowPlaceholder(F("{0}: {1}", ErrorWord(), ex.Message));
                    }
                });
            }
        });
    }

    // Reference identity for DiffFileRow, which is a record and therefore equal by
    // value to any row naming the same file in the same state.
    private sealed class RowByReference : IEqualityComparer<DiffFileRow>
    {
        public static RowByReference Instance { get; } = new();

        public bool Equals(DiffFileRow? x, DiffFileRow? y) => ReferenceEquals(x, y);

        public int GetHashCode(DiffFileRow obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private void OnFileSelected()
    {
        // A plain selection always shows the comparison the file list belongs to.
        _forceWorkingTreeCompare = false;
        LoadSelectedFileDiff();
    }

    // ---------------------------------------------------------------- toolbar

    // Flat toolbar chrome: the Fluent templates paint a button's background
    // through their inner ContentPresenter, so style that part directly.
    private void AddToolbarStyles()
    {
        IBrush hover = B("App.PanelAlt");
        IBrush border = B("App.Border");
        IBrush selection = B("App.Selection");

        // Each style is "difftool" plus zero or more pseudo-classes; they must be
        // chained as separate Class(...) calls (a single "a:b" string would be read
        // as one class name and never match).
        void Chrome<T>(string[] pseudo, IBrush background, IBrush stroke)
            where T : TemplatedControl =>
            Styles.Add(new Style(x =>
            {
                Selector s = x.OfType<T>().Class("difftool");
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

    // The caption is always a glyph, never a translated word; the tooltip is set
    // separately by ApplyTranslations so a language switch can revisit it.
    private Button ToolButton(string glyph, Action? onClick)
    {
        Button button = new()
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = B("App.Text"),
                VerticalAlignment = VerticalAlignment.Center,
            },
            Padding = new Thickness(6, 2),
            Margin = new Thickness(1, 0),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("difftool");

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    // icon: the name of a reused Windows resource to show instead of the glyph,
    // when that resource exists (IconLoader returns null when it does not).
    private ToggleButton ToggleTool(string glyph, bool isChecked, Action<bool> onChanged, string? icon = null)
    {
        Control face = icon is not null && IconLoader.Image(icon) is Image image
            ? image
            : new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = B("App.Text"),
                VerticalAlignment = VerticalAlignment.Center,
            };

        ToggleButton button = new()
        {
            Content = face,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(1, 0),
            MinWidth = 0,
            MinHeight = 0,
            IsChecked = isChecked,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("difftool");
        button.IsCheckedChanged += (_, _) => onChanged(button.IsChecked == true);

        return button;
    }

    private Control ToolSeparator() => new Border
    {
        Width = 1,
        Margin = new Thickness(3, 4),
        Background = B("App.Border"),
    };

    // The gear menu: the same options as the toolbar, plus the zoom commands.
    // The flyout's items are built in full BEFORE ShowAt (mutating them from
    // Opening leaves the popup mis-measured).
    private void ShowSettingsMenu(Control anchor)
    {
        MenuItem ignore = new()
        {
            Header = F("{0}  ({1})",
                T("FileViewer/ignoreAllWhitespaceChangesToolStripMenuItem.Text", "Ignore all whitespace changes"), "-w"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.IgnoreWhitespace,
        };
        ignore.Click += (_, _) => _ignoreWhitespaceButton.IsChecked = !_options.IgnoreWhitespace;

        MenuItem ignoreEol = new()
        {
            Header = F("{0}  ({1})",
                T("FileViewer/ignoreWhitespaceAtEolToolStripMenuItem.Text",
                    "Ignore whitespace changes at end of line"), "--ignore-space-at-eol"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _extras.IgnoreWhitespaceAtEol,
        };
        ignoreEol.Click += (_, _) => _ignoreWhitespaceEolButton.IsChecked = !_extras.IgnoreWhitespaceAtEol;

        MenuItem ignoreChange = new()
        {
            Header = F("{0}  ({1})",
                T("FileViewer/ignoreWhitespaceChangesToolStripMenuItem.Text",
                    "Ignore changes in amount of whitespace"), "-b"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _extras.IgnoreWhitespaceChange,
        };
        ignoreChange.Click += (_, _) =>
            _ignoreWhitespaceChangeButton.IsChecked = !_extras.IgnoreWhitespaceChange;

        MenuItem asText = new()
        {
            Header = F("{0}  ({1})",
                T("FileViewer/treatAllFilesAsTextToolStripMenuItem.Text", "Treat all files as text"), "--text"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _extras.TreatAllFilesAsText,
        };
        asText.Click += (_, _) =>
        {
            _extras.TreatAllFilesAsText = !_extras.TreatAllFilesAsText;
            DiffViewerOptions.Persist();
            ReloadDiff();
        };

        MenuItem syntax = new()
        {
            Header = T("FileViewer/showSyntaxHighlightingToolStripMenuItem.Text", "Show syntax highlighting"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _extras.SyntaxHighlighting,
        };
        syntax.Click += (_, _) => _syntaxButton.IsChecked = !_extras.SyntaxHighlighting;

        MenuItem nonPrinting = new()
        {
            Header = T("FileViewer/showNonprintableCharactersToolStripMenuItem.Text", "Show nonprinting characters"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.ShowNonPrinting,
        };
        nonPrinting.Click += (_, _) => _nonPrintingButton.IsChecked = !_options.ShowNonPrinting;

        MenuItem word = new()
        {
            Header = F("{0}  ({1})",
                T("FileViewer/showGitWordColoringToolStripMenuItem.Text", "Word diff"), "--word-diff"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.WordDiff,
        };
        word.Click += (_, _) => _wordDiffButton.IsChecked = !_options.WordDiff;

        MenuItem entireFile = new()
        {
            Header = T("FileViewer/showEntireFileToolStripMenuItem.Text", "Show entire file"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.ShowEntireFile,
        };
        entireFile.Click += (_, _) => _entireFileButton.IsChecked = !_options.ShowEntireFile;

        MenuItem moreContext = new()
        {
            Header = T("FileViewer/increaseNumberOfLinesToolStripMenuItem.Text",
                "Increase the number of lines of context"),
        };
        moreContext.Click += (_, _) => ChangeContext(+1);

        MenuItem lessContext = new()
        {
            Header = T("FileViewer/decreaseNumberOfLinesToolStripMenuItem.Text",
                "Decrease the number of lines of context"),
        };
        lessContext.Click += (_, _) => ChangeContext(-1);

        MenuItem find = new()
        {
            Header = F("{0}  ({1})", T("FileViewer/findToolStripMenuItem.Text", "Find..."), "Ctrl+F"),
        };
        find.Click += (_, _) => OpenFindBar(focusGoto: false);

        MenuItem goToLine = new()
        {
            Header = F("{0}  ({1})", T("FileViewer/goToLineToolStripMenuItem.Text", "Go to line"), "Ctrl+G"),
        };
        goToLine.Click += (_, _) => OpenFindBar(focusGoto: true);

        MenuItem zoomIn = new() { Header = T("Increase text size") };
        zoomIn.Click += (_, _) => Zoom(+1);
        MenuItem zoomOut = new() { Header = T("Decrease text size") };
        zoomOut.Click += (_, _) => Zoom(-1);
        MenuItem zoomReset = new() { Header = T("Reset text size") };
        zoomReset.Click += (_, _) => Zoom(0);

        // The encoding name is data: it must not be looked up, and its
        // underscores (if any) must be escaped so the access-key parser keeps them.
        MenuItem encodingReset = new()
        {
            Header = F(T("Reset encoding to {0}"), DiffTextService.DefaultEncodingName.Replace("_", "__")),
        };
        encodingReset.Click += (_, _) => _encodingBox.SelectedItem = DiffTextService.DefaultEncodingName;

        MenuFlyout flyout = new()
        {
            ItemsSource = new Control[]
            {
                find,
                goToLine,
                new Separator(),
                ignoreEol,
                ignoreChange,
                ignore,
                nonPrinting,
                syntax,
                word,
                asText,
                new Separator(),
                moreContext,
                lessContext,
                entireFile,
                new Separator(),
                zoomIn,
                zoomOut,
                zoomReset,
                new Separator(),
                encodingReset,
            },
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };

        flyout.ShowAt(anchor);
    }

    // direction: +1 larger, -1 smaller, 0 reset to the default size.
    private void Zoom(int direction)
    {
        double size = direction == 0
            ? DiffDisplayOptions.DefaultFontSize
            : Math.Clamp(_options.FontSize + direction, 6, 32);

        _options.FontSize = size;
        DiffViewerOptions.Persist();
        _editor.FontSize = size;
        _status.Text = F(T("Text size {0:0}pt"), size);
    }

    // ------------------------------------------------------- hunk navigation

    private void GoToNextChange() => GoToChange(+1);

    private void GoToPreviousChange() => GoToChange(-1);

    private void GoToChange(int step)
    {
        if (_hunkLines.Count == 0)
        {
            _status.Text = T("No changes to navigate in this file.");
            return;
        }

        int next = _hunkIndex < 0
            ? (step > 0 ? 0 : _hunkLines.Count - 1)
            : Math.Clamp(_hunkIndex + step, 0, _hunkLines.Count - 1);

        _hunkIndex = next;
        ScrollToLine(_hunkLines[next]);
        _status.Text = F(T("Change {0} of {1}"), next + 1, _hunkLines.Count);
    }

    // Brings a 1-based document line into view. This used to be an estimate — the
    // block's height divided by its line count — because a SelectableTextBlock has
    // no notion of lines; the editor knows exactly where line n is.
    private void ScrollToLine(int line)
    {
        line = Math.Clamp(line, 1, Math.Max(1, _editor.Document?.LineCount ?? 1));
        _editor.TextArea.Caret.Line = line;
        _editor.TextArea.Caret.Column = 1;
        _editor.ScrollToLine(line);
    }

    // ------------------------------------------------------- search / go to line

    private TextBox FindTextBox(double width) => new()
    {
        Width = width,
        FontSize = 12,
        MinHeight = 0,
        Padding = new Thickness(6, 2, 6, 2),
        Background = B("App.Panel"),
        Foreground = B("App.Text"),
        // App.BorderStrong: the find/goto boxes are editable TextBoxes sitting on the
        // find bar's App.Toolbar fill, and App.Panel is only 1.13:1 against it, so the
        // 1px outline alone says where the typing area is. WCAG 1.4.11 asks 3:1 there.
        BorderBrush = B("App.BorderStrong"),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private void RestartFindDebounce()
    {
        _findDebounce.Stop();
        _findDebounce.Start();
    }

    private void OpenFindBar(bool focusGoto)
    {
        _findBar.IsVisible = true;

        // Closing the bar drops the highlighting but keeps the term in the box
        // (so Ctrl+F Enter repeats the last search); re-opening must put the
        // highlighting back rather than show a term that matches nothing.
        string pending = _findBox.Text ?? string.Empty;
        if (pending.Length > 0 && !string.Equals(pending, _searchTerm, StringComparison.Ordinal))
        {
            ApplySearchTerm(pending);
        }

        TextBox target = focusGoto ? _gotoBox : _findBox;
        target.Focus();
        target.SelectAll();
    }

    private void CloseFindBar()
    {
        _findBar.IsVisible = false;
        _findDebounce.Stop();

        if (_searchTerm.Length > 0)
        {
            // Drop the highlighting with the bar, so a closed search leaves no
            // amber behind; the diff text itself is untouched.
            ApplySearchTerm(string.Empty);
        }

        _editor.Focus();
    }

    // Re-collects the occurrences of a new term and jumps to the first one. No
    // re-render any more: the highlight is a transformer, so only the visible
    // lines are repainted.
    private void ApplySearchTerm(string term)
    {
        if (string.Equals(term, _searchTerm, StringComparison.Ordinal))
        {
            return;
        }

        _searchTerm = term;
        CollectMatches();

        if (_searchMatches.Count > 0)
        {
            SelectMatch(0, scroll: true);
        }
        else
        {
            UpdateMatchCounter();
        }
    }

    private void StepMatch(int step)
    {
        if (_searchMatches.Count == 0)
        {
            UpdateMatchCounter();
            return;
        }

        SelectMatch(_matchIndex < 0 ? (step > 0 ? 0 : _searchMatches.Count - 1) : _matchIndex + step, scroll: true);
    }

    // Moves the "current match" marker. Walking a large result set costs one
    // repaint of the visible lines, whatever the size of the patch.
    private void SelectMatch(int index, bool scroll)
    {
        int count = _searchMatches.Count;
        if (count == 0)
        {
            _matchIndex = -1;
            _searchColorizer.SetCurrent(-1);
            RedrawDiff();
            UpdateMatchCounter();
            return;
        }

        index = ((index % count) + count) % count;   // wrap around both ends

        _matchIndex = index;
        _searchColorizer.SetCurrent(index);

        if (scroll)
        {
            ScrollToLine(_searchMatches[index].Line);
        }

        RedrawDiff();
        UpdateMatchCounter();
    }

    private void UpdateMatchCounter()
    {
        if (_searchTerm.Length == 0)
        {
            _matchCounter.Text = string.Empty;
            ToolTip.SetTip(_matchCounter, null);
            return;
        }

        if (_searchMatches.Count == 0)
        {
            _matchCounter.Text = T("No matches");
            ToolTip.SetTip(_matchCounter, null);
            return;
        }

        _matchCounter.Text = F(T("{0} of {1}"), _matchIndex + 1, _searchMatches.Count);

        // Say so rather than quietly presenting a capped total as the real one.
        ToolTip.SetTip(_matchCounter, _matchesTruncated
            ? F(T("Only the first {0} matches are listed."), _searchMatches.Count)
            : null);
    }

    private void GoToLineFromBox()
    {
        string text = (_gotoBox.Text ?? string.Empty).Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int line) &&
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out line))
        {
            _status.Text = F(T("Not a line number: {0}"), text);
            return;
        }

        int lineCount = Math.Max(1, _editor.Document?.LineCount ?? 1);
        line = Math.Clamp(line, 1, lineCount);

        ScrollToLine(line);
        _status.Text = F(T("Line {0} of {1}"), line, lineCount);
    }

    // ------------------------------------------------------- context lines

    // step: +1 one more line of context, -1 one less. Also cancels "entire file",
    // which would otherwise swallow the change.
    private void ChangeContext(int step)
    {
        if (_options.ShowEntireFile)
        {
            // Setting IsChecked runs the toggle handler, which reloads the diff.
            _entireFileButton.IsChecked = false;
        }

        int context = Math.Clamp(_options.ContextLines + step, 0, DiffDisplayOptions.MaxContextLines);
        if (context == _options.ContextLines)
        {
            return;
        }

        _options.ContextLines = context;
        DiffViewerOptions.Persist();
        _status.Text = F(T("Lines of context: {0}"), context);
        ReloadDiff();
    }

    // ------------------------------------------------------- file commands

    // Only IsEnabled/IsVisible here: the items themselves were built in the
    // constructor (a ContextMenu re-populated from Opening mis-measures).
    private void UpdateFileMenuState()
    {
        bool hasFile = _files.SelectedFile is DiffFileRow && _repoPath is not null;
        bool onDisk = hasFile && File.Exists(SelectedWorkingPath());

        // An artificial row is not a commit: the four commands below all resolve a
        // path "at a revision", so they are disabled rather than offered and left to
        // fail on the sentinel hash. Copy old/new version stays available — it knows
        // the artificial sides (index, HEAD, the file on disk); see CopyFileVersion.
        bool hasCommitObject = hasFile && !IsArtificialMode;

        _openWorkingFileItem.IsEnabled = onDisk;
        _openRevisionFileItem.IsEnabled = hasCommitObject;
        _showInFolderItem.IsEnabled = onDisk;
        _saveAsItem.IsEnabled = hasCommitObject;
        _copyPatchItem.IsEnabled = _currentDiffText.Length > 0;
        _copyPathItem.IsEnabled = hasFile;
        _blameItem.IsEnabled = hasFile;
        _historyItem.IsEnabled = hasFile;

        // Upstream gates the entry on the same condition as "File history"
        // (FileStatusList.ContextMenu.cs:1262) and hides it where no grid is
        // listening; here the host decides by subscribing or not.
        _filterFileInGridItem.IsVisible = FilterFileInGridRequested is not null;
        _filterFileInGridItem.IsEnabled = hasFile;
        _difftoolItem.IsEnabled = hasCommitObject;
        _compareWorkingDirItem.IsEnabled = hasCommitObject;
    }

    /// <summary>Whether the loaded comparison is one of the two artificial rows.</summary>
    private bool IsArtificialMode => _mode is CompareMode.WorkTree or CompareMode.Index;

    // Absolute path of the selected file in the working tree (it may not exist:
    // the file can have been deleted, or belong to an old revision).
    private string? SelectedWorkingPath() =>
        _files.SelectedFile is DiffFileRow row && _repoPath is not null
            ? Path.GetFullPath(Path.Combine(_repoPath, row.Name))
            : null;

    // Opens the working-tree copy in the external editor. Both the git config
    // read and the launch happen off the UI thread (the launch is detached).
    private void OpenSelectedWorkingFile()
    {
        if (SelectedWorkingPath() is not string path || _repoPath is null)
        {
            return;
        }

        string repoPath = _repoPath;
        RunFileCommand(() => _tools.OpenInEditor(path, repoPath));
    }

    private void ShowSelectedInFolder()
    {
        if (SelectedWorkingPath() is not string path)
        {
            return;
        }

        RunFileCommand(() => _tools.ShowInFolder(path));
    }

    // Materialises the file as of the displayed commit into a temp directory and
    // opens that copy — the equivalent of the original's "Open this revision".
    private void OpenSelectedRevisionFile()
    {
        if (_files.SelectedFile is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        string repoPath = _repoPath;
        string commit = _commitHash;
        string name = row.Name;

        RunFileLaunch(async () =>
        {
            byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, commit, name)
                .ConfigureAwait(false);

            string shortHash = commit.Length > 8 ? commit[..8] : commit;
            string dir = Path.Combine(Path.GetTempPath(), "GitExtensions.Avalonia", shortHash);
            Directory.CreateDirectory(dir);

            string temp = Path.Combine(dir, Path.GetFileName(name));
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);

            return _tools.OpenInEditor(temp, repoPath);
        });
    }

    // "Save selected as…": the file's content at the displayed commit, written
    // wherever the picker says. The picker must run on the UI thread; the git
    // read and the write do not.
    private void SaveSelectedAs()
    {
        if (_files.SelectedFile is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        string repoPath = _repoPath;
        string commit = _commitHash;
        string name = row.Name;

        _ = SaveSelectedAsCoreAsync(repoPath, commit, name);
    }

    private async Task SaveSelectedAsCoreAsync(string repoPath, string commit, string name)
    {
        try
        {
            IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                _status.Text = T("No file picker is available on this display.");
                return;
            }

            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("FileStatusList/tsmiSaveAs.Text", "Save selected as..."),
                SuggestedFileName = Path.GetFileName(name),
                ShowOverwritePrompt = true,
            });

            if (target is null)
            {
                return;   // cancelled
            }

            string? destination = target.TryGetLocalPath();
            if (destination is null)
            {
                _status.Text = T("The chosen location is not a local file.");
                return;
            }

            _status.Text = F(T("Saving {0}…"), destination);

            await Task.Run(async () =>
            {
                byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, commit, name)
                    .ConfigureAwait(false);
                await File.WriteAllBytesAsync(destination, bytes).ConfigureAwait(false);
            });

            _status.Text = F(T("Saved {0}"), destination);
        }
        catch (Exception ex)
        {
            _status.Text = F("{0}: {1}", ErrorWord(), ex.Message);
        }
    }

    // Runs a blocking external-tool launch off the UI thread and reports the
    // outcome in the status bar. Never throws into the caller.
    private void RunFileCommand(Func<ExternalToolResult> command) =>
        RunFileLaunch(() => Task.FromResult(command()));

    private void RunFileLaunch(Func<Task<ExternalToolResult>> command) =>
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                ExternalToolResult result = await command().ConfigureAwait(false);
                message = result.Message;
            }
            catch (Exception ex)
            {
                message = F("{0}: {1}", ErrorWord(), ex.Message);
            }

            Dispatcher.UIThread.Post(() => _status.Text = message);
        });

    // ---------------------------------------------------------- diff loading

    // Re-runs the diff of the currently selected file with the current options
    // (called by every toolbar toggle that maps onto a git argument).
    private void ReloadDiff() => LoadSelectedFileDiff();

    // Loads the selected file's patch through DiffTextService, so the toolbar
    // options (-w, --word-diff, encoding) become real git arguments.
    private void LoadSelectedFileDiff()
    {
        if (_files.SelectedFile is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        // A row of the search section is answered with git grep's matching lines, not
        // with a patch: the file need not have changed in this revision at all, and a
        // diff of it would usually be empty — the wrong answer to the click.
        if (_grepRows.Contains(row))
        {
            LoadSelectedGrepMatches(row, _repoPath, _commitHash);
            return;
        }

        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        // A row that carries its own pair belongs to ONE section of a multi-revision
        // comparison, and its patch is the patch of THAT section — clicking a file
        // under "Diff BASE with A" must show base..A, not the pane's own extremes.
        // The pane-level hashes remain the fallback for every ordinary list, whose
        // rows carry no pair at all.
        bool ownPair = row.SecondRev is { Length: > 0 } && !_forceWorkingTreeCompare;
        string commitHash = ownPair ? row.SecondRev! : _commitHash;
        string? baseHash = ownPair ? row.FirstRev : _baseHash;

        DiffTextKind kind = _forceWorkingTreeCompare || _mode == CompareMode.WorkingTree
            ? DiffTextKind.WorkingTree
            : ownPair
                ? DiffTextKind.Range
                : _mode switch
                {
                    CompareMode.Range => DiffTextKind.Range,
                    CompareMode.WorkTree => DiffTextKind.WorkTree,
                    CompareMode.Index => DiffTextKind.Index,
                    _ => DiffTextKind.Commit,
                };

        // IsTracked only ever matters for the working-directory row, where a brand
        // new file has no other side to be compared against.
        DiffTextRequest request = new(
            kind, _repoPath, commitHash, baseHash, row.Name, row.OldName, row.IsTracked);

        // Snapshot the options: they live on the UI thread and the git run does not.
        DiffDisplayOptions options = new()
        {
            IgnoreWhitespace = _options.IgnoreWhitespace,
            ShowNonPrinting = _options.ShowNonPrinting,
            WordDiff = _options.WordDiff,
            EncodingName = _options.EncodingName,
            FontSize = _options.FontSize,
            ContextLines = _options.ContextLines,
            ShowEntireFile = _options.ShowEntireFile,

            // The two git-side viewer settings. Read per patch, like the editor ones:
            // changing them in Settings must show on the next file clicked.
            UseHistogram = _viewerPrefs.UseHistogramDiffAlgorithm,
            OmitUninterestingDiff = _viewerPrefs.OmitUninterestingDiff,
        };

        // The extra flags travel as their own snapshot, for the same reason.
        DiffViewerOptions extras = new()
        {
            IgnoreWhitespaceAtEol = _extras.IgnoreWhitespaceAtEol,
            IgnoreWhitespaceChange = _extras.IgnoreWhitespaceChange,
            TreatAllFilesAsText = _extras.TreatAllFilesAsText,
        };

        // The language of the patch content, for the syntax highlighter.
        _diffPath = row.Name;

        // The old "Loading diff…" line is gone rather than kept: it was a sentence typed
        // into the editor's document, i.e. the wait dressed up as content, and it said
        // nothing the spinner does not say better — no file name, no error, nothing the
        // status line below is not already carrying. What survives is the EMPTYING: the
        // patch on screen belongs to the previously selected file and is a wrong answer,
        // not merely a stale one, so it goes even though the overlay would have dimmed it.
        ShowPlaceholder(string.Empty);
        _patchBusy.Show(LoadingCaption());

        _ = Task.Run(async () =>
        {
            try
            {
                string text = await ExtendedDiffTextService
                    .GetDiffTextAsync(request, options, extras, token)
                    .ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    // Guarded by the token on every exit path, this one included: a
                    // superseded load must NOT hide the overlay, because the load that
                    // superseded it has already asked for the same one and is still
                    // running behind it. Whoever cancels is who takes it down (Clear,
                    // LoadFileList) or who re-shows it (the next LoadSelectedFileDiff).
                    if (!token.IsCancellationRequested)
                    {
                        _patchBusy.Hide();
                        RenderDiff(text);

                        // Show the command that produced the patch, so the effect of
                        // the toolbar toggles (-w, -b, --word-diff, --text) is visible.
                        _status.Text = ExtendedDiffTextService.DescribeCommand(request, options, extras);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another selection/toggle; ignore.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        // A failed load is still a finished one: the message replaces
                        // the wait, it does not sit under it.
                        _patchBusy.Hide();
                        ShowPlaceholder(F("{0}: {1}", ErrorWord(), ex.Message));
                    }
                });
            }
        });
    }

    // Puts a one-line message in the pane (the loading notice, an error) and
    // clears everything that was derived from the previous patch. The copy of the
    // patch the clipboard commands read is deliberately NOT touched here.
    private void ShowPlaceholder(string message)
    {
        _hunkLines.Clear();
        _hunkIndex = -1;
        _searchMatches.Clear();
        _matchIndex = -1;
        _matchesTruncated = false;
        _searchColorizer.SetMatches([]);
        _colorizer.Invalidate();
        _editor.Document = new TextDocument(message);
    }

    /// <summary>
    ///  Hands the patch to the editor.
    ///
    ///  <para>What used to make this method expensive is gone: it built one
    ///  <c>Run</c> per line, plus one per syntax span and per search hit, for the
    ///  WHOLE patch, and every Run is its own text-layout box — about half a
    ///  millisecond per line, before the layout pass that followed it. The colours
    ///  now come from two <c>DocumentColorizingTransformer</c>s that AvaloniaEdit
    ///  runs for the VISIBLE lines only, so all that is left here is building the
    ///  document plus two linear passes over the text: the hunk headers the ▲/▼
    ///  navigation needs, and the search hits the counter needs.</para>
    /// </summary>
    private void RenderDiff(string diffText)
    {
        _currentDiffText = diffText;

        // The block-comment table the colorizer keeps is indexed by line number,
        // and those now mean something else.
        _colorizer.Invalidate();

        _editor.Document = new TextDocument(diffText);
        _editor.CaretOffset = 0;
        _editor.ScrollToHome();

        ApplySyntaxLanguage();
        CollectHunks();
        CollectMatches();

        // A reload (new file, new toggle) rebuilds the match list: put the marker
        // back on the first hit and refresh the counter.
        if (_searchMatches.Count > 0)
        {
            SelectMatch(0, scroll: false);
        }
        else
        {
            UpdateMatchCounter();
        }
    }

    // The line numbers of the "@@ … @@" headers, for ▲/▼. Read straight off the
    // document rather than from a split of the text: the document is what every
    // other line number in this view now refers to, and testing two characters
    // costs nothing where materialising each line as a string would not.
    private void CollectHunks()
    {
        _hunkLines.Clear();
        _hunkIndex = -1;

        if (_editor.Document is not TextDocument document)
        {
            return;
        }

        foreach (DocumentLine line in document.Lines)
        {
            if (line.Length >= 2 &&
                document.GetCharAt(line.Offset) == '@' &&
                document.GetCharAt(line.Offset + 1) == '@')
            {
                _hunkLines.Add(line.LineNumber);
            }
        }
    }

    /// <summary>
    ///  Fills <see cref="_searchMatches"/> with every occurrence of the current
    ///  search term (case-insensitive) and hands the list to the search colorizer.
    ///
    ///  <para>Only one limit is left, <see cref="MaxSearchMatches"/>, and it is
    ///  about the list itself: an incremental search for "e" on a huge patch must
    ///  not allocate without bound. The old caps on <i>highlighting</i> are gone —
    ///  the wash is painted per visible line now, so the total number of hits no
    ///  longer costs anything to draw.</para>
    /// </summary>
    private void CollectMatches()
    {
        _searchMatches.Clear();
        _matchesTruncated = false;
        _matchIndex = -1;

        string term = _searchTerm;
        if (term.Length > 0 && _editor.Document is TextDocument document)
        {
            foreach (DocumentLine line in document.Lines)
            {
                if (line.Length < term.Length)
                {
                    continue;
                }

                if (!ScanLine(document.GetText(line), line.LineNumber, term))
                {
                    _matchesTruncated = true;
                    break;
                }
            }
        }

        _searchColorizer.SetMatches(_searchMatches);
        RedrawDiff();
    }

    // Returns false once the match list is full, which stops the outer walk.
    private bool ScanLine(string text, int lineNumber, string term)
    {
        int from = 0;
        while (from <= text.Length - term.Length)
        {
            int at = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return true;
            }

            _searchMatches.Add(new DiffSearchMatch(lineNumber, at, term.Length));
            from = at + term.Length;

            if (_searchMatches.Count >= MaxSearchMatches)
            {
                return false;
            }
        }

        return true;
    }
}

