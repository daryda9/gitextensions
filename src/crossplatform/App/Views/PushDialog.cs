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
    /// <summary>
    ///  One attempt at a push. <paramref name="forceOverride"/> lets the SAME
    ///  operation be replayed harder than the dialog's check boxes asked for —
    ///  that is what the "Force push with lease" recovery from a rejected push
    ///  needs, and it mirrors upstream mutating <c>form.ProcessArguments</c> to
    ///  insert <c>--force-with-lease</c> before calling <c>Retry()</c>.
    /// </summary>
    private delegate RemoteOpResult PushOperation(
        Action<string> emit,
        GitCredentials? credentials,
        PushForceMode? forceOverride);

    /// <summary>
    ///  What the "push was rejected" recovery needs to know to offer a pull. Non-null
    ///  only for the case upstream supports: the <em>current</em> branch being pushed
    ///  to a configured <em>remote</em> from the "Push branches" tab. A URL target has
    ///  no tracking configuration to pull from, and a branch that is not checked out
    ///  cannot be pulled into — upstream bails out of <c>HandlePushOnExit</c> for both.
    /// </summary>
    private sealed record PushRejectionContext(string Remote, string LocalBranch, string RemoteBranch);

    private readonly string _repoPath;
    private readonly string _currentBranch;

    private readonly RadioButton _remoteRadio;
    private readonly RadioButton _urlRadio;
    private readonly ComboBox _remoteCombo;
    private readonly ComboBox _urlCombo;
    private readonly Button _browseBtn;
    private readonly Button _manageRemotesBtn;
    private readonly HeaderedContentControl _pushToGroup;

    private readonly TabControl _tabs;
    private readonly TabItem _branchesTab;
    private readonly TabItem _tagsTab;
    private readonly TabItem _multiTab;

    // Push branches tab.
    private readonly ComboBox _localBranchCombo;
    private readonly ComboBox _remoteBranchCombo;
    private readonly CheckBox _forceWithLease;
    private readonly CheckBox _forcePush;
    private readonly CheckBox _replaceTrackingReference;
    private readonly CheckBox _pushAllTagsOption;
    private readonly CheckBox _recursiveSubmodules;
    private readonly TextBlock _branchFromLabel;
    private readonly TextBlock _branchToLabel;
    private readonly Expander _showOptions;

    // Push tags tab.
    private readonly CheckBox _tagsAll;
    private readonly CheckBox _tagsForce;
    private readonly StackPanel _tagsPanel;
    private readonly List<(string Name, CheckBox Check)> _tagChecks = [];
    private readonly TextBlock _tagsEmpty;
    private readonly TextBlock _tagsToPushLabel;
    private readonly Button _tagsSelectAll;
    private readonly Button _tagsSelectNone;

    // Push multiple branches tab.
    private readonly CheckBox _multiForceWithLease;
    private readonly CheckBox _multiForce;
    private readonly CheckBox _multiSelectAll;
    private readonly StackPanel _multiPanel;
    private readonly List<MultiBranchRow> _multiRows = [];
    private readonly TextBlock _multiLocalHeader;
    private readonly TextBlock _multiRemoteHeader;
    private readonly TextBlock _multiTrackHeader;
    private readonly TextBlock? _multiEmpty;

    // Footer.
    private readonly Button _pullBtn;
    private readonly Button _pushBtn;

    // Guards against a slow destination resolution overwriting a newer one.
    private int _destinationToken;

    private bool _pushLaunched;
    private bool _suppressSelectAll;
    private bool _suppressForceSync;

    /// <summary>
    ///  Branches that already exist on each configured remote, keyed by remote name
    ///  (<c>origin</c> → <c>main</c>, <c>feature/x</c>, …). Feeds the "Remote
    ///  branch" combo and the "this would create a NEW remote branch" confirmation.
    ///  Reloaded off the UI thread whenever the remote list changes.
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _remoteBranches;

    /// <summary>
    ///  Each local branch's configured upstream (<c>main</c> → <c>origin/main</c>),
    ///  empty when it has none. Read from the same <c>for-each-ref</c> snapshot the
    ///  "Push multiple branches" grid uses, so deciding whether a push should write a
    ///  tracking reference costs no extra git call.
    /// </summary>
    private readonly IReadOnlyDictionary<string, string> _branchUpstreams;

    /// <summary>Configured remote names, used to spot a local branch named after one.</summary>
    private IReadOnlyList<string> _remoteNames;

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
        IReadOnlyList<PushBranchRow> BranchRows,
        IReadOnlyDictionary<string, IReadOnlyList<string>> RemoteBranches,
        string InitialDestination);

    private PushDialog(string repoPath, PushData data)
    {
        _repoPath = repoPath ?? string.Empty;
        _currentBranch = data.CurrentBranch;
        _remoteBranches = data.RemoteBranches;
        _remoteNames = [.. data.Remotes.Select(r => r.Name)];

        Dictionary<string, string> upstreams = new(StringComparer.Ordinal);
        foreach (PushBranchRow row in data.BranchRows)
        {
            upstreams[row.Local] = row.Upstream ?? string.Empty;
        }

        _branchUpstreams = upstreams;

        Width = 660;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        string currentBranch = data.CurrentBranch;
        IReadOnlyList<string> localBranches = data.LocalBranches;

        // ---- Push to group ------------------------------------------------
        _remoteRadio = new RadioButton
        {
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

        _manageRemotesBtn = MakeButton();
        _manageRemotesBtn.Click += (_, _) => _ = OnManageRemotesAsync();

        _urlRadio = new RadioButton
        {
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

        _browseBtn = MakeButton();
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
        AddAt(pushToGrid, _manageRemotesBtn, 0, 2);
        AddAt(pushToGrid, _urlRadio, 1, 0);
        AddAt(pushToGrid, _urlCombo, 1, 1);
        AddAt(pushToGrid, _browseBtn, 1, 2);

        _pushToGroup = new HeaderedContentControl
        {
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

        // The destination combo lists the branches that already exist ON the
        // selected remote — NOT the local ones, which would invite pushing to a
        // wrongly-named (and silently created) remote branch. It stays editable
        // because creating a new remote branch is legitimate; that case is
        // confirmed explicitly before the push runs.
        _remoteBranchCombo = new ComboBox
        {
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
            IsEditable = true,
        };

        // Default local branch = current; destination = whatever the configuration
        // says it should be (pre-resolved off the UI thread in LoadData).
        SelectBranch(_localBranchCombo, currentBranch, localBranches);
        UpdateRemoteBranchCombo(data.InitialDestination);

        // The destination list belongs to the selected remote, so it is rebuilt
        // whenever the remote changes (upstream: UpdateRemoteBranchDropDown).
        _remoteCombo.SelectionChanged += (_, _) => ScheduleDestinationUpdate(discardTyped: false);

        // Keep the remote target in step with the local selection: picking another
        // branch to push retargets the destination (as upstream's
        // BranchSelectedValueChanged does), typed-over name included.
        _localBranchCombo.SelectionChanged += (_, _) => ScheduleDestinationUpdate(discardTyped: true);

        _branchFromLabel = Label(string.Empty);
        _branchToLabel = Label(string.Empty);
        StackPanel branchRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                _branchFromLabel,
                _localBranchCombo,
                _branchToLabel,
                _remoteBranchCombo,
            },
        };

        _forceWithLease = MakeCheck();
        _forcePush = MakeCheck();
        MakeExclusive(_forceWithLease, _forcePush);
        _replaceTrackingReference = MakeCheck();
        _pushAllTagsOption = MakeCheck();
        _recursiveSubmodules = MakeCheck();

        _showOptions = new Expander
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Margin = new Thickness(0, 6, 0, 0),
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children = { _forceWithLease, _forcePush },
                    },
                    _replaceTrackingReference,
                    _pushAllTagsOption,
                    _recursiveSubmodules,
                },
            },
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        StackPanel branchesTabContent = new()
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(6),
            Children = { branchRow, _showOptions },
        };

        // ---- Tab 2: Push tags ----------------------------------------------
        _tagsAll = MakeCheck();
        _tagsForce = MakeCheck();
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
            Margin = new Thickness(4),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            IsVisible = _tagChecks.Count == 0,
        };
        _tagsPanel.Children.Insert(0, _tagsEmpty);

        _tagsSelectAll = MakeButton();
        _tagsSelectNone = MakeButton();
        _tagsSelectAll.Click += (_, _) => SetAllTags(true);
        _tagsSelectNone.Click += (_, _) => SetAllTags(false);

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
                    Children = { _tagsSelectAll, _tagsSelectNone },
                },
                _tagsAll,
                _tagsForce,
            },
        };
        DockPanel.SetDock(tagsFooter, Dock.Bottom);
        _tagsToPushLabel = Label(string.Empty);
        StackPanel tagsHeader = new()
        {
            Orientation = Orientation.Vertical,
            Children = { _tagsToPushLabel },
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
            _multiEmpty = new TextBlock
            {
                Margin = new Thickness(4),
                Foreground = Brush("App.TextDim", Brushes.Gray),
            };
            _multiPanel.Children.Add(_multiEmpty);
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

        _multiLocalHeader = Header(string.Empty);
        _multiRemoteHeader = Header(string.Empty);
        _multiTrackHeader = Header(string.Empty);

        Grid multiHeader = MultiGrid();
        AddAt(multiHeader, _multiSelectAll, 0, 0);
        AddAt(multiHeader, _multiLocalHeader, 0, 1);
        AddAt(multiHeader, _multiRemoteHeader, 0, 2);
        AddAt(multiHeader, _multiTrackHeader, 0, 3);
        multiHeader.Margin = new Thickness(0, 0, 0, 4);

        _multiForceWithLease = MakeCheck();
        _multiForce = MakeCheck();
        MakeExclusive(_multiForceWithLease, _multiForce);

        StackPanel multiForceRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _multiForceWithLease, _multiForce },
        };

        DockPanel multiTabContent = new() { Margin = new Thickness(6) };
        DockPanel.SetDock(multiHeader, Dock.Top);
        DockPanel.SetDock(multiForceRow, Dock.Bottom);
        multiTabContent.Children.Add(multiHeader);
        multiTabContent.Children.Add(multiForceRow);
        multiTabContent.Children.Add(Scroll(_multiPanel));

        SyncSelectAll();

        _branchesTab = new TabItem { Content = branchesTabContent };
        _tagsTab = new TabItem { Content = tagsTabContent };
        _multiTab = new TabItem { Content = multiTabContent };
        _tabs = new TabControl
        {
            Margin = new Thickness(0, 0, 0, 10),
            Items = { _branchesTab, _tagsTab, _multiTab },
        };

        // ---- Footer -------------------------------------------------------
        _pullBtn = MakeButton();
        _pullBtn.Click += (_, _) => _ = OnPullAsync();

        _pushBtn = MakeButton();
        _pushBtn.Background = Brush("App.Accent", new SolidColorBrush(Color.Parse("#007ACC")));
        _pushBtn.Foreground = Brushes.White;
        _pushBtn.Click += (_, _) => _ = OnPushAsync();

        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(_pullBtn, 0);
        Grid.SetColumn(_pushBtn, 2);
        footer.Children.Add(_pullBtn);
        footer.Children.Add(_pushBtn);

        // ---- Assemble -----------------------------------------------------
        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_pushToGroup, Dock.Top);
        body.Children.Add(footer);
        body.Children.Add(_pushToGroup);
        body.Children.Add(_tabs);
        Content = body;
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();

        // The dialog is short-lived, so in practice it is only ever translated at
        // construction time; this keeps it consistent if the language is switched
        // from the main window while it happens to be open.
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        UpdateTargetEnabled();
        UpdateTagsEnabled();
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    // Every fixed caption of the dialog. Data-derived text (tag names, branch
    // names, tracking info) is never translated.
    private void ApplyTranslations()
    {
        Title = $"{T("FormPush/$this.Text", "Push")} ({CollapseHome(_repoPath)})";

        _pushToGroup.Header = T("FormPush/groupBox2.Text", "Push to");
        _remoteRadio.Content = T("FormPush/PushToRemote.Text", "Remote");
        _urlRadio.Content = T("FormPush/PushToUrl.Text", "Url");
        _manageRemotesBtn.Content = T("FormPush/AddRemote.Text", "Manage remotes");
        _browseBtn.Content = T("FormPush/folderBrowserButton1.Text", "Browse…");

        _branchesTab.Header = T("FormPush/BranchTab.Text", "Push branches");
        _tagsTab.Header = T("FormPush/TagTab.Text", "Push tags");
        _multiTab.Header = T("FormPush/MultipleBranchTab.Text", "Push multiple branches");

        _branchFromLabel.Text = T("FormPush/labelFrom.Text", "Branch to push");
        _branchToLabel.Text = T("FormPush/labelTo.Text", "to");
        _showOptions.Header = T("FormPush/ShowOptions.Text", "Show options");
        _forceWithLease.Content = ForceWithLeaseCaption;
        _forcePush.Content = ForcePushCaption;
        _replaceTrackingReference.Content = T("FormPush/ReplaceTrackingReference.Text", "Replace tracking reference");
        _pushAllTagsOption.Content = PushAllTagsCaption;
        _recursiveSubmodules.Content = T("FormPush/label2.Text", "Recursive submodules");

        _tagsToPushLabel.Text = T("FormPush/label1.Text", "Tags to push");
        _tagsSelectAll.Content = T("FormPush/selectAllToolStripMenuItem.Text", "Select all");
        _tagsSelectNone.Content = T("FormPush/unselectAllToolStripMenuItem.Text", "Select none");
        _tagsAll.Content = PushAllTagsCaption + " (--tags)";

        // Tags get a PLAIN force check box, not "force with lease": git cannot push
        // a tag with a lease, so the tags tab has one force option only — the same
        // single ForcePushTags check box the Windows dialog shows.
        _tagsForce.Content = ForcePushCaption + " (--force)";
        _tagsEmpty.Text = T("This repository has no local tags.");

        _multiLocalHeader.Text = T("FormPush/LocalColumn.HeaderText", "Local branch");
        _multiRemoteHeader.Text = T("FormPush/RemoteColumn.HeaderText", "Remote branch");
        _multiTrackHeader.Text = T("FormPush/NewColumn.HeaderText", "Ahead/behind");
        _multiForceWithLease.Content = ForceWithLeaseCaption;
        _multiForce.Content = ForcePushCaption;
        if (_multiEmpty is not null)
        {
            _multiEmpty.Text = T("This repository has no local branches.");
        }

        _pullBtn.Content = T("FormPush/Pull.Text", "Pull");
        _pushBtn.Content = T("FormPush/_pushCaption.Text", "Push");
    }

    // Upstream calls this simply "Force with lease"; the port's longer caption is
    // kept as the English fallback and replaced wholesale once translated.
    private static string ForceWithLeaseCaption
        => T("FormPush/ckForceWithLease.Text", "Force with lease (safe force)");

    private static string ForcePushCaption => T("FormPush/ForcePushBranches.Text", "Force push");

    private static string PushAllTagsCaption => T("Push all tags");

    // Abbreviates the home directory to "~", like the toolbar and the revision grid.
    // The local copy this dialog used to carry is gone: PathDisplay.CollapseHome is
    // now the single implementation shared by every caption that shows a repo path.
    private static string CollapseHome(string path) => PathDisplay.CollapseHome(path);

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

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

        // Resolve the destination for the branch and remote the dialog will open on,
        // here rather than in the constructor: the chain shells out to git, and the
        // services deadlock when called from the UI thread.
        string initialRemote = remoteRows.Select(r => r.Name).FirstOrDefault(n => n == "origin")
            ?? remoteRows.Select(r => r.Name).FirstOrDefault()
            ?? string.Empty;

        string destination = current;
        try
        {
            if (!string.IsNullOrEmpty(initialRemote) && !string.IsNullOrEmpty(current))
            {
                destination = new PushRefsService().ResolvePushDestination(repoPath, initialRemote, current);
            }
        }
        catch (Exception)
        {
            destination = current;
        }

        return new PushData(
            remoteRows,
            current,
            locals,
            listing.Tags,
            listing.Branches,
            LoadRemoteBranches(repoPath),
            string.IsNullOrEmpty(destination) ? current : destination);
    }

    // Branches known to exist on each remote. Must run OFF the UI thread (it shells
    // out to git), which is why it is only ever called from LoadData / a Task.Run.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadRemoteBranches(string repoPath)
    {
        try
        {
            return new PushRefsService().LoadRemoteBranches(repoPath);
        }
        catch (Exception)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }
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

    /// <summary>The local branch currently picked in the "Branch to push" combo.</summary>
    private string LocalBranchName() => _localBranchCombo.SelectedItem as string ?? string.Empty;

    /// <summary>
    ///  The destination branch as it will be pushed: the text of the editable combo
    ///  (so a name the user typed counts), falling back to the selected item.
    /// </summary>
    private string RemoteBranchName()
    {
        string typed = (_remoteBranchCombo.Text ?? string.Empty).Trim();
        return typed.Length > 0 ? typed : (_remoteBranchCombo.SelectedItem as string ?? string.Empty).Trim();
    }

    /// <summary>Branches known to exist on the currently selected remote.</summary>
    private IReadOnlyList<string> BranchesOnSelectedRemote()
        => _remoteCombo.SelectedItem is string remote
            && _remoteBranches.TryGetValue(remote, out IReadOnlyList<string>? list)
            ? list
            : [];

    /// <summary>
    ///  Rebuilds the "Remote branch" drop-down for the currently selected remote:
    ///  <paramref name="preferred"/> (the local branch name, the natural
    ///  destination) first, then every branch that already exists on that remote.
    ///  Mirrors <c>FormPush.UpdateRemoteBranchDropDown</c>.
    ///
    ///  Anything the user had typed is preserved, unless
    ///  <paramref name="discardTyped"/> asks for the destination to be retargeted;
    ///  otherwise the destination defaults to <paramref name="preferred"/>.
    /// </summary>
    /// <summary>
    ///  Re-resolves the push destination for the current branch/remote pair and then
    ///  rebuilds the drop-down. The resolution chain shells out to git, so it runs on
    ///  a background thread; the result is applied on the UI thread and only if it is
    ///  still the latest request, so a quick sequence of selection changes cannot
    ///  leave an earlier answer on screen.
    /// </summary>
    private void ScheduleDestinationUpdate(bool discardTyped)
    {
        string local = LocalBranchName();
        string remote = _remoteCombo.SelectedItem as string ?? string.Empty;

        if (local.Length == 0 || remote.Length == 0)
        {
            UpdateRemoteBranchCombo(local, discardTyped);
            return;
        }

        string repo = _repoPath;
        int token = ++_destinationToken;

        _ = Task.Run(() =>
        {
            try
            {
                return new PushRefsService().ResolvePushDestination(repo, remote, local);
            }
            catch (Exception)
            {
                return local;
            }
        }).ContinueWith(
            task =>
            {
                string destination = task.IsFaulted || string.IsNullOrEmpty(task.Result) ? local : task.Result;
                Dispatcher.UIThread.Post(() =>
                {
                    if (token == _destinationToken)
                    {
                        UpdateRemoteBranchCombo(destination, discardTyped);
                    }
                });
            },
            TaskScheduler.Default);
    }

    private void UpdateRemoteBranchCombo(string preferred, bool discardTyped = false)
    {
        // A destination the user typed by hand must survive the rebuild; a value we
        // put there ourselves (an item of the old list) must not, or switching
        // remote would keep pointing at the other remote's branch.
        string typed = !discardTyped && _remoteBranchCombo.SelectedItem is null
            ? (_remoteBranchCombo.Text ?? string.Empty).Trim()
            : string.Empty;

        _remoteBranchCombo.Items.Clear();

        if (preferred.Length > 0)
        {
            _remoteBranchCombo.Items.Add(preferred);
        }

        foreach (string branch in BranchesOnSelectedRemote())
        {
            if (!_remoteBranchCombo.Items.Contains(branch))
            {
                _remoteBranchCombo.Items.Add(branch);
            }
        }

        if (typed.Length > 0)
        {
            _remoteBranchCombo.SelectedItem = _remoteBranchCombo.Items.Contains(typed) ? typed : null;
            _remoteBranchCombo.Text = typed;
            return;
        }

        if (preferred.Length > 0)
        {
            _remoteBranchCombo.SelectedItem = preferred;
            _remoteBranchCombo.Text = preferred;
        }
        else if (_remoteBranchCombo.Items.Count > 0)
        {
            _remoteBranchCombo.SelectedIndex = 0;
        }
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

            // Remotes may have been added / renamed / removed → reload the list AND
            // each remote's branches OFF the UI thread, then repopulate the target
            // combos (which in turn rebuilds the destination-branch drop-down).
            string repo = _repoPath;
            (IReadOnlyList<RemoteRow> Rows, IReadOnlyDictionary<string, IReadOnlyList<string>> Branches) reloaded
                = await Task.Run(() =>
                {
                    IReadOnlyList<RemoteRow> list;
                    try
                    {
                        list = new RemoteService().ListRemotes(repo);
                    }
                    catch (Exception)
                    {
                        list = [];
                    }

                    return (list, LoadRemoteBranches(repo));
                });

            _remoteBranches = reloaded.Branches;
            _remoteNames = [.. reloaded.Rows.Select(r => r.Name)];
            PopulateRemotes(reloaded.Rows);

            // PopulateRemotes only fires SelectionChanged when the selection really
            // changed, so refresh the destination list unconditionally.
            ScheduleDestinationUpdate(discardTyped: false);
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
                new FolderPickerOpenOptions
                {
                    Title = T("FormPush/_selectDestinationDirectory.Text", "Select repository to push to"),
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
        string local = LocalBranchName();
        string remoteBranch = RemoteBranchName();

        if (string.IsNullOrEmpty(remoteBranch))
        {
            remoteBranch = local;
        }

        if (string.IsNullOrEmpty(local) && string.IsNullOrEmpty(remoteBranch))
        {
            return;
        }

        // Creating a branch on the remote is legitimate but easy to do by accident
        // (a typo in the editable destination combo), so it is confirmed — upstream
        // FormPush does the same before pushing an unknown destination.
        if (!TargetIsUrl() && !BranchesOnSelectedRemote().Contains(remoteBranch)
            && !await ConfirmNewRemoteBranchAsync([remoteBranch], target))
        {
            return;
        }

        PushForceMode? mode = await ResolveForceAsync(_forceWithLease, _forcePush);
        if (mode is not { } force)
        {
            return;
        }

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
        // The recovery from a rejected push only applies where upstream applies it:
        // the current branch, pushed to a configured remote (see PushRejectionContext).
        PushRejectionContext? rejection = !isUrl && !string.IsNullOrEmpty(local)
            && string.Equals(local, _currentBranch, StringComparison.Ordinal)
                ? new PushRejectionContext(target, local, remoteBranch)
                : null;

        // Cancelling the tracking question abandons the whole push, as upstream does.
        if (await ResolveTrackingAsync(local, remoteBranch, isUrl) is not { } track)
        {
            return;
        }

        if (!isUrl && !allTags && !recurse
            && string.Equals(local, remoteBranch, StringComparison.Ordinal))
        {
            await RunPushAsync(
                T("FormPush/_pushCaption.Text", "Push"),
                (emit, creds, over) => new RemoteService().PushStreaming(repo, target, remoteBranch, over ?? force, track, emit, creds),
                rejection);
            return;
        }

        string refspec = string.IsNullOrEmpty(local) ? remoteBranch : $"{local}:refs/heads/{remoteBranch}";
        await RunPushAsync(
            T("FormPush/_pushCaption.Text", "Push"),
            (emit, creds, over) => new PushRefsService().PushRefsStreaming(
                repo, target, [refspec], over ?? force, allTags, setUpstream: track, recurse, emit, creds),
            rejection);
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

        // TAGS CANNOT BE FORCE-PUSHED WITH A LEASE: git only honours
        // --force-with-lease for branches, so a leased tag push either errors or
        // silently does nothing. Upstream therefore maps its tag force check box to
        // plain ForcePushOptions.Force (FormPush.GetForcePushOption), and so do we.
        PushForceMode force = _tagsForce.IsChecked == true ? PushForceMode.Force : PushForceMode.None;
        string repo = _repoPath;
        await RunPushAsync(T("FormPush/TagTab.Text", "Push tags"), (emit, creds, over) => new PushRefsService().PushRefsStreaming(
            repo, target, refspecs, over ?? force, allTags: all, setUpstream: false, recurseSubmodules: false, emit, creds));
    }

    private async Task PushMultipleBranchesAsync(string target)
    {
        List<string> refspecs = [];
        List<string> created = [];
        IReadOnlyList<string> known = BranchesOnSelectedRemote();
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
            if (!known.Contains(dest))
            {
                created.Add(dest);
            }
        }

        if (refspecs.Count == 0)
        {
            return;
        }

        // Same guard as the single-branch tab: a destination that does not exist on
        // the remote yet would be CREATED there, so say so before doing it.
        if (!TargetIsUrl() && created.Count > 0
            && !await ConfirmNewRemoteBranchAsync(created, target))
        {
            return;
        }

        PushForceMode? mode = await ResolveForceAsync(_multiForceWithLease, _multiForce);
        if (mode is not { } force)
        {
            return;
        }

        // Snapshot the control values on the UI thread (see PushSingleBranchAsync).
        string repo = _repoPath;

        // NO -u here, deliberately. Upstream's multi-branch command builder
        // (Commands.PushMultiple) takes no `track` argument at all, so this tab never
        // rewrites tracking configuration. The port used to pass -u for every
        // selected branch, which silently re-pointed the upstream of each one at the
        // destination typed in the grid — a side effect nothing on this tab announces.
        await RunPushAsync(T("FormPush/BranchTab.Text", "Push branches"), (emit, creds, over) => new PushRefsService().PushRefsStreaming(
            repo, target, refspecs, over ?? force, allTags: false, setUpstream: false, recurseSubmodules: false, emit, creds));
    }

    /// <summary>
    ///  Runs <paramref name="operation"/> through the shared process dialog (live
    ///  git output). Git runs strictly non-interactively, so when it fails for
    ///  lack of credentials the user is asked in-app and the SAME operation is
    ///  retried once with the credentials fed through a transient helper.
    /// </summary>
    private async Task RunPushAsync(string label, PushOperation operation, PushRejectionContext? rejection = null)
    {
        _pushLaunched = true;

        // The LAST attempt's result — a rejected push may be retried in place, and it
        // is that final attempt, not the first, that decides whether we still need to
        // ask for credentials afterwards.
        RemoteOpResult? res = null;

        await GitProcessDialog.RunStreamingAsync(
            this,
            label,
            emit =>
            {
                res = operation(emit, null, null);
                return new GitProcessOutcome(res.Success, res.Output);
            },
            closeOnAuthFailure: true,
            onExit: rejection is null
                ? null
                : (dialog, outcome) => HandlePushRejectedAsync(
                    dialog, outcome, operation, rejection, r => res = r));

        if (res is { AuthFailed: true })
        {
            GitCredentials? creds = await CredentialsDialog.ShowAsync(this);
            if (creds is not null)
            {
                await GitProcessDialog.RunStreamingAsync(this, string.Format(T("{0} (retry)"), label), emit =>
                {
                    RemoteOpResult r = operation(emit, creds, null);
                    return new GitProcessOutcome(r.Success, r.Output);
                });
            }
        }

        Close();
    }

    // --- Rejected push recovery -------------------------------------------

    /// <summary>
    ///  Exit hook for a push that the remote refused, porting
    ///  <c>FormPush.HandlePushOnExit</c>: recognise the rejection, offer the same four
    ///  ways out (pull with the default action / with rebase / with merge, or force
    ///  push with lease) and re-run the push <em>in the dialog that is already
    ///  open</em>, instead of leaving the user to fix it and start over.
    ///
    ///  <para>Returns <see langword="true"/> when a retry was started, which tells
    ///  the process dialog not to report this failure.</para>
    ///
    ///  <para>Divergence, deliberate: upstream runs the recovery pull through a
    ///  separate <c>FormPull</c> and only then calls <c>Retry()</c>. Here the pull and
    ///  the re-push are one composed operation streamed into the SAME console, so the
    ///  user watches the whole recovery in one place — and an Abort still kills it,
    ///  because both commands run inside the retry's process scope.</para>
    /// </summary>
    private async Task<bool> HandlePushRejectedAsync(
        GitProcessDialog dialog,
        GitProcessOutcome outcome,
        PushOperation operation,
        PushRejectionContext context,
        Action<RemoteOpResult> record)
    {
        if (outcome.Success)
        {
            return false;
        }

        PushRejection? rejection = PushRefsService.DetectRejection(outcome.Output, context.LocalBranch);
        if (rejection is null)
        {
            return false;
        }

        string repo = _repoPath;

        // A bare repository has no checked-out branch to pull into (upstream's
        // `!Module.IsBareRepository()` condition).
        if (await Task.Run(() => new PushRefsService().IsBareRepository(repo)))
        {
            return false;
        }

        GitPullAction? pull;
        bool forcePush = false;

        // A remembered choice skips the question entirely — and, as upstream, it can
        // only ever be a pull: "force push with lease" is never made automatic.
        if (ReadAutoPullOnPushRejected() is { } remembered)
        {
            pull = remembered;
        }
        else
        {
            PushRejectedAnswer? answer = await AskPushRejectedAsync(dialog, rejection.CurrentBranch, context.Remote);
            if (answer is null)
            {
                // Cancelled: let the dialog report the rejection as the failure it is.
                return false;
            }

            pull = answer.Pull;
            forcePush = answer.ForcePush;

            if (answer.DontAskAgain && pull is { } chosen)
            {
                WriteAutoPullOnPushRejected(chosen);
            }
        }

        if (forcePush)
        {
            // Upstream inserts --force-with-lease into the pending command line and
            // retries; the port replays the same operation with a force override,
            // which reaches the identical `git push --force-with-lease`. Never plain
            // --force: the recovery must not be able to discard unseen remote work.
            dialog.Retry(
                emit =>
                {
                    RemoteOpResult r = operation(emit, null, PushForceMode.WithLease);
                    record(r);
                    return new GitProcessOutcome(r.Success, r.Output);
                },
                T("Retrying with --force-with-lease…"));
            return true;
        }

        GitPullAction action = pull ?? GitPullAction.None;
        if (action == GitPullAction.Default)
        {
            action = ConfiguredDefaultPullAction();
        }

        if (action == GitPullAction.None)
        {
            return false;
        }

        if (action is not (GitPullAction.Merge or GitPullAction.Rebase))
        {
            await NoteAsync(dialog, T(
                "Automatical pull can only be performed, when the default pull action "
                + "is either set to Merge or Rebase."));
            return false;
        }

        // Rebasing a merge commit rewrites history the user never agreed to flatten,
        // so it is refused rather than automated.
        if (action == GitPullAction.Rebase
            && await Task.Run(() => new PushRefsService().WouldRebaseMergeCommit(
                repo, context.Remote, context.RemoteBranch, context.LocalBranch)))
        {
            await NoteAsync(dialog, T(
                "Can not perform automatical pull, when the pull action is set to Rebase "
                + "and one of the commits that are about to be rebased is a merge commit."));
            return false;
        }

        string remote = context.Remote;
        string remoteBranch = context.RemoteBranch;
        dialog.Retry(
            emit =>
            {
                RemoteOpResult pulled = new RemoteService().PullStreaming(
                    repo,
                    new PullOptions(action, remote, RemoteBranch: remoteBranch),
                    emit,
                    credentials: null);

                if (!pulled.Success)
                {
                    // The push is not attempted on a failed pull: it would only be
                    // rejected again, burying the reason the pull failed.
                    record(pulled);
                    return new GitProcessOutcome(false, pulled.Output);
                }

                emit(string.Empty);
                RemoteOpResult pushed = operation(emit, null, null);
                record(pushed);
                return new GitProcessOutcome(pushed.Success, pushed.Output);
            },
            action == GitPullAction.Rebase
                ? T("Pulling with rebase, then pushing again…")
                : T("Pulling with merge, then pushing again…"));

        return true;
    }

    /// <summary>The user's answer to the "push was rejected" question.</summary>
    private sealed record PushRejectedAnswer(GitPullAction? Pull, bool ForcePush, bool DontAskAgain);

    /// <summary>
    ///  The port's stand-in for upstream's <c>TaskDialog</c> with command links: the
    ///  rejection is explained, then one button per way out. The three pull buttons
    ///  appear only when the rejected ref is the current branch — pulling cannot fix a
    ///  ref that is not checked out, so offering it would be a button that lies.
    ///  Force push with lease is always offered, and always last.
    /// </summary>
    /// <returns><see langword="null"/> when the user cancels.</returns>
    private async Task<PushRejectedAnswer?> AskPushRejectedAsync(Window owner, bool allOptions, string remote)
    {
        TaskCompletionSource<PushRejectedAnswer?> tcs = new();

        Window dialog = new()
        {
            Title = string.Format(T("FormPush/_pullRepositoryCaption.Text", "Push was rejected from \"{0}\""), remote),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };

        CheckBox dontAskAgain = MakeCheck(T("Don't show again"));

        StackPanel buttons = new() { Orientation = Orientation.Vertical, Spacing = 6 };

        void AddChoice(string text, GitPullAction? pull, bool force)
        {
            Button button = MakeButton();
            button.Content = text;
            button.MinWidth = 0;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Click += (_, _) =>
            {
                // "Don't show again" only ever remembers a pull: see UiState.AutoPullOnPushRejected.
                tcs.TrySetResult(new PushRejectedAnswer(pull, force, dontAskAgain.IsChecked == true && pull is not null));
                dialog.Close();
            };
            buttons.Children.Add(button);
        }

        if (allOptions)
        {
            AddChoice(
                string.Format(
                    T("FormPush/_pullDefaultButton.Text", "Pull with the default pull action ({0})"),
                    DefaultPullActionName()),
                GitPullAction.Default,
                force: false);
            AddChoice(T("FormPush/_pullRebaseButton.Text", "Pull with rebase"), GitPullAction.Rebase, force: false);
            AddChoice(T("FormPush/_pullMergeButton.Text", "Pull with merge"), GitPullAction.Merge, force: false);
        }

        AddChoice(T("FormPush/_pushForceButton.Text", "Force push with lease"), pull: null, force: true);

        Button cancel = MakeButton();
        cancel.Content = T("Cancel");
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

        // Dismissing the window is a cancel, never consent to rewrite the remote.
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = allOptions
                ? T("FormPush/_pullRepositoryMainMergeInstruction.Text", "Pull latest changes from remote repository")
                : T("FormPush/_pullRepositoryMainForceInstruction.Text", "Push rejected"),
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        });
        content.Children.Add(new TextBlock
        {
            Text = allOptions
                ? T("FormPush/_pullRepositoryMergeInstruction.Text",
                    "The push was rejected because the tip of your current branch is behind "
                    + "its remote counterpart. Merge the remote changes before pushing again.")
                : T("FormPush/_pullRepositoryForceInstruction.Text",
                    "The push was rejected because the tip of your current branch is behind "
                    + "its remote counterpart"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        });
        content.Children.Add(buttons);
        content.Children.Add(dontAskAgain);
        content.Children.Add(cancel);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    // Single-button modal, for the two cases upstream reports with MessageBoxes.ShowError
    // (default pull action not actionable, and the rebase-a-merge-commit refusal).
    private async Task NoteAsync(Window owner, string message)
    {
        Window dialog = new()
        {
            Title = Title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };

        Button ok = MakeButton();
        ok.Content = T("OK");
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => dialog.Close();

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        });
        content.Children.Add(ok);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
    }

    // --- The persisted "don't show again" pull action ----------------------

    private static GitPullAction? ReadAutoPullOnPushRejected()
    {
        try
        {
            string name = new UiStateService().Load().AutoPullOnPushRejected;
            return Enum.TryParse(name, ignoreCase: true, out GitPullAction action) ? action : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteAutoPullOnPushRejected(GitPullAction action)
    {
        try
        {
            UiStateService service = new();
            UiState state = service.Load();
            state.AutoPullOnPushRejected = action.ToString();
            service.Save(state);
        }
        catch (Exception)
        {
            // Failing to remember the choice must not break the push itself.
        }
    }

    // The configured default pull action, i.e. the port's UiState.DefaultPullAction —
    // the equivalent of upstream's AppSettings.DefaultPullAction.
    private static GitPullAction ConfiguredDefaultPullAction()
    {
        try
        {
            return Enum.TryParse(new UiStateService().Load().DefaultPullAction, ignoreCase: true, out GitPullAction action)
                ? action
                : GitPullAction.Merge;
        }
        catch (Exception)
        {
            return GitPullAction.Merge;
        }
    }

    // Label for the "(…)" of the default-action button, as upstream spells it.
    private static string DefaultPullActionName() => ConfiguredDefaultPullAction() switch
    {
        GitPullAction.Fetch or GitPullAction.FetchAll or GitPullAction.FetchPruneAll => T("fetch"),
        GitPullAction.Merge => T("merge"),
        GitPullAction.Rebase => T("rebase"),
        _ => T("none"),
    };

    // --- Force choice / confirmations -------------------------------------

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
        await RunPushAsync(T("FormPush/Pull.Text", "Pull"), (emit, creds, _) =>
            new RemoteService().PullStreaming(repo, remote, rebase: false, emit, creds));
    }

    // --- Tracking reference ------------------------------------------------

    /// <summary>
    ///  Decides whether this push should also write a tracking reference (<c>-u</c>),
    ///  porting <c>FormPush.cs:335-365</c>. Either the user asked for it with
    ///  "Replace tracking reference", or the push is about to create the branch's
    ///  first upstream and we offer to record it:
    ///  <list type="number">
    ///   <item>the branch must have no upstream yet — never silently re-point one;</item>
    ///   <item>its name must not start with a remote's name (<c>origin/x</c> as a
    ///    LOCAL branch is a mistake waiting to happen, not a tracking candidate);</item>
    ///   <item><c>branch.autosetupmerge</c> must not be <c>false</c>;</item>
    ///   <item>and the user must confirm.</item>
    ///  </list>
    ///
    ///  <para>Returns <see langword="null"/> when the user cancels, which abandons
    ///  the push entirely.</para>
    ///
    ///  <para>Before this, the port hard-coded <c>-u</c> on every single-branch push,
    ///  so pushing a branch anywhere rewrote its upstream without saying so.</para>
    /// </summary>
    private async Task<bool?> ResolveTrackingAsync(string local, string remoteBranch, bool isUrl)
    {
        if (_replaceTrackingReference.IsChecked == true)
        {
            return true;
        }

        // A URL target is not a remote: there is no `remote.<name>` for git to record,
        // so -u could not write a meaningful upstream even if we asked for it.
        if (isUrl || string.IsNullOrEmpty(local) || string.IsNullOrEmpty(remoteBranch))
        {
            return false;
        }

        if (_branchUpstreams.TryGetValue(local, out string? upstream) && !string.IsNullOrEmpty(upstream))
        {
            return false;
        }

        if (_remoteNames.Any(name => !string.IsNullOrEmpty(name)
            && local.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string repo = _repoPath;
        if (await Task.Run(() => new PushRefsService().AutoSetupMergeDisabled(repo)))
        {
            return false;
        }

        return await AskAsync(
            string.Format(
                T("FormPush/_updateTrackingReference.Text",
                    "The branch {0} does not have a tracking reference. Do you want to add a tracking reference to {1}?"),
                local,
                remoteBranch),
            T("Yes"),
            T("No"),
            cancel: true);
    }

    // --- Force choice / confirmations -------------------------------------

    // Two force check boxes are mutually exclusive, like the Windows dialog's
    // ckForceWithLease / ForcePushBranches pair: ticking one clears the other.
    private void MakeExclusive(CheckBox lease, CheckBox plain)
    {
        lease.IsCheckedChanged += (_, _) => Sync(lease, plain);
        plain.IsCheckedChanged += (_, _) => Sync(plain, lease);

        void Sync(CheckBox changed, CheckBox other)
        {
            if (_suppressForceSync || changed.IsChecked != true)
            {
                return;
            }

            _suppressForceSync = true;
            other.IsChecked = false;
            _suppressForceSync = false;
        }
    }

    /// <summary>
    ///  Turns a lease/plain check box pair into the effective
    ///  <see cref="PushForceMode"/>. When plain force is requested the user is
    ///  offered the safer lease instead (upstream's <c>_useForceWithLeaseInstead</c>
    ///  Yes/No/Cancel question); <c>null</c> means "cancelled, do not push".
    /// </summary>
    private async Task<PushForceMode?> ResolveForceAsync(CheckBox lease, CheckBox plain)
    {
        if (plain.IsChecked != true)
        {
            return lease.IsChecked == true ? PushForceMode.WithLease : PushForceMode.None;
        }

        return await AskForceWithLeaseAsync() switch
        {
            // Yes → switch to the safe force, and reflect it in the check boxes.
            true => Switch(),
            // No → go ahead with the plain, unsafe force.
            false => PushForceMode.Force,
            // Cancel → abandon the push.
            _ => null,
        };

        PushForceMode Switch()
        {
            _suppressForceSync = true;
            plain.IsChecked = false;
            lease.IsChecked = true;
            _suppressForceSync = false;
            return PushForceMode.WithLease;
        }
    }

    // Yes = use force with lease, No = keep the plain force, null = cancel.
    private Task<bool?> AskForceWithLeaseAsync()
        => AskAsync(
            T("FormPush/_useForceWithLeaseInstead.Text",
                "Force push may overwrite changes since your last fetch. "
                + "Do you want to use the safer force with lease instead?"),
            T("Use force with lease"),
            T("Force push anyway"),
            cancel: true);

    private async Task<bool> ConfirmNewRemoteBranchAsync(IReadOnlyList<string> branches, string remote)
    {
        string names = string.Join(", ", branches);
        string message = T("FormPush/_branchNewForRemote.Text",
                "The branch you are about to push seems to be a new branch for the remote."
                + Environment.NewLine + "Are you sure you want to push this branch?")
            + Environment.NewLine + Environment.NewLine
            + string.Format(T("Will be created on '{0}': {1}"), remote, names);

        return await AskAsync(message, T("Push"), T("Cancel"), cancel: false) == true;
    }

    /// <summary>
    ///  Minimal modal question with two or three answers, matching the inline
    ///  confirm dialogs the other ported dialogs use. Returns <c>true</c> for the
    ///  affirmative button, <c>false</c> for the negative one and <c>null</c> when
    ///  cancelled (or dismissed).
    /// </summary>
    private async Task<bool?> AskAsync(string message, string yesText, string noText, bool cancel)
    {
        TaskCompletionSource<bool?> tcs = new();

        Window dialog = new()
        {
            Title = Title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Button yes = MakeButton();
        yes.Content = yesText;
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        buttons.Children.Add(yes);

        Button no = MakeButton();
        no.Content = noText;
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        buttons.Children.Add(no);

        if (cancel)
        {
            Button abort = MakeButton();
            abort.Content = T("Cancel");
            abort.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
            buttons.Children.Add(abort);
        }

        // Dismissing the window (Esc / close box) must never be read as consent.
        dialog.Closed += (_, _) => tcs.TrySetResult(cancel ? null : false);

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
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

    private CheckBox MakeCheck(string text)
    {
        CheckBox c = MakeCheck();
        c.Content = text;
        return c;
    }

    // Caption-less overloads: the text is applied (and re-applied on a language
    // switch) by ApplyTranslations.
    private CheckBox MakeCheck() => new()
    {
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private Button MakeButton() => new()
    {
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
