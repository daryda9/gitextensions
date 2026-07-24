using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Push configuration dialog modelled on the original Git Extensions
///  <c>FormPush</c>. Rather than pushing immediately, it lets the user pick the
///  target remote and branch mapping, optionally force-with-lease, then runs the
///  actual push (or a pull) through the shared <see cref="GitProcessDialog"/> so
///  the git output is visible.
///
///  Layout mirrors the Windows dialog: a <c>Push to</c> group (Remote combo +
///  Manage remotes, and a disabled Url row for visual parity), a
///  <c>Push branches | Push tags | Push multiple branches</c> tab strip (only the
///  first is functional), a <c>Branch to push</c> row (local branch → remote
///  target), a <c>Show options</c> expander with Force-with-lease / Push all tags
///  / Recursive submodules, and a footer with <c>Pull</c> (left) and the accented
///  <c>Push</c> (right).
///
///  Only the chrome resolves from the shared App.* brushes via <see cref="Brush"/>.
/// </summary>
public sealed class PushDialog : Window
{
    private readonly string _repoPath;

    private readonly RadioButton _remoteRadio;
    private readonly ComboBox _remoteCombo;
    private readonly ComboBox _localBranchCombo;
    private readonly ComboBox _remoteBranchCombo;
    private readonly CheckBox _forceWithLease;

    private bool _pushLaunched;

    /// <summary>
    ///  Repository data the dialog needs, loaded OFF the UI thread before the
    ///  dialog is constructed. The remote/branch services block synchronously on
    ///  async git calls, so touching them on the UI thread deadlocks — hence the
    ///  pre-load in <see cref="ShowAsync"/>.
    /// </summary>
    private sealed record PushData(
        IReadOnlyList<string> Remotes,
        string CurrentBranch,
        IReadOnlyList<string> LocalBranches);

    private PushDialog(string repoPath, PushData data)
    {
        _repoPath = repoPath ?? string.Empty;

        Title = $"Push ({_repoPath})";
        Width = 620;
        Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        IReadOnlyList<string> remoteRows = data.Remotes;
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
        foreach (string r in remoteRows)
        {
            _remoteCombo.Items.Add(r);
        }

        // Default to "origin" if present, otherwise the first remote.
        int originIndex = -1;
        for (int i = 0; i < remoteRows.Count; i++)
        {
            if (string.Equals(remoteRows[i], "origin", StringComparison.Ordinal))
            {
                originIndex = i;
                break;
            }
        }
        if (_remoteCombo.Items.Count > 0)
        {
            _remoteCombo.SelectedIndex = originIndex >= 0 ? originIndex : 0;
        }

        Button manageRemotes = MakeButton("Manage remotes");
        // No trivial cross-file event exists to open a remotes editor here; keep
        // the control present for visual parity with a no-op.
        manageRemotes.IsEnabled = false;

        RadioButton urlRadio = new()
        {
            Content = "Url",
            GroupName = "PushTo",
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBox urlBox = new()
        {
            IsEnabled = false, // Url push is out of scope; present for parity.
            MinWidth = 240,
            Watermark = "git@host:owner/repo.git",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Button browseBtn = MakeButton("Browse…");
        browseBtn.IsEnabled = false;

        Grid pushToGrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        pushToGrid.ColumnSpacing = 10;
        pushToGrid.RowSpacing = 8;

        Grid.SetRow(_remoteRadio, 0);
        Grid.SetColumn(_remoteRadio, 0);
        Grid.SetRow(_remoteCombo, 0);
        Grid.SetColumn(_remoteCombo, 1);
        Grid.SetRow(manageRemotes, 0);
        Grid.SetColumn(manageRemotes, 2);
        Grid.SetRow(urlRadio, 1);
        Grid.SetColumn(urlRadio, 0);
        Grid.SetRow(urlBox, 1);
        Grid.SetColumn(urlBox, 1);
        Grid.SetRow(browseBtn, 1);
        Grid.SetColumn(browseBtn, 2);
        pushToGrid.Children.Add(_remoteRadio);
        pushToGrid.Children.Add(_remoteCombo);
        pushToGrid.Children.Add(manageRemotes);
        pushToGrid.Children.Add(urlRadio);
        pushToGrid.Children.Add(urlBox);
        pushToGrid.Children.Add(browseBtn);

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

        // ---- Tab strip ----------------------------------------------------
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
                new TextBlock
                {
                    Text = "Branch to push",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush("App.Text", Brushes.Gainsboro),
                },
                _localBranchCombo,
                new TextBlock
                {
                    Text = "to",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush("App.Text", Brushes.Gainsboro),
                },
                _remoteBranchCombo,
            },
        };

        // ---- Show options -------------------------------------------------
        _forceWithLease = MakeCheck("Force with lease (safe force)");
        CheckBox pushAllTags = MakeCheck("Push all tags");
        CheckBox recursiveSubmodules = MakeCheck("Recursive submodules");

        StackPanel optionsPanel = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { _forceWithLease, pushAllTags, recursiveSubmodules },
        };

        Expander showOptions = new()
        {
            Header = "Show options",
            Content = optionsPanel,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        StackPanel branchesTabContent = new()
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(6),
            Children = { branchRow, showOptions },
        };

        TabControl tabs = new()
        {
            Margin = new Thickness(0, 0, 0, 10),
            Items =
            {
                new TabItem { Header = "Push branches", Content = branchesTabContent },
                new TabItem
                {
                    Header = "Push tags",
                    Content = new TextBlock
                    {
                        Text = "Tag push is not yet available.",
                        Margin = new Thickness(10),
                        Foreground = Brush("App.TextDim", Brushes.Gray),
                    },
                },
                new TabItem
                {
                    Header = "Push multiple branches",
                    Content = new TextBlock
                    {
                        Text = "Multiple-branch push is not yet available.",
                        Margin = new Thickness(10),
                        Foreground = Brush("App.TextDim", Brushes.Gray),
                    },
                },
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
        body.Children.Add(tabs);
        Content = body;
    }

    /// <summary>
    ///  Shows the push configuration dialog modally over <paramref name="owner"/>.
    ///  Returns <c>true</c> when a push (or pull) was launched through the process
    ///  dialog, <c>false</c> when the user simply closed it.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, string repoPath)
    {
        // Load remotes / branches OFF the UI thread; the git services block
        // synchronously on async work and would deadlock the UI thread.
        PushData data = await Task.Run(() => LoadData(repoPath));
        PushDialog dialog = new(repoPath, data);
        await dialog.ShowDialog(owner);
        return dialog._pushLaunched;
    }

    private static PushData LoadData(string repoPath)
    {
        RemoteService remotes = new();

        IReadOnlyList<string> remoteNames;
        try
        {
            remoteNames = [.. remotes.ListRemotes(repoPath).Select(r => r.Name)];
        }
        catch (Exception)
        {
            remoteNames = [];
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

        return new PushData(remoteNames, current, locals);
    }

    private async Task OnPushAsync()
    {
        string remote = _remoteCombo.SelectedItem as string ?? string.Empty;
        string branch = _remoteBranchCombo.SelectedItem as string
            ?? (_remoteBranchCombo.SelectedItem?.ToString())
            ?? (_localBranchCombo.SelectedItem as string)
            ?? string.Empty;
        bool force = _forceWithLease.IsChecked == true;

        if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(branch))
        {
            return;
        }

        _pushLaunched = true;
        string repo = _repoPath;
        RemoteOpResult? res = null;
        await GitProcessDialog.RunStreamingAsync(this, "Push", emit =>
        {
            res = new RemoteService().PushStreaming(repo, remote, branch, force, emit, null);
            return new GitProcessOutcome(res.Success, res.Output);
        }, closeOnAuthFailure: true);

        // Git ran non-interactively; if it failed for lack of credentials, ask
        // the user for them in-app and retry the SAME push with the creds fed
        // through a transient credential helper.
        if (res is { AuthFailed: true })
        {
            GitCredentials? creds = await CredentialsDialog.ShowAsync(this);
            if (creds is not null)
            {
                await GitProcessDialog.RunStreamingAsync(this, "Push (retry)", emit =>
                {
                    RemoteOpResult r = new RemoteService().PushStreaming(repo, remote, branch, force, emit, creds);
                    return new GitProcessOutcome(r.Success, r.Output);
                });
            }
        }

        Close();
    }

    private async Task OnPullAsync()
    {
        string remote = _remoteCombo.SelectedItem as string ?? string.Empty;
        if (string.IsNullOrEmpty(remote))
        {
            return;
        }

        _pushLaunched = true;
        string repo = _repoPath;
        RemoteOpResult? res = null;
        await GitProcessDialog.RunStreamingAsync(this, "Pull", emit =>
        {
            res = new RemoteService().PullStreaming(repo, remote, rebase: false, emit, null);
            return new GitProcessOutcome(res.Success, res.Output);
        }, closeOnAuthFailure: true);

        if (res is { AuthFailed: true })
        {
            GitCredentials? creds = await CredentialsDialog.ShowAsync(this);
            if (creds is not null)
            {
                await GitProcessDialog.RunStreamingAsync(this, "Pull (retry)", emit =>
                {
                    RemoteOpResult r = new RemoteService().PullStreaming(repo, remote, rebase: false, emit, creds);
                    return new GitProcessOutcome(r.Success, r.Output);
                });
            }
        }

        Close();
    }

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
