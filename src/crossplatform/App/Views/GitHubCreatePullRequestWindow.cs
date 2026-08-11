using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  "Create pull request" — the port of upstream's <c>CreatePullRequestForm</c>.
///
///  <para>Upstream's version asks two questions and infers the rest: it offers only the
///  remotes NOT owned by you as targets, and takes the source to be "the one GitHub
///  remote that is yours". Two consequences, both of which people hit: a repository you
///  own and contribute to directly — no fork involved — offers <b>no</b> target at all
///  and the form closes with "please clone GitHub repository before pull request"; and
///  with two of your own forks configured, which one is the source is a coin toss.</para>
///
///  <para>This version asks both ends explicitly. Target repository and branch, source
///  repository and branch, four drop-downs filled from the remotes this repository
///  actually has. The <c>head</c> is then <c>owner:branch</c> when the two repositories
///  differ and a bare branch name when they do not, which is what the API wants in each
///  case — so a same-repository pull request works, and a cross-fork one still does.</para>
/// </summary>
public sealed class GitHubCreatePullRequestWindow : Theming.ZoomWindow
{
    private readonly GitHubService _service;
    private readonly string _repoPath;
    private readonly CancellationTokenSource _closing = new();

    private readonly ComboBox _targetRemote;
    private readonly ComboBox _targetBranch;
    private readonly ComboBox _sourceRemote;
    private readonly ComboBox _sourceBranch;
    private readonly TextBox _title;
    private readonly TextBox _body;
    private readonly Button _create;
    private readonly TextBlock _status;

    /// <summary>True once a pull request was created, so the caller can refresh.</summary>
    public bool Created { get; private set; }

    /// <summary>Whether the failure of a branch load has already been shown; see the catch.</summary>
    private bool _branchErrorReported;

    /// <summary>True while the two ends are being started together, from <c>Opened</c>.</summary>
    private bool _loadingBothEnds;

    private GitHubCreatePullRequestWindow(
        GitHubService service, string repoPath, IReadOnlyList<GitHubHostedRemote> remotes, string? currentBranch, string? login)
    {
        _service = service;
        _repoPath = repoPath;
        IBrush dim = GitHubDialogs.Brush("App.TextDim", "#9B9B9B");

        Title = TranslationService.T("CreatePullRequestForm/$this.Text", "Create pull request");
        Width = 720;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = GitHubDialogs.Brush("App.Window", "#1E1E1E");
        Foreground = GitHubDialogs.Brush("App.Text", "#DCDCDC");

        _targetRemote = RemoteCombo(remotes);
        _sourceRemote = RemoteCombo(remotes);
        _targetBranch = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        _sourceBranch = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };

        // The target defaults to a repository that is NOT mine when there is one — that
        // is the fork workflow, and it is the case where guessing wrong is most annoying.
        // The source defaults to mine. With a single remote both land on it, which is
        // the same-repository workflow upstream cannot express at all.
        _targetRemote.SelectedIndex = IndexOf(remotes, owned: false, login) ?? 0;
        _sourceRemote.SelectedIndex = IndexOf(remotes, owned: true, login) ?? 0;

        _targetRemote.SelectionChanged += (_, _) => LoadBranches(_targetRemote, _targetBranch, preferDefault: true);
        _sourceRemote.SelectionChanged += (_, _) => LoadBranches(_sourceRemote, _sourceBranch, preferDefault: false, preselect: currentBranch);
        _sourceBranch.SelectionChanged += (_, _) => SuggestTitle();

        _title = new TextBox { Watermark = TranslationService.T("CreatePullRequestForm/_titleLbl.Text", "Title") };
        _body = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 200,
            Watermark = TranslationService.T("CreatePullRequestForm/_bodyLbl.Text", "Description"),
            FontFamily = Theming.AppFonts.Monospace,
        };

        _status = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center };

        _create = new Button
        {
            Content = TranslationService.T("CreatePullRequestForm/_createBtn.Text", "Create pull request"),
            IsDefault = true,
            MinWidth = 150,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _create.Click += (_, _) => Async.Run(CreateAsync, "GitHub create pull request");

        Button cancel = new()
        {
            Content = TranslationService.T("FormSettings/buttonCancel.Text", "Close"),
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancel.Click += (_, _) => Close();

        Grid buttons = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(_create, 1);
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(_status);
        buttons.Children.Add(_create);
        buttons.Children.Add(cancel);

        Grid ends = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        Place(ends, Labelled(TranslationService.T("Merge into (repository)"), _targetRemote, dim), 0, 0);
        Place(ends, Labelled(TranslationService.T("Merge into (branch)"), _targetBranch, dim), 0, 1);
        Place(ends, Labelled(TranslationService.T("Merge from (repository)"), _sourceRemote, dim), 1, 0);
        Place(ends, Labelled(TranslationService.T("Merge from (branch)"), _sourceBranch, dim), 1, 1);

        DockPanel root = new() { Margin = new Thickness(16) };
        StackPanel top = new() { Spacing = 10 };
        top.Children.Add(ends);
        top.Children.Add(Labelled(TranslationService.T("CreatePullRequestForm/_titleLbl.Text", "Title"), _title, dim));

        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(Labelled(TranslationService.T("CreatePullRequestForm/_bodyLbl.Text", "Description"), _body, dim));
        Content = root;

        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };

        Opened += (_, _) =>
        {
            _loadingBothEnds = true;
            LoadBranches(_targetRemote, _targetBranch, preferDefault: true);
            LoadBranches(_sourceRemote, _sourceBranch, preferDefault: false, preselect: currentBranch);
            _loadingBothEnds = false;
            LoadTemplate();
        };
    }

    /// <summary>
    ///  Opens the window for <paramref name="repoPath"/>, refusing — with a reason —
    ///  when there is no token or no remote on the host.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, GitHubService service, string repoPath)
    {
        if (!await GitHubDialogs.RequireTokenAsync(owner, service))
        {
            return false;
        }

        IReadOnlyList<GitHubHostedRemote> remotes = service.GetHostedRemotes(repoPath);
        if (remotes.Count == 0)
        {
            await GitHubDialogs.MessageAsync(
                owner,
                TranslationService.T("CreatePullRequestForm/$this.Text", "Create pull request"),
                TranslationService.TFormat(null, "None of this repository's remotes is on {0}.", service.Host));
            return false;
        }

        string? login = await service.GetLoginAsync(CancellationToken.None);
        GitHubCreatePullRequestWindow window = new(service, repoPath, remotes, CurrentBranch(repoPath), login);
        await window.ShowDialog(owner);
        return window.Created;
    }

    // ---- Filling in --------------------------------------------------------

    private static ComboBox RemoteCombo(IReadOnlyList<GitHubHostedRemote> remotes)
    {
        ComboBox combo = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.ItemsSource = remotes;
        return combo;
    }

    /// <summary>The first remote owned (or not owned) by <paramref name="login"/>, or null.</summary>
    private static int? IndexOf(IReadOnlyList<GitHubHostedRemote> remotes, bool owned, string? login)
    {
        if (login is not { Length: > 0 })
        {
            return null;
        }

        for (int i = 0; i < remotes.Count; i++)
        {
            if (string.Equals(remotes[i].Owner, login, StringComparison.OrdinalIgnoreCase) == owned)
            {
                return i;
            }
        }

        return null;
    }

    private void LoadBranches(ComboBox remoteCombo, ComboBox branchCombo, bool preferDefault, string? preselect = null)
    {
        if (remoteCombo.SelectedItem is not GitHubHostedRemote remote)
        {
            return;
        }

        branchCombo.ItemsSource = null;
        branchCombo.PlaceholderText = TranslationService.T("Loading…");

        // A deliberate CHANGE of remote is a new attempt and deserves to be told if it
        // fails; only the two loads of one opening share a single message.
        if (!_loadingBothEnds)
        {
            _branchErrorReported = false;
        }

        Async.Run(
            async () =>
            {
                try
                {
                    GitHubClient client = _service.CreateClient();
                    IReadOnlyList<GitHubBranch> branches =
                        await client.GetBranchesAsync(remote.Owner, remote.Repository, _closing.Token);

                    // Only the target needs the default branch, and it costs an extra
                    // round trip, so the source does not ask for it.
                    string? preferred = preselect;
                    if (preferDefault)
                    {
                        GitHubRepository repository =
                            await client.GetRepositoryAsync(remote.Owner, remote.Repository, _closing.Token);
                        preferred = repository.DefaultBranch;
                    }

                    // Still the same remote? The user may have changed the drop-down.
                    if (!ReferenceEquals(remoteCombo.SelectedItem, remote))
                    {
                        return;
                    }

                    List<string> names = [.. branches
                        .Select(b => b.Name)
                        .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];
                    branchCombo.ItemsSource = names;
                    branchCombo.SelectedItem = preferred is { Length: > 0 } && names.Contains(preferred)
                        ? preferred
                        : names.FirstOrDefault();
                    branchCombo.PlaceholderText = null;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    branchCombo.PlaceholderText = null;

                    // Both ends load at once, and they fail together for the same reason
                    // — a rejected token, an unreachable host. Seen on screen: two
                    // identical modal boxes stacked on top of each other, the second one
                    // arriving after the first had been dismissed. One is the message.
                    if (!_branchErrorReported)
                    {
                        _branchErrorReported = true;
                        await GitHubDialogs.ReportAsync(
                            this,
                            TranslationService.T("CreatePullRequestForm/_strRemoteFailToLoadBranches.Text", "Fail to load target branches"),
                            ex);
                    }
                }
            },
            "GitHub branches");
    }

    /// <summary>
    ///  Seeds the description from the repository's pull-request template, the way
    ///  upstream does (<c>LoadPRTemplate</c>) — extended to the other two locations
    ///  GitHub itself accepts, since a repository that uses one of those would
    ///  otherwise silently get nothing.
    /// </summary>
    private void LoadTemplate()
    {
        string[] candidates =
        [
            Path.Combine(_repoPath, ".github", "PULL_REQUEST_TEMPLATE.md"),
            Path.Combine(_repoPath, "PULL_REQUEST_TEMPLATE.md"),
            Path.Combine(_repoPath, "docs", "PULL_REQUEST_TEMPLATE.md"),
        ];

        foreach (string path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    _body.Text = File.ReadAllText(path);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable template is not worth a dialog; the box just stays empty.
            }
        }
    }

    /// <summary>
    ///  Suggests the subject of the source branch's last commit as the title, as
    ///  upstream does — but only while the box is untouched, so it never overwrites
    ///  something typed.
    /// </summary>
    private void SuggestTitle()
    {
        if ((_title.Text ?? string.Empty).Trim().Length > 0)
        {
            return;
        }

        if (_sourceRemote.SelectedItem is not GitHubHostedRemote remote
            || _sourceBranch.SelectedItem is not string branch)
        {
            return;
        }

        string revision = $"refs/remotes/{remote.Name}/{branch}";
        Async.OffUi(
            () => RunGit(_repoPath, $"log -1 --pretty=%s {revision}"),
            subject =>
            {
                if (subject.Length > 0 && (_title.Text ?? string.Empty).Trim().Length == 0)
                {
                    _title.Text = subject;
                }
            },
            "GitHub pull request title");
    }

    // ---- Creating ----------------------------------------------------------

    private async Task CreateAsync()
    {
        if (_targetRemote.SelectedItem is not GitHubHostedRemote target
            || _sourceRemote.SelectedItem is not GitHubHostedRemote source
            || _targetBranch.SelectedItem is not string targetBranch
            || _sourceBranch.SelectedItem is not string sourceBranch)
        {
            _status.Text = TranslationService.T("Choose both ends first.");
            return;
        }

        string title = (_title.Text ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            _status.Text = TranslationService.T(
                "CreatePullRequestForm/_strYouMustSpecifyATitle.Text", "You must specify a title.");
            return;
        }

        // Cross-repository requests must name the owner; same-repository ones must NOT
        // — GitHub rejects "owner:branch" when the owner is the repository's own.
        bool sameRepository = string.Equals(source.Data, target.Data, StringComparison.OrdinalIgnoreCase);
        string head = sameRepository ? sourceBranch : $"{source.Owner}:{sourceBranch}";

        if (sameRepository && string.Equals(sourceBranch, targetBranch, StringComparison.Ordinal))
        {
            _status.Text = TranslationService.T("A branch cannot be merged into itself.");
            return;
        }

        _create.IsEnabled = false;
        _status.Text = TranslationService.T("Creating…");
        try
        {
            GitHubPullRequest created = await _service.CreateClient().CreatePullRequestAsync(
                target.Owner, target.Repository, head, targetBranch, title, _body.Text ?? string.Empty, _closing.Token);

            Created = true;

            bool open = await GitHubDialogs.ConfirmAsync(
                this,
                TranslationService.T("CreatePullRequestForm/_strPullRequest.Text", "Pull request"),
                TranslationService.TFormat(
                    null, "Pull request #{0} created.\n\nOpen it in the browser?", created.Number));

            if (open && created.HtmlUrl is { Length: > 0 } url)
            {
                new ExternalToolService().OpenUrl(url);
            }

            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = string.Empty;
            await GitHubDialogs.ReportAsync(
                this,
                TranslationService.T("CreatePullRequestForm/_strFailedToCreatePullRequest.Text", "Failed to create pull request."),
                ex);
        }
        finally
        {
            _create.IsEnabled = true;
        }
    }

    // ---- git helpers -------------------------------------------------------

    private static string? CurrentBranch(string repoPath)
    {
        string branch = RunGit(repoPath, "rev-parse --abbrev-ref HEAD");
        return branch is "HEAD" or "" ? null : branch;
    }

    /// <summary>
    ///  One git invocation whose whole output is one short line. Deliberately not
    ///  routed through the command log: these are questions the window asks to fill a
    ///  field, not commands the user issued.
    /// </summary>
    private static string RunGit(string repoPath, string arguments)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            GitEnvironment.ApplyDiagnosticLocale(psi.Environment);

            using Process process = new() { StartInfo = psi };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 ? output.Trim() : string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return string.Empty;
        }
    }

    // ---- Layout helpers ----------------------------------------------------

    private static Control Labelled(string label, Control editor, IBrush dim)
    {
        StackPanel stack = new() { Spacing = 4, Margin = new Thickness(0, 0, 10, 0) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = dim, FontSize = 12 });
        stack.Children.Add(editor);
        return stack;
    }

    private static void Place(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        control.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(control);
    }
}
