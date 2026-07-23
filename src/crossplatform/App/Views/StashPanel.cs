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

        TextBlock listTitle = new()
        {
            Text = "Stashes",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };

        _messageBox = new TextBox
        {
            Watermark = "Stash message (optional)",
            Margin = new Thickness(0, 0, 0, 4),
        };

        _untrackedCheck = new CheckBox
        {
            Content = "Include untracked files",
            Margin = new Thickness(0, 0, 0, 4),
        };

        _saveButton = new Button { Content = "Save stash", Margin = new Thickness(0, 0, 6, 0) };
        _saveButton.Click += (_, _) => DoSave();

        _stashDialogButton = new Button { Content = "Stash…", Margin = new Thickness(0, 0, 6, 0) };
        _stashDialogButton.Click += (_, _) => _ = DoStashDialogAsync();

        _stagedButton = new Button { Content = "Stash staged" };
        _stagedButton.Click += (_, _) => DoStashStaged();

        _applyButton = new Button { Content = "Apply", Margin = new Thickness(0, 0, 6, 0) };
        _applyButton.Click += (_, _) => DoApply();

        _popButton = new Button { Content = "Pop", Margin = new Thickness(0, 0, 6, 0) };
        _popButton.Click += (_, _) => DoPop();

        _dropButton = new Button { Content = "Drop" };
        _dropButton.Click += (_, _) => _ = DoDropAsync();

        StackPanel opButtons = new()
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
        Grid.SetRow(listTitle, 0);
        Grid.SetRow(_stashList, 1);
        Grid.SetRow(opButtons, 2);
        listPanel.Children.Add(listTitle);
        listPanel.Children.Add(_stashList);
        listPanel.Children.Add(opButtons);

        StackPanel saveButtons = new() { Orientation = Orientation.Horizontal };
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
            Text = "No repository loaded.",
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
            Text = "Select a stash to view its diff.",
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
    }

    /// <summary>
    ///  Points the panel at <paramref name="repoPath"/> and loads its stashes.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        RefreshStashes();
    }

    private void RefreshStashes()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _status.Text = "No repository loaded.";
            return;
        }

        _status.Text = "Loading…";
        RunGit(
            () => _service.ListStashes(repo),
            stashes =>
            {
                _stashList.ItemsSource = stashes.ToList();
                _status.Text = $"{stashes.Count} stash(es).";
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

        _status.Text = "Saving stash…";
        RunGit(
            () => _service.StashSave(repo, message, untracked),
            result => OnMutated(result, "Stash saved.", () => _messageBox.Text = string.Empty));
    }

    private void DoStashStaged()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        string message = _messageBox.Text ?? string.Empty;

        _status.Text = "Stashing staged changes…";
        RunGit(
            () => _service.StashStaged(repo, message),
            result => OnMutated(result, "Staged changes stashed.", () => _messageBox.Text = string.Empty));
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

            _status.Text = "Saving stash…";
            RunGit(
                () => _service.StashSaveMessage(repo, prompt.Message, prompt.IncludeUntracked),
                result => OnMutated(result, "Stash saved.", () => _messageBox.Text = string.Empty));
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
        }
    }

    private void DoApply()
    {
        if (SelectedStash() is not { } stash || _repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _status.Text = "Applying…";
        RunGit(
            () => _service.StashApply(repo, stash.Name),
            result => OnMutated(result, "Stash applied."));
    }

    private void DoPop()
    {
        if (SelectedStash() is not { } stash || _repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _status.Text = "Popping…";
        RunGit(
            () => _service.StashPop(repo, stash.Name),
            result => OnMutated(result, "Stash popped."));
    }

    private async Task DoDropAsync()
    {
        try
        {
            if (SelectedStash() is not { } stash || _repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            bool confirmed = await ConfirmAsync($"Drop {stash.Name}?\n\n{stash.Message}\n\nThis cannot be undone.");
            if (!confirmed)
            {
                return;
            }

            _status.Text = "Dropping…";
            RunGit(
                () => _service.StashDrop(repo, stash.Name),
                result => OnMutated(result, "Stash dropped."));
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
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
            _status.Text = "Failed: " + result.Output.Trim();
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
            _diff.Text = "Select a stash to view its diff.";
            return;
        }

        _diff.Inlines?.Clear();
        _diff.Text = "Loading diff…";

        _ = Task.Run(() =>
        {
            try
            {
                string text = _service.GetStashDiff(repo, stash.Name);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        RenderDiff(string.IsNullOrEmpty(text) ? "(no changes)" : text);
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
                        _diff.Text = "Error: " + ex.Message;
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
            Watermark = "Stash message (optional)",
            Text = _messageBox.Text ?? string.Empty,
            Margin = new Thickness(0, 0, 0, 8),
        };
        CheckBox untracked = new()
        {
            Content = "Include untracked files",
            IsChecked = _untrackedCheck.IsChecked == true,
        };

        Button ok = new() { Content = "Stash", MinWidth = 70, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        Button cancel = new() { Content = "Cancel", MinWidth = 70, IsCancel = true };

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
            Text = "Create a new stash from the working directory:",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        content.Children.Add(message);
        content.Children.Add(untracked);
        content.Children.Add(buttons);

        Window dialog = new()
        {
            Title = "Stash changes",
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
    private async Task<bool> ConfirmAsync(string text)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        bool result = false;

        Button yes = new() { Content = "Yes", MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = "No", MinWidth = 70 };

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
            Title = "Confirm",
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
                    _status.Text = "Error: " + ex.Message;
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
