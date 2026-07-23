using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Views;

namespace GitExtensions.Avalonia;

/// <summary>
///  Integrated main window modelled on the original GitExtensions FormBrowse:
///  a top toolbar, a left repository-objects tree (branches/remotes/tags/
///  stashes), the revision-grid DAG in the centre, a bottom detail/diff +
///  working-directory panel, and a status bar. All views are self-contained
///  <see cref="UserControl"/>s driven over the reused core via <see cref="GitContext"/>.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MainToolbar _toolbar = new();
    private readonly StatusBarView _statusBar = new();
    private readonly RepoObjectsTree _tree = new();
    private readonly RevisionGridView _revisions = new();
    private readonly CommitDetailView _detail = new();
    private readonly DiffView _diff = new();
    private readonly WorkingDirectoryView _workingDir = new();

    private readonly TabControl _bottom;
    private readonly TabItem _commitInfoTab;
    private readonly TabItem _workingDirTab;
    private readonly TabItem _blameTab;
    private readonly TabItem _historyTab;
    private readonly BlameView _blame = new();
    private readonly FileHistoryView _fileHistory = new();

    private readonly StashOpsService _stashOps = new();

    private string? _repoPath;

    public MainWindow()
    {
        Title = "Git Extensions (Avalonia / Linux)";
        Width = 1280;
        Height = 820;
        Background = (IBrush)Resources["App.Window"]!;

        // ---- bottom panel: commit info (detail + diff) / working dir / blame / history
        Grid commitInfo = new() { RowDefinitions = new RowDefinitions("2*,4,3*") };
        GridSplitter infoSplit = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(_detail, 0);
        Grid.SetRow(infoSplit, 1);
        Grid.SetRow(_diff, 2);
        commitInfo.Children.Add(_detail);
        commitInfo.Children.Add(infoSplit);
        commitInfo.Children.Add(_diff);

        _commitInfoTab = new TabItem { Header = "Commit", Content = commitInfo };
        _workingDirTab = new TabItem { Header = "Working directory", Content = _workingDir };
        _blameTab = new TabItem { Header = "Blame", Content = _blame };
        _historyTab = new TabItem { Header = "File history", Content = _fileHistory };
        _bottom = new TabControl
        {
            Background = (IBrush)Resources["App.Window"]!,
            ClipToBounds = true,
            Items = { _commitInfoTab, _workingDirTab, _blameTab, _historyTab },
        };

        // ---- right side: revision grid over the bottom panel
        Grid right = new() { RowDefinitions = new RowDefinitions("3*,4,2*"), ClipToBounds = true };
        GridSplitter rightSplit = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(_revisions, 0);
        Grid.SetRow(rightSplit, 1);
        Grid.SetRow(_bottom, 2);
        right.Children.Add(_revisions);
        right.Children.Add(rightSplit);
        right.Children.Add(_bottom);

        // ---- main area: left tree | right side
        Grid main = new() { ColumnDefinitions = new ColumnDefinitions("260,4,*") };
        GridSplitter treeSplit = new() { Width = 4, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(_tree, 0);
        Grid.SetColumn(treeSplit, 1);
        Grid.SetColumn(right, 2);
        main.Children.Add(_tree);
        main.Children.Add(treeSplit);
        main.Children.Add(right);

        DockPanel root = new() { Background = (IBrush)Resources["App.Window"]! };
        DockPanel.SetDock(_toolbar, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        root.Children.Add(_toolbar);
        root.Children.Add(_statusBar);
        root.Children.Add(main);
        Content = root;

        WireEvents();

        Opened += (_, _) =>
        {
            string? initial = FindRepositoryRoot(App.InitialRepoPath ?? Directory.GetCurrentDirectory());
            if (initial is not null)
            {
                OpenRepository(initial);
            }
            else
            {
                _ = PickRepositoryAsync();
            }
        };
    }

    private void WireEvents()
    {
        _revisions.RevisionSelected += OnRevisionSelected;
        _fileHistory.RevisionSelected += OnRevisionSelected;
        _workingDir.Committed += RefreshAll;
        _tree.OperationCompleted += RefreshAll;
        _tree.RefSelected += OnRevisionSelected;

        _diff.BlameRequested += path => ShowInBottom(_blameTab, () => _blame.ShowBlame(_repoPath!, path));
        _diff.FileHistoryRequested += path => ShowInBottom(_historyTab, () => _fileHistory.ShowHistory(_repoPath!, path));

        // Toolbar actions.
        _toolbar.OpenRepoRequested += () => _ = PickRepositoryAsync();
        _toolbar.RefreshRequested += RefreshAll;
        _toolbar.CommitRequested += () => _bottom.SelectedItem = _workingDirTab;
        _toolbar.FetchRequested += () => RunRemoteOp("Fetch", (s, r) => s.Fetch(_repoPath!, r, null).Success);
        _toolbar.PullRequested += () => RunRemoteOp("Pull", (s, r) => s.Pull(_repoPath!, r, rebase: false, null).Success);
        _toolbar.PushRequested += () => RunRemoteOp("Push", (s, r) =>
            s.Push(_repoPath!, r, new RemoteService().GetCurrentBranch(_repoPath!), force: false, null).Success);
        _toolbar.StashRequested += () => RunOp("Stash", () => _stashOps.StashSave(_repoPath!, "WIP", includeUntracked: false).Success);
        _toolbar.NewBranchRequested += () => _ = NewBranchAsync();

        // Commit-targeted operations on the revision grid.
        _revisions.AddCommitCommand("Checkout this commit",
            hash => RunOp("Checkout", () => new BranchTagService().Checkout(_repoPath!, hash).Success));
        _revisions.AddCommitCommand("Cherry-pick",
            hash => RunOp("Cherry-pick", () => _stashOps.CherryPick(_repoPath!, hash).Success));
        _revisions.AddCommitCommand("Reset (soft) to here",
            hash => RunOp("Reset soft", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Soft).Success));
        _revisions.AddCommitCommand("Reset (mixed) to here",
            hash => RunOp("Reset mixed", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Mixed).Success));
        _revisions.AddCommitCommand("Reset (HARD) to here…",
            hash => RunOp("Reset hard", () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Hard).Success, confirm: true));
    }

    private void OnRevisionSelected(string commitHash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _detail.ShowCommit(_repoPath, commitHash);
        _diff.ShowCommit(_repoPath, commitHash);
        _bottom.SelectedItem = _commitInfoTab;
    }

    private void ShowInBottom(TabItem tab, Action show)
    {
        if (_repoPath is not null)
        {
            show();
            _bottom.SelectedItem = tab;
        }
    }

    private void RefreshAll()
    {
        if (_repoPath is null)
        {
            return;
        }

        WarmUpCore(_repoPath);
        _revisions.LoadRepository(_repoPath);
        _workingDir.LoadRepository(_repoPath);
        _tree.LoadRepository(_repoPath);
        _statusBar.LoadRepository(_repoPath);
    }

    // Picks the remote (first configured, or "origin") and runs a remote op.
    private void RunRemoteOp(string label, Func<RemoteService, string, bool> op)
    {
        if (_repoPath is null)
        {
            return;
        }

        RunOp(label, () =>
        {
            RemoteService svc = new();
            var remotes = svc.ListRemotes(_repoPath);
            string remote = remotes.Count > 0 ? remotes[0].Name : "origin";
            return op(svc, remote);
        });
    }

    private void RunOp(string label, Func<bool> op, bool confirm = false)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            if (confirm && !await ConfirmAsync($"{label}? This may discard work."))
            {
                return;
            }

            _statusBar.SetText($"{label}…");
            bool ok;
            try
            {
                ok = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"{label} failed: {ex.Message}");
                return;
            }

            RefreshAll();
            if (!ok)
            {
                _statusBar.SetText($"{label} failed — see the panel output.");
            }
        }
    }

    private async Task NewBranchAsync()
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("New branch", "Branch name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        RunOp($"Create branch {name}",
            () => new BranchTagService().CreateBranch(_repoPath!, name.Trim(), startPoint: "HEAD", checkout: true).Success);
    }

    private async Task PickRepositoryAsync()
    {
        RepositoryPickerView picker = new();
        Window dlg = new()
        {
            Title = "Open Git repository",
            Width = 640,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Resources["App.Window"]!,
            Content = picker,
        };
        picker.RepositorySelected += repo =>
        {
            dlg.Close();
            OpenRepository(repo);
        };
        await dlg.ShowDialog(this);
    }

    private void OpenRepository(string repoPath)
    {
        _repoPath = repoPath;
        WarmUpCore(repoPath);

        _revisions.LoadRepository(repoPath);
        _workingDir.LoadRepository(repoPath);
        _tree.LoadRepository(repoPath);
        _statusBar.LoadRepository(repoPath);
    }

    // Confirmation dialog (Yes/No).
    private Task<bool> ConfirmAsync(string message) => YesNoAsync(message);

    private async Task<bool> YesNoAsync(string message)
    {
        Button yes = new() { Content = "Yes", MinWidth = 80 };
        Button no = new() { Content = "No", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Window dlg = new()
        {
            Title = "Confirm",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Resources["App.Window"]!,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { yes, no },
                    },
                },
            },
        };
        bool result = false;
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // Single-line text prompt; returns null on cancel.
    private async Task<string?> PromptAsync(string title, string label)
    {
        TextBox input = new() { Watermark = label };
        Button ok = new() { Content = "OK", MinWidth = 80 };
        Button cancel = new() { Content = "Cancel", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Window dlg = new()
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Resources["App.Window"]!,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) },
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { ok, cancel },
                    },
                },
            },
        };
        string? result = null;
        ok.Click += (_, _) => { result = input.Text; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // Touches the core's main read paths once, sequentially, so shared
    // process-global lazy state initializes before concurrent panel loads.
    private static void WarmUpCore(string repoPath)
    {
        try
        {
            GitCommands.GitModule module = GitContext.CreateModule(repoPath);
            _ = module.GetCurrentCheckout();
            _ = new RevisionService().LoadRevisions(repoPath, 1);
        }
        catch
        {
            // Best-effort; the panels report their own errors.
        }
    }

    // Accept a subdirectory: walk up until a git working dir is found.
    private static string? FindRepositoryRoot(string path)
    {
        try
        {
            DirectoryInfo? dir = new(path);
            while (dir is not null)
            {
                if (GitService.IsGitRepository(dir.FullName))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }
}
