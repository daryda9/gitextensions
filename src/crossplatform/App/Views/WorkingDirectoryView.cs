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
    private readonly ListBox _conflictsList;
    private readonly Grid _conflictsPanel;
    private readonly Button _stageButton;
    private readonly Button _unstageButton;
    private readonly TextBox _messageBox;
    private readonly CheckBox _amendCheck;
    private readonly Button _commitButton;
    private readonly Button _undoButton;
    private readonly Button _cleanButton;
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

    // A ListBox of plain conflicted-path strings (distinct from the staged/unstaged
    // WorkingDirFileRow lists). Styled with the accent brush so conflicts stand out.
    private static ListBox MakeStringList()
        => new()
        {
            SelectionMode = SelectionMode.Multiple,
            FontFamily = Monospace,
            FontSize = 12,
            Background = B("App.Panel"),
            Foreground = B("App.Accent"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            MinHeight = 60,
            ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<string>(
                (path, _) => new TextBlock
                {
                    Text = string.IsNullOrEmpty(path) ? string.Empty : "⚠  " + path,
                    Foreground = B("App.Accent"),
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

        _conflictsList = MakeStringList();
        _conflictsList.DoubleTapped += (_, _) => OpenInMergetool();

        MenuItem mergetoolItem = new() { Header = "Open in mergetool" };
        mergetoolItem.Click += (_, _) => OpenInMergetool();
        MenuItem takeOursItem = new() { Header = "Take ours" };
        takeOursItem.Click += (_, _) => ResolveConflicts("ours");
        MenuItem takeTheirsItem = new() { Header = "Take theirs" };
        takeTheirsItem.Click += (_, _) => ResolveConflicts("theirs");
        MenuItem markResolvedItem = new() { Header = "Mark resolved" };
        markResolvedItem.Click += (_, _) => ResolveConflicts("resolved");
        _conflictsList.ContextMenu = new ContextMenu
        {
            ItemsSource = new[] { mergetoolItem, takeOursItem, takeTheirsItem, markResolvedItem },
        };

        _conflictsPanel = MakeConflictsPanel(_conflictsList);
        _conflictsPanel.IsVisible = false;

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

        _undoButton = new Button { Content = "Undo last commit", Margin = new Thickness(0, 0, 6, 0) };
        _undoButton.Click += (_, _) => UndoLastCommit();

        _cleanButton = new Button { Content = "Clean…" };
        _cleanButton.Click += (_, _) => _ = CleanWorkingDirectoryAsync();

        StackPanel actionsBar = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4),
        };
        actionsBar.Children.Add(_undoButton);
        actionsBar.Children.Add(_cleanButton);

        StackPanel commitPanel = new() { Margin = new Thickness(8, 4, 8, 4) };
        commitPanel.Children.Add(actionsBar);
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
        DockPanel.SetDock(_conflictsPanel, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(commitPanel);
        root.Children.Add(_conflictsPanel);
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

    // The conflicts section (title + list) lives in a bordered container docked
    // above the staged/unstaged lists. It is collapsed (IsVisible=false) whenever
    // there are no conflicts, so a clean repository shows the normal staging UI.
    private static Grid MakeConflictsPanel(ListBox list)
    {
        Grid grid = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(8, 8, 8, 0),
        };

        TextBlock title = new()
        {
            Text = "Merge conflicts (double-click to open mergetool)",
            FontWeight = FontWeight.Bold,
            Foreground = B("App.Accent"),
            Margin = new Thickness(0, 0, 0, 2),
        };

        Grid.SetRow(title, 0);
        Grid.SetRow(list, 1);
        grid.Children.Add(title);
        grid.Children.Add(list);
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

                List<string> conflicts = status.Conflicts.ToList();
                _conflictsList.ItemsSource = conflicts;
                _conflictsPanel.IsVisible = conflicts.Count > 0;

                _status.Text = conflicts.Count > 0
                    ? $"{conflicts.Count} conflict(s), {status.Unstaged.Count} unstaged, {status.Staged.Count} staged."
                    : $"{status.Unstaged.Count} unstaged, {status.Staged.Count} staged.";
            });
    }

    private List<string> SelectedConflicts()
        => _conflictsList.SelectedItems?.OfType<string>().ToList() ?? [];

    // Launches the configured merge tool for each selected conflict (detached).
    // Does not RefreshStatus immediately: the tool runs asynchronously, so the
    // user marks the file resolved (or takes ours/theirs) once done.
    private void OpenInMergetool()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        List<string> paths = SelectedConflicts();
        if (paths.Count == 0)
        {
            return;
        }

        _status.Text = "Launching merge tool…";
        RunGit(
            () =>
            {
                WorkingDirCommitResult last = new(true, string.Empty);
                foreach (string path in paths)
                {
                    last = _service.LaunchMergetool(repo, path);
                    if (!last.Success)
                    {
                        break;
                    }
                }

                return last;
            },
            result => _status.Text = result.Success
                ? "Merge tool launched. Mark resolved when done."
                : result.Output.Trim());
    }

    // Resolves selected conflicts via "ours", "theirs", or plain "mark resolved"
    // (git add), then refreshes so resolved files leave the conflicts section.
    private void ResolveConflicts(string mode)
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        List<string> paths = SelectedConflicts();
        if (paths.Count == 0)
        {
            return;
        }

        _status.Text = "Resolving conflict(s)…";
        RunGit(
            () =>
            {
                WorkingDirCommitResult last = new(true, string.Empty);
                foreach (string path in paths)
                {
                    last = mode switch
                    {
                        "ours" => _service.TakeOurs(repo, path),
                        "theirs" => _service.TakeTheirs(repo, path),
                        _ => _service.MarkResolved(repo, path),
                    };

                    if (!last.Success)
                    {
                        break;
                    }
                }

                return last;
            },
            result =>
            {
                if (!result.Success)
                {
                    _status.Text = "Resolve failed: " + result.Output.Trim();
                }

                RefreshStatus();
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

    // Undoes the last commit but keeps the changes (git reset --soft HEAD~1),
    // then refreshes via the shared RefreshStatus path. Fails gracefully when
    // there is no parent commit (the git error is shown in the status line).
    private void UndoLastCommit()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _status.Text = "Undoing last commit…";
        RunGit(
            () => _service.UndoLastCommit(repo),
            result =>
            {
                if (result.Success)
                {
                    _status.Text = "Last commit undone (changes kept).";
                    RefreshStatus();
                    Committed?.Invoke();
                }
                else
                {
                    _status.Text = "Undo failed: " + result.Output.Trim();
                }
            });
    }

    // Destructive: first shows a dry-run preview of what "git clean -f -d" would
    // remove and requires explicit confirmation before running the real clean.
    private async Task CleanWorkingDirectoryAsync()
    {
        if (_busy || _repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        _status.Text = "Previewing clean…";
        // Guard the preview/confirm window (RunGit's _busy check only covers the
        // actual clean below), so the button can't be re-triggered mid-flow.
        _cleanButton.IsEnabled = false;

        WorkingDirCommitResult preview;
        try
        {
            preview = await Task.Run(() => _service.CleanDryRun(repo));
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
            _cleanButton.IsEnabled = !_busy;
            return;
        }

        if (!preview.Success)
        {
            _status.Text = "Clean preview failed: " + preview.Output.Trim();
            _cleanButton.IsEnabled = !_busy;
            return;
        }

        string preview_text = preview.Output.Trim();
        if (preview_text.Length == 0)
        {
            _status.Text = "Nothing to clean (no untracked files).";
            _cleanButton.IsEnabled = !_busy;
            return;
        }

        bool confirmed = await ConfirmAsync(
            "The following untracked files/directories will be permanently removed:\n\n"
            + preview_text
            + "\n\nThis cannot be undone. Continue?");

        if (!confirmed)
        {
            _status.Text = "Clean cancelled.";
            _cleanButton.IsEnabled = !_busy;
            return;
        }

        // RunGit's SetBusy(true) takes over button-disabling from here.
        _status.Text = "Cleaning…";
        RunGit(
            () => _service.Clean(repo),
            result =>
            {
                _status.Text = result.Success
                    ? "Working directory cleaned."
                    : "Clean failed: " + result.Output.Trim();
                RefreshStatus();
            });
    }

    // Minimal modal confirmation using base Avalonia only (no message-box
    // package), matching the pattern used elsewhere in the app (StashPanel).
    private async Task<bool> ConfirmAsync(string text)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        bool result = false;

        Button yes = new() { Content = "Yes", MinWidth = 70, Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = "No", MinWidth = 70 };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        StackPanel content = new() { Margin = new Thickness(16), Background = B("App.Window") };
        content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = B("App.Text"),
            FontFamily = Monospace,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        });
        content.Children.Add(buttons);

        Window dialog = new()
        {
            Title = "Confirm clean",
            Width = 500,
            MaxHeight = 500,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = B("App.Window"),
            Content = new ScrollViewer { Content = content },
        };

        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => { result = false; dialog.Close(); };

        await dialog.ShowDialog(owner);
        return result;
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
        _undoButton.IsEnabled = !busy;
        _cleanButton.IsEnabled = !busy;
    }
}
