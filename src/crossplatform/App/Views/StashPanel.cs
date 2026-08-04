using System.Globalization;
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
///  Stash panel: lists the repository's stashes and lets the user save a new
///  stash (with a message), or apply / pop / drop an existing one. All git work
///  runs off the UI thread via <see cref="Task.Run"/> and posts results back with
///  <see cref="Dispatcher.UIThread"/>.
///
///  Cherry-pick and reset are commit-targeted and are exposed by
///  <see cref="StashOpsService"/> only; they are meant to be wired into the
///  revision grid's context menu by the integrator, so this panel provides no UI
///  for them.
///
///  <para>Captions go through <see cref="TranslationService"/>, mostly with
///  <c>FormStash</c> and <c>RepoObjectsTree</c> XLIFF ids (the upstream stash
///  dialog and the stash nodes of the left tree). Because the translated verbs
///  are markedly longer than the English ones — "Drop" becomes "Elimina stash",
///  "Stash staged" becomes "Stash delle modifiche in stage" — the two button
///  rows are <see cref="WrapPanel"/>s, not fixed horizontal strips: in a 340 px
///  column they would otherwise run past the splitter. The panel re-labels
///  itself on <see cref="TranslationService.LanguageChanged"/>.</para>
/// </summary>
public sealed class StashPanel : UserControl
{
    private readonly StashOpsService _service = new();

    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");
    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Diff line colours, from the same theme keys DiffView reads. They used to
    // duplicate DiffView's dark-palette literals, which measured 1.88:1 and 2.90:1
    // against the light theme's diff background.
    private static IBrush? _addedBrush;
    private static IBrush? _removedBrush;

    private static IBrush AddedBrush => _addedBrush ??= B("App.DiffAdded");

    private static IBrush RemovedBrush => _removedBrush ??= B("App.DiffRemoved");

    private readonly ListBox _stashList;
    private readonly TextBox _messageBox;
    private readonly CheckBox _untrackedCheck;
    private readonly CheckBox _keepIndexCheck;
    private readonly Button _saveButton;
    private readonly Button _stashDialogButton;
    private readonly Button _stagedButton;
    private readonly Button _stashSelectedButton;
    private readonly Button _applyButton;
    private readonly Button _popButton;
    private readonly Button _dropButton;
    private readonly SelectableTextBlock _diff;
    private readonly TextBlock _status;
    private readonly TextBlock _listTitle;

    // The middle pane: the files of the selected stash, or — for the working
    // directory entry — its Index group in the first list and its Workspace group
    // in the second (upstream's SetStashDiffs, which puts both in one list under
    // two group headers; this port has one list per group instead).
    private readonly Grid _filesGrid;
    private readonly FileStatusListView _indexFiles;
    private readonly FileStatusListView _workTreeFiles;
    private readonly TextBlock _indexHeader;
    private readonly TextBlock _workTreeHeader;

    private string? _repoPath;
    private bool _busy;
    private CancellationTokenSource? _diffCts;
    private CancellationTokenSource? _filesCts;

    // The new-stash message the user typed. The message box doubles as the
    // read-only display of the selected stash's message (upstream reuses the very
    // same control), so the draft has to survive a round trip through a stash.
    private string _draftMessage = string.Empty;

    // Set while one file list is being cleared because the other one took the
    // selection, so the resulting events do not fight each other.
    private bool _syncingFileSelection;

    /// <summary>
    ///  Raised on the UI thread after any successful mutating operation
    ///  (list already refreshed).
    /// </summary>
    public event Action? OperationCompleted;

    public StashPanel()
    {
        _stashList = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            FontFamily = Monospace,
        };
        _stashList.SelectionChanged += (_, _) => OnStashSelectionChanged();

        _listTitle = new TextBlock
        {
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };

        _messageBox = new TextBox { Margin = new Thickness(0, 0, 0, 4) };

        _untrackedCheck = new CheckBox { Margin = new Thickness(0, 0, 0, 4) };

        // Upstream persists this one in AppSettings.StashKeepIndex and reads it
        // back when the dialog opens (FormStash.cs:108,114).
        _keepIndexCheck = new CheckBox
        {
            Margin = new Thickness(0, 0, 0, 4),
            IsChecked = AppSettings.StashKeepIndex,
        };
        _keepIndexCheck.IsCheckedChanged += (_, _) =>
            AppSettings.StashKeepIndex = _keepIndexCheck.IsChecked == true;

        // A trailing margin (rather than the parent's spacing) is what separates
        // the buttons, so it survives a wrap onto a second line.
        Thickness gap = new(0, 0, 6, 4);

        _saveButton = new Button { Margin = gap };
        _saveButton.Click += (_, _) => DoSave();

        _stashDialogButton = new Button { Margin = gap };
        _stashDialogButton.Click += (_, _) => _ = DoStashDialogAsync();

        _stagedButton = new Button { Margin = gap };
        _stagedButton.Click += (_, _) => DoStashStaged();

        // Upstream enables this only on the working-directory entry and only with
        // at least one file selected (FormStash.EnablePartialStash).
        _stashSelectedButton = new Button { Margin = gap, IsEnabled = false };
        _stashSelectedButton.Click += (_, _) => DoStashSelected();

        _applyButton = new Button { Margin = gap };
        _applyButton.Click += (_, _) => DoApply();

        _popButton = new Button { Margin = gap };
        _popButton.Click += (_, _) => DoPop();

        _dropButton = new Button { Margin = gap };
        _dropButton.Click += (_, _) => _ = DoDropAsync();

        // WrapPanel, not a horizontal StackPanel: translated verbs are much
        // wider than the English ones and this column is only 340 px.
        WrapPanel opButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
        };
        opButtons.Children.Add(_applyButton);
        opButtons.Children.Add(_popButton);
        opButtons.Children.Add(_dropButton);

        Grid listPanel = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8, 4, 8, 4),
        };
        Grid.SetRow(_listTitle, 0);
        Grid.SetRow(_stashList, 1);
        Grid.SetRow(opButtons, 2);
        listPanel.Children.Add(_listTitle);
        listPanel.Children.Add(_stashList);
        listPanel.Children.Add(opButtons);

        WrapPanel saveButtons = new() { Orientation = Orientation.Horizontal };
        saveButtons.Children.Add(_saveButton);
        saveButtons.Children.Add(_stashDialogButton);
        saveButtons.Children.Add(_stagedButton);
        saveButtons.Children.Add(_stashSelectedButton);

        WrapPanel saveChecks = new() { Orientation = Orientation.Horizontal };
        _untrackedCheck.Margin = new Thickness(0, 0, 12, 4);
        saveChecks.Children.Add(_untrackedCheck);
        saveChecks.Children.Add(_keepIndexCheck);

        StackPanel savePanel = new() { Margin = new Thickness(8, 4, 8, 4) };
        savePanel.Children.Add(_messageBox);
        savePanel.Children.Add(saveChecks);
        savePanel.Children.Add(saveButtons);

        _status = new TextBlock
        {
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = (IBrush)Application.Current!.Resources["App.TextDim"]!,
            TextWrapping = TextWrapping.Wrap,
        };

        // Read-only, colour-styled patch view of the selected stash.
        _diff = new SelectableTextBlock
        {
            FontFamily = Monospace,
            FontSize = 12,
            Foreground = B("App.Text"),
            Margin = new Thickness(12, 10, 12, 12),
            TextWrapping = TextWrapping.NoWrap,
        };

        ScrollViewer diffScroll = new()
        {
            Content = _diff,
            Background = B("App.Window"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // ---- middle pane: the files of whatever the stash list has selected ----
        _indexFiles = new FileStatusListView { ShowRefreshButton = true };
        _indexFiles.RefreshRequested += ReloadFiles;
        _workTreeFiles = new FileStatusListView { ShowToolbar = false };

        foreach (FileStatusListView view in new[] { _indexFiles, _workTreeFiles })
        {
            // "Stash selected changes" takes a set of paths, so the lists must be
            // able to hold one. The component itself is untouched: SelectionMode
            // lives on the ListBox it already exposes.
            view.List.SelectionMode = SelectionMode.Multiple;
            view.SelectedFileChanged += _ => ShowSelectedFileDiff();
            view.List.SelectionChanged += OnFileSelectionChanged;
        }

        _indexHeader = GroupHeader();
        _workTreeHeader = GroupHeader();

        _filesGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,0") };
        Grid.SetRow(_indexHeader, 0);
        Grid.SetRow(_indexFiles, 1);
        Grid.SetRow(_workTreeHeader, 2);
        Grid.SetRow(_workTreeFiles, 3);
        _filesGrid.Children.Add(_indexHeader);
        _filesGrid.Children.Add(_indexFiles);
        _filesGrid.Children.Add(_workTreeHeader);
        _filesGrid.Children.Add(_workTreeFiles);

        // Starting widths on the COLUMNS, not on the children: a GridSplitter resizes
        // the column, and a child with its own fixed Width would stay behind, leaving a
        // dead strip between its right edge and the splitter instead of following the
        // width the user dragged to.
        Grid split = new()
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(340, GridUnitType.Pixel) { MinWidth = 120 },
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(320, GridUnitType.Pixel) { MinWidth = 120 },
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star) { MinWidth = 120 },
            },
        };
        Grid.SetColumn(listPanel, 0);

        Grid.SetColumn(_filesGrid, 2);

        Grid.SetColumn(diffScroll, 4);

        split.Children.Add(listPanel);
        split.Children.Add(Splitter(1));
        split.Children.Add(_filesGrid);
        split.Children.Add(Splitter(3));
        split.Children.Add(diffScroll);

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(savePanel, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(savePanel);
        root.Children.Add(split);

        Content = root;

        // Ctrl+N / Ctrl+P walk the stash list, upstream's Stash hotkeys
        // (HotkeySettingsManager: NextStash = Ctrl+N, PreviousStash = Ctrl+P).
        // Tunnelling, so the keys work while the message box has the focus.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    private GridSplitter Splitter(int column)
    {
        GridSplitter splitter = new()
        {
            Width = 4,
            Background = B("App.Border"),
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, column);
        return splitter;
    }

    private static TextBlock GroupHeader() => new()
    {
        FontWeight = FontWeight.Bold,
        FontSize = 12,
        Foreground = B("App.TextDim"),
        Margin = new Thickness(8, 4, 8, 2),
        IsVisible = false,
    };

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        if (e.Key == Key.N)
        {
            e.Handled = StepStash(next: true);
        }
        else if (e.Key == Key.P)
        {
            e.Handled = StepStash(next: false);
        }
    }

    // Upstream's ChangeSelectedStash: move by one, and stop at the ends.
    private bool StepStash(bool next)
    {
        if (_stashList.ItemCount == 0)
        {
            return false;
        }

        int index = _stashList.SelectedIndex + (next ? 1 : -1);
        if (index < 0 || index >= _stashList.ItemCount)
        {
            return false;
        }

        _stashList.SelectedIndex = index;
        return true;
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string ErrorWord() => T("TranslatedStrings/_error.Text", "Error");

    // WinForms mnemonics ("&Keep index") are not Avalonia's, and a stray "&"
    // would be drawn as-is.
    private static string Strip(string caption) => RevisionFilterDialog.StripMnemonic(caption);

    // One format with a placeholder for the raw git output, never a translated
    // prefix glued to a message.
    private static string FailedFormat() => T("Failed: {0}");

    private void ApplyTranslations()
    {
        _listTitle.Text = T("TranslatedStrings/_stashesText.Text", "Stashes");
        _messageBox.Watermark = T("Stash message (optional)");
        _untrackedCheck.Content = T("FormStash/chkIncludeUntrackedFiles.Text", "Include untracked files");
        _keepIndexCheck.Content = Strip(T("FormStash/StashKeepIndex.Text", "&Keep index"));
        ToolTip.SetTip(
            _keepIndexCheck,
            T("FormStash/StashKeepIndex.toolTip", "All changes already added to the index are left intact"));

        _indexHeader.Text = T("TranslatedStrings/_indexText.Text", "Commit index");
        _workTreeHeader.Text = T("TranslatedStrings/_workspaceText.Text", "Working directory");

        _saveButton.Content = T("FormStash/Stash.Text", "Save stash");
        _stashDialogButton.Content = T("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash…");
        _stagedButton.Content = T("FormBrowse/stashStagedToolStripMenuItem.Text", "Stash staged");
        _stashSelectedButton.Content = Strip(T("FormStash/StashSelectedFiles.Text", "Stash &selected changes"));
        ToolTip.SetTip(
            _stashSelectedButton,
            T(
                "FormStash/StashSelectedFiles.toolTip",
                "Stash changes for the selected files, then revert them to the original state"));
        _applyButton.Content = T("RepoObjectsTree/mnubtnApplyStash.Text", "Apply");
        _popButton.Content = T("RepoObjectsTree/mnubtnPopStash.Text", "Pop");
        _dropButton.Content = T("RepoObjectsTree/mnubtnDropStash.Text", "Drop");

        // Only the idle placeholders are re-stated: a live status line (a result,
        // an error) belongs to an operation that already happened.
        if (_repoPath is not { Length: > 0 })
        {
            _status.Text = T("No repository loaded.");
        }

        if (CurrentFile() is null)
        {
            ShowDiffPlaceholder();
        }
    }

    private void ShowDiffPlaceholder()
    {
        _diff.Inlines?.Clear();
        _diff.Text = T("Select a file to view its diff.");
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    /// <summary>
    ///  Points the panel at <paramref name="repoPath"/> and loads its stashes.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        RefreshStashes();
    }

    /// <summary>
    ///  Opens the "create a stash" prompt (message + include-untracked), the same flow
    ///  as the panel's own "Stash…" button. Exposed so the main toolbar's stash
    ///  split-button entry "Create a stash…" — upstream's
    ///  <c>createAStashToolStripMenuItem</c>, i.e. <c>StartStashDialog(this, false)</c>
    ///  — has a real surface to open. No-ops until <see cref="LoadRepository"/> has
    ///  been called. Call from the UI thread.
    /// </summary>
    public void BeginCreateStash() => _ = DoStashDialogAsync();

    private void RefreshStashes()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _status.Text = T("No repository loaded.");
            return;
        }

        _status.Text = T("FormBrowse/_loading.Text", "Loading…");
        RunGit(
            () => _service.ListStashes(repo),
            stashes =>
            {
                // A brand-new list instance, never a mutated one: reassigning the
                // same instance to ItemsSource does not rebuild the containers.
                // The synthetic working-directory entry goes first, as upstream
                // inserts it at index 0 (FormStash.Initialize).
                List<object> items = [new WorkingDirRow(WorkingDirText())];
                items.AddRange(stashes);
                _stashList.ItemsSource = items;
                _stashList.SelectedIndex = 0;

                _status.Text = stashes.Count == 0
                    ? T("FormStash/_noStashes.Text", "There are no stashes.")
                    : F(T("{0} stash(es)."), stashes.Count);
            });
    }

    private static string WorkingDirText()
        => T("FormStash/_currentWorkingDirChanges.Text", "Current working directory changes");

    /// <summary>
    ///  The synthetic first row of the stash list. A record of its own rather than
    ///  a <see cref="StashRow"/> with a fake index, so nothing can mistake it for
    ///  a stash that <c>git stash apply</c> could be pointed at.
    /// </summary>
    private sealed record WorkingDirRow(string Text)
    {
        public override string ToString() => Text;
    }

    private bool IsWorkingDirSelected => _stashList.SelectedItem is WorkingDirRow;

    private void DoSave()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        string message = DraftMessage();
        bool untracked = _untrackedCheck.IsChecked == true;
        bool keepIndex = _keepIndexCheck.IsChecked == true;

        _status.Text = T("Saving stash…");
        RunGit(
            () => _service.StashSave(repo, message, untracked, keepIndex),
            result => OnMutated(result, T("Stash saved."), ClearDraft));
    }

    /// <summary>
    ///  Stashes only the files picked in the middle pane — upstream's "Stash
    ///  selected changes", which is why that button is live on the working
    ///  directory entry only.
    /// </summary>
    private void DoStashSelected()
    {
        if (_repoPath is not { Length: > 0 } repo || !IsWorkingDirSelected)
        {
            return;
        }

        List<string> files = SelectedFileNames();
        if (files.Count == 0)
        {
            return;
        }

        string message = DraftMessage();
        bool untracked = _untrackedCheck.IsChecked == true;
        bool keepIndex = _keepIndexCheck.IsChecked == true;

        _status.Text = T("Saving stash…");
        RunGit(
            () => _service.StashSave(repo, message, untracked, keepIndex, files),
            result => OnMutated(result, F(T("{0} file(s) stashed."), files.Count), ClearDraft));
    }

    // The message box shows the selected stash's message while a stash is
    // selected, so the text a new stash should carry is the preserved draft.
    private string DraftMessage()
        => _messageBox.IsReadOnly ? _draftMessage : _messageBox.Text ?? string.Empty;

    private void ClearDraft()
    {
        _draftMessage = string.Empty;
        if (!_messageBox.IsReadOnly)
        {
            _messageBox.Text = string.Empty;
        }
    }

    private void DoStashStaged()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        string message = DraftMessage();

        _status.Text = T("Stashing staged changes…");
        RunGit(
            () => _service.StashStaged(repo, message),
            result => OnMutated(result, T("Staged changes stashed."), ClearDraft));
    }

    private async Task DoStashDialogAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (await PromptStashAsync() is not { } prompt)
            {
                return;
            }

            _status.Text = T("Saving stash…");
            RunGit(
                () => _service.StashSaveMessage(repo, prompt.Message, prompt.IncludeUntracked),
                result => OnMutated(result, T("Stash saved."), ClearDraft));
        }
        catch (Exception ex)
        {
            _status.Text = F(FailedFormat(), ex.Message);
        }
    }

    private void DoApply()
    {
        if (SelectedStash() is not { } stash || _repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _status.Text = T("Applying…");
        RunGit(
            () => _service.StashApply(repo, stash.Name),
            result => OnMutated(result, T("Stash applied.")));
    }

    private void DoPop()
    {
        if (SelectedStash() is not { } stash || _repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _status.Text = T("Popping…");
        RunGit(
            () => _service.StashPop(repo, stash.Name),
            result => OnMutated(result, T("Stash popped.")));
    }

    private async Task DoDropAsync()
    {
        try
        {
            if (SelectedStash() is not { } stash || _repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            // The stash name and message are data and go in verbatim (no
            // underscore escaping: this is a TextBlock, not a menu header); the
            // only translated part is the upstream confirmation sentence.
            string question = T(
                "TranslatedStrings/_areYouSure.Text",
                "Are you sure you want to drop the stash? This action cannot be undone.");
            string title = T("TranslatedStrings/_stashDropConfirmTitle.Text", "Drop Stash Confirmation");

            bool confirmed = await ConfirmAsync(
                F("{0}\n\n{1}\n\n{2}", stash.Name, stash.Message, question), title);
            if (!confirmed)
            {
                return;
            }

            _status.Text = T("Dropping…");
            RunGit(
                () => _service.StashDrop(repo, stash.Name),
                result => OnMutated(result, T("Stash dropped.")));
        }
        catch (Exception ex)
        {
            _status.Text = F(FailedFormat(), ex.Message);
        }
    }

    private void OnMutated(StashOpResult result, string okText, Action? onSuccess = null)
    {
        if (result.Success)
        {
            onSuccess?.Invoke();
            _status.Text = okText;
            RefreshStashes();
            OperationCompleted?.Invoke();
        }
        else
        {
            _status.Text = F(FailedFormat(), result.Output.Trim());
        }

        // `stash apply`/`pop` is a real merge, so it can leave conflicts: ask, as
        // upstream does. Fire-and-forget because this runs from a result callback;
        // the probe is a no-op when the index is clean.
        _ = AskAboutConflictsAsync();
    }

    private async Task AskAboutConflictsAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo
                || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            if (await ConflictFlow.HandleAsync(owner, repo) is { HadConflicts: true })
            {
                RefreshStashes();
                OperationCompleted?.Invoke();
            }
        }
        catch
        {
            // Never throw out of a result callback.
        }
    }

    private StashRow? SelectedStash()
        => _stashList.SelectedItem as StashRow;

    // ------------------------------------------------------- stash selection

    // The stash list drives everything else: the message box (upstream reuses one
    // control for the new-stash message and the selected stash's own message),
    // which operations are legal, and the file list of the middle pane.
    private void OnStashSelectionChanged()
    {
        bool workingDir = IsWorkingDirSelected;

        if (workingDir)
        {
            _messageBox.IsReadOnly = false;
            _messageBox.Text = _draftMessage;
        }
        else
        {
            if (!_messageBox.IsReadOnly)
            {
                _draftMessage = _messageBox.Text ?? string.Empty;
            }

            _messageBox.IsReadOnly = true;
            _messageBox.Text = SelectedStash()?.Message ?? string.Empty;
        }

        // Apply / Pop / Drop are meaningless on the working directory, and
        // upstream disables them there (FormStash.InitializeSoft).
        bool onStash = SelectedStash() is not null;
        _applyButton.IsEnabled = onStash && !_busy;
        _popButton.IsEnabled = onStash && !_busy;
        _dropButton.IsEnabled = onStash && !_busy;

        ReloadFiles();
    }

    // ------------------------------------------------------------- file list

    // Loads the middle pane for whatever the stash list has selected. Off the UI
    // thread, and outside RunGit's single-operation gate: a selection change must
    // not be dropped just because a mutation is in flight.
    private void ReloadFiles()
    {
        _filesCts?.Cancel();
        _filesCts?.Dispose();
        _filesCts = new CancellationTokenSource();
        CancellationToken token = _filesCts.Token;

        bool workingDir = IsWorkingDirSelected;
        SetFilesMode(workingDir);

        if (_repoPath is not { Length: > 0 } repo)
        {
            SetFileRows([], []);
            return;
        }

        StashRow? stash = SelectedStash();
        if (!workingDir && stash is null)
        {
            SetFileRows([], []);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                (IReadOnlyList<DiffFileRow> first, IReadOnlyList<DiffFileRow> second) = workingDir
                    ? Split(_service.GetWorkingDirFiles(repo))
                    : (_service.GetStashFiles(repo, stash!.Name), (IReadOnlyList<DiffFileRow>)[]);

                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        SetFileRows(first, second);
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        SetFileRows([], []);
                        _status.Text = F("{0}: {1}", ErrorWord(), ex.Message);
                    }
                });
            }
        });

        static (IReadOnlyList<DiffFileRow>, IReadOnlyList<DiffFileRow>) Split(StashWorkingDirFiles files)
            => (files.Index, files.WorkTree);
    }

    // In working-directory mode the pane shows two labelled lists (upstream's
    // Index and Workspace groups); for a stash it shows one, and the second row
    // pair is collapsed to zero height — an invisible child of a star row would
    // still take up half the pane.
    private void SetFilesMode(bool workingDir)
    {
        _indexHeader.IsVisible = workingDir;
        _workTreeHeader.IsVisible = workingDir;
        _workTreeFiles.IsVisible = workingDir;
        _filesGrid.RowDefinitions = new RowDefinitions(workingDir ? "Auto,*,Auto,*" : "Auto,*,Auto,0");
    }

    private void SetFileRows(IReadOnlyList<DiffFileRow> first, IReadOnlyList<DiffFileRow> second)
    {
        // Each SetFiles selects its list's first row, so without this guard the
        // two lists would end up both selected and would each load a diff.
        _syncingFileSelection = true;
        try
        {
            _indexFiles.SetFiles(first);
            _workTreeFiles.SetFiles(second);

            if (first.Count > 0)
            {
                _workTreeFiles.List.SelectedItem = null;
            }
        }
        finally
        {
            _syncingFileSelection = false;
        }

        ShowSelectedFileDiff();
        UpdateStashSelectedEnabled();
    }

    // One logical selection across the two lists: taking a selection in one drops
    // the other's, so the diff pane always has a single unambiguous subject.
    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingFileSelection)
        {
            return;
        }

        if (sender is ListBox list && e.AddedItems.Count > 0)
        {
            ListBox other = ReferenceEquals(list, _indexFiles.List) ? _workTreeFiles.List : _indexFiles.List;
            if (other.SelectedItems is { Count: > 0 })
            {
                _syncingFileSelection = true;
                try
                {
                    other.SelectedItems.Clear();
                }
                finally
                {
                    _syncingFileSelection = false;
                }
            }
        }

        UpdateStashSelectedEnabled();
    }

    // The file the diff pane should be showing, and whether it is a staged one
    // (which decides the git comparison for a working-directory file).
    private (DiffFileRow Row, bool Staged)? CurrentFile()
    {
        if (_indexFiles.SelectedFile is { } indexRow)
        {
            return (indexRow, IsWorkingDirSelected);
        }

        if (IsWorkingDirSelected && _workTreeFiles.SelectedFile is { } workRow)
        {
            return (workRow, false);
        }

        return null;
    }

    // Every file picked in either list, for the partial stash.
    private List<string> SelectedFileNames()
    {
        List<string> names = [];
        foreach (FileStatusListView view in new[] { _indexFiles, _workTreeFiles })
        {
            if (!view.IsVisible || view.List.SelectedItems is not { } selected)
            {
                continue;
            }

            foreach (object? item in selected)
            {
                if (item is FileListFileNode node && !names.Contains(node.Row.Name))
                {
                    names.Add(node.Row.Name);
                }
            }
        }

        return names;
    }

    private void UpdateStashSelectedEnabled()
        => _stashSelectedButton.IsEnabled = !_busy && IsWorkingDirSelected && SelectedFileNames().Count > 0;

    // ------------------------------------------------------------- diff pane

    // Loads and renders the patch of the selected file. Any in-flight load is
    // superseded so rapid selection changes stay correct.
    private void ShowSelectedFileDiff()
    {
        if (_syncingFileSelection)
        {
            return;
        }

        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        if (CurrentFile() is not { } selection || _repoPath is not { Length: > 0 } repo)
        {
            ShowDiffPlaceholder();
            return;
        }

        _diff.Inlines?.Clear();
        _diff.Text = T("FormBrowse/_loading.Text", "Loading diff…");

        if (IsWorkingDirSelected)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    string text = _service.GetWorkingDirFileDiff(repo, selection.Row, selection.Staged);
                    PostDiff(text, token);
                }
                catch (Exception ex)
                {
                    PostError(ex, token);
                }
            });

            return;
        }

        if (SelectedStash() is { } stash)
        {
            _ = LoadStashFileDiffAsync(repo, stash, selection.Row, token);
        }
    }

    // A stash's per-file patch is a plain two-revision diff, so it goes through
    // the shared DiffTextService and picks up the diff toolbar's options
    // (whitespace, context lines, encoding) like every other file diff in the app.
    // An untracked file was never in <ref>: it lives in the third parent, and that
    // is the side to compare against.
    private async Task LoadStashFileDiffAsync(
        string repo, StashRow stash, DiffFileRow file, CancellationToken token)
    {
        try
        {
            DiffTextRequest request = new(
                Kind: DiffTextKind.Range,
                RepoPath: repo,
                CommitHash: file.IsTracked ? stash.Name : stash.Name + "^3",
                BaseHash: stash.Name + "^",
                Path: file.Name,
                OldPath: file.OldName);

            string text = await DiffTextService.GetDiffTextAsync(request, DiffTextService.Session, token);
            PostDiff(text, token);
        }
        catch (OperationCanceledException)
        {
            // A newer selection won.
        }
        catch (Exception ex)
        {
            PostError(ex, token);
        }
    }

    private void PostDiff(string text, CancellationToken token) => Dispatcher.UIThread.Post(() =>
    {
        if (!token.IsCancellationRequested)
        {
            RenderDiff(string.IsNullOrEmpty(text)
                ? F("({0})", T("FileStatusList/NoFiles.Text", "no changes"))
                : text);
        }
    });

    private void PostError(Exception ex, CancellationToken token) => Dispatcher.UIThread.Post(() =>
    {
        if (!token.IsCancellationRequested)
        {
            _diff.Inlines?.Clear();
            _diff.Text = F("{0}: {1}", ErrorWord(), ex.Message);
        }
    });

    // Colour each diff line: added green, removed red, hunk headers accent,
    // file/meta headers dim. Mirrors DiffView.RenderDiff.
    private void RenderDiff(string diffText)
    {
        _diff.Text = string.Empty;
        InlineCollection inlines = _diff.Inlines ??= [];
        inlines.Clear();

        foreach (string line in diffText.Split('\n'))
        {
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
            }
            else if (line.StartsWith('+'))
            {
                brush = AddedBrush;
            }
            else if (line.StartsWith('-'))
            {
                brush = RemovedBrush;
            }

            Run run = new(line + "\n");
            if (brush is not null)
            {
                run.Foreground = brush;
            }

            inlines.Add(run);
        }
    }

    // Prompts for a stash message and an include-untracked choice. Returns null
    // if the user cancels.
    private async Task<StashPrompt?> PromptStashAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        StashPrompt? result = null;

        TextBox message = new()
        {
            Watermark = T("Stash message (optional)"),
            Text = DraftMessage(),
            Margin = new Thickness(0, 0, 0, 8),
        };
        CheckBox untracked = new()
        {
            Content = T("FormStash/chkIncludeUntrackedFiles.Text", "Include untracked files"),
            IsChecked = _untrackedCheck.IsChecked == true,
        };

        // MinWidth 70 is a floor, not a cap: the buttons still grow to fit a
        // longer translated caption ("Annulla"), and the row is right-aligned.
        Button ok = new()
        {
            Content = T("FormStash/$this.Text", "Stash"),
            MinWidth = 70,
            Margin = new Thickness(0, 0, 6, 0),
            IsDefault = true,
        };
        Button cancel = new()
        {
            Content = T("Globalized/Cancel.Text", "Cancel"),
            MinWidth = 70,
            IsCancel = true,
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        StackPanel content = new() { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock
        {
            Text = T("Create a new stash from the working directory:"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        content.Children.Add(message);
        content.Children.Add(untracked);
        content.Children.Add(buttons);

        Theming.ZoomWindow dialog = new()
        {
            Title = T("FormBrowse/stashChangesToolStripMenuItem.ToolTipText", "Stash changes"),
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = content,
        };

        ok.Click += (_, _) =>
        {
            result = new StashPrompt(message.Text ?? string.Empty, untracked.IsChecked == true);
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return result;
    }

    private sealed record StashPrompt(string Message, bool IncludeUntracked);

    // Minimal modal confirmation using base Avalonia only (no message-box package).
    private async Task<bool> ConfirmAsync(string text, string title)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        bool result = false;

        Button yes = new()
        {
            Content = T("TranslatedStrings/_yes.Text", "Yes"),
            MinWidth = 70,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Button no = new() { Content = T("TranslatedStrings/_no.Text", "No"), MinWidth = 70 };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        StackPanel content = new() { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);

        Theming.ZoomWindow dialog = new()
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = content,
        };

        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => { result = false; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return result;
    }

    // Runs a git operation off the UI thread and marshals the result (or error)
    // back onto it, disabling the action buttons while busy.
    private void RunGit<T>(Func<T> work, Action<T> onResult)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _ = Task.Run(() =>
        {
            try
            {
                T result = work();
                Dispatcher.UIThread.Post(() =>
                {
                    SetBusy(false);
                    onResult(result);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    SetBusy(false);
                    _status.Text = F("{0}: {1}", ErrorWord(), ex.Message);
                });
            }
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _saveButton.IsEnabled = !busy;
        _stashDialogButton.IsEnabled = !busy;
        _stagedButton.IsEnabled = !busy;

        // These three also depend on what is selected: a stash, never the working
        // directory entry.
        bool onStash = !busy && SelectedStash() is not null;
        _applyButton.IsEnabled = onStash;
        _popButton.IsEnabled = onStash;
        _dropButton.IsEnabled = onStash;

        UpdateStashSelectedEnabled();
    }
}
