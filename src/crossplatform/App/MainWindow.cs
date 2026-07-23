using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Views;

namespace GitExtensions.Avalonia;

/// <summary>
///  Shell window: a repository picker plus tabbed views (History, Commit, Diff)
///  wired together over the reused Git Extensions core. Each view is a
///  self-contained <see cref="UserControl"/> that talks to the core through
///  <see cref="GitContext"/>.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TextBlock _repoLabel;
    private readonly TabControl _tabs;
    private readonly RepositoryPickerView _picker;
    private readonly RevisionGridView _revisions;
    private readonly DiffView _diff;
    private readonly WorkingDirectoryView _workingDir;

    private readonly TabItem _openTab;
    private readonly TabItem _historyTab;
    private readonly TabItem _commitTab;
    private readonly TabItem _diffTab;

    private string? _repoPath;

    public MainWindow()
    {
        Title = "Git Extensions (Avalonia / Linux)";
        Width = 1100;
        Height = 720;

        _picker = new RepositoryPickerView();
        _revisions = new RevisionGridView();
        _diff = new DiffView();
        _workingDir = new WorkingDirectoryView();

        _openTab = new TabItem { Header = "Open Repository", Content = _picker };
        _historyTab = new TabItem { Header = "History", Content = _revisions };
        _commitTab = new TabItem { Header = "Commit", Content = _workingDir };
        _diffTab = new TabItem { Header = "Diff", Content = _diff };

        _tabs = new TabControl
        {
            Items =
            {
                _openTab,
                _historyTab,
                _commitTab,
                _diffTab,
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
        _workingDir.Committed += OnCommitted;

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

    private void OnRepositorySelected(string repoPath)
    {
        OpenRepository(repoPath);
    }

    private void OnRevisionSelected(string commitHash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _diff.ShowCommit(_repoPath, commitHash);
        _tabs.SelectedItem = _diffTab;
    }

    private void OnCommitted()
    {
        if (_repoPath is not null)
        {
            _revisions.LoadRepository(_repoPath);
        }
    }

    private void OpenRepository(string repoPath)
    {
        _repoPath = repoPath;
        _repoLabel.Text = $"Repository: {repoPath}";

        _revisions.LoadRepository(repoPath);
        _workingDir.LoadRepository(repoPath);
        _picker.Refresh();

        _tabs.SelectedItem = _historyTab;
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
