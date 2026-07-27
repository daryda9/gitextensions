using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Branch/tag operations panel: lists local + remote branches and tags (marking
///  the current branch) and offers checkout, create branch, create tag, merge,
///  rebase and delete. Names are entered inline. All git work runs off the UI
///  thread via <see cref="Task.Run"/> and posts results back with
///  <see cref="Dispatcher.UIThread"/>; buttons are disabled while busy and
///  operation output/errors are shown in a status line.
/// </summary>
public sealed class BranchTagPanel : UserControl
{
    private readonly BranchTagService _service = new();

    private readonly ListBox _branchList;
    private readonly ListBox _tagList;
    private readonly TextBox _refBox;
    private readonly CheckBox _forceDelete;
    private readonly Button _checkoutButton;
    private readonly Button _newBranchButton;
    private readonly Button _newTagButton;
    private readonly Button _mergeButton;
    private readonly Button _rebaseButton;
    private readonly Button _deleteButton;
    private readonly Button _refreshButton;
    private readonly TextBlock _status;

    private string? _repoPath;
    private bool _busy;

    /// <summary>
    ///  Raised on the UI thread after any successful mutating operation
    ///  (create/delete branch or tag, checkout, merge, rebase), so the host can
    ///  refresh history and other views.
    /// </summary>
    public event Action? OperationCompleted;

    public BranchTagPanel()
    {
        _branchList = new ListBox { FontFamily = new FontFamily("monospace,Consolas,Menlo") };
        _tagList = new ListBox { FontFamily = new FontFamily("monospace,Consolas,Menlo") };
        _branchList.SelectionChanged += (_, _) => { if (_branchList.SelectedItem is not null) { _tagList.SelectedItem = null; } };
        _tagList.SelectionChanged += (_, _) => { if (_tagList.SelectedItem is not null) { _branchList.SelectedItem = null; } };
        _branchList.DoubleTapped += (_, _) => DoCheckout();

        Grid lists = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(8, 4, 8, 4),
        };
        Control branchPanel = MakeListPanel("Branches (* = current)", _branchList);
        Control tagPanel = MakeListPanel("Tags", _tagList);
        Grid.SetColumn(branchPanel, 0);
        Grid.SetColumn(tagPanel, 1);
        lists.Children.Add(branchPanel);
        lists.Children.Add(tagPanel);

        // Name / message / checkout-after-create now live in the create dialogs
        // (CreateBranchDialog / CreateTagDialog); only the start point stays here.
        _refBox = new TextBox { Watermark = "Start point / commit (default HEAD)" };
        _forceDelete = new CheckBox { Content = "Force delete branch", Margin = new Thickness(0, 2, 0, 0) };

        StackPanel inputs = new() { Spacing = 4, Margin = new Thickness(8, 4, 8, 4) };
        inputs.Children.Add(_refBox);
        StackPanel checks = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
        checks.Children.Add(_forceDelete);
        inputs.Children.Add(checks);

        _checkoutButton = MakeButton("Checkout", () => DoCheckout());
        _newBranchButton = MakeButton("New branch…", () => DoCreateBranch());
        _newTagButton = MakeButton("New tag…", () => DoCreateTag());
        _mergeButton = MakeButton("Merge", () => DoMerge());
        _rebaseButton = MakeButton("Rebase", () => DoRebase());
        _deleteButton = MakeButton("Delete", () => _ = DoDeleteAsync());
        _refreshButton = MakeButton("Refresh", () => RefreshRefs());

        WrapPanel buttons = new() { Margin = new Thickness(8, 0, 8, 4) };
        foreach (Button b in new[] { _checkoutButton, _newBranchButton, _newTagButton, _mergeButton, _rebaseButton, _deleteButton, _refreshButton })
        {
            buttons.Children.Add(b);
        }

        _status = new TextBlock
        {
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = Brushes.Gray,
            Text = "No repository loaded.",
            TextWrapping = TextWrapping.Wrap,
        };

        StackPanel bottom = new();
        bottom.Children.Add(inputs);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(lists);

        Content = root;
    }

    /// <summary>
    ///  Points the panel at <paramref name="repoPath"/> and loads its refs.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        RefreshRefs();
    }

    private static Control MakeListPanel(string header, ListBox list)
    {
        Grid grid = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(4, 4, 4, 4),
        };
        TextBlock title = new()
        {
            Text = header,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };
        Grid.SetRow(title, 0);
        Grid.SetRow(list, 1);
        grid.Children.Add(title);
        grid.Children.Add(list);
        return grid;
    }

    private Button MakeButton(string text, Action onClick)
    {
        Button b = new() { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private BranchTagRow? SelectedRow()
        => _branchList.SelectedItem as BranchTagRow ?? _tagList.SelectedItem as BranchTagRow;

    private void RefreshRefs()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _status.Text = "No repository loaded.";
            return;
        }

        _status.Text = "Loading…";
        RunGit(
            () => _service.LoadRefs(repo),
            listing =>
            {
                _branchList.ItemsSource = listing.Branches.ToList();
                _tagList.ItemsSource = listing.Tags.ToList();
                _status.Text = $"{listing.Branches.Count} branches, {listing.Tags.Count} tags.";
            });
    }

    private void DoCheckout() => _ = DoCheckoutAsync();

    // A clean working tree checks out straight away; a dirty one goes through
    // CheckoutBranchDialog first (don't change / merge / reset / stash).
    private async Task DoCheckoutAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (SelectedRow() is not { } row)
            {
                _status.Text = "Select a branch or tag to checkout.";
                return;
            }

            LocalChangesAction? action = await CheckoutBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, row.Name);

            if (action is not { } changesAction)
            {
                return;
            }

            _status.Text = $"Checking out {row.Name}…";
            RunMutation(() => _service.Checkout(repo, row.Name, changesAction));
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
        }
    }

    private void DoCreateBranch() => _ = DoCreateBranchAsync();

    // Routed through CreateBranchDialog (name + checkout-after-create) instead
    // of the inline name box, so the panel offers the same options as the tree's
    // "Create branch…". The start point still comes from the ref box.
    private async Task DoCreateBranchAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            string start = (_refBox.Text ?? string.Empty).Trim();
            CreateBranchRequest? request = await CreateBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, start.Length > 0 ? start : "HEAD");

            if (request is not { } r)
            {
                return;
            }

            _status.Text = $"Creating branch {r.Name}…";
            RunMutation(() => _service.CreateBranch(repo, r.Name, start, r.Checkout));
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
        }
    }

    private void DoCreateTag() => _ = DoCreateTagAsync();

    // Routed through CreateTagDialog: kind (lightweight / annotated / signed),
    // force, and optional push to a remote.
    private async Task DoCreateTagAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            string commit = (_refBox.Text ?? string.Empty).Trim();
            CreateTagRequest? request = await CreateTagDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, commit.Length > 0 ? commit : "HEAD");

            if (request is not { } r)
            {
                return;
            }

            _status.Text = $"Creating tag {r.Name}…";
            RunMutation(() => _service.CreateTag(repo, r.Name, commit, r.Message, r.Operation, r.SignKeyId, r.Force, r.PushToRemote));
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
        }
    }

    private void DoMerge()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        if (SelectedRow() is not { IsTag: false } row)
        {
            _status.Text = "Select a branch to merge.";
            return;
        }

        _status.Text = $"Merging {row.Name}…";
        RunMutation(() => _service.MergeBranch(repo, row.Name));
    }

    private void DoRebase()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        if (SelectedRow() is not { IsTag: false } row)
        {
            _status.Text = "Select a branch to rebase onto.";
            return;
        }

        _status.Text = $"Rebasing onto {row.Name}…";
        RunMutation(() => _service.RebaseOnto(repo, row.Name));
    }

    private async Task DoDeleteAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (SelectedRow() is not { } row)
            {
                _status.Text = "Select a branch or tag to delete.";
                return;
            }

            if (row.IsCurrent)
            {
                _status.Text = "Cannot delete the current branch.";
                return;
            }

            string kind = row.IsTag ? "tag" : "branch";
            bool confirmed = await ConfirmAsync($"Delete {kind} '{row.Name}'?");
            if (!confirmed)
            {
                return;
            }

            bool force = _forceDelete.IsChecked == true;
            _status.Text = $"Deleting {kind} {row.Name}…";
            RunMutation(() => row.IsTag
                ? _service.DeleteTag(repo, row.Name)
                : _service.DeleteBranch(repo, row.Name, force));
        }
        catch (Exception ex)
        {
            _status.Text = "Failed: " + ex.Message;
        }
    }

    // Runs a mutating op off the UI thread; on success refreshes the ref lists
    // and raises OperationCompleted, otherwise shows the git output.
    private void RunMutation(Func<BranchTagResult> work)
    {
        RunGit(
            work,
            result =>
            {
                if (result.Success)
                {
                    _status.Text = "Done. " + result.Output.Trim();
                    RefreshRefs();
                    OperationCompleted?.Invoke();
                }
                else
                {
                    _status.Text = "Failed: " + result.Output.Trim();
                }
            });
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
        _checkoutButton.IsEnabled = !busy;
        _newBranchButton.IsEnabled = !busy;
        _newTagButton.IsEnabled = !busy;
        _mergeButton.IsEnabled = !busy;
        _rebaseButton.IsEnabled = !busy;
        _deleteButton.IsEnabled = !busy;
        _refreshButton.IsEnabled = !busy;
    }

    // Minimal modal yes/no confirmation. Falls back to allowing the action when
    // no owner window is available (e.g. headless).
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return true;
        }

        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = "Delete", Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Confirm",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
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

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
