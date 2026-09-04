using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal commit window rebuilt to mirror the original Git Extensions dedicated
///  commit form as a self-contained 3-zone layout (it no longer hosts the old
///  working-directory panel, which has been removed):
///  <list type="bullet">
///   <item>LEFT: Unstaged list (top) + Stage/Unstage buttons + Staged list (bottom).</item>
///   <item>RIGHT: read-only monospace diff of the selected file.</item>
///   <item>BOTTOM: commit message box, Amend checkbox, Commit / Commit&amp;push /
///    Reset buttons, and a <c>Staged x/y</c> status line.</item>
///  </list>
///  All git work runs off the UI thread. <see cref="Committed"/> fires on each
///  successful commit; the dialog deliberately does NOT auto-close so the user can
///  make several commits before closing the window.
/// </summary>
public sealed class CommitDialog : Theming.ZoomWindow
{
    private readonly string _repoPath;
    private readonly WorkingDirectoryService _service = new();
    private readonly CommitActionsService _actions = new();

    private readonly ListBox _unstagedList = MakeList();
    private readonly ListBox _stagedList = MakeList();
    private readonly TextBox _messageBox;
    private readonly TextRulerOverlay _messageRuler;
    private readonly CheckBox _amendBox;
    private readonly SelectableTextBlock _diffView;
    private readonly ScrollViewer _diffScroll;
    private readonly TextBlock _gutterView;
    private readonly ScrollViewer _gutterScroll;
    private readonly Border _gutterBorder;
    private readonly TextBlock _statusText;

    // ---- status bar (upstream's statusStrip, FormCommit.Designer.cs:805-909) ----
    // Four independent readings, each with its own refresh trigger:
    //  • committer      = the EFFECTIVE user.name / user.email (FormCommit.cs:2236);
    //  • branch → push  = the branch and where it would be pushed (:833-869);
    //  • Staged x/y     = the two lists' counts (:1820);
    //  • Ln / Col       = the caret, exactly as upstream reports it (:2428-2429).
    private readonly TextBlock _committerText = MakeStatusLabel();
    private readonly TextBlock _branchStatusText = MakeStatusLabel();
    private readonly TextBlock _remoteStatusText = MakeStatusLabel();
    private readonly TextBlock _stagedCountText = MakeStatusLabel();
    private readonly TextBlock _lnColText = MakeStatusLabel();
    private readonly Border _statusBar;

    // Cached readings so a re-caption (language switch) or a list reload can redraw
    // the bar without shelling out to git again.
    private string _committerName = string.Empty;
    private string _committerEmail = string.Empty;
    private string _pushTarget = string.Empty;

    // The caret the bar is reporting. Zero means "no caret yet", which is what
    // upstream's labels are initialised to (Designer.cs:894/908).
    private int _caretLine;
    private int _caretColumn;

    // Conflict (unmerged) support, mirroring the original commit form: unmerged
    // files show up in the unstaged list with a "U" status and get their own
    // context-menu entries, plus a banner while the merge is unresolved.
    private readonly Border _conflictBanner;
    private readonly TextBlock _conflictText;
    private readonly TextBlock _conflictHint;
    private readonly MenuItem _mergetoolItem = new();
    private readonly MenuItem _takeOursItem = new();
    private readonly MenuItem _takeTheirsItem = new();
    private readonly MenuItem _markResolvedItem = new();
    private readonly HashSet<string> _conflictPaths = new(StringComparer.Ordinal);

    // Per-file actions on the unstaged menu. Like the conflict entries above, these
    // are created once and only their IsEnabled is touched while the menu opens.
    // Naming/order follow the original shared file-list menu (FileStatusList's
    // ItemContextMenu, which FormCommit binds): the reset entry sits right below
    // Stage, "Copy path" and the .gitignore block come last, each after a separator.
    // The original's "Reset file(s) to" is a submenu (index / parent); the port keeps
    // the single meaningful choice here — discard back to the index — and reuses the
    // wording already used by the former working-directory panel.
    private readonly MenuItem _discardItem = new();
    private readonly MenuItem _ignorePathItem = new();
    private readonly MenuItem _ignoreExtItem = new();
    private readonly MenuItem _ignoreFolderItem = new();

    // The remaining re-labelable widgets. They are kept in fields so a language
    // switch while the dialog is open can re-caption the whole window in place
    // (ApplyTranslations), the same way MainMenu rebuilds itself.
    private readonly MenuItem _stageItem = new();
    private readonly MenuItem _unstageItem = new();

    // "Copy path" is the shared CopyPathsMenuItem, not a plain entry: upstream's
    // FileStatusList offers the flavour sub-menu here too, and its default — the one
    // the parent command uses, shown in bold — is the ABSOLUTE native path. This
    // dialog used to copy git's repo-relative path instead, which is the wrong thing
    // to paste into a shell or a file manager. Built in the constructor because the
    // item needs the list it reads its selection from.
    private readonly CopyPathsMenuItem _unstagedCopyItem;
    private readonly CopyPathsMenuItem _stagedCopyItem;

    // Upstream binds ONE shared menu (FileStatusList's ItemContextMenu) to whichever
    // file list has focus. This dialog owns two lists and a MenuItem cannot live in
    // two menus at once, so the shared block is instantiated once per list.
    private readonly FileEntries _unstagedExtras = new();
    private readonly FileEntries _stagedExtras = new();

    // .git/info/exclude sits next to the .gitignore block and, like it, only makes
    // sense for an UNTRACKED file, so it belongs to the unstaged menu alone.
    private readonly MenuItem _excludePathItem = new();

    // How many files git is currently hiding because of skip-worktree /
    // assume-unchanged. Refreshed by Reload (off the UI thread) because those files
    // appear in neither list and the menu must not shell out while it opens.
    private int _hiddenByIndexFlag;

    // Per-hunk / per-line entries on the diff panel's own context menu (the port's
    // answer to `git add -p`). Like every other menu here the Items are fixed and
    // only IsEnabled / IsVisible move while the menu opens.
    private readonly MenuItem _stageHunkItem = new();
    private readonly MenuItem _stageLinesItem = new();
    private readonly MenuItem _unstageHunkItem = new();
    private readonly MenuItem _unstageLinesItem = new();
    private readonly MenuItem _discardHunkItem = new();
    private readonly MenuItem _discardLinesItem = new();
    private readonly MenuItem _selectAllLinesItem = new();
    private readonly MenuItem _copyDiffItem = new();
    private ContextMenu _diffMenu = new();

    /// <summary>
    ///  How a file list is ordered. Upstream's <c>FileStatusList</c> toolbar offers the
    ///  same three keys (<c>btnByPath</c> / <c>btnByExtension</c> / <c>btnByStatus</c>,
    ///  FileStatusList.Toolbar.cs:173-175); the tree/flat variants it pairs them with
    ///  need a node model these flat lists do not have.
    /// </summary>
    // Upstream groups its file lists by one of three keys (FileStatusList's btnByPath /
    // btnByExtension / btnByStatus), with the path grouping shown either as a tree of
    // folders or as one header per folder. The port used the same three keys as a SORT
    // order, which is the same information without the headers that make a long list
    // readable — so they are grouping keys now, and "no grouping" is a flat sorted list.
    private enum FileSortMode
    {
        Path,
        Extension,
        Status,
    }

    // A group node of a file list. Key identifies it for the collapsed set — for the
    // path tree that is the folder path, so collapsing survives a refresh.
    private sealed record GroupHeader(string Key, string Text, int Level, int Count, bool Collapsed);

    // How a file row is drawn inside a group: how far it is indented, and whether the
    // folder part of its path is already said by the header above it. Kept beside the
    // row rather than in it, because WorkingDirFileRow is the service's type and knows
    // nothing about lists. A weak table, so nothing is held alive after a reload.
    private sealed record RowLayout(int Indent, bool NameOnly);

    private static readonly ConditionalWeakTable<WorkingDirFileRow, RowLayout> RowLayouts = new();

    /// <summary>
    ///  One file list plus the toolbar above it. Upstream gives EACH list its own
    ///  toolbar and its own "Filter files using a regular expression…" box; the port
    ///  used to have a single box driving both sides (PORTING 12.A.4, divergences 1-2).
    ///  <para>The filter is upstream's <c>selectionFilter</c>: a regular expression that
    ///  SELECTS the matching rows, throttled by 250 ms, with the pattern error surfaced
    ///  in a tooltip. The invalid-pattern outline sits on the COUNTER, not on the
    ///  TextBox: Fluent's focus border draws over a TextBox's own border, so the red
    ///  went invisible exactly while the user was typing.</para>
    /// </summary>
    private sealed class FileListPane(ListBox list, bool staged)
    {
        public readonly ListBox List = list;
        public readonly bool Staged = staged;
        public readonly TextBox FilterBox = new() { MinWidth = 90 };
        public readonly TextBlock CountText = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };

        public Border CountBox = new();
        public readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        /// <summary>
        ///  Every row this pane holds — which is NOT the same as the rows its
        ///  <see cref="List"/> is showing, because a collapsed group keeps its subtree
        ///  out of the items (see <c>BuildItems</c>).
        ///
        ///  <para><b>Why it exists.</b> The items used to be the only record of the rows,
        ///  and everything that needed them read <c>List.Items.OfType&lt;WorkingDirFileRow&gt;()</c>.
        ///  Collapsing a folder therefore did not hide its files, it DISCARDED them: the
        ///  next rebuild started from what was left on screen, so re-expanding showed an
        ///  empty folder, and with the only top-level folder collapsed the pane emptied
        ///  itself. The same read also decided the pane's counter and what
        ///  <c>Stage all</c> / <c>Unstage all</c> acted on, so a collapsed group silently
        ///  narrowed both (M226).</para>
        /// </summary>
        public IReadOnlyList<WorkingDirFileRow> Rows = [];

        // The last applied pattern, empty when the filter is off. Non-empty ONLY while
        // it compiles, so "filter active" and "pattern usable" are the same condition.
        public string Pattern = string.Empty;

        // Null = no grouping, a flat list sorted by path.
        public FileSortMode? Group;
        public bool AsTree = true;
        public readonly HashSet<string> Collapsed = new(StringComparer.Ordinal);

        public Button RefreshButton = new();
        public Button CollapseButton = new();
        public Button AsTreeButton = new();
        public Button GroupMenuButton = new();
        public ToggleButton ByPathButton = new();
        public ToggleButton ByExtensionButton = new();
        public ToggleButton ByStatusButton = new();
        public Button? SettingsButton;

        // The filter row, hidden while the list is empty — upstream's
        // FileStatusList.SetFileStatusListVisibility(showNoFiles) does exactly that, so
        // an empty pane is the "no changes" line alone.
        public Control FilterRow = new Border();

        // Upstream's selectionFilter is a ToolStripComboBox, so the patterns already
        // used are one click away. The port keeps them for the life of the dialog.
        public readonly List<string> History = [];
        public Button HistoryButton = new();

        public bool FilterActive => Pattern.Length > 0;
    }

    private readonly FileListPane _unstagedPane;
    private readonly FileListPane _stagedPane;

    // Upstream's tsmiShowUntrackedFiles (FileStatusList.Toolbar.cs:355), which it backs
    // with `git status -uno`. Here the rows are already loaded, so the toggle hides them
    // from the unstaged list — and, because "Stage all" acts on the rows the list shows,
    // an untracked file that is hidden is also not staged by it.
    // The 72-character cut upstream's message-menu labels use. How MANY messages the
    // menu offers is AppSettings.CommitDialogNumberOfPreviousMessages, now a setting
    // (_prefs.CommitDialogNumberOfPreviousMessages) rather than the constant it was.
    private const int MaxMessageLabel = 72;

    // Where the first character sits inside the message box, shared with the overlay
    // that has to line up with it. Set explicitly instead of inherited from the theme
    // so the two cannot drift apart when the style changes.
    private static readonly Thickness MessagePadding = new(6, 4);

    // "Show only my messages" of the message drop-down. Upstream keeps it in the menu
    // item alone, so it lasts as long as the dialog does; same here.
    private bool _onlyMyMessages;

    private bool _showUntracked = true;

    private readonly Button _stageBtn;
    private readonly Button _unstageBtn;
    private readonly Button _stageAllBtn;
    private readonly Button _unstageAllBtn;
    private readonly Button _commitBtn;
    private readonly Button _commitPushBtn;
    private readonly Button _stashBtn;
    private readonly Button _resetAllBtn;
    private readonly Button _resetUnstagedBtn;
    // Upstream's FileStatusList.NoFiles: an italic line in the middle of an empty list
    // ("There are no staged changes"), which is what tells the user the pane is empty on
    // purpose rather than still loading.
    private readonly TextBlock _unstagedEmpty = MakeEmptyLabel();
    private readonly TextBlock _stagedEmpty = MakeEmptyLabel();

    private readonly Dictionary<Button, Action<Button>> _toolbarActions = [];
    private Button _toolbarOverflowBtn = null!;
    private OverflowPanel _commitToolbar = null!;
    private readonly Button _messageMenuBtn;
    private readonly Button _templatesBtn;
    private readonly Button _createBranchBtn;
    private readonly Button _optionsBtn;

    // Upstream's Cancel button (FormCommit.Designer.cs:142-151), which is also the
    // form's CancelButton — so it doubles as the Escape handler. It only closes:
    // upstream asks nothing back, not even with a message typed.

    // The branch shown in the title bar, remembered so the title can be rebuilt
    // (translated format string) without asking git again.
    private string _titleBranch = string.Empty;

    private bool _busy;

    // Merge state, refreshed by every Reload off the UI thread. When a merge is in
    // progress (MERGE_HEAD exists in the *resolved* git directory) a commit is legal
    // even with an empty index diff — resolving every conflict as "ours" leaves the
    // index identical to HEAD, and the original form still lets the merge be recorded.
    private bool _mergeInProgress;

    // The MERGE_MSG text last pushed into the message box, so a later Reload can
    // refresh it without ever overwriting something the user typed.
    private string _prefilledMergeMessage = string.Empty;

    // Guards the programmatic cross-list selection reset against re-entrancy.
    private bool _syncingSelection;

    // ---- diff panel / line-patching state ----

    // One entry per line of the diff currently on screen. Two coordinate systems
    // are needed and they are NOT the same:
    //  • Render* are offsets into the text the SelectableTextBlock lays out, which
    //    is what its SelectionStart / SelectionEnd refer to;
    //  • Source* are offsets into _diffText, the untouched bytes git produced,
    //    which is what PatchManager must be given.
    // They drift apart on CRLF files: a '\r' is dropped for display but has to stay
    // in the patch, otherwise the removed lines no longer match the blob.
    private readonly record struct DiffLineSpan(
        int RenderStart,
        int RenderLength,
        int SourceStart,
        int SourceLength,
        int HunkIndex);

    private DiffLineSpan[] _diffSpans = [];

    // The exact string the patch is cut from: a CLEAN diff (no colour, no -w, no
    // word-diff, no textconv), rendered verbatim so the offsets above are valid.
    private string _diffText = string.Empty;

    // Which file the panel is showing, and on which side. Line patching is only
    // offered while these are set and the lists have not moved underneath.
    private string _diffPath = string.Empty;
    private bool _diffStaged;
    private bool _diffFileIsNew;
    private bool _diffFileIsRenamed;

    // Sequence number of the LoadDiff in flight, so a slow diff cannot land on top
    // of a newer one and leave _diffText describing the wrong file.
    private int _diffToken;

    // Last non-empty selection seen in the diff panel. Right-clicking may collapse
    // the caret depending on how the platform delivers the press, so the menu falls
    // back to this rather than silently acting on nothing.
    private int _lastSelStart;
    private int _lastSelLength;

    // Character index under the pointer at the last right-click, hit-tested against
    // the diff's text layout. -1 when the menu was not opened by pointer.
    private int _pointerCaret = -1;

    // Selection snapshot taken when the context menu opens, used by the click
    // handlers so they cannot act on a range the user no longer sees highlighted.
    private int _menuSelFirstLine = -1;
    private int _menuSelLastLine = -1;

    // After a partial stage the file is usually still in both lists; remember what
    // to re-select so the next Reload puts the user back where they were instead of
    // blanking the diff panel.
    private string? _reselectPath;
    private bool _reselectStaged;

    // Options-menu state (mirrors the original commit form's Options dropdown).
    // Amend lives in _amendBox so the visible checkbox and the menu stay in sync.
    // --signoff / --no-verify / --reset-author are per-commit choices: upstream does
    // NOT persist them either (FormCommit's handlers only flip the check mark), and
    // silently re-applying --no-verify in a later session would be a nasty surprise.
    private bool _signOff;
    private bool _noVerify;
    private bool _resetAuthor;

    // These four ARE persisted, as upstream persists them in AppSettings. They live
    // in app-settings.json rather than in UiState, which is a single shared instance
    // owned elsewhere in the port.
    private readonly SettingsService _settings = new();
    private AppPreferences _prefs;

    // Re-entrancy guard for FormatMessage: assigning Text raises the very event that
    // called it. _formatScheduled additionally collapses a burst of text changes into
    // one posted formatting pass.
    private bool _formattingMessage;
    private bool _formatScheduled;

    // One-shot: set when a commit succeeded and "close after all files committed" is
    // on, consumed by the Reload that follows — only then is it known whether
    // anything is left unstaged.
    private bool _closeIfNothingLeft;

    /// <summary>Raised on each successful commit so the owner can refresh.</summary>
    public event Action? Committed;

    public CommitDialog(string repoPath)
    {
        _repoPath = repoPath;
        _prefs = _settings.Load();

        Width = 1000;
        Height = 680;

        // Floor for the button rows: below this the wrapped rows start stacking one
        // caption per line, which is ugly but still fully usable — narrower than
        // this and even a single translated caption would be clipped.
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // The pane toolbars are bars, so their buttons look like the main toolbar's.
        // Installed on the window itself, before the buttons are built.
        Theming.BarButtonStyles.Apply(Styles);

        // The dialog's own action buttons (Commit, Commit & push, the two resets, the
        // stash) are not on a bar: they get a raised fill instead of an outline.
        // Modern only — the framed button IS the classic look, and the dialog is built
        // fresh on every opening, so the check is made at the right moment.
        if (Theming.ThemeManager.CurrentStyle == Theming.AppStyle.Modern)
        {
            Theming.BarButtonStyles.ApplyActions(Styles);
        }

        // ---- RIGHT: diff view ----
        _diffView = new SelectableTextBlock
        {
            FontFamily = Monospace,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            Margin = new Thickness(6),

            // Pinned to the top and given an EXPLICIT line height, both shared with the
            // gutter next to it. Left to their defaults the two blocks lay out at a
            // different pitch — measured on :215, 19.0 px per line here against 17.9 in
            // the gutter — so the numbers drifted a full line away over 15 lines.
            VerticalAlignment = VerticalAlignment.Top,
            LineHeight = DiffLineHeight,
        };
        _diffScroll = new ScrollViewer
        {
            Content = _diffView,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush("App.Panel", Brushes.Black),
            ClipToBounds = true,
        };

        // The two-column line-number gutter the original diff viewer shows (old line /
        // new line). It is a control of its OWN, deliberately not part of _diffView's
        // text: the line-staging code maps SelectionStart/End of that text onto byte
        // offsets in the patch, so prefixing the rendered lines with numbers would
        // shift every offset and break `git apply` (PORTING 12.A.4).
        _gutterView = new TextBlock
        {
            FontFamily = Monospace,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Right,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            Margin = new Thickness(6, 6, 6, 6),
            VerticalAlignment = VerticalAlignment.Top,
            LineHeight = DiffLineHeight,
        };

        // Pinned horizontally (it must not scroll away with a wide diff) and driven
        // vertically by the diff's own offset.
        _gutterScroll = new ScrollViewer
        {
            Content = _gutterView,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Background = Brush("App.Panel", Brushes.Black),
            ClipToBounds = true,
        };
        _diffScroll.ScrollChanged += (_, _) =>
            _gutterScroll.Offset = _gutterScroll.Offset.WithY(_diffScroll.Offset.Y);

        // ---- LEFT: unstaged / buttons / staged ----
        _unstagedList.SelectionChanged += (_, _) => OnSelected(_unstagedList, staged: false);
        _stagedList.SelectionChanged += (_, _) => OnSelected(_stagedList, staged: true);

        // A group header is a node, not a file: clicking it folds its subtree, and it
        // never joins the selection the stage / unstage / diff code works from.
        _unstagedList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        _stagedList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        _unstagedList.ContextRequested += OnListContextRequested;
        _stagedList.ContextRequested += OnListContextRequested;
        _unstagedList.DoubleTapped += (_, _) => OnUnstagedDoubleTapped();
        _stagedList.DoubleTapped += (_, _) => UnstageSelected();

        // The Items are static and only their IsEnabled changes while opening —
        // adding/removing entries in Opening leaves the popup unmeasured (HANDOFF §3).
        _mergetoolItem.Click += (_, _) => OpenInMergetool();
        _takeOursItem.Click += (_, _) => ResolveConflicts("ours");
        _takeTheirsItem.Click += (_, _) => ResolveConflicts("theirs");
        _markResolvedItem.Click += (_, _) => ResolveConflicts("resolved");

        // One flavour sub-menu per list: a MenuItem can only live in one menu, and this
        // dialog owns two lists (upstream binds its single FileStatusList menu to
        // whichever list has focus instead).
        _unstagedCopyItem = MakeCopyPathsItem(_unstagedList);
        _stagedCopyItem = MakeCopyPathsItem(_stagedList);

        _stageItem.Click += (_, _) => StageSelected();

        _discardItem.Click += (_, _) => DiscardSelected();
        _ignorePathItem.Click += (_, _) => AddSelectedToGitignore(GitignoreMode.Path);
        _ignoreExtItem.Click += (_, _) => AddSelectedToGitignore(GitignoreMode.Extension);
        _ignoreFolderItem.Click += (_, _) => AddSelectedToGitignore(GitignoreMode.Folder);
        _excludePathItem.Click += (_, _) => AddSelectedToInfoExclude();

        WireFileEntries(_unstagedExtras, _unstagedList, staged: false);
        WireFileEntries(_stagedExtras, _stagedList, staged: true);

        ContextMenu unstagedMenu = new()
        {
            ItemsSource = new Control[]
            {
                _stageItem,
                _discardItem,
                new Separator(),
                _mergetoolItem, _takeOursItem, _takeTheirsItem, _markResolvedItem,
                new Separator(),
            }
            .Concat(FileEntryControls(_unstagedExtras))
            .Concat(new Control[]
            {
                new Separator(),
                _unstagedCopyItem,
                new Separator(),
                _ignorePathItem, _ignoreExtItem, _ignoreFolderItem, _excludePathItem,
                new Separator(),
            })
            .Concat(FileFlagControls(_unstagedExtras))
            .Concat(new Control[]
            {
                new Separator(),
                _unstagedExtras.Refresh,
            })
            .ToArray(),
        };
        unstagedMenu.Opening += (_, _) =>
        {
            bool conflict = SelectedConflicts().Count > 0;
            List<WorkingDirFileRow> rows = SelectedRows(_unstagedList);
            int count = rows.Count;
            _stageItem.IsEnabled = !conflict && count > 0;
            _stageItem.Header = WithCount(StageCaption, count);
            _unstagedCopyItem.IsEnabled = count > 0;
            _unstagedCopyItem.Header = WithCount(CopyPathCaption, count);

            // "Reset file changes" only makes sense for tracked, non-conflicted files:
            // untracked ones are handled by .gitignore / clean, never discarded here.
            int discardable = rows.Count(r => r.Status != "new" && !_conflictPaths.Contains(r.Path));
            _discardItem.IsEnabled = !conflict && discardable > 0;
            _discardItem.Header = WithCount(DiscardCaption, discardable);

            // The .gitignore entries mirror the former working-directory panel: a single UNTRACKED
            // file only, plus an extension / a parent folder where applicable.
            WorkingDirFileRow? untracked = SingleUntracked();
            string path = (untracked?.Path ?? string.Empty).Replace('\\', '/');
            _ignorePathItem.IsEnabled = untracked is not null;
            _ignoreExtItem.IsEnabled = untracked is not null
                && System.IO.Path.GetExtension(path).TrimStart('.').Length > 0;
            _ignoreFolderItem.IsEnabled = untracked is not null && path.LastIndexOf('/') > 0;
            _excludePathItem.IsEnabled = untracked is not null;

            // The merge tool opens one file at a time, so it stays single-selection
            // only; taking a side / marking resolved already loops over the selection.
            _mergetoolItem.IsEnabled = conflict && count == 1;
            _takeOursItem.IsEnabled = conflict;
            _takeTheirsItem.IsEnabled = conflict;
            _markResolvedItem.IsEnabled = conflict;

            UpdateFileEntries(_unstagedExtras, _unstagedList, staged: false);
        };
        _unstagedList.ContextMenu = unstagedMenu;

        _unstageItem.Click += (_, _) => UnstageSelected();
        ContextMenu stagedMenu = new()
        {
            ItemsSource = new Control[] { _unstageItem, new Separator() }
                .Concat(FileEntryControls(_stagedExtras))
                .Concat(new Control[]
                {
                    new Separator(),
                    _stagedCopyItem,
                    new Separator(),
                })
                .Concat(FileFlagControls(_stagedExtras))
                .Concat(new Control[]
                {
                    new Separator(),
                    _stagedExtras.Refresh,
                })
                .ToArray(),
        };
        stagedMenu.Opening += (_, _) =>
        {
            int count = SelectedRows(_stagedList).Count;
            _unstageItem.IsEnabled = count > 0;
            _unstageItem.Header = WithCount(UnstageCaption, count);
            _stagedCopyItem.IsEnabled = count > 0;
            _stagedCopyItem.Header = WithCount(CopyPathCaption, count);
            UpdateFileEntries(_stagedExtras, _stagedList, staged: true);
        };
        _stagedList.ContextMenu = stagedMenu;

        BuildDiffMenu();

        _conflictText = new TextBlock
        {
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
        };
        // The explanatory sentence is a line of its own, never glued onto the
        // upstream one. Concatenating "translated sentence" + ". " + "English
        // sentence" produced a stray period at the start of the wrapped second
        // line in every language whose translation is longer than English.
        _conflictHint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
        };
        _conflictBanner = new Border
        {
            Background = Brush("App.Accent", Brushes.DarkRed),
            Margin = new Thickness(6, 6, 6, 0),
            Padding = new Thickness(8, 4),
            IsVisible = false,
            ClipToBounds = true,
            Child = new StackPanel { Children = { _conflictText, _conflictHint } },
        };

        _stageBtn = MakeButton(StageSelected);
        _unstageBtn = MakeButton(UnstageSelected);
        _stageAllBtn = MakeButton(StageAll);
        _unstageAllBtn = MakeButton(UnstageAll);

        // Upstream's toolbarStaged: the stage / unstage commands live in a strip of
        // their own at the TOP of the staged pane (FormCommit.Designer.cs:313-319 —
        // toolStageAllItem, toolStageItem, toolUnstageAllItem, toolUnstageItem), not in
        // a band between the two lists. A WrapPanel and not a horizontal StackPanel:
        // "Stage all" / "Unstage all" become "Inserisci tutto nello stage" / "Rimuovi
        // tutto dallo stage" in Italian (longer still in German) and a StackPanel simply
        // overflowed the left column, pushing the last button past the dialog border.
        // The strip reads exactly like upstream's: the unstage pair on the LEFT (the
        // "all" button image-only, then the icon-and-text one) and the stage pair pushed
        // to the RIGHT, all of them flat like ToolStrip buttons rather than framed.
        StackPanel unstageGroup = new()
        {
            Orientation = Orientation.Horizontal,
            Children = { _unstageAllBtn, _unstageBtn },
        };
        StackPanel stageGroup = new()
        {
            Orientation = Orientation.Horizontal,
            Children = { _stageBtn, _stageAllBtn },
        };
        DockPanel stageButtons = new() { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(stageGroup, Dock.Right);
        DockPanel.SetDock(unstageGroup, Dock.Left);
        stageButtons.Children.Add(stageGroup);
        stageButtons.Children.Add(unstageGroup);
        foreach (Button b in new[] { _stageBtn, _unstageBtn, _stageAllBtn, _unstageAllBtn })
        {
            b.Margin = new Thickness(0, 0, 2, 0);
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(6, 2);
        }

        _unstagedPane = new FileListPane(_unstagedList, staged: false);
        _stagedPane = new FileListPane(_stagedList, staged: true);
        Control unstagedToolbar = BuildPaneToolbar(_unstagedPane);
        Control stagedToolbar = BuildPaneToolbar(_stagedPane);

        // ---- LEFT column: unstaged over staged (upstream's splitLeft) ----
        Grid leftPanel = new()
        {
            RowDefinitions = new RowDefinitions("*,4,*"),
        };
        leftPanel.Children.Add(WrapWithToolbars(Overlay(_unstagedList, _unstagedEmpty), 0, unstagedToolbar));
        GridSplitter leftSplitter = new()
        {
            Height = 4,
            ResizeDirection = GridResizeDirection.Rows,

            // Transparent, not App.Border: a 4px painted band is a pale stripe across
            // the dialog, and MainWindow's splitters have never drawn one. Transparent
            // rather than null so the grip still takes the pointer.
            Background = Brushes.Transparent,
        };
        Grid.SetRow(leftSplitter, 1);
        leftPanel.Children.Add(leftSplitter);
        leftPanel.Children.Add(
            WrapWithToolbars(Overlay(_stagedList, _stagedEmpty), 2, stageButtons, stagedToolbar));

        _gutterBorder = new Border
        {
            Child = _gutterScroll,
            BorderBrush = Brush("App.Rule", Brushes.Gray),
            BorderThickness = new Thickness(0, 0, 1, 0),
            ClipToBounds = true,
        };

        Grid diffWithGutter = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_gutterBorder, 0);
        Grid.SetColumn(_diffScroll, 1);
        diffWithGutter.Children.Add(_gutterBorder);
        diffWithGutter.Children.Add(_diffScroll);

        Border diffBorder = new()
        {
            Child = diffWithGutter,
            Background = Brush("App.Panel", Brushes.Black),
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = StyleDensity.PaneOutline,
            ClipToBounds = true,
        };

        // ---- BOTTOM RIGHT: the commit buttons, the commit toolbar and the message ----
        // Upstream's tableLayoutPanel1 (FormCommit.Designer.cs:458-460): the buttons are
        // a TOP-DOWN flow in column 0 spanning both rows, the commit toolbar is row 0 of
        // column 1 and the message box fills row 1 under it. The port used to lay the
        // message across the whole dialog width with the buttons wrapped underneath,
        // which is why the two looked nothing alike.
        _messageBox = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 70,

            // Upstream's AppSettings.MessageEditorWordWrap, default off. Off also keeps
            // one logical line on one visual row, which is what lets the ruler overlay
            // put its marks where the characters actually are.
            TextWrapping = _prefs.CommitMessageWordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = Monospace,
            Padding = MessagePadding,
        };

        _messageRuler = new TextRulerOverlay(_messageBox, MessagePadding)
        {
            // The ruler stands at the line limit that applies to the BODY: the subject
            // limit only concerns row one and a full-height line at 50 columns would read
            // as a rule for the whole message, which it is not.
            RulerColumn = _prefs.CommitValidationMaxCharsPerLine,
            FirstLineLimit = _prefs.CommitValidationFirstLineMaxChars,
            OtherLineLimit = _prefs.CommitValidationMaxCharsPerLine,
            MarkIllFormed = _prefs.MarkIllFormedCommitLines,
            Wrapping = _prefs.CommitMessageWordWrap,
        };
        _amendBox = new CheckBox
        {
            Margin = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _commitBtn = MakeButton(() => Async.Run(() => DoCommitAsync(push: false), "committing"));
        _commitPushBtn = MakeButton(() => Async.Run(() => DoCommitAsync(push: true), "committing and pushing"));
        _stashBtn = MakeButton(DoStashStaged);
        _resetAllBtn = MakeButton(() => Async.Run(() => DoResetAsync(includeStaged: true), "resetting all changes"));
        _resetUnstagedBtn = MakeButton(() => Async.Run(() => DoResetAsync(includeStaged: false), "resetting unstaged changes"));

        _messageMenuBtn = new Button();
        _messageMenuBtn.Click += (_, _) => Async.Run(() => ShowMessageMenuAsync(_messageMenuBtn), "opening the commit-message menu");

        _templatesBtn = new Button();
        _templatesBtn.Click += (_, _) => Async.Run(() => ShowTemplatesMenuAsync(_templatesBtn), "opening the commit-template menu");

        _createBranchBtn = MakeButton(() => Async.Run(PromptCreateBranchAsync, "creating a branch"));

        // The dialog's actions, as opposed to the flat strip buttons above them: a
        // raised fill and no outline (Theming/BarButtonStyles.ApplyActions).
        foreach (Button action in new[]
                 {
                     _commitBtn, _commitPushBtn, _stashBtn, _resetAllBtn, _resetUnstagedBtn, _createBranchBtn,
                 })
        {
            action.Classes.Add(Theming.BarButtonStyles.ActionClass);
        }

        _optionsBtn = new Button();
        _optionsBtn.Click += (_, _) => ShowOptionsMenu(_optionsBtn);

        StackPanel commitButtons = new()
        {
            Orientation = Orientation.Vertical,
            MinWidth = 171,   // upstream's flowCommitButtons.Size.Width
            Margin = new Thickness(0, 0, 6, 0),
            Children =
            {
                _commitBtn, _commitPushBtn, _amendBox, _stashBtn,
                _resetAllBtn, _resetUnstagedBtn,
            },
        };
        foreach (Control c in commitButtons.Children)
        {
            if (c is Button button)
            {
                button.Margin = new Thickness(0, 0, 0, 3);
                button.HorizontalAlignment = HorizontalAlignment.Stretch;

                // Stretch, not Center: ButtonFace docks the icon to the left edge, and a
                // centred content box would carry the icon into the middle with the text.
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            }
        }

        // The commit toolbar, upstream's toolbarCommit (:626-630). Options sits at the
        // far right there (Alignment = Right), the rest flow from the left.
        DockPanel commitToolbar = new() { Margin = new Thickness(0, 0, 0, 2) };

        // Upstream's ToolStrip parks what does not fit behind a "»" chevron; the port
        // has the same panel already (OverflowPanel, from the main toolbar), so the row
        // stays ONE row however narrow the dialog gets instead of wrapping.
        _toolbarOverflowBtn = new Button { Content = "»" };
        _toolbarOverflowBtn.Click += (_, _) => ShowToolbarOverflow();
        ToolTip.SetTip(_toolbarOverflowBtn, T("More toolbar commands"));

        OverflowPanel commitToolbarLeft = new(_toolbarOverflowBtn) { Spacing = 4 };
        commitToolbarLeft.AddItem(_messageMenuBtn);
        commitToolbarLeft.AddItem(_templatesBtn);
        commitToolbarLeft.AddItem(_createBranchBtn);
        _commitToolbar = commitToolbarLeft;

        // What each toolbar entry does, so the overflow menu can run it anchored on the
        // chevron: raising Click on the parked button would open its flyout off-screen.
        _toolbarActions[_messageMenuBtn] = anchor => _ = ShowMessageMenuAsync(anchor);
        _toolbarActions[_templatesBtn] = anchor => _ = ShowTemplatesMenuAsync(anchor);
        _toolbarActions[_createBranchBtn] = _ => Async.Run(PromptCreateBranchAsync, "creating a branch");

        // Flat, like the ToolStrip upstream uses here: framed buttons made the row look
        // like a second set of commands competing with the column on the left, and cost
        // the width that pushed "Create branch" onto a line of its own.
        foreach (Button b in new[]
                 { _messageMenuBtn, _templatesBtn, _createBranchBtn, _optionsBtn, _toolbarOverflowBtn })
        {
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(6, 2);
        }

        // Options goes in FIRST: a DockPanel serves its children in order, so with the
        // left group added first that group takes the width it wants and Options is left
        // with the sliver that remains.
        _optionsBtn.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(_optionsBtn, Dock.Right);
        DockPanel.SetDock(commitToolbarLeft, Dock.Left);
        commitToolbar.Children.Add(_optionsBtn);
        commitToolbar.Children.Add(commitToolbarLeft);

        Grid messageArea = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        Grid.SetRowSpan(commitButtons, 2);
        Grid.SetColumn(commitToolbar, 1);
        Grid.SetColumn(_messageBox, 1);
        Grid.SetRow(_messageBox, 1);
        Grid.SetColumn(_messageRuler, 1);
        Grid.SetRow(_messageRuler, 1);
        messageArea.Children.Add(commitButtons);
        messageArea.Children.Add(commitToolbar);

        // The overlay goes on TOP of the box, not behind it: the box paints an opaque
        // background of its own in both styles, which would swallow anything underneath.
        // Both marks are translucent and the control is not hit-testable, so the text
        // stays readable and the caret keeps working through it.
        messageArea.Children.Add(_messageBox);
        messageArea.Children.Add(_messageRuler);

        _statusText = new TextBlock
        {
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            Margin = new Thickness(0, 2, 0, 0),
        };
        DockPanel bottom = new() { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_statusText, Dock.Bottom);
        bottom.Children.Add(_statusText);
        bottom.Children.Add(messageArea);

        // ---- RIGHT column: conflict banner, diff, then the commit region ----
        // Upstream's splitRight: Panel1 = SolveMergeconflicts + SelectedDiff,
        // Panel2 = the commit region. So the banner and the buttons are BOTH inside the
        // right column, and the file lists on the left keep the full height.
        Grid rightPanel = new()
        {
            RowDefinitions = new RowDefinitions("Auto,3*,4,2*"),
            Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetRow(diffBorder, 1);
        GridSplitter rightSplitter = new()
        {
            Height = 4,
            ResizeDirection = GridResizeDirection.Rows,

            // Transparent, not App.Border: a 4px painted band is a pale stripe across
            // the dialog, and MainWindow's splitters have never drawn one. Transparent
            // rather than null so the grip still takes the pointer.
            Background = Brushes.Transparent,
        };
        Grid.SetRow(rightSplitter, 2);
        Grid.SetRow(bottom, 3);
        rightPanel.Children.Add(_conflictBanner);
        rightPanel.Children.Add(diffBorder);
        rightPanel.Children.Add(rightSplitter);
        rightPanel.Children.Add(bottom);

        // ---- top region: left | right split (upstream's splitMain) ----
        Grid split = new()
        {
            ColumnDefinitions = new ColumnDefinitions("2*,4,3*"),
            Margin = new Thickness(6),
        };
        Grid.SetColumn(leftPanel, 0);
        GridSplitter splitter = new()
        {
            Width = 4,
            ResizeDirection = GridResizeDirection.Columns,
            Background = Brushes.Transparent,
        };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(rightPanel, 2);
        split.Children.Add(leftPanel);
        split.Children.Add(splitter);
        split.Children.Add(rightPanel);

        _statusBar = BuildStatusBar();

        DockPanel root = new();
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        root.Children.Add(_statusBar);
        root.Children.Add(split);
        Content = root;
        DialogKeys.EnsureFocusRoute(this);

        InstallShortcuts();
        TrackCaret();
        ApplyTranslations();

        // A language switch while the dialog is open re-captions it in place
        // (MainMenu does the same by rebuilding itself). The handler may run on
        // the loader's thread, hence the hop onto the UI thread.
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        // "Refresh dialog on form focus": the user typically alt-tabs out to edit a
        // file and comes back, so the lists are re-read on activation. Off by default,
        // as upstream has it — a reload steals the diff panel's scroll position.
        Activated += (_, _) =>
        {
            if (_prefs.RefreshCommitDialogOnFocus)
            {
                Reload();
            }
        };

        // "Select staged files on entering the commit message": entering the message
        // brings the diff panel onto what is about to be committed. Only SELECTION
        // moves — focus stays in the message box, which is where the user is typing.
        _messageBox.GotFocus += (_, _) =>
        {
            if (_prefs.CommitDialogSelectStagedOnEnterMessage)
            {
                SelectStagedForMessage();
            }
        };

        Reload();
        RefreshBranchCaption();
        PrefetchGitHubIssues();
    }

    /// <summary>
    ///  What a user script bound to a commit event can substitute. The message is passed
    ///  in for the AFTER case, where the box has already been cleared; the branch and the
    ///  repository are what this dialog already knows.
    /// </summary>
    private UserScriptContext ScriptContext(string? message = null)
        => new(
            _repoPath,
            CurrentBranch: _titleBranch,
            Message: message ?? _messageBox.Text ?? string.Empty,
            Subject: (message ?? _messageBox.Text ?? string.Empty).Split('\n')[0].Trim(),
            Author: _committerName.Length > 0 ? $"{_committerName} <{_committerEmail}>" : string.Empty);

    // Persists one options-menu toggle. The same delegate is applied twice: to the copy
    // this dialog renders from, and — at write time, on whatever the file says then — to
    // the stored document. That second application is what keeps one toggle here from
    // reverting a sibling setting another surface changed while the dialog was open;
    // rewriting the whole record used to do exactly that.
    private void SaveOption(Action<AppPreferences> change)
    {
        change(_prefs);
        _ = Task.Run(() => _settings.Update(change));
    }

    // Upstream's SelectStaged(): if nothing is selected in the staged list, put the
    // selection on its first row so the diff panel shows it. Does nothing when the
    // user has already chosen a file, and never touches ItemsSource — reassigning it
    // from a selection-driven handler is what crashed this dialog twice before.
    private void SelectStagedForMessage()
    {
        if (_stagedList.SelectedItems?.Count > 0 || _stagedPane.Rows.Count == 0)
        {
            return;
        }

        // The first FILE, not the first item: with a grouping on, the first item is a
        // group header, and a header has no diff to put in the panel.
        if (_stagedList.Items.OfType<WorkingDirFileRow>().FirstOrDefault() is { } first)
        {
            _stagedList.SelectedItem = first;
        }
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    // Every fixed caption of the dialog, in one place, so it can be applied at
    // construction time and again after a language switch. Captions that carry a
    // selection count (the context menus) are re-computed in the menus' Opening
    // handler and only get their singular form here.
    private void ApplyTranslations()
    {

        _stageItem.Header = StageCaption;
        _unstageItem.Header = UnstageCaption;

        // The copy items re-caption themselves AND their flavour sub-menu; the count
        // suffix the menus' Opening handler adds is re-applied on the next open.
        _unstagedCopyItem.ApplyTranslations();
        _stagedCopyItem.ApplyTranslations();
        _discardItem.Header = DiscardCaption;

        _mergetoolItem.Header = T("FormResolveConflicts/OpenMergetool.Text", "Open in mergetool");
        _takeOursItem.Header = T("Take ours");
        _takeTheirsItem.Header = T("Take theirs");
        _markResolvedItem.Header = T("Mark resolved");

        // Diff-panel entries. The three line-level verbs reuse the upstream
        // FileViewer trans-units so the catalogues fit; the hunk-level variants
        // have no upstream equivalent and go through the English-literal lookup.
        _stageLinesItem.Header =
            T("FileViewer/stageSelectedLinesToolStripMenuItem.Text", "Stage selected line(s)");
        _unstageLinesItem.Header =
            T("FileViewer/unstageSelectedLinesToolStripMenuItem.Text", "Unstage selected line(s)");
        _discardLinesItem.Header =
            T("FileViewer/resetSelectedLinesToolStripMenuItem.Text", "Reset selected line(s)");
        _stageHunkItem.Header = T("Stage hunk");
        _unstageHunkItem.Header = T("Unstage hunk");
        _discardHunkItem.Header = T("Reset hunk");
        _selectAllLinesItem.Header = T("Select whole diff");
        _copyDiffItem.Header = T("FormBrowse/copyToolStripMenuItem.Text", "Copy");

        CaptionFileEntries(_unstagedExtras);
        CaptionFileEntries(_stagedExtras);

        _ignorePathItem.Header = T("FileStatusList/tsmiAddFileToGitIgnore.Text", "Add to .gitignore");
        _ignoreExtItem.Header = T("Ignore by extension");
        _ignoreFolderItem.Header = T("Ignore in folder");
        _excludePathItem.Header =
            T("FileStatusList/tsmiAddFileToGitInfoExclude.Text", "Add file to .git/info/exclude");

        // Headline: the upstream trans-unit that is a *complete* sentence, period
        // included, in every catalogue — unlike FormCommit/SolveMergeconflicts.Text,
        // whose translations are bare fragments that need punctuation glued on.
        _conflictText.Text = T(
            "FormCommit/_mergeConflicts.Text",
            "There are unresolved merge conflicts, solve merge conflicts before committing.");
        _conflictHint.Text = T("Right-click a file marked \"U\" in the unstaged list to open the mergetool, "
            + "take ours/theirs or mark it resolved.");

        // The captions carry upstream's icons: every one of these buttons has an Image
        // in FormCommit.Designer.cs, and the port was drawing text alone.
        _stageBtn.Content = IconText.Header("Stage", StageCaption);
        _unstageBtn.Content = IconText.Header("Unstage", UnstageCaption);
        ApplyFilterCaptions();

        CaptionPane(_unstagedPane);
        CaptionPane(_stagedPane);

        _unstagedEmpty.Text = T("FormCommit/_noUnstagedChanges.Text", "There are no unstaged changes");
        _stagedEmpty.Text = T("FormCommit/_noStagedChanges.Text", "There are no staged changes");

        _messageBox.Watermark = T("FormCommit/_enterCommitMessageHint.Text", "Enter commit message");
        _amendBox.Content = T("FormCommit/_amendCommitCaption.Text", "Amend commit");

        _commitBtn.Content = ButtonFace("RepoStateClean", T("FormCommit/Commit.Text", "Commit"));
        // "Push", not the bare "ArrowUp" it used to carry: this button pushes, and the
        // arrow-over-a-baseline is the shape the toolbar's Push already uses — which is
        // also what earns it the transfer colour, where ArrowUp is chrome (it is the
        // "move this row up" glyph elsewhere) and stays grey.
        _commitPushBtn.Content = ButtonFace("Push", T("FormCommit/_commitAndPush.Text", "Commit & push"));
        _stashBtn.Content = ButtonFace("stash", T("FormCommit/StashStaged.Text", "Stash staged changes"));
        _resetAllBtn.Content = ButtonFace(
            "ResetWorkingDirChanges", T("FormCommit/btnResetAllChanges.Text", "Reset all changes"));
        _resetUnstagedBtn.Content = ButtonFace(
            "ResetWorkingDirChanges", T("FormCommit/btnResetUnstagedChanges.Text", "Reset unstaged changes"));
        // Tag = the plain caption, which is what the overflow menu shows for a button
        // the strip could not fit (its Content is an icon-and-text panel by then).
        _messageMenuBtn.Tag = T("FormCommit/commitMessageToolStripMenuItem.Text", "Commit message");
        _templatesBtn.Tag = T("FormCommit/commitTemplatesToolStripMenuItem.ToolTipText", "Commit templates");
        _createBranchBtn.Tag = T("FormCommit/createBranchToolStripButton.ToolTipText", "Create branch");
        _messageMenuBtn.Content = IconText.Header("WorkingDirChanges", (string)_messageMenuBtn.Tag + " ▾");
        _templatesBtn.Content = IconText.Header("CommitTemplates", (string)_templatesBtn.Tag + " ▾");
        _createBranchBtn.Content = IconText.Header("BranchCreate", (string)_createBranchBtn.Tag);
        _optionsBtn.Content = T("FormCommit/tsmiOptions.Text", "Options") + " ▾";

        UpdateTitle();
        RenderStatus();
    }

    // The captions of one pane's toolbar. Icon-only buttons, so everything the user can
    // read about them is in the tooltips.
    private void CaptionPane(FileListPane pane)
    {
        pane.FilterBox.Watermark = T(
            "FileStatusList/cboFilterComboBox.Watermark",
            "Filter files using a regular expression...");
        ToolTip.SetTip(pane.FilterBox, SelectionFilterTip);
        ToolTip.SetTip(
            pane.CollapseButton,
            T("FileStatusList/btnCollapseGroups.ToolTipText",
                "Collapse all groups, otherwise expand the selected group"));
        ToolTip.SetTip(pane.AsTreeButton, T("FileStatusList/btnAsTree.ToolTipText", "Toggle flat list / tree"));
        ToolTip.SetTip(pane.GroupMenuButton, T("Grouping"));
        ToolTip.SetTip(pane.ByPathButton, T("FileStatusList/btnByPath.ToolTipText", "Group by file path"));
        ToolTip.SetTip(
            pane.ByExtensionButton,
            T("FileStatusList/btnByExtension.ToolTipText", "Group by file extension"));
        ToolTip.SetTip(pane.ByStatusButton, T("FileStatusList/btnByStatus.ToolTipText", "Group by diff status"));
        ToolTip.SetTip(pane.RefreshButton, T("FormBrowse/refreshToolStripMenuItem.Text", "Refresh"));
        if (pane.SettingsButton is not null)
        {
            ToolTip.SetTip(
                pane.SettingsButton,
                T("FileStatusList/tsmiShowUntrackedFiles.Text", "Show untracked files"));
        }
    }

    private static string StageCaption => T("FormCommit/toolStageItem.Text", "Stage");
    private static string UnstageCaption => T("FormCommit/toolUnstageItem.Text", "Unstage");
    private static string CopyPathCaption => T("FileStatusList/tsmiCopyPaths.Text", "Copy path");
    private static string DiscardCaption => T("Discard changes");

    // No upstream trans-unit: upstream reaches the same result by TOGGLING the
    // "Show skip-worktree files" / "Show assume-unchanged files" filters and
    // unchecking the bit on the rows that then appear. This dialog's lists have no
    // such filters, so the only way back is a single restore-all entry.
    private static string RestoreHiddenCaption => T("Restore skipped / assumed-unchanged files");

    // "Stage" + 3 → "Stage (3 files)". Upstream has a counted variant for staging
    // only (FormCommit/_stageFiles.Text, "Stage {0} files"); using it just for that
    // one entry would make it read differently from its three siblings, so all four
    // share this pattern instead — the verb is translated, the parenthesised count
    // has no trans-unit and normally stays English.
    private static string WithCount(string caption, int count)
        => count > 1 ? string.Format(T("{0} ({1} files)"), caption, count) : caption;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TFormat(string? key, string englishFormat, params object?[] args)
        => TranslationService.TFormat(key, englishFormat, args);

    // Keyboard accelerators the former working-directory panel had:
    //  • Enter / Space on a list = stage (unstaged list) or unstage (staged list);
    //  • Ctrl+Enter = commit, from anywhere in the dialog including the message box.
    // Ctrl+Enter is caught in the TUNNELLING phase so it also fires while the
    // multi-line TextBox has focus; a bare Enter is left alone there, so typing a
    // new line in the commit message keeps working.
    private void InstallShortcuts()
    {
        AddHandler(
            KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key is Key.Enter or Key.Return
                    && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    Async.Run(() => DoCommitAsync(push: false), "committing");
                    return;
                }

                // The Commit scope. Ctrl+Enter above stays hard-wired: it is the
                // dialog's default action, not a command with an entry in a table.
                switch (HotkeyService.Shared.Command(HotkeyScope.Commit, e))
                {
                    // Upstream's ToggleSelectionFilter hides and shows the whole filter
                    // toolbar; here the boxes are always visible, so the toggle is
                    // "focus it" / "clear it and hand focus back to the list", which is
                    // what the hotkey is actually used for. Each list has its own box,
                    // so the key acts on the pane the user is in — the staged one only
                    // while the focus really sits there.
                    case "ToggleSelectionFilter":
                    {
                        e.Handled = true;
                        FileListPane pane = _stagedPane.FilterBox.IsFocused
                            || _stagedList.IsKeyboardFocusWithin
                            ? _stagedPane
                            : _unstagedPane;
                        if (pane.FilterBox.IsFocused)
                        {
                            pane.FilterBox.Text = string.Empty;
                            pane.Timer.Stop();
                            ApplyPaneFilter(pane);
                            pane.List.Focus();
                        }
                        else
                        {
                            pane.FilterBox.Focus();
                            pane.FilterBox.SelectAll();
                        }

                        return;
                    }

                    case "FocusUnstagedFiles":
                        _unstagedList.Focus();
                        e.Handled = true;
                        return;

                    case "FocusStagedFiles":
                        _stagedList.Focus();
                        e.Handled = true;
                        return;

                    case "FocusSelectedDiff":
                        _diffView.Focus();
                        e.Handled = true;
                        return;

                    case "FocusCommitMessage":
                        _messageBox.Focus();
                        e.Handled = true;
                        return;

                    case "StageAll":
                        StageAll();
                        e.Handled = true;
                        return;

                    case "Refresh":
                        Reload();
                        e.Handled = true;
                        return;

                    case "CreateBranch":
                        e.Handled = true;
                        Async.Run(PromptCreateBranchAsync, "creating a branch");
                        return;
                }
            },
            RoutingStrategies.Tunnel);

        // Escape = Cancel, the way upstream's CancelButton (FormCommit.Designer.cs:921)
        // wires it. Deliberately on the BUBBLING phase: an open context menu or
        // completion popup gets first refusal and swallows its own Escape, so the
        // dialog only closes when nothing inside it wanted the key.
        KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        };

        _unstagedList.KeyDown += (_, e) =>
        {
            if (IsPlainActivation(e))
            {
                e.Handled = true;
                OnUnstagedDoubleTapped();
            }
        };

        _stagedList.KeyDown += (_, e) =>
        {
            if (IsPlainActivation(e))
            {
                e.Handled = true;
                UnstageSelected();
            }
        };

        static bool IsPlainActivation(KeyEventArgs e)
            => e.KeyModifiers == KeyModifiers.None
                && e.Key is Key.Enter
                    or Key.Return
                    or Key.Space;
    }

    public static async Task ShowAsync(Window owner, string repoPath, Action onCommitted)
    {
        CommitDialog dialog = new(repoPath);
        dialog.Committed += onCommitted;
        await dialog.ShowDialog(owner);
    }

    // ---------- shared per-file menu entries ----------

    /// <summary>
    ///  The entries the original shared file-list menu offers for the file under the
    ///  cursor, regardless of which list it sits in (difftool, open, show in folder,
    ///  history, blame, refresh). One instance per list — see the fields.
    /// </summary>
    private sealed class FileEntries
    {
        public readonly MenuItem ResetToParent = new();
        public readonly MenuItem Difftool = new();
        public readonly MenuItem Open = new();
        public readonly MenuItem OpenEditor = new();
        public readonly MenuItem ShowFolder = new();
        public readonly MenuItem History = new();
        public readonly MenuItem Blame = new();
        public readonly MenuItem SaveAs = new();
        public readonly MenuItem Move = new();
        public readonly MenuItem Delete = new();
        public readonly MenuItem SkipWorktree = new();
        public readonly MenuItem AssumeUnchanged = new();
        public readonly MenuItem RestoreHidden = new();
        public readonly MenuItem Refresh = new();
    }

    // The block as it appears in a menu, in the original's order: difftool first,
    // then the open/edit group, then the navigation group. Refresh is NOT here: it
    // is not a per-file entry and goes last in each menu, as upstream places it.
    private static Control[] FileEntryControls(FileEntries e)
        =>
        [
            e.ResetToParent,
            new Separator(),
            e.Difftool,
            new Separator(),
            e.Open, e.OpenEditor, e.ShowFolder,
            new Separator(),
            e.SaveAs, e.Move, e.Delete,
            new Separator(),
            e.History, e.Blame,
        ];

    // Captions come from the original shared menu's own trans-units, so the
    // catalogues fit the port's entries without new strings.
    private static void CaptionFileEntries(FileEntries e)
    {
        e.ResetToParent.Header =
            T("FileStatusList/tsmiResetFileTo.Text", "Reset file(s) to") + "  HEAD";
        e.Difftool.Header = T("FileStatusList/tsmiOpenWithDifftool.Text", "Open with difftool");
        e.Open.Header = T("FileStatusList/tsmiOpenWorkingDirectoryFile.Text", "Open working directory file");
        e.OpenEditor.Header = T("FileStatusList/tsmiEditWorkingDirectoryFile.Text", "Edit working directory file");
        e.ShowFolder.Header = T("FileStatusList/tsmiShowInFolder.Text", "Show in folder");
        e.History.Header = T("FileStatusList/tsmiFileHistory.Text", "File history");
        e.Blame.Header = T("FileStatusList/tsmiBlame.Text", "Blame");
        e.SaveAs.Header = T("FileStatusList/tsmiSaveAs.Text", "Save selected as...");
        e.Move.Header = T("FileStatusList/tsmiMove.Text", "Rename / move");
        e.Delete.Header = T("FileStatusList/tsmiDeleteFile.Text", "Delete file");
        e.SkipWorktree.Header = T("FileStatusList/tsmiSkipWorktree.Text", "Skip worktree");
        e.AssumeUnchanged.Header = T("FileStatusList/tsmiAssumeUnchanged.Text", "Assume unchanged");
        e.RestoreHidden.Header = RestoreHiddenCaption;
        e.Refresh.Header = T("FormBrowse/refreshToolStripMenuItem.Text", "Refresh");
    }

    // The index-bit entries, which upstream keeps at the bottom of the menu next to
    // the ignore entries because they are the other way of making a file "go away".
    private static Control[] FileFlagControls(FileEntries e)
        => [e.SkipWorktree, e.AssumeUnchanged, e.RestoreHidden];

    private void WireFileEntries(FileEntries e, ListBox list, bool staged)
    {
        e.Difftool.Click += (_, _) => OpenWithDifftool(list, staged);
        e.Open.Click += (_, _) => OpenWorkingFile(list, inEditor: false);
        e.OpenEditor.Click += (_, _) => OpenWorkingFile(list, inEditor: true);
        e.ShowFolder.Click += (_, _) => ShowSelectedInFolder(list);
        e.History.Click += (_, _) => ShowFileTool(list, blame: false);
        e.Blame.Click += (_, _) => ShowFileTool(list, blame: true);
        e.Refresh.Click += (_, _) => Reload();
        e.ResetToParent.Click += (_, _) => ResetSelectedToHead(list);
        e.SkipWorktree.Click += (_, _) => SetIndexFlag(list, skipWorktree: true);
        e.AssumeUnchanged.Click += (_, _) => SetIndexFlag(list, skipWorktree: false);
        e.RestoreHidden.Click += (_, _) => RestoreHiddenFiles();
        e.SaveAs.Click += (_, _) => Async.Run(() => SaveSelectedAsAsync(list), "saving a file as");
        e.Move.Click += (_, _) => Async.Run(() => MoveSelectedAsync(list), "renaming a file");
        e.Delete.Click += (_, _) => DeleteSelected(list);
    }

    // Enable/disable only — the Items themselves never move while the menu opens.
    private void UpdateFileEntries(FileEntries e, ListBox list, bool staged)
    {
        List<WorkingDirFileRow> rows = SelectedRows(list);
        WorkingDirFileRow? row = rows.Count == 1 ? rows[0] : null;
        bool conflict = row is not null && _conflictPaths.Contains(row.Path);
        bool onDisk = row is not null && File.Exists(FullPath(row));

        // Every entry below acts on exactly ONE file: the difftool, the editor and
        // the file manager all take a single target, and history/blame are per-file
        // views. Upstream's counted variants only exist for stage/unstage/copy.
        e.Difftool.IsEnabled = row is not null && !conflict;
        e.Open.IsEnabled = onDisk;
        e.OpenEditor.IsEnabled = onDisk;
        e.ShowFolder.IsEnabled = row is not null;
        e.History.IsEnabled = row is not null;
        e.Blame.IsEnabled = onDisk && row!.Status != "new";

        // Only a TRACKED file can be reset to HEAD or carry an index bit: an
        // untracked one has no HEAD version and no index entry at all.
        bool tracked = row is not null && row.Status != "new" && !conflict;
        e.ResetToParent.IsEnabled = tracked;
        e.SkipWorktree.IsEnabled = tracked;
        e.AssumeUnchanged.IsEnabled = tracked;
        // All three act on the file as it is ON DISK: a deleted row has nothing to
        // copy, move or delete any more.
        e.SaveAs.IsEnabled = onDisk;
        e.Move.IsEnabled = onDisk && !conflict;
        e.Delete.IsEnabled = onDisk && !conflict;
        e.RestoreHidden.IsEnabled = _hiddenByIndexFlag > 0;
        e.RestoreHidden.Header = WithCount(RestoreHiddenCaption, _hiddenByIndexFlag);
    }

    private string FullPath(WorkingDirFileRow row)
        => System.IO.Path.Combine(_repoPath, row.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private void OpenWithDifftool(ListBox list, bool staged)
    {
        if (SingleRow(list) is not { } row)
        {
            return;
        }

        string repo = _repoPath;
        string path = row.Path;
        bool tracked = row.Status != "new";
        SetStatus(string.Format(T("Running {0} …"), "git difftool"));
        RunGitResult(
            () => _service.LaunchDifftool(repo, path, staged, tracked),
            result => SetStatus(result.Success
                ? string.Format(T("Opened '{0}' in the difftool."), path)
                : FirstLine(result.Output)));
    }

    private void OpenWorkingFile(ListBox list, bool inEditor)
    {
        if (SingleRow(list) is not { } row)
        {
            return;
        }

        string full = FullPath(row);
        string repo = _repoPath;
        RunTool(() => inEditor
            ? new ExternalToolService().OpenInEditor(full, repo)
            : new ExternalToolService().OpenOrCreateFile(full));
    }

    private void ShowSelectedInFolder(ListBox list)
    {
        if (SingleRow(list) is not { } row)
        {
            return;
        }

        string full = FullPath(row);
        RunTool(() => new ExternalToolService().ShowInFolder(full));
    }

    /// <summary>
    ///  Opens the file history / blame of the selected file in a window of its own,
    ///  owned by this dialog.
    ///
    ///  <para>Upstream opens a separate <c>FormFileHistory</c> here rather than
    ///  routing into the main window's bottom panel, and it has to: the commit form
    ///  is modal, so anything shown behind it would be unreachable until it closes.
    ///  The port therefore hosts the existing <see cref="FileHistoryView"/> /
    ///  <see cref="BlameView"/> controls in a child window instead of raising an
    ///  event at the host.</para>
    /// </summary>
    private void ShowFileTool(ListBox list, bool blame)
    {
        if (SingleRow(list) is not { } row)
        {
            return;
        }

        string full = FullPath(row);

        // One window for both entries, as upstream has one form for its "filehistory"
        // and "blamehistory" commands: the blame entry is the same window opened on its
        // Blame tab (M113). It used to be a bare ZoomWindow holding a single view, which
        // meant the history had no diff and the blame had no history next to it.
        FileHistoryWindow window = new(_repoPath, row.Path, showBlame: blame);

        // Not a dialog: the user must be able to keep staging while it is open.
        window.Show(this);
        SetStatus(string.Format(T("Opened '{0}'."), full));
    }

    /// <summary>
    ///  "Reset file(s) to HEAD" — <c>git checkout HEAD -- &lt;path&gt;</c>. Unlike the
    ///  Discard entry (which only restores the work tree from the index) this also
    ///  drops what is STAGED for the file, so it is confirmed in its own words.
    /// </summary>
    private void ResetSelectedToHead(ListBox list)
    {
        if (SingleRow(list) is not { } row || row.Status == "new")
        {
            return;
        }

        string repo = _repoPath;
        string path = row.Path;
        ConfirmThen(
            string.Format(
                T("Reset '{0}' to HEAD? Both the staged and the unstaged changes to this file are discarded, and this cannot be undone."),
                path),
            () =>
            {
                SetStatus(string.Format(T("Running {0} …"), $"git checkout HEAD -- {path}"));
                RunGitResult(
                    () => _service.ResetFileToHead(repo, path),
                    result =>
                    {
                        SetStatus(result.Success
                            ? string.Format(T("Reset '{0}' to HEAD."), path)
                            : FirstLine(result.Output));
                        Reload();
                    });
            });
    }

    // Appends the selected untracked file's path to .git/info/exclude — the same
    // gesture as the .gitignore entries above it, but repository-local.
    private void AddSelectedToInfoExclude()
    {
        if (SingleUntracked() is not { } row)
        {
            return;
        }

        string repo = _repoPath;
        string pattern = "/" + row.Path.Replace('\\', '/');
        SetStatus(string.Format(T("Adding '{0}' to .git/info/exclude …"), pattern));
        RunGitResult(
            () => _service.AddToInfoExclude(repo, pattern),
            result =>
            {
                SetStatus(FirstLine(result.Output));
                Reload();
            });
    }

    /// <summary>
    ///  Sets <c>--skip-worktree</c> / <c>--assume-unchanged</c> on the selected file.
    ///  The file then disappears from both lists (git stops reporting it), which is
    ///  the whole point of the bit — and why the menu also carries the restore entry.
    /// </summary>
    private void SetIndexFlag(ListBox list, bool skipWorktree)
    {
        if (SingleRow(list) is not { } row || row.Status == "new")
        {
            return;
        }

        string repo = _repoPath;
        string path = row.Path;
        bool skip = skipWorktree;
        SetStatus(string.Format(
            T("Running {0} …"),
            $"git update-index {(skip ? "--skip-worktree" : "--assume-unchanged")} -- {path}"));
        RunGitResult(
            () => _service.SetIndexFlag(repo, path, skip, on: true),
            result =>
            {
                SetStatus(result.Success
                    ? string.Format(
                        skip
                            ? T("git now skips the work tree for '{0}'; it is hidden until restored.")
                            : T("git now assumes '{0}' unchanged; it is hidden until restored."),
                        path)
                    : FirstLine(result.Output));
                Reload();
            });
    }

    private void RestoreHiddenFiles()
    {
        string repo = _repoPath;
        SetStatus(string.Format(T("Running {0} …"), "git update-index --no-skip-worktree --no-assume-unchanged"));
        RunGitResult(
            () => _service.RestoreHiddenByIndexFlag(repo),
            result =>
            {
                SetStatus(FirstLine(result.Output));
                Reload();
            });
    }

    /// <summary>
    ///  "Save selected as..." — for these lists the file always exists on disk, so
    ///  this copies the WORK-TREE version; there is no revision to extract here.
    /// </summary>
    private async Task SaveSelectedAsAsync(ListBox list)
    {
        if (SingleRow(list) is not { } row)
        {
            return;
        }

        try
        {
            IStorageProvider? storage = StorageProvider;
            if (storage is null)
            {
                SetStatus(T("No file picker is available on this display."));
                return;
            }

            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("FileStatusList/tsmiSaveAs.Text", "Save selected as..."),
                SuggestedFileName = System.IO.Path.GetFileName(row.Path),
                ShowOverwritePrompt = true,
            });

            if (target?.TryGetLocalPath() is not { } destination)
            {
                return;   // cancelled, or a location that is not a local file
            }

            string repo = _repoPath;
            string path = row.Path;
            RunGitResult(
                () => _service.SaveFileAs(repo, path, destination),
                result => SetStatus(FirstLine(result.Output)));
        }
        catch (Exception ex)
        {
            SetStatus(FirstLine(ex.Message));
        }
    }

    /// <summary>
    ///  "Rename / move" via <c>git mv</c>, so the move lands in the index instead of
    ///  looking like a delete plus an untracked file. The new path is asked for as a
    ///  repository-relative path, which is what git mv takes.
    /// </summary>
    private async Task MoveSelectedAsync(ListBox list)
    {
        if (SingleRow(list) is not { } row)
        {
            return;
        }

        string? destination = await PromptTextAsync(
            T("FileStatusList/tsmiMove.Text", "Rename / move"),
            T("New path, relative to the repository root:"),
            row.Path);
        if (string.IsNullOrWhiteSpace(destination)
            || string.Equals(destination.Trim(), row.Path, StringComparison.Ordinal))
        {
            return;
        }

        string repo = _repoPath;
        string path = row.Path;
        string target = destination.Trim();
        SetStatus(string.Format(T("Running {0} …"), $"git mv -- {path} {target}"));
        RunGitResult(
            () => _service.MoveFile(repo, path, target),
            result =>
            {
                SetStatus(result.Success
                    ? string.Format(T("Moved '{0}' to '{1}'."), path, target)
                    : FirstLine(result.Output));
                Reload();
            });
    }

    /// <summary>
    ///  "Delete file". A tracked file goes through <c>git rm -f</c> (the deletion is
    ///  staged with it); an untracked one is removed from disk. Both are irreversible
    ///  for the work-tree content, so both are confirmed.
    /// </summary>
    private void DeleteSelected(ListBox list)
    {
        if (SingleRow(list) is not { } row)
        {
            return;
        }

        string repo = _repoPath;
        string path = row.Path;
        bool tracked = row.Status != "new";
        ConfirmThen(
            string.Format(
                tracked
                    ? T("Delete '{0}'? The file is removed from the work tree and the deletion is staged.")
                    : T("Delete '{0}'? The file is untracked, so it cannot be recovered from git."),
                path),
            () =>
            {
                SetStatus(string.Format(
                    T("Running {0} …"),
                    tracked ? $"git rm -f -- {path}" : $"rm {path}"));
                RunGitResult(
                    () => _service.DeleteFile(repo, path, tracked),
                    result =>
                    {
                        SetStatus(result.Success
                            ? string.Format(T("Deleted '{0}'."), path)
                            : FirstLine(result.Output));
                        Reload();
                    });
            });
    }

    /// <summary>
    ///  A modal one-line text question: returns the entered text, or <c>null</c> when
    ///  cancelled. Same shape as the dialog's other modal questions
    ///  (<see cref="ChooseAsync"/>), so it inherits the window styling and Esc.
    /// </summary>
    private async Task<string?> PromptTextAsync(string caption, string label, string initial)
    {
        Theming.ZoomWindow prompt = new()
        {
            Title = caption,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", Brushes.DimGray),
        };

        TextBox box = new() { Text = initial };
        string? result = null;

        Button ok = new()
        {
            Content = T("FormCommit/Ok.Text", "OK"),
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            result = box.Text;
            prompt.Close();
        };

        Button cancel = MakeButton(T("FormCommit/Cancel.Text", "Cancel"), prompt.Close);
        prompt.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                prompt.Close();
            }
        };

        prompt.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("App.Foreground", Brushes.Gainsboro),
                },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel },
                },
            },
        };

        box.Focus();
        await prompt.ShowDialog(this);
        return result;
    }

    private static WorkingDirFileRow? SingleRow(ListBox list)
    {
        List<WorkingDirFileRow> rows = SelectedRows(list);
        return rows.Count == 1 ? rows[0] : null;
    }

    // External tools are launched detached but Process.Start itself can block on a
    // slow filesystem, so it goes to the pool; only the failure message comes back.
    private void RunTool(Func<ExternalToolResult> work)
        => Async.OffUi(
            () =>
            {
                try
                {
                    return work();
                }
                catch (Exception ex)
                {
                    return new ExternalToolResult(false, ex.Message);
                }
            },
            result =>
            {
                if (!result.Success)
                {
                    SetStatus(FirstLine(result.Message));
                }
            },
            "launching an external tool");

    // ---------- list plumbing ----------

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        FileListPane pane = ReferenceEquals(list, _stagedList) ? _stagedPane : _unstagedPane;
        if (HeaderAt(e.Source) is not { } header)
        {
            return;
        }

        // The right button belongs to the folder's own menu (OnListContextRequested), so
        // it must not fold the folder on the way there: this handler runs in the
        // TUNNELLING phase and used to treat every button alike, which meant a
        // right-click closed the folder under the menu that was about to open over it.
        if (e.GetCurrentPoint(list).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (!pane.Collapsed.Remove(header.Key))
        {
            pane.Collapsed.Add(header.Key);
        }

        e.Handled = true;
        RegroupPane(pane);
    }

    // The group header the pointer is over, or null for a file row / empty space.
    private static GroupHeader? HeaderAt(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem container)
            {
                return container.DataContext as GroupHeader;
            }
        }

        return null;
    }

    /// <summary>
    ///  Right-clicking a GROUP shows the folder's own menu instead of the list's file
    ///  menu, and stops the latter from opening.
    ///
    ///  <para><b>Why stopping it matters.</b> The two file menus act on the SELECTION,
    ///  and a right-click on a header changes no selection: right-clicking a folder used
    ///  to open a menu whose entries applied to whatever file was selected somewhere
    ///  else in the list — "Reset file changes" included. Aiming at a folder and hitting
    ///  another file is not a menu with nothing to offer, it is a menu that lies.</para>
    /// </summary>
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list || HeaderAt(e.Source) is not { } header)
        {
            return;
        }

        e.Handled = true;
        ShowFolderMenu(ReferenceEquals(list, _stagedList) ? _stagedPane : _unstagedPane, header, list);
    }

    // The rows currently selected in <paramref name="list"/>, in selection order.
    private static List<WorkingDirFileRow> SelectedRows(ListBox list)
        => [.. list.SelectedItems?.OfType<WorkingDirFileRow>() ?? []];

    private void OnSelected(ListBox source, bool staged)
    {
        // Re-entrancy from the programmatic clear below.
        if (_syncingSelection)
        {
            return;
        }

        List<WorkingDirFileRow> rows = SelectedRows(source);
        ListBox other = staged ? _unstagedList : _stagedList;
        if (rows.Count == 0)
        {
            // The selection was dropped — either by the user or by a Reload that
            // removed the row. Blank the diff panel so it cannot keep showing a
            // stale diff for a file that is no longer listed.
            if (SelectedRows(other).Count == 0)
            {
                ClearDiff();
            }

            return;
        }

        // Only one list at a time drives the diff, so clear the other one's
        // selection without letting its SelectionChanged blank the diff again.
        _syncingSelection = true;
        try
        {
            other.SelectedItems?.Clear();
        }
        finally
        {
            _syncingSelection = false;
        }

        // With a multi-selection the panel always shows the LAST selected row —
        // the one the user just clicked / extended the range to.
        LoadDiff(rows[^1], staged);
    }

    // Loads the CLEAN diff of one file. "Clean" is the whole point: the string that
    // lands in the panel is the same string PatchStagingService cuts the patch from,
    // so it must never carry a display-only option (-w, --word-diff, colour…).
    private void LoadDiff(WorkingDirFileRow row, bool staged)
    {
        string repo = _repoPath;
        string path = row.Path;
        bool isNew = row.Status == "new";
        bool isRenamed = row.Status == "renamed" || row.Status == "copied";

        // A "new" row on the UNSTAGED side is an untracked file: it is in no tree git
        // can diff against, so `git diff` prints nothing and the panel used to stay
        // blank. PatchStagingService diffs it against /dev/null instead.
        bool untracked = isNew && !staged;
        int token = ++_diffToken;

        Async.OffUi(
            () =>
        {
            try
            {
                return (Diff: PatchStagingService.LoadDiff(repo, path, staged, untracked), Failed: false);
            }
            catch (Exception ex)
            {
                string message = string.Format(T("Could not load diff: {0}"), ex.Message);
                return (Diff: new DiffLoad(message, string.Empty), Failed: true);
            }
        },
            loaded =>
        {
            // A newer selection already won the race; drop this result rather than
            // letting _diffText describe a file the panel is no longer showing.
            if (token != _diffToken)
            {
                return;
            }

            (DiffLoad diff, bool failed) = loaded;
            _diffPath = failed ? string.Empty : path;
            _diffStaged = staged;
            _diffFileIsNew = isNew;
            _diffFileIsRenamed = isRenamed;

            // The service already decides what may be cut from: an error message or a
            // truncated whole-file view carries an EMPTY source, so line staging stays
            // disabled while the text is still shown.
            RenderDiff(diff.Source, diff.Display);
        },
            "loading the diff of a file");
    }

    // Blanks the panel and forgets everything line patching depends on.
    private void ClearDiff() => RenderDiff(string.Empty, string.Empty);

    /// <summary>
    ///  Renders the diff and, at the same time, records the render-offset ↔
    ///  source-offset map the patch builder needs.
    /// </summary>
    /// <param name="source">
    ///  The patch source: untouched git output, or empty when the panel is showing
    ///  something that is not a real diff (an error message, a blank panel), in
    ///  which case line patching stays disabled.
    /// </param>
    /// <param name="display">The text to put on screen.</param>
    private void RenderDiff(string source, string? display = null)
    {
        _diffText = source ?? string.Empty;
        string text = display ?? _diffText;
        if (_diffText.Length == 0)
        {
            _diffPath = string.Empty;
        }

        InlineCollection inlines = new();
        List<DiffLineSpan> spans = [];
        IBrush add = Brush("App.DiffAdded", Brushes.LimeGreen);
        IBrush del = Brush("App.DiffRemoved", Brushes.OrangeRed);
        IBrush hunk = Brush("App.Accent", Brushes.DeepSkyBlue);
        IBrush normal = Brush("App.Foreground", Brushes.Gainsboro);

        // The map is only meaningful when what is rendered IS the patch source.
        bool mapped = ReferenceEquals(text, _diffText) || text == _diffText;
        int renderPos = 0;
        int sourcePos = 0;
        int hunkIndex = -1;

        // Gutter numbers, one pair per rendered line, 0 meaning "no number on this
        // side". They are PARSED from the @@ -a,b +c,d @@ headers and then counted per
        // line, which is the only way to get them right: a context line advances both
        // sides, a '+' line only the new one, a '-' line only the old one.
        List<(int Old, int New)> numbers = [];
        int oldNo = 0;
        int newNo = 0;
        bool inHunk = false;

        foreach (string rawLine in text.Split('\n'))
        {
            // The '\r' of a CRLF file is hidden on screen but kept in the source
            // span, so the patch still carries it.
            string shown = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

            IBrush color = normal;
            if (shown.StartsWith("@@", StringComparison.Ordinal))
            {
                color = hunk;
                hunkIndex++;

                // A two-sided header restarts both counters. A COMBINED diff (@@@ …,
                // produced for an unmerged path) does not match and leaves the gutter
                // empty from there on: wrong numbers would be worse than none.
                if (ParseHunkStart(shown) is (int oldStart, int newStart))
                {
                    oldNo = oldStart;
                    newNo = newStart;
                    inHunk = true;
                }
                else
                {
                    inHunk = false;
                }

                numbers.Add((0, 0));
            }
            else if (shown.StartsWith('+') && !shown.StartsWith("+++", StringComparison.Ordinal))
            {
                color = add;
                numbers.Add((0, inHunk ? newNo++ : 0));
            }
            else if (shown.StartsWith('-') && !shown.StartsWith("---", StringComparison.Ordinal))
            {
                color = del;
                numbers.Add((inHunk ? oldNo++ : 0, 0));
            }
            else if (!inHunk || shown.Length == 0 || shown[0] == '\\')
            {
                // The file headers, the "\ No newline at end of file" marker and the
                // empty tail Split leaves behind are on neither side of the file.
                numbers.Add((0, 0));
            }
            else
            {
                // A context line: it exists on both sides, so it carries both numbers.
                numbers.Add((oldNo++, newNo++));
            }

            inlines.Add(new Run(shown + "\n") { Foreground = color });
            if (mapped)
            {
                spans.Add(new DiffLineSpan(renderPos, shown.Length, sourcePos, rawLine.Length, hunkIndex));
            }

            renderPos += shown.Length + 1;
            sourcePos += rawLine.Length + 1;
        }

        RenderGutter(numbers);
        _diffSpans = mapped && _diffText.Length > 0 ? [.. spans] : [];
        _lastSelStart = 0;
        _lastSelLength = 0;
        _pointerCaret = -1;
        _menuSelFirstLine = -1;
        _menuSelLastLine = -1;
        _diffView.Inlines = inlines;
        _diffView.SelectionStart = 0;
        _diffView.SelectionEnd = 0;
        _diffScroll.Offset = default;
    }

    /// <summary>
    ///  The <c>-a</c> / <c>+c</c> starting lines of a <c>@@ -a,b +c,d @@</c> header, or
    ///  <see langword="null"/> when the header is not a plain two-sided one.
    /// </summary>
    private static (int Old, int New)? ParseHunkStart(string header)
    {
        Match m = HunkHeader.Match(header);
        return m.Success
            ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))
            : null;
    }

    private static readonly Regex HunkHeader =
        new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    /// <summary>
    ///  Fills the gutter: one line per rendered diff line, old number then new number,
    ///  right-aligned in fixed-width columns so both stay in line with a monospace
    ///  font. A zero means the line does not exist on that side of the file.
    /// </summary>
    private void RenderGutter(List<(int Old, int New)> numbers)
    {
        int oldWidth = 1;
        int newWidth = 1;
        foreach ((int old, int @new) in numbers)
        {
            oldWidth = Math.Max(oldWidth, Digits(old));
            newWidth = Math.Max(newWidth, Digits(@new));
        }

        // One line per rendered diff line, each closed by '\n' exactly as the diff's
        // Runs are, so both blocks lay out the same number of lines and stay in step.
        System.Text.StringBuilder text = new();
        foreach ((int old, int @new) in numbers)
        {
            text.Append(Cell(old, oldWidth)).Append(' ').Append(Cell(@new, newWidth)).Append('\n');
        }

        _gutterView.Text = text.ToString();

        // No numbers at all (a blank panel, an error message, a combined diff): the
        // column would just be an empty stripe, so it goes away entirely.
        _gutterBorder.IsVisible = numbers.Any(n => n.Old > 0 || n.New > 0);

        static string Cell(int value, int width)
            => value > 0 ? value.ToString().PadLeft(width) : new string(' ', width);

        static int Digits(int value) => value <= 0 ? 1 : value.ToString().Length;
    }

    // ---------- per-hunk / per-line staging (the port's `git add -p`) ----------

    // The diff panel's own context menu. Both sides live in the same menu and are
    // shown/hidden by side while it opens — building the Items later would leave
    // the popup unmeasured (HANDOFF §3).
    private void BuildDiffMenu()
    {
        _stageHunkItem.Click += (_, _) => ApplyLines(PatchStagingAction.Stage, wholeHunk: true);
        _stageLinesItem.Click += (_, _) => ApplyLines(PatchStagingAction.Stage, wholeHunk: false);
        _unstageHunkItem.Click += (_, _) => ApplyLines(PatchStagingAction.Unstage, wholeHunk: true);
        _unstageLinesItem.Click += (_, _) => ApplyLines(PatchStagingAction.Unstage, wholeHunk: false);
        _discardHunkItem.Click += (_, _) => ApplyLines(PatchStagingAction.DiscardWorkTree, wholeHunk: true);
        _discardLinesItem.Click += (_, _) => ApplyLines(PatchStagingAction.DiscardWorkTree, wholeHunk: false);
        _selectAllLinesItem.Click += (_, _) => SelectWholeDiff();
        _copyDiffItem.Click += (_, _) => CopyDiffSelection();

        // Avalonia gives SelectableTextBlock a built-in "Copy" ContextFlyout. Left
        // in place it opens ON TOP of this menu (both popups were visible at once
        // in the headless run), so it is dropped and Copy is folded in here.
        _diffView.ContextFlyout = null;

        _diffMenu = new ContextMenu
        {
            Placement = PlacementMode.Pointer,
            ItemsSource = new Control[]
            {
                _stageHunkItem, _stageLinesItem,
                _unstageHunkItem, _unstageLinesItem,
                new Separator(),
                _discardHunkItem, _discardLinesItem,
                new Separator(),
                _selectAllLinesItem,
                _copyDiffItem,
            },
        };
        _diffMenu.Opening += (_, _) => UpdateDiffMenuState();

        // The menu is opened by hand instead of through _diffView.ContextMenu.
        // SelectableTextBlock captures the pointer and marks the press handled, so
        // the built-in ContextRequested path never fires on it (verified headless:
        // right-clicking the diff simply did nothing). Handling the press in the
        // TUNNELLING phase gets in before that, and has the welcome side effect of
        // leaving an existing highlight alone instead of collapsing it.
        _diffScroll.AddHandler(
            PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
            {
                if (!e.GetCurrentPoint(_diffScroll).Properties.IsRightButtonPressed)
                {
                    return;
                }

                e.Handled = true;

                // Which line was right-clicked, worked out from the text layout
                // rather than from the control's caret: a plain click on a
                // SelectableTextBlock does not move SelectionStart, so relying on
                // it left every entry disabled (seen headless). Hit-testing is the
                // same thing the control itself would do, minus the guesswork.
                _pointerCaret = -1;
                try
                {
                    _pointerCaret = _diffView.TextLayout
                        .HitTestPoint(e.GetPosition(_diffView)).TextPosition;
                }
                catch
                {
                    // No layout yet — fall back to whatever is selected.
                }

                SetDiffCaret(_pointerCaret);

                // State is settled BEFORE the popup is shown: mutating Items (or
                // even sizes) from Opening leaves it unmeasured — HANDOFF §3.
                UpdateDiffMenuState();
                _diffMenu.Open(_diffView);
            },
            RoutingStrategies.Tunnel);

        // Remember the last non-empty highlight: on some backends the secondary
        // press collapses the caret before the menu opens, and acting on an empty
        // range would look like the command silently did nothing.
        _diffView.PropertyChanged += (_, e) =>
        {
            if (e.Property != SelectableTextBlock.SelectionStartProperty
                && e.Property != SelectableTextBlock.SelectionEndProperty)
            {
                return;
            }

            int start = Math.Min(_diffView.SelectionStart, _diffView.SelectionEnd);
            int end = Math.Max(_diffView.SelectionStart, _diffView.SelectionEnd);
            if (end > start)
            {
                _lastSelStart = start;
                _lastSelLength = end - start;
            }

            // The status bar follows the caret here too: while line-staging, the diff
            // panel is where the user's "cursor" actually is. The END of the highlight
            // is reported, which is where the caret sits after a drag.
            SetDiffCaret(_diffView.SelectionEnd);
        };
    }

    // Snapshots the highlight and re-labels / enables the diff-menu entries for it.
    private void UpdateDiffMenuState()
    {
        (int first, int last) = SnapshotSelection();
        _menuSelFirstLine = first;
        _menuSelLastLine = last;

        bool patchable = _diffSpans.Length > 0 && _diffPath.Length > 0 && !_busy;
        bool hasLines = patchable && first >= 0 && SpanRangeTouchesContent(first, last);
        bool hasHunk = patchable && first >= 0 && HunkRange(first, last) is not null;

        // Only the side the diff belongs to is offered; the opposite verb would
        // silently build a patch against the wrong blob.
        _stageHunkItem.IsVisible = !_diffStaged;
        _stageLinesItem.IsVisible = !_diffStaged;
        _discardHunkItem.IsVisible = !_diffStaged;
        _discardLinesItem.IsVisible = !_diffStaged;
        _unstageHunkItem.IsVisible = _diffStaged;
        _unstageLinesItem.IsVisible = _diffStaged;

        _stageHunkItem.IsEnabled = hasHunk;
        _unstageHunkItem.IsEnabled = hasHunk;
        _discardHunkItem.IsEnabled = hasHunk;
        _stageLinesItem.IsEnabled = hasLines;
        _unstageLinesItem.IsEnabled = hasLines;
        _discardLinesItem.IsEnabled = hasLines;
        _selectAllLinesItem.IsEnabled = _diffSpans.Length > 0;
        _copyDiffItem.IsEnabled = _diffView.SelectionEnd != _diffView.SelectionStart;
    }

    // Replaces the built-in Copy that was dropped with the default ContextFlyout.
    private void CopyDiffSelection()
    {
        int start = Math.Min(_diffView.SelectionStart, _diffView.SelectionEnd);
        int end = Math.Max(_diffView.SelectionStart, _diffView.SelectionEnd);
        if (end <= start || _diffSpans.Length == 0)
        {
            return;
        }

        // Rebuilt from the spans rather than substring'd out of _diffText: the two
        // coordinate systems differ on CRLF files.
        List<string> picked = [];
        foreach (DiffLineSpan span in _diffSpans)
        {
            if (span.RenderStart + span.RenderLength >= start && span.RenderStart <= end)
            {
                picked.Add(_diffText.Substring(span.SourceStart, span.SourceLength));
            }
        }

        try
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(string.Join("\n", picked));
            SetStatus(string.Format(T("Copied {0} line(s)."), picked.Count));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(T("Could not copy the path: {0}"), ex.Message));
        }
    }

    private void SelectWholeDiff()
    {
        if (_diffSpans.Length == 0)
        {
            return;
        }

        DiffLineSpan last = _diffSpans[^1];
        _diffView.SelectionStart = 0;
        _diffView.SelectionEnd = last.RenderStart + last.RenderLength;
    }

    // The current highlight expressed as a LINE range. Line granularity is the
    // whole trick: PatchManager wants character offsets into the raw diff, but the
    // control reports offsets into the rendered text, and the two differ on CRLF
    // files. Rounding the highlight out to whole lines first — which is also the
    // only granularity `git apply` understands — makes the conversion exact.
    private (int First, int Last) SnapshotSelection()
    {
        if (_diffSpans.Length == 0)
        {
            return (-1, -1);
        }

        // Priority: a live highlight beats everything, then the line the pointer is
        // actually on, then the last highlight seen (for a keyboard-opened menu).
        int start = Math.Min(_diffView.SelectionStart, _diffView.SelectionEnd);
        int end = Math.Max(_diffView.SelectionStart, _diffView.SelectionEnd);
        if (end <= start)
        {
            if (_pointerCaret >= 0)
            {
                start = _pointerCaret;
                end = start + 1;
            }
            else if (_lastSelLength > 0)
            {
                start = _lastSelStart;
                end = _lastSelStart + _lastSelLength;
            }
            else
            {
                return (-1, -1);
            }
        }

        int first = LineAt(start);
        int last = LineAt(Math.Max(start, end - 1));
        return first < 0 || last < 0 ? (-1, -1) : (Math.Min(first, last), Math.Max(first, last));
    }

    private int LineAt(int renderOffset)
    {
        for (int i = 0; i < _diffSpans.Length; i++)
        {
            DiffLineSpan span = _diffSpans[i];
            if (renderOffset >= span.RenderStart && renderOffset <= span.RenderStart + span.RenderLength)
            {
                return i;
            }
        }

        return renderOffset > 0 && _diffSpans.Length > 0 ? _diffSpans.Length - 1 : -1;
    }

    // True when the line range holds at least one +/- line: a selection made only
    // of headers or context produces an empty patch, so the entry stays disabled.
    private bool SpanRangeTouchesContent(int first, int last)
    {
        for (int i = first; i <= last && i < _diffSpans.Length; i++)
        {
            if (_diffSpans[i].HunkIndex < 0)
            {
                continue;
            }

            string line = SourceLine(i);
            if ((line.StartsWith('+') || line.StartsWith('-')) && !line.StartsWith("+++") && !line.StartsWith("---"))
            {
                return true;
            }
        }

        return false;
    }

    private string SourceLine(int index)
    {
        DiffLineSpan span = _diffSpans[index];
        return _diffText.Substring(span.SourceStart, span.SourceLength);
    }

    // The full line range of every hunk the selection touches — this is what makes
    // "Stage hunk" work from a single click anywhere inside it.
    private (int First, int Last)? HunkRange(int first, int last)
    {
        int lo = int.MaxValue;
        int hi = -1;
        for (int i = first; i <= last && i < _diffSpans.Length; i++)
        {
            if (_diffSpans[i].HunkIndex >= 0)
            {
                lo = Math.Min(lo, _diffSpans[i].HunkIndex);
                hi = Math.Max(hi, _diffSpans[i].HunkIndex);
            }
        }

        if (hi < 0)
        {
            return null;
        }

        int firstLine = -1;
        int lastLine = -1;
        for (int i = 0; i < _diffSpans.Length; i++)
        {
            if (_diffSpans[i].HunkIndex >= lo && _diffSpans[i].HunkIndex <= hi)
            {
                if (firstLine < 0)
                {
                    firstLine = i;
                }

                lastLine = i;
            }
        }

        return firstLine < 0 ? null : (firstLine, lastLine);
    }

    private void ApplyLines(PatchStagingAction action, bool wholeHunk)
    {
        if (_diffSpans.Length == 0 || _diffPath.Length == 0)
        {
            SetStatus(T(PatchStagingService.NoSelectionMessage));
            return;
        }

        (int first, int last) = _menuSelFirstLine >= 0
            ? (_menuSelFirstLine, _menuSelLastLine)
            : SnapshotSelection();
        if (first < 0)
        {
            SetStatus(T(PatchStagingService.NoSelectionMessage));
            return;
        }

        if (wholeHunk)
        {
            if (HunkRange(first, last) is not (int hFirst, int hLast))
            {
                SetStatus(T(PatchStagingService.NoSelectionMessage));
                return;
            }

            (first, last) = (hFirst, hLast);
        }

        // Line range -> exact character range in the RAW diff. The end deliberately
        // stops at the last character of the last line, before its newline: one
        // character further and PatchManager would also pull in the line after it.
        int selectionStart = _diffSpans[first].SourceStart;
        int selectionLength = _diffSpans[last].SourceStart + _diffSpans[last].SourceLength - selectionStart;
        if (selectionLength <= 0)
        {
            SetStatus(T(PatchStagingService.NoSelectionMessage));
            return;
        }

        string repo = _repoPath;
        string diffText = _diffText;
        string path = _diffPath;
        bool staged = _diffStaged;
        bool isNew = _diffFileIsNew;
        bool isRenamed = _diffFileIsRenamed;

        // "new" on the work-tree side means untracked: the patch has to create the
        // index entry rather than modify one (see PatchStagingService.Apply).
        bool isUntracked = isNew && !staged;
        int lines = last - first + 1;

        void Run()
        {
            SetStatus(string.Format(DescribeLineAction(action), lines));
            _reselectPath = path;
            _reselectStaged = staged;
            RunGitResult(
                () =>
                {
                    PatchStagingResult result = PatchStagingService.Apply(
                        repo, diffText, selectionStart, selectionLength, action, isNew, isRenamed, isUntracked);
                    return new WorkingDirCommitResult(result.Success, result.Output);
                },
                result =>
                {
                    SetStatus(result.Success
                        ? string.Format(DescribeLineDone(action), lines)
                        : string.Format(T("Patch failed: {0}"), Translate(FirstLine(result.Output))));
                    Reload();
                });
        }

        if (action == PatchStagingAction.DiscardWorkTree)
        {
            // Destructive and unrecoverable — the lines are not in the index either.
            ConfirmThen(
                T("TranslatedStrings/_resetSelectedLinesConfirmation.Text",
                  "Are you sure you want to reset the changes to the selected lines?"),
                Run);
            return;
        }

        Run();
    }

    // The service speaks plain English so it stays UI-free; the dialog is where its
    // few fixed messages get a translation.
    private static string Translate(string message) => message switch
    {
        PatchStagingService.NoHunksMessage => T("This file has no text hunks to patch (binary, or nothing changed)."),
        PatchStagingService.NotUtf8Message => T("The diff is not valid UTF-8; line staging is not available for this file."),
        PatchStagingService.NoSelectionMessage => T("Select one or more diff lines first."),
        PatchStagingService.UntrackedOnlyStageMessage =>
            T("This file is not tracked yet: only staging lines of it is possible."),
        _ => message,
    };

    private static string DescribeLineAction(PatchStagingAction action) => action switch
    {
        PatchStagingAction.Stage => T("Staging {0} line(s) …"),
        PatchStagingAction.Unstage => T("Unstaging {0} line(s) …"),
        _ => T("Discarding {0} line(s) …"),
    };

    private static string DescribeLineDone(PatchStagingAction action) => action switch
    {
        PatchStagingAction.Stage => T("Staged {0} line(s)."),
        PatchStagingAction.Unstage => T("Unstaged {0} line(s)."),
        _ => T("Discarded {0} line(s)."),
    };

    // ---------- stage / unstage ----------

    // Stages every selected unstaged row. Conflicted files are skipped: `git add`
    // on an unmerged path silently marks it resolved (use the conflict entries).
    private void StageSelected()
    {
        List<WorkingDirFileRow> rows =
            [.. SelectedRows(_unstagedList).Where(r => !_conflictPaths.Contains(r.Path))];
        if (rows.Count > 0)
        {
            RunGit(() => _service.Stage(_repoPath, rows));
        }
    }

    private void UnstageSelected()
    {
        List<WorkingDirFileRow> rows = SelectedRows(_stagedList);
        if (rows.Count > 0)
        {
            RunGit(() => _service.Unstage(_repoPath, rows));
        }
    }

    // "Stage all" / "Stage filtered": with a filter on, the button acts on the matching
    // files only — upstream's StageAllAccordingToFilter.
    private void StageAll()
    {
        List<WorkingDirFileRow> rows = [.. Filtered(_unstagedPane)];
        if (rows.Count > 0)
        {
            RunGit(() => _service.Stage(_repoPath, rows));
        }
    }

    private void UnstageAll()
    {
        List<WorkingDirFileRow> rows = [.. Filtered(_stagedPane)];
        if (rows.Count > 0)
        {
            RunGit(() => _service.Unstage(_repoPath, rows));
        }
    }

    // ---------- selection filter (regex) ----------

    private IEnumerable<WorkingDirFileRow> Filtered(FileListPane pane)
    {
        // pane.Rows and not the list's items: a file inside a collapsed folder is
        // hidden, not gone, and "Stage all" must still reach it (M226).
        IEnumerable<WorkingDirFileRow> rows = pane.Rows;
        return pane.FilterActive
            ? rows.Where(r => Regex.IsMatch(r.Path, pane.Pattern, RegexOptions.IgnoreCase))
            : rows;
    }

    // Compiles one pane's pattern and, on success, SELECTS the matching rows of THAT
    // list the way upstream's FileStatusList.SetSelectionFilter does, so the plain
    // Stage / Unstage button acts on them. An invalid pattern leaves the previous
    // selection alone and only reports itself.
    private void ApplyPaneFilter(FileListPane pane)
    {
        string pattern = (pane.FilterBox.Text ?? string.Empty).Trim();

        if (pattern.Length == 0)
        {
            pane.Pattern = string.Empty;
            pane.CountBox.BorderBrush = Brushes.Transparent;
            pane.CountText.Text = string.Empty;
            ToolTip.SetTip(pane.FilterBox, SelectionFilterTip);
            ApplyFilterCaptions();
            return;
        }

        try
        {
            // Compile before anything else: an invalid pattern must not touch the
            // selection or the captions.
            _ = Regex.IsMatch(string.Empty, pattern, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            pane.Pattern = string.Empty;
            pane.CountBox.BorderBrush = Brush("App.DiffRemoved", Brushes.OrangeRed);
            pane.CountText.Text = "!";
            ToolTip.SetTip(
                pane.FilterBox,
                string.Format(T("FormCommit/_selectionFilterErrorToolTip.Text", "Error {0}"), ex.Message));
            ApplyFilterCaptions();
            return;
        }

        pane.Pattern = pattern;
        RememberFilter(pane);
        pane.CountBox.BorderBrush = Brushes.Transparent;
        ToolTip.SetTip(pane.FilterBox, SelectionFilterTip);

        List<WorkingDirFileRow> matches = [.. Filtered(pane)];
        pane.List.SelectedItems?.Clear();
        foreach (WorkingDirFileRow row in matches)
        {
            pane.List.SelectedItems?.Add(row);
        }

        RefreshPaneCount(pane);
        ApplyFilterCaptions();
    }

    // The two "all" buttons say what they will actually do — each from ITS OWN list's
    // filter box now, the way upstream re-captions them from the per-list filters.
    private void ApplyFilterCaptions()
    {
        // Upstream swaps the icon too when a filter is on (StageAllFiltered /
        // UnstageAllFiltered), which is the only cue that "all" now means "the matches".
        // Image-only with the caption as the tooltip, which is upstream's
        // DisplayStyle = Image on toolStageAllItem / toolUnstageAllItem.
        string stageAll = _unstagedPane.FilterActive
            ? T("FormCommit/_stageFiltered.Text", "Stage filtered")
            : T("FormCommit/_stageAll.Text", "Stage all");
        string unstageAll = _stagedPane.FilterActive
            ? T("FormCommit/_unstageFiltered.Text", "Unstage filtered")
            : T("FormCommit/_unstageAll.Text", "Unstage all");

        _stageAllBtn.Content = IconOnly(_unstagedPane.FilterActive ? "StageAllFiltered" : "StageAll", stageAll);
        _unstageAllBtn.Content = IconOnly(_stagedPane.FilterActive ? "UnstageAllFiltered" : "UnstageAll", unstageAll);
        ToolTip.SetTip(_stageAllBtn, stageAll);
        ToolTip.SetTip(_unstageAllBtn, unstageAll);
    }

    private void RefreshPaneCount(FileListPane pane)
    {
        if (!pane.FilterActive)
        {
            return;
        }

        pane.CountText.Text = string.Format(
            "{0}/{1}",
            Filtered(pane).Count(),
            pane.Rows.Count);
    }

    private static string SelectionFilterTip => T(
        "FormCommit/_selectionFilterToolTip.Text",
        "Enter a regular expression to select unstaged files.");

    // ---------- per-file actions (discard / copy path / .gitignore) ----------

    // Discards the work-tree changes of the selected TRACKED file
    // (git checkout -- <path>). Destructive and not undoable, so it is confirmed
    // first, exactly like Take ours / Take theirs.
    private void DiscardSelected()
    {
        List<string> paths = [.. SelectedRows(_unstagedList)
            .Where(r => r.Status != "new" && !_conflictPaths.Contains(r.Path))
            .Select(r => r.Path)];
        DiscardPaths(paths, describe: null);
    }

    /// <summary>
    ///  Restores <paramref name="paths"/> from the index, after asking. Shared by the
    ///  file menu and the folder menu; <paramref name="describe"/> is what the question
    ///  and the outcome call the target — the folder's name when the gesture was a
    ///  folder, otherwise the file itself or a count.
    /// </summary>
    private void DiscardPaths(List<string> paths, string? describe)
    {
        if (paths.Count == 0)
        {
            return;
        }

        string repo = _repoPath;
        string what = describe is { Length: > 0 }
            ? string.Format(T("'{0}' ({1} files)"), describe, paths.Count)
            : paths.Count == 1
                ? $"'{paths[0]}'"
                : string.Format(T("{0} files"), paths.Count);
        ConfirmThen(
            string.Format(
                T("Discard changes to {0}? The files are restored from the index and this cannot be undone."),
                what),
            () =>
            {
                SetStatus(string.Format(T("Discarding changes to {0} …"), what));
                RunGitResult(
                    () =>
                    {
                        WorkingDirCommitResult last = new(true, string.Empty);
                        foreach (string path in paths)
                        {
                            last = _service.ResetFile(repo, path);
                            if (!last.Success)
                            {
                                break;
                            }
                        }

                        return last;
                    },
                    result =>
                    {
                        SetStatus(result.Success
                            ? string.Format(T("Discarded changes to {0}."), what)
                            : string.Format(T("Discard failed: {0}"), FirstLine(result.Output)));
                        Reload();
                    });
            });
    }

    // Builds the "Copy path" entry for one of the two file lists. The item assembles
    // the text itself (absolute native by default, relative and bare name in the
    // sub-menu); this dialog only supplies the selection, the repository root that
    // turns a git path into an absolute one, and the status feedback — the lists sit
    // in a modal with no other confirmation that anything reached the clipboard.
    private CopyPathsMenuItem MakeCopyPathsItem(ListBox list) => new(
        () => SelectedRows(list).Select(r => (string?)r.Path),
        () => _repoPath,
        text => PutOnClipboard(text, SelectedRows(list).Count));

    // Nothing else depends on the clipboard, so a missing one (headless) is reported
    // and otherwise ignored.
    private void PutOnClipboard(string text, int count)
    {
        try
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
            SetStatus(count == 1
                ? string.Format(T("Copied path: {0}"), text)
                : string.Format(T("Copied {0} paths."), count));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(T("Could not copy the path: {0}"), ex.Message));
        }
    }

    private enum GitignoreMode
    {
        Path,
        Extension,
        Folder,
    }

    // The single selected UNTRACKED row (git "??" → status "new"), or null when the
    // selection is anything else. Same semantics as the former panel: the ignore
    // actions never apply to files git already tracks.
    private WorkingDirFileRow? SingleUntracked()
    {
        List<WorkingDirFileRow> rows = SelectedRows(_unstagedList);
        return rows.Count == 1 && rows[0].Status == "new" ? rows[0] : null;
    }

    // Builds the .gitignore pattern for the selected untracked file and appends it,
    // then reloads so the now-ignored file drops out of the unstaged list.
    private void AddSelectedToGitignore(GitignoreMode mode)
    {
        WorkingDirFileRow? row = SingleUntracked();
        if (row is null)
        {
            return;
        }

        string path = row.Path.Replace('\\', '/');
        string pattern;
        switch (mode)
        {
            case GitignoreMode.Extension:
                string ext = System.IO.Path.GetExtension(path).TrimStart('.');
                if (ext.Length == 0)
                {
                    return;
                }

                pattern = "*." + ext;
                break;

            case GitignoreMode.Folder:
                int slash = path.LastIndexOf('/');
                if (slash <= 0)
                {
                    return;
                }

                pattern = path[..slash] + "/";
                break;

            default:
                // Anchor the exact relative path to the repo root with a leading '/'.
                pattern = "/" + path;
                break;
        }

        string repo = _repoPath;
        SetStatus(string.Format(T("Adding '{0}' to .gitignore …"), pattern));
        RunGitResult(
            () => _service.AddToGitignore(repo, pattern),
            result =>
            {
                SetStatus(result.Success
                    ? FirstLine(result.Output)
                    : string.Format(T("Could not update .gitignore: {0}"), FirstLine(result.Output)));
                Reload();
            });
    }

    // ---------- merge conflicts ----------

    // Double-click stages a normal file, but opens the merge tool for an unmerged
    // one (staging a conflicted file would silently mark it resolved).
    private void OnUnstagedDoubleTapped()
    {
        if (SelectedConflicts().Count > 0)
        {
            OpenInMergetool();
            return;
        }

        StageSelected();
    }

    private List<string> SelectedConflicts()
        => [.. _unstagedList.SelectedItems?
            .OfType<WorkingDirFileRow>()
            .Select(r => r.Path)
            .Where(_conflictPaths.Contains) ?? []];

    // Launches the configured merge tool for each selected conflict (detached, off
    // the UI thread). No immediate reload: the tool runs asynchronously, so the user
    // marks the file resolved (or takes ours/theirs) once done.
    private void OpenInMergetool()
    {
        List<string> paths = SelectedConflicts();
        if (paths.Count == 0)
        {
            return;
        }

        string repo = _repoPath;
        SetStatus(T("Launching merge tool…"));
        RunGitResult(
            () =>
            {
                WorkingDirCommitResult last = new(true, string.Empty);
                foreach (string path in paths)
                {
                    last = _service.LaunchMergetool(repo, path);
                    if (!last.Success)
                    {
                        break;
                    }
                }

                return last;
            },
            result => SetStatus(result.Success
                ? T("Merge tool launched. Mark resolved when done.")
                : FirstLine(result.Output)));
    }

    // Resolves the selected conflicts with "ours", "theirs" or a plain mark-resolved
    // (git add), then reloads so the files lose their "U" status. Taking a side
    // overwrites the working-tree file, so it is confirmed first.
    private void ResolveConflicts(string mode)
    {
        List<string> paths = SelectedConflicts();
        if (paths.Count == 0)
        {
            return;
        }

        if (mode is "ours" or "theirs")
        {
            ConfirmThen(
                string.Format(
                    mode == "ours"
                        ? T("Resolve {0} conflict(s) by keeping our version? "
                            + "The other side is discarded in the working tree and cannot be undone.")
                        : T("Resolve {0} conflict(s) by keeping their version? "
                            + "The other side is discarded in the working tree and cannot be undone."),
                    paths.Count),
                () => RunResolve(mode, paths));
            return;
        }

        RunResolve(mode, paths);
    }

    private void RunResolve(string mode, List<string> paths)
    {
        string repo = _repoPath;
        SetStatus(T("Resolving conflict(s)…"));
        RunGitResult(
            () =>
            {
                WorkingDirCommitResult last = new(true, string.Empty);
                foreach (string path in paths)
                {
                    last = mode switch
                    {
                        "ours" => _service.TakeOurs(repo, path),
                        "theirs" => _service.TakeTheirs(repo, path),
                        _ => _service.MarkResolved(repo, path),
                    };

                    if (!last.Success)
                    {
                        break;
                    }
                }

                return last;
            },
            result =>
            {
                SetStatus(result.Success
                    ? string.Format(
                        mode switch
                        {
                            "ours" => T("Resolved {0} conflict(s) keeping our version."),
                            "theirs" => T("Resolved {0} conflict(s) keeping their version."),
                            _ => T("Marked {0} conflict(s) as resolved."),
                        },
                        paths.Count)
                    : string.Format(T("Resolve failed: {0}"), FirstLine(result.Output)));
                Reload();
            });
    }

    // ---------- commit / reset ----------

    private CommitOptions CurrentOptions() => new(
        Amend: _amendBox.IsChecked == true,
        SignOff: _signOff,
        NoVerify: _noVerify,
        ResetAuthor: _resetAuthor,
        CloseAfterCommit: _prefs.CloseCommitDialogAfterCommit);

    // async void: it is an event handler in all but name (three button/hotkey call
    // sites), and every await inside is a modal the user drives.
    private async Task DoCommitAsync(bool push)
    {
        int staged = _stagedPane.Rows.Count;
        string message = _messageBox.Text ?? string.Empty;
        CommitOptions options = CurrentOptions();

        if (_conflictPaths.Count > 0)
        {
            SetStatus(T(
                "FormCommit/_mergeConflicts.Text",
                "There are unresolved merge conflicts, solve merge conflicts before committing."));
            return;
        }

        // A merge commit is legitimate even with an empty index diff: resolving every
        // conflict in favour of "ours" leaves the index identical to HEAD, yet the
        // merge still has to be recorded (git itself allows it while MERGE_HEAD exists).
        if (staged == 0 && !options.Amend && !_mergeInProgress)
        {
            SetStatus(T("FormCommit/_noStagedChanges.Text", "There are no staged changes"));
            return;
        }

        if (message.Trim().Length == 0)
        {
            SetStatus(T("FormCommit/_enterCommitMessage.Text", "Please enter commit message"));
            return;
        }

        // The three confirmations upstream asks for and the port used to skip
        // (FormCommit.cs:1098-1123 and 1191-1231). Order follows upstream: amend
        // first, then the empty merge commit, then "not on a branch".
        if (options.Amend
            && !await ConfirmAsync(
                T("FormCommit/_amendCommit.Text",
                    "You are about to rewrite history.\n"
                    + "Only use Amend if the commit has not been published yet!\n\n"
                    + "Do you want to continue?"),
                T("FormCommit/_amendCommitCaption.Text", "Amend commit")))
        {
            return;
        }

        // An empty merge commit is allowed, but the user may equally have forgotten to
        // stage: upstream asks rather than assuming either way.
        if (staged == 0 && !options.Amend && _mergeInProgress
            && !await ConfirmAsync(
                T("FormCommit/_noFilesStagedAndConfirmAnEmptyMergeCommit.Text",
                    "There are no files staged for this commit.\nAre you sure you want to commit?"),
                T("FormCommit/_noStagedChanges.Text", "There are no staged changes")))
        {
            return;
        }

        if (!await ConfirmDetachedHeadAsync())
        {
            return;
        }

        if (!await ConfirmMessageLengthAsync(message))
        {
            return;
        }

        // User scripts bound to BeforeCommit. A failing one STOPS the commit — that is
        // what a pre-hook is for — and says so on the status line, so the message the
        // user typed is still there to fix and commit again.
        if (!await UserScriptRunner.RunEventAsync(this, UserScriptEvent.BeforeCommit, ScriptContext()))
        {
            SetStatus(T("Commit cancelled by a user script."));
            return;
        }

        // Upstream runs the commit inside FormProcess (FormCommit.cs:1265) so the user
        // sees the command line, git's output and — the reason this matters — whatever
        // the pre-commit hook prints. Same surface the push already uses.
        SetStatus(string.Format(T("Running {0} …"), CommitActionsService.DescribeCommit(options)));
        string repoPath = _repoPath;
        CommitActionsService actions = _actions;
        GitProcessOutcome outcome = await GitProcessDialog.RunStreamingAsync(
            this,
            T("FormCommit/_commitButton.Text", "Commit"),
            emit =>
            {
                CommitActionResult r = actions.Commit(repoPath, message, options, emit);
                return new GitProcessOutcome(r.Success, r.Output);
            });

        // Everything below is the POST-commit work: it must only run when git really
        // committed. In particular a failing hook must leave the message alone, so the
        // user can fix the problem and press Commit again.
        if (!outcome.Success)
        {
            SetStatus(outcome.Aborted
                ? T("Commit aborted.")
                : string.Format(T("Commit failed: {0}"), LastLine(outcome.Output)));
            Reload();
            return;
        }

        _messageBox.Text = string.Empty;
        _amendBox.IsChecked = false;

        // AfterCommit scripts see the commit that was just made, message included: the
        // context is built from the message BEFORE the box is cleared, above.
        await UserScriptRunner.RunEventAsync(this, UserScriptEvent.AfterCommit, ScriptContext(message));
        Committed?.Invoke();
        SetStatus(string.Format(T("Committed ({0})."), CommitActionsService.DescribeCommit(options)));

        // Upstream only consults "after all files committed" when "after each
        // commit" is off, and closes on the state AFTER the reload.
        _closeIfNothingLeft = !options.CloseAfterCommit
            && _prefs.CloseCommitDialogAfterLastCommit
            && !push;
        Reload();

        if (push)
        {
            await PushAsync();
        }

        if (options.CloseAfterCommit)
        {
            Close();
        }
    }

    /// <summary>
    ///  Upstream's "not on a branch" prompt (FormCommit.cs:1191-1231): committing on a
    ///  detached HEAD leaves the commit unreferenced as soon as the user checks
    ///  something else out. Skipped during a rebase, where a detached HEAD is normal
    ///  and expected — upstream skips it for the same reason.
    ///  Returns false when the commit must not proceed.
    /// </summary>
    private async Task<bool> ConfirmDetachedHeadAsync()
    {
        string repo = _repoPath;
        (bool detached, bool rebasing) = await Task.Run(() => ReadHeadState(repo));
        if (!detached || rebasing)
        {
            return true;
        }

        // Upstream offers "Checkout branch", "Create branch" and "Continue". The port
        // has no checkout dialog reachable from here, so only the two it can really
        // perform are offered.
        int choice = await ChooseAsync(
            T("FormCommit/_notOnBranch.Text",
                "This commit will be unreferenced when switching to another branch and can be lost.\n\n"
                + "Do you want to continue?"),
            T("TranslatedStrings/_errorCaptionNotOnBranch.Text", "Not on a branch"),
            [
                T("TranslatedStrings/_buttonCreateBranch.Text", "Create branch"),
                T("TranslatedStrings/_buttonContinue.Text", "Continue"),
            ]);

        if (choice == 0)
        {
            // Creating the branch is itself a git run through RunActionResult; the
            // commit is not chained onto it, the user presses Commit again on the
            // branch that now exists.
            Async.Run(PromptCreateBranchAsync, "creating a branch");
            SetStatus(T("Create the branch, then commit again."));
            return false;
        }

        return choice == 1;
    }

    // Detached HEAD and rebase state read straight from the git directory: ".git/HEAD"
    // holds "ref: refs/heads/<name>" on a branch and a bare hash when detached, and a
    // rebase leaves a rebase-merge / rebase-apply directory behind. No git process, so
    // it is cheap enough to re-read at commit time instead of trusting the cached
    // branch caption.
    private static (bool Detached, bool Rebasing) ReadHeadState(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string gitDir = ResolveGitDir(module, repoPath);
            if (gitDir.Length == 0)
            {
                return (false, false);
            }

            string headPath = System.IO.Path.Combine(gitDir, "HEAD");
            if (!System.IO.File.Exists(headPath))
            {
                return (false, false);
            }

            bool detached = !System.IO.File.ReadAllText(headPath)
                .TrimStart()
                .StartsWith("ref:", StringComparison.Ordinal);
            bool rebasing = System.IO.Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-merge"))
                || System.IO.Directory.Exists(System.IO.Path.Combine(gitDir, "rebase-apply"));
            return (detached, rebasing);
        }
        catch
        {
            // Never block a commit because the state could not be read.
            return (false, false);
        }
    }

    /// <summary>
    ///  "Commit &amp; push". The commit form has no remote/branch pickers, so both are
    ///  probed from the repository (<see cref="PushTrackingService"/>) instead of
    ///  being guessed: the branch goes to its OWN configured remote, and <c>-u</c> is
    ///  only ever passed after the same question the push dialog asks
    ///  (<c>PushDialog.ResolveTrackingAsync</c>).
    ///
    ///  <para>This used to call the two-state <c>PushStreaming</c> overload, which
    ///  hard-codes <c>track: true</c>: every push from here re-pointed the branch's
    ///  upstream at whatever remote happened to be listed first.</para>
    /// </summary>
    private async Task PushAsync()
    {
        string repo = _repoPath;
        PushTracking tracking = await Task.Run(() => new PushTrackingService().Probe(repo));

        if (tracking.Branch.Length == 0)
        {
            SetStatus(T("FormPush/_selectRemote.Text", "No branch to push (detached HEAD?)."));
            return;
        }

        if (tracking.Remote.Length == 0)
        {
            SetStatus(T("FormPush/_selectRemote.Text", "Please select a remote repository"));
            return;
        }

        // Only a branch with no upstream can be offered one, and cancelling the
        // question abandons the push — exactly as the push dialog behaves.
        bool track = false;
        if (tracking.MayOfferTracking)
        {
            int answer = await ChooseAsync(
                string.Format(
                    T("FormPush/_updateTrackingReference.Text",
                        "The branch {0} does not have a tracking reference. Do you want to add a tracking reference to {1}?"),
                    tracking.Branch,
                    $"{tracking.Remote}/{tracking.Branch}"),
                T("FormPush/_pushCaption.Text", "Push"),
                [T("Yes"), T("No")]);
            if (answer < 0)
            {
                SetStatus(T("Push cancelled."));
                return;
            }

            track = answer == 0;
        }

        string remote = tracking.Remote;
        string branch = tracking.Branch;
        bool setUpstream = track;
        await GitProcessDialog.RunStreamingAsync(this, T("FormPush/_pushCaption.Text", "Push"), emit =>
        {
            RemoteOpResult r = new RemoteService().PushStreaming(
                repo, remote, branch, PushForceMode.None, setUpstream, emit, null);
            return new GitProcessOutcome(r.Success, r.Output);
        });
    }

    /// <summary>
    ///  "Reset all changes" / "Reset unstaged changes". Both go through
    ///  <see cref="ResetChangesDialog"/>, exactly as upstream routes both buttons
    ///  through <c>FormResetChanges</c> (<c>FormCommit.cs:2184-2198</c>): the
    ///  unstaged branch used to run <c>git checkout -- .</c> with <b>no confirmation
    ///  at all</b>, and neither branch ever said anything about untracked files, which
    ///  a reset leaves behind.
    ///  <para>
    ///  The counts that drive the dialog come from the rows on screen — the same
    ///  <c>Unstaged.AllItems</c> upstream passes — and the tracked paths it reverts are
    ///  those rows, not a blind <c>.</c>.
    ///  </para>
    /// </summary>
    private async Task DoResetAsync(bool includeStaged)
    {
        // Upstream sizes the question from the WORK-TREE list only (it is the one
        // passed to StartResetChangesDialog), because that is where untracked files
        // can appear at all: an index entry is by definition tracked.
        List<WorkingDirFileRow> unstagedRows = [.. _unstagedPane.Rows];
        List<string> untracked = [.. unstagedRows.Where(IsUntrackedRow).Select(r => r.Path)];

        // Unmerged paths are left out of the checkout list on purpose: `git checkout --
        // <path>` refuses an unmerged path ("path ... is unmerged"), and naming it would
        // make the whole command fail and take the other files' revert down with it.
        // A hard reset (includeStaged) clears them anyway, since it does not go through
        // this list.
        List<string> tracked =
        [
            .. unstagedRows
                .Where(r => !IsUntrackedRow(r) && !_conflictPaths.Contains(r.Path))
                .Select(r => r.Path)
        ];

        // A hard reset covers the index too, so its tracked count includes staged rows
        // that have no work-tree counterpart.
        int trackedCount = tracked.Count;
        if (includeStaged)
        {
            HashSet<string> seen = [.. tracked];
            foreach (WorkingDirFileRow row in _stagedPane.Rows)
            {
                if (seen.Add(row.Path))
                {
                    trackedCount++;
                }
            }
        }

        ResetChangesAction action = await ResetChangesDialog.ShowAsync(
            this, trackedCount, untracked.Count, onlyWorkTree: !includeStaged);

        if (action == ResetChangesAction.Cancel)
        {
            SetStatus(T("Reset cancelled."));
            return;
        }

        bool clean = action == ResetChangesAction.ResetAndDelete;
        string repo = _repoPath;
        SetStatus(T("Resetting changes…"));
        RunGitResult(
            () => _service.ResetChanges(repo, includeStaged, clean, tracked),
            result =>
            {
                SetStatus(result.Success
                    ? clean
                        ? T("Changes reset and untracked files deleted.")
                        : T("Changes reset.")
                    : string.Format(T("Reset failed: {0}"), FirstLine(result.Output)));
                Reload();
            });
    }

    // A row that git only knows from the work tree. Kept in one place: "new" on the
    // index side means "added", which IS tracked.
    private static bool IsUntrackedRow(WorkingDirFileRow row)
        => !row.IsStaged && row.Status == "new";

    // ---------- stash staged ----------

    // `git stash push --staged -m <message>` (with a plumbing fallback for git < 2.35,
    // see CommitActionsService). Only the staged changes go to the stash; unstaged
    // edits stay in the working tree, so both lists are refreshed afterwards.
    private void DoStashStaged()
    {
        if (_stagedPane.Rows.Count == 0)
        {
            SetStatus(T("There are no staged changes to stash."));
            return;
        }

        string message = (_messageBox.Text ?? string.Empty).Trim();
        string stashMessage = message.Length > 0 ? FirstLine(message) : T("Staged changes");

        SetStatus(string.Format(T("Running {0} …"), "git stash push --staged"));
        RunActionResult(
            () => _actions.StashStaged(_repoPath, stashMessage),
            result =>
            {
                SetStatus(result.Success
                    ? string.Format(T("Stashed staged changes: {0}"), stashMessage)
                    : string.Format(T("Stash failed: {0}"), FirstLine(result.Output)));
                Reload();
            });
    }

    // ---------- commit templates ----------

    // Templates are discovered off the UI thread (git config + repository scan),
    // and the MenuFlyout is fully populated BEFORE ShowAt — mutating Items while
    // the popup is open leaves it unmeasured (see HANDOFF §3).
    // Replaces the message with the list of staged submodule bumps, or says so in the
    // status line when there are none — upstream simply does nothing in that case, which
    // reads as a dead menu entry.
    private async Task GenerateSubmoduleMessageAsync()
    {
        string repo = _repoPath;
        List<string> staged = [.. _stagedPane.Rows.Select(r => r.Path)];
        string message = await Task.Run(() =>
        {
            try
            {
                return _service.SubmoduleChangesMessage(repo, staged);
            }
            catch
            {
                return string.Empty;
            }
        });

        if (message.Length == 0)
        {
            SetStatus(T("No staged submodule changes to describe."));
            return;
        }

        _messageBox.Text = message;
        _messageBox.Focus();
    }

    // One entry per toolbar button the strip could not fit, in toolbar order. Captions
    // come from the button's Tag, which ApplyTranslations keeps in step with its face.
    private void ShowToolbarOverflow()
    {
        MenuFlyout flyout = new();
        foreach (Control item in _commitToolbar.HiddenItems)
        {
            if (item is not Button button || !_toolbarActions.TryGetValue(button, out Action<Button>? run))
            {
                continue;
            }

            MenuItem entry = new() { Header = Escape(button.Tag as string ?? string.Empty) };
            entry.Click += (_, _) => run(_toolbarOverflowBtn);
            flyout.Items.Add(entry);
        }

        if (flyout.Items.Count > 0)
        {
            flyout.ShowAt(_toolbarOverflowBtn);
        }
    }

    // Upstream's "Commit message" drop-down (commitMessageToolStripMenuItem): the
    // messages of the last commits, one entry each, labelled with the first line cut to
    // 72 characters, and clicking one REPLACES the message box. Its "Show only my
    // messages" toggle filters by the committer identity the status bar already shows.
    // The one entry not ported is "Generate list of changes in submodules", which builds
    // a message from the submodule bumps of the index — noted in PORTING, not silently
    // dropped.
    private async Task ShowMessageMenuAsync(Button anchor)
    {
        string repo = _repoPath;
        int previousCount = Math.Max(1, _prefs.CommitDialogNumberOfPreviousMessages);
        string authorPattern = _onlyMyMessages && _committerName.Length > 0
            ? $"^{Regex.Escape(_committerName)} <{Regex.Escape(_committerEmail)}>$"
            : string.Empty;

        IReadOnlyList<string> messages = await Task.Run(() =>
        {
            try
            {
                return _service.PreviousCommitMessages(repo, previousCount, authorPattern);
            }
            catch
            {
                return (IReadOnlyList<string>)Array.Empty<string>();
            }
        });

        MenuFlyout flyout = new();
        if (messages.Count == 0)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = T("No previous commit messages found"),
                IsEnabled = false,
            });
        }

        foreach (string message in messages)
        {
            string captured = message;
            string label = captured.Split('\n')[0].Trim();
            if (label.Length > MaxMessageLabel)
            {
                label = label[..MaxMessageLabel] + "…";
            }

            MenuItem item = new() { Header = Escape(label) };
            ToolTip.SetTip(item, captured);
            item.Click += (_, _) =>
            {
                _messageBox.Text = captured.Trim();
                _messageBox.Focus();
            };
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());

        // Upstream's generateListOfChangesInSubmodulesChangesToolStripMenuItem: builds a
        // message out of the staged submodule bumps. Disabled when nothing staged is a
        // submodule, which is also when upstream's handler returns without doing anything.
        MenuItem submodules = new()
        {
            Header = T(
                "FormCommit/generateListOfChangesInSubmodulesChangesToolStripMenuItem.Text",
                "Generate list of changes in submodules"),
        };
        submodules.Click += (_, _) => _ = GenerateSubmoduleMessageAsync();
        flyout.Items.Add(submodules);

        MenuItem onlyMine = new()
        {
            Header = T("FormCommit/ShowOnlyMyMessagesToolStripMenuItem.Text", "Show only my messages"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _onlyMyMessages,
        };
        onlyMine.Click += (_, _) => _onlyMyMessages = !_onlyMyMessages;
        flyout.Items.Add(onlyMine);

        flyout.ShowAt(anchor);
    }

    /// <summary>
    ///  The issues assigned to the token's owner in one of this repository's GitHub
    ///  remotes, or null while they have not been fetched (or were not asked for).
    ///  Upstream's <c>IGitPluginForCommit</c> hook, which adds them as commit templates.
    /// </summary>
    private IReadOnlyList<GitHubIssue>? _githubIssues;

    /// <summary>
    ///  Fetches them ONCE, in the background, when the dialog opens. Upstream fetches
    ///  on the PreCommit event, which is to say while the dialog is being built; a
    ///  network round trip on that path is a dialog that opens late for a feature most
    ///  users have off, so the port asks after the window is up and the menu shows what
    ///  it has at the time.
    /// </summary>
    private void PrefetchGitHubIssues()
    {
        if (!_prefs.GitHubIssueCommitMessages)
        {
            return;
        }

        GitHubService service = new(_prefs);
        if (!service.IsConfigured)
        {
            return;
        }

        string repo = _repoPath;
        Async.Run(
            async () =>
            {
                IReadOnlyList<GitHubHostedRemote> remotes = await Task.Run(() => service.GetHostedRemotes(repo));
                if (remotes.Count == 0)
                {
                    return;
                }

                IReadOnlyList<GitHubIssue> issues;
                try
                {
                    issues = await service.CreateClient().GetAssignedIssuesAsync(CancellationToken.None);
                }
                catch (GitHubApiException)
                {
                    // A bad token or an unreachable host must not put an error box in
                    // front of someone who came here to write a commit message.
                    return;
                }

                // Only the issues of a repository this checkout actually has a remote
                // for: "fixes #12" means nothing if #12 belongs to another project.
                _githubIssues = [.. issues
                    .Where(i => remotes.Any(r =>
                        string.Equals(r.Owner, i.Repository?.OwnerLogin, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Repository, i.Repository?.Name, StringComparison.OrdinalIgnoreCase)))
                    .Take(Math.Max(1, _prefs.GitHubIssueCommitMessageCount))];
            },
            "fetching the assigned GitHub issues");
    }

    private async Task ShowTemplatesMenuAsync(Button anchor)
    {
        string repo = _repoPath;
        IReadOnlyList<CommitTemplate> templates = await Task.Run(() =>
        {
            try
            {
                return _actions.ListTemplates(repo);
            }
            catch
            {
                return (IReadOnlyList<CommitTemplate>)Array.Empty<CommitTemplate>();
            }
        });

        MenuFlyout flyout = new();
        if (templates.Count == 0)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = T("No commit templates found"),
                IsEnabled = false,
            });
        }
        else
        {
            foreach (CommitTemplate template in templates)
            {
                CommitTemplate captured = template;
                // Avalonia menu headers treat '_' as an access-key marker, so it
                // must be doubled to survive in file names like PULL_REQUEST_TEMPLATE.md.
                MenuItem item = new() { Header = Escape($"{captured.Name}  ({captured.Source})") };
                ToolTip.SetTip(item, captured.Path);
                item.Click += (_, _) => ApplyTemplate(captured);
                flyout.Items.Add(item);
            }
        }

        // The GitHub issues assigned to me, offered as a message to start from —
        // upstream's issue commit-message helper. Silent when the feature is off or the
        // fetch found nothing: an empty section would be a promise this repository
        // cannot keep.
        if (_githubIssues is { Count: > 0 } issues)
        {
            flyout.Items.Add(new Separator());
            foreach (GitHubIssue issue in issues)
            {
                GitHubIssue captured = issue;
                MenuItem item = new()
                {
                    Header = Escape(string.Format(
                        CultureInfo.CurrentCulture, "#{0}: {1}", captured.Number, captured.Title)),
                };
                item.Click += (_, _) =>
                {
                    // Upstream's exact wording (GetIssueDescription), so a message
                    // written in either build closes the issue the same way.
                    _messageBox.Text = string.Format(
                        CultureInfo.CurrentCulture,
                        "\nFixes #{0} : {1}\n\n{2}\n",
                        captured.Number,
                        captured.Title,
                        captured.Body ?? string.Empty);
                    _messageBox.Focus();
                };
                flyout.Items.Add(item);
            }
        }

        flyout.Items.Add(new Separator());
        MenuItem clear = new() { Header = T("Clear message") };
        clear.Click += (_, _) =>
        {
            _messageBox.Text = string.Empty;
            SetStatus(T("Commit message cleared."));
        };
        flyout.Items.Add(clear);

        flyout.ShowAt(anchor);
    }

    private void ApplyTemplate(CommitTemplate template)
    {
        Async.OffUi(
            () => CommitActionsService.ReadTemplate(template),
            text =>
            {
                _messageBox.Text = text;
                _messageBox.Focus();
                SetStatus(string.Format(T("Applied commit template {0}."), template.Name));
            },
            "reading a commit template");
    }

    // ---------- create branch ----------

    // Prompts for a name, validates it with `git check-ref-format --branch` (plus a
    // duplicate check), then runs `git checkout -b <name> HEAD`, carrying the staged
    // and unstaged changes over to the new branch, exactly like the original form.
    private async Task PromptCreateBranchAsync()
    {
        Theming.ZoomWindow prompt = new()
        {
            Title = T("FormCreateBranch/$this.Text", "Create branch"),
            Width = 440,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", Brushes.DimGray),
        };

        TextBox nameBox = new() { Watermark = "new-branch-name", Width = 400 };
        CheckBox checkoutBox = new()
        {
            Content = T("FormCreateBranch/chkCheckoutAfterCreate.Text", "Checkout after create"),
            IsChecked = true,
        };
        TextBlock error = new()
        {
            Foreground = Brush("App.DiffRemoved", Brushes.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
        };

        string? chosen = null;
        bool checkout = true;

        Button create = new()
        {
            Content = T("FormCreateBranch/cmdOk.Text", "Create branch"),
            IsDefault = true,
        };
        Button cancel = MakeButton(T("FormCommit/Cancel.Text", "Cancel"), prompt.Close);
        create.Click += (_, _) => Async.Run(
            async () =>
        {
            string name = (nameBox.Text ?? string.Empty).Trim();
            create.IsEnabled = false;
            string? problem = await Task.Run(() =>
            {
                try
                {
                    return _actions.ValidateBranchName(_repoPath, name);
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });

            create.IsEnabled = true;
            if (problem is not null)
            {
                error.Text = problem;
                return;
            }

            chosen = name;
            checkout = checkoutBox.IsChecked == true;
            prompt.Close();
        },
            "validating the new branch name");

        prompt.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = T("Create a new branch at the current HEAD:"),
                    Foreground = Brush("App.Foreground", Brushes.Gainsboro),
                },
                nameBox,
                checkoutBox,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { create, cancel },
                },
            },
        };

        await prompt.ShowDialog(this);
        if (chosen is null)
        {
            return;
        }

        string branch = chosen;
        bool doCheckout = checkout;
        if (_busy)
        {
            return;
        }

        SetStatus(string.Format(
            T("Running {0} …"),
            $"git {(doCheckout ? "checkout -b" : "branch")} {branch} HEAD"));

        // Same command as CommitActionsService.CreateBranch produced, but routed
        // through the process dialog so a failure is readable instead of a truncated
        // status line. _busy is ours now that RunActionResult is gone.
        bool ok;
        _busy = true;
        try
        {
            ok = await RefProcessRunner.CreateBranchAsync(this, _repoPath, branch, "HEAD", doCheckout);
        }
        finally
        {
            _busy = false;
        }

        SetStatus(ok
            ? string.Format(
                doCheckout
                    ? T("Created and checked out branch '{0}'.")
                    : T("Created branch '{0}'."),
                branch)
            : T("Create branch failed — see the process output."));

        // Refreshed on failure too: an aborted checkout -b may already have moved HEAD.
        RefreshBranchCaption();
        Reload();
    }

    // ---------- options ----------

    // Every entry maps to a real `git commit` flag (except "Close dialog after
    // commit"), applied by CommitActionsService.Commit. The menu is rebuilt on each
    // click so the check marks always reflect the current state.
    private void ShowOptionsMenu(Button anchor)
    {
        MenuFlyout flyout = new();

        flyout.Items.Add(Toggle(
            T("FormCommit/_amendCommitCaption.Text", "Amend commit") + "  (--amend)",
            _amendBox.IsChecked == true,
            v => _amendBox.IsChecked = v));
        flyout.Items.Add(Toggle(
            T("FormCommit/signOffToolStripMenuItem.Text", "Sign-off commit") + "  (--signoff)",
            _signOff,
            v => _signOff = v));
        flyout.Items.Add(Toggle(
            T("FormCommit/noVerifyToolStripMenuItem.Text", "No verify") + "  (--no-verify)",
            _noVerify,
            v => _noVerify = v));
        flyout.Items.Add(Toggle(
            T("FormCommit/ResetAuthor.Text", "Reset author") + "  (--reset-author)",
            _resetAuthor,
            v => _resetAuthor = v));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Toggle(
            T("FormCommit/closeDialogAfterEachCommitToolStripMenuItem.Text", "Close dialog after each commit"),
            _prefs.CloseCommitDialogAfterCommit,
            v => SaveOption(p => p.CloseCommitDialogAfterCommit = v)));
        flyout.Items.Add(Toggle(
            T("FormCommit/closeDialogAfterAllFilesCommittedToolStripMenuItem.Text",
                "Close dialog after all files committed"),
            _prefs.CloseCommitDialogAfterLastCommit,
            v => SaveOption(p => p.CloseCommitDialogAfterLastCommit = v)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Toggle(
            T("FormCommit/refreshDialogOnFormFocusToolStripMenuItem.Text",
                "Refresh dialog on form focus"),
            _prefs.RefreshCommitDialogOnFocus,
            v => SaveOption(p => p.RefreshCommitDialogOnFocus = v)));
        flyout.Items.Add(Toggle(
            T("FormCommit/tsmiSelectStagedOnEnterMessage.Text",
                "Select staged files on entering the commit message"),
            _prefs.CommitDialogSelectStagedOnEnterMessage,
            v => SaveOption(p => p.CommitDialogSelectStagedOnEnterMessage = v)));

        flyout.ShowAt(anchor);

        MenuItem Toggle(string text, bool value, Action<bool> set)
        {
            MenuItem item = new() { Header = (value ? "☑  " : "☐  ") + text };
            item.Click += (_, _) =>
            {
                set(!value);
                SetStatus(string.Format(
                    T("Commit command: {0}"),
                    CommitActionsService.DescribeCommit(CurrentOptions())));
            };
            return item;
        }
    }

    // ---------- status bar ----------

    /// <summary>
    ///  Upstream's status strip, rebuilt as a bar docked below the button column:
    ///  <c>Committer &lt;name&gt; &lt;mail&gt;</c> · <c>branch → origin/branch</c> ·
    ///  <c>Staged x/y  Ln n  Col n</c> (FormCommit.Designer.cs:805-909).
    /// </summary>
    private Border BuildStatusBar()
    {
        // Upstream puts its toolStripStatusBranchIcon in front of the branch name.
        StackPanel branchPanel = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Theming.IconLoader.Image("Branch") is { } branchIcon)
        {
            branchIcon.VerticalAlignment = VerticalAlignment.Center;
            branchIcon.Margin = new Thickness(0, 0, 4, 0);
            branchPanel.Children.Add(branchIcon);
        }

        branchPanel.Children.Add(_branchStatusText);
        branchPanel.Children.Add(_remoteStatusText);
        _branchStatusText.Margin = new Thickness(0, 0, 6, 0);

        StackPanel counters = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _stagedCountText, _lnColText },
        };
        _stagedCountText.Margin = new Thickness(0, 0, 12, 0);

        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
        };
        Grid.SetColumn(_committerText, 0);
        Grid.SetColumn(branchPanel, 1);
        Grid.SetColumn(counters, 2);
        branchPanel.Margin = new Thickness(12, 0, 18, 0);
        grid.Children.Add(_committerText);
        grid.Children.Add(branchPanel);
        grid.Children.Add(counters);

        return new Border
        {
            Background = Brush("App.Panel", Brushes.Black),
            BorderBrush = Brush("App.Rule", Brushes.Gray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = StyleDensity.BarButtonWide,
            ClipToBounds = true,
            Child = grid,
        };
    }

    // The caret the bar reports. Upstream reads it off the commit message box
    // (FormCommit.cs:2428-2429); the port also reports the DIFF panel's caret, which
    // is the one that matters while line-staging — whichever moved last wins.
    private void TrackCaret()
    {
        _messageBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_formattingMessage && !_formatScheduled)
            {
                // POSTED, not called here: while TextProperty is being raised the box has
                // the new text but the OLD caret index, so formatting now would compute
                // the caret fix-up one character behind and leave the character just
                // typed on the wrong side of an inserted line. Background priority puts
                // the work after the input handling that moves the caret.
                _formatScheduled = true;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        _formatScheduled = false;
                        FormatMessage();
                    },
                    DispatcherPriority.Background);
            }

            if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.TextProperty)
            {
                SetCaret(_messageBox.Text ?? string.Empty, _messageBox.CaretIndex);
            }
        };
    }

    // Line / column of <paramref name="caret"/> inside <paramref name="text"/>, both
    // 1-based; an empty surface reports 0 / 0, the value upstream's labels start at.
    private void SetCaret(string text, int caret)
    {
        if (text.Length == 0)
        {
            _caretLine = 0;
            _caretColumn = 0;
            RenderStatusBar();
            return;
        }

        int clamped = Math.Clamp(caret, 0, text.Length);
        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < clamped; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        _caretLine = line;
        _caretColumn = clamped - lineStart + 1;
        RenderStatusBar();
    }

    // The diff panel's caret, expressed in the coordinates the user sees: the line
    // index within the rendered diff (1-based) and the column inside that line.
    private void SetDiffCaret(int renderOffset)
    {
        if (_diffSpans.Length == 0 || renderOffset < 0)
        {
            return;
        }

        int line = LineAt(renderOffset);
        if (line < 0)
        {
            return;
        }

        _caretLine = line + 1;
        _caretColumn = Math.Max(0, renderOffset - _diffSpans[line].RenderStart) + 1;
        RenderStatusBar();
    }

    private void RenderStatusBar()
    {
        // Upstream's committer line, with the same "/<key> not configured/" filler it
        // uses when the setting is missing (FormCommit.cs:2236-2246).
        string name = _committerName.Length > 0 ? _committerName : NotConfigured("user.name");
        string mail = _committerEmail.Length > 0 ? _committerEmail : NotConfigured("user.email");
        _committerText.Text = $"{T("FormCommit/_commitCommitterInfo.Text", "Committer")} {name} <{mail}>";

        // "<branch> →" / "<push target>", left empty when HEAD is not on a local
        // branch — exactly what upstream leaves behind (FormCommit.cs:843-849).
        _branchStatusText.Text = _titleBranch.Length > 0 && _pushTarget.Length > 0
            ? _titleBranch + " →"
            : _titleBranch;
        _remoteStatusText.Text = _pushTarget;

        string counts = string.Format(
            "{0} {1}/{2}",
            T("FormCommit/commitStagedCountLabel.Text", "Staged"),
            _stagedPane.Rows.Count,
            _stagedPane.Rows.Count + _unstagedPane.Rows.Count);
        _stagedCountText.Text = counts;
        _lnColText.Text = string.Format(
            "{0} {1} {2} {3}",
            T("FormCommit/commitCursorLineLabel.Text", "Ln"),
            _caretLine,
            T("FormCommit/commitCursorColumnLabel.Text", "Col"),
            _caretColumn);
    }

    private static string NotConfigured(string key)
        => "/" + string.Format(T("TranslatedStrings/_notConfigured.Text", "{0} not configured"), key) + "/";

    private static TextBlock MakeStatusLabel() => new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("App.Foreground", Brushes.Gainsboro),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    // Everything the status bar needs from git, read in ONE off-UI-thread pass.
    private sealed record StatusBarInfo(string Branch, string PushTarget, string UserName, string UserEmail);

    private void RefreshBranchCaption()
    {
        string repo = _repoPath;
        Async.OffUi(
            () => ReadStatusBarInfo(repo),
            info =>
            {
                _titleBranch = info.Branch;
                _pushTarget = info.PushTarget;
                _committerName = info.UserName;
                _committerEmail = info.UserEmail;
                UpdateTitle();
                RenderStatusBar();
            },
            "reading the status bar information");
    }

    /// <summary>
    ///  The branch, where it would be pushed, and the effective committer identity.
    ///  <para>The push target follows upstream's rules exactly
    ///  (<c>FormCommit.UpdateBranchNameDisplayAsync</c>, :833-869): the configured
    ///  upstream when there is one; otherwise <c>&lt;origin-or-first-remote&gt;/&lt;branch&gt;
    ///  (untracked)</c>; <c>(remote not configured)</c> when the repository has no
    ///  remote at all; and NOTHING when HEAD is not on a local branch — no invented
    ///  string in any of the four cases.</para>
    /// </summary>
    private StatusBarInfo ReadStatusBarInfo(string repo)
    {
        string branch;
        try
        {
            branch = _actions.CurrentBranch(repo);
        }
        catch
        {
            branch = string.Empty;
        }

        try
        {
            GitModule module = GitContext.CreateModule(repo);
            string name = GitOutput(module, "config --get user.name");
            string mail = GitOutput(module, "config --get user.email");

            // One call answers both questions: an empty result means HEAD is not on a
            // local branch (detached, or a name git does not know), in which case
            // upstream shows no push target at all.
            string refInfo = branch.Length > 0
                ? GitOutput(module, $"for-each-ref --format=%(refname:short)%09%(upstream:short) refs/heads/{branch}")
                : string.Empty;
            if (refInfo.Length == 0)
            {
                return new StatusBarInfo(branch, string.Empty, name, mail);
            }

            string[] parts = refInfo.Split('\t');
            string upstreamRef = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            if (upstreamRef.Length > 0)
            {
                return new StatusBarInfo(branch, upstreamRef, name, mail);
            }

            string[] remotes = GitOutput(module, "remote")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? chosen = remotes.FirstOrDefault(r => r == "origin")
                ?? remotes.OrderBy(r => r, StringComparer.Ordinal).FirstOrDefault();
            string target = chosen is not null
                ? $"{chosen}/{branch} {T("FormCommit/_untrackedRemote.Text", "(untracked)")}"
                : T("FormCommit/_statusBarBranchWithoutRemote.Text", "(remote not configured)");
            return new StatusBarInfo(branch, target, name, mail);
        }
        catch
        {
            return new StatusBarInfo(branch, string.Empty, string.Empty, string.Empty);
        }
    }

    private static string GitOutput(GitModule module, string arguments)
    {
        try
        {
            var result = module.GitExecutable.Execute(arguments, throwOnErrorExit: false);
            return result.ExitedSuccessfully ? (result.StandardOutput ?? string.Empty).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // "Commit to <branch> (<repo>)" — the same format string the original form uses,
    // so the translated catalogues fit it exactly.
    private void UpdateTitle()
        => Title = _titleBranch.Length > 0
            ? string.Format(T("FormCommit/_formTitle.Text", "Commit to {0} ({1})"), _titleBranch, _repoPath)
            : T("FormCommit/$this.Text", "Commit");

    // Asks, then acts, from a void context. The five call sites are all inside plain
    // handlers, so the wait cannot be awaited there: Async.Run is what keeps a throw
    // inside onConfirmed from escaping as an unobserved exception.
    private void ConfirmThen(string prompt, Action onConfirmed)
        => Async.Run(() => ConfirmThenAsync(prompt, onConfirmed), "asking for confirmation");

    // Simple in-dialog confirmation flyout on the status line via a modal child window.
    private async Task ConfirmThenAsync(string prompt, Action onConfirmed)
    {
        if (await ConfirmAsync(prompt))
        {
            onConfirmed();
        }
    }

    /// <summary>
    ///  The length check upstream runs last, right before the commit
    ///  (<c>FormCommit.IsCommitMessageValid</c>): a subject longer than
    ///  <c>CommitValidationMaxCntCharsFirstLine</c>, or any line longer than
    ///  <c>CommitValidationMaxCntCharsPerLine</c>, is a question and not a refusal —
    ///  answering No returns to the editor, Yes commits as typed.
    ///
    ///  <para>Both limits default to 0 = off, so a user who has not set them never sees
    ///  this. Only the FIRST offending body line is reported: upstream asks once per
    ///  line, which turns a pasted log into a queue of identical modal questions.</para>
    /// </summary>
    private async Task<bool> ConfirmMessageLengthAsync(string message)
    {
        string[] lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return true;
        }

        int first = _prefs.CommitValidationFirstLineMaxChars;
        if (first > 0 && lines[0].TrimEnd('\r').Length > first
            && !await ConfirmAsync(
                T("FormCommit/_commitMsgFirstLineInvalid.Text",
                    "First line of commit message contains too many characters."
                    + "\nDo you want to continue?"),
                T("FormCommit/_commitValidationCaption.Text", "Commit message")))
        {
            return false;
        }

        int perLine = _prefs.CommitValidationMaxCharsPerLine;
        if (perLine <= 0)
        {
            return true;
        }

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (line.Length <= perLine)
            {
                continue;
            }

            return await ConfirmAsync(
                TFormat("FormCommit/_commitMsgLineInvalid.Text",
                    "The following line of commit message contains too many characters:"
                    + "\n{0}\nDo you want to continue?",
                    line),
                T("FormCommit/_commitValidationCaption.Text", "Commit message"));
        }

        return true;
    }

    /// <summary>
    ///  Applies the two formatting settings as the user types: keeps line two empty
    ///  (<c>CommitValidationSecondLineMustBeEmpty</c>) and breaks a body line that has
    ///  grown past the per-line limit (<c>CommitValidationAutoWrap</c>).
    ///
    ///  <para>A break REPLACES the space it happens at, so the text keeps its length and
    ///  only the inserted blank line can move the caret — which is why the caret fix-up
    ///  below is a single conditional increment and not an offset map. A line with no
    ///  space inside the limit (a URL, a long path) is left alone rather than cut mid-word:
    ///  upstream wraps only at whitespace too, and a broken path is worse than a long one.</para>
    ///
    ///  <para>Wrapping SPLITS, it does not re-flow: text is never pulled back up from the
    ///  next line. Re-flowing would fight the user editing an earlier paragraph, and
    ///  upstream's own re-flow is limited to the line being typed.</para>
    /// </summary>
    private void FormatMessage()
    {
        int limit = _prefs.CommitValidationMaxCharsPerLine;
        bool wrap = _prefs.CommitValidationAutoWrap && limit > 0;
        bool blankSecond = _prefs.CommitValidationSecondLineMustBeEmpty;
        if (_formattingMessage || (!wrap && !blankSecond))
        {
            return;
        }

        string text = _messageBox.Text ?? string.Empty;
        int caret = Math.Clamp(_messageBox.CaretIndex, 0, text.Length);
        string[] source = text.Split('\n');
        StringBuilder built = new(text.Length + 1);
        int newCaret = caret;
        int lineStart = 0;

        for (int i = 0; i < source.Length; i++)
        {
            if (i > 0)
            {
                built.Append('\n');
            }

            string line = source[i];
            if (blankSecond && i == 1 && line.Trim().Length > 0)
            {
                built.Append('\n');
                if (caret >= lineStart)
                {
                    newCaret++;
                }
            }

            built.Append(wrap && i >= 1 ? WrapLine(line, limit) : line);
            lineStart += line.Length + 1;
        }

        string formatted = built.ToString();
        if (string.Equals(formatted, text, StringComparison.Ordinal))
        {
            return;
        }

        _formattingMessage = true;
        try
        {
            _messageBox.Text = formatted;
            _messageBox.CaretIndex = Math.Clamp(newCaret, 0, formatted.Length);
        }
        finally
        {
            _formattingMessage = false;
        }
    }

    // Breaks <paramref name="line"/> at the last space at or before each limit. Returns
    // it unchanged when no such space exists, so a long unbroken token survives intact.
    private static string WrapLine(string line, int limit)
    {
        if (line.Length <= limit)
        {
            return line;
        }

        StringBuilder wrapped = new(line.Length);
        int start = 0;
        while (line.Length - start > limit)
        {
            int from = Math.Min(start + limit, line.Length - 1);
            int cut = line.LastIndexOf(' ', from, from - start + 1);
            if (cut <= start)
            {
                break;
            }

            wrapped.Append(line, start, cut - start).Append('\n');
            start = cut + 1;
        }

        wrapped.Append(line, start, line.Length - start);
        return wrapped.ToString();
    }

    /// <summary>
    ///  Awaitable yes/cancel confirmation, so several of them can be chained before a
    ///  single action (the commit path asks up to three).
    /// </summary>
    private async Task<bool> ConfirmAsync(string prompt, string? caption = null)
        => await ChooseAsync(prompt, caption, [T("Yes")]) == 0;

    /// <summary>
    ///  A modal question with N ordered choices plus Cancel; returns the index of the
    ///  chosen one, or -1 when cancelled. Upstream uses a TaskDialog with command
    ///  links for exactly this (the "not on a branch" prompt offers three).
    /// </summary>
    private async Task<int> ChooseAsync(string prompt, string? caption, IReadOnlyList<string> choices)
    {
        Theming.ZoomWindow confirm = new()
        {
            Title = caption ?? T("Confirm"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", Brushes.DimGray),
        };

        int picked = -1;
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            buttons.Children.Add(MakeButton(choices[i], () =>
            {
                picked = index;
                confirm.Close();
            }));
        }

        buttons.Children.Add(MakeButton(T("FormCommit/Cancel.Text", "Cancel"), confirm.Close));

        confirm.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = prompt,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("App.Foreground", Brushes.Gainsboro),
                },
                buttons,
            },
        };

        await confirm.ShowDialog(this);
        return picked;
    }

    // ---------- shared execution ----------

    private void RunGit(Func<WorkingDirCommitResult> work)
        => RunGitResult(work, r =>
        {
            if (!r.Success)
            {
                SetStatus(FirstLine(r.Output));
            }

            Reload();
        });

    // Same contract as RunGitResult for the CommitActionsService result type: the
    // work runs on the thread pool, the callback on the UI thread.
    private void RunActionResult(Func<CommitActionResult> work, Action<CommitActionResult> onResult)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Async.OffUi(
            () =>
            {
                try
                {
                    return work();
                }
                catch (Exception ex)
                {
                    return new CommitActionResult(false, ex.Message);
                }
            },
            result =>
            {
                _busy = false;
                onResult(result);
            },
            "running a git command");
    }

    private void RunGitResult(Func<WorkingDirCommitResult> work, Action<WorkingDirCommitResult> onResult)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Async.OffUi(
            () =>
            {
                try
                {
                    return work();
                }
                catch (Exception ex)
                {
                    return new WorkingDirCommitResult(false, ex.Message);
                }
            },
            result =>
            {
                _busy = false;
                onResult(result);
            },
            "running a git command");
    }

    // Everything one Reload needs, gathered in a single off-UI-thread pass.
    private sealed record ReloadSnapshot(
        WorkingDirStatus Status,
        bool Merging,
        string MergeMessage,
        int HiddenByIndexFlag);

    // True when the repository has an in-progress merge, i.e. MERGE_HEAD exists in
    // the REAL git directory. That is not always "<repo>/.git": in a linked worktree
    // `.git` is a file pointing at <main>/.git/worktrees/<name>, and MERGE_HEAD lives
    // there. GitModule.WorkingDirGitDir already resolves this; `git rev-parse
    // --git-dir` is the fallback. Also returns MERGE_MSG so the commit message can be
    // pre-populated the way the original form does.
    private static (bool Merging, string MergeMessage) ReadMergeState(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string gitDir = ResolveGitDir(module, repoPath);
            if (gitDir.Length == 0
                || !System.IO.File.Exists(System.IO.Path.Combine(gitDir, "MERGE_HEAD")))
            {
                return (false, string.Empty);
            }

            string msgPath = System.IO.Path.Combine(gitDir, "MERGE_MSG");
            string message = System.IO.File.Exists(msgPath)
                ? System.IO.File.ReadAllText(msgPath)
                : string.Empty;
            return (true, message);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static string ResolveGitDir(GitModule module, string repoPath)
    {
        string gitDir = string.Empty;
        try
        {
            gitDir = module.WorkingDirGitDir ?? string.Empty;
        }
        catch
        {
            // fall through to rev-parse
        }

        if (gitDir.Length == 0)
        {
            try
            {
                var res = module.GitExecutable.Execute("rev-parse --git-dir", throwOnErrorExit: false);
                if (res.ExitedSuccessfully)
                {
                    gitDir = (res.StandardOutput ?? string.Empty).Trim();
                }
            }
            catch
            {
                // fall through to the conventional location
            }
        }

        if (gitDir.Length == 0)
        {
            gitDir = System.IO.Path.Combine(repoPath, ".git");
        }

        // rev-parse may answer with a path relative to the working directory.
        return System.IO.Path.IsPathRooted(gitDir)
            ? gitDir
            : System.IO.Path.Combine(repoPath, gitDir);
    }

    private void Reload()
    {
        string repo = _repoPath;
        Async.OffUi(
            () =>
        {
            WorkingDirStatus status;
            try
            {
                status = _service.LoadStatus(repo);
            }
            catch
            {
                status = new WorkingDirStatus([], [], []);
            }

            (bool merging, string mergeMessage) = ReadMergeState(repo);

            // Files hidden by skip-worktree / assume-unchanged are in NEITHER list
            // (git status does not report them), so the count is read here, off the
            // UI thread, and only used to caption/enable the restore entry.
            int hidden;
            try
            {
                hidden = _service.ListHiddenByIndexFlag(repo).Count;
            }
            catch
            {
                hidden = 0;
            }

            return new ReloadSnapshot(status, merging, mergeMessage, hidden);
        },
            snapshot =>
        {
            WorkingDirStatus status = snapshot.Status;
            _hiddenByIndexFlag = snapshot.HiddenByIndexFlag;
            ApplyMergeState(snapshot);

            // Unmerged paths are shown inside the unstaged list with a "U" status,
            // like the original commit form, rather than in a separate panel.
            _conflictPaths.Clear();
            foreach (string path in status.Conflicts)
            {
                _conflictPaths.Add(path);
            }

            List<WorkingDirFileRow> unstaged = [.. status.Unstaged
                .Select(r => _conflictPaths.Contains(r.Path)
                    ? r with { Status = "U conflict" }
                    : r)];

            // Defensive: surface conflicts the work-tree listing may have missed.
            foreach (string path in status.Conflicts)
            {
                if (!unstaged.Any(r => string.Equals(r.Path, path, StringComparison.Ordinal)))
                {
                    unstaged.Insert(0, new WorkingDirFileRow(path, "U conflict", false));
                }
            }

            // The untracked toggle hides rows git DID report; a hidden row is in the
            // list's Items for nobody, so "Stage all" cannot reach it either.
            if (!_showUntracked)
            {
                unstaged = [.. unstaged.Where(r => !IsUntrackedRow(r))];
            }

            // Each list is ordered by its own sort key, and a NEW list instance is
            // handed to ItemsSource (M50).
            SetPaneRows(_unstagedPane, unstaged);

            // An unmerged path is reported by the index listing too; showing it in
            // both lists would be misleading, so it stays only in the unstaged one.
            SetPaneRows(
                _stagedPane,
                _conflictPaths.Count == 0
                    ? status.Staged
                    : status.Staged.Where(r => !_conflictPaths.Contains(r.Path)));
            _conflictBanner.IsVisible = _conflictPaths.Count > 0;
            // An empty pane is the "no changes" line alone: upstream hides the filter
            // row with the list (FileStatusList.SetFileStatusListVisibility).
            _unstagedEmpty.IsVisible = _unstagedPane.Rows.Count == 0;
            _stagedEmpty.IsVisible = _stagedPane.Rows.Count == 0;
            _unstagedPane.FilterRow.IsVisible = !_unstagedEmpty.IsVisible || _unstagedPane.FilterActive;
            _stagedPane.FilterRow.IsVisible = !_stagedEmpty.IsVisible || _stagedPane.FilterActive;
            RestoreDiffSelection();
            RenderStatus();

            // The lists are new objects, so the filter's match count is stale. Only the
            // COUNT is refreshed: re-selecting here would fight RestoreDiffSelection,
            // which has just put the user back on the file they were staging hunks of.
            RefreshPaneCount(_unstagedPane);
            RefreshPaneCount(_stagedPane);

            // "Close dialog after all files committed": only now, on the snapshot
            // taken AFTER the commit, is it known whether anything is left to stage.
            if (_closeIfNothingLeft)
            {
                _closeIfNothingLeft = false;
                if (unstaged.Count == 0 && _stagedPane.Rows.Count == 0)
                {
                    Close();
                }
            }
        },
            "reloading the working directory");
    }

    // After a partial stage / unstage the file is normally still listed, but the
    // fresh ItemsSource has dropped the selection and with it the diff. Put the user
    // back on the same row so a second hunk can be staged straight away; if the file
    // has left that list (everything staged), fall back to the other side.
    private void RestoreDiffSelection()
    {
        string? path = _reselectPath;
        _reselectPath = null;
        if (path is null)
        {
            // Nothing to go back to — the first fill. Upstream lands on a file
            // (FormCommit's lists select an item as soon as they have one), so the diff
            // panel shows something instead of staying empty until the user clicks.
            SelectFirstFile();
            return;
        }

        foreach (ListBox list in _reselectStaged
            ? new[] { _stagedList, _unstagedList }
            : [_unstagedList, _stagedList])
        {
            WorkingDirFileRow? row = list.Items
                .OfType<WorkingDirFileRow>()
                .FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.Ordinal));
            if (row is not null)
            {
                list.SelectedItem = row;
                return;
            }
        }

        ClearDiff();
    }

    // Puts the selection on the first file of the unstaged list, or of the staged one
    // when everything is already staged. A no-op once something is selected, so a
    // refresh cannot steal the user's place.
    private void SelectFirstFile()
    {
        if (_unstagedList.SelectedItem is not null || _stagedList.SelectedItem is not null)
        {
            return;
        }

        ListBox list = _unstagedPane.Rows.Count > 0 ? _unstagedList : _stagedList;
        if (list.Items.OfType<WorkingDirFileRow>().FirstOrDefault() is { } first)
        {
            list.SelectedItem = first;
        }
    }

    // Records the merge state and, while a merge is pending, seeds the message box
    // with MERGE_MSG — but never on top of text the user typed or edited.
    private void ApplyMergeState(ReloadSnapshot snapshot)
    {
        _mergeInProgress = snapshot.Merging;
        if (!snapshot.Merging)
        {
            _prefilledMergeMessage = string.Empty;
            return;
        }

        string suggested = snapshot.MergeMessage.TrimEnd();
        string current = _messageBox.Text ?? string.Empty;
        if (suggested.Length > 0
            && (current.Trim().Length == 0 || current == _prefilledMergeMessage))
        {
            _prefilledMergeMessage = suggested;
            _messageBox.Text = suggested;
        }
    }

    // The last action message. It is kept across refreshes so the outcome of
    // stash / create-branch / commit is not wiped out by Reload's "Staged x/y".
    private string _statusHint = string.Empty;

    private void SetStatus(string text)
    {
        _statusHint = text ?? string.Empty;
        RenderStatus();
    }

    private void RenderStatus()
    {
        // "Staged x/y" now lives in the status bar, where upstream keeps it; this line
        // is the action log plus the two repository states that have no upstream slot.
        List<string> parts = [];
        if (_statusHint.Length > 0)
        {
            parts.Add(_statusHint);
        }

        if (_conflictPaths.Count > 0)
        {
            parts.Add(string.Format(T("{0} conflict(s)"), _conflictPaths.Count));
        }

        if (_mergeInProgress)
        {
            parts.Add(T("merge in progress"));
        }

        _statusText.Text = string.Join("   —   ", parts);
        RenderStatusBar();

        // Upstream enables the two reset buttons only when there is something to
        // reset: "Reset unstaged changes" on a non-empty work-tree list
        // (FormCommit.cs:831), "Reset all changes" on either list (:2806). A live
        // button that can only ever say "nothing to do" is worse than a dead one.
        int unstagedCount = _unstagedPane.Rows.Count;
        int stagedCount = _stagedPane.Rows.Count;
        _resetUnstagedBtn.IsEnabled = unstagedCount > 0;
        _resetAllBtn.IsEnabled = unstagedCount > 0 || stagedCount > 0;
    }

    // '_' in a menu header is an access-key marker in Avalonia; double it to show it.
    private static string Escape(string text) => text.Replace("_", "__");

    /// <summary>
    ///  The LAST line that carries text. Used for streamed output, whose first lines
    ///  are the echoed command header ("Command to be executed:") — the reason a
    ///  failure is worth reporting is always at the end (git's error, or the last
    ///  thing a refusing hook printed).
    /// </summary>
    private static string LastLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        string[] lines = s.Replace("\r\n", "\n").Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0)
            {
                return lines[i].Trim();
            }
        }

        return string.Empty;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        // git often starts its output with a blank line (or with the hook's own
        // output), so return the first line that actually carries text.
        foreach (string line in s.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                return line.Trim();
            }
        }

        return string.Empty;
    }

    // ---------- ui helpers ----------

    // A property, not a field: a static field initialiser can run before the font
    // manager exists, which would cache the fallback for the life of the process.
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

    // The pitch the diff panel and its gutter BOTH lay out at. It has to be stated:
    // the two controls compute different default line heights for the same font.
    private const double DiffLineHeight = 19;

    // The status colours of FileStatusListView, so the two changed-file lists of the
    // app read the same. Upstream draws a per-status ICON here; the port has no such
    // icon set, so it uses the letter the same way its own diff list already does.
    private static readonly IBrush ModifiedGlyph = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xD6));
    private static readonly IBrush AddedGlyph = new SolidColorBrush(Color.FromRgb(0x6A, 0xC7, 0x76));
    private static readonly IBrush DeletedGlyph = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));
    private static readonly IBrush ConflictGlyph = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));

    // Upstream's NoFiles label sits at the TOP LEFT of the empty list, not in its middle.
    private static TextBlock MakeEmptyLabel() => new()
    {
        FontStyle = FontStyle.Italic,
        Foreground = Brush("App.TextDim", Brushes.Gray),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(8, 4, 0, 0),
        IsHitTestVisible = false,
    };

    private static ListBox MakeList()
    {
        ListBox list = new()
        {
            // Multiple, so stage / unstage / discard / copy path can act on a set of
            // files (the former working-directory panel offered "Discard changes (N files)").
            SelectionMode = SelectionMode.Multiple,
            FontFamily = Monospace,
            ClipToBounds = true,

            // Fluent's own ListBox background is #2B2B2B, a grey that belongs to no
            // palette of ours: the two file lists were the only surfaces in the dialog
            // not on the ramp, which read as two pale boxes. Every other list in the
            // port names its surface; these two now do too.
            Background = Brush("App.Panel", Brushes.Black),

            // No recycling: the rows are built by hand rather than bound, which is what
            // FileStatusListView does for the same reason.
            ItemTemplate = new FuncDataTemplate<object>((item, _) => BuildFileRow(item), supportsRecycling: false),
        };

        // Upstream's rows are one line of a list view, ~18 px tall. Fluent's default
        // ListBoxItem padding made them twice that, so eight changed files filled the
        // whole pane where upstream shows twenty.
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 1, 8, 1)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
            },
        });
        return list;
    }

    // A changed-file row: the coloured status letter, then the path — the shape
    // FileStatusListView already uses. The word ("new", "modified", …) that the port
    // used to print in front of every path is not what upstream shows.
    private static Control? BuildFileRow(object? item)
    {
        if (item is GroupHeader header)
        {
            StackPanel group = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(header.Level * 12, 0, 0, 0),
            };
            group.Children.Add(new TextBlock
            {
                Text = header.Collapsed ? "▸" : "▾",
                Foreground = Brush("App.TextDim", Brushes.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            });
            group.Children.Add(new TextBlock
            {
                Text = header.Text,
                FontWeight = FontWeight.Bold,
                Foreground = Brush("App.Text", Brushes.Gainsboro),
                VerticalAlignment = VerticalAlignment.Center,
            });
            group.Children.Add(new TextBlock
            {
                Text = "(" + header.Count.ToString(CultureInfo.InvariantCulture) + ")",
                Foreground = Brush("App.TextDim", Brushes.Gray),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return group;
        }

        if (item is not WorkingDirFileRow row)
        {
            return null;
        }

        (string icon, string glyph, IBrush brush) = row.Status switch
        {
            "new" => ("FileStatusAdded", "A", AddedGlyph),
            "deleted" => ("FileStatusRemoved", "D", DeletedGlyph),
            "renamed" => ("FileStatusRenamed", "R", ModifiedGlyph),
            "copied" => ("FileStatusCopied", "C", ModifiedGlyph),
            "unmerged" => ("FileStatusUnknown", "U", ConflictGlyph),
            _ when row.Status.StartsWith('U') => ("FileStatusUnknown", "U", ConflictGlyph),
            _ => ("FileStatusModified", "M", ModifiedGlyph),
        };

        RowLayouts.TryGetValue(row, out RowLayout? layout);
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness((layout?.Indent ?? 0) * 12, 0, 0, 0),
        };

        // Upstream's FileStatusList draws the status ICON of the file (the green plus,
        // the pencil, the red minus); the coloured letter is what the port falls back to
        // when the asset does not resolve, and is also what its own diff list uses.
        if (Theming.IconLoader.Image(icon, 16) is { } image)
        {
            image.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(image);
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = glyph,
                Foreground = brush,
                FontFamily = Monospace,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // Upstream prints the directory dim and the file name in full colour, which is
        // what makes a list of long paths scannable at all. Under a group header the
        // folder is already on the header, so only the name is left.
        int cut = layout?.NameOnly == true ? row.Path.LastIndexOf('/') + 1 : 0;
        string text = row.Path[cut..];
        cut = layout?.NameOnly == true ? 0 : row.Path.LastIndexOf('/') + 1;
        TextBlock block = new()
        {
            FontFamily = Monospace,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        if (cut > 0)
        {
            block.Inlines?.Add(new Run(text[..cut]) { Foreground = Brush("App.TextDim", Brushes.Gray) });
        }

        block.Inlines?.Add(new Run(text[cut..]));
        panel.Children.Add(block);
        return panel;
    }

    // One file list under its toolbars. Upstream has no caption above either list — the
    // window title says what is being committed and the toolbars say the rest
    // (FormCommit.Designer.cs: Unstaged and Staged are docked straight under their
    // ToolStrips) — so the port's two bold labels are gone with the rest of M98.
    // A list with its "nothing here" line on top of it.
    private static Control Overlay(Control list, Control label)
    {
        Grid grid = new();
        grid.Children.Add(list);
        grid.Children.Add(label);
        return grid;
    }

    private Control WrapWithToolbars(Control content, int row, params Control[] toolbars)
    {
        DockPanel panel = new();
        foreach (Control toolbar in toolbars)
        {
            DockPanel.SetDock(toolbar, Dock.Top);
            panel.Children.Add(toolbar);
        }

        // The pane is a SURFACE: App.Panel says where it starts and ends in the modern
        // style, and the 1px box comes back only in the classic one (see
        // StyleDensity.PaneOutline).
        panel.Children.Add(new Border
        {
            Child = content,
            Background = Brush("App.Panel", Brushes.Black),
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = StyleDensity.PaneOutline,
            ClipToBounds = true,
        });
        Grid.SetRow(panel, row);
        return panel;
    }

    // ---------- per-list toolbar (upstream's FileStatusList.Toolbar) ----------

    /// <summary>
    ///  The toolbar above one file list. Only entries the port can really carry out are
    ///  here: the sort key, a refresh, the untracked toggle (unstaged list only) and the
    ///  regular-expression filter box with its match counter. What upstream also has and
    ///  the port deliberately does NOT show is listed in <c>NOTES.md</c>.
    /// </summary>
    private Control BuildPaneToolbar(FileListPane pane)
    {
        // Upstream's FileStatusList toolbar, in its order: collapse groups, refresh, the
        // flat/tree split button, the three grouping toggles, then the settings menu.
        pane.CollapseButton = IconButton("CollapseAll", "⊟", () => ToggleAllGroups(pane));
        pane.RefreshButton = IconButton("ReloadRevisions", "⟳", Reload);
        pane.AsTreeButton = IconButton("FileTree", "☰", () => SetGrouping(pane, pane.Group, !pane.AsTree));
        pane.GroupMenuButton = IconButton(null, "▾", () => ShowGroupMenu(pane));
        pane.ByPathButton = GroupToggle(pane, FileSortMode.Path, "FolderClosed", "/");
        pane.ByExtensionButton = GroupToggle(pane, FileSortMode.Extension, "File", ".*");
        pane.ByStatusButton = GroupToggle(pane, FileSortMode.Status, "FileStatusModified", "M");
        if (!pane.Staged)
        {
            pane.SettingsButton = IconButton("Settings", "⚙", () => ShowPaneSettingsMenu(pane));
        }

        pane.FilterBox.TextChanged += (_, _) =>
        {
            // Restart the window on every keystroke: upstream throttles the same way,
            // so a regex is compiled once per pause and not once per character.
            pane.Timer.Stop();
            pane.Timer.Start();
        };
        pane.Timer.Tick += (_, _) =>
        {
            pane.Timer.Stop();
            ApplyPaneFilter(pane);
        };

        pane.CountBox = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(2, 0),
            Child = pane.CountText,
        };

        // Two rows, the way upstream stacks them: the ToolStrip of icon buttons, and
        // under it the selection filter across the FULL width of the pane. The port used
        // to put both on one line, which left the filter box a stub next to the buttons.
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        buttons.Children.Add(pane.CollapseButton);
        buttons.Children.Add(pane.RefreshButton);
        buttons.Children.Add(pane.AsTreeButton);
        buttons.Children.Add(pane.GroupMenuButton);
        buttons.Children.Add(pane.ByPathButton);
        buttons.Children.Add(pane.ByExtensionButton);
        buttons.Children.Add(pane.ByStatusButton);
        if (pane.SettingsButton is not null)
        {
            buttons.Children.Add(pane.SettingsButton);
        }

        RestoreGrouping(pane);
        UpdateGroupButtons(pane);

        DockPanel toolbarRow = new() { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(buttons, Dock.Left);
        DockPanel.SetDock(pane.CountBox, Dock.Right);
        toolbarRow.Children.Add(buttons);
        toolbarRow.Children.Add(pane.CountBox);
        toolbarRow.Children.Add(new Panel());

        pane.HistoryButton = IconButton(null, "▾", () => ShowFilterHistory(pane));
        pane.HistoryButton.Margin = new Thickness(2, 0, 0, 0);
        ToolTip.SetTip(pane.HistoryButton, T("Previously used filters"));

        DockPanel filterRow = new() { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(pane.HistoryButton, Dock.Right);
        filterRow.Children.Add(pane.HistoryButton);
        filterRow.Children.Add(pane.FilterBox);
        pane.FilterRow = filterRow;

        StackPanel stack = new() { Orientation = Orientation.Vertical };
        stack.Children.Add(toolbarRow);
        stack.Children.Add(filterRow);
        return stack;
    }

    // The drop-down of the filter box: the patterns already used in this pane, newest
    // first, plus a way to clear the box. Upstream gets this for free from its
    // ToolStripComboBox; here the list is kept by hand in the pane.
    private void ShowFilterHistory(FileListPane pane)
    {
        MenuFlyout flyout = new();
        if (pane.History.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = T("No filters used yet"), IsEnabled = false });
        }

        foreach (string pattern in pane.History)
        {
            string captured = pattern;
            MenuItem item = new() { Header = Escape(captured) };
            item.Click += (_, _) => pane.FilterBox.Text = captured;
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        MenuItem clear = new() { Header = T("FileStatusList/tsmiClearFilter.Text", "Clear filter") };
        clear.Click += (_, _) => pane.FilterBox.Text = string.Empty;
        flyout.Items.Add(clear);
        flyout.ShowAt(pane.HistoryButton);
    }

    // Records a pattern that really filtered something, so the drop-down offers it again.
    private static void RememberFilter(FileListPane pane)
    {
        if (pane.Pattern.Length == 0)
        {
            return;
        }

        pane.History.Remove(pane.Pattern);
        pane.History.Insert(0, pane.Pattern);
        if (pane.History.Count > 10)
        {
            pane.History.RemoveAt(pane.History.Count - 1);
        }
    }

    // A commit-column button's face: upstream anchors the image to the LEFT edge
    // (ImageAlign = MiddleLeft) and centres the caption in what is left, so the five
    // buttons read as a column with a gutter of icons.
    private static object ButtonFace(string icon, string caption)
    {
        if (Theming.IconLoader.Image(icon) is not { } image)
        {
            return caption;
        }

        image.VerticalAlignment = VerticalAlignment.Center;
        image.Margin = new Thickness(0, 0, 6, 0);
        DockPanel face = new();
        DockPanel.SetDock(image, Dock.Left);
        face.Children.Add(image);
        face.Children.Add(new TextBlock
        {
            Text = caption,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return face;
    }

    // The icon on its own, falling back to the caption when the asset does not resolve —
    // a button with an empty content would be an invisible click target.
    private static object IconOnly(string icon, string caption)
        => Theming.IconLoader.Image(icon) is { } image ? image : caption;

    // A small icon-only toolbar button. The glyph is the fallback: IconLoader returns
    // null for a name that does not resolve (the asset names are case-sensitive), and a
    // button with no content at all would be an invisible click target.
    private Button IconButton(string? icon, string glyph, Action onClick)
    {
        Button b = new()
        {
            Padding = StyleDensity.BarButton,
            Margin = new Thickness(0, 0, 2, 0),

            // Flat like the main toolbar's buttons, not framed like Fluent's default:
            // a pane toolbar of six outlined boxes is six borders the eye has to sort
            // through before it reaches the file list (see Theming/BarButtonStyles).
            Background = Brushes.Transparent,
            Content = (icon is null ? null : (Control?)Theming.IconLoader.Image(icon))
                ?? new TextBlock { Text = glyph, Foreground = Brush("App.Foreground", Brushes.Gainsboro) },
        };
        b.Classes.Add(Theming.BarButtonStyles.Class);
        b.Click += (_, _) => onClick();
        return b;
    }

    // The sort menu. Items are added BEFORE ShowAt (HANDOFF §3) and each entry really
    // re-orders the rows on screen.
    // One grouping toggle. Clicking the active one turns grouping OFF, which is how
    // upstream's checkable btnByPath / btnByExtension / btnByStatus behave.
    private ToggleButton GroupToggle(FileListPane pane, FileSortMode mode, string icon, string glyph)
    {
        ToggleButton button = new()
        {
            Padding = StyleDensity.BarButton,
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            Content = (Control?)Theming.IconLoader.Image(icon)
                ?? new TextBlock { Text = glyph, Foreground = Brush("App.Foreground", Brushes.Gainsboro) },
        };
        button.Classes.Add(Theming.BarButtonStyles.Class);
        button.Click += (_, _) => SetGrouping(pane, pane.Group == mode ? null : mode, pane.AsTree);
        return button;
    }

    /// <summary>
    ///  The ONE way this dialog's grouping changes: the pane's choice, the toolbar's
    ///  look, the rebuilt items and the remembered preference, in that order.
    ///
    ///  <para>The flat/tree entry point used to only assign the field — no button
    ///  update and no rebuild — so the ☰ button visibly did nothing until something
    ///  else happened to re-group the list (M226).</para>
    /// </summary>
    private void SetGrouping(FileListPane pane, FileSortMode? group, bool asTree)
    {
        pane.Group = group;
        pane.AsTree = asTree;
        RememberGrouping(pane);
        UpdateGroupButtons(pane);
        RegroupPane(pane);
    }

    // Upstream's btnAsTree drop-down: the three groupings, each as tree or flat.
    private void ShowGroupMenu(FileListPane pane)
    {
        MenuFlyout flyout = new();
        Add(FileSortMode.Path, asTree: true, T("FileStatusList/tsmiGroupByFilePathTree.Text", "Group by file path - tree"));
        Add(FileSortMode.Path, asTree: false, T("FileStatusList/tsmiGroupByFilePathFlat.Text", "Group by file path - flat"));
        Add(FileSortMode.Extension, asTree: true, T("FileStatusList/tsmiGroupByFileExtensionTree.Text", "Group by file extension - tree"));
        Add(FileSortMode.Status, asTree: true, T("FileStatusList/tsmiGroupByFileStatusTree.Text", "Group by diff status - tree"));

        flyout.Items.Add(new Separator());
        MenuItem none = new() { Header = (pane.Group is null ? "●  " : "○  ") + T("No grouping") };
        none.Click += (_, _) => SetGrouping(pane, null, pane.AsTree);
        flyout.Items.Add(none);
        flyout.ShowAt(pane.GroupMenuButton);

        void Add(FileSortMode mode, bool asTree, string caption)
        {
            bool active = pane.Group == mode && (mode != FileSortMode.Path || pane.AsTree == asTree);
            MenuItem item = new() { Header = (active ? "●  " : "○  ") + caption };
            item.Click += (_, _) => SetGrouping(pane, mode, asTree);
            flyout.Items.Add(item);
        }
    }

    // ---------- the remembered grouping (view-prefs.json, FileListPrefs) ----------
    //
    // Upstream keeps ONE grouping for every file list in the application
    // (AppSettings.DiffListSorting, broadcast by DiffListSortService); this dialog's
    // two lists have a toolbar each and have always been able to disagree, so each
    // remembers its own. The reasoning is written out on Services.FileListPrefs.

    private static readonly Services.ViewPrefsService GroupingPrefs = new();

    private static void RestoreGrouping(FileListPane pane)
    {
        Services.FileListPrefs prefs = GroupingPrefs.Load().FileList;
        Services.FileListGrouping stored = pane.Staged ? prefs.CommitStaged : prefs.CommitUnstaged;
        pane.Group = ToSortMode(stored.Group);
        pane.AsTree = stored.AsTree;
    }

    private static void RememberGrouping(FileListPane pane)
    {
        // Built BEFORE the call, out of the pane's state as it is now: Update's
        // delegate may run later, on another thread, and more than once.
        Services.FileListGrouping grouping = new()
        {
            Group = ToGroupMode(pane.Group),
            AsTree = pane.AsTree,
        };

        bool staged = pane.Staged;
        GroupingPrefs.Update(p =>
        {
            if (staged)
            {
                p.FileList.CommitStaged = grouping;
            }
            else
            {
                p.FileList.CommitUnstaged = grouping;
            }
        });
    }

    // This dialog predates DiffFileGroupMode and carries its own enum, whose "no
    // grouping" is a null rather than a member. The stored shape is the shared one,
    // so the two meet here and nowhere else.
    private static FileSortMode? ToSortMode(DiffFileGroupMode mode) => mode switch
    {
        DiffFileGroupMode.Path => FileSortMode.Path,
        DiffFileGroupMode.Extension => FileSortMode.Extension,
        DiffFileGroupMode.Status => FileSortMode.Status,
        _ => null,
    };

    private static DiffFileGroupMode ToGroupMode(FileSortMode? mode) => mode switch
    {
        FileSortMode.Path => DiffFileGroupMode.Path,
        FileSortMode.Extension => DiffFileGroupMode.Extension,
        FileSortMode.Status => DiffFileGroupMode.Status,
        _ => DiffFileGroupMode.None,
    };

    // ---------------- the folder menu (right-click on a group header) ----------------

    /// <summary>
    ///  Every row under <paramref name="header"/>, read from the pane's own rows and not
    ///  from the list: a folded folder shows none of its files, and it is precisely a
    ///  folded folder that this menu is most useful on.
    ///
    ///  <para>For the path TREE the header's key is the directory with its separator, so
    ///  a prefix test takes the whole subtree — which is what "this folder" means to
    ///  someone looking at it. The other groupings have one flat bucket per key, and
    ///  that key comes from <see cref="GroupKey"/>, the same function the list was built
    ///  with.</para>
    /// </summary>
    private static List<WorkingDirFileRow> RowsInGroup(FileListPane pane, GroupHeader header)
    {
        if (pane.Group is not { } group)
        {
            return [];
        }

        return group == FileSortMode.Path && pane.AsTree
            ? [.. pane.Rows.Where(r => r.Path.StartsWith(header.Key, StringComparison.OrdinalIgnoreCase))]
            : [.. pane.Rows.Where(r => string.Equals(
                GroupKey(group, r), header.Key, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    ///  The menu of a whole group: the folder-wide versions of what the file menu offers
    ///  one file at a time, plus the three tree entries.
    ///
    ///  <para><b>What upstream has here.</b> Its own folder menu
    ///  (<c>FileStatusList.TreeContextMenu.cs</c>) offers <c>Select all</c>,
    ///  <c>Collapse all</c>, <c>Expand all</c> and <c>Collapse root folders</c> — and no
    ///  git action at all: there, acting on a folder means "select all" first and then
    ///  the file menu. Those four entries are ported; staging, discarding and stashing a
    ///  folder in one gesture are this port's, asked for by use.</para>
    /// </summary>
    private void ShowFolderMenu(FileListPane pane, GroupHeader header, Control anchor)
    {
        List<WorkingDirFileRow> rows = RowsInGroup(pane, header);
        if (rows.Count == 0)
        {
            return;
        }

        string what = header.Text;
        MenuFlyout flyout = new() { Placement = PlacementMode.Pointer };

        if (pane.Staged)
        {
            Add(WithCount(UnstageCaption, rows.Count), true, () => RunGit(() => _service.Unstage(_repoPath, rows)));
        }
        else
        {
            // Conflicted paths are left out the way StageSelected leaves them out: a
            // resolution is a decision per file, never something a folder-wide gesture
            // should make.
            List<WorkingDirFileRow> stageable = [.. rows.Where(r => !_conflictPaths.Contains(r.Path))];
            Add(
                WithCount(StageCaption, stageable.Count),
                stageable.Count > 0,
                () => RunGit(() => _service.Stage(_repoPath, stageable)));

            List<string> discardable = [.. rows
                .Where(r => r.Status != "new" && !_conflictPaths.Contains(r.Path))
                .Select(r => r.Path)];
            Add(WithCount(DiscardCaption, discardable.Count), discardable.Count > 0,
                () => DiscardPaths(discardable, describe: what));
        }

        // Stash: the entry this menu was asked for. Disabled while the repository has an
        // unresolved merge, because git refuses the whole command then — measured, and
        // for ANY pathspec, not only one that touches the conflict (see
        // CommitActionsService.StashPaths). An entry that can only fail is worse than one
        // that says why it is out of reach.
        Add(
            TFormat(null, "Stash {0} ({1})", what, rows.Count),
            _conflictPaths.Count == 0,
            () => StashFolder(rows, what));

        flyout.Items.Add(new Separator());

        // Upstream's three, in its order. "Select all" hands the folder to the file menu,
        // which is upstream's whole answer to acting on a folder — so it also has to
        // unfold it: a hidden row cannot be selected.
        Add(T("FileStatusList/_selectAll.Text", "Select all"), true, () => SelectFolder(pane, header));
        Add(T("FileStatusList/_collapseAll.Text", "Collapse all"), true, () =>
        {
            foreach (GroupHeader all in AllHeaders(pane))
            {
                pane.Collapsed.Add(all.Key);
            }

            RegroupPane(pane);
        });
        Add(T("FileStatusList/_expandAll.Text", "Expand all"), pane.Collapsed.Count > 0, () =>
        {
            pane.Collapsed.Clear();
            RegroupPane(pane);
        });

        flyout.Items.Add(new Separator());
        Add(WithCount(CopyPathCaption, rows.Count), true, () => CopyFolderPaths(rows));

        flyout.ShowAt(anchor, showAtPointer: true);

        void Add(string caption, bool enabled, Action run)
        {
            MenuItem item = new() { Header = Escape(caption), IsEnabled = enabled };
            item.Click += (_, _) => run();
            flyout.Items.Add(item);
        }
    }

    // `git stash push -- <the folder's files>`. The message follows what the staged-stash
    // entry does: the commit message's first line when there is one, so the entry is
    // recognisable in the stash list, otherwise the folder's name.
    private void StashFolder(List<WorkingDirFileRow> rows, string what)
    {
        string typed = (_messageBox.Text ?? string.Empty).Trim();
        string message = typed.Length > 0 ? FirstLine(typed) : string.Format(T("Changes in {0}"), what);

        // -u is mandatory once an untracked file is in the set: without it git fails the
        // whole command over the untracked path and stashes nothing (see StashPaths).
        bool untracked = rows.Any(IsUntrackedRow);
        List<string> paths = [.. rows.Select(r => r.Path)];
        string repo = _repoPath;

        SetStatus(string.Format(T("Running {0} …"), "git stash push"));
        RunActionResult(
            () => _actions.StashPaths(repo, message, paths, untracked),
            result =>
            {
                SetStatus(result.Success
                    ? string.Format(T("Stashed {0}: {1}"), what, message)
                    : string.Format(T("Stash failed: {0}"), FirstLine(result.Output)));
                Reload();
            });
    }

    // Unfolds the group and selects its files, so the file menu can act on them — the
    // gesture upstream offers instead of folder-wide actions.
    private void SelectFolder(FileListPane pane, GroupHeader header)
    {
        pane.Collapsed.RemoveWhere(key => key.StartsWith(header.Key, StringComparison.OrdinalIgnoreCase));
        pane.Collapsed.Remove(header.Key);
        RegroupPane(pane);

        // The pane's rows, not the list's items — and they can all be selected because
        // the subtree was just unfolded, so every one of them is on screen. The item
        // instances ARE these rows (BuildItems puts the same references in), which is
        // what makes selecting them by object work.
        pane.List.SelectedItems?.Clear();
        foreach (WorkingDirFileRow row in RowsInGroup(pane, header))
        {
            pane.List.SelectedItems?.Add(row);
        }
    }

    private void CopyFolderPaths(List<WorkingDirFileRow> rows)
    {
        string text = string.Join(Environment.NewLine, rows.Select(r => r.Path));
        PutOnClipboard(text, rows.Count);
    }

    private static void UpdateGroupButtons(FileListPane pane)
    {
        pane.ByPathButton.IsChecked = pane.Group == FileSortMode.Path;
        pane.ByExtensionButton.IsChecked = pane.Group == FileSortMode.Extension;
        pane.ByStatusButton.IsChecked = pane.Group == FileSortMode.Status;

        // Nothing to collapse without groups, which is why upstream keeps the button
        // hidden until its list has some.
        pane.CollapseButton.IsVisible = pane.Group is not null;
        pane.AsTreeButton.IsVisible = pane.Group == FileSortMode.Path;
    }

    // Collapse-all, or expand-all when everything is already collapsed — upstream's
    // btnCollapseGroups, whose tooltip says exactly that.
    private void ToggleAllGroups(FileListPane pane)
    {
        // Every header the grouping WOULD produce, not the ones on screen: a header
        // nested inside a folded folder is still one this button has to fold.
        List<GroupHeader> headers = [.. AllHeaders(pane)];
        if (headers.Count == 0)
        {
            // Nothing is grouped (or the pane is empty), so there is nothing to fold;
            // clearing the set is the only useful thing left to do.
            pane.Collapsed.Clear();
            RegroupPane(pane);
            return;
        }

        bool anyOpen = headers.Any(h => !pane.Collapsed.Contains(h.Key));
        if (anyOpen)
        {
            foreach (GroupHeader header in headers)
            {
                pane.Collapsed.Add(header.Key);
            }
        }
        else
        {
            pane.Collapsed.Clear();
        }

        RegroupPane(pane);
    }

    // Every header the pane WOULD show if nothing were collapsed.
    private static IEnumerable<GroupHeader> AllHeaders(FileListPane pane)
        => BuildItems(pane, pane.Rows, ignoreCollapsed: true).OfType<GroupHeader>();

    // Upstream's per-list settings dropdown, reduced to the one toggle the port can
    // honour on the work-tree list.
    private void ShowPaneSettingsMenu(FileListPane pane)
    {
        MenuFlyout flyout = new();
        MenuItem untracked = new()
        {
            Header = (_showUntracked ? "☑  " : "☐  ")
                + T("FileStatusList/tsmiShowUntrackedFiles.Text", "Show untracked files"),
        };
        untracked.Click += (_, _) =>
        {
            _showUntracked = !_showUntracked;
            Reload();
        };
        flyout.Items.Add(untracked);
        flyout.ShowAt(pane.SettingsButton!);
    }

    /// <summary>
    ///  Re-orders the rows a pane is showing. A NEW list instance is assigned: handing
    ///  the same instance back to <c>ItemsSource</c> leaves the realised containers
    ///  untouched and the visible rows keep their old visuals (HANDOFF §3 / M50).
    /// </summary>
    /// <summary>
    ///  The one way a pane is given rows: the pane RECORDS them
    ///  (<see cref="FileListPane.Rows"/>) and the list shows whatever the current
    ///  grouping makes of them. Assigning <c>ItemsSource</c> without recording the rows
    ///  is what M226 was.
    /// </summary>
    private static void SetPaneRows(FileListPane pane, IEnumerable<WorkingDirFileRow> rows)
    {
        pane.Rows = [.. rows];
        pane.List.ItemsSource = BuildItems(pane, pane.Rows);
    }

    // Rebuilds one pane's items from the rows it already holds, keeping the selection.
    private void RegroupPane(FileListPane pane)
    {
        List<WorkingDirFileRow> selected = SelectedRows(pane.List);
        pane.List.ItemsSource = BuildItems(pane, pane.Rows);
        foreach (WorkingDirFileRow row in selected)
        {
            if (pane.List.Items.OfType<WorkingDirFileRow>()
                .FirstOrDefault(r => string.Equals(r.Path, row.Path, StringComparison.Ordinal)) is { } again)
            {
                pane.List.SelectedItems?.Add(again);
            }
        }

        RefreshPaneCount(pane);
    }

    // Which group a row falls in, for every grouping but the path TREE (whose keys are
    // built one directory level at a time in AddFolder below). A method of the class and
    // not a local of BuildItems, because the folder menu has to answer the same question
    // in reverse — which rows are under this header — and two spellings of one grouping
    // rule is how a menu ends up acting on a set the list never showed.
    private static string GroupKey(FileSortMode group, WorkingDirFileRow row) => group switch
    {
        FileSortMode.Extension => System.IO.Path.GetExtension(row.Path) is { Length: > 0 } ext
            ? ext
            : "(none)",
        FileSortMode.Status => row.Status,
        _ => row.Path.LastIndexOf('/') > 0 ? row.Path[..row.Path.LastIndexOf('/')] : "(root)",
    };

    // The items of one list: the rows alone when nothing is grouped, otherwise group
    // headers with their rows under them — a real folder tree for the path grouping,
    // one header per key for the other two. A collapsed header keeps its subtree out.
    private static List<object> BuildItems(
        FileListPane pane, IEnumerable<WorkingDirFileRow> rows, bool ignoreCollapsed = false)
    {
        List<WorkingDirFileRow> sorted = [.. rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)];
        if (pane.Group is not { } group)
        {
            foreach (WorkingDirFileRow row in sorted)
            {
                RowLayouts.AddOrUpdate(row, new RowLayout(0, NameOnly: false));
            }

            return [.. sorted.Cast<object>()];
        }

        if (group == FileSortMode.Path && pane.AsTree)
        {
            List<object> tree = [];
            AddFolder(tree, sorted, prefix: string.Empty, level: 0);
            return tree;
        }

        List<object> flat = [];
        foreach (IGrouping<string, WorkingDirFileRow> bucket in sorted
            .GroupBy(r => GroupKey(group, r), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            GroupHeader header = new(
                bucket.Key, bucket.Key, 0, bucket.Count(), pane.Collapsed.Contains(bucket.Key));
            flat.Add(header);
            foreach (WorkingDirFileRow row in bucket)
            {
                RowLayouts.AddOrUpdate(row, new RowLayout(1, group == FileSortMode.Path));
            }

            if (ignoreCollapsed || !pane.Collapsed.Contains(header.Key))
            {
                flat.AddRange(bucket.Cast<object>());
            }
        }

        return flat;

        void AddFolder(List<object> into, IReadOnlyList<WorkingDirFileRow> scope, string prefix, int level)
        {
            // Files of this folder first? No: upstream puts the sub-folders first, then
            // the files, which is what a tree control does.
            foreach (IGrouping<string, WorkingDirFileRow> folder in scope
                .Where(r => r.Path.Length > prefix.Length && r.Path.IndexOf('/', prefix.Length) >= 0)
                .GroupBy(r => r.Path[prefix.Length..r.Path.IndexOf('/', prefix.Length)], StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                string key = prefix + folder.Key + "/";
                GroupHeader header = new(
                    key, folder.Key, level, folder.Count(), pane.Collapsed.Contains(key));
                into.Add(header);
                if (ignoreCollapsed || !pane.Collapsed.Contains(key))
                {
                    AddFolder(into, [.. folder], key, level + 1);
                }
            }

            foreach (WorkingDirFileRow file in scope
                .Where(r => r.Path.IndexOf('/', prefix.Length) < 0)
                .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                RowLayouts.AddOrUpdate(file, new RowLayout(level, NameOnly: true));
                into.Add(file);
            }
        }
    }

    private Button MakeButton(string text, Action onClick)
    {
        Button b = MakeButton(onClick);
        b.Classes.Add(Theming.BarButtonStyles.ActionClass);
        b.Content = text;
        return b;
    }

    // Caption-less overload: the text is applied (and re-applied on a language
    // switch) by ApplyTranslations.
    private Button MakeButton(Action onClick)
    {
        Button b = new();
        b.Click += (_, _) => onClick();
        return b;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
