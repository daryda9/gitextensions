using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Push configuration dialog modelled on the original Git Extensions
///  <c>FormPush</c>. Rather than pushing immediately, it lets the user pick the
///  target (a configured remote OR an arbitrary URL) and what to push, then runs
///  the actual push (or a pull) through the shared <see cref="GitProcessDialog"/>
///  so the git output is visible live, with the same credential-prompt-and-retry
///  flow on authentication failure.
///
///  Layout mirrors the Windows dialog: a <c>Push to</c> group (Remote combo +
///  Manage remotes, Url combo + Browse…), a
///  <c>Push branches | Push tags | Push multiple branches</c> tab strip, and a
///  footer with <c>Pull</c> (left) and the accented <c>Push</c> (right).
///
///  Tabs:
///  <list type="bullet">
///   <item>Push branches — one local branch → one remote branch, plus options.</item>
///   <item>Push tags — every local tag listed with a checkbox, or <c>--tags</c>.</item>
///   <item>Push multiple branches — grid of local branches (select, destination
///    branch, ahead/behind) pushed with a single <c>git push</c>.</item>
///  </list>
///
///  Threading: every git call is made off the UI thread. The repository data
///  (remotes, branches, tags) is pre-loaded in <see cref="ShowAsync"/> and handed
///  to the constructor, because the git services block synchronously on async
///  work and deadlock when touched from the UI thread.
///
///  Only the chrome resolves from the shared App.* brushes via <see cref="Brush"/>.
/// </summary>
public sealed class PushDialog : Window
{
    private readonly string _repoPath;

    private readonly RadioButton _remoteRadio;
    private readonly RadioButton _urlRadio;
    private readonly ComboBox _remoteCombo;
    private readonly ComboBox _urlCombo;
    private readonly Button _browseBtn;

    private readonly TabControl _tabs;

    // Push branches tab.
    private readonly ComboBox _localBranchCombo;
    private readonly ComboBox _remoteBranchCombo;
    private readonly CheckBox _forceWithLease;
    private readonly CheckBox _pushAllTagsOption;
    private readonly CheckBox _recursiveSubmodules;

    // Push tags tab.
    private readonly CheckBox _tagsAll;
    private readonly CheckBox _tagsForce;
    private readonly StackPanel _tagsPanel;
    private readonly List<(string Name, CheckBox Check)> _tagChecks = [];
    private readonly TextBlock _tagsEmpty;

    // Push multiple branches tab.
    private readonly CheckBox _multiForce;
    private readonly CheckBox _multiSelectAll;
    private readonly StackPanel _multiPanel;
    private readonly List<MultiBranchRow> _multiRows = [];

    private bool _pushLaunched;
    private bool _suppressSelectAll;

    /// <summary>A row of the "Push multiple branches" grid.</summary>
    private sealed record MultiBranchRow(string Local, CheckBox Check, TextBox Destination);

    /// <summary>
    ///  Repository data the dialog needs, loaded OFF the UI thread before the
    ///  dialog is constructed. The remote/branch services block synchronously on
    ///  async git calls, so touching them on the UI thread deadlocks — hence the
    ///  pre-load in <see cref="ShowAsync"/>.
    /// </summary>
    private sealed record PushData(
        IReadOnlyList<RemoteRow> Remotes,
        string CurrentBranch,
        IReadOnlyList<string> LocalBranches,
        IReadOnlyList<PushTagRow> Tags,
        IReadOnlyList<PushBranchRow> BranchRows);

    private PushDialog(string repoPath, PushData data)
    {
        _repoPath = repoPath ?? string.Empty;

        Title = $"Push ({_repoPath})";
        Width = 660;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        string currentBranch = data.CurrentBranch;
        IReadOnlyList<string> localBranches = data.LocalBranches;

        // ---- Push to group ------------------------------------------------
        _remoteRadio = new RadioButton
        {
            Content = "Remote",
            GroupName = "PushTo",
            IsChecked = true,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _remoteCombo = new ComboBox
        {
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Button manageRemotes = MakeButton("Manage remotes");
        manageRemotes.Click += (_, _) => _ = OnManageRemotesAsync();

        _urlRadio = new RadioButton
        {
            Content = "Url",
            GroupName = "PushTo",
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Editable: the user may type any URL, and the dropdown offers the URLs
        // of the configured remotes as a starting point.
        _urlCombo = new ComboBox
        {
            IsEditable = true,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _browseBtn = MakeButton("Browse…");
        _browseBtn.Click += (_, _) => _ = OnBrowseAsync();

        PopulateRemotes(data.Remotes);

        _remoteRadio.IsCheckedChanged += (_, _) => UpdateTargetEnabled();
        _urlRadio.IsCheckedChanged += (_, _) => UpdateTargetEnabled();

        Grid pushToGrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8,
        };

        AddAt(pushToGrid, _remoteRadio, 0, 0);
        AddAt(pushToGrid, _remoteCombo, 0, 1);
        AddAt(pushToGrid, manageRemotes, 0, 2);
        AddAt(pushToGrid, _urlRadio, 1, 0);
        AddAt(pushToGrid, _urlCombo, 1, 1);
        AddAt(pushToGrid, _browseBtn, 1, 2);

        HeaderedContentControl pushToGroup = new()
        {
            Header = "Push to",
            Content = pushToGrid,
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        // ---- Tab 1: Push branches ------------------------------------------
        _localBranchCombo = new ComboBox { MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
        foreach (string b in localBranches)
        {
            _localBranchCombo.Items.Add(b);
        }

        _remoteBranchCombo = new ComboBox
        {
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
            IsEditable = true,
        };
        foreach (string b in localBranches)
        {
            _remoteBranchCombo.Items.Add(b);
        }

        // Default local branch = current; remote target = same name.
        SelectBranch(_localBranchCombo, currentBranch, localBranches);
        if (!string.IsNullOrEmpty(currentBranch))
        {
            _remoteBranchCombo.SelectedItem = _localBranchCombo.SelectedItem;
            if (_remoteBranchCombo.SelectedItem is null)
            {
                _remoteBranchCombo.Items.Add(currentBranch);
                _remoteBranchCombo.SelectedItem = currentBranch;
            }
        }

        // Keep the remote target in step with the local selection.
        _localBranchCombo.SelectionChanged += (_, _) =>
        {
            if (_localBranchCombo.SelectedItem is string name)
            {
                if (!_remoteBranchCombo.Items.Contains(name))
                {
                    _remoteBranchCombo.Items.Add(name);
                }
                _remoteBranchCombo.SelectedItem = name;
            }
        };

        StackPanel branchRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                Label("Branch to push"),
                _localBranchCombo,
                Label("to"),
                _remoteBranchCombo,
            },
        };

        _forceWithLease = MakeCheck("Force with lease (safe force)");
        _pushAllTagsOption = MakeCheck("Push all tags");
        _recursiveSubmodules = MakeCheck("Recursive submodules");

        Expander showOptions = new()
        {
            Header = "Show options",
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Margin = new Thickness(0, 6, 0, 0),
                Children = { _forceWithLease, _pushAllTagsOption, _recursiveSubmodules },
            },
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        StackPanel branchesTabContent = new()
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(6),
            Children = { branchRow, showOptions },
        };

        // ---- Tab 2: Push tags ----------------------------------------------
        _tagsAll = MakeCheck("Push all tags (--tags)");
        _tagsForce = MakeCheck("Force with lease (safe force)");
        _tagsAll.IsCheckedChanged += (_, _) => UpdateTagsEnabled();

        _tagsPanel = new StackPanel { Orientation = Orientation.Vertical };
        foreach (PushTagRow tag in data.Tags)
        {
            CheckBox cb = MakeCheck(string.IsNullOrEmpty(tag.ObjectId) ? tag.Name : $"{tag.Name}   {tag.ObjectId}");
            cb.Margin = new Thickness(2);
            _tagChecks.Add((tag.Name, cb));
            _tagsPanel.Children.Add(cb);
        }

        _tagsEmpty = new TextBlock
        {
            Text = "This repository has no local tags.",
            Margin = new Thickness(4),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            IsVisible = _tagChecks.Count == 0,
        };
        _tagsPanel.Children.Insert(0, _tagsEmpty);

        Button tagsSelectAll = MakeButton("Select all");
        Button tagsSelectNone = MakeButton("Select none");
        tagsSelectAll.Click += (_, _) => SetAllTags(true);
        tagsSelectNone.Click += (_, _) => SetAllTags(false);

        DockPanel tagsTabContent = new() { Margin = new Thickness(6) };
        StackPanel tagsFooter = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { tagsSelectAll, tagsSelectNone },
                },
                _tagsAll,
                _tagsForce,
            },
        };
        DockPanel.SetDock(tagsFooter, Dock.Bottom);
        StackPanel tagsHeader = new()
        {
            Orientation = Orientation.Vertical,
            Children = { Label("Tags to push") },
        };
        DockPanel.SetDock(tagsHeader, Dock.Top);
        tagsTabContent.Children.Add(tagsHeader);
        tagsTabContent.Children.Add(tagsFooter);
        tagsTabContent.Children.Add(Scroll(_tagsPanel));

        // ---- Tab 3: Push multiple branches ---------------------------------
        _multiPanel = new StackPanel { Orientation = Orientation.Vertical };
        foreach (PushBranchRow row in data.BranchRows)
        {
            CheckBox cb = new()
            {
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = string.Equals(row.Local, currentBranch, StringComparison.Ordinal),
            };
            cb.IsCheckedChanged += (_, _) => SyncSelectAll();

            TextBox dest = new()
            {
                Text = string.IsNullOrEmpty(row.Upstream) ? row.Local : StripRemote(row.Upstream),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 1, 4, 1),
            };

            Grid grid = MultiGrid();
            AddAt(grid, cb, 0, 0);
            AddAt(grid, Label(row.Local), 0, 1);
            AddAt(grid, dest, 0, 2);
            AddAt(grid, Label(row.Track), 0, 3);
            _multiPanel.Children.Add(grid);

            _multiRows.Add(new MultiBranchRow(row.Local, cb, dest));
        }

        if (_multiRows.Count == 0)
        {
            _multiPanel.Children.Add(new TextBlock
            {
                Text = "This repository has no local branches.",
                Margin = new Thickness(4),
                Foreground = Brush("App.TextDim", Brushes.Gray),
            });
        }

        _multiSelectAll = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        _multiSelectAll.IsCheckedChanged += (_, _) =>
        {
            if (_suppressSelectAll)
            {
                return;
            }

            bool on = _multiSelectAll.IsChecked == true;
            foreach (MultiBranchRow row in _multiRows)
            {
                row.Check.IsChecked = on;
            }
        };

        Grid multiHeader = MultiGrid();
        AddAt(multiHeader, _multiSelectAll, 0, 0);
        AddAt(multiHeader, Header("Local branch"), 0, 1);
        AddAt(multiHeader, Header("Remote branch"), 0, 2);
        AddAt(multiHeader, Header("Ahead/behind"), 0, 3);
        multiHeader.Margin = new Thickness(0, 0, 0, 4);

        _multiForce = MakeCheck("Force with lease (safe force)");

        DockPanel multiTabContent = new() { Margin = new Thickness(6) };
        DockPanel.SetDock(multiHeader, Dock.Top);
        DockPanel.SetDock(_multiForce, Dock.Bottom);
        _multiForce.Margin = new Thickness(0, 8, 0, 0);
        multiTabContent.Children.Add(multiHeader);
        multiTabContent.Children.Add(_multiForce);
        multiTabContent.Children.Add(Scroll(_multiPanel));

        SyncSelectAll();

        _tabs = new TabControl
        {
            Margin = new Thickness(0, 0, 0, 10),
            Items =
            {
                new TabItem { Header = "Push branches", Content = branchesTabContent },
                new TabItem { Header = "Push tags", Content = tagsTabContent },
                new TabItem { Header = "Push multiple branches", Content = multiTabContent },
            },
        };

        // ---- Footer -------------------------------------------------------
        Button pull = MakeButton("Pull");
        pull.Click += (_, _) => _ = OnPullAsync();

        Button push = MakeButton("Push");
        push.Background = Brush("App.Accent", new SolidColorBrush(Color.Parse("#007ACC")));
        push.Foreground = Brushes.White;
        push.Click += (_, _) => _ = OnPushAsync();

        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(pull, 0);
        Grid.SetColumn(push, 2);
        footer.Children.Add(pull);
        footer.Children.Add(push);

        // ---- Assemble -----------------------------------------------------
        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(pushToGroup, Dock.Top);
        body.Children.Add(footer);
        body.Children.Add(pushToGroup);
        body.Children.Add(_tabs);
        Content = body;

        UpdateTargetEnabled();
        UpdateTagsEnabled();
    }

    /// <summary>
    ///  Shows the push configuration dialog modally over <paramref name="owner"/>.
    ///  Returns <c>true</c> when a push (or pull) was launched through the process
    ///  dialog, <c>false</c> when the user simply closed it.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, string repoPath)
    {
        // Load remotes / branches / tags OFF the UI thread; the git services block
        // synchronously on async work and would deadlock the UI thread.
        PushData data = await Task.Run(() => LoadData(repoPath));
        PushDialog dialog = new(repoPath, data);
        await dialog.ShowDialog(owner);
        return dialog._pushLaunched;
    }

    private static PushData LoadData(string repoPath)
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

        IReadOnlyList<string> locals;
        try
        {
            locals = [.. new BranchTagService().LoadRefs(repoPath).Branches
                .Where(b => !b.IsRemote && !b.IsTag)
                .Select(b => b.Name)];
        }
        catch (Exception)
        {
            locals = [];
        }

        PushRefsListing listing;
        try
        {
            listing = new PushRefsService().Load(repoPath);
        }
        catch (Exception)
        {
            listing = new PushRefsListing([], []);
        }

        return new PushData(remoteRows, current, locals, listing.Tags, listing.Branches);
    }

    // --- Target (remote / url) -------------------------------------------

    private void PopulateRemotes(IReadOnlyList<RemoteRow> remotes)
    {
        string? keepRemote = _remoteCombo.SelectedItem as string;

        _remoteCombo.Items.Clear();
        _urlCombo.Items.Clear();

        foreach (RemoteRow r in remotes)
        {
            _remoteCombo.Items.Add(r.Name);
            string url = string.IsNullOrEmpty(r.PushUrl) ? r.FetchUrl : r.PushUrl;
            if (!string.IsNullOrEmpty(url) && !_urlCombo.Items.Contains(url))
            {
                _urlCombo.Items.Add(url);
            }
        }

        if (_remoteCombo.Items.Count == 0)
        {
            return;
        }

        // Restore the previous selection, else default to "origin", else first.
        int index = keepRemote is null ? -1 : _remoteCombo.Items.IndexOf(keepRemote);
        if (index < 0)
        {
            index = _remoteCombo.Items.IndexOf("origin");
        }

        _remoteCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void UpdateTargetEnabled()
    {
        bool byRemote = _remoteRadio.IsChecked == true;
        _remoteCombo.IsEnabled = byRemote;
        _urlCombo.IsEnabled = !byRemote;
        _browseBtn.IsEnabled = !byRemote;
    }

    /// <summary>The push target: the selected remote name, or the typed URL.</summary>
    private string Target()
        => _urlRadio.IsChecked == true
            ? (_urlCombo.SelectedItem as string ?? _urlCombo.Text ?? string.Empty).Trim()
            : (_remoteCombo.SelectedItem as string ?? string.Empty);

    /// <summary>True when the target is a URL rather than a configured remote.</summary>
    private bool TargetIsUrl() => _urlRadio.IsChecked == true;

    private async Task OnManageRemotesAsync()
    {
        try
        {
            RemotesDialog dialog = new(_repoPath);
            await dialog.ShowDialog(this);

            // Remotes may have been added / renamed / removed → reload the list
            // OFF the UI thread and repopulate both target combos.
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
        }
        catch (Exception)
        {
            // Never let the remotes editor break the push dialog.
        }
    }

    // "Browse…" picks a local directory (a bare repository / clone on disk) and
    // uses its path as the push URL — the same thing the Windows dialog does.
    private async Task OnBrowseAsync()
    {
        try
        {
            IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Select repository to push to", AllowMultiple = false });

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

    // --- Tags tab ---------------------------------------------------------

    private void SetAllTags(bool value)
    {
        foreach ((_, CheckBox cb) in _tagChecks)
        {
            cb.IsChecked = value;
        }
    }

    private void UpdateTagsEnabled()
    {
        // "--tags" pushes every tag, so the per-tag selection is meaningless then.
        bool individual = _tagsAll.IsChecked != true;
        foreach ((_, CheckBox cb) in _tagChecks)
        {
            cb.IsEnabled = individual;
        }
    }

    // --- Multiple branches tab -------------------------------------------

    private static Grid MultiGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("30,*,*,110"),
    };

    private void SyncSelectAll()
    {
        if (_multiRows.Count == 0)
        {
            return;
        }

        bool all = _multiRows.All(r => r.Check.IsChecked == true);
        _suppressSelectAll = true;
        _multiSelectAll.IsChecked = all;
        _suppressSelectAll = false;
    }

    // "origin/main" → "main": the destination column holds the branch name only.
    private static string StripRemote(string upstream)
    {
        int slash = upstream.IndexOf('/');
        return slash >= 0 && slash + 1 < upstream.Length ? upstream[(slash + 1)..] : upstream;
    }

    // --- Push / pull ------------------------------------------------------

    private async Task OnPushAsync()
    {
        string target = Target();
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        switch (_tabs.SelectedIndex)
        {
            case 1:
                await PushTagsAsync(target);
                break;
            case 2:
                await PushMultipleBranchesAsync(target);
                break;
            default:
                await PushSingleBranchAsync(target);
                break;
        }
    }

    private async Task PushSingleBranchAsync(string target)
    {
        string local = _localBranchCombo.SelectedItem as string ?? string.Empty;
        string remoteBranch = _remoteBranchCombo.SelectedItem as string
            ?? _remoteBranchCombo.Text
            ?? local;

        if (string.IsNullOrEmpty(remoteBranch))
        {
            remoteBranch = local;
        }

        if (string.IsNullOrEmpty(local) && string.IsNullOrEmpty(remoteBranch))
        {
            return;
        }

        bool force = _forceWithLease.IsChecked == true;
        bool allTags = _pushAllTagsOption.IsChecked == true;
        bool recurse = _recursiveSubmodules.IsChecked == true;
        string repo = _repoPath;

        // Read every control value HERE, on the UI thread: the operation lambda
        // below runs on a background thread and Avalonia throws on cross-thread
        // property access (the failure would surface as an empty "Failed" console).
        bool isUrl = TargetIsUrl();

        // The plain "push this branch to this remote" case keeps using the
        // long-standing RemoteService path; anything extra (URL target, --tags,
        // submodule recursion, a renamed destination) goes through the refspec
        // service, which builds the single equivalent `git push`.
        if (!isUrl && !allTags && !recurse
            && string.Equals(local, remoteBranch, StringComparison.Ordinal))
        {
            await RunPushAsync("Push", (emit, creds) =>
                new RemoteService().PushStreaming(repo, target, remoteBranch, force, emit, creds));
            return;
        }

        string refspec = string.IsNullOrEmpty(local) ? remoteBranch : $"{local}:refs/heads/{remoteBranch}";
        await RunPushAsync("Push", (emit, creds) => new PushRefsService().PushRefsStreaming(
            repo, target, [refspec], force, allTags, setUpstream: !isUrl, recurse, emit, creds));
    }

    private async Task PushTagsAsync(string target)
    {
        bool all = _tagsAll.IsChecked == true;
        List<string> refspecs = all
            ? []
            : [.. _tagChecks.Where(t => t.Check.IsChecked == true).Select(t => $"refs/tags/{t.Name}")];

        if (!all && refspecs.Count == 0)
        {
            return;
        }

        bool force = _tagsForce.IsChecked == true;
        string repo = _repoPath;
        await RunPushAsync("Push tags", (emit, creds) => new PushRefsService().PushRefsStreaming(
            repo, target, refspecs, force, allTags: all, setUpstream: false, recurseSubmodules: false, emit, creds));
    }

    private async Task PushMultipleBranchesAsync(string target)
    {
        List<string> refspecs = [];
        foreach (MultiBranchRow row in _multiRows)
        {
            if (row.Check.IsChecked != true)
            {
                continue;
            }

            string dest = (row.Destination.Text ?? string.Empty).Trim();
            if (dest.Length == 0)
            {
                dest = row.Local;
            }

            refspecs.Add($"{row.Local}:refs/heads/{dest}");
        }

        if (refspecs.Count == 0)
        {
            return;
        }

        // Snapshot the control values on the UI thread (see PushSingleBranchAsync).
        bool force = _multiForce.IsChecked == true;
        bool isUrl = TargetIsUrl();
        string repo = _repoPath;
        await RunPushAsync("Push branches", (emit, creds) => new PushRefsService().PushRefsStreaming(
            repo, target, refspecs, force, allTags: false, setUpstream: !isUrl, recurseSubmodules: false, emit, creds));
    }

    /// <summary>
    ///  Runs <paramref name="operation"/> through the shared process dialog (live
    ///  git output). Git runs strictly non-interactively, so when it fails for
    ///  lack of credentials the user is asked in-app and the SAME operation is
    ///  retried once with the credentials fed through a transient helper.
    /// </summary>
    private async Task RunPushAsync(string label, Func<Action<string>, GitCredentials?, RemoteOpResult> operation)
    {
        _pushLaunched = true;
        RemoteOpResult? res = null;

        await GitProcessDialog.RunStreamingAsync(this, label, emit =>
        {
            res = operation(emit, null);
            return new GitProcessOutcome(res.Success, res.Output);
        }, closeOnAuthFailure: true);

        if (res is { AuthFailed: true })
        {
            GitCredentials? creds = await CredentialsDialog.ShowAsync(this);
            if (creds is not null)
            {
                await GitProcessDialog.RunStreamingAsync(this, $"{label} (retry)", emit =>
                {
                    RemoteOpResult r = operation(emit, creds);
                    return new GitProcessOutcome(r.Success, r.Output);
                });
            }
        }

        Close();
    }

    private async Task OnPullAsync()
    {
        // Pull always goes to a configured remote (a bare URL has no tracking
        // configuration to merge into), so it ignores the Url radio.
        string remote = _remoteCombo.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrEmpty(remote))
        {
            return;
        }

        string repo = _repoPath;
        await RunPushAsync("Pull", (emit, creds) =>
            new RemoteService().PullStreaming(repo, remote, rebase: false, emit, creds));
    }

    // --- Helpers ----------------------------------------------------------

    private static void AddAt(Grid grid, Control child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private TextBlock Header(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = FontWeight.Bold,
        Foreground = Brush("App.TextDim", Brushes.Gray),
    };

    private static void SelectBranch(ComboBox combo, string branch, IReadOnlyList<string> known)
    {
        if (string.IsNullOrEmpty(branch))
        {
            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
            return;
        }

        if (!known.Contains(branch))
        {
            combo.Items.Add(branch);
        }
        combo.SelectedItem = branch;
        if (combo.SelectedItem is null && combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private CheckBox MakeCheck(string text) => new()
    {
        Content = text,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private Button MakeButton(string text) => new()
    {
        Content = text,
        MinWidth = 90,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = Brush("App.Control", Brushes.DimGray),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
