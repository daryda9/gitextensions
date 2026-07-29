using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The notification strip above the revision grid that says a multi-step git
///  operation is stopped half-way.
///
///  <para><b>What it ports.</b> Upstream <c>FormBrowse</c> keeps two notification
///  bars docked above the grid — <c>notificationBarBisectInProgress</c> and
///  <c>notificationBarGitActionInProgress</c>
///  (<c>FormBrowse.Designer.cs:650-668</c>, refreshed at
///  <c>FormBrowse.cs:1175-1182</c>, the bisect one driven by
///  <c>InteractiveGitActionControl.RefreshBisect:47-61</c>). This port had no visual
///  cue at all: a stopped rebase, merge, bisect, cherry-pick or revert could only be
///  discovered by asking for it — the bisect through
///  <see cref="BisectService.IsInProgress"/>, the merge only from inside the commit
///  dialog. The two bars are kept as two bars here, for the same reason upstream has
///  two: a bisect is orthogonal to the rest and can be open while something else is
///  stopped.</para>
///
///  <para><b>Which buttons are real.</b> Only the bisect bar gets action buttons —
///  good, bad, skip and stop — because <see cref="BisectService"/> is the one service
///  of this port that actually implements them. There is deliberately <i>no</i>
///  continue/abort button for rebase, merge, cherry-pick, revert or <c>git am</c>:
///  the port has no service behind them (the only two <c>--abort</c> calls in
///  <c>App/Services</c> are private clean-up paths inside
///  <c>CommitEditService</c> and <c>PatchService</c>, not an API), and a button that
///  cannot do its job is worse than no button. Those states show what is going on
///  and name the git command that finishes or undoes it, which is honest and still
///  removes the "why is my repository behaving strangely" dead end.</para>
///
///  <para><b>Refreshing.</b> The banner never polls. <see cref="Refresh"/> re-reads
///  the state on a thread-pool thread and marshals the result back, and the host
///  calls it from the same repository-changed notification that refreshes the grid
///  (<see cref="RepositoryWatcherService"/>). Every git command the banner itself
///  starts is wrapped in <see cref="SuspendWatcher"/> so it cannot feed the watcher's
///  own change detector.</para>
///
///  <para>Collapses to nothing (<see cref="Visual.IsVisible"/> false) whenever there
///  is nothing in progress, so a host can drop it in an auto-sized row and forget
///  about it. Nothing here throws: a failed probe simply hides the banner.</para>
/// </summary>
public sealed class RepositoryProgressBanner : UserControl
{
    private readonly RepositoryStateService _state = new();
    private readonly BisectService _bisect = new();

    private readonly Border _bisectBar;
    private readonly TextBlock _bisectText;
    private readonly Button _good;
    private readonly Button _bad;
    private readonly Button _skip;
    private readonly Button _stop;
    private readonly Button _more;

    // The last session state read, so the buttons can be restored to exactly the set
    // the repository allows after a command finishes.
    private BisectSession _session = BisectSession.None;

    private readonly Border _actionBar;
    private readonly TextBlock _actionText;
    private readonly TextBlock _actionHint;

    private string? _repoPath;

    // Discards the result of a probe that was overtaken by a newer one — the same
    // guard the other views of this port use.
    private int _generation;

    public RepositoryProgressBanner()
    {
        IsVisible = false;

        _bisectText = new TextBlock
        {
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _good = MakeButton(T("Good"), T("Mark the checked-out commit as good"), OnGood);
        _bad = MakeButton(T("Bad"), T("Mark the checked-out commit as bad"), OnBad);
        _skip = MakeButton(T("Skip"), T("Skip the checked-out commit"), OnSkip);
        _stop = MakeButton(T("Stop bisect"), T("End the bisect session and restore HEAD"), OnStop);

        // Upstream's bisect bar carries exactly one button, "More", which opens
        // FormBisect (InteractiveGitActionControl.cs:138-141, :242-254). Kept as the
        // way to the full panel — the four inline buttons above are this port's
        // shortcut for the two marks you make on every step.
        _more = MakeButton(
            T("InteractiveGitActionControl/MoreButton.Text", "More"),
            T("Open the bisect control panel"),
            OnMore);

        StackPanel bisectButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _good, _bad, _skip, _stop, _more },
        };

        _bisectBar = MakeBar(_bisectText, bisectButtons);

        _actionText = new TextBlock
        {
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The command that finishes or undoes the operation. Dim, because it is a
        // hint and not an offer — see the class remarks on why there is no button.
        _actionHint = new TextBlock
        {
            Foreground = Brush("App.TextDim", "#9B9B9B"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _actionBar = MakeBar(_actionText, _actionHint);

        Content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _bisectBar, _actionBar },
        };

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    ///  Raised, on the UI thread, after a bisect action changed the repository, so
    ///  the host can refresh the grid and the rest of the shell.
    /// </summary>
    public event Action? RepositoryChanged;

    /// <summary>
    ///  Raised by the bar's "More" button — upstream's own entry point from the
    ///  notification bar into <c>FormBisect</c>
    ///  (<c>InteractiveGitActionControl.cs:242-254</c>). The host answers by opening
    ///  <see cref="BisectDialog"/>; with nothing subscribed the button does nothing,
    ///  so it is hidden in that case.
    /// </summary>
    public event Action? BisectDetailsRequested;

    /// <summary>
    ///  Supplied by the host so the banner's own git commands do not trip the
    ///  repository watcher's change detection — set it to
    ///  <c>RepositoryWatcherService.Suspend</c>. Left null, the commands simply run
    ///  unguarded, which costs one redundant refresh and nothing else.
    /// </summary>
    public Func<IDisposable>? SuspendWatcher { get; set; }

    /// <summary>
    ///  Points the banner at a repository (null or empty hides it) and refreshes it.
    ///  Safe to call with the same path again.
    /// </summary>
    public void SetRepository(string? repoPath)
    {
        _repoPath = string.IsNullOrWhiteSpace(repoPath) ? null : repoPath;
        Refresh();
    }

    /// <summary>
    ///  Re-reads the repository state and updates the bars. Returns immediately: the
    ///  disk work runs on a thread-pool thread and the result is applied on the UI
    ///  thread. Call it from the host's repository-changed handler; it never throws.
    /// </summary>
    public void Refresh()
    {
        string? repo = _repoPath;
        int generation = ++_generation;

        if (repo is null)
        {
            Apply(RepositoryProgress.None, BisectSession.None);
            return;
        }

        _ = Task.Run(() =>
        {
            RepositoryProgress progress;
            try
            {
                progress = _state.GetProgress(repo);
            }
            catch
            {
                progress = RepositoryProgress.None;
            }

            // Only asked when the marker file says there is a session, so an idle
            // repository still costs no git process (see BisectService.GetSession).
            BisectSession session;
            try
            {
                session = progress.BisectInProgress
                    ? _bisect.GetSession(repo)
                    : BisectSession.None;
            }
            catch
            {
                session = new BisectSession(progress.BisectInProgress);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation)
                {
                    Apply(progress, session);
                }
            });
        });
    }

    // ---- rendering the state ---------------------------------------------------------

    private void Apply(RepositoryProgress progress, BisectSession session)
    {
        _session = session;
        _bisectBar.IsVisible = progress.BisectInProgress;
        if (progress.BisectInProgress)
        {
            _bisectText.Text = DescribeBisect(session);

            // No subscriber means no panel to open: an inert button would be a lie.
            _more.IsVisible = BisectDetailsRequested is not null;
            EnableBisectButtons(true);
        }

        _actionBar.IsVisible = progress.Operation != RepositoryOperation.None;
        if (_actionBar.IsVisible)
        {
            _actionText.Text = DescribeOperation(progress);
            _actionHint.Text = HintFor(progress.Operation);
        }

        IsVisible = progress.IsActive;
    }

    /// <summary>
    ///  What the bisect bar says. Upstream says only "Bisect is currently in
    ///  progress." (<c>InteractiveGitActionControl.cs:13,18</c>); the remaining-work
    ///  figures added here are git's own, from
    ///  <c>git rev-list --bisect-vars</c> via <see cref="BisectService.GetSession"/>,
    ///  and are shown only in the states where git can actually compute them — with
    ///  the range still unbounded the bar names the missing mark instead.
    /// </summary>
    private static string DescribeBisect(BisectSession session)
    {
        if (session.Finished)
        {
            return session.CulpritHash is { Length: > 0 } culprit
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("Bisect finished — {0} is the first bad commit. Stop the bisect to restore your branch."),
                    culprit.Length > 8 ? culprit[..8] : culprit)
                : T("Bisect finished — stop the bisect to restore your branch.");
        }

        if (!session.Ready)
        {
            return !session.BadKnown && !session.GoodKnown
                ? T("Bisecting — mark the checked-out commit good or bad to bound the search.")
                : session.BadKnown
                    ? T("Bisecting — a bad commit is known; mark a good one to bound the search.")
                    : T("Bisecting — a good commit is known; mark a bad one to bound the search.");
        }

        if (session.HasProgress)
        {
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Bisecting — {0} revisions left to test, roughly {1} more steps."),
                session.RevisionsLeft,
                session.StepsLeft);
        }

        return T("Bisecting — this is the last commit to test.");
    }

    /// <summary>
    ///  "&lt;operation&gt; in progress", plus git's own step counter and target branch
    ///  when it recorded them — the same facts upstream's bar shows.
    /// </summary>
    private static string DescribeOperation(RepositoryProgress progress)
    {
        string headline = progress.Operation switch
        {
            RepositoryOperation.Merge => T("A merge is in progress."),
            RepositoryOperation.Rebase => T("A rebase is in progress."),
            RepositoryOperation.RebaseInteractive => T("An interactive rebase is in progress."),
            RepositoryOperation.ApplyMailbox => T("A patch series is being applied (git am)."),
            RepositoryOperation.CherryPick => T("A cherry-pick is in progress."),
            RepositoryOperation.Revert => T("A revert is in progress."),
            _ => string.Empty,
        };

        if (progress.HasStepCount)
        {
            headline += "  " + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Step {0} of {1}."),
                progress.Step,
                progress.TotalSteps);
        }

        if (progress.Target is { Length: > 0 } target)
        {
            headline += "  " + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Branch: {0}."),
                target);
        }

        return headline;
    }

    /// <summary>
    ///  Names the command that finishes or undoes the operation. Not a button: this
    ///  port has no service behind any of them (see the class remarks).
    /// </summary>
    private static string HintFor(RepositoryOperation operation) => operation switch
    {
        RepositoryOperation.Merge =>
            T("Resolve the conflicts and commit, or run: git merge --abort"),
        RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive =>
            T("Resolve the conflicts, then run: git rebase --continue / --skip / --abort"),
        RepositoryOperation.ApplyMailbox =>
            T("Resolve the conflicts, then run: git am --continue / --skip / --abort"),
        RepositoryOperation.CherryPick =>
            T("Resolve the conflicts, then run: git cherry-pick --continue / --abort"),
        RepositoryOperation.Revert =>
            T("Resolve the conflicts, then run: git revert --continue / --abort"),
        _ => string.Empty,
    };

    // ---- bisect actions ---------------------------------------------------------------

    private void OnGood() => RunBisect(repo => _bisect.MarkGood(repo, "HEAD"));

    private void OnBad() => RunBisect(repo => _bisect.MarkBad(repo, "HEAD"));

    private void OnSkip() => RunBisect(repo => _bisect.Skip(repo, "HEAD"));

    private void OnStop() => RunBisect(repo => _bisect.Reset(repo));

    // The host owns the dialog: it is the one that knows the grid selection (for
    // upstream's range seeding) and how to refresh the shell afterwards.
    private void OnMore() => BisectDetailsRequested?.Invoke();

    /// <summary>
    ///  Runs one bisect command off the UI thread, with the buttons disabled for the
    ///  duration, then refreshes the banner and tells the host to refresh itself.
    ///  A failure is reported in the bar's own text rather than thrown.
    /// </summary>
    private void RunBisect(Func<string, BisectResult> action)
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            return;
        }

        EnableBisectButtons(false);

        _ = Task.Run(() =>
        {
            BisectResult result;
            try
            {
                using IDisposable? guard = SuspendWatcher?.Invoke();
                result = action(repo);
            }
            catch (Exception ex)
            {
                result = new BisectResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                EnableBisectButtons(true);

                if (!result.Success)
                {
                    // Keep the bar up and say what git said; the refresh below still
                    // corrects the state if the session actually ended.
                    _bisectText.Text = FirstLine(result.Output) is { Length: > 0 } line
                        ? line
                        : T("The bisect command failed.");
                }

                Refresh();
                RepositoryChanged?.Invoke();
            });
        });
    }

    /// <summary>
    ///  Enables the bar's buttons for what the session actually allows: once the
    ///  search has converged there is nothing left to mark, so good / bad / skip go
    ///  quiet and only the reset (and the panel) remain. Passing
    ///  <see langword="false"/> disables everything for the duration of a command.
    /// </summary>
    private void EnableBisectButtons(bool enabled)
    {
        bool markable = enabled && !_session.Finished;
        _good.IsEnabled = markable;
        _bad.IsEnabled = markable;
        _skip.IsEnabled = markable;
        _stop.IsEnabled = enabled;
        _more.IsEnabled = enabled;
    }

    /// <summary>First non-empty line of git's output, trimmed for a one-line bar.</summary>
    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return string.Empty;
    }

    // ---- construction helpers -----------------------------------------------------------

    /// <summary>
    ///  One notification bar: an accent stripe down the leading edge, the message,
    ///  and whatever trails on the right.
    /// </summary>
    private static Border MakeBar(Control message, Control trailing)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("4,*,Auto"),
        };

        Border stripe = new()
        {
            Background = Brush("App.Accent", "#007ACC"),
        };

        message.Margin = new Thickness(8, 0, 8, 0);
        trailing.Margin = new Thickness(0, 0, 4, 0);

        Grid.SetColumn(stripe, 0);
        Grid.SetColumn(message, 1);
        Grid.SetColumn(trailing, 2);
        row.Children.Add(stripe);
        row.Children.Add(message);
        row.Children.Add(trailing);

        return new Border
        {
            IsVisible = false,
            Background = Brush("App.Panel", "#252526"),
            BorderBrush = Brush("App.Border", "#3F3F46"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 4),
            Child = row,
        };
    }

    private static Button MakeButton(string caption, string tooltip, Action onClick)
    {
        Button button = new()
        {
            Content = caption,
            FontSize = 12,
            Padding = new Thickness(10, 2),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // A language switch just re-renders from the current repository state.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        _good.Content = T("Good");
        _bad.Content = T("Bad");
        _skip.Content = T("Skip");
        _stop.Content = T("Stop bisect");
        _more.Content = T("InteractiveGitActionControl/MoreButton.Text", "More");
        Refresh();
    });

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
