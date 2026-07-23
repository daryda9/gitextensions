using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitExtensions.Avalonia;

public sealed class MainWindow : Window
{
    private readonly TextBox _pathBox;
    private readonly TextBlock _status;
    private readonly ListBox _commits;

    public MainWindow()
    {
        Title = "Git Extensions (Avalonia / Linux)";
        Width = 1000;
        Height = 640;

        _pathBox = new TextBox
        {
            Text = Directory.GetCurrentDirectory(),
            Watermark = "Path to a git repository",
            VerticalAlignment = VerticalAlignment.Center,
        };

        Button openButton = new() { Content = "Open", Margin = new Thickness(6, 0, 0, 0) };
        openButton.Click += (_, _) => LoadRepository(_pathBox.Text ?? string.Empty);

        Grid toolbar = new()
        {
            Margin = new Thickness(8),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(_pathBox, 0);
        Grid.SetColumn(openButton, 1);
        toolbar.Children.Add(_pathBox);
        toolbar.Children.Add(openButton);

        _status = new TextBlock
        {
            Margin = new Thickness(10, 0, 10, 6),
            Foreground = Brushes.Gray,
            Text = "Enter a repository path and press Open.",
        };

        _commits = new ListBox
        {
            Margin = new Thickness(8, 0, 8, 8),
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
        };

        DockPanel root = new();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(_commits);

        Content = root;

        // Auto-load the current directory if it is a repo, so the app shows
        // real data immediately.
        Opened += (_, _) => LoadRepository(_pathBox.Text ?? string.Empty);
    }

    private void LoadRepository(string path)
    {
        _commits.ItemsSource = null;

        if (!GitService.IsGitRepository(path))
        {
            _status.Text = $"Not a git repository: {path}";
            return;
        }

        _status.Text = "Loading…";

        _ = Task.Run(() =>
        {
            try
            {
                string branch = GitService.ReadCurrentBranch(path);
                IReadOnlyList<CommitRow> commits = GitService.ReadCommits(path);
                Dispatcher.UIThread.Post(() =>
                {
                    _commits.ItemsSource = commits.Select(c => c.Display).ToList();
                    _status.Text = $"{path}  —  branch '{branch}'  —  {commits.Count} commits";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = "Error: " + ex.Message);
            }
        });
    }
}
