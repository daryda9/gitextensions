using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal commit window rebuilt to mirror the original Git Extensions dedicated
///  commit form as a self-contained 3-zone layout (it no longer hosts a
///  <see cref="WorkingDirectoryView"/>):
///  <list type="bullet">
///   <item>LEFT: Unstaged list (top) + Stage/Unstage buttons + Staged list (bottom).</item>
///   <item>RIGHT: read-only monospace diff of the selected file.</item>
///   <item>BOTTOM: commit message box, Amend checkbox, Commit / Commit&amp;push /
///    Reset buttons, and a <c>Staged x/y</c> status line.</item>
///  </list>
///  All git work runs off the UI thread. <see cref="Committed"/> fires on each
///  successful commit; the dialog deliberately does NOT auto-close so the user can
///  make several commits before closing the window.
/// </summary>
public sealed class CommitDialog : Window
{
    private readonly string _repoPath;
    private readonly WorkingDirectoryService _service = new();

    private readonly ListBox _unstagedList = MakeList();
    private readonly ListBox _stagedList = MakeList();
    private readonly TextBox _messageBox;
    private readonly CheckBox _amendBox;
    private readonly SelectableTextBlock _diffView;
    private readonly ScrollViewer _diffScroll;
    private readonly TextBlock _statusText;

    private bool _busy;

    /// <summary>Raised on each successful commit so the owner can refresh.</summary>
    public event Action? Committed;

    public CommitDialog(string repoPath)
    {
        _repoPath = repoPath;

        Title = "Commit";
        Width = 1000;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // ---- RIGHT: diff view ----
        _diffView = new SelectableTextBlock
        {
            FontFamily = Monospace,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            Margin = new Thickness(6),
        };
        _diffScroll = new ScrollViewer
        {
            Content = _diffView,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush("App.Panel", Brushes.Black),
            ClipToBounds = true,
        };

        // ---- LEFT: unstaged / buttons / staged ----
        _unstagedList.SelectionChanged += (_, _) => OnSelected(_unstagedList, staged: false);
        _stagedList.SelectionChanged += (_, _) => OnSelected(_stagedList, staged: true);
        _unstagedList.DoubleTapped += (_, _) => StageSelected();
        _stagedList.DoubleTapped += (_, _) => UnstageSelected();

        Button stageBtn = MakeButton("Stage ▼", StageSelected);
        Button unstageBtn = MakeButton("Unstage ▲", UnstageSelected);
        Button stageAllBtn = MakeButton("Stage all", StageAll);
        Button unstageAllBtn = MakeButton("Unstage all", UnstageAll);

        StackPanel stageButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 4),
            Children = { stageBtn, unstageBtn, stageAllBtn, unstageAllBtn },
        };

        Grid leftPanel = new()
        {
            RowDefinitions = new RowDefinitions("*,Auto,*"),
        };
        leftPanel.Children.Add(WrapWithHeader("Unstaged changes", _unstagedList, 0));
        Grid.SetRow(stageButtons, 1);
        leftPanel.Children.Add(stageButtons);
        leftPanel.Children.Add(WrapWithHeader("Staged changes", _stagedList, 2));

        // ---- top region: left | right split ----
        Grid split = new()
        {
            ColumnDefinitions = new ColumnDefinitions("2*,3*"),
            Margin = new Thickness(6),
        };
        Grid.SetColumn(leftPanel, 0);
        GridSplitter splitter = new() { Width = 4, Background = Brush("App.Border", Brushes.Gray) };
        Grid.SetColumn(splitter, 1);
        splitter.HorizontalAlignment = HorizontalAlignment.Left;
        Border diffBorder = new()
        {
            Child = _diffScroll,
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 0, 0),
            ClipToBounds = true,
        };
        Grid.SetColumn(diffBorder, 1);
        split.Children.Add(leftPanel);
        split.Children.Add(diffBorder);
        split.Children.Add(splitter);

        // ---- BOTTOM: message + buttons + status ----
        _messageBox = new TextBox
        {
            AcceptsReturn = true,
            Watermark = "Enter commit message",
            MinHeight = 70,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
            FontFamily = Monospace,
        };
        _amendBox = new CheckBox { Content = "Amend commit", Margin = new Thickness(0, 0, 12, 0) };

        Button commitBtn = MakeButton("Commit", () => DoCommit(push: false));
        Button commitPushBtn = MakeButton("Commit & push", () => DoCommit(push: true));
        Button stashBtn = MakeButton("Stash staged changes", () => { }); stashBtn.IsEnabled = false;
        Button resetAllBtn = MakeButton("Reset all changes", () => DoReset(includeStaged: true));
        Button resetUnstagedBtn = MakeButton("Reset unstaged changes", () => DoReset(includeStaged: false));
        Button templatesBtn = MakeButton("Commit templates", () => { }); templatesBtn.IsEnabled = false;
        Button createBranchBtn = MakeButton("Create branch", () => { }); createBranchBtn.IsEnabled = false;
        Button optionsBtn = MakeButton("Options", () => { }); optionsBtn.IsEnabled = false;

        WrapPanel buttonRow = new()
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                commitBtn, commitPushBtn, _amendBox, stashBtn,
                resetAllBtn, resetUnstagedBtn, templatesBtn, createBranchBtn, optionsBtn,
            },
        };
        foreach (Control c in buttonRow.Children)
        {
            c.Margin = new Thickness(0, 0, 6, 4);
        }

        _statusText = new TextBlock
        {
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            Margin = new Thickness(0, 2, 0, 0),
        };

        StackPanel bottom = new()
        {
            Margin = new Thickness(6),
            Children = { _messageBox, buttonRow, _statusText },
        };

        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(split);
        Content = root;

        Reload();
    }

    public static async Task ShowAsync(Window owner, string repoPath, Action onCommitted)
    {
        CommitDialog dialog = new(repoPath);
        dialog.Committed += onCommitted;
        await dialog.ShowDialog(owner);
    }

    // ---------- list plumbing ----------

    private void OnSelected(ListBox source, bool staged)
    {
        if (source.SelectedItem is not WorkingDirFileRow row)
        {
            return;
        }

        // Clear the other list's selection so only one file drives the diff.
        if (staged)
        {
            _unstagedList.SelectedItem = null;
        }
        else
        {
            _stagedList.SelectedItem = null;
        }

        LoadDiff(row.Path, staged);
    }

    private void LoadDiff(string path, bool staged)
    {
        string repo = _repoPath;
        _ = Task.Run(() =>
        {
            try
            {
                GitModule module = GitContext.CreateModule(repo);
                var res = module.GitExecutable.Execute(
                    staged ? $"diff --cached -- \"{path}\"" : $"diff -- \"{path}\"",
                    throwOnErrorExit: false);
                return res.AllOutput;
            }
            catch (Exception ex)
            {
                return "Could not load diff: " + ex.Message;
            }
        }).ContinueWith(t =>
            Dispatcher.UIThread.Post(() => RenderDiff(t.Result)),
            TaskScheduler.Default);
    }

    private void RenderDiff(string diff)
    {
        InlineCollection inlines = new();
        IBrush add = Brush("App.DiffAdded", Brushes.LimeGreen);
        IBrush del = Brush("App.DiffRemoved", Brushes.OrangeRed);
        IBrush hunk = Brush("App.Accent", Brushes.DeepSkyBlue);
        IBrush normal = Brush("App.Foreground", Brushes.Gainsboro);

        foreach (string line in (diff ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            IBrush color = normal;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                color = hunk;
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                color = add;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                color = del;
            }

            inlines.Add(new Run(line + "\n") { Foreground = color });
        }

        _diffView.Inlines = inlines;
        _diffScroll.Offset = default;
    }

    // ---------- stage / unstage ----------

    private void StageSelected()
    {
        if (_unstagedList.SelectedItem is WorkingDirFileRow row)
        {
            RunGit(() => _service.Stage(_repoPath, new[] { row }));
        }
    }

    private void UnstageSelected()
    {
        if (_stagedList.SelectedItem is WorkingDirFileRow row)
        {
            RunGit(() => _service.Unstage(_repoPath, new[] { row }));
        }
    }

    private void StageAll()
    {
        var rows = _unstagedList.Items.OfType<WorkingDirFileRow>().ToList();
        if (rows.Count > 0)
        {
            RunGit(() => _service.Stage(_repoPath, rows));
        }
    }

    private void UnstageAll()
    {
        var rows = _stagedList.Items.OfType<WorkingDirFileRow>().ToList();
        if (rows.Count > 0)
        {
            RunGit(() => _service.Unstage(_repoPath, rows));
        }
    }

    // ---------- commit / reset ----------

    private void DoCommit(bool push)
    {
        int staged = _stagedList.Items.Count;
        string message = _messageBox.Text ?? string.Empty;
        if (staged == 0)
        {
            SetStatus("Nothing staged to commit.");
            return;
        }

        if (message.Trim().Length == 0)
        {
            SetStatus("Enter a commit message.");
            return;
        }

        bool amend = _amendBox.IsChecked == true;
        RunGitResult(
            () => _service.Commit(_repoPath, message, amend),
            async result =>
            {
                if (!result.Success)
                {
                    SetStatus("Commit failed: " + FirstLine(result.Output));
                    return;
                }

                _messageBox.Text = string.Empty;
                _amendBox.IsChecked = false;
                Committed?.Invoke();
                SetStatus("Committed.");
                Reload();

                if (push)
                {
                    await PushAsync();
                }
            });
    }

    private async Task PushAsync()
    {
        string repo = _repoPath;
        await GitProcessDialog.RunAsync(this, "Push", () =>
        {
            var remotes = new RemoteService().ListRemotes(repo);
            string remote = remotes.Count > 0 ? remotes[0].Name : "origin";
            string branch = new RemoteService().GetCurrentBranch(repo);
            var r = new RemoteService().Push(repo, remote, branch, false, null);
            return new GitProcessOutcome(r.Success, r.Output);
        });
    }

    private void DoReset(bool includeStaged)
    {
        if (includeStaged)
        {
            ConfirmThen(
                "Reset ALL changes? This discards staged and unstaged tracked changes and cannot be undone.",
                () => RunGit(() => _service.ResetChanges(_repoPath, includeStaged: true)));
            return;
        }

        RunGit(() => _service.ResetChanges(_repoPath, includeStaged: false));
    }

    // Simple in-dialog confirmation flyout on the status line via a modal child window.
    private async void ConfirmThen(string prompt, Action onConfirmed)
    {
        Window confirm = new()
        {
            Title = "Confirm",
            Width = 420,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", Brushes.DimGray),
        };

        bool ok = false;
        Button yes = MakeButton("Yes", () => { ok = true; confirm.Close(); });
        Button no = MakeButton("Cancel", confirm.Close);
        confirm.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = prompt,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("App.Foreground", Brushes.Gainsboro),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { yes, no },
                },
            },
        };

        await confirm.ShowDialog(this);
        if (ok)
        {
            onConfirmed();
        }
    }

    // ---------- shared execution ----------

    private void RunGit(Func<WorkingDirCommitResult> work)
        => RunGitResult(work, r =>
        {
            if (!r.Success)
            {
                SetStatus(FirstLine(r.Output));
            }

            Reload();
        });

    private void RunGitResult(Func<WorkingDirCommitResult> work, Action<WorkingDirCommitResult> onResult)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                return new WorkingDirCommitResult(false, ex.Message);
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            onResult(t.Result);
        }), TaskScheduler.Default);
    }

    private void Reload()
    {
        string repo = _repoPath;
        _ = Task.Run(() =>
        {
            try
            {
                return _service.LoadStatus(repo);
            }
            catch
            {
                return new WorkingDirStatus([], [], []);
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            WorkingDirStatus status = t.Result;
            _unstagedList.ItemsSource = status.Unstaged;
            _stagedList.ItemsSource = status.Staged;
            int staged = status.Staged.Count;
            int total = staged + status.Unstaged.Count;
            _statusText.Text = $"Staged {staged}/{total}";
        }), TaskScheduler.Default);
    }

    private void SetStatus(string text)
    {
        // Preserve the Staged x/y suffix by prefixing the hint.
        _statusText.Text = text;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        int i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }

    // ---------- ui helpers ----------

    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static ListBox MakeList() => new()
    {
        SelectionMode = SelectionMode.Single,
        FontFamily = Monospace,
        ClipToBounds = true,
    };

    private Control WrapWithHeader(string header, Control content, int row)
    {
        DockPanel panel = new() { Margin = new Thickness(0, 2) };
        TextBlock label = new()
        {
            Text = header,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            Margin = new Thickness(2, 0, 0, 2),
        };
        DockPanel.SetDock(label, Dock.Top);
        Border box = new()
        {
            Child = content,
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        };
        panel.Children.Add(label);
        panel.Children.Add(box);
        Grid.SetRow(panel, row);
        return panel;
    }

    private Button MakeButton(string text, Action onClick)
    {
        Button b = new() { Content = text };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
