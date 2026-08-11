using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A read-only <c>git blame</c> view: one row per source line showing the
///  commit (short hash), author, final line number and the line text, in a
///  monospace multi-column list. Heavy git work runs off the UI thread, matching
///  <see cref="DiffView"/>. Built on a <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) is required.
///
///  <para>Upstream's <c>BlameControl</c> (which <c>FormBlame</c> merely hosts —
///  there is no toolbar in this tab) adds three things on top of the grid, all
///  ported here: the context menu (<c>BlameControl.Designer.cs:23-32</c>) with
///  <i>Blame this revision</i> / <i>Blame previous revision</i> /
///  <i>Show changes</i> / <i>Copy to clipboard ▸ hash|message|all info</i>, the
///  commit-details panel above the grid (<c>:20</c>, upstream a <c>CommitInfo</c>,
///  here a reused <see cref="CommitDetailView"/>) and the hover tooltip
///  (<c>blameTooltip</c>, <c>:33</c>) carrying the commit of the line under the
///  pointer.</para>
///
///  <para>Captions go through <see cref="TranslationService"/>. Upstream's
///  <c>FormBlame</c> carries a single trans-unit (the window title) and its blame
///  grid headers are hard-coded in code, so only the columns that do have an
///  upstream equivalent are keyed (<c>FormVerify/columnHash</c>,
///  <c>TranslatedStrings/_author</c>); "Line" and "Text" fall back to the
///  one-argument overload and therefore stay English until a catalogue gains them.
///  The menu captions do have upstream ids (category <c>BlameControl</c>). The
///  header, the menu and the status line are rebuilt on
///  <see cref="TranslationService.LanguageChanged"/>.</para>
/// </summary>
public sealed class BlameView : UserControl
{
    // A property, not a field: a static field initialiser can run before the font
    // manager exists, which would cache the fallback for the life of the process.
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 160;
    private const double DateWidth = 90;
    private const double LineWidth = 60;

    // Upstream keeps the commit-info panel a fixed-size splitter panel
    // (BlameControl.Designer.cs:49-63, SplitterDistance 160).
    private const double DetailHeight = 170;

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;
    private static readonly IBrush MetaBrush = B("App.TextDim");

    // Upstream's whole-commit highlight is the editor background nudged 6% darker
    // (BlameControl.cs:84) — deliberately a shade of the surface, not an accent, so a
    // commit spanning half the file does not repaint the view. "App.PanelAlt" is the
    // palette's shade-of-the-panel entry and is exactly that relationship to
    // "App.Panel", which the list is painted with; the brush instance is mutated in
    // place on a theme switch, so capturing it once still follows the theme.
    private static readonly IBrush CommitHighlightBrush = B("App.PanelAlt");

    // Background of a search hit inside a source line. "App.Selection" is the
    // palette's "this text is picked out" colour and is legible under both themes.
    private static readonly IBrush SearchMatchBrush = B("App.Selection");

    // Index of the source-text cell inside a row grid; the cells are added in
    // column order, so this is also its position in Grid.Children.
    private const int TextColumn = 4;

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private readonly BlameService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly Border _headerHost;

    // Over the blamed lines only. The commit-details panel does its own loading for
    // whatever revision it was pointed at, and the find bar and the column header are
    // chrome that stays true across a re-blame; dimming them would announce a wait
    // that does not concern them.
    private readonly BusyOverlay _busy = new();

    // The commit-details panel: CommitDetailView is reused as-is (its public
    // surface — ShowCommit + CommitNavigated — is all this needs), exactly as
    // MainWindow drives it.
    private readonly CommitDetailView _detail = new();

    private readonly MenuItem _blameThisItem;
    private readonly MenuItem _blamePreviousItem;
    private readonly MenuItem _showChangesItem;
    private readonly MenuItem _copyMenuItem;

    // "View in GitHub", and the separator that has to disappear with it. Built empty
    // and refilled per repository — see RebuildViewInHostMenu.
    private readonly MenuItem _viewInHostItem = new() { IsVisible = false };
    private readonly Separator _viewInHostSeparator = new() { IsVisible = false };

    // The three switches that change what git blame is asked to compute
    // (BlameOptions); upstream carries the same three in FormFileHistory's View menu.
    private readonly MenuItem _ignoreWhitespaceItem;
    private readonly MenuItem _detectCopyInFileItem;
    private readonly MenuItem _detectCopyInAllItem;
    private readonly MenuItem _copyHashItem;
    private readonly MenuItem _copyMessageItem;
    private readonly MenuItem _copyAllItem;

    // Last successful load, kept so a language switch can re-word the status line
    // without re-running git.
    private string? _shownFile;
    private string? _shownCommit;
    private int _shownLines;
    private string? _repoPath;

    // Full hash the blamed revision resolved to, used to word "Blame previous
    // revision" like upstream does (see UpdateMenuState).
    private string _resolvedCommit = string.Empty;

    // Commit currently rendered in the details panel, so re-selecting a line of
    // the same commit does not re-run git (upstream SelectedLineChanged does the
    // same ReferenceEquals check).
    private string? _detailHash;

    // First parent of the selected line's commit, resolved off the UI thread on
    // every selection change so the context menu can enable/disable "Blame
    // previous revision" without doing git work while the popup opens.
    private string? _selectedParent;
    private string? _selectedParentOf;

    // One source per blame request, cancelling the one before it: two quick file
    // changes must not leave file A's lines under file B's status line. Upstream
    // gets the same serialisation from AsyncLoader + CancellationTokenSequence
    // (BlameControl.cs:34,132,151).
    //
    // Deliberately never disposed: the token it owns can still be observed by the
    // git call it just cancelled, and disposing a source while its token is in
    // flight is the very race this guards against. A source with no registered
    // callback and no timer holds no unmanaged resource, so the cost is one small
    // collectable object per blame.
    private CancellationTokenSource? _blameCts;

    // Final line numbers that begin a run of consecutive lines from the same commit,
    // i.e. the only rows whose gutter is printed (see BuildRow).
    private HashSet<int> _bandStarts = [];

    // The commit whose lines are currently tinted, and the one the pointer is over.
    // Upstream drives the tint from the pointer alone (BlameControl.cs:191,223 →
    // HighlightLinesForCommit); this port also drives it from the selection, so the
    // affordance survives keyboard navigation and stays put once the pointer leaves —
    // hence the two fields: _hoverCommit wins while the pointer is inside the list,
    // and the selection is what the view falls back to.
    private string? _highlightedCommit;
    private string? _hoverCommit;

    // ---- find / go to line (upstream gets these for free: both blame panels are
    // FileViewers, BlameControl.cs:126,347). The bar is modelled on DiffView's.
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBox _gotoBox;
    private readonly TextBlock _matchCounter;
    private readonly Button _findPrevButton;
    private readonly Button _findNextButton;
    private readonly Button _findCloseButton;
    private readonly DispatcherTimer _findDebounce;

    private string _searchTerm = string.Empty;

    // Indices into _rows of the lines whose text contains the term, and which of
    // them is the current one (-1 before the first step).
    private readonly List<int> _matches = [];
    private int _matchIndex = -1;

    // The rows on screen, kept so search and go-to-line can work on them without
    // reading back through ItemsSource.
    private IReadOnlyList<BlameLineRow> _rows = [];

    // The encoding the file's bytes are decoded with. Upstream passes the diff
    // viewer's encoding into LoadBlameAsync and re-blames when it changes
    // (BlameControl.cs:117-135); this is the same selector DiffView carries.
    private readonly ComboBox _encodingBox;

    /// <summary>
    ///  Raised with a full commit hash when the user picks "Show changes" for a
    ///  blamed line. Upstream opens <c>FormCommitDiff</c>; the port already shows
    ///  commit diffs in <see cref="DiffView"/>, so the host (MainWindow) decides
    ///  where to route it. Unwired is harmless.
    /// </summary>
    public event Action<string>? ShowChangesRequested;

    /// <summary>
    ///  Forwarded from the embedded <see cref="CommitDetailView"/>: a full commit
    ///  hash the user reached by clicking a parent/child link in the details
    ///  panel. The host may navigate the grid to it. Unwired is harmless.
    /// </summary>
    public event Action<string>? CommitNavigated;

    public BlameView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4),
            Foreground = B("App.TextDim"),
            Background = B("App.Toolbar"),
            Padding = new Thickness(4, 4, 4, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = T("No file loaded."),
        };

        _list = new ListBox
        {
            FontFamily = Monospace,
            FontSize = 12,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderThickness = new Thickness(0),

            // No recycling: each row's cells and its tooltip are built from the
            // row instance, and a recycled container would only get a new
            // DataContext, keeping the previous line's text and tooltip. The
            // flip side is that clearing a container re-runs the template with a
            // null item (Avalonia's ContentPresenter.UpdateChild on unset), so
            // BuildRow must tolerate null.
            ItemTemplate = new FuncDataTemplate<BlameLineRow>((row, _) => BuildRow(row), supportsRecycling: false),
        };

        _blameThisItem = new MenuItem();
        _blameThisItem.Click += (_, _) => BlameSelectedRevision();
        _blamePreviousItem = new MenuItem();
        _blamePreviousItem.Click += (_, _) => BlamePreviousRevision();
        _showChangesItem = new MenuItem();
        _showChangesItem.Click += (_, _) => ShowChangesForSelection();
        _copyHashItem = new MenuItem();
        _copyHashItem.Click += (_, _) => CopyFromSelection(r => r.CommitHash);
        _copyMessageItem = new MenuItem();
        _copyMessageItem.Click += (_, _) => CopyFromSelection(r => r.Summary);
        _copyAllItem = new MenuItem();
        _copyAllItem.Click += (_, _) => CopyFromSelection(r => r.Details);
        _copyMenuItem = new MenuItem
        {
            ItemsSource = new[] { _copyHashItem, _copyMessageItem, _copyAllItem },
        };

        // The blame switches. ToggleType.CheckBox gives the tick upstream's menu items
        // show; the click handler writes the flag and immediately re-runs the blame,
        // because the flag only reaches git on the next invocation.
        _ignoreWhitespaceItem = new MenuItem { ToggleType = MenuItemToggleType.CheckBox };
        _ignoreWhitespaceItem.Click += (_, _) =>
            ApplyBlameOptions(o => o with { IgnoreWhitespace = !o.IgnoreWhitespace });
        _detectCopyInFileItem = new MenuItem { ToggleType = MenuItemToggleType.CheckBox };
        _detectCopyInFileItem.Click += (_, _) =>
            ApplyBlameOptions(o => o with { DetectCopyInFile = !o.DetectCopyInFile });
        _detectCopyInAllItem = new MenuItem { ToggleType = MenuItemToggleType.CheckBox };
        _detectCopyInAllItem.Click += (_, _) =>
            ApplyBlameOptions(o => o with { DetectCopyInAll = !o.DetectCopyInAll });

        // Items (including the submenu's) are built in full here: mutating them
        // from Opening leaves the popup mis-measured. Opening only flips
        // IsEnabled and re-words the "previous revision" header.
        ContextMenu menu = new()
        {
            ItemsSource = new Control[]
            {
                _blameThisItem,
                _blamePreviousItem,
                _showChangesItem,
                new Separator(),
                _copyMenuItem,
                new Separator(),
                _ignoreWhitespaceItem,
                _detectCopyInFileItem,
                _detectCopyInAllItem,
                _viewInHostSeparator,
                _viewInHostItem,
            },
        };
        menu.Opening += (_, _) => UpdateMenuState();
        _list.ContextMenu = menu;

        // A ListBox does not select on right-click, so the menu would act on
        // whatever was selected before. Tunnelling with handledEventsToo: the
        // ListBoxItem's own handler runs first and marks the event handled.
        _list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.SelectionChanged += OnSelectionChanged;

        // Hover drives the whole-commit tint, as upstream's two MouseMove handlers do.
        _list.PointerMoved += OnListPointerMoved;
        _list.PointerExited += (_, _) =>
        {
            _hoverCommit = null;
            RefreshHighlight();
        };

        // Upstream's double click on either blame panel blames the revision of the
        // line under the pointer (BlameControl.cs:71,530-537). DoubleTapped bubbles
        // from the row, and the preceding single click has already selected it.
        _list.DoubleTapped += (_, e) =>
        {
            if (RowFrom(e.Source) is not null)
            {
                BlameSelectedRevision();
            }
        };

        _detail.CommitNavigated += hash => CommitNavigated?.Invoke(hash);

        // ---- find bar (Ctrl+F / Ctrl+G), hidden until asked for -------------
        _findBox = FindTextBox(220);

        // Through a method, not a lambda closing over _findDebounce: the field is
        // assigned further down, and the compiler cannot see that the timer exists
        // long before the first keystroke.
        _findBox.TextChanged += (_, _) => RestartFindDebounce();

        // Walking the matches re-renders the text cells, so an incremental search must
        // not do it on every keystroke.
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce.Stop();
            ApplySearchTerm(_findBox.Text ?? string.Empty);
        };

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

        _findPrevButton = FindButton("▲", () => StepMatch(-1));
        _findNextButton = FindButton("▼", () => StepMatch(+1));
        _findCloseButton = FindButton("✕", CloseFindBar);

        // A WrapPanel, as in DiffView: the go-to-line watermark and the "n of m"
        // counter are translated and grow noticeably in some languages.
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

        // Tunnelling, like DiffView's: the ListBox consumes the arrow keys and the
        // text boxes consume the rest.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _encodingBox = new ComboBox
        {
            ItemsSource = DiffTextService.EncodingNames,
            SelectedItem = DiffTextService.DefaultEncodingName,
            Width = 190,
            FontSize = 12,
            Padding = new Thickness(6, 1, 4, 1),
            MinHeight = 0,
            Margin = new Thickness(8, 4, 8, 4),
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _encodingBox.SelectionChanged += (_, _) =>
        {
            // Only the decoding changes, so the file on screen is blamed again with
            // the same revision — and on the same line, which is what makes trying
            // one encoding after another usable at all.
            if (_repoPath is not null && _shownFile is not null)
            {
                ShowBlame(_repoPath, _shownFile, _shownCommit == "HEAD" ? null : _shownCommit, SelectedLineNumber);
            }
        };

        DockPanel topBar = new();
        DockPanel.SetDock(_encodingBox, Dock.Right);
        topBar.Children.Add(_encodingBox);
        topBar.Children.Add(_status);

        ScrollViewer scroll = new()
        {
            Content = _list,
            Background = B("App.Panel"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _headerHost = new Border
        {
            // App.Border, not Brushes.Gray: the rest of the file is themed and a
            // fixed #808080 rule reads as a hard line on the light panel.
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = BuildHeader(),
        };

        GridSplitter splitter = new()
        {
            ResizeDirection = GridResizeDirection.Rows,
            Background = B("App.Border"),
            Height = 4,
        };

        Grid root = new()
        {
            Background = B("App.Window"),
            RowDefinitions = new RowDefinitions($"Auto,{DetailHeight},Auto,Auto,Auto,*"),
        };
        Grid.SetRow(topBar, 0);
        Grid.SetRow(_detail, 1);
        Grid.SetRow(splitter, 2);
        Grid.SetRow(_findBar, 3);
        Grid.SetRow(_headerHost, 4);
        Panel linesHost = new();
        linesHost.Children.Add(scroll);
        linesHost.Children.Add(_busy);

        Grid.SetRow(linesHost, 5);
        root.Children.Add(topBar);
        root.Children.Add(_detail);
        root.Children.Add(splitter);
        root.Children.Add(_findBar);
        root.Children.Add(_headerHost);
        root.Children.Add(linesHost);

        Content = root;

        Retranslate();

        // Ticks only: nothing is on screen yet, so there is nothing to re-blame.
        ReloadBlameOptions(reblame: false);
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // The event fires on whichever thread finished loading the catalogue, so the
    // relabel is marshalled to the UI thread.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        _headerHost.Child = BuildHeader();
        _status.Text = _shownFile is null ? T("No file loaded.") : StatusLine();

        _blameThisItem.Header = T("BlameControl/blameRevisionToolStripMenuItem.Text", "Blame _this revision");
        _blamePreviousItem.Header = ActualPreviousHeader;
        _showChangesItem.Header = T("BlameControl/showChangesToolStripMenuItem.Text", "_Show changes");
        _copyMenuItem.Header = T("BlameControl/copyToClipboardToolStripMenuItem.Text", "_Copy to clipboard");
        _copyHashItem.Header = T("BlameControl/commitHashToolStripMenuItem.Text", "Commit _hash");
        _copyMessageItem.Header = T("BlameControl/commitMessageToolStripMenuItem.Text", "Commit _message");
        _copyAllItem.Header = T("BlameControl/allCommitInfoToolStripMenuItem.Text", "_All commit info");
        _ignoreWhitespaceItem.Header =
            T("FormFileHistory/ignoreWhitespaceToolStripMenuItem.Text", "Ignore whitespace");
        _detectCopyInFileItem.Header =
            T("FormFileHistory/detectMoveAndCopyInThisFileToolStripMenuItem.Text", "Detect move and copy in this file");
        _detectCopyInAllItem.Header =
            T("FormFileHistory/detectMoveAndCopyInAllFilesToolStripMenuItem.Text", "Detect move and copy in all files");

        // Same upstream ids DiffView uses: both panels are FileViewers there.
        _findBox.Watermark = T("FileViewer/findToolStripMenuItem.Text", "Find...");
        _gotoBox.Watermark = T("FileViewer/goToLineToolStripMenuItem.Text", "Go to line");
        ToolTip.SetTip(_findPrevButton, T("Previous match (Shift+F3)"));
        ToolTip.SetTip(_findNextButton, T("Next match (F3)"));
        ToolTip.SetTip(_findCloseButton, T("Close (Esc)"));
        ToolTip.SetTip(_encodingBox, T("Encoding used to decode the file"));
        UpdateMatchCounter();
    }

    /// <summary>
    ///  Re-reads the three blame switches into the menu ticks and, when
    ///  <paramref name="reblame"/> is set and a file is on screen, blames it again so
    ///  the change is visible at once.
    ///
    ///  <para>Public because the Settings dialog edits the very same flags: after an
    ///  Apply the host can call this to keep an open blame in step (see
    ///  <c>MainWindow</c> wiring note). Called on the UI thread; the flags come from
    ///  upstream's in-memory settings container, not from git.</para>
    /// </summary>
    public void ReloadBlameOptions(bool reblame = true)
    {
        BlameOptions options = BlameOptions.Load();
        _ignoreWhitespaceItem.IsChecked = options.IgnoreWhitespace;
        _detectCopyInFileItem.IsChecked = options.DetectCopyInFile;
        _detectCopyInAllItem.IsChecked = options.DetectCopyInAll;

        if (reblame && _repoPath is not null && _shownFile is not null)
        {
            // Keep the reader where they were: a switch flipped mid-file must not
            // send the view back to line 1 (upstream BlameControl.cs:117-135).
            ShowBlame(_repoPath, _shownFile, _shownCommit == "HEAD" ? null : _shownCommit, SelectedLineNumber);
        }
    }

    // Flips one switch, persists all three and re-blames the file on screen.
    private void ApplyBlameOptions(Func<BlameOptions, BlameOptions> change)
    {
        // Writing the flags goes through upstream's settings container, which may touch
        // disk: keep it off the UI thread, then come back to update the ticks and
        // re-run the blame (which is itself asynchronous).
        BlameOptions updated = change(BlameOptions.Load());
        _ = Task.Run(() =>
        {
            updated.Apply();
            Dispatcher.UIThread.Post(() => ReloadBlameOptions());
        });
    }

    private static string ActualPreviousHeader
        => T("BlameControl/_blameActualPreviousRevision.Text", "_Blame previous revision");

    private static string VisiblePreviousHeader
        => T("BlameControl/_blameVisiblePreviousRevision.Text", "_Blame previous visible revision");

    private string StatusLine() => string.Format(
        T("{0}  —  {1} line(s)  @ {2}"), _shownFile, _shownLines, _shownCommit);

    /// <summary>
    ///  Loads and displays the blame of <paramref name="filePath"/> in the
    ///  repository at <paramref name="repoPath"/> at <paramref name="commit"/>
    ///  (defaults to <c>HEAD</c> when null). Heavy git work runs off the UI thread.
    /// </summary>
    /// <param name="initialLine">
    ///  The 1-based line to select and scroll to once the blame is on screen.
    ///  Upstream opens the blame on the line the caller was reading and keeps it
    ///  across refreshes (<c>BlameControl.cs:117-135</c>); a null means line 1, which
    ///  is only right the first time a file is opened.
    /// </param>
    public void ShowBlame(string repoPath, string filePath, string? commit = null, int? initialLine = null)
    {
        _list.ItemsSource = null;
        _rows = [];
        _matches.Clear();
        _matchIndex = -1;
        _bandStarts = [];
        _shownFile = null;
        if (!string.Equals(_repoPath, repoPath, StringComparison.Ordinal))
        {
            RebuildViewInHostMenu(repoPath);
        }

        _repoPath = repoPath;
        _detailHash = null;
        _selectedParent = null;
        _selectedParentOf = null;
        _highlightedCommit = null;
        _hoverCommit = null;
        // "Blaming {0}…" stays: it names the file, and a blame is the one read in this
        // app that is routinely slow enough for the user to look away and come back
        // wondering what the pane is showing. The overlay carries the "still going"
        // half of that message, which a static line cannot — it looks identical
        // whether git is working or has silently stopped.
        _status.Text = string.Format(T("Blaming {0}…"), filePath);
        _busy.Show();

        // Supersede whatever was in flight. ShowBlame only ever runs on the UI
        // thread, so swapping the field needs no locking.
        CancellationTokenSource? previous = _blameCts;
        CancellationTokenSource cts = new();
        _blameCts = cts;
        try
        {
            previous?.Cancel();
        }
        catch (Exception)
        {
            // An already-cancelled/faulted source is not a reason to refuse the new
            // request.
        }

        CancellationToken token = cts.Token;

        // Read on the UI thread: the combo must not be touched from the worker.
        Encoding encoding = DiffTextService.ResolveEncoding(_encodingBox.SelectedItem as string);

        _ = Task.Run(() =>
        {
            try
            {
                BlameResult result = _service.GetBlameResult(
                    repoPath, filePath, commit, token, options: null, encoding: encoding);
                Dispatcher.UIThread.Post(() =>
                {
                    // Staleness guard: a newer request may have started (and even
                    // finished) between the git call returning and this post being
                    // pumped. The current source is the single source of truth for
                    // "am I still the request the view is waiting for".
                    if (!ReferenceEquals(_blameCts, cts) || token.IsCancellationRequested)
                    {
                        return;
                    }

                    // Behind the same guard as the status line, for the same reason: a
                    // superseded blame must not take down the successor's spinner.
                    _busy.Hide();

                    _bandStarts = ComputeBandStarts(result.Lines);
                    _rows = result.Lines;
                    _list.ItemsSource = result.Lines;
                    _resolvedCommit = result.ResolvedCommit;
                    _shownFile = filePath;
                    _shownCommit = commit ?? "HEAD";
                    _shownLines = result.Lines.Count;
                    _status.Text = StatusLine();

                    // Upstream shows the revision just blamed in the panel until a
                    // line is selected (ProcessBlame → CommitInfo.SetRevisionWithChildren).
                    if (_detailHash != result.ResolvedCommit)
                    {
                        _detailHash = result.ResolvedCommit;
                        _detail.ShowCommit(repoPath, result.ResolvedCommit);
                    }

                    // A term left in the find box keeps meaning something after a
                    // re-blame, so the match set is recomputed against the new lines.
                    if (_searchTerm.Length > 0)
                    {
                        RecomputeMatches();
                    }

                    if (initialLine is { } line)
                    {
                        GoToLine(line, report: false);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer ShowBlame: it owns the status line now.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_blameCts, cts) && !token.IsCancellationRequested)
                    {
                        // A blame that failed — an unreadable path, a bad revision —
                        // has stopped waiting; the error on the status line is the
                        // outcome, and the spinner must not outlive it.
                        _busy.Hide();
                        _status.Text = string.Format(T("Error: {0}"), ex.Message);
                    }
                });
            }
        });
    }

    // First line of every run of consecutive lines from the same commit. The core
    // hands the lines over in file order, so a single pass comparing each line's
    // commit with the previous one is enough.
    private static HashSet<int> ComputeBandStarts(IReadOnlyList<BlameLineRow> lines)
    {
        HashSet<int> starts = new(lines.Count);
        string? previous = null;
        foreach (BlameLineRow line in lines)
        {
            if (line.CommitHash != previous)
            {
                starts.Add(line.LineNumber);
                previous = line.CommitHash;
            }
        }

        return starts;
    }

    // ---- selection ---------------------------------------------------------

    private BlameLineRow? Selected => _list.SelectedItem as BlameLineRow;

    // The row an event landed on, or null when it landed on the list's background.
    private static BlameLineRow? RowFrom(object? source)
        => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as BlameLineRow;

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (RowFrom(e.Source) is { } row)
        {
            _list.SelectedItem = row;
        }
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        string? commit = RowFrom(e.Source) is { IsUncommitted: false } row ? row.CommitHash : null;
        if (commit == _hoverCommit)
        {
            return;
        }

        _hoverCommit = commit;
        RefreshHighlight();
    }

    // ---- whole-commit highlight ---------------------------------------------

    /// <summary>
    ///  Tints every line of one commit, which is what makes "this block came in
    ///  together" readable at a glance (upstream <c>HighlightLinesForCommit</c>,
    ///  <c>BlameControl.cs:226-274</c>). The pointer wins while it is over the list;
    ///  otherwise the selected line's commit is tinted.
    ///
    ///  <para>Only the <see cref="Grid.Background"/> of the already-built rows is
    ///  touched: re-assigning <c>ItemsSource</c> would rebuild every container (and,
    ///  from inside a selection handler, re-enter selection), which is the very
    ///  defect the revision grid was just cured of.</para>
    /// </summary>
    /// <param name="force">
    ///  Repaint even when the highlighted commit did not change. Needed on a
    ///  selection change inside one commit's block: the tint itself is unchanged,
    ///  but which row is left untinted (the selected one) has moved.
    /// </param>
    private void RefreshHighlight(bool force = false)
    {
        string? wanted = _hoverCommit
            ?? (Selected is { IsUncommitted: false } selected ? selected.CommitHash : null);

        if (wanted == _highlightedCommit && !force)
        {
            return;
        }

        _highlightedCommit = wanted;

        foreach (Control container in _list.GetRealizedContainers())
        {
            // The container is a ListBoxItem wrapping the templated row; the grid
            // BuildRow produced is found by its Tag, so no grid the control theme
            // itself may contribute is ever repainted.
            if (container.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Tag is BlameLineRow) is { Tag: BlameLineRow row } grid)
            {
                grid.Background = TintFor(row);
            }
        }
    }

    // Null (transparent) leaves the container's own selection brush visible, so the
    // selected line still reads as selected inside a tinted block.
    private IBrush? TintFor(BlameLineRow row)
        => _highlightedCommit is not null
           && row.CommitHash == _highlightedCommit
           && !ReferenceEquals(row, _list.SelectedItem)
            ? CommitHighlightBrush
            : null;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Only row backgrounds are touched, never ItemsSource: rebinding the items
        // from inside a selection handler is the re-entrancy the revision grid was
        // just fixed for.
        RefreshHighlight(force: true);

        BlameLineRow? row = Selected;
        if (row is null || row.IsUncommitted || _repoPath is null)
        {
            return;
        }

        if (row.CommitHash != _detailHash)
        {
            _detailHash = row.CommitHash;
            _detail.ShowCommit(_repoPath, row.CommitHash);
        }

        // Pre-resolve the parent so the context menu never runs git while opening.
        if (row.CommitHash != _selectedParentOf)
        {
            string repo = _repoPath;
            string hash = row.CommitHash;
            _selectedParentOf = hash;
            _selectedParent = null;
            _ = Task.Run(() =>
            {
                string? parent = null;
                try
                {
                    parent = _service.ResolveParent(repo, hash);
                }
                catch (Exception)
                {
                    // A missing parent is a disabled menu item, never an error.
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (_selectedParentOf == hash)
                    {
                        _selectedParent = parent;
                    }
                });
            });
        }
    }

    // ---- find / go to line --------------------------------------------------

    /// <summary>The 1-based line currently selected, or null when nothing is.</summary>
    private int? SelectedLineNumber => Selected?.LineNumber;

    private TextBox FindTextBox(double width) => new()
    {
        Width = width,
        FontSize = 12,
        MinHeight = 0,
        Padding = new Thickness(6, 2, 6, 2),
        Background = B("App.Panel"),
        Foreground = B("App.Text"),
        BorderBrush = B("App.BorderStrong"),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Button FindButton(string glyph, Action action)
    {
        Button button = new()
        {
            Content = glyph,
            FontSize = 12,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(2, 0, 2, 0),
            Background = B("App.Control"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.BorderStrong"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void RestartFindDebounce()
    {
        _findDebounce.Stop();
        _findDebounce.Start();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (control && e.Key is Key.F or Key.G)
        {
            OpenFindBar(focusGoto: e.Key == Key.G);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            StepMatch(shift ? -1 : +1);
            e.Handled = true;
            return;
        }

        if (!_findBar.IsVisible)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Return)
        {
            if (_gotoBox.IsKeyboardFocusWithin)
            {
                GoToLineFromBox();
                e.Handled = true;
            }
            else if (_findBox.IsKeyboardFocusWithin)
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
            }
        }
    }

    private void OpenFindBar(bool focusGoto)
    {
        _findBar.IsVisible = true;

        // Closing the bar drops the highlighting but keeps the term, so re-opening
        // must put the highlighting back rather than show a term matching nothing.
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
            ApplySearchTerm(string.Empty);
        }

        _list.Focus();
    }

    private void ApplySearchTerm(string term)
    {
        if (string.Equals(term, _searchTerm, StringComparison.Ordinal))
        {
            return;
        }

        _searchTerm = term;
        RecomputeMatches();

        if (_matches.Count > 0)
        {
            SelectMatch(0);
        }
    }

    // Rebuilds the match set from the rows on screen and repaints the text cells.
    private void RecomputeMatches()
    {
        _matches.Clear();
        _matchIndex = -1;

        if (_searchTerm.Length > 0)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Text.Contains(_searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    _matches.Add(i);
                }
            }
        }

        RefreshMatchHighlight();
        UpdateMatchCounter();
    }

    private void StepMatch(int step)
    {
        if (_matches.Count == 0)
        {
            UpdateMatchCounter();
            return;
        }

        int next = _matchIndex < 0
            ? (step > 0 ? 0 : _matches.Count - 1)
            : _matchIndex + step;

        // Wrap around both ends, as the diff viewer's search does.
        SelectMatch(((next % _matches.Count) + _matches.Count) % _matches.Count);
    }

    private void SelectMatch(int index)
    {
        _matchIndex = index;
        int row = _matches[index];
        if (row < _rows.Count)
        {
            _list.SelectedItem = _rows[row];
            _list.ScrollIntoView(row);
        }

        UpdateMatchCounter();
    }

    private void UpdateMatchCounter()
    {
        _matchCounter.Text = _searchTerm.Length == 0
            ? string.Empty
            : _matches.Count == 0
                ? T("No matches")
                : string.Format(T("{0} of {1}"), _matchIndex + 1, _matches.Count);
    }

    private void GoToLineFromBox()
    {
        if (int.TryParse((_gotoBox.Text ?? string.Empty).Trim(), out int line))
        {
            GoToLine(line, report: true);
        }
        else
        {
            _status.Text = T("Enter a line number.");
        }
    }

    /// <summary>
    ///  Selects and scrolls to the 1-based <paramref name="line"/>, clamped to the
    ///  file (upstream <c>FileViewer.GoToLine</c>, used at <c>BlameControl.cs:347</c>).
    /// </summary>
    private void GoToLine(int line, bool report)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        // The blame's final line numbers are 1..N in order, so the row index is the
        // line minus one; the clamp keeps a stale "go to 9999" on the last line.
        int index = Math.Clamp(line, 1, _rows.Count) - 1;
        _list.SelectedItem = _rows[index];
        _list.ScrollIntoView(index);

        if (report)
        {
            _status.Text = string.Format(T("Line {0} of {1}"), index + 1, _rows.Count);
        }
    }

    // Repaints the text cell of every realized row so the search term stands out.
    // ItemsSource is left alone, for the reason spelled out in RefreshHighlight.
    private void RefreshMatchHighlight()
    {
        foreach (Control container in _list.GetRealizedContainers())
        {
            if (container.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Tag is BlameLineRow) is { Tag: BlameLineRow row } grid
                && grid.Children.Count > TextColumn
                && grid.Children[TextColumn] is TextBlock cell)
            {
                FillLineText(cell, row.Text);
            }
        }
    }

    /// <summary>
    ///  Writes a source line into its cell, marking the occurrences of the current
    ///  search term. Plain text when nothing is being searched, so the common case
    ///  costs no inline runs.
    /// </summary>
    private void FillLineText(TextBlock cell, string text)
    {
        cell.Inlines?.Clear();

        int at = _searchTerm.Length == 0
            ? -1
            : text.IndexOf(_searchTerm, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            cell.Text = text;
            return;
        }

        cell.Text = null;
        InlineCollection inlines = cell.Inlines ??= [];

        int cursor = 0;
        while (at >= 0)
        {
            if (at > cursor)
            {
                inlines.Add(new Run(text[cursor..at]));
            }

            inlines.Add(new Run(text.Substring(at, _searchTerm.Length)) { Background = SearchMatchBrush });
            cursor = at + _searchTerm.Length;
            at = text.IndexOf(_searchTerm, cursor, StringComparison.OrdinalIgnoreCase);
        }

        if (cursor < text.Length)
        {
            inlines.Add(new Run(text[cursor..]));
        }
    }

    // ---- context menu ------------------------------------------------------

    // Opening-time work is limited to IsEnabled/Header (adding or removing Items
    // here would leave the popup mis-measured).
    private void UpdateMenuState()
    {
        BlameLineRow? row = Selected;
        bool hasCommit = row is not null && !row.IsUncommitted && _repoPath is not null;

        _blameThisItem.IsEnabled = hasCommit;
        _showChangesItem.IsEnabled = hasCommit;
        _copyMenuItem.IsEnabled = hasCommit;
        _blamePreviousItem.IsEnabled = hasCommit && _selectedParent is not null;

        // Re-read the blame switches every time the menu opens. They are shared state:
        // the Settings dialog writes the same three, and a tick left over from
        // construction would claim the opposite of what the next blame will do (seen on
        // screen: "Ignore whitespace" still ticked right after Settings had cleared it).
        ReloadBlameOptions(reblame: false);

        // Upstream words this item after where the parent it will blame comes
        // from: "previous revision" when the actual parent is present in the
        // revision grid, "previous *visible* revision" otherwise
        // (BlameControl.cs:576-588). BlameView has no grid to ask, so the port
        // uses the only equivalent signal it has: when the line was last touched
        // by the revision being blamed, its parent really is the previous
        // revision; when the line comes from an older commit, this view is not
        // showing what lies between, so "previous visible revision" is the
        // honest wording.
        _blamePreviousItem.Header = row is not null && row.CommitHash == _resolvedCommit
            ? ActualPreviousHeader
            : VisiblePreviousHeader;

        // The one place upstream's repository host actually reaches into a context menu
        // (IRepositoryHostPlugin.ConfigureContextMenu, called with the blame menu and a
        // GitBlameContext). Same URL shape: /blame/<sha>/<file>#L<n>, so the browser
        // lands on the very line that is selected here.
        _viewInHostItem.IsVisible = _viewInHostItem.Items.Count > 0 && hasCommit;
        _viewInHostSeparator.IsVisible = _viewInHostItem.IsVisible;
    }

    /// <summary>
    ///  Fills the "View in GitHub" submenu, one entry per remote on the host. Called
    ///  when the blamed repository changes rather than when the menu opens: adding
    ///  items during <c>Opening</c> leaves the popup mis-measured, which is the rule the
    ///  rest of this menu already follows.
    /// </summary>
    private void RebuildViewInHostMenu(string repoPath)
    {
        _viewInHostItem.Items.Clear();
        _viewInHostItem.Header = TranslationService.TFormat(null, "View in {0}", "GitHub");

        GitHubService service = new();
        foreach (GitHubHostedRemote remote in service.GetHostedRemotes(repoPath))
        {
            GitHubHostedRemote captured = remote;
            MenuItem entry = new() { Header = captured.Data.Replace("_", "__") };
            entry.Click += (_, _) =>
            {
                BlameLineRow? row = Selected;
                if (row is null || row.IsUncommitted || _shownFile is null)
                {
                    return;
                }

                new ExternalToolService().OpenUrl(
                    service.BlameUrl(captured, row.CommitHash, _shownFile, row.LineNumber));
            };
            _viewInHostItem.Items.Add(entry);
        }
    }

    private void BlameSelectedRevision()
    {
        BlameLineRow? row = Selected;
        if (row is null || row.IsUncommitted || _repoPath is null)
        {
            return;
        }

        // Upstream blames the path as it was named in that commit, which git
        // blame --porcelain reports per line (it may have been renamed since), and
        // opens on the line as numbered in that revision, not in this one
        // (BlameControl.cs:126).
        ShowBlame(_repoPath, FileOf(row), row.CommitHash, row.OriginLineNumber > 0 ? row.OriginLineNumber : row.LineNumber);
    }

    private void BlamePreviousRevision()
    {
        BlameLineRow? row = Selected;
        if (row is null || row.IsUncommitted || _repoPath is null)
        {
            return;
        }

        string repo = _repoPath;
        string file = FileOf(row);
        string hash = row.CommitHash;

        // The diff about to be walked is parent(hash) → hash, so the line handed to it
        // has to be numbered as of `hash`, not as of the revision on screen. The
        // porcelain already reports it. (Upstream passes the on-screen line here,
        // which only coincides when the line was last touched by the revision being
        // blamed — BlameControl.cs:566.)
        int line = row.OriginLineNumber > 0 ? row.OriginLineNumber : row.LineNumber;
        string? parent = _selectedParent;

        // Both the parent lookup (when it has not landed yet) and the line mapping
        // are git calls, so the whole thing goes on a worker.
        _ = Task.Run(() =>
        {
            string? resolved = parent;
            if (resolved is null)
            {
                try
                {
                    resolved = _service.ResolveParent(repo, hash);
                }
                catch (Exception)
                {
                    // Fall through to the status message below.
                }
            }

            if (resolved is null)
            {
                Dispatcher.UIThread.Post(() =>
                    _status.Text = string.Format(
                        T("{0} has no previous revision."), hash[..Math.Min(8, hash.Length)]));
                return;
            }

            // Where the selected line sat before this commit touched the file.
            // Without this the parent's blame opens at line 1, which makes walking
            // back through a large file's history pointless — the whole reason the
            // command exists (upstream GitBlameParser.cs:26-88).
            int parentLine = _service.MapLineToParent(repo, hash, resolved, file, line);

            string target = resolved;
            Dispatcher.UIThread.Post(() => ShowBlame(repo, file, target, parentLine));
        });
    }

    private string FileOf(BlameLineRow row)
        => row.OriginFileName.Length > 0 ? row.OriginFileName : _shownFile!;

    private void ShowChangesForSelection()
    {
        BlameLineRow? row = Selected;
        if (row is not null && !row.IsUncommitted)
        {
            ShowChangesRequested?.Invoke(row.CommitHash);
        }
    }

    private void CopyFromSelection(Func<BlameLineRow, string> formatter)
    {
        BlameLineRow? row = Selected;
        if (row is null || row.IsUncommitted)
        {
            return;
        }

        string text = formatter(row);
        if (text.Length == 0)
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    // ---- rows --------------------------------------------------------------

    private static Grid MakeColumns()
        => new()
        {
            ColumnDefinitions = new ColumnDefinitions($"{HashWidth},{AuthorWidth},{DateWidth},{LineWidth},*"),
        };

    private static Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 0, 8, 2);

        AddCell(grid, 0, T("FormVerify/columnHash.HeaderText", "Hash"), bold: true);
        AddCell(grid, 1, T("TranslatedStrings/_author.Text", "Author"), bold: true);
        AddCell(grid, 2, T("TranslatedStrings/_authorDateText.Text", "Author date"), bold: true);
        AddCell(grid, 3, T("Line"), bold: true);
        AddCell(grid, 4, T("Text"), bold: true);

        return grid;
    }

    private Control BuildRow(BlameLineRow? row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 0, 8, 0);

        // Container being cleared (unset content): nothing to render.
        if (row is null)
        {
            return grid;
        }

        // Upstream's gutter is banded: hash, author and date are printed only on the
        // first line of a run of consecutive lines from the same commit, and the rest
        // of the band is left blank (BlameControl.cs:402-405). Repeating the hash on
        // every line — which is what this port used to do — destroys exactly the
        // "where does this commit's block start and end" reading the original gives.
        // Tagged so RefreshHighlight can find this grid again inside its container,
        // and tinted up front so a row scrolled into view while a commit is
        // highlighted arrives already tinted.
        grid.Tag = row;
        grid.Background = TintFor(row);

        bool bandStart = _bandStarts.Contains(row.LineNumber);

        AddCell(grid, 0, bandStart ? row.ShortHash : string.Empty, foreground: MetaBrush);
        AddCell(grid, 1, bandStart ? row.Author : string.Empty, foreground: MetaBrush);

        // The author date comes from the same --porcelain pass (BlameService fills
        // Date), so showing it costs nothing.
        AddCell(grid, 2, bandStart ? row.Date : string.Empty, foreground: MetaBrush);
        AddCell(grid, 3, row.LineNumber.ToString(), foreground: MetaBrush);

        // The text cell carries the search highlighting, so it is filled through
        // FillLineText rather than with a bare string.
        FillLineText(AddCell(grid, TextColumn, string.Empty, trim: false), row.Text);

        // Upstream's blameTooltip shows the commit of the line under the pointer,
        // formatted by GitBlameCommit.ToString() — the same text BlameService
        // already carries in Details, so hovering costs no git work.
        if (row.Details.Length > 0)
        {
            ToolTip.SetTip(grid, row.Details);
            ToolTip.SetShowDelay(grid, 400);
        }

        return grid;
    }

    private static TextBlock AddCell(Grid grid, int column, string text, bool bold = false, bool trim = true, IBrush? foreground = null)
    {
        TextBlock block = new()
        {
            Text = text,
            FontFamily = Monospace,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        };

        if (foreground is not null)
        {
            block.Foreground = foreground;
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
        return block;
    }
}
