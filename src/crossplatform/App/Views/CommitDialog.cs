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
    private readonly CommitActionsService _actions = new();

    private readonly ListBox _unstagedList = MakeList();
    private readonly ListBox _stagedList = MakeList();
    private readonly TextBox _messageBox;
    private readonly CheckBox _amendBox;
    private readonly SelectableTextBlock _diffView;
    private readonly ScrollViewer _diffScroll;
    private readonly TextBlock _statusText;

    // Conflict (unmerged) support, mirroring the original commit form: unmerged
    // files show up in the unstaged list with a "U" status and get their own
    // context-menu entries, plus a banner while the merge is unresolved.
    private readonly Border _conflictBanner;
    private readonly MenuItem _mergetoolItem = new() { Header = "Open in mergetool" };
    private readonly MenuItem _takeOursItem = new() { Header = "Take ours" };
    private readonly MenuItem _takeTheirsItem = new() { Header = "Take theirs" };
    private readonly MenuItem _markResolvedItem = new() { Header = "Mark resolved" };
    private readonly HashSet<string> _conflictPaths = new(StringComparer.Ordinal);

    // Per-file actions on the unstaged menu. Like the conflict entries above, these
    // are created once and only their IsEnabled is touched while the menu opens.
    // Naming/order follow the original shared file-list menu (FileStatusList's
    // ItemContextMenu, which FormCommit binds): the reset entry sits right below
    // Stage, "Copy path" and the .gitignore block come last, each after a separator.
    // The original's "Reset file(s) to" is a submenu (index / parent); the port keeps
    // the single meaningful choice here — discard back to the index — and reuses the
    // wording already used by WorkingDirectoryView.
    private readonly MenuItem _discardItem = new() { Header = "Discard changes" };
    private readonly MenuItem _ignorePathItem = new() { Header = "Add to .gitignore" };
    private readonly MenuItem _ignoreExtItem = new() { Header = "Ignore by extension" };
    private readonly MenuItem _ignoreFolderItem = new() { Header = "Ignore in folder" };

    private bool _busy;

    // Options-menu state (mirrors the original commit form's Options dropdown).
    // Amend lives in _amendBox so the visible checkbox and the menu stay in sync.
    private bool _signOff;
    private bool _noVerify;
    private bool _resetAuthor;
    private bool _closeAfterCommit;

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
        _unstagedList.DoubleTapped += (_, _) => OnUnstagedDoubleTapped();
        _stagedList.DoubleTapped += (_, _) => UnstageSelected();

        // The Items are static and only their IsEnabled changes while opening —
        // adding/removing entries in Opening leaves the popup unmeasured (HANDOFF §3).
        _mergetoolItem.Click += (_, _) => OpenInMergetool();
        _takeOursItem.Click += (_, _) => ResolveConflicts("ours");
        _takeTheirsItem.Click += (_, _) => ResolveConflicts("theirs");
        _markResolvedItem.Click += (_, _) => ResolveConflicts("resolved");

        MenuItem stageItem = new() { Header = "Stage" };
        stageItem.Click += (_, _) => StageSelected();

        MenuItem unstagedCopyItem = new() { Header = "Copy path" };
        unstagedCopyItem.Click += (_, _) => CopySelectedPath(_unstagedList);

        _discardItem.Click += (_, _) => DiscardSelected();
        _ignorePathItem.Click += (_, _) => AddSelectedToGitignore(GitignoreMode.Path);
        _ignoreExtItem.Click += (_, _) => AddSelectedToGitignore(GitignoreMode.Extension);
        _ignoreFolderItem.Click += (_, _) => AddSelectedToGitignore(GitignoreMode.Folder);

        ContextMenu unstagedMenu = new()
        {
            ItemsSource = new Control[]
            {
                stageItem,
                _discardItem,
                new Separator(),
                _mergetoolItem, _takeOursItem, _takeTheirsItem, _markResolvedItem,
                new Separator(),
                unstagedCopyItem,
                new Separator(),
                _ignorePathItem, _ignoreExtItem, _ignoreFolderItem,
            },
        };
        unstagedMenu.Opening += (_, _) =>
        {
            bool conflict = SelectedConflicts().Count > 0;
            WorkingDirFileRow? row = _unstagedList.SelectedItem as WorkingDirFileRow;
            stageItem.IsEnabled = !conflict && row is not null;
            unstagedCopyItem.IsEnabled = row is not null;

            // "Reset file changes" only makes sense for a tracked, non-conflicted file:
            // untracked ones are handled by .gitignore / clean, never discarded here.
            _discardItem.IsEnabled = !conflict && row is not null && row.Status != "new";

            // The .gitignore entries mirror WorkingDirectoryView: a single UNTRACKED
            // file only, plus an extension / a parent folder where applicable.
            WorkingDirFileRow? untracked = SingleUntracked();
            string path = (untracked?.Path ?? string.Empty).Replace('\\', '/');
            _ignorePathItem.IsEnabled = untracked is not null;
            _ignoreExtItem.IsEnabled = untracked is not null
                && System.IO.Path.GetExtension(path).TrimStart('.').Length > 0;
            _ignoreFolderItem.IsEnabled = untracked is not null && path.LastIndexOf('/') > 0;

            _mergetoolItem.IsEnabled = conflict;
            _takeOursItem.IsEnabled = conflict;
            _takeTheirsItem.IsEnabled = conflict;
            _markResolvedItem.IsEnabled = conflict;
        };
        _unstagedList.ContextMenu = unstagedMenu;

        MenuItem unstageItem = new() { Header = "Unstage" };
        unstageItem.Click += (_, _) => UnstageSelected();
        MenuItem stagedCopyItem = new() { Header = "Copy path" };
        stagedCopyItem.Click += (_, _) => CopySelectedPath(_stagedList);
        ContextMenu stagedMenu = new()
        {
            ItemsSource = new Control[] { unstageItem, new Separator(), stagedCopyItem },
        };
        stagedMenu.Opening += (_, _) =>
        {
            bool has = _stagedList.SelectedItem is WorkingDirFileRow;
            unstageItem.IsEnabled = has;
            stagedCopyItem.IsEnabled = has;
        };
        _stagedList.ContextMenu = stagedMenu;

        _conflictBanner = new Border
        {
            Background = Brush("App.Accent", Brushes.DarkRed),
            Margin = new Thickness(6, 6, 6, 0),
            Padding = new Thickness(8, 4),
            IsVisible = false,
            ClipToBounds = true,
            Child = new TextBlock
            {
                Text = "There are unresolved merge conflicts. Right-click a file marked \"U\" "
                     + "in the unstaged list to open the mergetool, take ours/theirs or mark it resolved.",
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("App.Foreground", Brushes.Gainsboro),
            },
        };

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
        Button stashBtn = MakeButton("Stash staged changes", DoStashStaged);
        Button resetAllBtn = MakeButton("Reset all changes", () => DoReset(includeStaged: true));
        Button resetUnstagedBtn = MakeButton("Reset unstaged changes", () => DoReset(includeStaged: false));

        Button templatesBtn = new() { Content = "Commit templates ▾" };
        templatesBtn.Click += async (_, _) => await ShowTemplatesMenuAsync(templatesBtn);

        Button createBranchBtn = MakeButton("Create branch", PromptCreateBranch);

        Button optionsBtn = new() { Content = "Options ▾" };
        optionsBtn.Click += (_, _) => ShowOptionsMenu(optionsBtn);

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
        DockPanel.SetDock(_conflictBanner, Dock.Top);
        root.Children.Add(bottom);
        root.Children.Add(_conflictBanner);
        root.Children.Add(split);
        Content = root;

        Reload();
        RefreshBranchCaption();
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

    // ---------- per-file actions (discard / copy path / .gitignore) ----------

    // Discards the work-tree changes of the selected TRACKED file
    // (git checkout -- <path>). Destructive and not undoable, so it is confirmed
    // first, exactly like Take ours / Take theirs.
    private void DiscardSelected()
    {
        if (_unstagedList.SelectedItem is not WorkingDirFileRow row
            || row.Status == "new"
            || _conflictPaths.Contains(row.Path))
        {
            return;
        }

        string repo = _repoPath;
        string path = row.Path;
        ConfirmThen(
            $"Discard changes to '{path}'? The file is restored from the index and this cannot be undone.",
            () =>
            {
                SetStatus($"Discarding changes to {path} …");
                RunGitResult(
                    () => _service.ResetFile(repo, path),
                    result =>
                    {
                        SetStatus(result.Success
                            ? $"Discarded changes to {path}."
                            : "Discard failed: " + FirstLine(result.Output));
                        Reload();
                    });
            });
    }

    // Copies the selected file's repo-relative path to the clipboard. Nothing else
    // depends on it, so a missing clipboard (headless) is silently ignored.
    private void CopySelectedPath(ListBox list)
    {
        if (list.SelectedItem is not WorkingDirFileRow row)
        {
            return;
        }

        try
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(row.Path);
            SetStatus("Copied path: " + row.Path);
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy the path: " + ex.Message);
        }
    }

    private enum GitignoreMode
    {
        Path,
        Extension,
        Folder,
    }

    // The single selected UNTRACKED row (git "??" → status "new"), or null when the
    // selection is anything else. Same semantics as WorkingDirectoryView: the ignore
    // actions never apply to files git already tracks.
    private WorkingDirFileRow? SingleUntracked()
        => _unstagedList.SelectedItem is WorkingDirFileRow row && row.Status == "new"
            ? row
            : null;

    // Builds the .gitignore pattern for the selected untracked file and appends it,
    // then reloads so the now-ignored file drops out of the unstaged list.
    private void AddSelectedToGitignore(GitignoreMode mode)
    {
        WorkingDirFileRow? row = SingleUntracked();
        if (row is null)
        {
            return;
        }

        string path = row.Path.Replace('\\', '/');
        string pattern;
        switch (mode)
        {
            case GitignoreMode.Extension:
                string ext = System.IO.Path.GetExtension(path).TrimStart('.');
                if (ext.Length == 0)
                {
                    return;
                }

                pattern = "*." + ext;
                break;

            case GitignoreMode.Folder:
                int slash = path.LastIndexOf('/');
                if (slash <= 0)
                {
                    return;
                }

                pattern = path[..slash] + "/";
                break;

            default:
                // Anchor the exact relative path to the repo root with a leading '/'.
                pattern = "/" + path;
                break;
        }

        string repo = _repoPath;
        SetStatus($"Adding '{pattern}' to .gitignore …");
        RunGitResult(
            () => _service.AddToGitignore(repo, pattern),
            result =>
            {
                SetStatus(result.Success
                    ? FirstLine(result.Output)
                    : "Could not update .gitignore: " + FirstLine(result.Output));
                Reload();
            });
    }

    // ---------- merge conflicts ----------

    // Double-click stages a normal file, but opens the merge tool for an unmerged
    // one (staging a conflicted file would silently mark it resolved).
    private void OnUnstagedDoubleTapped()
    {
        if (SelectedConflicts().Count > 0)
        {
            OpenInMergetool();
            return;
        }

        StageSelected();
    }

    private List<string> SelectedConflicts()
        => [.. _unstagedList.SelectedItems?
            .OfType<WorkingDirFileRow>()
            .Select(r => r.Path)
            .Where(_conflictPaths.Contains) ?? []];

    // Launches the configured merge tool for each selected conflict (detached, off
    // the UI thread). No immediate reload: the tool runs asynchronously, so the user
    // marks the file resolved (or takes ours/theirs) once done.
    private void OpenInMergetool()
    {
        List<string> paths = SelectedConflicts();
        if (paths.Count == 0)
        {
            return;
        }

        string repo = _repoPath;
        SetStatus("Launching merge tool…");
        RunGitResult(
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
            result => SetStatus(result.Success
                ? "Merge tool launched. Mark resolved when done."
                : FirstLine(result.Output)));
    }

    // Resolves the selected conflicts with "ours", "theirs" or a plain mark-resolved
    // (git add), then reloads so the files lose their "U" status. Taking a side
    // overwrites the working-tree file, so it is confirmed first.
    private void ResolveConflicts(string mode)
    {
        List<string> paths = SelectedConflicts();
        if (paths.Count == 0)
        {
            return;
        }

        if (mode is "ours" or "theirs")
        {
            string side = mode == "ours" ? "our" : "their";
            ConfirmThen(
                $"Resolve {paths.Count} conflict(s) by keeping {side} version? "
                + "The other side is discarded in the working tree and cannot be undone.",
                () => RunResolve(mode, paths));
            return;
        }

        RunResolve(mode, paths);
    }

    private void RunResolve(string mode, List<string> paths)
    {
        string repo = _repoPath;
        SetStatus("Resolving conflict(s)…");
        RunGitResult(
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
                SetStatus(result.Success
                    ? mode switch
                    {
                        "ours" => $"Resolved {paths.Count} conflict(s) keeping our version.",
                        "theirs" => $"Resolved {paths.Count} conflict(s) keeping their version.",
                        _ => $"Marked {paths.Count} conflict(s) as resolved.",
                    }
                    : "Resolve failed: " + FirstLine(result.Output));
                Reload();
            });
    }

    // ---------- commit / reset ----------

    private CommitOptions CurrentOptions() => new(
        Amend: _amendBox.IsChecked == true,
        SignOff: _signOff,
        NoVerify: _noVerify,
        ResetAuthor: _resetAuthor,
        CloseAfterCommit: _closeAfterCommit);

    private void DoCommit(bool push)
    {
        int staged = _stagedList.Items.Count;
        string message = _messageBox.Text ?? string.Empty;
        CommitOptions options = CurrentOptions();

        if (_conflictPaths.Count > 0)
        {
            SetStatus("There are unresolved merge conflicts, solve merge conflicts before committing.");
            return;
        }

        if (staged == 0 && !options.Amend)
        {
            SetStatus("Nothing staged to commit.");
            return;
        }

        if (message.Trim().Length == 0)
        {
            SetStatus("Enter a commit message.");
            return;
        }

        SetStatus("Running " + CommitActionsService.DescribeCommit(options) + " …");
        RunActionResult(
            () => _actions.Commit(_repoPath, message, options),
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
                SetStatus("Committed (" + CommitActionsService.DescribeCommit(options) + ").");
                Reload();

                if (push)
                {
                    await PushAsync();
                }

                if (options.CloseAfterCommit)
                {
                    Close();
                }
            });
    }

    private async Task PushAsync()
    {
        string repo = _repoPath;
        await GitProcessDialog.RunStreamingAsync(this, "Push", emit =>
        {
            var remotes = new RemoteService().ListRemotes(repo);
            string remote = remotes.Count > 0 ? remotes[0].Name : "origin";
            string branch = new RemoteService().GetCurrentBranch(repo);
            var r = new RemoteService().PushStreaming(repo, remote, branch, false, emit, null);
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

    // ---------- stash staged ----------

    // `git stash push --staged -m <message>` (with a plumbing fallback for git < 2.35,
    // see CommitActionsService). Only the staged changes go to the stash; unstaged
    // edits stay in the working tree, so both lists are refreshed afterwards.
    private void DoStashStaged()
    {
        if (_stagedList.Items.Count == 0)
        {
            SetStatus("There are no staged changes to stash.");
            return;
        }

        string message = (_messageBox.Text ?? string.Empty).Trim();
        string stashMessage = message.Length > 0 ? FirstLine(message) : "Staged changes";

        SetStatus("Running git stash push --staged …");
        RunActionResult(
            () => _actions.StashStaged(_repoPath, stashMessage),
            result =>
            {
                SetStatus(result.Success
                    ? "Stashed staged changes: " + stashMessage
                    : "Stash failed: " + FirstLine(result.Output));
                Reload();
            });
    }

    // ---------- commit templates ----------

    // Templates are discovered off the UI thread (git config + repository scan),
    // and the MenuFlyout is fully populated BEFORE ShowAt — mutating Items while
    // the popup is open leaves it unmeasured (see HANDOFF §3).
    private async Task ShowTemplatesMenuAsync(Button anchor)
    {
        string repo = _repoPath;
        IReadOnlyList<CommitTemplate> templates = await Task.Run(() =>
        {
            try
            {
                return _actions.ListTemplates(repo);
            }
            catch
            {
                return (IReadOnlyList<CommitTemplate>)Array.Empty<CommitTemplate>();
            }
        });

        MenuFlyout flyout = new();
        if (templates.Count == 0)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = "No commit templates found",
                IsEnabled = false,
            });
        }
        else
        {
            foreach (CommitTemplate template in templates)
            {
                CommitTemplate captured = template;
                // Avalonia menu headers treat '_' as an access-key marker, so it
                // must be doubled to survive in file names like PULL_REQUEST_TEMPLATE.md.
                MenuItem item = new() { Header = Escape($"{captured.Name}  ({captured.Source})") };
                ToolTip.SetTip(item, captured.Path);
                item.Click += (_, _) => ApplyTemplate(captured);
                flyout.Items.Add(item);
            }
        }

        flyout.Items.Add(new Separator());
        MenuItem clear = new() { Header = "Clear message" };
        clear.Click += (_, _) =>
        {
            _messageBox.Text = string.Empty;
            SetStatus("Commit message cleared.");
        };
        flyout.Items.Add(clear);

        flyout.ShowAt(anchor);
    }

    private void ApplyTemplate(CommitTemplate template)
    {
        _ = Task.Run(() => CommitActionsService.ReadTemplate(template))
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                _messageBox.Text = t.Result;
                _messageBox.Focus();
                SetStatus("Applied commit template " + template.Name + ".");
            }), TaskScheduler.Default);
    }

    // ---------- create branch ----------

    // Prompts for a name, validates it with `git check-ref-format --branch` (plus a
    // duplicate check), then runs `git checkout -b <name> HEAD`, carrying the staged
    // and unstaged changes over to the new branch, exactly like the original form.
    private async void PromptCreateBranch()
    {
        Window prompt = new()
        {
            Title = "Create branch",
            Width = 440,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", Brushes.DimGray),
        };

        TextBox nameBox = new() { Watermark = "new-branch-name", Width = 400 };
        CheckBox checkoutBox = new() { Content = "Checkout after create", IsChecked = true };
        TextBlock error = new()
        {
            Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap,
        };

        string? chosen = null;
        bool checkout = true;

        Button create = new() { Content = "Create branch", IsDefault = true };
        Button cancel = MakeButton("Cancel", prompt.Close);
        create.Click += async (_, _) =>
        {
            string name = (nameBox.Text ?? string.Empty).Trim();
            create.IsEnabled = false;
            string? problem = await Task.Run(() =>
            {
                try
                {
                    return _actions.ValidateBranchName(_repoPath, name);
                }
                catch (Exception ex)
                {
                    return ex.Message;
                }
            });

            create.IsEnabled = true;
            if (problem is not null)
            {
                error.Text = problem;
                return;
            }

            chosen = name;
            checkout = checkoutBox.IsChecked == true;
            prompt.Close();
        };

        prompt.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "Create a new branch at the current HEAD:",
                    Foreground = Brush("App.Foreground", Brushes.Gainsboro),
                },
                nameBox,
                checkoutBox,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { create, cancel },
                },
            },
        };

        await prompt.ShowDialog(this);
        if (chosen is null)
        {
            return;
        }

        string branch = chosen;
        bool doCheckout = checkout;
        SetStatus($"Running git {(doCheckout ? "checkout -b" : "branch")} {branch} HEAD …");
        RunActionResult(
            () => _actions.CreateBranch(_repoPath, branch, doCheckout),
            result =>
            {
                SetStatus(result.Success
                    ? (doCheckout ? $"Created and checked out branch '{branch}'." : $"Created branch '{branch}'.")
                    : "Create branch failed: " + FirstLine(result.Output));
                RefreshBranchCaption();
                Reload();
            });
    }

    // ---------- options ----------

    // Every entry maps to a real `git commit` flag (except "Close dialog after
    // commit"), applied by CommitActionsService.Commit. The menu is rebuilt on each
    // click so the check marks always reflect the current state.
    private void ShowOptionsMenu(Button anchor)
    {
        MenuFlyout flyout = new();

        flyout.Items.Add(Toggle(
            "Amend last commit  (--amend)",
            _amendBox.IsChecked == true,
            v => _amendBox.IsChecked = v));
        flyout.Items.Add(Toggle(
            "Add sign-off  (--signoff)",
            _signOff,
            v => _signOff = v));
        flyout.Items.Add(Toggle(
            "Skip hooks  (--no-verify)",
            _noVerify,
            v => _noVerify = v));
        flyout.Items.Add(Toggle(
            "Reset author  (--reset-author, needs amend)",
            _resetAuthor,
            v => _resetAuthor = v));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Toggle(
            "Close dialog after commit",
            _closeAfterCommit,
            v => _closeAfterCommit = v));

        flyout.ShowAt(anchor);

        MenuItem Toggle(string text, bool value, Action<bool> set)
        {
            MenuItem item = new() { Header = (value ? "☑  " : "☐  ") + text };
            item.Click += (_, _) =>
            {
                set(!value);
                SetStatus("Commit command: " + CommitActionsService.DescribeCommit(CurrentOptions()));
            };
            return item;
        }
    }

    private void RefreshBranchCaption()
    {
        string repo = _repoPath;
        _ = Task.Run(() =>
        {
            try
            {
                return _actions.CurrentBranch(repo);
            }
            catch
            {
                return string.Empty;
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            Title = t.Result.Length > 0
                ? $"Commit to {t.Result} ({repo})"
                : "Commit";
        }), TaskScheduler.Default);
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

    // Same contract as RunGitResult for the CommitActionsService result type: the
    // work runs on the thread pool, the callback on the UI thread.
    private void RunActionResult(Func<CommitActionResult> work, Action<CommitActionResult> onResult)
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
                return new CommitActionResult(false, ex.Message);
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            onResult(t.Result);
        }), TaskScheduler.Default);
    }

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

            // Unmerged paths are shown inside the unstaged list with a "U" status,
            // like the original commit form, rather than in a separate panel.
            _conflictPaths.Clear();
            foreach (string path in status.Conflicts)
            {
                _conflictPaths.Add(path);
            }

            List<WorkingDirFileRow> unstaged = [.. status.Unstaged
                .Select(r => _conflictPaths.Contains(r.Path)
                    ? r with { Status = "U conflict" }
                    : r)];

            // Defensive: surface conflicts the work-tree listing may have missed.
            foreach (string path in status.Conflicts)
            {
                if (!unstaged.Any(r => string.Equals(r.Path, path, StringComparison.Ordinal)))
                {
                    unstaged.Insert(0, new WorkingDirFileRow(path, "U conflict", false));
                }
            }

            _unstagedList.ItemsSource = unstaged;

            // An unmerged path is reported by the index listing too; showing it in
            // both lists would be misleading, so it stays only in the unstaged one.
            _stagedList.ItemsSource = _conflictPaths.Count == 0
                ? status.Staged
                : [.. status.Staged.Where(r => !_conflictPaths.Contains(r.Path))];
            _conflictBanner.IsVisible = _conflictPaths.Count > 0;
            RenderStatus();
        }), TaskScheduler.Default);
    }

    // The last action message. It is kept across refreshes so the outcome of
    // stash / create-branch / commit is not wiped out by Reload's "Staged x/y".
    private string _statusHint = string.Empty;

    private void SetStatus(string text)
    {
        _statusHint = text ?? string.Empty;
        RenderStatus();
    }

    private void RenderStatus()
    {
        string counts = $"Staged {_stagedList.Items.Count}/{_stagedList.Items.Count + _unstagedList.Items.Count}";
        if (_conflictPaths.Count > 0)
        {
            counts += $"   —   {_conflictPaths.Count} conflict(s)";
        }

        _statusText.Text = _statusHint.Length > 0 ? $"{_statusHint}   —   {counts}" : counts;
    }

    // '_' in a menu header is an access-key marker in Avalonia; double it to show it.
    private static string Escape(string text) => text.Replace("_", "__");

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        // git often starts its output with a blank line (or with the hook's own
        // output), so return the first line that actually carries text.
        foreach (string line in s.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                return line.Trim();
            }
        }

        return string.Empty;
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
