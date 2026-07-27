using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;
using GitExtensions.Extensibility.Git;

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
    // "<category> (<count>)" — a format, not a concatenation, so a translation is
    // free to place the count differently.
    private const string CategoryCountFormat = "{0} ({1})";

    private readonly BranchTagService _branchTagService = new();
    private readonly StashOpsService _stashService = new();
    private readonly SubmoduleService _submoduleService = new();
    private readonly RemoteService _remoteService = new();
    private readonly WorktreeService _worktreeService = new();

    private readonly TreeView _tree;

    private string? _repoPath;
    private bool _busy;

    // --- Session-local ref ordering state --------------------------------
    // All sorting/reordering below is view-only: it reorders the displayed
    // child nodes for the current session and never touches git. The last
    // loaded snapshot is retained so a re-sort can rebuild the tree without
    // re-listing refs.
    private enum RefSortKey { Name, CommitDate }
    private enum RefSortOrder { Ascending, Descending }

    private RefSortKey _sortKey = RefSortKey.Name;
    private RefSortOrder _sortOrder = RefSortOrder.Ascending;

    private RepoSnapshot? _snapshot;

    // Commit dates resolved lazily (only when the user picks "sort by commit
    // date"), keyed by full ObjectId, cached for the session.
    private readonly Dictionary<string, DateTime> _commitDates = new(StringComparer.Ordinal);
    private bool _resolvingDates;

    // Manual local-branch order (branch names) engaged by Move up / Move down.
    // When set it takes precedence over _sortKey/_sortOrder for local branches;
    // choosing any explicit sort clears it.
    private List<string>? _manualBranchOrder;

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

    /// <summary>
    ///  Raised on the UI thread when the user chooses "Open" on a submodule or
    ///  worktree node, carrying the absolute path to open as the active
    ///  repository. The host (MainWindow) subscribes and routes it to its own
    ///  OpenRepository — the tree never references MainWindow directly.
    /// </summary>
    public event Action<string>? OpenRepositoryRequested;

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

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // A language switch re-labels the tree from the snapshot already in memory —
    // no git, no I/O, so this is safe to do straight on the UI thread once posted.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_snapshot is { } snapshot)
        {
            BuildTree(snapshot);
        }
    });

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
                IReadOnlyList<WorktreeRow> worktrees = _worktreeService.ListWorktrees(repo);
                snapshot = new RepoSnapshot(refs, stashes, submodules, worktrees);
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
                    _tree.ItemsSource = new[]
                    {
                        Category(string.Format(CultureInfo.CurrentCulture, "{0}: {1}", T("TranslatedStrings/_error.Text", "Error"), error), null, null),
                    };
                }
            });
        });
    }

    private void BuildTree(RepoSnapshot snapshot)
    {
        _snapshot = snapshot;

        List<BranchTagRow> local = [];
        List<BranchTagRow> remote = [];
        foreach (BranchTagRow row in snapshot.Refs.Branches)
        {
            (row.IsRemote ? remote : local).Add(row);
        }

        IReadOnlyList<BranchTagRow> tags = snapshot.Refs.Tags;
        IReadOnlyList<StashRow> stashes = snapshot.Stashes;
        IReadOnlyList<SubmoduleRow> submodules = snapshot.Submodules;
        IReadOnlyList<WorktreeRow> worktrees = snapshot.Worktrees;

        List<TreeViewItem> roots = [];

        // Branches (local).
        TreeViewItem branchesNode = Category(T("RepoObjectsTree/tsbShowBranches.ToolTipText", "Branches"), "Branch", local.Count);
        branchesNode.ContextMenu = RefSortMenu(BranchesRootItems());
        foreach (BranchTagRow row in OrderLocalBranches(local))
        {
            string label = row.IsCurrent ? $"✓ {row.Name}" : row.Name;
            TreeViewItem leaf = Leaf(label, "BranchLocal", row, row.IsCurrent);
            leaf.ContextMenu = BranchMenu(row);
            branchesNode.Items.Add(leaf);
        }

        roots.Add(branchesNode);

        // Remotes (remote branches grouped by remote name, e.g. "origin/...").
        TreeViewItem remotesNode = Category(T("RepoObjectsTree/tsbShowRemotes.ToolTipText", "Remotes"), "Remotes", remote.Count);
        remotesNode.ContextMenu = RemotesRootMenu();
        foreach (IGrouping<string, BranchTagRow> group in remote
                     .GroupBy(RemoteName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            TreeViewItem groupNode = Category(group.Key, "Remote", group.Count());
            groupNode.ContextMenu = RemoteGroupMenu(group.Key);
            foreach (BranchTagRow row in SortRefs(group))
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
        TreeViewItem tagsNode = Category(T("RepoObjectsTree/tsbShowTags.ToolTipText", "Tags"), "Tag", tags.Count);
        tagsNode.ContextMenu = RefSortMenu(TagsRootItems());
        foreach (BranchTagRow row in SortRefs(tags))
        {
            TreeViewItem leaf = Leaf(row.Name, "Tag", row, isCurrent: false);
            leaf.ContextMenu = TagMenu(row);
            tagsNode.Items.Add(leaf);
        }

        roots.Add(tagsNode);

        // Stashes.
        TreeViewItem stashesNode = Category(T("RepoObjectsTree/tsbShowStashes.ToolTipText", "Stashes"), "stash", stashes.Count);
        foreach (StashRow row in stashes)
        {
            TreeViewItem leaf = Leaf($"{row.Name}: {row.Message}", "stash", row, isCurrent: false);
            leaf.ContextMenu = StashMenu(row);
            stashesNode.Items.Add(leaf);
        }

        roots.Add(stashesNode);

        // Submodules. The root node carries "Update all"; each leaf carries
        // "Open" (open the submodule as the active repository, via
        // OpenRepositoryRequested) plus "Update" for its own path.
        TreeViewItem submodulesNode = Category(T("RepoObjectsTree/tsbShowSubmodules.ToolTipText", "Submodules"), "SubmodulesManage", submodules.Count);
        submodulesNode.ContextMenu = SubmoduleRootMenu();
        foreach (SubmoduleRow row in submodules)
        {
            string label = row.Status switch
            {
                SubmoduleState.NotInitialized => TF("{0} (not initialized)", row.Display),
                SubmoduleState.OutOfDate => TF("{0} (out of date)", row.Display),
                _ => row.Display,
            };
            TreeViewItem leaf = Leaf(label, "FolderSubmodule", row, isCurrent: false);
            leaf.ContextMenu = SubmoduleMenu(row);
            submodulesNode.Items.Add(leaf);
        }

        roots.Add(submodulesNode);

        // Worktrees. The root node carries "Add…" and "Prune"; each leaf carries
        // "Open" (open the worktree as the active repository, via
        // OpenRepositoryRequested) plus "Remove" for its own path.
        TreeViewItem worktreesNode = Category(T("RepoObjectsTree/tsbShowWorktrees.ToolTipText", "Worktrees"), "WorkTree", worktrees.Count);
        worktreesNode.ContextMenu = WorktreeRootMenu();
        foreach (WorktreeRow row in worktrees)
        {
            TreeViewItem leaf = Leaf(row.Display, "WorkTree", row, isCurrent: false);
            leaf.ContextMenu = WorktreeMenu(row);
            worktreesNode.Items.Add(leaf);
        }

        roots.Add(worktreesNode);

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
        string header = count is { } c ? string.Format(CultureInfo.CurrentCulture, CategoryCountFormat, text, c) : text;
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
            menu.Items.Add(MenuItem(T("BranchMenuItemsStrings/Checkout.Text", "Checkout"), "BranchCheckout", () => DoCheckout(row)));
        }

        menu.Items.Add(MenuItem(T("MenuItemsStrings/Merge.Text", "Merge into current"), "Merge", () => RunMutation(() => _branchTagService.MergeBranch(_repoPath!, row.Name))));
        menu.Items.Add(MenuItem(T("BranchMenuItemsStrings/Rebase.Text", "Rebase current onto"), "Rebase", () => RunMutation(() => _branchTagService.RebaseOnto(_repoPath!, row.Name))));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Name)));

        if (!row.IsRemote)
        {
            menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnMoveUp.Text", "Move up"), null, () => MoveBranch(row, up: true)));
            menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnMoveDown.Text", "Move down"), null, () => MoveBranch(row, up: false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(T("MenuItemsStrings/Rename.Text", "Rename branch…"), "BranchRename", () => _ = DoRenameBranchAsync(row)));
            menu.Items.Add(MenuItem(T("BranchMenuItemsStrings/Delete.Text", "Delete"), "BranchDelete", () => _ = DoDeleteBranchAsync(row)));
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(T("RemoteBranchMenuItemsStrings/Delete.Text", "Delete remote branch…"), "BranchDelete", () => _ = DoDeleteRemoteBranchAsync(row)));
        }

        return menu;
    }

    private ContextMenu TagMenu(BranchTagRow row)
    {
        ContextMenu menu = new();
        // Checkout the tag ref: a plain `git checkout <tag>` lands on a detached
        // HEAD, which is the expected "checkout tag revision" behaviour. Reuses
        // the same BranchTagService.Checkout used for branches/revisions.
        menu.Items.Add(MenuItem(T("TagMenuItemsStrings/Checkout.Text", "Checkout tag revision…"), "BranchCheckout", () => DoCheckout(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("TagMenuItemsStrings/Delete.Text", "Delete"), "TagDelete", () => _ = DoDeleteTagAsync(row)));
        return menu;
    }

    private ContextMenu WorktreeRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnManageWorktreesFromRootNode.Text", "Manage worktrees…"), "WorkTree", () => _ = DoManageWorktreesAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnCreateWorktreeFromRootNode.Text", "Add…"), "WorkTree", () => _ = DoAddWorktreeAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnPruneWorktreesFromRootNode.Text", "Prune"), "CleanupRepo", () => RunWorktree(() => _worktreeService.PruneWorktrees(_repoPath!))));
        return menu;
    }

    private ContextMenu WorktreeMenu(WorktreeRow row)
    {
        ContextMenu menu = new();
        // "Open" makes the worktree the active repository, routed to the host via
        // OpenRepositoryRequested (the tree never references MainWindow directly).
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnOpenWorktree.Text", "Open"), "RepoOpen", () => OpenRepositoryRequested?.Invoke(row.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Branch.Length > 0 ? row.Branch : System.IO.Path.GetFileName(row.Path.TrimEnd('/', '\\')))));
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnCopyWorktreePath.Text", "Copy path"), "CopyToClipboard", () => CopyText(row.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnDeleteWorktree.Text", "Remove"), "Remove", () => _ = DoRemoveWorktreeAsync(row)));
        return menu;
    }

    private ContextMenu StashMenu(StashRow row)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnApplyStash.Text", "Apply"), null, () => RunStash(() => _stashService.StashApply(_repoPath!, row.Name))));
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnPopStash.Text", "Pop"), null, () => RunStash(() => _stashService.StashPop(_repoPath!, row.Name))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnDropStash.Text", "Drop"), null, () => _ = DoDropStashAsync(row)));
        return menu;
    }

    private ContextMenu SubmoduleMenu(SubmoduleRow row)
    {
        ContextMenu menu = new();
        // "Open" makes the submodule the active repository, routed to the host via
        // OpenRepositoryRequested (the tree never references MainWindow directly).
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnOpenSubmodule.Text", "Open"), "RepoOpen", () => OpenRepositoryRequested?.Invoke(SubmoduleFullPath(row))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnUpdateSubmodule.Text", "Update"), "SubmodulesUpdate", () => RunSubmodule(() => _submoduleService.Update(_repoPath!, row.Path))));
        menu.Items.Add(MenuItem(T("Update (merge)…"), "Merge", () => _ = DoMergeSubmoduleAsync(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(row.Path)));
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnubtnCopyWorktreePath.Text", "Copy path"), "CopyToClipboard", () => CopyText(SubmoduleFullPath(row))));
        return menu;
    }

    private ContextMenu SubmoduleRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("FormBrowse/manageSubmodulesToolStripMenuItem.Text", "Manage submodules…"), "SubmodulesManage", () => _ = DoManageSubmodulesAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("FormBrowse/updateAllSubmodulesToolStripMenuItem.Text", "Update all"), "SubmodulesSync", () => RunSubmodule(() => _submoduleService.UpdateAll(_repoPath!))));
        return menu;
    }

    private ContextMenu RemotesRootMenu()
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("RepoObjectsTree/mnuBtnManageRemotesFromRootNode.ToolTipText", "Manage remotes…"), "Remotes", () => _ = DoManageRemotesAsync()));
        return menu;
    }

    private ContextMenu RemoteGroupMenu(string remote)
    {
        ContextMenu menu = new();
        menu.Items.Add(MenuItem(T("Copy name"), "CopyToClipboard", () => CopyText(remote)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("Edit URL…"), "Remote", () => _ = DoEditRemoteUrlAsync(remote)));
        menu.Items.Add(MenuItem(T("TranslatedStrings/_actionRename.Text", "Rename…"), "Remote", () => _ = DoRenameRemoteAsync(remote)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(T("FormSubmodules/RemoveSubmodule.Text", "Remove"), "Remove", () => _ = DoRemoveRemoteAsync(remote)));
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

    // --- Ref sorting / reordering (view-only) -----------------------------

    // Sort submenu attached to the Branches and Tags root nodes. Rebuilt on
    // every BuildTree so the ✓ markers reflect the current session settings.
    private ContextMenu RefSortMenu(IReadOnlyList<Control>? leadingItems = null)
    {
        ContextMenu menu = new();
        if (leadingItems is { Count: > 0 })
        {
            foreach (Control item in leadingItems)
            {
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
        }

        menu.Items.Add(SortKeyItem(T("Sort by name"), RefSortKey.Name));
        menu.Items.Add(SortKeyItem(T("Sort by commit date"), RefSortKey.CommitDate));
        menu.Items.Add(new Separator());
        menu.Items.Add(SortOrderItem(T("Ascending"), RefSortOrder.Ascending));
        menu.Items.Add(SortOrderItem(T("Descending"), RefSortOrder.Descending));
        return menu;
    }

    // "Create branch…" / "Create tag…" on the Branches / Tags root nodes: the
    // proper modals (name + checkout-after-create, name + kind/force/push),
    // not the bare text prompts.
    private IReadOnlyList<Control> BranchesRootItems()
        => [MenuItem(T("FormCreateBranch/$this.Text", "Create branch") + "…", "BranchCreate", () => _ = DoCreateBranchAsync())];

    private IReadOnlyList<Control> TagsRootItems()
        => [MenuItem(T("FormCreateTag/$this.Text", "Create tag") + "…", "TagCreate", () => _ = DoCreateTagAsync())];

    private MenuItem SortKeyItem(string text, RefSortKey key)
        => MenuItem(_sortKey == key ? "✓ " + text : "    " + text, null, () => SetSort(key, _sortOrder));

    private MenuItem SortOrderItem(string text, RefSortOrder order)
        => MenuItem(_sortOrder == order ? "✓ " + text : "    " + text, null, () => SetSort(_sortKey, order));

    // Applies new session-local sort settings and rebuilds the tree from the
    // retained snapshot. Choosing an explicit sort clears any manual order.
    private void SetSort(RefSortKey key, RefSortOrder order)
    {
        _sortKey = key;
        _sortOrder = order;
        _manualBranchOrder = null;

        if (_snapshot is not { } snapshot)
        {
            return;
        }

        if (key == RefSortKey.CommitDate)
        {
            EnsureDatesThenRebuild(snapshot);
        }
        else
        {
            BuildTree(snapshot);
        }
    }

    // Resolves any missing commit dates off the UI thread (via the reused core
    // module), caches them for the session, then rebuilds. Read-only work; a
    // reentrancy flag avoids overlapping resolves.
    private void EnsureDatesThenRebuild(RepoSnapshot snapshot)
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            BuildTree(snapshot);
            return;
        }

        List<string> missing = snapshot.Refs.Branches
            .Concat(snapshot.Refs.Tags)
            .Select(r => r.ObjectId)
            .Where(oid => oid.Length > 0 && !_commitDates.ContainsKey(oid))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0 || _resolvingDates)
        {
            BuildTree(snapshot);
            return;
        }

        _resolvingDates = true;
        _ = Task.Run(() =>
        {
            Dictionary<string, DateTime> resolved = new(StringComparer.Ordinal);
            try
            {
                GitModule module = GitContext.CreateModule(repo);
                foreach (string oid in missing)
                {
                    try
                    {
                        resolved[oid] = module.GetRevision(ObjectId.Parse(oid), shortFormat: true).CommitDate;
                    }
                    catch
                    {
                        // Unresolvable ref (e.g. annotated-tag object): leave it
                        // out so DateFor falls back to DateTime.MinValue.
                    }
                }
            }
            catch
            {
                // Module creation failed; rebuild with whatever is cached.
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach ((string oid, DateTime date) in resolved)
                {
                    _commitDates[oid] = date;
                }

                _resolvingDates = false;
                BuildTree(snapshot);
            });
        });
    }

    // Local-branch order: manual order (if engaged) wins, otherwise the current
    // sort settings.
    private IEnumerable<BranchTagRow> OrderLocalBranches(IReadOnlyList<BranchTagRow> local)
    {
        if (_manualBranchOrder is { Count: > 0 } order)
        {
            return local.OrderBy(r =>
            {
                int i = order.IndexOf(r.Name);
                return i < 0 ? int.MaxValue : i;
            }).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        }

        return SortRefs(local);
    }

    private List<BranchTagRow> SortRefs(IEnumerable<BranchTagRow> rows)
    {
        bool asc = _sortOrder == RefSortOrder.Ascending;
        if (_sortKey == RefSortKey.CommitDate)
        {
            return (asc
                    ? rows.OrderBy(DateFor).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(DateFor).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return (asc
                ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private DateTime DateFor(BranchTagRow row)
        => row.ObjectId.Length > 0 && _commitDates.TryGetValue(row.ObjectId, out DateTime d) ? d : DateTime.MinValue;

    // Moves a local branch up/down in the displayed order (session-local visual
    // only). Engages manual order, seeding it from the currently displayed order
    // so the first move is relative to what the user sees.
    private void MoveBranch(BranchTagRow row, bool up)
    {
        if (row.IsRemote || row.IsTag || _snapshot is not { } snapshot)
        {
            return;
        }

        List<BranchTagRow> local = snapshot.Refs.Branches.Where(r => !r.IsRemote).ToList();
        if (local.Count < 2)
        {
            return;
        }

        _manualBranchOrder ??= OrderLocalBranches(local).Select(r => r.Name).ToList();

        int index = _manualBranchOrder.IndexOf(row.Name);
        int target = up ? index - 1 : index + 1;
        if (index < 0 || target < 0 || target >= _manualBranchOrder.Count)
        {
            return;
        }

        (_manualBranchOrder[index], _manualBranchOrder[target]) = (_manualBranchOrder[target], _manualBranchOrder[index]);
        BuildTree(snapshot);
    }

    // Fire-and-forget copy to the system clipboard via the Avalonia TopLevel.
    private void CopyText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        }
    }

    // Absolute filesystem path of a submodule (its Path is repo-relative).
    private string SubmoduleFullPath(SubmoduleRow row)
        => _repoPath is { Length: > 0 } repo
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(repo, row.Path))
            : row.Path;

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

    private void DoCheckout(BranchTagRow row) => _ = DoCheckoutAsync(row);

    // Checkout with the upstream "local changes" semantics: a clean working tree
    // checks out straight away (no dialog), a dirty one first asks what to do with
    // the pending changes (don't change / merge / reset / stash). The probe and the
    // git work both run off the UI thread — the services block on async work.
    private async Task DoCheckoutAsync(BranchTagRow row)
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || _busy)
            {
                return;
            }

            LocalChangesAction? action = await CheckoutBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, row.Name);

            if (action is not { } changesAction)
            {
                return;
            }

            RunMutation(() => _branchTagService.Checkout(repo, row.Name, changesAction));
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    // --- Create branch / tag ----------------------------------------------

    private async Task DoCreateBranchAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || _busy)
            {
                return;
            }

            CreateBranchRequest? request = await CreateBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, T("HEAD (current revision)"));

            if (request is not { } r)
            {
                return;
            }

            RunMutation(() => _branchTagService.CreateBranch(repo, r.Name, startPoint: "HEAD", r.Checkout));
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    private async Task DoCreateTagAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || _busy)
            {
                return;
            }

            CreateTagRequest? request = await CreateTagDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, T("HEAD (current revision)"));

            if (request is not { } r)
            {
                return;
            }

            RunMutation(() => _branchTagService.CreateTag(
                repo, r.Name, commit: "HEAD", r.Message, r.Operation, r.SignKeyId, r.Force, r.PushToRemote));
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    private async Task DoRenameBranchAsync(BranchTagRow row)
    {
        try
        {
            if (row.IsTag || row.IsRemote)
            {
                return;
            }

            string? newName = await PromptAsync(TF("Rename branch '{0}' to:", row.Name), row.Name, T("FormRenameBranch/$this.Text", "Rename branch"));
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

            if (await ConfirmAsync(TF("Delete branch '{0}'?", row.Name)))
            {
                RunMutation(() => _branchTagService.DeleteBranch(_repoPath!, row.Name, force: false));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoDeleteRemoteBranchAsync(BranchTagRow row)
    {
        try
        {
            if (!row.IsRemote)
            {
                return;
            }

            string remote = RemoteName(row);
            string branch = ShortRemoteName(row.Name, remote);
            if (remote.Length == 0 || branch.Length == 0)
            {
                return;
            }

            // Destructive and affects the remote: confirm before pushing the delete.
            if (await ConfirmAsync(TF("Delete branch '{0}' on remote '{1}'?", branch, remote)
                + "\n" + T("Deleting a branch on the remote cannot be undone.")))
            {
                RunMutation(() => _branchTagService.DeleteRemoteBranch(_repoPath!, remote, branch));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoMergeSubmoduleAsync(SubmoduleRow row)
    {
        try
        {
            if (await ConfirmAsync(TF("Update submodule '{0}' to its remote branch and merge into the current checkout?", row.Path)))
            {
                RunSubmodule(() => _submoduleService.UpdateMerge(_repoPath!, row.Path));
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
            if (await ConfirmAsync(TF("Delete tag '{0}'?", row.Name)))
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
            if (await ConfirmAsync(TF("Drop stash '{0}'?", row.Name)))
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

    private async Task DoManageSubmodulesAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            SubmodulesDialog dialog = new(repo);
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
            // Off the UI thread: the lookup shells out to git (see RemoteService).
            string current = await Task.Run(() => FindRemoteUrl(remote));
            string? url = await PromptAsync(TF("URL for remote '{0}':", remote), current, T("Edit URL"));
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
            string? name = await PromptAsync(TF("Rename remote '{0}' to:", remote), remote, T("TranslatedStrings/_actionRename.Text", "Rename"));
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
            if (await ConfirmAsync(TF("Remove remote '{0}'?", remote)))
            {
                RunRemote(() => _remoteService.RemoveRemote(_repoPath!, remote));
            }
        }
        catch
        {
            // No status surface on this control; the confirm/mutation simply aborts.
        }
    }

    private async Task DoManageWorktreesAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            WorktreesDialog dialog = new(repo);
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

    private async Task DoAddWorktreeAsync()
    {
        try
        {
            string? path = await PromptAsync(T("FormCreateWorktree/lblNewWorktreeFolder.Text", "New worktree path:"), string.Empty, T("FormCreateWorktree/$this.Text", "Create a new worktree"));
            if (path is not { Length: > 0 } target)
            {
                return;
            }

            // Branch is optional: empty lets git create a branch named after the path.
            string? branch = await PromptAsync(TF("Branch/revision for '{0}' (blank = new branch):", target), string.Empty, T("FormCreateWorktree/$this.Text", "Create a new worktree"));
            RunWorktree(() => _worktreeService.AddWorktree(_repoPath!, target, branch ?? string.Empty));
        }
        catch
        {
            // No status surface on this control; the prompt/mutation simply aborts.
        }
    }

    private async Task DoRemoveWorktreeAsync(WorktreeRow row)
    {
        try
        {
            if (await ConfirmAsync(TF("Remove worktree '{0}'?", row.Path)))
            {
                RunWorktree(() => _worktreeService.RemoveWorktree(_repoPath!, row.Path));
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

    private void RunWorktree(Func<WorktreeOpResult> work)
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

        // Yes/No rather than the former Confirm/Cancel: both words have a real
        // trans-unit upstream, and it matches the shell's own confirmation dialog.
        Button yes = new() { Content = T("TranslatedStrings/_yes.Text", "Yes"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("TranslatedStrings/_no.Text", "No") };
        Window dialog = new()
        {
            Title = T("Confirm"),
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
    // <paramref name="title"/> is the window caption: it used to be hard-coded to
    // "Rename", which was wrong for the worktree and remote-URL prompts and had no
    // sensible translation key either.
    private async Task<string?> PromptAsync(string message, string initial, string title)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        TaskCompletionSource<string?> tcs = new();

        TextBox input = new() { Text = initial };
        Button ok = new() { Content = T("OK"), Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Window dialog = new()
        {
            Title = title,
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

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // Short alias for the composite-format overload; data (branch, remote, stash
    // names) always goes through a placeholder, never string concatenation.
    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(null, englishFormat, args);

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private sealed record RepoSnapshot(BranchTagListing Refs, IReadOnlyList<StashRow> Stashes, IReadOnlyList<SubmoduleRow> Submodules, IReadOnlyList<WorktreeRow> Worktrees);
}
