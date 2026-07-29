using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
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
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Diff line colours. These used to be literals "tuned for the dark palette",
    // which made them 1.88:1 (added) and 2.90:1 (removed) against the light theme's
    // #F3F3F3 — measurably unreadable. They now come from App.DiffAdded /
    // App.DiffRemoved, whose dark values are exactly the two literals that were
    // here, so the dark theme is unchanged and the light theme gets the darkened
    // pair (4.58:1 and 5.39:1).
    //
    // Resolved lazily and cached: the identity of these two instances is load
    // bearing (the render pass compares with ReferenceEquals), and ThemeManager
    // mutates the resource brush in place, so a hot theme switch repaints without
    // invalidating the cache.
    private static IBrush? _addedBrush;
    private static IBrush? _removedBrush;

    private static IBrush AddedBrush => _addedBrush ??= B("App.DiffAdded");

    private static IBrush RemovedBrush => _removedBrush ??= B("App.DiffRemoved");

    // Syntax highlighting repaints the content of a +/- line with the token
    // colours, so the line's identity moves to a background tint (which is how
    // the original marks added/removed lines too).
    private static readonly IBrush AddedTint = new SolidColorBrush(Color.FromArgb(0x28, 0x6A, 0xC7, 0x76));
    private static readonly IBrush RemovedTint = new SolidColorBrush(Color.FromArgb(0x28, 0xE0, 0x6C, 0x6C));

    // Token colours for the syntax highlighter, from App.Token* — the same move the
    // diff colours above already made, and for the same measured reason: as literals
    // "tuned for the dark palette" the five scored 1.31:1 (number) to 2.29:1 (comment)
    // on the light theme's surfaces. The dark values in ThemeManager are those very
    // literals, except comment/preprocessor, which needed an imperceptible lift to
    // clear AA on the +/- tints below (see the ThemeManager comment).
    //
    // Resolved lazily and cached, exactly like AddedBrush/RemovedBrush: the resource
    // brush INSTANCE is what gets cached, and ThemeManager mutates its Color in place,
    // so a hot theme switch repaints these without touching the cache. Copying into a
    // new SolidColorBrush here would freeze them on the theme that happened to be
    // active first.
    private static IBrush? _keyword;
    private static IBrush? _string;
    private static IBrush? _comment;
    private static IBrush? _number;
    private static IBrush? _preprocessor;

    private static IBrush KeywordBrush => _keyword ??= B("App.TokenKeyword");

    private static IBrush StringBrush => _string ??= B("App.TokenString");

    private static IBrush CommentBrush => _comment ??= B("App.TokenComment");

    private static IBrush NumberBrush => _number ??= B("App.TokenNumber");

    private static IBrush PreprocessorBrush => _preprocessor ??= B("App.TokenPreprocessor");

    // Search highlight: amber for every occurrence, a stronger amber for the one
    // the ▲/▼ navigation currently sits on. Literal colours (like the diff
    // colours above) because the palette has no "highlight" resource.
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xC8, 0x9B, 0x2C));
    private static readonly IBrush CurrentMatchBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x2E));

    // Guard rails for the inline-run highlighter (see RenderDiff): splitting a
    // line into several Runs costs a text-layout box per Run, so on a very large
    // diff we keep the match list (counter + navigation still work) but render
    // the diff as one Run per line, as before.
    private const int MaxHighlightLines = 20_000;
    private const int MaxHighlightMatches = 2_000;

    // Hard cap on the match list itself, so an incremental search for "e" on a
    // huge patch cannot allocate without bound.
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
    private readonly SelectableTextBlock _diff;
    private readonly TextBlock _status;
    private readonly ScrollViewer _diffScroll;

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

    // The term currently highlighted, the (line, column, length) of every
    // occurrence in the rendered text, and the Run each occurrence was rendered
    // into (empty when highlighting was suppressed — see MaxHighlightLines).
    private string _searchTerm = string.Empty;
    private readonly List<(int Line, int Start, int Length)> _searchMatches = [];
    private readonly List<Run> _matchRuns = [];
    private int _matchIndex = -1;
    private bool _highlightSuppressed;

    // Launches the external editor / file manager for the file context menu.
    private readonly ExternalToolService _tools = new();

    // False while the view shows its "nothing loaded yet" placeholder, so a
    // language switch can re-translate that placeholder without clobbering a
    // real status message (a command line, an error) with a stale one.
    private bool _hasCommit;

    // Line indices (into the currently rendered diff) of each hunk header, and
    // where the ▲/▼ navigation currently sits.
    private readonly List<int> _hunkLines = [];
    private int _hunkIndex = -1;

    // Re-runs the load that produced the current file list (the refresh button).
    private Action? _reload;

    private string? _repoPath;
    private string? _commitHash;   // the (right/"new") commit; also the "other" side in Range mode
    private string? _baseHash;     // the ("old"/left) commit in Range mode
    private CompareMode _mode = CompareMode.Commit;
    private CancellationTokenSource? _diffCts;

    // Whether the last file diff was loaded as "commit vs working tree" through
    // the context-menu command, so a toggle re-runs the same comparison.
    private bool _forceWorkingTreeCompare;

    // The raw unified-diff text currently displayed (the SelectableTextBlock's
    // Text is cleared while inlines are rendered, so keep our own copy to copy).
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

        _diff = new SelectableTextBlock
        {
            FontFamily = Monospace,
            FontSize = 12,
            Foreground = B("App.Text"),
            Margin = new Thickness(12, 10, 12, 12),
            TextWrapping = TextWrapping.NoWrap,
        };

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
        _diff.ContextMenu = diffMenu;

        _diff.FontSize = _options.FontSize;

        _diffScroll = new ScrollViewer
        {
            Content = _diff,
            Background = B("App.Window"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

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
                RenderDiff(_currentDiffText);
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

        // Display-only: the patch is already loaded, so this re-renders it instead
        // of re-running git.
        _syntaxButton = ToggleTool(
            "{;}", _extras.SyntaxHighlighting,
            v =>
            {
                _extras.SyntaxHighlighting = v;
                DiffViewerOptions.Persist();
                RenderDiff(_currentDiffText);
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
            BorderBrush = B("App.Border"),
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

        DockPanel diffPane = new();
        DockPanel.SetDock(toolbarBar, Dock.Top);
        DockPanel.SetDock(_findBar, Dock.Top);
        diffPane.Children.Add(toolbarBar);
        diffPane.Children.Add(_findBar);
        diffPane.Children.Add(_diffScroll);

        _status = new TextBlock
        {
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("App.Toolbar"),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Grid split = new()
        {
            Background = B("App.Panel"),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
        };

        Grid.SetColumn(_files, 0);
        _files.Width = 320;

        GridSplitter splitter = new()
        {
            Width = 4,
            Background = B("App.Border"),
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, 1);

        Grid.SetColumn(diffPane, 2);

        split.Children.Add(_files);
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

        // Ctrl+F / Ctrl+G open (and focus) the find bar; F3 walks the matches
        // from anywhere in the view; Esc and Enter only act while the bar is up.
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenFindBar(focusGoto: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.G && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenFindBar(focusGoto: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            StepMatch(shift ? -1 : +1);
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
            }
            else
            {
                CopyDiffText();
            }

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
        _diff.SelectAll();
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
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _baseHash = null;
        _mode = CompareMode.Commit;

        LoadFileList(
            () => DiffService.GetChangedFiles(repoPath, commitHash),
            count => F(ChangedFilesFormat(), commitHash, count),
            F(LoadingFilesFormat(), commitHash));
    }

    /// <summary>
    ///  Loads the changed-files list and per-file diffs for the range
    ///  <paramref name="baseHash"/>..<paramref name="otherHash"/>
    ///  (i.e. <c>git diff &lt;base&gt; &lt;other&gt;</c>).
    /// </summary>
    public void ShowRange(string repoPath, string baseHash, string otherHash)
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
            F(LoadingFilesFormat(), range));
    }

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
            F(LoadingFilesFormat(), range));
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

    // Composed status texts are single formats with placeholders, never
    // assembled from translated fragments: {0} is the comparison being shown
    // (a hash or a range) and {1} the file count.
    private static string ChangedFilesFormat() => T("{0}  —  {1} changed file(s)");

    private static string LoadingFilesFormat() => T("Loading changed files for {0}…");

    // Shared changed-file-list loader: clears the panes, loads the file rows off
    // the UI thread, then hands them to the list, which selects the first row and
    // reports it back through SelectedFileChanged (which dispatches on _mode).
    private void LoadFileList(
        Func<IReadOnlyList<DiffFileRow>> load,
        Func<int, string> statusFor,
        string loadingText)
    {
        // Remembered so the toolbar's refresh button can re-run exactly this load.
        _reload = () => LoadFileList(load, statusFor, loadingText);

        _files.Clear();
        _diff.Inlines?.Clear();
        _diff.Text = string.Empty;
        _currentDiffText = string.Empty;
        _status.Text = loadingText;
        _hasCommit = true;

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DiffFileRow> rows = load();
                Dispatcher.UIThread.Post(() =>
                {
                    _files.SetFiles(rows);
                    _status.Text = statusFor(rows.Count);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = F("{0}: {1}", ErrorWord(), ex.Message));
            }
        });
    }

    // The toolbar's refresh button: re-reads the changed-file list of whatever
    // comparison is on screen (the working-tree one is the one that goes stale).
    private void ReloadFileList() => _reload?.Invoke();

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
        Control face = icon is not null && IconLoader.Image(icon, 16) is Image image
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
        _diff.FontSize = size;
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

    // The diff pane is a uniform monospace block, so a line's offset is simply
    // its index times the measured average line height.
    private void ScrollToLine(int line)
    {
        int lineCount = Math.Max(1, _currentDiffText.Split('\n').Length);
        double height = _diff.Bounds.Height;
        double lineHeight = height > 0 ? height / lineCount : _diff.FontSize * 1.4;
        double y = Math.Max(0, (line * lineHeight) + _diff.Margin.Top - (lineHeight * 2));

        _diffScroll.Offset = new Vector(_diffScroll.Offset.X, y);
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
        BorderBrush = B("App.Border"),
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

        _diff.Focus();
    }

    // Re-renders with a new highlight term and jumps to the first occurrence.
    private void ApplySearchTerm(string term)
    {
        if (string.Equals(term, _searchTerm, StringComparison.Ordinal))
        {
            return;
        }

        _searchTerm = term;
        RenderDiff(_currentDiffText);

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

    // Moves the "current match" marker. Only two Run backgrounds change, so
    // walking a large result set never re-renders the diff.
    private void SelectMatch(int index, bool scroll)
    {
        int count = _searchMatches.Count;
        if (count == 0)
        {
            _matchIndex = -1;
            UpdateMatchCounter();
            return;
        }

        index = ((index % count) + count) % count;   // wrap around both ends

        if (_matchIndex >= 0 && _matchIndex < _matchRuns.Count)
        {
            _matchRuns[_matchIndex].Background = MatchBrush;
        }

        _matchIndex = index;

        if (index < _matchRuns.Count)
        {
            _matchRuns[index].Background = CurrentMatchBrush;
        }

        if (scroll)
        {
            ScrollToLine(_searchMatches[index].Line);
        }

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

        // Say so rather than silently showing an unhighlighted diff.
        ToolTip.SetTip(_matchCounter, _highlightSuppressed
            ? F(T("Too many matches to highlight ({0}); use ▲/▼ to walk them."), _searchMatches.Count)
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

        int lineCount = Math.Max(1, _currentDiffText.Split('\n').Length);
        line = Math.Clamp(line, 1, lineCount);

        ScrollToLine(line - 1);
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

        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        DiffTextKind kind = _forceWorkingTreeCompare || _mode == CompareMode.WorkingTree
            ? DiffTextKind.WorkingTree
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
            kind, _repoPath, _commitHash, _baseHash, row.Name, row.OldName, row.IsTracked);

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

        _diff.Inlines?.Clear();
        _diff.Text = T("FormBrowse/_loading.Text", "Loading diff…");

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
                    if (!token.IsCancellationRequested)
                    {
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
                        _diff.Inlines?.Clear();
                        _diff.Text = F("{0}: {1}", ErrorWord(), ex.Message);
                    }
                });
            }
        });
    }

    // Renders spaces/tabs/CR as visible symbols when the ¶ toggle is on.
    private static string ApplyNonPrinting(string line) => line
        .Replace("\r", "␍", StringComparison.Ordinal)
        .Replace("\t", "→   ", StringComparison.Ordinal)
        .Replace(" ", "·", StringComparison.Ordinal);

    // Colour each diff line: added green, removed red, hunk headers blue,
    // file/meta headers gray. When a search term is active the occurrences are
    // highlighted by splitting the affected lines into several Runs — see
    // CollectMatches for the size limits that turn that off.
    private void RenderDiff(string diffText)
    {
        _currentDiffText = diffText;
        _diff.Text = string.Empty;
        InlineCollection inlines = _diff.Inlines ??= [];
        inlines.Clear();
        _hunkLines.Clear();
        _hunkIndex = -1;
        _matchRuns.Clear();
        _matchIndex = -1;

        string[] rawLines = diffText.Split('\n');

        // What the user actually sees, which is also what the search must match.
        string[] display = new string[rawLines.Length];
        for (int i = 0; i < rawLines.Length; i++)
        {
            display[i] = _options.ShowNonPrinting ? ApplyNonPrinting(rawLines[i]) : rawLines[i];
        }

        bool inlineHighlight = CollectMatches(display);
        int matchCursor = 0;

        // Syntax highlighting obeys the same size rule as the search highlighting:
        // both work by splitting lines into extra Runs, and each Run is its own
        // text-layout box. Past the cap the patch renders one Run per line, as
        // before, rather than making a huge file crawl.
        SyntaxLanguage? language = _extras.SyntaxHighlighting && rawLines.Length <= MaxHighlightLines
            ? DiffSyntaxHighlighter.Detect(_diffPath)
            : null;
        SyntaxState syntaxState = new();
        List<SyntaxSpan> spans = [];

        int lineNumber = -1;
        foreach (string rawLine in rawLines)
        {
            lineNumber++;
            string line = rawLine;
            IBrush? brush = null;

            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("new file", StringComparison.Ordinal) ||
                line.StartsWith("deleted file", StringComparison.Ordinal) ||
                line.StartsWith("rename ", StringComparison.Ordinal) ||
                line.StartsWith("copy ", StringComparison.Ordinal) ||
                line.StartsWith("similarity ", StringComparison.Ordinal))
            {
                brush = B("App.TextDim");
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                brush = B("App.Accent");
                _hunkLines.Add(lineNumber);
            }
            else if (line.StartsWith('+'))
            {
                brush = AddedBrush;
            }
            else if (line.StartsWith('-'))
            {
                brush = RemovedBrush;
            }

            line = display[lineNumber];

            // Only the content lines carry code; the file/hunk headers keep their
            // own colour. A tokenized +/- line moves its identity to a background
            // tint, because its foreground now belongs to the tokens.
            IBrush? lineBackground = null;
            spans.Clear();

            if (language is not null &&
                (brush is null || ReferenceEquals(brush, AddedBrush) || ReferenceEquals(brush, RemovedBrush)))
            {
                // The leading +/-/space is diff syntax, not code.
                int from = rawLine.Length > 0 && rawLine[0] is '+' or '-' or ' ' ? 1 : 0;
                DiffSyntaxHighlighter.Tokenize(language, line, from, syntaxState, spans);

                if (ReferenceEquals(brush, AddedBrush))
                {
                    lineBackground = AddedTint;
                }
                else if (ReferenceEquals(brush, RemovedBrush))
                {
                    lineBackground = RemovedTint;
                }
            }

            // Skip past matches belonging to earlier lines (only possible when
            // highlighting is suppressed, but keeps the cursor honest).
            while (matchCursor < _searchMatches.Count && _searchMatches[matchCursor].Line < lineNumber)
            {
                matchCursor++;
            }

            bool lineHasMatch = inlineHighlight
                && matchCursor < _searchMatches.Count
                && _searchMatches[matchCursor].Line == lineNumber;

            int firstRun = inlines.Count;
            int pos = 0;

            while (lineHasMatch
                && matchCursor < _searchMatches.Count
                && _searchMatches[matchCursor].Line == lineNumber)
            {
                (_, int start, int length) = _searchMatches[matchCursor];

                EmitSpans(inlines, line, pos, start, brush, lineBackground, spans);

                // A match is always exactly one Run, whatever the tokens under it:
                // the ▲/▼ navigation addresses matches by Run.
                Run hit = Segment(line.Substring(start, length), brush, MatchBrush);
                inlines.Add(hit);
                _matchRuns.Add(hit);

                pos = start + length;
                matchCursor++;
            }

            EmitSpans(inlines, line, pos, line.Length, brush, lineBackground, spans);

            // The line break rides on the line's last Run, so an unhighlighted
            // patch still costs exactly one Run per line, as it did before.
            if (inlines.Count == firstRun)
            {
                inlines.Add(Segment("\n", brush, lineBackground));
            }
            else if (inlines[^1] is Run tail)
            {
                tail.Text += "\n";
            }
        }

        // A reload (new file, new toggle) rebuilds the match list: put the
        // marker back on the first hit and refresh the counter.
        if (_searchMatches.Count > 0)
        {
            SelectMatch(0, scroll: false);
        }
        else
        {
            UpdateMatchCounter();
        }
    }

    // Emits line[from..to], splitting it wherever a syntax span applies. The spans
    // are ordered and clipped to the range, so this can be called once per
    // between-matches region of the same line.
    private static void EmitSpans(
        InlineCollection inlines,
        string line,
        int from,
        int to,
        IBrush? baseForeground,
        IBrush? background,
        List<SyntaxSpan> spans)
    {
        if (to <= from)
        {
            return;
        }

        if (spans.Count == 0)
        {
            inlines.Add(Segment(line[from..to], baseForeground, background));
            return;
        }

        int pos = from;
        foreach (SyntaxSpan span in spans)
        {
            int start = Math.Max(span.Start, from);
            int end = Math.Min(span.Start + span.Length, to);
            if (end <= start)
            {
                continue;
            }

            if (start > pos)
            {
                inlines.Add(Segment(line[pos..start], baseForeground, background));
            }

            inlines.Add(Segment(line[start..end], TokenBrush(span.Kind), background));
            pos = end;
        }

        if (pos < to)
        {
            inlines.Add(Segment(line[pos..to], baseForeground, background));
        }
    }

    private static IBrush TokenBrush(SyntaxTokenKind kind) => kind switch
    {
        SyntaxTokenKind.Keyword => KeywordBrush,
        SyntaxTokenKind.String => StringBrush,
        SyntaxTokenKind.Comment => CommentBrush,
        SyntaxTokenKind.Number => NumberBrush,
        _ => PreprocessorBrush,
    };

    private static Run Segment(string text, IBrush? foreground, IBrush? background)
    {
        Run run = new(text);
        if (foreground is not null)
        {
            run.Foreground = foreground;
        }

        if (background is not null)
        {
            run.Background = background;
        }

        return run;
    }

    /// <summary>
    ///  Fills <see cref="_searchMatches"/> with every occurrence of the current
    ///  search term (case-insensitive) in the rendered lines, and decides whether
    ///  the renderer should highlight them inline.
    ///
    ///  <para>Two explicit limits, because a highlight costs Run objects and each
    ///  Run is a separate text-layout box: no inline highlighting past
    ///  <see cref="MaxHighlightLines"/> rendered lines or
    ///  <see cref="MaxHighlightMatches"/> hits, and the match list itself stops at
    ///  <see cref="MaxSearchMatches"/>. Beyond those the counter and the ▲/▼
    ///  navigation keep working (they only need line numbers) and the counter's
    ///  tooltip says the highlighting was dropped.</para>
    /// </summary>
    private bool CollectMatches(string[] display)
    {
        _searchMatches.Clear();
        _highlightSuppressed = false;

        string term = _searchTerm;
        if (term.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < display.Length; i++)
        {
            string line = display[i];
            int from = 0;

            while (from <= line.Length - term.Length)
            {
                int at = line.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                _searchMatches.Add((i, at, term.Length));
                from = at + term.Length;

                if (_searchMatches.Count >= MaxSearchMatches)
                {
                    _highlightSuppressed = true;
                    return false;
                }
            }
        }

        bool inline = display.Length <= MaxHighlightLines && _searchMatches.Count <= MaxHighlightMatches;
        _highlightSuppressed = !inline && _searchMatches.Count > 0;

        return inline;
    }
}
