using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

    // Diff line colours, matching DiffView's dark-palette tuning.
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0xC7, 0x76));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));

    private readonly ListBox _stashList;
    private readonly TextBox _messageBox;
    private readonly CheckBox _untrackedCheck;
    private readonly Button _saveButton;
    private readonly Button _stashDialogButton;
    private readonly Button _stagedButton;
    private readonly Button _applyButton;
    private readonly Button _popButton;
    private readonly Button _dropButton;
    private readonly SelectableTextBlock _diff;
    private readonly TextBlock _status;
    private readonly TextBlock _listTitle;

    private string? _repoPath;
    private bool _busy;
    private CancellationTokenSource? _diffCts;

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
        _stashList.SelectionChanged += (_, _) => ShowSelectedStashDiff();

        _listTitle = new TextBlock
        {
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };

        _messageBox = new TextBox { Margin = new Thickness(0, 0, 0, 4) };

        _untrackedCheck = new CheckBox { Margin = new Thickness(0, 0, 0, 4) };

        // A trailing margin (rather than the parent's spacing) is what separates
        // the buttons, so it survives a wrap onto a second line.
        Thickness gap = new(0, 0, 6, 4);

        _saveButton = new Button { Margin = gap };
        _saveButton.Click += (_, _) => DoSave();

        _stashDialogButton = new Button { Margin = gap };
        _stashDialogButton.Click += (_, _) => _ = DoStashDialogAsync();

        _stagedButton = new Button { Margin = gap };
        _stagedButton.Click += (_, _) => DoStashStaged();

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

        StackPanel savePanel = new() { Margin = new Thickness(8, 4, 8, 4) };
        savePanel.Children.Add(_messageBox);
        savePanel.Children.Add(_untrackedCheck);
        savePanel.Children.Add(saveButtons);

        _status = new TextBlock
        {
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = Brushes.Gray,
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

        Grid split = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
        };
        Grid.SetColumn(listPanel, 0);
        listPanel.Width = 340;

        GridSplitter splitter = new()
        {
            Width = 4,
            Background = B("App.Border"),
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, 1);

        Grid.SetColumn(diffScroll, 2);

        split.Children.Add(listPanel);
        split.Children.Add(splitter);
        split.Children.Add(diffScroll);

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(savePanel, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(savePanel);
        root.Children.Add(split);

        Content = root;

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string ErrorWord() => T("TranslatedStrings/_error.Text", "Error");

    // One format with a placeholder for the raw git output, never a translated
    // prefix glued to a message.
    private static string FailedFormat() => T("Failed: {0}");

    private void ApplyTranslations()
    {
        _listTitle.Text = T("TranslatedStrings/_stashesText.Text", "Stashes");
        _messageBox.Watermark = T("Stash message (optional)");
        _untrackedCheck.Content = T("FormStash/chkIncludeUntrackedFiles.Text", "Include untracked files");

        _saveButton.Content = T("FormStash/Stash.Text", "Save stash");
        _stashDialogButton.Content = T("FormBrowse/stashChangesToolStripMenuItem.Text", "Stash…");
        _stagedButton.Content = T("FormBrowse/stashStagedToolStripMenuItem.Text", "Stash staged");
        _applyButton.Content = T("RepoObjectsTree/mnubtnApplyStash.Text", "Apply");
        _popButton.Content = T("RepoObjectsTree/mnubtnPopStash.Text", "Pop");
        _dropButton.Content = T("RepoObjectsTree/mnubtnDropStash.Text", "Drop");

        // Only the idle placeholders are re-stated: a live status line (a result,
        // an error) belongs to an operation that already happened.
        if (_repoPath is not { Length: > 0 })
        {
            _status.Text = T("No repository loaded.");
        }

        if (SelectedStash() is null)
        {
            _diff.Inlines?.Clear();
            _diff.Text = T("Select a stash to view its diff.");
        }
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
                _stashList.ItemsSource = stashes.ToList();
                _status.Text = stashes.Count == 0
                    ? T("FormStash/_noStashes.Text", "There are no stashes.")
                    : F(T("{0} stash(es)."), stashes.Count);
            });
    }

    private void DoSave()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        string message = _messageBox.Text ?? string.Empty;
        bool untracked = _untrackedCheck.IsChecked == true;

        _status.Text = T("Saving stash…");
        RunGit(
            () => _service.StashSave(repo, message, untracked),
            result => OnMutated(result, T("Stash saved."), () => _messageBox.Text = string.Empty));
    }

    private void DoStashStaged()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        string message = _messageBox.Text ?? string.Empty;

        _status.Text = T("Stashing staged changes…");
        RunGit(
            () => _service.StashStaged(repo, message),
            result => OnMutated(result, T("Staged changes stashed."), () => _messageBox.Text = string.Empty));
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
                result => OnMutated(result, T("Stash saved."), () => _messageBox.Text = string.Empty));
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
    }

    private StashRow? SelectedStash()
        => _stashList.SelectedItem as StashRow;

    // Loads and renders the selected stash's full patch, off the UI thread.
    // Any in-flight load is superseded so rapid selection changes stay correct.
    private void ShowSelectedStashDiff()
    {
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        if (SelectedStash() is not { } stash || _repoPath is not { Length: > 0 } repo)
        {
            _diff.Inlines?.Clear();
            _diff.Text = T("Select a stash to view its diff.");
            return;
        }

        _diff.Inlines?.Clear();
        _diff.Text = T("FormBrowse/_loading.Text", "Loading diff…");

        _ = Task.Run(() =>
        {
            try
            {
                string text = _service.GetStashDiff(repo, stash.Name);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        RenderDiff(string.IsNullOrEmpty(text)
                            ? F("({0})", T("FileStatusList/NoFiles.Text", "no changes"))
                            : text);
                    }
                });
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
            Text = _messageBox.Text ?? string.Empty,
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

        Window dialog = new()
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

        Window dialog = new()
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
        _applyButton.IsEnabled = !busy;
        _popButton.IsEnabled = !busy;
        _dropButton.IsEnabled = !busy;
    }
}
