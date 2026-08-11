using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  "Fork and clone" — the port of upstream's <c>ForkAndCloneForm</c>.
///
///  <para>Two ways in: the repositories the token's owner can push to, and a search
///  (by text, or by account name). A repository found in the search can be forked; any
///  repository, found either way, can be cloned — with the protocol, the destination,
///  the subdirectory, the shallow depth and the name to add the parent under, exactly
///  the knobs the original form has.</para>
///
///  <para>Two departures from upstream, both deliberate:</para>
///  <list type="bullet">
///   <item>the clone runs through <see cref="GitProcessDialog"/> instead of a modal
///    "please wait", so its progress is visible and it can be aborted;</item>
///   <item>the parent of a fork is read on SELECTION rather than assumed. GitHub only
///    fills in <c>parent</c> on a repository fetched by name — the objects inside a
///    listing or a search result do not have it — so upstream's "add upstream remote"
///    box silently stays empty for repositories reached through search. Here the
///    detail is fetched when a fork is selected, and the box fills in.</item>
///  </list>
/// </summary>
public sealed class GitHubForkCloneWindow : Theming.ZoomWindow
{
    private readonly GitHubService _service;
    private readonly CancellationTokenSource _closing = new();

    private readonly TabControl _tabs;
    private readonly TabItem _myReposTab;
    private readonly TabItem _searchTab;

    private readonly ListBox _myRepos;
    private readonly ListBox _searchResults;
    private readonly TextBox _searchText;
    private readonly Button _search;
    private readonly Button _searchUser;
    private readonly Button _fork;
    private readonly TextBlock _description;

    private readonly ComboBox _protocol;
    private readonly TextBox _destination;
    private readonly TextBox _subdirectory;
    private readonly ComboBox _upstreamName;
    private readonly TextBox _depth;
    private readonly TextBlock _cloneInfo;
    private readonly Button _clone;
    private readonly Button _homepage;
    private readonly TextBlock _status;

    private readonly IBrush _dim;

    /// <summary>Detail lookups already done, so re-selecting a repository costs nothing.</summary>
    private readonly Dictionary<string, GitHubRepository> _detailed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clone's working directory, or null if nothing was cloned.</summary>
    public string? ClonedRepoPath { get; private set; }

    public GitHubForkCloneWindow(GitHubService service)
    {
        _service = service;
        IBrush text = GitHubDialogs.Brush("App.Text", "#DCDCDC");
        _dim = GitHubDialogs.Brush("App.TextDim", "#9B9B9B");
        IBrush border = GitHubDialogs.Brush("App.Border", "#3F3F46");

        Title = TranslationService.TFormat(null, "{0}: fork and clone", service.Host);
        Width = 860;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = GitHubDialogs.Brush("App.Window", "#1E1E1E");

        // ---- "My repositories" -------------------------------------------------
        _myRepos = RepositoryList();
        _myRepos.SelectionChanged += (_, _) => OnSelectionChanged();

        Button refresh = new() { Content = TranslationService.T("FormBrowse/refreshToolStripMenuItem.Text", "Refresh") };
        refresh.Click += (_, _) => LoadMyRepositories();

        DockPanel myReposPanel = new() { Margin = new Thickness(10) };
        DockPanel.SetDock(refresh, Dock.Bottom);
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.Margin = new Thickness(0, 8, 0, 0);
        myReposPanel.Children.Add(refresh);
        myReposPanel.Children.Add(_myRepos);

        _myReposTab = new TabItem
        {
            Header = TranslationService.T("ForkAndCloneForm/myReposPage.Text", "My repositories"),
            Content = myReposPanel,
        };

        // ---- "Search" ----------------------------------------------------------
        _searchText = new TextBox { Watermark = TranslationService.T("Repository name, or an account name") };
        _searchText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Search(byUser: false);
            }
        };

        _search = new Button { Content = TranslationService.T("ForkAndCloneForm/searchBtn.Text", "Search") };
        _search.Click += (_, _) => Search(byUser: false);

        _searchUser = new Button
        {
            Content = TranslationService.T("ForkAndCloneForm/getFromUserBtn.Text", "Repositories of user"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        _searchUser.Click += (_, _) => Search(byUser: true);

        Grid searchRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(_searchText, 0);
        Grid.SetColumn(_search, 1);
        Grid.SetColumn(_searchUser, 2);
        _search.Margin = new Thickness(8, 0, 0, 0);
        searchRow.Children.Add(_searchText);
        searchRow.Children.Add(_search);
        searchRow.Children.Add(_searchUser);

        _searchResults = RepositoryList();
        _searchResults.SelectionChanged += (_, _) => OnSelectionChanged();

        _description = new TextBlock
        {
            Foreground = _dim,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 60,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _fork = new Button
        {
            Content = TranslationService.T("ForkAndCloneForm/forkBtn.Text", "Fork"),
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _fork.Click += (_, _) => Fork();

        DockPanel searchPanel = new() { Margin = new Thickness(10) };
        DockPanel.SetDock(searchRow, Dock.Top);
        DockPanel.SetDock(_description, Dock.Bottom);
        DockPanel.SetDock(_fork, Dock.Bottom);
        searchRow.Margin = new Thickness(0, 0, 0, 8);
        searchPanel.Children.Add(searchRow);
        searchPanel.Children.Add(_fork);
        searchPanel.Children.Add(_description);
        searchPanel.Children.Add(_searchResults);

        _searchTab = new TabItem
        {
            Header = TranslationService.T("ForkAndCloneForm/searchReposPage.Text", "Search"),
            Content = searchPanel,
        };

        _tabs = new TabControl();
        _tabs.Items.Add(_myReposTab);
        _tabs.Items.Add(_searchTab);
        _tabs.SelectionChanged += (_, e) =>
        {
            // The tab strip is inside this window, but so are two ListBoxes whose own
            // SelectionChanged bubbles up to here. Acting on someone else's event is
            // what made the diff pane reload files on a tab switch (M157); this handler
            // asks who raised it before believing it.
            if (ReferenceEquals(e.Source, _tabs))
            {
                OnSelectionChanged();
            }
        };

        // ---- The clone panel, shared by both tabs -------------------------------
        _protocol = new ComboBox { MinWidth = 120 };
        _protocol.Items.Add(new ComboBoxItem { Content = "HTTPS" });
        _protocol.Items.Add(new ComboBoxItem { Content = "SSH" });
        _protocol.SelectedIndex = 0;
        _protocol.SelectionChanged += (_, _) => UpdateCloneInfo();

        _destination = new TextBox
        {
            Watermark = TranslationService.T("Directory to clone into"),
            Text = AppSettings.DefaultCloneDestinationPath,
        };
        _destination.TextChanged += (_, _) => UpdateCloneInfo();

        Button browse = new() { Content = TranslationService.T("Browse…"), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) => Async.Run(BrowseAsync, "GitHub clone destination");

        Grid destinationRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_destination, 0);
        Grid.SetColumn(browse, 1);
        destinationRow.Children.Add(_destination);
        destinationRow.Children.Add(browse);

        _subdirectory = new TextBox { Watermark = TranslationService.T("Subdirectory to create") };
        _subdirectory.TextChanged += (_, _) => UpdateCloneInfo();

        _upstreamName = new ComboBox { MinWidth = 200, IsEnabled = false };
        _upstreamName.SelectionChanged += (_, _) => UpdateCloneInfo();

        _depth = new TextBox { Text = "0", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Left };

        _cloneInfo = new TextBlock
        {
            Foreground = _dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 34,
        };

        _homepage = new Button
        {
            Content = TranslationService.T("ForkAndCloneForm/openGithubPageBtn.Text", "Open the project page"),
            IsEnabled = false,
        };
        _homepage.Click += (_, _) => OpenHomepage();

        _clone = new Button
        {
            Content = TranslationService.T("ForkAndCloneForm/cloneBtn.Text", "Clone"),
            IsDefault = true,
            IsEnabled = false,
            MinWidth = 100,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _clone.Click += (_, _) => Async.Run(CloneAsync, "GitHub clone");

        Button close = new()
        {
            Content = TranslationService.T("FormSettings/buttonCancel.Text", "Close"),
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
        };
        close.Click += (_, _) => Close();

        _status = new TextBlock { Foreground = _dim, VerticalAlignment = VerticalAlignment.Center };

        Grid buttonRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        Grid.SetColumn(_homepage, 0);
        Grid.SetColumn(_status, 1);
        Grid.SetColumn(_clone, 2);
        Grid.SetColumn(close, 3);
        _status.Margin = new Thickness(12, 0, 12, 0);
        buttonRow.Children.Add(_homepage);
        buttonRow.Children.Add(_status);
        buttonRow.Children.Add(_clone);
        buttonRow.Children.Add(close);

        Grid fields = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };
        Add(fields, Labelled(TranslationService.T("Destination"), destinationRow), 0, 0, columnSpan: 3);
        Add(fields, Labelled(TranslationService.T("Subdirectory to create"), _subdirectory), 1, 0);
        Add(fields, Labelled(TranslationService.T("Protocol"), _protocol), 1, 1);
        Add(fields, Labelled(TranslationService.T("Shallow depth (0 = all)"), _depth), 1, 2);
        Add(
            fields,
            Labelled(
                TranslationService.T("ForkAndCloneForm/addUpstreamRemoteAsLbl.Text", "Add the parent as a remote named"),
                _upstreamName),
            2,
            0,
            columnSpan: 3);
        Add(fields, _cloneInfo, 3, 0, columnSpan: 3);

        StackPanel bottom = new() { Margin = new Thickness(14), Spacing = 10 };
        bottom.Children.Add(fields);
        bottom.Children.Add(buttonRow);

        Border bottomBox = new()
        {
            Background = GitHubDialogs.Brush("App.Panel", "#252526"),
            BorderBrush = border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = bottom,
        };

        DockPanel root = new();
        DockPanel.SetDock(bottomBox, Dock.Bottom);
        root.Children.Add(bottomBox);
        root.Children.Add(_tabs);
        Content = root;

        Foreground = text;
        Opened += (_, _) => LoadMyRepositories();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };

        UpdateCloneInfo();
    }

    /// <summary>Opens the window, returning the path of a clone that happened, if any.</summary>
    public static async Task<string?> ShowAsync(Window owner, GitHubService service)
    {
        if (!await GitHubDialogs.RequireTokenAsync(owner, service))
        {
            return null;
        }

        GitHubForkCloneWindow window = new(service);
        await window.ShowDialog(owner);
        return window.ClonedRepoPath;
    }

    // ---- Loading -----------------------------------------------------------

    private void LoadMyRepositories()
    {
        _myRepos.ItemsSource = null;
        _status.Text = TranslationService.T(" : LOADING : ");

        Async.Run(
            async () =>
            {
                try
                {
                    IReadOnlyList<GitHubRepository> repos = await _service.CreateClient()
                        .GetMyRepositoriesAsync(_closing.Token);
                    Fill(_myRepos, repos, showOwner: false);
                    _status.Text = TranslationService.TFormat(null, "{0} repositories.", repos.Count);
                }
                catch (OperationCanceledException)
                {
                    // The window closed while the request was in flight.
                }
                catch (Exception ex)
                {
                    _status.Text = string.Empty;
                    await GitHubDialogs.ReportAsync(this, Title ?? "GitHub", ex);
                }
            },
            "GitHub my repositories");
    }

    private void Search(bool byUser)
    {
        string query = (_searchText.Text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return;
        }

        _search.IsEnabled = false;
        _searchUser.IsEnabled = false;
        _searchResults.ItemsSource = null;
        _status.Text = TranslationService.T(" : SEARCHING : ");

        Async.Run(
            async () =>
            {
                try
                {
                    GitHubClient client = _service.CreateClient();
                    IReadOnlyList<GitHubRepository> repos = byUser
                        ? await client.GetUserRepositoriesAsync(query, _closing.Token)
                        : await client.SearchRepositoriesAsync(query, _closing.Token);

                    Fill(_searchResults, repos, showOwner: true);
                    _status.Text = TranslationService.TFormat(null, "{0} matches.", repos.Count);
                }
                catch (OperationCanceledException)
                {
                }
                catch (GitHubApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound && byUser)
                {
                    _status.Text = string.Empty;
                    await GitHubDialogs.MessageAsync(
                        this,
                        Title ?? "GitHub",
                        TranslationService.TFormat(null, "{0} has no account on {1}.", query, _service.Host));
                }
                catch (Exception ex)
                {
                    _status.Text = string.Empty;
                    await GitHubDialogs.ReportAsync(this, Title ?? "GitHub", ex);
                }
                finally
                {
                    _search.IsEnabled = true;
                    _searchUser.IsEnabled = true;
                }
            },
            "GitHub search");
    }

    private void Fill(ListBox list, IReadOnlyList<GitHubRepository> repos, bool showOwner)
    {
        List<ListBoxItem> items = [];
        foreach (GitHubRepository repo in repos.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            items.Add(new ListBoxItem { Content = Row(repo, showOwner), Tag = repo });
        }

        list.ItemsSource = items;
    }

    // ---- Fork --------------------------------------------------------------

    private void Fork()
    {
        if (Selected is not GitHubRepository repo)
        {
            return;
        }

        Async.Run(
            async () =>
            {
                bool confirmed = await GitHubDialogs.ConfirmAsync(
                    this,
                    TranslationService.T("ForkAndCloneForm/forkBtn.Text", "Fork"),
                    TranslationService.TFormat(null, "Fork {0} into your account?", repo.FullName));
                if (!confirmed)
                {
                    return;
                }

                _fork.IsEnabled = false;
                _status.Text = TranslationService.T("Forking…");
                try
                {
                    await _service.CreateClient().ForkRepositoryAsync(repo.OwnerLogin, repo.Name, _closing.Token);

                    // GitHub creates the fork asynchronously and answers 202 before it
                    // exists, so the list is reloaded rather than having the answer
                    // spliced into it: a repository that is not there yet must not be
                    // offered for cloning.
                    _tabs.SelectedItem = _myReposTab;
                    LoadMyRepositories();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _status.Text = string.Empty;
                    await GitHubDialogs.ReportAsync(
                        this, TranslationService.T("ForkAndCloneForm/_strFailedToFork.Text", "Failed to fork"), ex);
                }
                finally
                {
                    _fork.IsEnabled = Selected is not null;
                }
            },
            "GitHub fork");
    }

    // ---- Selection ---------------------------------------------------------

    private GitHubRepository? Selected =>
        (ActiveList.SelectedItem as ListBoxItem)?.Tag as GitHubRepository;

    private ListBox ActiveList => ReferenceEquals(_tabs.SelectedItem, _searchTab) ? _searchResults : _myRepos;

    private void OnSelectionChanged()
    {
        GitHubRepository? repo = Selected;

        _fork.IsEnabled = ReferenceEquals(_tabs.SelectedItem, _searchTab) && repo is not null;
        _clone.IsEnabled = repo is not null;
        _homepage.IsEnabled = repo is not null;
        _description.Text = repo?.Description ?? string.Empty;

        if (repo is null)
        {
            _subdirectory.Text = string.Empty;
            _upstreamName.ItemsSource = null;
            _upstreamName.IsEnabled = false;
            UpdateCloneInfo();
            return;
        }

        _subdirectory.Text = repo.Name;
        FillUpstreamNames(Detail(repo) ?? repo);
        UpdateCloneInfo();

        if (repo.Fork && Detail(repo) is null)
        {
            LoadParent(repo);
        }
    }

    private GitHubRepository? Detail(GitHubRepository repo)
        => _detailed.TryGetValue(repo.FullName, out GitHubRepository? detailed) ? detailed : null;

    /// <summary>
    ///  Fetches the repository by name so its <c>parent</c> is filled in. Only a fork
    ///  has one, so this runs on selecting a fork and at no other time.
    /// </summary>
    private void LoadParent(GitHubRepository repo)
        => Async.Run(
            async () =>
            {
                try
                {
                    GitHubRepository detailed = await _service.CreateClient()
                        .GetRepositoryAsync(repo.OwnerLogin, repo.Name, _closing.Token);
                    _detailed[repo.FullName] = detailed;

                    // The user may have moved on while this was in flight; only touch
                    // the fields if the selection is still the repository asked about.
                    if (ReferenceEquals(Selected, repo))
                    {
                        FillUpstreamNames(detailed);
                        UpdateCloneInfo();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (GitHubApiException)
                {
                    // Not fatal: without the detail the "add upstream" box simply stays
                    // empty, which is where upstream leaves it in every case.
                }
            },
            "GitHub repository detail");

    private void FillUpstreamNames(GitHubRepository repo)
    {
        string? parentOwner = repo.Parent?.OwnerLogin;
        if (parentOwner is not { Length: > 0 })
        {
            _upstreamName.ItemsSource = null;
            _upstreamName.IsEnabled = false;
            return;
        }

        // Upstream offers the parent's account name and the literal "upstream", and
        // pre-selects the account name (ForkAndCloneForm.cs:456-464).
        List<string> names = [parentOwner, GitHubService.UpstreamRemoteName];
        _upstreamName.ItemsSource = names;
        _upstreamName.SelectedIndex = 0;
        _upstreamName.IsEnabled = true;
    }

    private void OpenHomepage()
    {
        if (Selected is not GitHubRepository repo)
        {
            return;
        }

        // The homepage field is free text and is very often empty; the repository's own
        // page is the answer that is always right, so it is the fallback.
        string url = repo.Homepage is { Length: > 0 } homepage
            && (homepage.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || homepage.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? homepage
            : repo.HtmlUrl ?? $"{_service.WebEndpoint}/{repo.FullName}";

        ExternalToolResult result = new ExternalToolService().OpenUrl(url);
        if (!result.Success)
        {
            _status.Text = result.Message;
        }
    }

    // ---- Clone -------------------------------------------------------------

    private string CloneUrl(GitHubRepository repo)
    {
        bool ssh = _protocol.SelectedIndex == 1;
        string? url = ssh ? repo.SshUrl : repo.CloneUrl;
        return url is { Length: > 0 }
            ? url
            : ssh
                ? $"git@{_service.Host}:{repo.FullName}.git"
                : $"{_service.WebEndpoint}/{repo.FullName}.git";
    }

    private string DestinationPath
    {
        get
        {
            string parent = (_destination.Text ?? string.Empty).Trim();
            string child = (_subdirectory.Text ?? string.Empty).Trim();
            return parent.Length == 0 || child.Length == 0 ? string.Empty : Path.Combine(parent, child);
        }
    }

    private void UpdateCloneInfo()
    {
        if (Selected is not GitHubRepository repo)
        {
            _cloneInfo.Text = string.Empty;
            return;
        }

        string destination = DestinationPath;
        string remoteNote = _upstreamName is { IsEnabled: true, SelectedItem: string name }
            ? TranslationService.TFormat(null, " \"{0}\" will be added as a remote.", name)
            : string.Empty;

        // Upstream words this differently for a repository of mine ("you will have push
        // access") than for one found by search ("you can not push unless you are a
        // collaborator"), which is a claim it cannot actually check for an organisation
        // repository. The honest version states what will happen and leaves the guess out.
        _cloneInfo.Text = destination.Length == 0
            ? TranslationService.T("Choose a destination directory and a subdirectory name.")
            : TranslationService.TFormat(null, "Will clone {0} into {1}.{2}", CloneUrl(repo), destination, remoteNote);
    }

    private async Task BrowseAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = TranslationService.T("Choose a directory to clone into"),
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            _destination.Text = path;
        }
    }

    private async Task CloneAsync()
    {
        if (Selected is not GitHubRepository repo)
        {
            return;
        }

        string destination = DestinationPath;
        if (destination.Length == 0 || !Path.IsPathRooted(destination))
        {
            _status.Text = TranslationService.T("The destination must be an absolute path.");
            return;
        }

        string url = CloneUrl(repo);
        int? depth = int.TryParse(_depth.Text?.Trim(), out int parsed) && parsed > 0 ? parsed : null;
        string arguments = CloneInitService.CloneArguments(url, destination, central: false, initSubmodules: true, branch: string.Empty, depth);
        string parentDirectory = Path.GetDirectoryName(destination) ?? destination;

        try
        {
            Directory.CreateDirectory(parentDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await GitHubDialogs.MessageAsync(this, Title ?? "GitHub", ex.Message);
            return;
        }

        GitProcessOutcome outcome = await GitProcessDialog.RunStreamingAsync(
            this,
            TranslationService.TFormat(null, "Clone {0}", repo.FullName),
            onOutput => new GitProcessOutcome(
                GitStreamRunner.Run(parentDirectory, arguments, onOutput) == 0, string.Empty));

        if (!outcome.Success)
        {
            return;
        }

        // The parent remote is added after the clone rather than through
        // --origin/--upstream tricks: the clone must succeed first, and the name is the
        // user's choice, so this is a plain `git remote add` on the fresh repository.
        if (_upstreamName is { IsEnabled: true, SelectedItem: string remoteName }
            && Detail(repo)?.Parent is GitHubRepository parent)
        {
            string parentUrl = (_protocol.SelectedIndex == 1 ? parent.SshUrl : parent.CloneUrl) ?? string.Empty;
            if (parentUrl.Length > 0)
            {
                RemoteOpResult added = new RemoteService().AddRemote(destination, remoteName, parentUrl);
                if (!added.Success)
                {
                    await GitHubDialogs.MessageAsync(
                        this,
                        TranslationService.T("ForkAndCloneForm/_strCouldNotAddRemote.Text", "Could not add remote"),
                        added.Output);
                }
            }
        }

        try
        {
            await new RecentRepositoriesService().AddAsync(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed history write must never fail the clone.
        }

        ClonedRepoPath = destination;
        Close();
    }

    // ---- Small builders ----------------------------------------------------

    private static ListBox RepositoryList() => new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
    };

    /// <summary>
    ///  One row: name, owner (search only), and the two facts upstream shows in columns
    ///  — whether it is a fork and how many forks it has — plus a padlock for a private
    ///  repository, which is a column upstream only has on its own-repositories tab.
    /// </summary>
    private Control Row(GitHubRepository repo, bool showOwner)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = repo.Name, MinWidth = 260 });

        if (showOwner)
        {
            row.Children.Add(new TextBlock { Text = repo.OwnerLogin, Foreground = _dim, MinWidth = 160 });
        }

        if (repo.Fork)
        {
            row.Children.Add(new TextBlock
            {
                Text = TranslationService.T("ForkAndCloneForm/columnHeaderIsAFork.Text", "fork"),
                Foreground = _dim,
            });
        }

        if (repo.Private)
        {
            row.Children.Add(new TextBlock { Text = TranslationService.T("private"), Foreground = _dim });
        }

        if (repo.ForksCount > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = TranslationService.TFormat(null, "{0} forks", repo.ForksCount),
                Foreground = _dim,
            });
        }

        return row;
    }

    private Control Labelled(string label, Control editor)
    {
        StackPanel stack = new() { Spacing = 4, Margin = new Thickness(0, 0, 12, 8) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = _dim, FontSize = 12 });
        stack.Children.Add(editor);
        return stack;
    }

    private static void Add(Grid grid, Control control, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
        grid.Children.Add(control);
    }
}
