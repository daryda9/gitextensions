using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Pull configuration dialog modelled on the original Git Extensions
///  <c>FormPull</c>: the user picks the source (a configured remote, <c>[ All ]</c>,
///  or an arbitrary URL), the remote branch, what to do with the fetched commits
///  (merge / rebase / fetch only), the tag policy and the prune options, and the
///  dialog then runs the resulting <c>git pull</c>/<c>git fetch</c> through the
///  shared <see cref="GitProcessDialog"/> so the transfer progress is visible live,
///  with the same credential-prompt-and-retry flow on authentication failure.
///
///  <para>Layout mirrors the Windows dialog top to bottom: <c>Pull from</c>,
///  <c>Branch</c>, <c>Merge options</c>, <c>Tag options</c>, the two prune
///  checkboxes, and a footer with <c>Solve conflicts</c> | <c>Stash changes</c> |
///  <c>Auto stash</c> | the accented <c>Pull</c>. The left-hand illustration of the
///  merge/rebase scenarios is deliberately not ported.</para>
///
///  <para>The dialog produces a structured <see cref="PullOptions"/> and every git
///  command is built and run by <see cref="RemoteService"/> — the dialog itself
///  never assembles a command line.</para>
///
///  <para>Threading: the repository data (remotes, remote branches, current branch,
///  conflict state) is pre-loaded OFF the UI thread in <see cref="ShowAsync"/> and
///  handed to the constructor, because the git services block synchronously on
///  async work and deadlock when touched from the UI thread.</para>
/// </summary>
public sealed class PullDialog : Theming.ZoomWindow
{
    /// <summary>
    ///  How upstream shows "every remote" in the remotes combo. Selecting it makes
    ///  the pull a <c>git fetch --all</c>: merging or rebasing several remotes at
    ///  once is meaningless, so those two options are disabled while it is chosen
    ///  (<c>FormPull.PullFromRemoteCheckedChanged</c> does the same).
    /// </summary>
    private const string AllRemotesDisplay = "[ All ]";

    private readonly string _repoPath;
    private readonly string _currentBranch;
    private readonly IReadOnlyList<string> _remoteBranches;

    // Pull from.
    private readonly HeaderedContentControl _pullFromGroup;
    private readonly RadioButton _remoteRadio;
    private readonly RadioButton _urlRadio;
    private readonly ComboBox _remoteCombo;
    private readonly ComboBox _urlCombo;
    private readonly Button _manageRemotesBtn;
    private readonly Button _browseBtn;

    // Branch.
    private readonly HeaderedContentControl _branchGroup;
    private readonly TextBlock _localBranchLabel;
    private readonly TextBox _localBranchBox;
    private readonly TextBlock _remoteBranchLabel;
    private readonly ComboBox _remoteBranchCombo;

    // Merge options.
    private readonly HeaderedContentControl _mergeGroup;
    private readonly RadioButton _mergeRadio;
    private readonly RadioButton _rebaseRadio;
    private readonly RadioButton _fetchRadio;

    // Tag options.
    private readonly HeaderedContentControl _tagGroup;
    private readonly RadioButton _reachableTags;
    private readonly RadioButton _noTags;
    private readonly RadioButton _allTags;

    // Prune.
    private readonly CheckBox _prune;
    private readonly CheckBox _pruneTags;

    // Footer.
    private readonly Button _solveConflictsBtn;
    private readonly Button _stashBtn;
    private readonly CheckBox _autoStash;
    private readonly Button _pullBtn;

    private readonly Func<Task>? _solveConflicts;
    private readonly IReadOnlyList<string> _conflicts;
    private readonly bool _execute;

    // Set to the confirmed options once the user presses Pull; ShowAsync returns it.
    private PullOptions? _result;

    // Guards the prune/tag interlock so the two CheckedChanged handlers cannot
    // re-enter each other.
    private bool _syncingPrune;

    /// <summary>
    ///  Repository data the dialog needs, loaded OFF the UI thread before the dialog
    ///  is constructed (see the class remarks).
    /// </summary>
    private sealed record PullData(
        IReadOnlyList<RemoteRow> Remotes,
        string CurrentBranch,
        IReadOnlyList<string> RemoteBranches,
        IReadOnlyList<string> Conflicts);

    private PullDialog(string repoPath, PullData data, GitPullAction initialAction, Func<Task>? solveConflicts, bool execute)
    {
        _repoPath = repoPath ?? string.Empty;
        _currentBranch = data.CurrentBranch;
        _remoteBranches = data.RemoteBranches;
        _solveConflicts = solveConflicts;
        _conflicts = data.Conflicts;
        _execute = execute;

        Width = 720;

        // Tall enough that the four groups AND the two prune checkboxes are all
        // visible without scrolling (the ScrollViewer below is the safety net for
        // small screens and for translations that wrap the long radio captions onto
        // extra lines).
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // ---- Pull from ----------------------------------------------------
        _remoteRadio = MakeRadio("PullFrom");
        _remoteRadio.IsChecked = true;
        _urlRadio = MakeRadio("PullFrom");

        _remoteCombo = new ComboBox
        {
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Editable: any URL may be typed, with the configured remotes' URLs offered
        // as a starting point.
        _urlCombo = new ComboBox
        {
            IsEditable = true,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _manageRemotesBtn = MakeButton();
        _manageRemotesBtn.Click += (_, _) => _ = OnManageRemotesAsync();
        _browseBtn = MakeButton();
        _browseBtn.Click += (_, _) => _ = OnBrowseAsync();

        PopulateRemotes(data.Remotes);

        _remoteRadio.IsCheckedChanged += (_, _) => UpdateEnabledState();
        _urlRadio.IsCheckedChanged += (_, _) => UpdateEnabledState();
        _remoteCombo.SelectionChanged += (_, _) =>
        {
            PopulateRemoteBranches();
            UpdateEnabledState();
        };

        Grid pullFromGrid = Grid2Rows();
        AddAt(pullFromGrid, _remoteRadio, 0, 0);
        AddAt(pullFromGrid, _remoteCombo, 0, 1);
        AddAt(pullFromGrid, _manageRemotesBtn, 0, 2);
        AddAt(pullFromGrid, _urlRadio, 1, 0);
        AddAt(pullFromGrid, _urlCombo, 1, 1);
        AddAt(pullFromGrid, _browseBtn, 1, 2);
        _pullFromGroup = MakeGroup(pullFromGrid);

        // ---- Branch -------------------------------------------------------
        _localBranchLabel = Label(string.Empty);
        // TextBoxSurface: see OutputView — the Fluent per-state repaint beats the local
        // Background, so clicking this read-only field flipped its surface to pure
        // black (dark) / pure white (light).
        _localBranchBox = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                Text = _currentBranch,
                IsReadOnly = true,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                BorderBrush = Brush("App.Border", Brushes.Gray),
                BorderThickness = new Thickness(1),
            },
            Brush("App.PanelAlt", Brushes.DimGray),
            Brush("App.TextDim", Brushes.Gray));

        _remoteBranchLabel = Label(string.Empty);
        _remoteBranchCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        PopulateRemoteBranches();

        Grid branchGrid = Grid2Rows();
        AddAt(branchGrid, _localBranchLabel, 0, 0);
        AddAt(branchGrid, _localBranchBox, 0, 1);
        AddAt(branchGrid, _remoteBranchLabel, 1, 0);
        AddAt(branchGrid, _remoteBranchCombo, 1, 1);
        Grid.SetColumnSpan(_localBranchBox, 2);
        Grid.SetColumnSpan(_remoteBranchCombo, 2);
        _branchGroup = MakeGroup(branchGrid);

        // ---- Merge options -------------------------------------------------
        _mergeRadio = MakeRadio("MergeOptions");
        _rebaseRadio = MakeRadio("MergeOptions");
        _fetchRadio = MakeRadio("MergeOptions");
        _mergeRadio.IsChecked = true;
        _mergeRadio.IsCheckedChanged += (_, _) => UpdateEnabledState();
        _rebaseRadio.IsCheckedChanged += (_, _) => UpdateEnabledState();
        _fetchRadio.IsCheckedChanged += (_, _) => UpdateEnabledState();
        _mergeGroup = MakeGroup(Rows(_mergeRadio, _rebaseRadio, _fetchRadio));

        // ---- Tag options ---------------------------------------------------
        _reachableTags = MakeRadio("TagOptions");
        _noTags = MakeRadio("TagOptions");
        _allTags = MakeRadio("TagOptions");
        _reachableTags.IsChecked = true; // upstream's default (follow tagopt)
        _tagGroup = MakeGroup(Rows(_reachableTags, _noTags, _allTags));

        // ---- Prune ---------------------------------------------------------
        _prune = MakeCheck();
        _pruneTags = MakeCheck();

        // Upstream's interlock: clearing "prune" clears "prune tags"; setting
        // "prune tags" sets "prune" and forces "fetch all tags".
        _prune.IsCheckedChanged += (_, _) =>
        {
            if (_syncingPrune)
            {
                return;
            }

            _syncingPrune = true;
            _pruneTags.IsChecked = _prune.IsChecked == true && _pruneTags.IsChecked == true;
            _syncingPrune = false;
        };
        _pruneTags.IsCheckedChanged += (_, _) =>
        {
            if (_syncingPrune)
            {
                return;
            }

            _syncingPrune = true;
            if (_pruneTags.IsChecked == true)
            {
                _prune.IsChecked = true;
                _allTags.IsChecked = true;
            }

            _syncingPrune = false;
        };

        // ---- Footer --------------------------------------------------------
        _solveConflictsBtn = MakeButton();
        _solveConflictsBtn.Click += (_, _) => _ = OnSolveConflictsAsync();
        _stashBtn = MakeButton();
        _stashBtn.Click += (_, _) => _ = OnStashAsync();
        _autoStash = MakeCheck();
        _pullBtn = MakeButton();
        _pullBtn.Background = Brush("App.Accent", new SolidColorBrush(Color.Parse("#007ACC")));
        _pullBtn.Foreground = Brushes.White;
        _pullBtn.Click += (_, _) => _ = OnPullAsync();

        // A Grid (not a fixed-width horizontal StackPanel): translated captions are
        // routinely longer than the English ones, and the columns must grow with them.
        Grid footer = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        AddAt(footer, _solveConflictsBtn, 0, 0);
        AddAt(footer, _stashBtn, 0, 1);
        AddAt(footer, _autoStash, 0, 2);
        AddAt(footer, _pullBtn, 0, 4);
        _autoStash.VerticalAlignment = VerticalAlignment.Center;

        // ---- Assemble ------------------------------------------------------
        StackPanel stack = new()
        {
            Orientation = Orientation.Vertical,
            Children = { _pullFromGroup, _branchGroup, _mergeGroup, _tagGroup, _prune, _pruneTags },
        };
        _prune.Margin = new Thickness(4, 4, 0, 0);
        _pruneTags.Margin = new Thickness(4, 2, 0, 0);

        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Content = body;
        DialogKeys.InstallEscapeClose(this);

        // The keyboard starts on the button the dialog was opened to press.
        DialogKeys.FocusOnOpen(this, _pullBtn);

        ApplyInitialAction(initialAction);
        ApplyTranslations();

        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        UpdateEnabledState();
    }

    /// <summary>
    ///  Shows the pull configuration dialog modally over <paramref name="owner"/>.
    ///  Returns the options the user confirmed, or <c>null</c> when the dialog was
    ///  simply closed.
    /// </summary>
    /// <param name="owner">Window to own the modal.</param>
    /// <param name="repoPath">Active repository.</param>
    /// <param name="initialAction">
    ///  Preselects the merge/rebase/fetch radio (and, for
    ///  <see cref="GitPullAction.FetchAll"/> / <see cref="GitPullAction.FetchPruneAll"/>,
    ///  the <c>[ All ]</c> source and the prune box), the way <c>FormPull</c>'s
    ///  constructor does when it is opened for a specific pull action.
    /// </param>
    /// <param name="solveConflicts">
    ///  Optional handler for the "Solve conflicts" button — the port has no
    ///  conflict-resolution dialog of its own, so the host supplies one (e.g. it
    ///  opens its merge view). The button stays disabled when this is null or the
    ///  working tree has no conflicts.
    /// </param>
    /// <param name="execute">
    ///  When true (the default) the dialog runs the pull itself through
    ///  <see cref="GitProcessDialog"/> and <see cref="RemoteService"/> before
    ///  closing. Pass false to only collect the options and let the caller run them.
    /// </param>
    public static async Task<PullOptions?> ShowAsync(
        Window owner,
        string repoPath,
        GitPullAction initialAction = GitPullAction.Merge,
        Func<Task>? solveConflicts = null,
        bool execute = true)
    {
        // Load remotes / branches / conflict state OFF the UI thread: the git
        // services block synchronously on async work and would deadlock it.
        PullData data = await Task.Run(() => LoadData(repoPath));
        PullDialog dialog = new(repoPath, data, initialAction, solveConflicts, execute);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private static PullData LoadData(string repoPath)
    {
        RemoteService remotes = new();

        IReadOnlyList<RemoteRow> remoteRows;
        try
        {
            remoteRows = remotes.ListRemotes(repoPath);
        }
        catch (Exception)
        {
            remoteRows = [];
        }

        string current;
        try
        {
            current = remotes.GetCurrentBranch(repoPath) ?? string.Empty;
        }
        catch (Exception)
        {
            current = string.Empty;
        }

        IReadOnlyList<string> remoteBranches;
        try
        {
            remoteBranches = [.. new BranchTagService().LoadRefs(repoPath).Branches
                .Where(b => b.IsRemote && !b.IsTag)
                .Select(b => b.Name)
                .Distinct(StringComparer.Ordinal)];
        }
        catch (Exception)
        {
            remoteBranches = [];
        }

        IReadOnlyList<string> conflicts;
        try
        {
            conflicts = new WorkingDirectoryService().ListConflicts(repoPath);
        }
        catch (Exception)
        {
            conflicts = [];
        }

        return new PullData(remoteRows, current, remoteBranches, conflicts);
    }

    // --- Population -------------------------------------------------------

    private void PopulateRemotes(IReadOnlyList<RemoteRow> remotes)
    {
        string? keep = _remoteCombo.SelectedItem as string;

        _remoteCombo.Items.Clear();
        _urlCombo.Items.Clear();

        _remoteCombo.Items.Add(AllRemotesDisplay);
        foreach (RemoteRow r in remotes)
        {
            _remoteCombo.Items.Add(r.Name);
            string url = string.IsNullOrEmpty(r.FetchUrl) ? r.PushUrl : r.FetchUrl;
            if (!string.IsNullOrEmpty(url) && !_urlCombo.Items.Contains(url))
            {
                _urlCombo.Items.Add(url);
            }
        }

        // Restore the previous selection, else "origin", else the first real remote.
        int index = keep is null ? -1 : _remoteCombo.Items.IndexOf(keep);
        if (index < 0)
        {
            index = _remoteCombo.Items.IndexOf("origin");
        }

        if (index < 0 && _remoteCombo.Items.Count > 1)
        {
            index = 1;
        }

        _remoteCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    // The remote-branch dropdown lists the branches of the selected remote, with the
    // remote prefix stripped ("origin/main" → "main") because that is what git wants
    // as a fetch/pull refspec. Empty selection means "all branches", as upstream's
    // tooltip says.
    private void PopulateRemoteBranches()
    {
        string remote = SelectedRemoteName();
        string keep = _remoteBranchCombo.Text ?? string.Empty;

        _remoteBranchCombo.Items.Clear();

        IEnumerable<string> names = _remoteBranches;
        if (!string.IsNullOrEmpty(remote))
        {
            string prefix = remote + "/";
            names = names
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                .Select(n => n[prefix.Length..]);
        }

        foreach (string name in names.Where(n => n != "HEAD").Distinct(StringComparer.Ordinal))
        {
            _remoteBranchCombo.Items.Add(name);
        }

        // Default to no branch (pull everything the refspec configures), matching
        // the empty combo of the original dialog.
        _remoteBranchCombo.SelectedItem = null;
        _remoteBranchCombo.Text = _remoteBranchCombo.Items.Contains(keep) ? keep : string.Empty;
    }

    // Presets the radios/prune the way FormPull's constructor does for a given action.
    private void ApplyInitialAction(GitPullAction action)
    {
        switch (action)
        {
            case GitPullAction.Rebase:
                _rebaseRadio.IsChecked = true;
                break;
            case GitPullAction.Fetch:
                _fetchRadio.IsChecked = true;
                break;
            case GitPullAction.FetchAll:
                _fetchRadio.IsChecked = true;
                SelectAllRemotes();
                break;
            case GitPullAction.FetchPruneAll:
                _fetchRadio.IsChecked = true;
                SelectAllRemotes();
                _prune.IsChecked = true;
                _pruneTags.IsChecked = false;
                break;
            default:
                _mergeRadio.IsChecked = true;
                break;
        }
    }

    private void SelectAllRemotes()
    {
        int index = _remoteCombo.Items.IndexOf(AllRemotesDisplay);
        if (index >= 0)
        {
            _remoteCombo.SelectedIndex = index;
        }
    }

    // --- State ------------------------------------------------------------

    private bool PullFromUrl => _urlRadio.IsChecked == true;

    private bool IsPullAll
        => !PullFromUrl && string.Equals(_remoteCombo.SelectedItem as string, AllRemotesDisplay, StringComparison.Ordinal);

    // The selected remote's NAME, or empty for [ All ] / a URL source.
    private string SelectedRemoteName()
    {
        if (PullFromUrl || IsPullAll)
        {
            return string.Empty;
        }

        return _remoteCombo.SelectedItem as string ?? string.Empty;
    }

    private void UpdateEnabledState()
    {
        bool byUrl = PullFromUrl;
        _remoteCombo.IsEnabled = !byUrl;
        _manageRemotesBtn.IsEnabled = !byUrl;
        _urlCombo.IsEnabled = byUrl;
        _browseBtn.IsEnabled = byUrl;

        // Merging or rebasing "all remotes" makes no sense — upstream disables both.
        bool all = IsPullAll;
        _mergeRadio.IsEnabled = !all;
        _rebaseRadio.IsEnabled = !all;
        if (all && _fetchRadio.IsChecked != true)
        {
            _fetchRadio.IsChecked = true;
        }

        // Prune is a fetch-only option: `git pull` takes no --prune, and upstream
        // greys both boxes out as soon as merge or rebase is selected.
        bool fetchOnly = _fetchRadio.IsChecked == true;
        _prune.IsEnabled = fetchOnly;
        _pruneTags.IsEnabled = fetchOnly;

        // A pull into no branch cannot merge/rebase; only a fetch is meaningful.
        _remoteBranchCombo.IsEnabled = !all;

        // The port's local-branch box is read-only (a display of the checked-out
        // branch), so it is never a fetch target.
        _localBranchBox.IsEnabled = false;

        // Enabled whenever the working tree actually has conflicts: with no host
        // handler the button falls back to the configured merge tool (see OnSolveConflictsAsync).
        _solveConflictsBtn.IsEnabled = _conflicts.Count > 0;

        _pullBtn.Content = fetchOnly
            ? T("FormPull/_buttonFetch.Text", "Fetch")
            : T("FormPull/_buttonPull.Text", "Pull");
    }

    /// <summary>
    ///  The structured choice the dialog represents: exactly what
    ///  <see cref="RemoteService.PullStreaming(string, PullOptions, Action{string}, GitCredentials?)"/>
    ///  needs, and nothing about how the command is spelled.
    /// </summary>
    private PullOptions CurrentOptions()
    {
        bool byUrl = PullFromUrl;
        bool fetchOnly = _fetchRadio.IsChecked == true;

        GitPullAction action = fetchOnly
            ? (IsPullAll
                ? (_prune.IsChecked == true ? GitPullAction.FetchPruneAll : GitPullAction.FetchAll)
                : GitPullAction.Fetch)
            : _rebaseRadio.IsChecked == true ? GitPullAction.Rebase : GitPullAction.Merge;

        string source = byUrl
            ? (_urlCombo.SelectedItem as string ?? _urlCombo.Text ?? string.Empty).Trim()
            : IsPullAll ? PullOptions.AllRemotes : SelectedRemoteName();

        PullTagPolicy tags = _allTags.IsChecked == true
            ? PullTagPolicy.All
            : _noTags.IsChecked == true ? PullTagPolicy.None : PullTagPolicy.Default;

        return new PullOptions(
            Action: action,
            Remote: source,
            RemoteIsUrl: byUrl,
            RemoteBranch: (_remoteBranchCombo.SelectedItem as string ?? _remoteBranchCombo.Text ?? string.Empty).Trim(),
            LocalBranch: string.Empty,
            Tags: tags,
            Prune: fetchOnly && _prune.IsChecked == true,
            PruneTags: fetchOnly && _pruneTags.IsChecked == true,
            AutoStash: !fetchOnly && _autoStash.IsChecked == true);
    }

    // --- Actions ----------------------------------------------------------

    private async Task OnPullAsync()
    {
        // Every control value is read HERE, on the UI thread: the operation lambda
        // runs on a background thread and Avalonia throws on cross-thread property
        // access (which would surface only as an empty "Failed" console).
        PullOptions options = CurrentOptions();
        if (string.IsNullOrEmpty(options.EffectiveRemote))
        {
            return;
        }

        _result = options;

        if (!_execute)
        {
            Close();
            return;
        }

        string repo = _repoPath;
        string label = options.IsFetchOnly
            ? T("FormPull/_buttonFetch.Text", "Fetch")
            : T("FormPull/$this.Text", "Pull");

        RemoteOpResult? res = null;
        await GitProcessDialog.RunStreamingAsync(this, label, emit =>
        {
            res = new RemoteService().PullStreaming(repo, options, emit, credentials: null);
            return new GitProcessOutcome(res.Success, res.Output);
        }, closeOnAuthFailure: true);

        // Git runs strictly non-interactively, so an authentication failure is
        // answered in-app and the SAME operation retried once (as in PushDialog).
        if (res is { AuthFailed: true })
        {
            GitCredentials? creds = await CredentialsDialog.ShowAsync(this);
            if (creds is not null)
            {
                await GitProcessDialog.RunStreamingAsync(this, string.Format(T("{0} (retry)"), label), emit =>
                {
                    RemoteOpResult r = new RemoteService().PullStreaming(repo, options, emit, creds);
                    return new GitProcessOutcome(r.Success, r.Output);
                });
            }
        }

        // A pull that conflicts asks the question upstream asks before this dialog
        // disappears — otherwise the conflicted state has no surface at all here.
        await ConflictFlow.HandleAsync(this, repo);

        Close();
    }

    private async Task OnManageRemotesAsync()
    {
        try
        {
            RemotesDialog dialog = new(_repoPath);
            await dialog.ShowDialog(this);

            string repo = _repoPath;
            IReadOnlyList<RemoteRow> rows = await Task.Run(() =>
            {
                try
                {
                    return new RemoteService().ListRemotes(repo);
                }
                catch (Exception)
                {
                    return (IReadOnlyList<RemoteRow>)[];
                }
            });

            PopulateRemotes(rows);
            PopulateRemoteBranches();
        }
        catch (Exception)
        {
            // The remotes editor must never break the pull dialog.
        }
    }

    // "Browse…" picks a local repository on disk and uses its path as the pull URL.
    private async Task OnBrowseAsync()
    {
        try
        {
            IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = T("Select repository to pull from"),
                    AllowMultiple = false,
                });

            if (picked.Count == 0)
            {
                return;
            }

            string? path = picked[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!_urlCombo.Items.Contains(path))
            {
                _urlCombo.Items.Add(path);
            }

            _urlCombo.SelectedItem = path;
            _urlRadio.IsChecked = true;
        }
        catch (Exception)
        {
            // Picker unavailable (headless) → leave the URL as typed.
        }
    }

    // Upstream's "Solve conflicts" opens FormResolveConflicts, which the port does
    // not have. When the host supplies its own handler that wins; otherwise the
    // button falls back to the port's existing mechanism — launching the configured
    // merge tool for each conflicted path (detached, exactly as the working-directory
    // view does it) — so the button is never a dead end.
    private async Task OnSolveConflictsAsync()
    {
        Func<Task>? handler = _solveConflicts;
        if (handler is not null)
        {
            try
            {
                await handler();
            }
            catch (Exception)
            {
                // A failing host handler must not take the dialog down.
            }

            return;
        }

        IReadOnlyList<string> conflicts = _conflicts;
        string repo = _repoPath;
        await GitProcessDialog.RunAsync(this, T("FormPull/Mergetool.Text", "Solve conflicts"), () =>
        {
            WorkingDirectoryService service = new();
            System.Text.StringBuilder log = new();
            bool ok = true;
            foreach (string path in conflicts)
            {
                WorkingDirCommitResult r = service.LaunchMergetool(repo, path);
                ok &= r.Success;
                log.AppendLine(r.Output);
            }

            return new GitProcessOutcome(ok, log.ToString());
        });
    }

    // "Stash changes": stashes the working tree before pulling, the way upstream's
    // Stash button does (it calls UICommands.StashSave). Runs through the process
    // dialog so the git output is visible, and off the UI thread.
    private async Task OnStashAsync()
    {
        string repo = _repoPath;
        await GitProcessDialog.RunAsync(this, T("FormPull/Stash.Text", "Stash changes"), () =>
        {
            StashOpResult r = new StashOpsService().StashSave(repo, string.Empty, includeUntracked: false);
            return new GitProcessOutcome(r.Success, r.Output);
        });
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        Title = string.Format(T("FormPull/_formTitlePull.Text", "Pull ({0})"), CollapseHome(_repoPath));

        _pullFromGroup.Header = T("FormPull/GroupPullFrom.Text", "Pull from");
        Caption(_remoteRadio, T("FormPull/PullFromRemote.Text", "Remote"));
        Caption(_urlRadio, T("FormPull/PullFromUrl.Text", "URL"));
        _manageRemotesBtn.Content = T("FormPull/AddRemote.Text", "Manage remotes");
        // FormPull has no id of its own for this button: its label lives on the
        // shared FolderBrowserButton control.
        _browseBtn.Content = T("FolderBrowserButton/buttonBrowse.Text", "Browse…");
        ToolTip.SetTip(_remoteCombo, T("FormPull/PullFromRemote.Tooltip", "Remote repository to pull from"));
        ToolTip.SetTip(_urlCombo, T("FormPull/PullFromUrl.Tooltip", "Url to pull from"));

        _branchGroup.Header = T("FormPull/GroupBranch.Text", "Branch");
        _localBranchLabel.Text = T("FormPull/lblLocalBranch.Text", "Local branch");
        _remoteBranchLabel.Text = T("FormPull/lblRemoteBranch.Text", "Remote branch");
        ToolTip.SetTip(_remoteBranchCombo,
            T("FormPull/lblRemoteBranch.Tooltip", "Remote branch to pull. Leave empty to pull all branches."));

        _mergeGroup.Header = T("FormPull/GroupMergeOptions.Text", "Merge options");
        Caption(_mergeRadio, T("FormPull/Merge.Text", "Merge remote branch into current branch"));
        Caption(_rebaseRadio, T("FormPull/Rebase.Text",
            "Rebase current branch on top of remote branch, creates linear history (use with caution)"));
        Caption(_fetchRadio, T("FormPull/Fetch.Text", "Do not merge, only fetch remote changes"));

        _tagGroup.Header = T("FormPull/GroupTagOptions.Text", "Tag options");
        Caption(_reachableTags, T("FormPull/ReachableTags.Text",
            "Follow tagopt, if not specified, fetch tags reachable from remote HEAD"));
        Caption(_noTags, T("FormPull/NoTags.Text", "Fetch no tag"));
        Caption(_allTags, T("FormPull/AllTags.Text", "Fetch all tags"));

        Caption(_prune, T("FormPull/Prune.Text", "Prune remote branches"));
        ToolTip.SetTip(_prune, T("Removes remote tracking branches that no longer exist on the remote (--prune --force)"));
        Caption(_pruneTags, T("FormPull/PruneTags.Text", "Prune remote branches and tags"));
        ToolTip.SetTip(_pruneTags, T("FormPull/PruneTags.Tooltip",
            "Before fetching, remove any local tags that no longer exist on the remote if --prune is enabled."));

        _solveConflictsBtn.Content = T("FormPull/Mergetool.Text", "Solve conflicts");
        ToolTip.SetTip(_solveConflictsBtn, _conflicts.Count > 0
            ? T("FormPull/Mergetool.Text", "Solve conflicts")
            : T("The working tree has no merge conflicts."));
        _stashBtn.Content = T("FormPull/Stash.Text", "Stash changes");
        Caption(_autoStash, T("FormPull/AutoStash.Text", "Auto stash"));
        ToolTip.SetTip(_autoStash, T("Stash the working tree before pulling and re-apply it afterwards (--autostash)"));

        // Also refreshes the Pull/Fetch caption for the current radio state.
        UpdateEnabledState();
    }

    private static string CollapseHome(string path) => PathDisplay.CollapseHome(path);

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // --- Chrome helpers ---------------------------------------------------

    private static void AddAt(Grid grid, Control child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static Grid Grid2Rows() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        RowDefinitions = new RowDefinitions("Auto,Auto"),
        ColumnSpacing = 10,
        RowSpacing = 8,
    };

    private static StackPanel Rows(params Control[] children)
    {
        StackPanel panel = new() { Orientation = Orientation.Vertical, Spacing = 4 };
        foreach (Control child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private HeaderedContentControl MakeGroup(Control content) => new()
    {
        Content = content,
        Padding = new Thickness(10),
        Margin = new Thickness(0, 0, 0, 10),
        BorderBrush = Brush("App.Border", Brushes.Gray),
        BorderThickness = new Thickness(1),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    // Radios and checkboxes carry their caption in a wrapping TextBlock rather than
    // as a plain string: the merge/rebase/tag captions are long sentences, and the
    // Italian (and German) translations are longer still — a string Content would
    // simply be clipped at the dialog's edge. Captions are written through
    // <see cref="Caption"/>, which updates that TextBlock in place.
    private RadioButton MakeRadio(string group) => new()
    {
        GroupName = group,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        VerticalAlignment = VerticalAlignment.Center,
        Content = WrappingCaption(),
    };

    private CheckBox MakeCheck() => new()
    {
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        Content = WrappingCaption(),
    };

    private static TextBlock WrappingCaption() => new()
    {
        Text = string.Empty,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Writes a caption into a control created by MakeRadio / MakeCheck. Plain text,
    // so underscores are NOT escaped here (that doubling belongs to MenuItem headers,
    // whose string header goes through the access-key parser).
    private static void Caption(ContentControl control, string text)
    {
        if (control.Content is TextBlock block)
        {
            block.Text = text;
            return;
        }

        control.Content = text;
    }

    private Button MakeButton() => new()
    {
        MinWidth = 90,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = Brush("App.Control", Brushes.DimGray),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
