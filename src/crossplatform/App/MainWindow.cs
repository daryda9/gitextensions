using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Views;

namespace GitExtensions.Avalonia;

/// <summary>A minimal ICommand for wiring key bindings to void actions.</summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

/// <summary>
///  Integrated main window modelled on the original GitExtensions FormBrowse:
///  a top toolbar, a left repository-objects tree (branches/remotes/tags/
///  stashes), the revision-grid DAG in the centre, a bottom detail/diff +
///  working-directory panel, and a status bar. All views are self-contained
///  <see cref="UserControl"/>s driven over the reused core via <see cref="GitContext"/>.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly MainMenu _menu = new();
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
    private readonly ExternalToolService _externalTools = new();
    private readonly BisectService _bisect = new();

    private readonly UiStateService _uiStateService = new();
    private readonly UiState _uiState;

    // Splitter-driven definitions we persist/restore (assigned in the ctor).
    private readonly ColumnDefinition _treeCol;
    private readonly RowDefinition _revRow;
    private readonly RowDefinition _bottomRow;
    private readonly RowDefinition _detailRow;
    private readonly RowDefinition _diffRow;

    private string? _repoPath;
    private string? _lastSelectedHash;

    // The commit chosen as the "BASE" for the grid's Compare actions (single-select
    // grid, so BASE + "Compare to BASE" together stand in for a two-commit compare).
    private string? _compareBaseHash;

    public MainWindow()
    {
        // Load persisted UI state first, and apply the remembered theme before
        // any App.* brushes are read below, so the window opens in that theme.
        _uiState = _uiStateService.Load();
        Theming.ThemeManager.Apply(_uiState.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);

        Title = "Git Extensions (Avalonia / Linux)";
        Width = _uiState.WindowWidth;
        Height = _uiState.WindowHeight;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        // ---- bottom panel: commit info (detail + diff) / working dir / blame / history
        _detailRow = new RowDefinition(new GridLength(_uiState.DetailStar, GridUnitType.Star));
        _diffRow = new RowDefinition(new GridLength(_uiState.DiffStar, GridUnitType.Star));
        Grid commitInfo = new()
        {
            RowDefinitions = new RowDefinitions
            {
                _detailRow,
                new RowDefinition(new GridLength(4, GridUnitType.Pixel)),
                _diffRow,
            },
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
        };
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
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            ClipToBounds = true,
            Items = { _commitInfoTab, _workingDirTab, _blameTab, _historyTab },
        };

        // ---- right side: revision grid over the bottom panel
        _revRow = new RowDefinition(new GridLength(_uiState.RevisionsStar, GridUnitType.Star));
        _bottomRow = new RowDefinition(new GridLength(_uiState.BottomStar, GridUnitType.Star));
        Grid right = new()
        {
            RowDefinitions = new RowDefinitions
            {
                _revRow,
                new RowDefinition(new GridLength(4, GridUnitType.Pixel)),
                _bottomRow,
            },
            ClipToBounds = true,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
        };
        GridSplitter rightSplit = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(_revisions, 0);
        Grid.SetRow(rightSplit, 1);
        Grid.SetRow(_bottom, 2);
        right.Children.Add(_revisions);
        right.Children.Add(rightSplit);
        right.Children.Add(_bottom);

        // ---- main area: left tree | right side
        _treeCol = new ColumnDefinition(new GridLength(_uiState.TreeWidth, GridUnitType.Pixel));
        Grid main = new()
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                _treeCol,
                new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
        };
        GridSplitter treeSplit = new() { Width = 4, VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(_tree, 0);
        Grid.SetColumn(treeSplit, 1);
        Grid.SetColumn(right, 2);
        main.Children.Add(_tree);
        main.Children.Add(treeSplit);
        main.Children.Add(right);

        DockPanel root = new() { Background = (IBrush)Application.Current!.Resources["App.Window"]! };
        DockPanel.SetDock(_menu, Dock.Top);
        DockPanel.SetDock(_toolbar, Dock.Top);
        DockPanel.SetDock(_statusBar, Dock.Bottom);
        root.Children.Add(_menu);
        root.Children.Add(_toolbar);
        root.Children.Add(_statusBar);
        root.Children.Add(main);
        Content = root;

        // Global shortcuts: F5 refresh, Ctrl+O open.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5), Command = new RelayCommand(RefreshAll) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Control), Command = new RelayCommand(() => _ = PickRepositoryAsync()) });

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

        // Persist window size + splitter positions when the window closes.
        Closing += (_, _) => PersistLayout();
    }

    // Captures the current window size and splitter panel sizes and saves them.
    private void PersistLayout()
    {
        try
        {
            _uiState.WindowWidth = Width;
            _uiState.WindowHeight = Height;
            _uiState.TreeWidth = _treeCol.Width.Value;
            _uiState.RevisionsStar = _revRow.Height.Value;
            _uiState.BottomStar = _bottomRow.Height.Value;
            _uiState.DetailStar = _detailRow.Height.Value;
            _uiState.DiffStar = _diffRow.Height.Value;
            _uiStateService.Save(_uiState);
        }
        catch
        {
            // Best-effort; never block window close on a persistence failure.
        }
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

        // Menu actions (mirror the toolbar + menu-only entries).
        _menu.OpenRepoRequested += () => _ = PickRepositoryAsync();
        _menu.CloneRequested += () => _ = CloneRepositoryAsync();
        _menu.InitRequested += () => _ = InitRepositoryAsync();
        _menu.OpenRecentRequested += repo => { if (Directory.Exists(repo)) OpenRepository(repo); };
        _menu.ExitRequested += Close;
        _menu.RefreshRequested += RefreshAll;
        _menu.ShowReflogRequested += () => _ = ShowReflogAsync();
        _menu.LightThemeRequested += () =>
        {
            Theming.ThemeManager.Apply(ThemeVariant.Light);
            _uiState.Theme = "Light";
            _uiStateService.Save(_uiState);
        };
        _menu.DarkThemeRequested += () =>
        {
            Theming.ThemeManager.Apply(ThemeVariant.Dark);
            _uiState.Theme = "Dark";
            _uiStateService.Save(_uiState);
        };
        _menu.FetchRequested += () => RunRemoteOp("Fetch", (s, r) => s.Fetch(_repoPath!, r, null).Success);
        _menu.PullRequested += () => RunRemoteOp("Pull", (s, r) => s.Pull(_repoPath!, r, rebase: false, null).Success);
        _menu.PushRequested += () => RunRemoteOp("Push", (s, r) =>
            s.Push(_repoPath!, r, new RemoteService().GetCurrentBranch(_repoPath!), force: false, null).Success);
        _menu.CommitRequested += () => _bottom.SelectedItem = _workingDirTab;
        _menu.StashRequested += () => RunOp("Stash", () => _stashOps.StashSave(_repoPath!, "WIP", includeUntracked: false).Success);
        _menu.NewBranchRequested += () => _ = NewBranchAsync();
        _menu.NewTagRequested += () => _ = NewTagAsync();
        _menu.CopyHashRequested += () =>
        {
            if (_lastSelectedHash is { Length: > 0 } h)
            {
                _ = Clipboard?.SetTextAsync(h);
            }
        };
        _menu.AboutRequested += () => _ = AboutDialog.ShowAsync(this);
        _menu.SettingsRequested += () => _ = OpenSettingsAsync();

        // Repository: file explorer + edit repo config files (created if absent).
        _menu.FileExplorerRequested += () => WithRepo(p => _externalTools.OpenPath(p));
        _menu.EditGitignoreRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".gitignore")));
        _menu.EditGitattributesRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".gitattributes")));
        _menu.EditMailmapRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".mailmap")));
        _menu.EditInfoExcludeRequested += () => WithRepo(p => _externalTools.OpenOrCreateFile(Path.Combine(p, ".git", "info", "exclude")));

        // Tools: terminal + external git GUIs, launched detached in the repo dir.
        _menu.GitBashRequested += () => WithRepo(p => _externalTools.OpenTerminal(p));
        _menu.GitKRequested += () => WithRepo(p => _externalTools.LaunchDetached("gitk", Array.Empty<string>(), p, "Launched gitk"));
        _menu.GitGuiRequested += () => WithRepo(p => _externalTools.LaunchDetached("git", new[] { "gui" }, p, "Launched git gui"));

        // Help: external documentation / project links (no repo required).
        _menu.UserManualRequested += () => Surface(_externalTools.OpenUrl("https://git-extensions-documentation.readthedocs.io/"));
        _menu.ReportIssueRequested += () => Surface(_externalTools.OpenUrl("https://github.com/gitextensions/gitextensions/issues"));
        _menu.ChangelogRequested += () => Surface(_externalTools.OpenUrl("https://github.com/gitextensions/gitextensions/releases"));
        _menu.DonateRequested += () => Surface(_externalTools.OpenUrl("https://opencollective.com/gitextensions"));

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
        _revisions.AddCommitCommand("Create branch here…", hash => _ = CreateBranchHereAsync(hash));
        _revisions.AddCommitCommand("Create tag here…", hash => _ = CreateTagHereAsync(hash));
        _revisions.AddCommitCommand("Revert this commit…", RevertThisCommit);
        _revisions.AddCommitCommand("Archive this commit…", hash => _ = ArchiveThisCommitAsync(hash));

        // History-rewriting commit edits on the current branch. Each is guarded by a
        // dirty-tree refusal + a confirm dialog, and rebase-backed paths abort cleanly
        // on failure so the repository is never left mid-rebase (see CommitEditService).
        _revisions.AddCommitCommand("Reword commit…", hash => _ = RewordCommitAsync(hash));
        _revisions.AddCommitCommand("Squash with previous…", hash => _ = SquashOrFixupAsync(hash, squash: true));
        _revisions.AddCommitCommand("Fixup with previous…", hash => _ = SquashOrFixupAsync(hash, squash: false));

        // Compare actions. The grid is single-select, so we mirror the original's
        // two-commit compare with a remembered BASE + "Compare to BASE" pair, plus
        // a direct commit-vs-working-tree compare. Results drive the shared DiffView.
        _revisions.AddCommitCommand("Select as BASE to compare", SelectCompareBase);
        _revisions.AddCommitCommand("Compare to BASE", CompareToBase);
        _revisions.AddCommitCommand("Compare to working directory", CompareToWorkingDirectory);

        // Bisect: mark the selected commit good/bad/skip (auto-starting a session
        // if none is in progress), plus a stop/reset entry. Each surfaces git's
        // output — the next commit to test, or the final "first bad commit".
        _revisions.AddCommitCommand("Bisect: mark good",
            hash => RunBisect("Bisect good", () => _bisect.MarkGood(_repoPath!, hash), ensureStarted: true));
        _revisions.AddCommitCommand("Bisect: mark bad",
            hash => RunBisect("Bisect bad", () => _bisect.MarkBad(_repoPath!, hash), ensureStarted: true));
        _revisions.AddCommitCommand("Bisect: skip",
            hash => RunBisect("Bisect skip", () => _bisect.Skip(_repoPath!, hash), ensureStarted: true));
        _revisions.AddCommitCommand("Bisect: stop/reset",
            _ => RunBisect("Bisect reset", () => _bisect.Reset(_repoPath!), ensureStarted: false));
    }

    // Opens the modal reflog browser; on a checkout from it, refreshes the main
    // view. Mirrors the other dialog-launch helpers (e.g. RemotesDialog).
    private async Task ShowReflogAsync()
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        ReflogWindow window = new(_repoPath);
        await window.ShowDialog(this);

        if (window.CheckedOut)
        {
            RefreshAll();
        }
    }

    // Runs a bisect step off the UI thread, optionally auto-starting a session
    // first, then surfaces git's output (next commit to test / first bad commit)
    // in the status bar and refreshes the grid. Never throws.
    private void RunBisect(string label, Func<BisectResult> op, bool ensureStarted)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            _statusBar.SetText($"{label}…");
            BisectResult result;
            try
            {
                result = await Task.Run(() =>
                {
                    if (ensureStarted && !_bisect.IsInProgress(_repoPath!))
                    {
                        BisectResult start = _bisect.Start(_repoPath!);
                        if (!start.Success)
                        {
                            return start;
                        }
                    }

                    return op();
                });
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"{label} failed: {ex.Message}");
                return;
            }

            RefreshAll();

            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText(result.Success
                ? (firstLine.Length > 0 ? $"{label}: {firstLine}" : $"{label} done.")
                : $"{label} failed: {firstLine}");
        }
    }

    // Reverts the selected commit on the current branch (git revert --no-edit).
    // Reuses the RunOp refresh pattern via the output-surfacing overload so a
    // revert that stops on a conflict shows the git output instead of crashing.
    private void RevertThisCommit(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        RunOp("Revert", () => new RevertArchiveService().Revert(_repoPath!, hash));
    }

    // Opens the archive dialog for the selected commit; on success reports the
    // written path in the status bar.
    private async Task ArchiveThisCommitAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        ArchiveDialog dlg = new(_repoPath, hash);
        await dlg.ShowDialog(this);

        if (dlg.ArchivedPath is { Length: > 0 } path)
        {
            string shortHash = hash.Length > 8 ? hash[..8] : hash;
            _statusBar.SetText($"Archived {shortHash} → {path}");
        }
    }

    // ---- commit editing (reword / squash / fixup) ----------------------------------

    // Shared safety gate for every history-rewriting edit: refuses when the working
    // tree is dirty (with a clear message) and otherwise asks the user to confirm the
    // rewrite. Returns true only when it is safe to proceed.
    private async Task<bool> GuardRewriteAsync(string label)
    {
        CommitEditService svc = new();
        bool dirty;
        try
        {
            dirty = await Task.Run(() => svc.IsWorkingTreeDirty(_repoPath!));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"{label} failed: {ex.Message}");
            return false;
        }

        if (dirty)
        {
            _statusBar.SetText($"{label} refused: you have uncommitted changes. Commit or stash them first.");
            return false;
        }

        return await ConfirmAsync("This rewrites history on the current branch. Continue?");
    }

    // Runs a commit-edit operation off the UI thread, then refreshes the grid and
    // surfaces success or the first line of git's output on failure. The service
    // already aborts a stuck rebase, so this never leaves a half-rebase behind.
    private async Task RunEditAsync(string label, Func<CommitEditResult> op)
    {
        _statusBar.SetText($"{label}…");
        CommitEditResult result;
        try
        {
            result = await Task.Run(op);
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"{label} failed: {ex.Message}");
            return;
        }

        RefreshAll();
        if (result.Success)
        {
            _statusBar.SetText($"{label} done.");
        }
        else
        {
            string firstLine = result.Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
            _statusBar.SetText($"{label} failed: {firstLine}");
        }
    }

    // Rewords the selected commit. HEAD is a plain `git commit --amend -m`; an older
    // commit uses a scripted non-interactive reword rebase. Prefills the current
    // message in a multi-line prompt.
    private async Task RewordCommitAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        if (!await GuardRewriteAsync("Reword"))
        {
            return;
        }

        CommitEditService svc = new();
        bool isHead;
        string current;
        try
        {
            isHead = await Task.Run(() => svc.IsHead(_repoPath!, hash));
            current = await Task.Run(() => svc.GetCommitMessage(_repoPath!, hash));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Reword failed: {ex.Message}");
            return;
        }

        string? message = await PromptAsync("Reword commit", "New commit message:", current.Trim(), multiline: true);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await RunEditAsync("Reword",
            () => isHead ? svc.AmendHead(_repoPath!, message) : svc.Reword(_repoPath!, hash, message));
    }

    // Squashes (prompting for a combined message) or fixes up (discarding the message)
    // the selected commit into its parent. Refuses on the root commit, which has no
    // previous commit to combine with.
    private async Task SquashOrFixupAsync(string hash, bool squash)
    {
        if (_repoPath is null)
        {
            return;
        }

        string label = squash ? "Squash" : "Fixup";
        CommitEditService svc = new();

        bool hasParent;
        try
        {
            hasParent = await Task.Run(() => svc.HasParent(_repoPath!, hash));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"{label} failed: {ex.Message}");
            return;
        }

        if (!hasParent)
        {
            _statusBar.SetText($"{label} not possible: the root commit has no previous commit to combine with.");
            return;
        }

        if (!await GuardRewriteAsync(label))
        {
            return;
        }

        if (squash)
        {
            string combined;
            try
            {
                combined = await Task.Run(() => svc.GetCombinedMessage(_repoPath!, hash));
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"Squash failed: {ex.Message}");
                return;
            }

            string? message = await PromptAsync("Squash with previous", "Combined commit message:", combined.Trim(), multiline: true);
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            await RunEditAsync("Squash", () => svc.Squash(_repoPath!, hash, message));
        }
        else
        {
            await RunEditAsync("Fixup", () => svc.Fixup(_repoPath!, hash));
        }
    }

    // Prompts for a branch name and creates it at the selected commit.
    private async Task CreateBranchHereAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("Create branch", "Branch name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        RunOp($"Create branch {name}",
            () => new BranchTagService().CreateBranch(_repoPath!, name.Trim(), startPoint: hash, checkout: false).Success);
    }

    // Prompts for a tag name (and optional message) and creates it at the commit.
    private async Task CreateTagHereAsync(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("Create tag", "Tag name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string? message = await PromptAsync("Create tag", "Message (leave blank for a lightweight tag):");

        RunOp($"Create tag {name}",
            () => new BranchTagService().CreateTag(_repoPath!, name.Trim(), commit: hash, message?.Trim() ?? string.Empty).Success);
    }

    // Runs an external-tool action that needs the current repo, surfacing its
    // result (or a "no repository" note) in the status bar. Never throws: the
    // service catches launch failures and returns them as a failed result.
    private void WithRepo(Func<string, ExternalToolResult> action)
    {
        if (_repoPath is null)
        {
            _statusBar.SetText("No repository is open.");
            return;
        }

        Surface(action(_repoPath));
    }

    // Reflects an external-tool result in the status bar; failures are reported
    // as text rather than thrown, so a missing tool never crashes the UI.
    private void Surface(ExternalToolResult result) => _statusBar.SetText(result.Message);

    private void OnRevisionSelected(string commitHash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _lastSelectedHash = commitHash;
        _detail.ShowCommit(_repoPath, commitHash);
        _diff.ShowCommit(_repoPath, commitHash);
        _bottom.SelectedItem = _commitInfoTab;
    }

    // Remembers the chosen commit as the comparison BASE (the "old" side of a
    // later "Compare to BASE"). Reported in the status bar as a short hash.
    private void SelectCompareBase(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _compareBaseHash = hash;
        string shortHash = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText($"Selected {shortHash} as compare BASE. Use \"Compare to BASE\" on another commit.");
    }

    // Diffs BASE..selected (BASE the "old" side) and renders the changed files +
    // per-file diffs in the shared DiffView. Hints in the status bar if no BASE set.
    private void CompareToBase(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        if (_compareBaseHash is not { Length: > 0 } baseHash)
        {
            _statusBar.SetText("No BASE selected. Right-click a commit and choose \"Select as BASE to compare\" first.");
            return;
        }

        _diff.ShowRange(_repoPath, baseHash, hash);
        _bottom.SelectedItem = _commitInfoTab;

        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText($"Comparing {shortBase} .. {shortOther}");
    }

    // Diffs the selected commit against the current working tree (git diff <hash>)
    // and renders the result in the shared DiffView.
    private void CompareToWorkingDirectory(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _diff.ShowAgainstWorkingDirectory(_repoPath, hash);
        _bottom.SelectedItem = _commitInfoTab;

        string shortHash = hash.Length > 8 ? hash[..8] : hash;
        _statusBar.SetText($"Comparing {shortHash} .. working tree");
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

    // Output-surfacing variant of RunOp: mirrors the same status→run→refresh
    // structure, but on failure shows the first line of the git output (e.g. a
    // revert conflict) rather than a generic message. Never crashes on conflict.
    private void RunOp(string label, Func<RevertArchiveResult> op)
    {
        if (_repoPath is null)
        {
            return;
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            _statusBar.SetText($"{label}…");
            RevertArchiveResult result;
            try
            {
                result = await Task.Run(op);
            }
            catch (Exception ex)
            {
                _statusBar.SetText($"{label} failed: {ex.Message}");
                return;
            }

            RefreshAll();
            if (!result.Success)
            {
                string firstLine = result.Output.Split('\n')[0].Trim();
                _statusBar.SetText($"{label} stopped: {firstLine} — see the panel output.");
            }
        }
    }

    private async Task NewTagAsync()
    {
        if (_repoPath is null)
        {
            return;
        }

        string? name = await PromptAsync("New tag", "Tag name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        RunOp($"Create tag {name}",
            () => new BranchTagService().CreateTag(_repoPath!, name.Trim(), "HEAD", string.Empty).Success);
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

    // Opens the modal Settings window over the main window, passing the current
    // repo path. Settings persists its own changes; afterwards we re-sync the
    // in-memory theme so PersistLayout() on close doesn't overwrite a change
    // the user made in the dialog.
    private async Task OpenSettingsAsync()
    {
        await SettingsWindow.ShowAsync(this, _repoPath);
        _uiState.Theme = _uiStateService.Load().Theme;
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
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Content = picker,
        };
        picker.RepositorySelected += repo =>
        {
            dlg.Close();
            OpenRepository(repo);
        };
        await dlg.ShowDialog(this);
    }

    // Shows the clone dialog; on success opens the freshly cloned repository
    // through the same path the picker uses (OpenRepository).
    private async Task CloneRepositoryAsync()
    {
        CloneDialog dlg = new();
        await dlg.ShowDialog(this);

        if (dlg.ClonedRepoPath is { Length: > 0 } repo && Directory.Exists(repo))
        {
            _statusBar.SetText($"Cloned into {repo}");
            OpenRepository(repo);
        }
    }

    // Picks a directory, runs git init in it off the UI thread, then opens the
    // new repository through OpenRepository (same as clone / picker).
    private async Task InitRepositoryAsync()
    {
        TopLevel? top = GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders =
            await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Choose a directory for the new repository",
            });

        if (folders.Count == 0)
        {
            return;
        }

        string? dir = folders[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(dir))
        {
            _statusBar.SetText("The selected folder has no local path.");
            return;
        }

        _statusBar.SetText("Initialising repository…");

        CloneInitResult result;
        try
        {
            result = await Task.Run(() => new CloneInitService().Init(dir));
        }
        catch (Exception ex)
        {
            _statusBar.SetText($"Init failed: {ex.Message}");
            return;
        }

        if (result.Success && result.RepoPath is not null)
        {
            _statusBar.SetText($"Initialised repository at {result.RepoPath}");
            OpenRepository(result.RepoPath);
        }
        else
        {
            _statusBar.SetText("Init failed — see output: " + result.Output);
        }
    }

    private void OpenRepository(string repoPath)
    {
        _repoPath = repoPath;
        WarmUpCore(repoPath);

        _revisions.LoadRepository(repoPath);
        _workingDir.LoadRepository(repoPath);
        _tree.LoadRepository(repoPath);
        _statusBar.LoadRepository(repoPath);
        _ = PopulateRecentAsync();
    }

    private async Task PopulateRecentAsync()
    {
        try
        {
            IReadOnlyList<string> recent = await new RecentRepositoriesService().LoadAsync();
            _menu.SetRecentRepositories(recent);
        }
        catch
        {
            // Non-fatal: the menu just shows "(none)".
        }
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
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
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

    // Text prompt; returns null on cancel. Optionally prefills an initial value and,
    // for multi-line input (e.g. commit messages), grows into a wrapping text area.
    private async Task<string?> PromptAsync(string title, string label, string? initial = null, bool multiline = false)
    {
        TextBox input = new() { Watermark = label, Text = initial };
        if (multiline)
        {
            input.AcceptsReturn = true;
            input.TextWrapping = TextWrapping.Wrap;
            input.MinHeight = 120;
        }
        Button ok = new() { Content = "OK", MinWidth = 80 };
        Button cancel = new() { Content = "Cancel", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        Window dlg = new()
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
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
