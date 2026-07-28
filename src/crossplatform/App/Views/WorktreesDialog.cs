using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    private readonly Button _prune;
    private readonly TextBox _output;
    private readonly TextBlock _status;

    private bool _busy;

    /// <summary>True when at least one listed worktree is stale (prunable).</summary>
    private bool _anyPrunable;

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

        // Stale (prunable) worktrees are struck through and dimmed, so it is visible
        // at a glance why they cannot be removed and what Prune would clear out.
        _list.ItemTemplate = new FuncDataTemplate<WorktreeItem>((item, _) =>
        {
            TextBlock text = new()
            {
                Text = item?.ToString() ?? string.Empty,
                Foreground = item?.Row.IsPrunable == true
                    ? Brush("App.TextDim", Brushes.Gray)
                    : Brush("App.Text", Brushes.Gainsboro),
            };

            if (item?.Row.IsPrunable == true)
            {
                text.TextDecorations = TextDecorations.Strikethrough;
            }

            return text;
        });

        Button add = MakeButton("Add…");
        _remove = MakeButton("Remove");
        _prune = MakeButton("Prune");
        Button close = MakeButton("Close");

        add.Click += (_, _) => _ = DoAddAsync();
        _remove.Click += (_, _) => _ = DoRemoveAsync();
        _prune.Click += (_, _) => Run("Prune", () => _service.PruneWorktrees(_repoPath));
        close.Click += (_, _) => Close();

        // Escape = Close (upstream's CancelButton). Bubbling, so inner popups keep
        // their own Escape; Close() does not touch <see cref="Changed"/>.
        KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Width = 140,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(add);
        buttons.Children.Add(_remove);
        buttons.Children.Add(_prune);
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

        // TextBoxSurface: see OutputView — the Fluent per-state repaint beats the local
        // Background, so clicking this read-only log flipped its surface to pure
        // black (dark) / pure white (light).
        _output = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Height = 120,
                FontFamily = new FontFamily("monospace"),
                VerticalContentAlignment = VerticalAlignment.Top,
            },
            Brush("App.PanelAlt", Brushes.Black),
            Brush("App.Text", Brushes.Gainsboro));

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
        DialogKeys.EnsureFocusRoute(this);

        Opened += (_, _) => ReloadList();
        UpdateButtons();
    }

    private WorktreeItem? Selected => _list.SelectedItem as WorktreeItem;

    /// <summary>
    ///  `git worktree remove` refuses several of the entries it lists, so Remove is
    ///  only offered where it can actually work. Mirrors the gating of the Windows
    ///  dialog (<c>FormManageWorktree.CanDeleteSelectedWorkspace</c>):
    ///  <list type="bullet">
    ///   <item>the MAIN worktree owns the repository — it can never be removed;</item>
    ///   <item>a bare worktree has no working tree to remove;</item>
    ///   <item>a stale (prunable) entry has no working directory left; it is cleared
    ///    with Prune, not Remove;</item>
    ///   <item>the worktree the app currently has OPEN cannot remove itself.</item>
    ///  </list>
    ///  Previously only bare entries were excluded, so Remove was offered on the
    ///  main and on the open worktree and git simply failed.
    /// </summary>
    private bool CanRemove(WorktreeItem? item)
        => item is not null
            && !item.Row.IsMain
            && !item.Row.IsBare
            && !item.Row.IsPrunable
            && !item.Row.IsSamePath(_repoPath);

    private void UpdateButtons()
    {
        _remove.IsEnabled = !_busy && CanRemove(Selected);

        // Nothing to prune → nothing for the button to do.
        _prune.IsEnabled = !_busy && _anyPrunable;
    }

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
                _anyPrunable = items.Exists(i => i.Row.IsPrunable);
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
        if (Selected is not { } item || !CanRemove(item))
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

            string label = state.Length > 0 ? $"{Row.Path}  [{state}]" : Row.Path;

            // Say why an entry is not actionable rather than leaving a silently
            // disabled Remove button.
            if (Row.IsPrunable)
            {
                label += "  (deleted — use Prune)";
            }
            else if (Row.IsMain)
            {
                label += "  (main)";
            }

            return label;
        }
    }
}
