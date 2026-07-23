using Avalonia;
using Avalonia.Controls;
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

    private readonly ListBox _stashList;
    private readonly TextBox _messageBox;
    private readonly CheckBox _untrackedCheck;
    private readonly Button _saveButton;
    private readonly Button _applyButton;
    private readonly Button _popButton;
    private readonly Button _dropButton;
    private readonly TextBlock _status;

    private string? _repoPath;
    private bool _busy;

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
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
        };

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

        _saveButton = new Button { Content = "Save stash" };
        _saveButton.Click += (_, _) => DoSave();

        _applyButton = new Button { Content = "Apply", Margin = new Thickness(0, 0, 6, 0) };
        _applyButton.Click += (_, _) => DoApply();

        _popButton = new Button { Content = "Pop", Margin = new Thickness(0, 0, 6, 0) };
        _popButton.Click += (_, _) => DoPop();

        _dropButton = new Button { Content = "Drop" };
        _dropButton.Click += (_, _) => DoDrop();

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

        StackPanel savePanel = new() { Margin = new Thickness(8, 4, 8, 4) };
        savePanel.Children.Add(_messageBox);
        savePanel.Children.Add(_untrackedCheck);
        savePanel.Children.Add(_saveButton);

        _status = new TextBlock
        {
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = Brushes.Gray,
            Text = "No repository loaded.",
            TextWrapping = TextWrapping.Wrap,
        };

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(savePanel, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(savePanel);
        root.Children.Add(listPanel);

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

    private async void DoDrop()
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
        _applyButton.IsEnabled = !busy;
        _popButton.IsEnabled = !busy;
        _dropButton.IsEnabled = !busy;
    }
}
