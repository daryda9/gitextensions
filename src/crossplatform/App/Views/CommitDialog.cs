using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

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
public sealed class CommitDialog : Window
{
    private readonly string _repoPath;
    private readonly WorkingDirectoryService _service = new();
    private readonly CommitActionsService _actions = new();

    private readonly ListBox _unstagedList = MakeList();
    private readonly ListBox _stagedList = MakeList();
    private readonly TextBox _messageBox;
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
    private readonly MenuItem _unstagedCopyItem = new();
    private readonly MenuItem _unstageItem = new();
    private readonly MenuItem _stagedCopyItem = new();

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
    // Upstream's selectionFilter toolbar (FormCommit.Designer.cs:253-278): a regular
    // expression that SELECTS the matching unstaged files, throttled by 250 ms, with
    // the pattern error surfaced in a tooltip. The invalid-pattern outline sits on the
    // COUNTER, not on the TextBox: Fluent's focus border draws over a TextBox's own
    // border, so the red went invisible exactly while the user was typing.
    private readonly TextBox _selectionFilterBox;
    private readonly Border _selectionFilterCount;
    private readonly TextBlock _selectionFilterCountText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 4, 0),
    };

    private readonly DispatcherTimer _selectionFilterTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };

    // The last applied pattern, empty when the filter is off. Non-empty ONLY while it
    // compiles, so "filter active" and "pattern usable" are the same condition.
    private string _selectionFilter = string.Empty;

    private readonly TextBlock _unstagedHeader = MakeHeaderLabel();
    private readonly TextBlock _stagedHeader = MakeHeaderLabel();
    private readonly Button _stageBtn;
    private readonly Button _unstageBtn;
    private readonly Button _stageAllBtn;
    private readonly Button _unstageAllBtn;
    private readonly Button _commitBtn;
    private readonly Button _commitPushBtn;
    private readonly Button _stashBtn;
    private readonly Button _resetAllBtn;
    private readonly Button _resetUnstagedBtn;
    private readonly Button _templatesBtn;
    private readonly Button _createBranchBtn;
    private readonly Button _optionsBtn;

    // Upstream's Cancel button (FormCommit.Designer.cs:142-151), which is also the
    // form's CancelButton — so it doubles as the Escape handler. It only closes:
    // upstream asks nothing back, not even with a message typed.
    private readonly Button _cancelBtn;

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

        // ---- RIGHT: diff view ----
        _diffView = new SelectableTextBlock
        {
            FontFamily = Monospace,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            Margin = new Thickness(6),
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
        _unstagedList.DoubleTapped += (_, _) => OnUnstagedDoubleTapped();
        _stagedList.DoubleTapped += (_, _) => UnstageSelected();

        // The Items are static and only their IsEnabled changes while opening —
        // adding/removing entries in Opening leaves the popup unmeasured (HANDOFF §3).
        _mergetoolItem.Click += (_, _) => OpenInMergetool();
        _takeOursItem.Click += (_, _) => ResolveConflicts("ours");
        _takeTheirsItem.Click += (_, _) => ResolveConflicts("theirs");
        _markResolvedItem.Click += (_, _) => ResolveConflicts("resolved");

        _stageItem.Click += (_, _) => StageSelected();
        _unstagedCopyItem.Click += (_, _) => CopySelectedPath(_unstagedList);

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
        _stagedCopyItem.Click += (_, _) => CopySelectedPath(_stagedList);
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

        // A WrapPanel, not a horizontal StackPanel: "Stage all" / "Unstage all"
        // become "Inserisci tutto nello stage" / "Rimuovi tutto dallo stage" in
        // Italian (longer still in German) and a StackPanel simply overflowed the
        // left column, pushing the last button past the dialog border.
        WrapPanel stageButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4),
            Children = { _stageBtn, _unstageBtn, _stageAllBtn, _unstageAllBtn },
        };
        foreach (Control c in stageButtons.Children)
        {
            c.Margin = new Thickness(0, 0, 4, 4);
        }

        _selectionFilterBox = new TextBox { MinWidth = 120 };
        _selectionFilterBox.TextChanged += (_, _) =>
        {
            // Restart the window on every keystroke: upstream throttles the same way,
            // so a regex is compiled once per pause and not once per character.
            _selectionFilterTimer.Stop();
            _selectionFilterTimer.Start();
        };
        _selectionFilterTimer.Tick += (_, _) =>
        {
            _selectionFilterTimer.Stop();
            ApplySelectionFilter();
        };

        _selectionFilterCount = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(2, 0),
            Child = _selectionFilterCountText,
        };

        DockPanel filterRow = new() { Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(_selectionFilterCount, Dock.Right);
        filterRow.Children.Add(_selectionFilterCount);
        filterRow.Children.Add(_selectionFilterBox);

        Grid leftPanel = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,*"),
        };
        Grid.SetRow(filterRow, 0);
        leftPanel.Children.Add(filterRow);
        leftPanel.Children.Add(WrapWithHeader(_unstagedHeader, _unstagedList, 1));
        Grid.SetRow(stageButtons, 2);
        leftPanel.Children.Add(stageButtons);
        leftPanel.Children.Add(WrapWithHeader(_stagedHeader, _stagedList, 3));

        // ---- top region: left | right split ----
        Grid split = new()
        {
            ColumnDefinitions = new ColumnDefinitions("2*,3*"),
            Margin = new Thickness(6),
        };
        Grid.SetColumn(leftPanel, 0);
        GridSplitter splitter = new() { Width = 4, Background = Brush("App.Border", Brushes.Gray) };
        Grid.SetColumn(splitter, 1);
        splitter.HorizontalAlignment = HorizontalAlignment.Left;
        _gutterBorder = new Border
        {
            Child = _gutterScroll,
            BorderBrush = Brush("App.Border", Brushes.Gray),
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
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 0, 0),
            ClipToBounds = true,
        };
        Grid.SetColumn(diffBorder, 1);
        split.Children.Add(leftPanel);
        split.Children.Add(diffBorder);
        split.Children.Add(splitter);

        // ---- BOTTOM: message + buttons + status ----
        _messageBox = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 70,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
            FontFamily = Monospace,
        };
        _amendBox = new CheckBox { Margin = new Thickness(0, 0, 12, 0) };

        _commitBtn = MakeButton(() => DoCommit(push: false));
        _commitPushBtn = MakeButton(() => DoCommit(push: true));
        _stashBtn = MakeButton(DoStashStaged);
        _resetAllBtn = MakeButton(() => DoReset(includeStaged: true));
        _resetUnstagedBtn = MakeButton(() => DoReset(includeStaged: false));

        _templatesBtn = new Button();
        _templatesBtn.Click += async (_, _) => await ShowTemplatesMenuAsync(_templatesBtn);

        _createBranchBtn = MakeButton(PromptCreateBranch);

        _optionsBtn = new Button();
        _optionsBtn.Click += (_, _) => ShowOptionsMenu(_optionsBtn);

        _cancelBtn = MakeButton(Close);

        WrapPanel buttonRow = new()
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                _commitBtn, _commitPushBtn, _amendBox, _stashBtn,
                _resetAllBtn, _resetUnstagedBtn, _templatesBtn, _createBranchBtn, _optionsBtn,
                _cancelBtn,
            },
        };
        foreach (Control c in buttonRow.Children)
        {
            c.Margin = new Thickness(0, 0, 6, 4);
        }

        _statusText = new TextBlock
        {
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            Margin = new Thickness(0, 2, 0, 0),
        };

        StackPanel bottom = new()
        {
            Margin = new Thickness(6),
            Children = { _messageBox, buttonRow, _statusText },
        };

        _statusBar = BuildStatusBar();

        DockPanel root = new();
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(_statusBar);
        DockPanel.SetDock(_conflictBanner, Dock.Top);
        root.Children.Add(bottom);
        root.Children.Add(_conflictBanner);
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
    }

    // Persists one options-menu toggle. The whole record is rewritten, so a
    // concurrently changed sibling setting would be overwritten — the file is only
    // written from the UI thread, and only by an explicit user click.
    private void SaveOption(Action<AppPreferences> change)
    {
        change(_prefs);
        AppPreferences prefs = _prefs;
        _ = Task.Run(() => _settings.Save(prefs));
    }

    // Upstream's SelectStaged(): if nothing is selected in the staged list, put the
    // selection on its first row so the diff panel shows it. Does nothing when the
    // user has already chosen a file, and never touches ItemsSource — reassigning it
    // from a selection-driven handler is what crashed this dialog twice before.
    private void SelectStagedForMessage()
    {
        if (_stagedList.SelectedItems?.Count > 0 || _stagedList.Items.Count == 0)
        {
            return;
        }

        _stagedList.SelectedItem = _stagedList.Items[0];
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    // Every fixed caption of the dialog, in one place, so it can be applied at
    // construction time and again after a language switch. Captions that carry a
    // selection count (the context menus) are re-computed in the menus' Opening
    // handler and only get their singular form here.
    private void ApplyTranslations()
    {
        _unstagedHeader.Text = T("Unstaged changes");
        _stagedHeader.Text = T("Staged changes");

        _stageItem.Header = StageCaption;
        _unstagedCopyItem.Header = CopyPathCaption;
        _unstageItem.Header = UnstageCaption;
        _stagedCopyItem.Header = CopyPathCaption;
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

        _stageBtn.Content = StageCaption + " ▼";
        _unstageBtn.Content = UnstageCaption + " ▲";
        ApplyFilterCaptions();

        _selectionFilterBox.Watermark = T(
            "FileStatusList/cboFilterComboBox.Watermark",
            "Filter files using a regular expression...");
        ToolTip.SetTip(_selectionFilterBox, SelectionFilterTip);

        _messageBox.Watermark = T("FormCommit/_enterCommitMessageHint.Text", "Enter commit message");
        _amendBox.Content = T("FormCommit/_amendCommitCaption.Text", "Amend commit");

        _commitBtn.Content = T("FormCommit/Commit.Text", "Commit");
        _commitPushBtn.Content = T("FormCommit/_commitAndPush.Text", "Commit & push");
        _stashBtn.Content = T("FormCommit/StashStaged.Text", "Stash staged changes");
        _resetAllBtn.Content = T("FormCommit/btnResetAllChanges.Text", "Reset all changes");
        _resetUnstagedBtn.Content = T("FormCommit/btnResetUnstagedChanges.Text", "Reset unstaged changes");
        _templatesBtn.Content = T("FormCommit/commitTemplatesToolStripMenuItem.ToolTipText", "Commit templates") + " ▾";
        _createBranchBtn.Content = T("FormCommit/createBranchToolStripButton.ToolTipText", "Create branch");
        _optionsBtn.Content = T("FormCommit/tsmiOptions.Text", "Options") + " ▾";
        _cancelBtn.Content = T("FormCommit/Cancel.Text", "Cancel");

        UpdateTitle();
        RenderStatus();
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
                    DoCommit(push: false);
                    return;
                }

                // Upstream's ToggleSelectionFilter hotkey (Ctrl+F). Upstream hides and
                // shows the whole filter toolbar; here the box is always visible, so
                // the toggle is "focus it" / "clear it and hand focus back to the
                // list", which is what the hotkey is actually used for.
                if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    if (_selectionFilterBox.IsFocused)
                    {
                        _selectionFilterBox.Text = string.Empty;
                        _selectionFilterTimer.Stop();
                        ApplySelectionFilter();
                        _unstagedList.Focus();
                    }
                    else
                    {
                        _selectionFilterBox.Focus();
                        _selectionFilterBox.SelectAll();
                    }
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
        e.SaveAs.Click += (_, _) => SaveSelectedAs(list);
        e.Move.Click += (_, _) => MoveSelected(list);
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
        Control view;
        string caption;
        if (blame)
        {
            BlameView blameView = new();
            blameView.ShowBlame(_repoPath, row.Path);
            view = blameView;
            caption = string.Format(T("FormBlame/$this.Text", "Blame - {0}"), row.Path);
        }
        else
        {
            FileHistoryView history = new();
            history.ShowHistory(_repoPath, row.Path);
            view = history;
            caption = string.Format(T("FormFileHistory/$this.Text", "File History - {0}"), row.Path);
        }

        Window window = new()
        {
            Title = caption,
            Width = 900,
            Height = 600,
            Background = Brush("App.Window", Brushes.DimGray),
            Content = view,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

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
    private async void SaveSelectedAs(ListBox list)
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
    private async void MoveSelected(ListBox list)
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
        Window prompt = new()
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
        => _ = Task.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                return new ExternalToolResult(false, ex.Message);
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!t.Result.Success)
            {
                SetStatus(FirstLine(t.Result.Message));
            }
        }), TaskScheduler.Default);

    // ---------- list plumbing ----------

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

        _ = Task.Run(() =>
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
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            // A newer selection already won the race; drop this result rather than
            // letting _diffText describe a file the panel is no longer showing.
            if (token != _diffToken)
            {
                return;
            }

            (DiffLoad diff, bool failed) = t.Result;
            _diffPath = failed ? string.Empty : path;
            _diffStaged = staged;
            _diffFileIsNew = isNew;
            _diffFileIsRenamed = isRenamed;

            // The service already decides what may be cut from: an error message or a
            // truncated whole-file view carries an EMPTY source, so line staging stays
            // disabled while the text is still shown.
            RenderDiff(diff.Source, diff.Display);
        }), TaskScheduler.Default);
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

        System.Text.StringBuilder text = new();
        for (int i = 0; i < numbers.Count; i++)
        {
            (int old, int @new) = numbers[i];
            if (i > 0)
            {
                text.Append('\n');
            }

            text.Append(Cell(old, oldWidth)).Append(' ').Append(Cell(@new, newWidth));
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
        List<WorkingDirFileRow> rows = [.. Filtered(_unstagedList)];
        if (rows.Count > 0)
        {
            RunGit(() => _service.Stage(_repoPath, rows));
        }
    }

    private void UnstageAll()
    {
        List<WorkingDirFileRow> rows = [.. Filtered(_stagedList)];
        if (rows.Count > 0)
        {
            RunGit(() => _service.Unstage(_repoPath, rows));
        }
    }

    // ---------- selection filter (regex) ----------

    /// <summary>True while a usable (compiling, non-empty) pattern is applied.</summary>
    private bool IsSelectionFilterActive => _selectionFilter.Length > 0;

    private IEnumerable<WorkingDirFileRow> Filtered(ListBox list)
    {
        IEnumerable<WorkingDirFileRow> rows = list.Items.OfType<WorkingDirFileRow>();
        return IsSelectionFilterActive
            ? rows.Where(r => Regex.IsMatch(r.Path, _selectionFilter, RegexOptions.IgnoreCase))
            : rows;
    }

    // Compiles the pattern and, on success, SELECTS the matching unstaged rows the way
    // upstream's FileStatusList.SetSelectionFilter does, so the plain "Stage" button
    // acts on them. An invalid pattern leaves the previous selection alone and only
    // reports itself.
    private void ApplySelectionFilter()
    {
        string pattern = (_selectionFilterBox.Text ?? string.Empty).Trim();

        if (pattern.Length == 0)
        {
            _selectionFilter = string.Empty;
            _selectionFilterCount.BorderBrush = Brushes.Transparent;
            _selectionFilterCountText.Text = string.Empty;
            ToolTip.SetTip(_selectionFilterBox, SelectionFilterTip);
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
            _selectionFilter = string.Empty;
            _selectionFilterCount.BorderBrush = Brush("App.DiffRemoved", Brushes.OrangeRed);
            _selectionFilterCountText.Text = "!";
            ToolTip.SetTip(
                _selectionFilterBox,
                string.Format(T("FormCommit/_selectionFilterErrorToolTip.Text", "Error {0}"), ex.Message));
            ApplyFilterCaptions();
            return;
        }

        _selectionFilter = pattern;
        _selectionFilterCount.BorderBrush = Brushes.Transparent;
        ToolTip.SetTip(_selectionFilterBox, SelectionFilterTip);

        List<WorkingDirFileRow> matches = [.. Filtered(_unstagedList)];
        _unstagedList.SelectedItems?.Clear();
        foreach (WorkingDirFileRow row in matches)
        {
            _unstagedList.SelectedItems?.Add(row);
        }

        _selectionFilterCountText.Text = string.Format(
            "{0}/{1}",
            matches.Count,
            _unstagedList.Items.Count);
        ApplyFilterCaptions();
    }

    // The two "all" buttons say what they will actually do. Upstream re-captions
    // "Stage all" from the unstaged filter and "Unstage all" from the staged list's own
    // filter widget; the port's dialog has a single filter box, so the one pattern
    // drives both sides.
    private void ApplyFilterCaptions()
    {
        _stageAllBtn.Content = IsSelectionFilterActive
            ? T("FormCommit/_stageFiltered.Text", "Stage filtered")
            : T("FormCommit/_stageAll.Text", "Stage all");
        _unstageAllBtn.Content = IsSelectionFilterActive
            ? T("FormCommit/_unstageFiltered.Text", "Unstage filtered")
            : T("FormCommit/_unstageAll.Text", "Unstage all");
    }

    private void RefreshSelectionFilterCount()
    {
        if (!IsSelectionFilterActive)
        {
            return;
        }

        _selectionFilterCountText.Text = string.Format(
            "{0}/{1}",
            Filtered(_unstagedList).Count(),
            _unstagedList.Items.Count);
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
        if (paths.Count == 0)
        {
            return;
        }

        string repo = _repoPath;
        string what = paths.Count == 1
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

    // Copies the selected file's repo-relative path to the clipboard. Nothing else
    // depends on it, so a missing clipboard (headless) is silently ignored.
    private void CopySelectedPath(ListBox list)
    {
        List<WorkingDirFileRow> rows = SelectedRows(list);
        if (rows.Count == 0)
        {
            return;
        }

        // One path per line, like the original's multi-file "Copy path".
        string text = string.Join("\n", rows.Select(r => r.Path));
        try
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
            SetStatus(rows.Count == 1
                ? string.Format(T("Copied path: {0}"), rows[0].Path)
                : string.Format(T("Copied {0} paths."), rows.Count));
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
    private async void DoCommit(bool push)
    {
        int staged = _stagedList.Items.Count;
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
            PromptCreateBranch();
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
    private async void DoReset(bool includeStaged)
    {
        // Upstream sizes the question from the WORK-TREE list only (it is the one
        // passed to StartResetChangesDialog), because that is where untracked files
        // can appear at all: an index entry is by definition tracked.
        List<WorkingDirFileRow> unstagedRows = [.. _unstagedList.Items.OfType<WorkingDirFileRow>()];
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
            foreach (WorkingDirFileRow row in _stagedList.Items.OfType<WorkingDirFileRow>())
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
        if (_stagedList.Items.Count == 0)
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
        _ = Task.Run(() => CommitActionsService.ReadTemplate(template))
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                _messageBox.Text = t.Result;
                _messageBox.Focus();
                SetStatus(string.Format(T("Applied commit template {0}."), template.Name));
            }), TaskScheduler.Default);
    }

    // ---------- create branch ----------

    // Prompts for a name, validates it with `git check-ref-format --branch` (plus a
    // duplicate check), then runs `git checkout -b <name> HEAD`, carrying the staged
    // and unstaged changes over to the new branch, exactly like the original form.
    private async void PromptCreateBranch()
    {
        Window prompt = new()
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
        create.Click += async (_, _) =>
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
        };

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
        SetStatus(string.Format(
            T("Running {0} …"),
            $"git {(doCheckout ? "checkout -b" : "branch")} {branch} HEAD"));
        RunActionResult(
            () => _actions.CreateBranch(_repoPath, branch, doCheckout),
            result =>
            {
                SetStatus(result.Success
                    ? string.Format(
                        doCheckout
                            ? T("Created and checked out branch '{0}'.")
                            : T("Created branch '{0}'."),
                        branch)
                    : string.Format(T("Create branch failed: {0}"), FirstLine(result.Output)));
                RefreshBranchCaption();
                Reload();
            });
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
        StackPanel branchPanel = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _branchStatusText, _remoteStatusText },
        };
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
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 3),
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
            _stagedList.Items.Count,
            _stagedList.Items.Count + _unstagedList.Items.Count);
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
        _ = Task.Run(() => ReadStatusBarInfo(repo))
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                StatusBarInfo info = t.Result;
                _titleBranch = info.Branch;
                _pushTarget = info.PushTarget;
                _committerName = info.UserName;
                _committerEmail = info.UserEmail;
                UpdateTitle();
                RenderStatusBar();
            }), TaskScheduler.Default);
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

    // Simple in-dialog confirmation flyout on the status line via a modal child window.
    private async void ConfirmThen(string prompt, Action onConfirmed)
    {
        if (await ConfirmAsync(prompt))
        {
            onConfirmed();
        }
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
        Window confirm = new()
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
        _ = Task.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                return new CommitActionResult(false, ex.Message);
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            onResult(t.Result);
        }), TaskScheduler.Default);
    }

    private void RunGitResult(Func<WorkingDirCommitResult> work, Action<WorkingDirCommitResult> onResult)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                return new WorkingDirCommitResult(false, ex.Message);
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            onResult(t.Result);
        }), TaskScheduler.Default);
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
        _ = Task.Run(() =>
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
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            WorkingDirStatus status = t.Result.Status;
            _hiddenByIndexFlag = t.Result.HiddenByIndexFlag;
            ApplyMergeState(t.Result);

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

            _unstagedList.ItemsSource = unstaged;

            // An unmerged path is reported by the index listing too; showing it in
            // both lists would be misleading, so it stays only in the unstaged one.
            _stagedList.ItemsSource = _conflictPaths.Count == 0
                ? status.Staged
                : [.. status.Staged.Where(r => !_conflictPaths.Contains(r.Path))];
            _conflictBanner.IsVisible = _conflictPaths.Count > 0;
            RestoreDiffSelection();
            RenderStatus();

            // The lists are new objects, so the filter's match count is stale. Only the
            // COUNT is refreshed: re-selecting here would fight RestoreDiffSelection,
            // which has just put the user back on the file they were staging hunks of.
            RefreshSelectionFilterCount();

            // "Close dialog after all files committed": only now, on the snapshot
            // taken AFTER the commit, is it known whether anything is left to stage.
            if (_closeIfNothingLeft)
            {
                _closeIfNothingLeft = false;
                if (unstaged.Count == 0 && _stagedList.Items.Count == 0)
                {
                    Close();
                }
            }
        }), TaskScheduler.Default);
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
        int unstagedCount = _unstagedList.Items.Count;
        int stagedCount = _stagedList.Items.Count;
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

    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static ListBox MakeList() => new()
    {
        // Multiple, so stage / unstage / discard / copy path can act on a set of
        // files (the former working-directory panel offered "Discard changes (N files)").
        SelectionMode = SelectionMode.Multiple,
        FontFamily = Monospace,
        ClipToBounds = true,
    };

    private static TextBlock MakeHeaderLabel() => new()
    {
        FontWeight = FontWeight.Bold,
        Foreground = Brush("App.Foreground", Brushes.Gainsboro),
        Margin = new Thickness(2, 0, 0, 2),
    };

    private Control WrapWithHeader(TextBlock label, Control content, int row)
    {
        DockPanel panel = new() { Margin = new Thickness(0, 2) };
        DockPanel.SetDock(label, Dock.Top);
        Border box = new()
        {
            Child = content,
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        };
        panel.Children.Add(label);
        panel.Children.Add(box);
        Grid.SetRow(panel, row);
        return panel;
    }

    private Button MakeButton(string text, Action onClick)
    {
        Button b = MakeButton(onClick);
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
