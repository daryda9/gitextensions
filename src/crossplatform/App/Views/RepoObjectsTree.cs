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
    private readonly SubmoduleService _submoduleService = new();
    private readonly RemoteService _remoteService = new();

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
                IReadOnlyList<SubmoduleRow> submodules = _submoduleService.ListSubmodules(repo);
                snapshot = new RepoSnapshot(refs, stashes, submodules);
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
        IReadOnlyList<SubmoduleRow> submodules = snapshot.Submodules;

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
        remotesNode.ContextMenu = RemotesRootMenu();
        foreach (IGrouping<string, BranchTagRow> group in remote
                     .GroupBy(RemoteName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            TreeViewItem groupNode = Category(group.Key, "Remote", group.Count());
            groupNode.ContextMenu = RemoteGroupMenu(group.Key);
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

        // Submodules. The root node carries "Update all"; each leaf carries
        // "Update" for its own path. No "Open" action is wired: opening a
        // submodule as the active repository requires MainWindow, which is out
        // of scope for this control.
        TreeViewItem submodulesNode = Category("Submodules", "SubmodulesManage", submodules.Count);
        submodulesNode.ContextMenu = SubmoduleRootMenu();
        foreach (SubmoduleRow row in submodules)
        {
            string label = row.Status switch
            {
                SubmoduleState.NotInitialized => $"{row.Display} (not initialized)",
                SubmoduleState.OutOfDate => $"{row.Display} (out of date)",
                _ => row.Display,
            };
            TreeViewItem leaf = Leaf(label, "FolderSubmodule", row, isCurrent: false);
            leaf.ContextMenu = SubmoduleMenu(row);
            submodulesNode.Items.Add(leaf);
        }

        roots.Add(submodulesNode);

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
            menu.Items.Add(MenuItem("Rename branch…", "BranchRename", () => _ = DoRenameBranchAsync(row)));
            menu.Items.Add(MenuItem("Delete", "BranchDelete", () => _ = DoDeleteBranchAsync(row)));
        }

        return menu;
    }

    private ContextMenu TagMenu(BranchTagRow row)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Delete", "TagDelete", () => _ = DoDeleteTagAsync(row)));
        return menu;
    }

    private ContextMenu StashMenu(StashRow row)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Apply", null, () => RunStash(() => _stashService.StashApply(_repoPath!, row.Name))));
        menu.Items.Add(MenuItem("Pop", null, () => RunStash(() => _stashService.StashPop(_repoPath!, row.Name))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Drop", null, () => _ = DoDropStashAsync(row)));
        return menu;
    }

    private ContextMenu SubmoduleMenu(SubmoduleRow row)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Update", "SubmodulesUpdate", () => RunSubmodule(() => _submoduleService.Update(_repoPath!, row.Path))));
        return menu;
    }

    private ContextMenu SubmoduleRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Update all", "SubmodulesSync", () => RunSubmodule(() => _submoduleService.UpdateAll(_repoPath!))));
        return menu;
    }

    private ContextMenu RemotesRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Manage remotes…", "Remotes", () => _ = DoManageRemotesAsync()));
        return menu;
    }

    private ContextMenu RemoteGroupMenu(string remote)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem("Edit URL…", "Remote", () => _ = DoEditRemoteUrlAsync(remote)));
        menu.Items.Add(MenuItem("Rename…", "Remote", () => _ = DoRenameRemoteAsync(remote)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Remove", "Remove", () => _ = DoRemoveRemoteAsync(remote)));
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

    private async Task DoRenameBranchAsync(BranchTagRow row)
    {
        try
        {
            if (row.IsTag || row.IsRemote)
            {
                return;
            }

            string? newName = await PromptAsync($"Rename branch '{row.Name}' to:", row.Name);
            if (newName is { Length: > 0 } target
                && !string.Equals(target, row.Name, StringComparison.Ordinal))
            {
                RunMutation(() => _branchTagService.RenameBranch(_repoPath!, row.Name, target));
            }
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoDeleteBranchAsync(BranchTagRow row)
    {
        try
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
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoDeleteTagAsync(BranchTagRow row)
    {
        try
        {
            if (await ConfirmAsync($"Delete tag '{row.Name}'?"))
            {
                RunMutation(() => _branchTagService.DeleteTag(_repoPath!, row.Name));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoDropStashAsync(StashRow row)
    {
        try
        {
            if (await ConfirmAsync($"Drop stash '{row.Name}'?"))
            {
                RunStash(() => _stashService.StashDrop(_repoPath!, row.Name));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoManageRemotesAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            RemotesDialog dialog = new(repo);
            await dialog.ShowDialog(owner);
            if (dialog.Changed)
            {
                OperationCompleted?.Invoke();
                Refresh();
            }
        }
        catch
        {
            // No status surface on this control; the dialog simply closes.
        }
    }

    private async Task DoEditRemoteUrlAsync(string remote)
    {
        try
        {
            string current = FindRemoteUrl(remote);
            string? url = await PromptAsync($"URL for remote '{remote}':", current);
            if (url is { Length: > 0 } target && !string.Equals(target, current, StringComparison.Ordinal))
            {
                RunRemote(() => _remoteService.SetRemoteUrl(_repoPath!, remote, target));
            }
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoRenameRemoteAsync(string remote)
    {
        try
        {
            string? name = await PromptAsync($"Rename remote '{remote}' to:", remote);
            if (name is { Length: > 0 } target && !string.Equals(target, remote, StringComparison.Ordinal))
            {
                RunRemote(() => _remoteService.RenameRemote(_repoPath!, remote, target));
            }
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoRemoveRemoteAsync(string remote)
    {
        try
        {
            if (await ConfirmAsync($"Remove remote '{remote}'?"))
            {
                RunRemote(() => _remoteService.RemoveRemote(_repoPath!, remote));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    // Best-effort lookup of a remote's fetch URL to prefill the edit prompt;
    // returns empty when unavailable (the prompt then starts blank).
    private string FindRemoteUrl(string remote)
    {
        try
        {
            return _repoPath is { Length: > 0 } repo
                ? _remoteService.ListRemotes(repo).FirstOrDefault(r => r.Name == remote)?.FetchUrl ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
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

    private void RunRemote(Func<RemoteOpResult> work)
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

    private void RunSubmodule(Func<SubmoduleOpResult> work)
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

    // Minimal modal text prompt mirroring ConfirmAsync; returns the entered text,
    // or null when cancelled / no owner window is available (e.g. headless).
    private async Task<string?> PromptAsync(string message, string initial)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        TaskCompletionSource<string?> tcs = new();

        TextBox input = new() { Text = initial };
        Button ok = new() { Content = "OK", Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Rename",
            Width = 340,
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

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private sealed record RepoSnapshot(BranchTagListing Refs, IReadOnlyList<StashRow> Stashes, IReadOnlyList<SubmoduleRow> Submodules);
}
