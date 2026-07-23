using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Left-hand repository-objects tree for the Avalonia port, mirroring the
///  original <c>GitUI/LeftPanel/RepoObjectsTree</c>: a single <see cref="TreeView"/>
///  with top-level category nodes — Branches (local, current marked), Remotes
///  (remote branches grouped by remote), Tags and Stashes — each showing an icon
///  and a count. Double-click / Enter on a local branch checks it out; right-click
///  context menus offer checkout / merge / rebase / delete on branches, delete on
///  tags and apply / pop / drop on stashes. All git work runs off the UI thread
///  via <see cref="Task.Run"/> and marshals back with <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class RepoObjectsTree : UserControl
{
    private readonly BranchTagService _branchTagService = new();
    private readonly StashOpsService _stashService = new();

    private readonly TreeView _tree;

    private string? _repoPath;
    private bool _busy;

    /// <summary>
    ///  Raised on the UI thread when a branch or tag node is selected, carrying the
    ///  full ObjectId / hash of the ref so the host can highlight it in the
    ///  revision grid. Not raised for nodes without a resolvable ObjectId.
    /// </summary>
    public event Action<string>? RefSelected;

    /// <summary>
    ///  Raised on the UI thread after any successful mutating operation (checkout,
    ///  merge, rebase, delete, stash apply / pop / drop) so the host can refresh.
    /// </summary>
    public event Action? OperationCompleted;

    public RepoObjectsTree()
    {
        _tree = new TreeView
        {
            Background = Brushes.Transparent,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        _tree.SelectionChanged += (_, _) => OnSelectionChanged();
        _tree.DoubleTapped += (_, _) => OnActivate();
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OnActivate();
                e.Handled = true;
            }
        };

        Background = Brush("App.Panel", Brushes.Transparent);
        Content = _tree;
    }

    /// <summary>
    ///  Points the tree at <paramref name="repoPath"/> and loads its objects.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        Refresh();
    }

    /// <summary>
    ///  Reloads all categories for the current repository.
    /// </summary>
    public void Refresh()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _tree.ItemsSource = null;
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            RepoSnapshot? snapshot = null;
            string? error = null;
            try
            {
                BranchTagListing refs = _branchTagService.LoadRefs(repo);
                IReadOnlyList<StashRow> stashes = _stashService.ListStashes(repo);
                snapshot = new RepoSnapshot(refs, stashes);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (snapshot is not null)
                {
                    BuildTree(snapshot);
                }
                else
                {
                    _tree.ItemsSource = new[] { Category($"Error: {error}", null, null) };
                }
            });
        });
    }

    private void BuildTree(RepoSnapshot snapshot)
    {
        List<BranchTagRow> local = [];
        List<BranchTagRow> remote = [];
        foreach (BranchTagRow row in snapshot.Refs.Branches)
        {
            (row.IsRemote ? remote : local).Add(row);
        }

        IReadOnlyList<BranchTagRow> tags = snapshot.Refs.Tags;
        IReadOnlyList<StashRow> stashes = snapshot.Stashes;

        List<TreeViewItem> roots = [];

        // Branches (local).
        TreeViewItem branchesNode = Category("Branches", "Branch", local.Count);
        foreach (BranchTagRow row in local.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            string label = row.IsCurrent ? $"✓ {row.Name}" : row.Name;
            TreeViewItem leaf = Leaf(label, "BranchLocal", row, row.IsCurrent);
            leaf.ContextMenu = BranchMenu(row);
            branchesNode.Items.Add(leaf);
        }

        roots.Add(branchesNode);

        // Remotes (remote branches grouped by remote name, e.g. "origin/...").
        TreeViewItem remotesNode = Category("Remotes", "Remotes", remote.Count);
        foreach (IGrouping<string, BranchTagRow> group in remote
                     .GroupBy(RemoteName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            TreeViewItem groupNode = Category(group.Key, "Remote", group.Count());
            foreach (BranchTagRow row in group.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                string label = ShortRemoteName(row.Name, group.Key);
                TreeViewItem leaf = Leaf(label, "BranchRemote", row, isCurrent: false);
                leaf.ContextMenu = BranchMenu(row);
                groupNode.Items.Add(leaf);
            }

            remotesNode.Items.Add(groupNode);
        }

        roots.Add(remotesNode);

        // Tags.
        TreeViewItem tagsNode = Category("Tags", "Tag", tags.Count);
        foreach (BranchTagRow row in tags.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            TreeViewItem leaf = Leaf(row.Name, "Tag", row, isCurrent: false);
            leaf.ContextMenu = TagMenu(row);
            tagsNode.Items.Add(leaf);
        }

        roots.Add(tagsNode);

        // Stashes.
        TreeViewItem stashesNode = Category("Stashes", "stash", stashes.Count);
        foreach (StashRow row in stashes)
        {
            TreeViewItem leaf = Leaf($"{row.Name}: {row.Message}", "stash", row, isCurrent: false);
            leaf.ContextMenu = StashMenu(row);
            stashesNode.Items.Add(leaf);
        }

        roots.Add(stashesNode);

        branchesNode.IsExpanded = true;
        _tree.ItemsSource = roots;
    }

    private static string RemoteName(BranchTagRow row)
    {
        int slash = row.Name.IndexOf('/');
        return slash > 0 ? row.Name[..slash] : "remote";
    }

    private static string ShortRemoteName(string name, string remote)
        => name.StartsWith(remote + "/", StringComparison.Ordinal) ? name[(remote.Length + 1)..] : name;

    // --- Node construction ------------------------------------------------

    private TreeViewItem Category(string text, string? icon, int? count)
    {
        string header = count is { } c ? $"{text} ({c})" : text;
        return new TreeViewItem
        {
            Header = HeaderPanel(header, icon, bold: true),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
    }

    private TreeViewItem Leaf(string text, string? icon, object tag, bool isCurrent)
        => new()
        {
            Header = HeaderPanel(text, icon, bold: isCurrent),
            Tag = tag,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

    private static Control HeaderPanel(string text, string? icon, bool bold)
    {
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (icon is not null && IconLoader.Image(icon, 16) is { } img)
        {
            img.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(img);
        }

        panel.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        });

        return panel;
    }

    // --- Context menus ----------------------------------------------------

    private ContextMenu BranchMenu(BranchTagRow row)
    {
        ContextMenu menu = new();
        if (!row.IsRemote)
        {
            menu.Items.Add(MenuItem("Checkout", "BranchCheckout", () => DoCheckout(row)));
        }

        menu.Items.Add(MenuItem("Merge into current", "Merge", () => RunMutation(() => _branchTagService.MergeBranch(_repoPath!, row.Name))));
        menu.Items.Add(MenuItem("Rebase current onto", "Rebase", () => RunMutation(() => _branchTagService.RebaseOnto(_repoPath!, row.Name))));

        if (!row.IsRemote)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Delete", "BranchDelete", () => DoDeleteBranch(row)));
        }

        return menu;
    }

    private ContextMenu TagMenu(BranchTagRow row)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Delete", "TagDelete", () => DoDeleteTag(row)));
        return menu;
    }

    private ContextMenu StashMenu(StashRow row)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Apply", null, () => RunStash(() => _stashService.StashApply(_repoPath!, row.Name))));
        menu.Items.Add(MenuItem("Pop", null, () => RunStash(() => _stashService.StashPop(_repoPath!, row.Name))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Drop", null, () => DoDropStash(row)));
        return menu;
    }

    private static MenuItem MenuItem(string text, string? icon, Action onClick)
    {
        MenuItem item = new() { Header = text };
        if (icon is not null && IconLoader.Image(icon, 16) is { } img)
        {
            item.Icon = img;
        }

        item.Click += (_, _) => onClick();
        return item;
    }

    // --- Interactions -----------------------------------------------------

    private void OnSelectionChanged()
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: BranchTagRow row } && row.ObjectId.Length > 0)
        {
            RefSelected?.Invoke(row.ObjectId);
        }
    }

    private void OnActivate()
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: BranchTagRow { IsTag: false, IsRemote: false } row })
        {
            DoCheckout(row);
        }
    }

    private void DoCheckout(BranchTagRow row)
        => RunMutation(() => _branchTagService.Checkout(_repoPath!, row.Name));

    private async void DoDeleteBranch(BranchTagRow row)
    {
        if (row.IsCurrent)
        {
            return;
        }

        if (await ConfirmAsync($"Delete branch '{row.Name}'?"))
        {
            RunMutation(() => _branchTagService.DeleteBranch(_repoPath!, row.Name, force: false));
        }
    }

    private async void DoDeleteTag(BranchTagRow row)
    {
        if (await ConfirmAsync($"Delete tag '{row.Name}'?"))
        {
            RunMutation(() => _branchTagService.DeleteTag(_repoPath!, row.Name));
        }
    }

    private async void DoDropStash(StashRow row)
    {
        if (await ConfirmAsync($"Drop stash '{row.Name}'?"))
        {
            RunStash(() => _stashService.StashDrop(_repoPath!, row.Name));
        }
    }

    // --- Mutation plumbing ------------------------------------------------

    private void RunMutation(Func<BranchTagResult> work)
    {
        if (_repoPath is not { Length: > 0 } || _busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            bool success;
            try
            {
                success = work().Success;
            }
            catch
            {
                success = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (success)
                {
                    OperationCompleted?.Invoke();
                    Refresh();
                }
            });
        });
    }

    private void RunStash(Func<StashOpResult> work)
    {
        if (_repoPath is not { Length: > 0 } || _busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            bool success;
            try
            {
                success = work().Success;
            }
            catch
            {
                success = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (success)
                {
                    OperationCompleted?.Invoke();
                    Refresh();
                }
            });
        });
    }

    // Minimal modal yes/no confirmation; allows the action when no owner window
    // is available (e.g. headless).
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return true;
        }

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

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private sealed record RepoSnapshot(BranchTagListing Refs, IReadOnlyList<StashRow> Stashes);
}
