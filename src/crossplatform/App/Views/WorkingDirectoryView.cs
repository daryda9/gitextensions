using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Working-directory / staging view: lists unstaged and staged changes, lets the
///  user move files between them, enter a commit message and commit (optionally
///  amending). All git work runs off the UI thread via <see cref="Task.Run"/> and
///  posts results back with <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class WorkingDirectoryView : UserControl
{
    private readonly WorkingDirectoryService _service = new();

    private readonly ListBox _unstagedList;
    private readonly ListBox _stagedList;
    private readonly Button _stageButton;
    private readonly Button _unstageButton;
    private readonly TextBox _messageBox;
    private readonly CheckBox _amendCheck;
    private readonly Button _commitButton;
    private readonly TextBlock _status;

    private string? _repoPath;
    private bool _busy;

    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");
    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private static ListBox MakeList()
        => new()
        {
            SelectionMode = SelectionMode.Multiple,
            FontFamily = Monospace,
            FontSize = 12,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            MinHeight = 90,
            ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<WorkingDirFileRow>(
                (row, _) => new TextBlock
                {
                    Text = row?.Display ?? string.Empty,
                    Foreground = B("App.Text"),
                    FontFamily = Monospace,
                    FontSize = 12,
                },
                supportsRecycling: true),
        };

    /// <summary>
    ///  Raised on the UI thread after a successful commit (lists already refreshed).
    /// </summary>
    public event Action? Committed;

    public WorkingDirectoryView()
    {
        _unstagedList = MakeList();
        _unstagedList.DoubleTapped += (_, _) => StageSelected();
        _unstagedList.KeyDown += OnUnstagedKeyDown;

        MenuItem stageItem = new() { Header = "Stage" };
        stageItem.Click += (_, _) => StageSelected();
        MenuItem unstagedCopyItem = new() { Header = "Copy path" };
        unstagedCopyItem.Click += (_, _) => CopySelectedPath(_unstagedList);
        _unstagedList.ContextMenu = new ContextMenu { ItemsSource = new[] { stageItem, unstagedCopyItem } };

        _stagedList = MakeList();
        _stagedList.DoubleTapped += (_, _) => UnstageSelected();
        _stagedList.KeyDown += OnStagedKeyDown;

        MenuItem unstageItem = new() { Header = "Unstage" };
        unstageItem.Click += (_, _) => UnstageSelected();
        MenuItem stagedCopyItem = new() { Header = "Copy path" };
        stagedCopyItem.Click += (_, _) => CopySelectedPath(_stagedList);
        _stagedList.ContextMenu = new ContextMenu { ItemsSource = new[] { unstageItem, stagedCopyItem } };

        _stageButton = new Button { Content = "Stage ▼", Margin = new Thickness(0, 4, 0, 0) };
        _stageButton.Click += (_, _) => StageSelected();

        _unstageButton = new Button { Content = "Unstage ▲", Margin = new Thickness(0, 4, 0, 0) };
        _unstageButton.Click += (_, _) => UnstageSelected();

        Grid unstagedPanel = MakeListPanel("Unstaged changes (double-click to stage)", _unstagedList, _stageButton);
        Grid stagedPanel = MakeListPanel("Staged changes (double-click to unstage)", _stagedList, _unstageButton);

        Grid lists = new()
        {
            RowDefinitions = new RowDefinitions("*,*"),
            Margin = new Thickness(8, 4, 8, 4),
        };
        Grid.SetRow(unstagedPanel, 0);
        Grid.SetRow(stagedPanel, 1);
        lists.Children.Add(unstagedPanel);
        lists.Children.Add(stagedPanel);

        _messageBox = new TextBox
        {
            Watermark = "Commit message",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _messageBox.KeyDown += OnMessageBoxKeyDown;

        _amendCheck = new CheckBox { Content = "Amend last commit" };

        _commitButton = new Button
        {
            Content = "Commit",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _commitButton.Click += (_, _) => DoCommit();

        Grid commitBar = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 0),
        };
        Grid.SetColumn(_amendCheck, 0);
        Grid.SetColumn(_commitButton, 1);
        commitBar.Children.Add(_amendCheck);
        commitBar.Children.Add(_commitButton);

        StackPanel commitPanel = new() { Margin = new Thickness(8, 4, 8, 4) };
        commitPanel.Children.Add(_messageBox);
        commitPanel.Children.Add(commitBar);

        _status = new TextBlock
        {
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = B("App.TextDim"),
            Text = "No repository loaded.",
            TextWrapping = TextWrapping.Wrap,
        };

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(commitPanel, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(commitPanel);
        root.Children.Add(lists);

        Content = root;
    }

    /// <summary>
    ///  Points the view at <paramref name="repoPath"/> and loads its status.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        RefreshStatus();
    }

    private static Grid MakeListPanel(string header, ListBox list, Button button)
    {
        Grid grid = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 4, 0, 4),
        };

        TextBlock title = new()
        {
            Text = header,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };

        Grid.SetRow(title, 0);
        Grid.SetRow(list, 1);
        Grid.SetRow(button, 2);
        grid.Children.Add(title);
        grid.Children.Add(list);
        grid.Children.Add(button);
        return grid;
    }

    private void RefreshStatus()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _status.Text = "No repository loaded.";
            return;
        }

        _status.Text = "Loading…";
        RunGit(
            () => _service.LoadStatus(repo),
            status =>
            {
                _unstagedList.ItemsSource = status.Unstaged.ToList();
                _stagedList.ItemsSource = status.Staged.ToList();
                _status.Text = $"{status.Unstaged.Count} unstaged, {status.Staged.Count} staged.";
            });
    }

    private void StageSelected()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        List<WorkingDirFileRow> files = SelectedRows(_unstagedList);
        if (files.Count == 0)
        {
            return;
        }

        _status.Text = "Staging…";
        RunGit(
            () => _service.Stage(repo, files),
            _ => RefreshStatus());
    }

    private void UnstageSelected()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        List<WorkingDirFileRow> files = SelectedRows(_stagedList);
        if (files.Count == 0)
        {
            return;
        }

        _status.Text = "Unstaging…";
        RunGit(
            () => _service.Unstage(repo, files),
            _ => RefreshStatus());
    }

    private void DoCommit()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        string message = _messageBox.Text ?? string.Empty;
        bool amend = _amendCheck.IsChecked == true;

        if (string.IsNullOrWhiteSpace(message) && !amend)
        {
            _status.Text = "Enter a commit message.";
            return;
        }

        _status.Text = "Committing…";
        RunGit(
            () => _service.Commit(repo, message, amend),
            result =>
            {
                if (result.Success)
                {
                    _messageBox.Text = string.Empty;
                    _amendCheck.IsChecked = false;
                    _status.Text = "Committed.";
                    RefreshStatus();
                    Committed?.Invoke();
                }
                else
                {
                    _status.Text = "Commit failed: " + result.Output.Trim();
                }
            });
    }

    // Enter/Space stages the focused unstaged item(s); Ctrl+Enter commits.
    private void OnUnstagedKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            DoCommit();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            StageSelected();
            e.Handled = true;
        }
    }

    // Enter/Space unstages the focused staged item(s); Ctrl+Enter commits.
    private void OnStagedKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            DoCommit();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            UnstageSelected();
            e.Handled = true;
        }
    }

    private void OnMessageBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            DoCommit();
            e.Handled = true;
        }
    }

    private void CopySelectedPath(ListBox list)
    {
        List<WorkingDirFileRow> rows = SelectedRows(list);
        if (rows.Count == 0)
        {
            return;
        }

        string text = string.Join('\n', rows.Select(r => r.Path));
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private static List<WorkingDirFileRow> SelectedRows(ListBox list)
        => list.SelectedItems?.OfType<WorkingDirFileRow>().ToList() ?? [];

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
        _stageButton.IsEnabled = !busy;
        _unstageButton.IsEnabled = !busy;
        _commitButton.IsEnabled = !busy;
    }
}
