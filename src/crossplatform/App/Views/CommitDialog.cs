using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly TextBlock _statusText;

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

    // Options-menu state (mirrors the original commit form's Options dropdown).
    // Amend lives in _amendBox so the visible checkbox and the menu stay in sync.
    private bool _signOff;
    private bool _noVerify;
    private bool _resetAuthor;
    private bool _closeAfterCommit;

    /// <summary>Raised on each successful commit so the owner can refresh.</summary>
    public event Action? Committed;

    public CommitDialog(string repoPath)
    {
        _repoPath = repoPath;

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

        ContextMenu unstagedMenu = new()
        {
            ItemsSource = new Control[]
            {
                _stageItem,
                _discardItem,
                new Separator(),
                _mergetoolItem, _takeOursItem, _takeTheirsItem, _markResolvedItem,
                new Separator(),
                _unstagedCopyItem,
                new Separator(),
                _ignorePathItem, _ignoreExtItem, _ignoreFolderItem,
            },
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

            // The merge tool opens one file at a time, so it stays single-selection
            // only; taking a side / marking resolved already loops over the selection.
            _mergetoolItem.IsEnabled = conflict && count == 1;
            _takeOursItem.IsEnabled = conflict;
            _takeTheirsItem.IsEnabled = conflict;
            _markResolvedItem.IsEnabled = conflict;
        };
        _unstagedList.ContextMenu = unstagedMenu;

        _unstageItem.Click += (_, _) => UnstageSelected();
        _stagedCopyItem.Click += (_, _) => CopySelectedPath(_stagedList);
        ContextMenu stagedMenu = new()
        {
            ItemsSource = new Control[] { _unstageItem, new Separator(), _stagedCopyItem },
        };
        stagedMenu.Opening += (_, _) =>
        {
            int count = SelectedRows(_stagedList).Count;
            _unstageItem.IsEnabled = count > 0;
            _unstageItem.Header = WithCount(UnstageCaption, count);
            _stagedCopyItem.IsEnabled = count > 0;
            _stagedCopyItem.Header = WithCount(CopyPathCaption, count);
        };
        _stagedList.ContextMenu = stagedMenu;

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

        Grid leftPanel = new()
        {
            RowDefinitions = new RowDefinitions("*,Auto,*"),
        };
        leftPanel.Children.Add(WrapWithHeader(_unstagedHeader, _unstagedList, 0));
        Grid.SetRow(stageButtons, 1);
        leftPanel.Children.Add(stageButtons);
        leftPanel.Children.Add(WrapWithHeader(_stagedHeader, _stagedList, 2));

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
        Border diffBorder = new()
        {
            Child = _diffScroll,
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

        WrapPanel buttonRow = new()
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                _commitBtn, _commitPushBtn, _amendBox, _stashBtn,
                _resetAllBtn, _resetUnstagedBtn, _templatesBtn, _createBranchBtn, _optionsBtn,
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

        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_conflictBanner, Dock.Top);
        root.Children.Add(bottom);
        root.Children.Add(_conflictBanner);
        root.Children.Add(split);
        Content = root;

        InstallShortcuts();
        ApplyTranslations();

        // A language switch while the dialog is open re-captions it in place
        // (MainMenu does the same by rebuilding itself). The handler may run on
        // the loader's thread, hence the hop onto the UI thread.
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        Reload();
        RefreshBranchCaption();
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

        _ignorePathItem.Header = T("FileStatusList/tsmiAddFileToGitIgnore.Text", "Add to .gitignore");
        _ignoreExtItem.Header = T("Ignore by extension");
        _ignoreFolderItem.Header = T("Ignore in folder");

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
        _stageAllBtn.Content = T("FormCommit/_stageAll.Text", "Stage all");
        _unstageAllBtn.Content = T("FormCommit/_unstageAll.Text", "Unstage all");

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

        UpdateTitle();
        RenderStatus();
    }

    private static string StageCaption => T("FormCommit/toolStageItem.Text", "Stage");
    private static string UnstageCaption => T("FormCommit/toolUnstageItem.Text", "Unstage");
    private static string CopyPathCaption => T("FileStatusList/tsmiCopyPaths.Text", "Copy path");
    private static string DiscardCaption => T("Discard changes");

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
                }
            },
            RoutingStrategies.Tunnel);

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
                RenderDiff(string.Empty);
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
        LoadDiff(rows[^1].Path, staged);
    }

    private void LoadDiff(string path, bool staged)
    {
        string repo = _repoPath;
        _ = Task.Run(() =>
        {
            try
            {
                GitModule module = GitContext.CreateModule(repo);
                var res = module.GitExecutable.Execute(
                    staged ? $"diff --cached -- \"{path}\"" : $"diff -- \"{path}\"",
                    throwOnErrorExit: false);
                return res.AllOutput;
            }
            catch (Exception ex)
            {
                return string.Format(T("Could not load diff: {0}"), ex.Message);
            }
        }).ContinueWith(t =>
            Dispatcher.UIThread.Post(() => RenderDiff(t.Result)),
            TaskScheduler.Default);
    }

    private void RenderDiff(string diff)
    {
        InlineCollection inlines = new();
        IBrush add = Brush("App.DiffAdded", Brushes.LimeGreen);
        IBrush del = Brush("App.DiffRemoved", Brushes.OrangeRed);
        IBrush hunk = Brush("App.Accent", Brushes.DeepSkyBlue);
        IBrush normal = Brush("App.Foreground", Brushes.Gainsboro);

        foreach (string line in (diff ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            IBrush color = normal;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                color = hunk;
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                color = add;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                color = del;
            }

            inlines.Add(new Run(line + "\n") { Foreground = color });
        }

        _diffView.Inlines = inlines;
        _diffScroll.Offset = default;
    }

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

    private void StageAll()
    {
        var rows = _unstagedList.Items.OfType<WorkingDirFileRow>().ToList();
        if (rows.Count > 0)
        {
            RunGit(() => _service.Stage(_repoPath, rows));
        }
    }

    private void UnstageAll()
    {
        var rows = _stagedList.Items.OfType<WorkingDirFileRow>().ToList();
        if (rows.Count > 0)
        {
            RunGit(() => _service.Unstage(_repoPath, rows));
        }
    }

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
        CloseAfterCommit: _closeAfterCommit);

    private void DoCommit(bool push)
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

        SetStatus(string.Format(T("Running {0} …"), CommitActionsService.DescribeCommit(options)));
        RunActionResult(
            () => _actions.Commit(_repoPath, message, options),
            async result =>
            {
                if (!result.Success)
                {
                    SetStatus(string.Format(T("Commit failed: {0}"), FirstLine(result.Output)));
                    return;
                }

                _messageBox.Text = string.Empty;
                _amendBox.IsChecked = false;
                Committed?.Invoke();
                SetStatus(string.Format(T("Committed ({0})."), CommitActionsService.DescribeCommit(options)));
                Reload();

                if (push)
                {
                    await PushAsync();
                }

                if (options.CloseAfterCommit)
                {
                    Close();
                }
            });
    }

    private async Task PushAsync()
    {
        string repo = _repoPath;
        await GitProcessDialog.RunStreamingAsync(this, T("FormPush/_pushCaption.Text", "Push"), emit =>
        {
            var remotes = new RemoteService().ListRemotes(repo);
            string remote = remotes.Count > 0 ? remotes[0].Name : "origin";
            string branch = new RemoteService().GetCurrentBranch(repo);
            var r = new RemoteService().PushStreaming(repo, remote, branch, false, emit, null);
            return new GitProcessOutcome(r.Success, r.Output);
        });
    }

    private void DoReset(bool includeStaged)
    {
        if (includeStaged)
        {
            ConfirmThen(
                T("Reset ALL changes? This discards staged and unstaged tracked changes and cannot be undone."),
                () => RunGit(() => _service.ResetChanges(_repoPath, includeStaged: true)));
            return;
        }

        RunGit(() => _service.ResetChanges(_repoPath, includeStaged: false));
    }

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
            Foreground = Brushes.OrangeRed,
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
            _closeAfterCommit,
            v => _closeAfterCommit = v));

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

    private void RefreshBranchCaption()
    {
        string repo = _repoPath;
        _ = Task.Run(() =>
        {
            try
            {
                return _actions.CurrentBranch(repo);
            }
            catch
            {
                return string.Empty;
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _titleBranch = t.Result;
            UpdateTitle();
        }), TaskScheduler.Default);
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
        Window confirm = new()
        {
            Title = T("Confirm"),
            Width = 420,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", Brushes.DimGray),
        };

        bool ok = false;
        Button yes = MakeButton(T("Yes"), () => { ok = true; confirm.Close(); });
        Button no = MakeButton(T("FormCommit/Cancel.Text", "Cancel"), confirm.Close);
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
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { yes, no },
                },
            },
        };

        await confirm.ShowDialog(this);
        if (ok)
        {
            onConfirmed();
        }
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
    private sealed record ReloadSnapshot(WorkingDirStatus Status, bool Merging, string MergeMessage);

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
            return new ReloadSnapshot(status, merging, mergeMessage);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            WorkingDirStatus status = t.Result.Status;
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
            RenderStatus();
        }), TaskScheduler.Default);
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
        // "<Staged> 1/4": the label is upstream's status-bar caption, the ratio is
        // appended to it exactly as the original form's status strip does.
        string counts = string.Format(
            "{0} {1}/{2}",
            T("FormCommit/commitStagedCountLabel.Text", "Staged"),
            _stagedList.Items.Count,
            _stagedList.Items.Count + _unstagedList.Items.Count);
        if (_conflictPaths.Count > 0)
        {
            counts += "   —   " + string.Format(T("{0} conflict(s)"), _conflictPaths.Count);
        }

        if (_mergeInProgress)
        {
            counts += "   —   " + T("merge in progress");
        }

        _statusText.Text = _statusHint.Length > 0 ? $"{_statusHint}   —   {counts}" : counts;
    }

    // '_' in a menu header is an access-key marker in Avalonia; double it to show it.
    private static string Escape(string text) => text.Replace("_", "__");

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
