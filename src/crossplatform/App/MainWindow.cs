using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Views;

namespace GitExtensions.Avalonia;

/// <summary>
///  Shell window: a repository picker plus tabbed views (History, Commit, Diff,
///  Branches, Remote, Stash, Blame, File History) wired together over the reused
///  Git Extensions core. Each view is a self-contained <see cref="UserControl"/>
///  that talks to the core through <see cref="GitContext"/>.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TextBlock _repoLabel;
    private readonly TabControl _tabs;

    private readonly RepositoryPickerView _picker;
    private readonly RevisionGridView _revisions;
    private readonly CommitDetailView _detail;
    private readonly DiffView _diff;
    private readonly WorkingDirectoryView _workingDir;
    private readonly BranchTagPanel _branches;
    private readonly RemotePanel _remote;
    private readonly StashPanel _stash;
    private readonly BlameView _blame;
    private readonly FileHistoryView _fileHistory;

    private readonly StashOpsService _stashOps = new();

    private readonly TabItem _openTab;
    private readonly TabItem _historyTab;
    private readonly TabItem _diffTab;

    private string? _repoPath;

    public MainWindow()
    {
        Title = "Git Extensions (Avalonia / Linux)";
        Width = 1200;
        Height = 760;

        _picker = new RepositoryPickerView();
        _revisions = new RevisionGridView();
        _detail = new CommitDetailView();
        _diff = new DiffView();
        _workingDir = new WorkingDirectoryView();
        _branches = new BranchTagPanel();
        _remote = new RemotePanel();
        _stash = new StashPanel();
        _blame = new BlameView();
        _fileHistory = new FileHistoryView();

        _openTab = new TabItem { Header = "Open Repository", Content = _picker };
        _historyTab = new TabItem { Header = "History", Content = _revisions };
        _diffTab = new TabItem { Header = "Diff", Content = BuildDiffPane() };

        _tabs = new TabControl
        {
            Items =
            {
                _openTab,
                _historyTab,
                new TabItem { Header = "Commit", Content = _workingDir },
                _diffTab,
                new TabItem { Header = "Branches", Content = _branches },
                new TabItem { Header = "Remote", Content = _remote },
                new TabItem { Header = "Stash", Content = _stash },
                new TabItem { Header = "Blame", Content = BuildFilePane(_blame, ShowBlame) },
                new TabItem { Header = "File History", Content = BuildFilePane(_fileHistory, ShowFileHistory) },
            },
        };

        _repoLabel = new TextBlock
        {
            Margin = new Thickness(10, 6),
            Foreground = Brushes.Gray,
            Text = "No repository open.",
            VerticalAlignment = VerticalAlignment.Center,
        };

        DockPanel root = new();
        DockPanel.SetDock(_repoLabel, Dock.Top);
        root.Children.Add(_repoLabel);
        root.Children.Add(_tabs);
        Content = root;

        // Wire the views together.
        _picker.RepositorySelected += OnRepositorySelected;
        _revisions.RevisionSelected += OnRevisionSelected;
        _workingDir.Committed += RefreshAfterMutation;
        _branches.OperationCompleted += RefreshAfterMutation;
        _remote.OperationCompleted += RefreshAfterMutation;
        _stash.OperationCompleted += RefreshAfterMutation;
        _fileHistory.RevisionSelected += OnRevisionSelected;

        // Commit-targeted operations, offered on each revision-grid row.
        _revisions.AddCommitCommand("Checkout this commit",
            hash => RunCommitOp("Checkout", hash, () => new BranchTagService().Checkout(_repoPath!, hash) is { Success: true }));
        _revisions.AddCommitCommand("Cherry-pick",
            hash => RunCommitOp("Cherry-pick", hash, () => _stashOps.CherryPick(_repoPath!, hash).Success));
        _revisions.AddCommitCommand("Reset (soft) to here",
            hash => RunCommitOp("Reset soft", hash, () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Soft).Success));
        _revisions.AddCommitCommand("Reset (mixed) to here",
            hash => RunCommitOp("Reset mixed", hash, () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Mixed).Success));
        _revisions.AddCommitCommand("Reset (HARD) to here…",
            hash => RunCommitOp("Reset hard", hash, () => _stashOps.Reset(_repoPath!, hash, StashResetMode.Hard).Success, confirm: true));

        // Startup: if a repo was supplied (CLI / cwd), open it; else show picker.
        Opened += (_, _) =>
        {
            string? initial = FindRepositoryRoot(
                App.InitialRepoPath ?? Directory.GetCurrentDirectory());
            if (initial is not null)
            {
                OpenRepository(initial);
            }
            else
            {
                _tabs.SelectedItem = _openTab;
            }
        };
    }

    // Diff tab: commit metadata/message (top) over the file diff (bottom).
    private Control BuildDiffPane()
    {
        Grid pane = new() { RowDefinitions = new RowDefinitions("2*,4,3*") };
        GridSplitter splitter = new()
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetRow(_detail, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(_diff, 2);
        pane.Children.Add(_detail);
        pane.Children.Add(splitter);
        pane.Children.Add(_diff);
        return pane;
    }

    // Blame / File-History tab: a repo-relative path input above the view.
    private static Control BuildFilePane(Control view, Action<string> show)
    {
        TextBox pathBox = new()
        {
            Watermark = "Repo-relative file path (e.g. src/crossplatform/App/Program.cs)",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Button showButton = new() { Content = "Show", Margin = new Thickness(6, 0, 0, 0) };
        showButton.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(pathBox.Text))
            {
                show(pathBox.Text.Trim());
            }
        };

        Grid bar = new()
        {
            Margin = new Thickness(8),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(showButton, 1);
        bar.Children.Add(pathBox);
        bar.Children.Add(showButton);

        DockPanel pane = new();
        DockPanel.SetDock(bar, Dock.Top);
        pane.Children.Add(bar);
        pane.Children.Add(view);
        return pane;
    }

    private void ShowBlame(string path)
    {
        if (_repoPath is not null)
        {
            _blame.ShowBlame(_repoPath, path);
        }
    }

    private void ShowFileHistory(string path)
    {
        if (_repoPath is not null)
        {
            _fileHistory.ShowHistory(_repoPath, path);
        }
    }

    private void OnRepositorySelected(string repoPath) => OpenRepository(repoPath);

    private void OnRevisionSelected(string commitHash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _detail.ShowCommit(_repoPath, commitHash);
        _diff.ShowCommit(_repoPath, commitHash);
        _tabs.SelectedItem = _diffTab;
    }

    private void RefreshAfterMutation()
    {
        if (_repoPath is null)
        {
            return;
        }

        _revisions.LoadRepository(_repoPath);
        _workingDir.LoadRepository(_repoPath);
        _branches.LoadRepository(_repoPath);
        _stash.LoadRepository(_repoPath);
    }

    // Runs a commit-targeted git op off the UI thread, optionally after a
    // confirmation, then refreshes the dependent views.
    private void RunCommitOp(string label, string hash, Func<bool> op, bool confirm = false)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            if (confirm && !await ConfirmAsync($"{label} at {hash[..Math.Min(8, hash.Length)]}? This may discard work."))
            {
                return;
            }

            _repoLabel.Text = $"{label}…";
            bool ok;
            try
            {
                ok = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _repoLabel.Text = $"{label} failed: {ex.Message}";
                return;
            }

            _repoLabel.Text = $"Repository: {_repoPath}  —  {label}: {(ok ? "ok" : "failed")}";
            RefreshAfterMutation();
        }
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        Button yes = new() { Content = "Yes", MinWidth = 80 };
        Button no = new() { Content = "No", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Window dlg = new()
        {
            Title = "Confirm",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
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

    private void OpenRepository(string repoPath)
    {
        _repoPath = repoPath;
        _repoLabel.Text = $"Repository: {repoPath}";

        // Warm the reused core on a single thread before the panels load
        // concurrently: several views each build a GitModule and hit the core's
        // first-time initialization at once, which otherwise races a shared
        // Lazy ("ValueFactory attempted to access the Value property").
        WarmUpCore(repoPath);

        _revisions.LoadRepository(repoPath);
        _workingDir.LoadRepository(repoPath);
        _branches.LoadRepository(repoPath);
        _remote.LoadRepository(repoPath);
        _stash.LoadRepository(repoPath);
        _picker.Refresh();

        _tabs.SelectedItem = _historyTab;
    }

    // Touches the core's main read paths once, sequentially, so any shared
    // process-global lazy state is initialized before concurrent panel loads.
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
            // Warm-up is best-effort; the panels report their own errors.
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
