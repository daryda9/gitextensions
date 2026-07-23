using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Remote operations panel: lists the repository's remotes and offers Fetch,
///  Pull (with a rebase option) and Push (with a force option) against the
///  selected remote, reusing the Git Extensions core via <see cref="RemoteService"/>.
///
///  All git work runs off the UI thread (<see cref="Task.Run"/>) and results are
///  marshalled back with await continuations; the action buttons are disabled
///  while an operation is in flight. When an operation reports an authentication
///  failure the <see cref="CredentialsDialog"/> is shown and the operation is
///  retried once with the entered credentials.
/// </summary>
public sealed class RemotePanel : UserControl
{
    private readonly RemoteService _service = new();

    private readonly ListBox _remotesList;
    private readonly CheckBox _rebaseCheck;
    private readonly CheckBox _forceCheck;
    private readonly Button _fetchButton;
    private readonly Button _pullButton;
    private readonly Button _pushButton;
    private readonly TextBlock _status;
    private readonly TextBox _output;

    private string? _repoPath;
    private string _currentBranch = string.Empty;
    private bool _busy;

    /// <summary>
    ///  Raised on the UI thread after any remote operation completes successfully
    ///  (so the shell can refresh the history / working-directory views).
    /// </summary>
    public event Action? OperationCompleted;

    public RemotePanel()
    {
        _remotesList = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
        };
        _remotesList.SelectionChanged += (_, _) => UpdateButtons();

        _rebaseCheck = new CheckBox { Content = "Rebase on pull", Margin = new Thickness(0, 0, 12, 0) };
        _forceCheck = new CheckBox { Content = "Force push", Margin = new Thickness(0, 0, 12, 0) };

        _fetchButton = new Button { Content = "Fetch", MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        _fetchButton.Click += (_, _) => _ = DoFetch();

        _pullButton = new Button { Content = "Pull", MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        _pullButton.Click += (_, _) => _ = DoPull();

        _pushButton = new Button { Content = "Push", MinWidth = 80 };
        _pushButton.Click += (_, _) => _ = DoPush();

        StackPanel options = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 6),
        };
        options.Children.Add(_rebaseCheck);
        options.Children.Add(_forceCheck);

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
        };
        actions.Children.Add(_fetchButton);
        actions.Children.Add(_pullButton);
        actions.Children.Add(_pushButton);

        StackPanel controls = new() { Margin = new Thickness(0, 4, 0, 0) };
        controls.Children.Add(options);
        controls.Children.Add(actions);

        TextBlock listTitle = new()
        {
            Text = "Remotes",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };

        Grid top = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8, 8, 8, 4),
        };
        Grid.SetRow(listTitle, 0);
        Grid.SetRow(_remotesList, 1);
        Grid.SetRow(controls, 2);
        top.Children.Add(listTitle);
        top.Children.Add(_remotesList);
        top.Children.Add(controls);

        _output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
            Margin = new Thickness(8, 4, 8, 4),
            MinHeight = 140,
        };

        _status = new TextBlock
        {
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = Brushes.Gray,
            Text = "No repository loaded.",
            TextWrapping = TextWrapping.Wrap,
        };

        Grid outputArea = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        TextBlock outTitle = new()
        {
            Text = "Output",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(10, 4, 0, 0),
        };
        Grid.SetRow(outTitle, 0);
        Grid.SetRow(_output, 1);
        outputArea.Children.Add(outTitle);
        outputArea.Children.Add(_output);

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(top);
        root.Children.Add(outputArea);

        Content = root;
        UpdateButtons();
    }

    /// <summary>
    ///  Points the panel at <paramref name="repoPath"/> and loads its remotes.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        RefreshRemotes();
    }

    private void RefreshRemotes()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _status.Text = "No repository loaded.";
            return;
        }

        _status.Text = "Loading remotes…";
        SetBusy(true);
        _ = LoadRemotesAsync(repo);
    }

    private async Task LoadRemotesAsync(string repo)
    {
        try
        {
            (IReadOnlyList<RemoteRow> remotes, string branch) = await Task.Run(
                () => (_service.ListRemotes(repo), _service.GetCurrentBranch(repo)));

            _currentBranch = branch;
            _remotesList.ItemsSource = remotes;
            if (remotes.Count > 0)
            {
                _remotesList.SelectedItem = remotes.FirstOrDefault(r => r.Name == "origin") ?? remotes[0];
            }

            string branchText = string.IsNullOrEmpty(branch) ? "detached HEAD" : branch;
            _status.Text = remotes.Count == 0
                ? "No remotes configured."
                : $"{remotes.Count} remote(s). Current branch: {branchText}.";
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task DoFetch()
    {
        RemoteRow? remote = Selected();
        if (_repoPath is not { Length: > 0 } repo || remote is null)
        {
            return Task.CompletedTask;
        }

        return RunOp("Fetch", creds => _service.Fetch(repo, remote.Name, creds));
    }

    private Task DoPull()
    {
        RemoteRow? remote = Selected();
        if (_repoPath is not { Length: > 0 } repo || remote is null)
        {
            return Task.CompletedTask;
        }

        bool rebase = _rebaseCheck.IsChecked == true;
        return RunOp("Pull", creds => _service.Pull(repo, remote.Name, rebase, creds));
    }

    private Task DoPush()
    {
        RemoteRow? remote = Selected();
        if (_repoPath is not { Length: > 0 } repo || remote is null)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(_currentBranch))
        {
            _status.Text = "Cannot push: detached HEAD.";
            return Task.CompletedTask;
        }

        bool force = _forceCheck.IsChecked == true;
        string branch = _currentBranch;
        return RunOp("Push", creds => _service.Push(repo, remote.Name, branch, force, creds));
    }

    // Runs a remote operation off the UI thread; on an authentication failure it
    // prompts for credentials and retries the operation exactly once.
    private async Task RunOp(string label, Func<GitCredentials?, RemoteOpResult> op)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _status.Text = label + "…";
        _output.Text = string.Empty;

        try
        {
            RemoteOpResult result = await Task.Run(() => op(null));

            if (result.AuthFailed)
            {
                GitCredentials? creds = await PromptCredentials();
                if (creds is not null)
                {
                    _status.Text = $"{label} (retrying with credentials)…";
                    result = await Task.Run(() => op(creds));
                }
            }

            _output.Text = result.Output;

            if (result.Success)
            {
                _status.Text = $"{label} succeeded.";
                OperationCompleted?.Invoke();
            }
            else if (result.AuthFailed)
            {
                _status.Text = $"{label} failed: authentication required.";
            }
            else
            {
                _status.Text = $"{label} failed.";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<GitCredentials?> PromptCredentials()
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            return await CredentialsDialog.ShowAsync(owner);
        }

        return null;
    }

    private RemoteRow? Selected() => _remotesList.SelectedItem as RemoteRow;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool canAct = !_busy && Selected() is not null;
        _fetchButton.IsEnabled = canAct;
        _pullButton.IsEnabled = canAct;
        _pushButton.IsEnabled = canAct;
    }
}
