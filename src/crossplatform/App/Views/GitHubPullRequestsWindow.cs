using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using AvaloniaEdit;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  "View pull requests" — the port of upstream's <c>ViewPullRequestsForm</c>.
///
///  <para>The open requests of one of this repository's GitHub remotes, with the diff
///  of the selected one split per file, its conversation, and the two ways of getting
///  it into the local repository: fetch the head into <c>pr/n&lt;number&gt;_&lt;branch&gt;</c>,
///  or add the contributor's fork as a remote and check the branch out from there.</para>
///
///  <para>The diff comes from the API's <c>.diff</c> media type rather than from the
///  public <c>diff_url</c> upstream downloads: that URL carries no credentials, so on a
///  private repository upstream's version fails with a 404 that reads like the request
///  does not exist. The patch is then split by the same rule upstream uses, and shown
///  through the port's own file list and colorizer, so it looks and behaves like every
///  other diff in the app.</para>
///
///  <para>Comments can be posted, which upstream's discussion pane also allows. What is
///  <b>not</b> here is upstream's HTML rendering of the conversation: it hosts an
///  Internet Explorer control (<c>WebBrowser</c>) that has no counterpart on this
///  platform, so the thread is laid out as text — the same information, drawn by
///  something that exists.</para>
/// </summary>
public sealed class GitHubPullRequestsWindow : Theming.ZoomWindow
{
    // Upstream's two expressions (ViewPullRequestsForm.cs:38-41), kept verbatim: the
    // patch format is git's, not GitHub's, and it is the same one either way.
    private static readonly Regex DiffCommandRegex =
        new(@"(?:\n|^)diff --git ", RegexOptions.ExplicitCapture | RegexOptions.Compiled);

    private static readonly Regex FilePartRegex =
        new(@"^a/([^\n]+) b/(?<name>[^\n]+)\s*(?<value>.*)$",
            RegexOptions.Singleline | RegexOptions.ExplicitCapture | RegexOptions.Compiled);

    private readonly GitHubService _service;
    private readonly string _repoPath;
    private readonly CancellationTokenSource _closing = new();

    private readonly ComboBox _remotes;
    private readonly ListBox _requests;
    private readonly FileStatusListView _files;
    private readonly TextEditor _patch;
    private readonly DiffLineColorizer _colorizer = new();
    private readonly TextBox _conversation;
    private readonly TextBox _comment;
    private readonly Button _post;
    private readonly Button _fetch;
    private readonly Button _addRemoteAndFetch;
    private readonly Button _closeRequest;
    private readonly Button _browser;
    private readonly TextBlock _status;

    /// <summary>Per-file patch text of the request on screen, keyed by the file's path.</summary>
    private readonly Dictionary<string, string> _patches = new(StringComparer.Ordinal);

    /// <summary>True if anything was fetched, so the caller knows to refresh the grid.</summary>
    public bool RepositoryChanged { get; private set; }

    private GitHubPullRequestsWindow(GitHubService service, string repoPath, IReadOnlyList<GitHubHostedRemote> remotes)
    {
        _service = service;
        _repoPath = repoPath;
        IBrush dim = GitHubDialogs.Brush("App.TextDim", "#9B9B9B");
        IBrush border = GitHubDialogs.Brush("App.Border", "#3F3F46");

        Title = TranslationService.T("ViewPullRequestsForm/$this.Text", "View pull requests");
        Width = 1180;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = GitHubDialogs.Brush("App.Window", "#1E1E1E");
        Foreground = GitHubDialogs.Brush("App.Text", "#DCDCDC");

        // ---- Header: which remote's requests ----------------------------------
        _remotes = new ComboBox { MinWidth = 300 };
        _remotes.ItemsSource = remotes;
        _remotes.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _remotes))
            {
                LoadRequests();
            }
        };

        Button refresh = new()
        {
            Content = TranslationService.T("FormBrowse/refreshToolStripMenuItem.Text", "Refresh"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        refresh.Click += (_, _) => LoadRequests();

        _status = new TextBlock
        {
            Foreground = dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        StackPanel header = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 10, 12, 10),
            Children = { _remotes, refresh, _status },
        };

        // ---- Left: the requests ----------------------------------------------
        _requests = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        _requests.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, _requests))
            {
                ShowRequest();
            }
        };

        // ---- Right: files + patch, and the conversation ------------------------
        _files = new FileStatusListView { ShowToolbar = false };
        _files.SelectedFileChanged += ShowPatch;

        Styles.Add(new StyleInclude(new Uri("avares://GitExtensions.Avalonia/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });

        _patch = new TextEditor
        {
            FontFamily = Theming.AppFonts.Monospace,
            IsReadOnly = true,
            WordWrap = false,
            Background = GitHubDialogs.Brush("App.Window", "#1E1E1E"),
            Foreground = GitHubDialogs.Brush("App.Text", "#DCDCDC"),
            Padding = new Thickness(10),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _patch.Options.EnableHyperlinks = false;
        _patch.Options.EnableEmailHyperlinks = false;
        _patch.Options.AllowScrollBelowDocument = false;
        _patch.Options.HighlightCurrentLine = false;
        _patch.TextArea.TextView.LineTransformers.Add(_colorizer);

        Grid diffPane = new() { ColumnDefinitions = new ColumnDefinitions("300,4,*") };
        GridSplitter diffSplitter = new() { Background = border };
        Grid.SetColumn(_files, 0);
        Grid.SetColumn(diffSplitter, 1);
        Grid.SetColumn(_patch, 2);
        diffPane.Children.Add(_files);
        diffPane.Children.Add(diffSplitter);
        diffPane.Children.Add(_patch);

        _conversation = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Theming.AppFonts.Monospace,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        _comment = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 80,
            Watermark = TranslationService.T("Write a comment…"),
        };

        _post = new Button
        {
            Content = TranslationService.T("Comment"),
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _post.Click += (_, _) => PostComment();

        DockPanel conversationPane = new() { Margin = new Thickness(10) };
        StackPanel commentBox = new();
        commentBox.Children.Add(_comment);
        commentBox.Children.Add(_post);
        DockPanel.SetDock(commentBox, Dock.Bottom);
        commentBox.Margin = new Thickness(0, 10, 0, 0);
        conversationPane.Children.Add(commentBox);
        conversationPane.Children.Add(new ScrollViewer { Content = _conversation });

        TabControl detail = new();
        detail.Items.Add(new TabItem
        {
            Header = TranslationService.T("FormBrowse/DiffTabPage.Text", "Diff"),
            Content = diffPane,
        });
        detail.Items.Add(new TabItem
        {
            Header = TranslationService.T("ViewPullRequestsForm/_discussionPage.Text", "Conversation"),
            Content = conversationPane,
        });

        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("360,4,*") };
        GridSplitter bodySplitter = new() { Background = border };
        Grid.SetColumn(_requests, 0);
        Grid.SetColumn(bodySplitter, 1);
        Grid.SetColumn(detail, 2);
        body.Children.Add(_requests);
        body.Children.Add(bodySplitter);
        body.Children.Add(detail);

        // ---- Bottom buttons ---------------------------------------------------
        _fetch = Action(
            TranslationService.T("ViewPullRequestsForm/_fetchBtn.Text", "Fetch"),
            () => Async.Run(FetchAsync, "GitHub fetch pull request"));
        _addRemoteAndFetch = Action(
            TranslationService.T("ViewPullRequestsForm/_addAsRemoteAndFetch.Text", "Add as remote and fetch"),
            () => Async.Run(AddRemoteAndFetchAsync, "GitHub add remote and fetch"));
        _closeRequest = Action(
            TranslationService.T("ViewPullRequestsForm/_closePullRequestBtn.Text", "Close pull request"),
            () => Async.Run(ClosePullRequestAsync, "GitHub close pull request"));
        _browser = Action(TranslationService.T("Open in browser"), OpenInBrowser);

        Button close = new()
        {
            Content = TranslationService.T("FormSettings/buttonCancel.Text", "Close"),
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
        };
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 10, 12, 12),
            Children = { _fetch, _addRemoteAndFetch, _closeRequest, _browser, close },
        };

        Border buttonBar = new()
        {
            BorderBrush = border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = buttons,
        };

        DockPanel root = new();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttonBar, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttonBar);
        root.Children.Add(body);
        Content = root;

        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };

        Opened += (_, _) =>
        {
            // Pre-select the remote the current branch tracks, as upstream does
            // (SelectHostedRepositoryForCurrentRemote); otherwise the first one.
            string current = CurrentRemoteName();
            int index = 0;
            for (int i = 0; i < remotes.Count; i++)
            {
                if (string.Equals(remotes[i].Name, current, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            _remotes.SelectedIndex = index;
            LoadRequests();
        };

        UpdateButtons();
    }

    /// <summary>
    ///  Opens the window; returns whether anything was fetched into the repository.
    ///
    ///  <para>Unlike the other two windows this one does <b>not</b> demand a token.
    ///  Reading the open pull requests of a public repository, their diffs and their
    ///  conversations needs no credentials, and refusing to show them — which is what
    ///  upstream's <c>ConfigurationOk</c> gate does — would make the port useless to
    ///  anyone browsing a project they do not have an account on. The two actions that
    ///  really do need authentication, posting a comment and closing a request, are
    ///  disabled instead, and the header says why.</para>
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, GitHubService service, string repoPath)
    {
        IReadOnlyList<GitHubHostedRemote> remotes = service.GetHostedRemotes(repoPath);
        if (remotes.Count == 0)
        {
            await GitHubDialogs.MessageAsync(
                owner,
                TranslationService.T("ViewPullRequestsForm/$this.Text", "View pull requests"),
                TranslationService.TFormat(null, "None of this repository's remotes is on {0}.", service.Host));
            return false;
        }

        GitHubPullRequestsWindow window = new(service, repoPath, remotes);
        await window.ShowDialog(owner);
        return window.RepositoryChanged;
    }

    // ---- Loading -----------------------------------------------------------

    private GitHubHostedRemote? Remote => _remotes.SelectedItem as GitHubHostedRemote;

    private GitHubPullRequest? Current => (_requests.SelectedItem as ListBoxItem)?.Tag as GitHubPullRequest;

    private void LoadRequests()
    {
        if (Remote is not GitHubHostedRemote remote)
        {
            return;
        }

        _requests.ItemsSource = null;
        ClearDetail();
        _status.Text = TranslationService.T(" : LOADING : ");

        Async.Run(
            async () =>
            {
                try
                {
                    IReadOnlyList<GitHubPullRequest> requests = await _service.CreateClient()
                        .GetPullRequestsAsync(remote.Owner, remote.Repository, _closing.Token);

                    if (!ReferenceEquals(Remote, remote))
                    {
                        return;
                    }

                    List<ListBoxItem> items = [];
                    foreach (GitHubPullRequest request in requests)
                    {
                        items.Add(new ListBoxItem { Content = RequestRow(request), Tag = request });
                    }

                    _requests.ItemsSource = items;
                    _status.Text = requests.Count == 0
                        ? TranslationService.T("No open pull requests.")
                        : TranslationService.TFormat(null, "{0} open pull requests.", requests.Count);

                    if (items.Count > 0)
                    {
                        _requests.SelectedIndex = 0;
                        ShowRequest();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _status.Text = string.Empty;
                    await GitHubDialogs.ReportAsync(
                        this,
                        TranslationService.T("ViewPullRequestsForm/_strFailedToFetchPullData.Text", "Failed to fetch pull data!"),
                        ex);
                }
            },
            "GitHub pull requests");
    }

    private void ShowRequest()
    {
        ClearDetail();
        UpdateButtons();

        if (Current is not GitHubPullRequest request || Remote is not GitHubHostedRemote remote)
        {
            return;
        }

        LoadDiff(remote, request);
        LoadConversation(remote, request);
    }

    private void LoadDiff(GitHubHostedRemote remote, GitHubPullRequest request)
        => Async.Run(
            async () =>
            {
                try
                {
                    string diff = await _service.CreateClient()
                        .GetPullRequestDiffAsync(remote.Owner, remote.Repository, request.Number, _closing.Token);

                    if (!ReferenceEquals(Current, request))
                    {
                        return;
                    }

                    SplitDiff(diff);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await GitHubDialogs.ReportAsync(
                        this,
                        TranslationService.T("ViewPullRequestsForm/_strFailedToLoadDiffData.Text", "Failed to load diff data!"),
                        ex);
                }
            },
            "GitHub pull request diff");

    /// <summary>
    ///  Splits one big unified diff into per-file patches and hands the list to the
    ///  ordinary file list, so grouping, filtering and the status glyphs all work.
    /// </summary>
    private void SplitDiff(string diff)
    {
        _patches.Clear();
        List<DiffFileRow> rows = [];

        foreach (string part in DiffCommandRegex.Split(diff))
        {
            if (part.Trim().Length <= 10)
            {
                continue;
            }

            Match match = FilePartRegex.Match(part);
            if (!match.Success)
            {
                continue;
            }

            string name = match.Groups["name"].Value.Trim();
            string body = match.Groups["value"].Value;

            // The header lines say what happened to the file; without them every row
            // would be drawn as a modification, including the added and deleted ones.
            DiffChangeKind kind =
                body.Contains("\nnew file mode", StringComparison.Ordinal) ? DiffChangeKind.Added
                : body.Contains("\ndeleted file mode", StringComparison.Ordinal) ? DiffChangeKind.Deleted
                : body.Contains("\nrename to ", StringComparison.Ordinal) ? DiffChangeKind.Renamed
                : DiffChangeKind.Modified;

            rows.Add(new DiffFileRow(name, OldName: null, kind, IsTracked: true));
            _patches[name] = body;
        }

        _files.SetFiles(rows);
        if (rows.Count == 0)
        {
            _patch.Text = TranslationService.T("This pull request changes nothing.");
        }
    }

    private void ShowPatch(DiffFileRow? row)
    {
        _colorizer.Invalidate();
        _patch.Text = row is not null && _patches.TryGetValue(row.Name, out string? text) ? text : string.Empty;
    }

    private void LoadConversation(GitHubHostedRemote remote, GitHubPullRequest request)
        => Async.Run(
            async () =>
            {
                try
                {
                    GitHubClient client = _service.CreateClient();
                    IReadOnlyList<GitHubPullRequestCommit> commits =
                        await client.GetPullRequestCommitsAsync(remote.Owner, remote.Repository, request.Number, _closing.Token);
                    IReadOnlyList<GitHubComment> comments =
                        await client.GetIssueCommentsAsync(remote.Owner, remote.Repository, request.Number, _closing.Token);

                    if (!ReferenceEquals(Current, request))
                    {
                        return;
                    }

                    _conversation.Text = Conversation(request, commits, comments);
                    _post.IsEnabled = _service.IsConfigured;
                    _comment.IsEnabled = _service.IsConfigured;
                    _comment.Watermark = _service.IsConfigured
                        ? TranslationService.T("Write a comment…")
                        : TranslationService.T("Storing a personal access token would let you comment here.");
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await GitHubDialogs.ReportAsync(
                        this,
                        TranslationService.T("ViewPullRequestsForm/_strCouldNotLoadDiscussion.Text", "Could not load discussion!"),
                        ex);
                }
            },
            "GitHub pull request conversation");

    /// <summary>
    ///  The thread as text, in the order it happened: the request itself, then its
    ///  commits and comments merged by time — which is the ordering upstream's HTML
    ///  view produces and the one that makes a review readable.
    /// </summary>
    private static string Conversation(
        GitHubPullRequest request,
        IReadOnlyList<GitHubPullRequestCommit> commits,
        IReadOnlyList<GitHubComment> comments)
    {
        List<(DateTimeOffset When, string Text)> entries = [];

        foreach (GitHubPullRequestCommit commit in commits)
        {
            string subject = commit.Commit?.Message.Split('\n')[0] ?? string.Empty;
            entries.Add((
                commit.Commit?.Author?.Date ?? request.CreatedAt,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"● {commit.Commit?.Author?.Name} committed {commit.Sha[..Math.Min(8, commit.Sha.Length)]}\n  {subject}")));
        }

        foreach (GitHubComment comment in comments)
        {
            entries.Add((comment.CreatedAt, $"■ {comment.User?.Login} wrote:\n{Indent(comment.Body)}"));
        }

        StringBuilder text = new();
        text.Append(CultureInfo.CurrentCulture, $"#{request.Number} — {request.Title}\n");
        text.Append(CultureInfo.CurrentCulture, $"{request.OwnerLogin}, {Local(request.CreatedAt)}\n");
        text.Append(CultureInfo.CurrentCulture, $"{request.Head?.Ref} → {request.Base?.Ref}\n\n");
        if (request.Body is { Length: > 0 } body)
        {
            text.Append(Indent(body)).Append("\n\n");
        }

        foreach ((DateTimeOffset when, string entry) in entries.OrderBy(e => e.When))
        {
            text.Append(CultureInfo.CurrentCulture, $"[{Local(when)}] {entry}\n\n");
        }

        return text.ToString();
    }

    private static string Local(DateTimeOffset when)
        => when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Indent(string? text)
        => string.Join('\n', (text ?? string.Empty).Split('\n').Select(line => "  " + line));

    private void PostComment()
    {
        string body = (_comment.Text ?? string.Empty).Trim();
        if (body.Length == 0 || Current is not GitHubPullRequest request || Remote is not GitHubHostedRemote remote)
        {
            return;
        }

        _post.IsEnabled = false;
        Async.Run(
            async () =>
            {
                try
                {
                    await _service.CreateClient()
                        .PostIssueCommentAsync(remote.Owner, remote.Repository, request.Number, body, _closing.Token);
                    _comment.Text = string.Empty;
                    LoadConversation(remote, request);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    await GitHubDialogs.ReportAsync(
                        this,
                        TranslationService.T("ViewPullRequestsForm/_strFailedToLoadDiscussionItem.Text", "Failed to post discussion item!"),
                        ex);
                }
                finally
                {
                    _post.IsEnabled = true;
                }
            },
            "GitHub post comment");
    }

    // ---- Actions -----------------------------------------------------------

    /// <summary>
    ///  Fetches the request's head into a local branch named after it, without adding a
    ///  remote — upstream's <c>_fetchBtn</c>. The fork's clone URL is used directly, so
    ///  nothing is left configured behind.
    /// </summary>
    private async Task FetchAsync()
    {
        if (Current is not GitHubPullRequest request)
        {
            return;
        }

        if (HeadUrl(request) is not { Length: > 0 } url)
        {
            await GitHubDialogs.MessageAsync(
                this,
                Title ?? "GitHub",
                TranslationService.T("The fork this pull request came from no longer exists, so there is nothing to fetch."));
            return;
        }

        string arguments = $"fetch --no-tags --progress \"{url}\" {request.Head!.Ref}:{request.FetchBranch}";
        await RunGitAsync(TranslationService.TFormat(null, "Fetch pull request #{0}", request.Number), arguments);
    }

    /// <summary>
    ///  Adds the contributor's fork as a remote named after them, fetches the branch and
    ///  checks it out — upstream's <c>_addAsRemoteAndFetch</c>. Unlike upstream this
    ///  refuses to reuse an existing remote of the same name pointing somewhere else,
    ///  rather than fetching into it.
    /// </summary>
    private async Task AddRemoteAndFetchAsync()
    {
        if (Current is not GitHubPullRequest request)
        {
            return;
        }

        if (HeadUrl(request) is not { Length: > 0 } url || request.Head?.Ref is not { Length: > 0 } branch)
        {
            await GitHubDialogs.MessageAsync(
                this,
                Title ?? "GitHub",
                TranslationService.T("The fork this pull request came from no longer exists, so there is nothing to fetch."));
            return;
        }

        string remoteName = request.OwnerLogin;
        RemoteRow? existing = new RemoteService().ListRemotes(_repoPath)
            .FirstOrDefault(r => string.Equals(r.Name, remoteName, StringComparison.Ordinal));

        if (existing is not null && !string.Equals(existing.FetchUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            await GitHubDialogs.MessageAsync(
                this,
                Title ?? "GitHub",
                TranslationService.TFormat(
                    null,
                    "A remote named {0} already exists and points at {1}, not at {2}.",
                    remoteName,
                    existing.FetchUrl,
                    url));
            return;
        }

        if (existing is null)
        {
            RemoteOpResult added = new RemoteService().AddRemote(_repoPath, remoteName, url);
            if (!added.Success)
            {
                await GitHubDialogs.MessageAsync(this, Title ?? "GitHub", added.Output);
                return;
            }

            RepositoryChanged = true;
        }

        if (!await RunGitAsync(
            TranslationService.TFormat(null, "Fetch {0}/{1}", remoteName, branch),
            $"fetch --no-tags --progress {remoteName} {branch}:{remoteName}/{branch}"))
        {
            return;
        }

        await RunGitAsync(
            TranslationService.TFormat(null, "Checkout {0}/{1}", remoteName, branch),
            $"checkout {remoteName}/{branch}");
    }

    private async Task ClosePullRequestAsync()
    {
        if (Current is not GitHubPullRequest request || Remote is not GitHubHostedRemote remote)
        {
            return;
        }

        bool confirmed = await GitHubDialogs.ConfirmAsync(
            this,
            TranslationService.T("ViewPullRequestsForm/_closePullRequestBtn.Text", "Close pull request"),
            TranslationService.TFormat(null, "Close pull request #{0}, \"{1}\"?", request.Number, request.Title));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _service.CreateClient()
                .ClosePullRequestAsync(remote.Owner, remote.Repository, request.Number, _closing.Token);
            LoadRequests();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await GitHubDialogs.ReportAsync(
                this,
                TranslationService.T("ViewPullRequestsForm/_strFailedToClosePullRequest.Text", "Failed to close pull request!"),
                ex);
        }
    }

    private void OpenInBrowser()
    {
        if (Current?.HtmlUrl is { Length: > 0 } url)
        {
            new ExternalToolService().OpenUrl(url);
        }
    }

    /// <summary>The clone URL of the request's head repository, in the protocol the
    /// selected remote already uses, or null when the fork is gone.</summary>
    private string? HeadUrl(GitHubPullRequest request)
    {
        GitHubRepository? head = request.Head?.Repo;
        if (head is null)
        {
            return null;
        }

        return Remote?.UsesSsh == true ? head.SshUrl : head.CloneUrl;
    }

    private async Task<bool> RunGitAsync(string label, string arguments)
    {
        GitProcessOutcome outcome = await GitProcessDialog.RunStreamingAsync(
            this,
            label,
            onOutput => new GitProcessOutcome(
                GitStreamRunner.Run(_repoPath, arguments, onOutput) == 0, string.Empty));

        if (outcome.Success)
        {
            RepositoryChanged = true;
        }

        return outcome.Success;
    }

    // ---- Plumbing ----------------------------------------------------------

    private void ClearDetail()
    {
        _patches.Clear();
        _files.Clear();
        _patch.Text = string.Empty;
        _conversation.Text = string.Empty;
        _post.IsEnabled = false;
    }

    private void UpdateButtons()
    {
        bool has = Current is not null;

        // Fetching uses git, which has its own credentials (and needs none at all for a
        // public repository); closing a request goes through the API and does need the
        // token. Two different authorities, so two different gates.
        _fetch.IsEnabled = has;
        _addRemoteAndFetch.IsEnabled = has;
        _browser.IsEnabled = has;
        _closeRequest.IsEnabled = has && _service.IsConfigured;
    }

    /// <summary>
    ///  The remote the checked-out branch tracks, so the window opens on the repository
    ///  the user is actually working against rather than on whichever remote git happens
    ///  to list first. Empty when the branch tracks nothing (or HEAD is detached).
    /// </summary>
    private string CurrentRemoteName()
    {
        try
        {
            GitModule module = GitContext.CreateModule(_repoPath);
            string branch = module.GetSelectedBranch();
            return branch.Length == 0 ? string.Empty : module.GetSetting($"branch.{branch}.remote");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private Control RequestRow(GitHubPullRequest request)
    {
        StackPanel row = new() { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = string.Create(CultureInfo.CurrentCulture, $"#{request.Number}  {request.Title}"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold,
        });
        row.Children.Add(new TextBlock
        {
            Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{request.OwnerLogin} · {Local(request.CreatedAt)} · {request.Head?.Ref} → {request.Base?.Ref}"),
            Foreground = GitHubDialogs.Brush("App.TextDim", "#9B9B9B"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    private static Button Action(string caption, Action onClick)
    {
        Button button = new() { Content = caption, IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }
}
