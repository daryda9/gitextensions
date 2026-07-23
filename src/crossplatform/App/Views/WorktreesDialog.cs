using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Worktrees manager" for the Avalonia port: lists the repository's
///  linked worktrees (path, branch/HEAD, flags) and offers Add… (path + branch),
///  Remove (selected, confirmed), Prune and Close, each delegating to
///  <see cref="WorktreeService"/> (which shells out to <c>git worktree …</c>).
///  Command output/status is shown in a read-only pane, and the list re-reflects
///  the new state after every action. All git work runs off the UI thread via
///  <see cref="Task.Run"/> and marshals back with <see cref="Dispatcher.UIThread"/>.
///  <see cref="Changed"/> is set when any mutation succeeds so the caller can
///  refresh the repository tree after the dialog closes.
/// </summary>
public sealed class WorktreesDialog : Window
{
    private readonly WorktreeService _service = new();
    private readonly string _repoPath;
    private readonly ListBox _list;
    private readonly Button _remove;
    private readonly TextBox _output;
    private readonly TextBlock _status;

    private bool _busy;

    /// <summary>
    ///  True when at least one add/remove/prune succeeded, so the owner can
    ///  refresh its view once the dialog is dismissed.
    /// </summary>
    public bool Changed { get; private set; }

    public WorktreesDialog(string repoPath)
    {
        _repoPath = repoPath;

        Title = "Worktrees";
        Width = 640;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Panel", Brushes.DimGray);

        _list = new ListBox
        {
            Background = Brush("App.PanelAlt", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();

        Button add = MakeButton("Add…");
        _remove = MakeButton("Remove");
        Button prune = MakeButton("Prune");
        Button close = MakeButton("Close");

        add.Click += (_, _) => _ = DoAddAsync();
        _remove.Click += (_, _) => _ = DoRemoveAsync();
        prune.Click += (_, _) => Run("Prune", () => _service.PruneWorktrees(_repoPath));
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Width = 140,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(add);
        buttons.Children.Add(_remove);
        buttons.Children.Add(prune);
        buttons.Children.Add(new Border { Height = 8 });
        buttons.Children.Add(close);

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_list, 0);
        Grid.SetColumn(buttons, 1);
        top.Children.Add(_list);
        top.Children.Add(buttons);

        _status = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gainsboro),
            Margin = new Thickness(0, 8, 0, 4),
            Text = string.Empty,
        };

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 120,
            FontFamily = new FontFamily("monospace"),
            Background = Brush("App.PanelAlt", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        Grid body = new()
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
        };
        Grid.SetRow(top, 0);
        Grid.SetRow(_status, 1);
        Grid.SetRow(_output, 2);
        body.Children.Add(top);
        body.Children.Add(_status);
        body.Children.Add(_output);

        Content = body;

        Opened += (_, _) => ReloadList();
        UpdateButtons();
    }

    private WorktreeItem? Selected => _list.SelectedItem as WorktreeItem;

    // The main/bare worktree cannot be removed via `git worktree remove`; only
    // linked, non-bare entries are removable.
    private void UpdateButtons()
        => _remove.IsEnabled = Selected is { Row.IsBare: false } && !_busy;

    private void ReloadList()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        _ = Task.Run(() =>
        {
            IReadOnlyList<WorktreeRow> rows;
            try
            {
                rows = _service.ListWorktrees(_repoPath);
            }
            catch
            {
                rows = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                string? keep = Selected?.Row.Path;
                List<WorktreeItem> items = rows.Select(r => new WorktreeItem(r)).ToList();
                _list.ItemsSource = items;
                if (keep is not null)
                {
                    _list.SelectedItem = items.FirstOrDefault(i => i.Row.Path == keep);
                }

                UpdateButtons();
            });
        });
    }

    // --- Operations -------------------------------------------------------

    private async Task DoAddAsync()
    {
        string? path = await PromptAsync("New worktree path:", string.Empty);
        if (path is not { Length: > 0 } target)
        {
            return;
        }

        // Branch is optional: empty lets git create a branch named after the path.
        string? branch = await PromptAsync($"Branch/revision for '{target}' (blank = new branch):", string.Empty);
        Run($"Add '{target}'", () => _service.AddWorktree(_repoPath, target, branch ?? string.Empty));
    }

    private async Task DoRemoveAsync()
    {
        if (Selected is not { Row.IsBare: false } item)
        {
            return;
        }

        if (await ConfirmAsync($"Remove worktree '{item.Row.Path}'?"))
        {
            Run($"Remove '{item.Row.Path}'", () => _service.RemoveWorktree(_repoPath, item.Row.Path));
        }
    }

    private void Run(string label, Func<WorktreeOpResult> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _status.Text = $"{label}…";
        UpdateButtons();
        _ = Task.Run(() =>
        {
            WorktreeOpResult result;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                result = new WorktreeOpResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (result.Success)
                {
                    Changed = true;
                }

                _status.Text = $"{label}: {(result.Success ? "OK" : "failed")}";
                _output.Text = result.Output;
                ReloadList();
            });
        });
    }

    // --- Inline prompt / confirm (mirrors RepoObjectsTree helpers) --------

    private async Task<bool> ConfirmAsync(string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = "Confirm", Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Confirm",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task<string?> PromptAsync(string message, string initial)
    {
        TaskCompletionSource<string?> tcs = new();

        TextBox input = new() { Text = initial };
        Button ok = new() { Content = "OK", Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Worktree",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        ok.Click += (_, _) => { tcs.TrySetResult(input.Text?.Trim()); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                tcs.TrySetResult(input.Text?.Trim());
                dialog.Close();
                e.Handled = true;
            }
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private static Button MakeButton(string text)
        => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    // Display wrapper: the ListBox renders ToString(), so surface path + state
    // while keeping the underlying row for actions.
    private sealed record WorktreeItem(WorktreeRow Row)
    {
        public override string ToString()
        {
            string state = Row.IsBare ? "bare"
                : Row.Branch.Length > 0 ? Row.Branch
                : Row.IsDetached ? $"detached @ {Row.Head}"
                : Row.Head;

            return state.Length > 0 ? $"{Row.Path}  [{state}]" : Row.Path;
        }
    }
}
