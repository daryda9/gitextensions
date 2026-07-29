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
///  <para><b>Which buttons are real.</b> The bisect bar gets good, bad, skip and stop
///  from <see cref="BisectService"/>, and the <b>merge</b> state gets upstream's own
///  three — <c>Resolve...</c>, <c>Continue</c> and <c>Abort</c>
///  (<c>InteractiveGitActionControl.cs:148-152</c>) — now that
///  <see cref="MergeSessionService"/> implements the two commands behind them. There is
///  still deliberately <i>no</i> continue/abort button for rebase, cherry-pick, revert
///  or <c>git am</c>: the port has no service behind those (the only <c>--abort</c>
///  calls left in <c>App/Services</c> are private clean-up paths inside
///  <c>CommitEditService</c> and <c>PatchService</c>, not an API), and a button that
///  cannot do its job is worse than no button. Those states show what is going on
///  and name the git command that finishes or undoes it, which is honest and still
///  removes the "why is my repository behaving strangely" dead end.</para>
///
///  <para><b>The two merge states.</b> Upstream splits a stopped merge in two by asking
///  <c>Module.InTheMiddleOfConflictedMerge()</c> (<c>InteractiveGitActionControl.cs:82</c>):
///  with unresolved paths it says "… with merge conflicts." and offers
///  <c>Resolve...</c>; with a clean index the merge itself is done and only the commit
///  is missing, so it offers <c>Continue</c>. Both states offer <c>Abort</c>. This port
///  reads the same fact from the index
///  (<see cref="MergeSessionService.HasUnresolvedConflicts"/>), and pays for that one
///  extra git process <i>only</i> while a merge is actually in progress — an idle
///  repository still costs nothing.</para>
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
    private readonly MergeSessionService _merge = new();

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
    private readonly Border _actionStripe;
    private readonly TextBlock _actionText;
    private readonly TextBlock _actionHint;

    // Upstream's three merge buttons. Resolve and Continue share one slot — which of
    // the two is up depends on whether the index still has unmerged paths.
    private readonly StackPanel _mergeButtons;
    private readonly Button _resolve;
    private readonly Button _continue;
    private readonly Button _abort;

    // True while the bar is showing a merge that still has unresolved conflicts: the
    // one state upstream paints orange instead of blue.
    private bool _conflicted;

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

        _bisectBar = MakeBar(_bisectText, bisectButtons, out _);

        _actionText = new TextBlock
        {
            Foreground = Brush("App.Text", "#DCDCDC"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The command that finishes or undoes the operation, for the states that still
        // have no button. Dim, because it is a hint and not an offer — see the class
        // remarks on which states those are.
        _actionHint = new TextBlock
        {
            Foreground = Brush("App.TextDim", "#9B9B9B"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _resolve = MakeButton(
            T("InteractiveGitActionControl/ResolveButton.Text", "Resolve..."),
            T("Open the conflict resolution dialog"),
            OnResolve);

        _continue = MakeButton(
            T("InteractiveGitActionControl/ContinueButton.Text", "Continue"),
            T("Record the merge commit (git merge --continue)"),
            OnContinue);

        _abort = MakeButton(
            T("InteractiveGitActionControl/AbortButton.Text", "Abort"),
            T("Discard the merge and restore the working tree (git merge --abort)"),
            OnAbort);

        _mergeButtons = new StackPanel
        {
            IsVisible = false,
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _resolve, _continue, _abort },
        };

        StackPanel actionTrailing = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _actionHint, _mergeButtons },
        };

        _actionBar = MakeBar(_actionText, actionTrailing, out _actionStripe);

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
    ///  Raised, on the UI thread, by the merge bar's <c>Resolve...</c> button —
    ///  upstream's own entry point from the notification bar into the conflict
    ///  resolution dialog (<c>InteractiveGitActionControl.cs:180</c>,
    ///  <c>StartResolveConflictsDialog</c>). The banner deliberately does <b>not</b> own
    ///  that dialog: like the bisect panel, it belongs to the host, which is the one
    ///  that can refresh the rest of the shell afterwards.
    ///
    ///  <para><b>Contract for the host.</b> Subscribe before (or at any time after) the
    ///  first <see cref="SetRepository"/>; the handler runs on the UI thread and takes
    ///  no argument — the repository is the one the host already gave the banner. Open
    ///  the dialog modally, then call the banner's <see cref="Refresh"/> (or just
    ///  refresh the shell, which does) so the bar re-reads the index: resolving the last
    ///  conflict turns "… with merge conflicts." into the plain "in progress" state with
    ///  a <c>Continue</c> button, and committing the merge makes the bar disappear.
    ///  <b>With nothing subscribed the button is hidden</b> and the bar falls back to a
    ///  one-line hint pointing at the commit dialog, which is where this port's conflict
    ///  resolution lives today — an inert button would be a lie.</para>
    /// </summary>
    public event Action? ResolveConflictsRequested;

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
            Apply(RepositoryProgress.None, BisectSession.None, false);
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

            // Same discipline as the bisect probe above: the index is only inspected
            // while a merge is actually stopped, so an idle repository still costs no
            // git process. Upstream asks on every refresh instead
            // (InteractiveGitActionControl.cs:82).
            bool hasConflicts = progress.Operation == RepositoryOperation.Merge
                && _merge.HasUnresolvedConflicts(repo);

            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation)
                {
                    Apply(progress, session, hasConflicts);
                }
            });
        });
    }

    // ---- rendering the state ---------------------------------------------------------

    private void Apply(RepositoryProgress progress, BisectSession session, bool hasConflicts)
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
            bool merge = progress.Operation == RepositoryOperation.Merge;
            bool canResolve = ResolveConflictsRequested is not null;

            _actionText.Text = DescribeOperation(progress, hasConflicts);

            string hint = HintFor(progress.Operation, hasConflicts, canResolve);
            _actionHint.Text = hint;
            _actionHint.IsVisible = hint.Length > 0;

            _mergeButtons.IsVisible = merge;
            if (merge)
            {
                // Upstream puts Resolve and Continue in the same slot and picks by the
                // index (InteractiveGitActionControl.cs:150); Abort is offered in both
                // states. No subscriber means no dialog to open, so Resolve stays down
                // and the hint above takes over.
                _resolve.IsVisible = hasConflicts && canResolve;
                _continue.IsVisible = !hasConflicts;
                EnableMergeButtons(true);
            }

            _conflicted = merge && hasConflicts;
            PaintActionBar(_conflicted);
        }

        IsVisible = progress.IsActive;
    }

    /// <summary>
    ///  Repaints the action bar for the state it is showing. Upstream fills the whole
    ///  strip orange when there are unresolved conflicts and light blue otherwise, and
    ///  picks the ink from the fill (<c>InteractiveGitActionControl.cs:130-132</c>,
    ///  <c>SetForeColorForBackColor</c>). Ported as: the themed
    ///  <c>App.RepoStateDirty</c> as the fill (the port's existing "this repository needs
    ///  attention" orange, see <c>MainToolbar.cs:1000</c>), and the ink chosen by
    ///  measured contrast, because the port has no on-accent foreground key.
    ///  <para>Called from <see cref="Apply"/>, so the brushes are re-read from the
    ///  application resources on every refresh instead of being cached in the
    ///  constructor: a theme switch is picked up by the next refresh.</para>
    /// </summary>
    private void PaintActionBar(bool conflicted)
    {
        if (conflicted)
        {
            IBrush fill = Brush("App.RepoStateDirty", "#FFA07A");
            IBrush ink = InkFor(fill);

            _actionBar.Background = fill;

            // The leading accent stripe would read as a stray blue notch inside a filled
            // strip; upstream's filled bar has no stripe at all.
            _actionStripe.Background = fill;
            _actionText.Foreground = ink;
            _actionHint.Foreground = ink;
            PaintMergeButtons(onFill: true);
            return;
        }

        _actionBar.Background = Brush("App.Panel", "#252526");
        _actionStripe.Background = Brush("App.Accent", "#007ACC");
        _actionText.Foreground = Brush("App.Text", "#DCDCDC");
        _actionHint.Foreground = Brush("App.TextDim", "#9B9B9B");
        PaintMergeButtons(onFill: false);
    }

    /// <summary>
    ///  Keeps the merge buttons readable on the filled strip. A <see cref="Button"/> in
    ///  the Fluent theme paints its face from a <i>translucent</i> overlay resolved
    ///  through <c>ButtonBackground*</c>, so over an orange parent it turns into pale
    ///  orange carrying the dark theme's near-white label — measured at 1.4:1, i.e.
    ///  invisible. Upstream has the same bar with ordinary system buttons on it (light
    ///  face, dark label), so the fix is to give these three an opaque control face.
    ///  <para>The face has to be pinned through the theme's own per-state resource keys,
    ///  set on this instance only: a plain local <c>Background</c> is beaten by the
    ///  control theme's style setters in <c>:pointerover</c> / <c>:pressed</c>, the same
    ///  trap <c>Theming/TextBoxSurface</c> exists for. Keys the theme in use does not
    ///  consume are simply inert.</para>
    /// </summary>
    private void PaintMergeButtons(bool onFill)
    {
        if (!onFill)
        {
            // Back to whatever the control theme wants: the bar is a normal panel again.
            _mergeButtons.Resources.Clear();
            return;
        }

        IBrush face = Brush("App.Panel", "#252526");
        IBrush hover = Brush("App.Selection", "#094771");
        IBrush label = Brush("App.Text", "#DCDCDC");
        IBrush edge = Brush("App.Border", "#3F3F46");
        IBrush dim = Brush("App.TextDim", "#9B9B9B");

        foreach (string state in ButtonStates)
        {
            bool disabled = state == "Disabled";
            _mergeButtons.Resources[$"ButtonBackground{state}"] = disabled ? face : Hovered(state) ? hover : face;
            _mergeButtons.Resources[$"ButtonForeground{state}"] = disabled ? dim : label;
            _mergeButtons.Resources[$"ButtonBorderBrush{state}"] = edge;
        }

        static bool Hovered(string state) => state is "PointerOver" or "Pressed";
    }

    // The suffixes the Fluent button theme appends to its brush keys.
    private static readonly string[] ButtonStates = ["", "PointerOver", "Pressed", "Disabled"];

    /// <summary>
    ///  Black or white, whichever has the higher WCAG contrast ratio against
    ///  <paramref name="fill"/> — the port of upstream's <c>SetForeColorForBackColor</c>.
    ///  These two are not theme colours and must not come from a theme key: the point is
    ///  that the ink is derived from the fill, so it stays legible in both themes
    ///  (<c>App.RepoStateDirty</c> is a light salmon in dark and a dark rust in light,
    ///  which need opposite inks). Falls back to the normal body ink for a fill this
    ///  cannot measure (a gradient, or a missing key).
    /// </summary>
    private static IBrush InkFor(IBrush fill)
    {
        if (fill is not ISolidColorBrush solid)
        {
            return Brush("App.Text", "#DCDCDC");
        }

        double luminance = RelativeLuminance(solid.Color);
        double onBlack = (luminance + 0.05) / 0.05;
        double onWhite = 1.05 / (luminance + 0.05);
        return onBlack >= onWhite ? Brushes.Black : Brushes.White;
    }

    /// <summary>WCAG 2.1 relative luminance of a colour (alpha ignored).</summary>
    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte raw)
        {
            double v = raw / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R))
            + (0.7152 * Channel(color.G))
            + (0.0722 * Channel(color.B));
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
    private static string DescribeOperation(RepositoryProgress progress, bool hasConflicts)
    {
        string headline = progress.Operation switch
        {
            // Upstream's own two merge sentences, with its own operation noun
            // (InteractiveGitActionControl.cs:13,15,19) — so the wording, and the
            // translations behind it, are the ones the user already knows. The states
            // below keep this port's phrasing: upstream has no sentence for a stopped
            // cherry-pick or revert, and none of them has buttons to describe yet.
            RepositoryOperation.Merge => string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                hasConflicts
                    ? T("{0} is currently in progress with merge conflicts.")
                    : T("{0} is currently in progress."),
                T("Merge")),
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
    ///  Names the command that finishes or undoes the operation, for the states that
    ///  have no button because this port has no service behind them (see the class
    ///  remarks). Merge has buttons now, so it contributes a hint only in the one case
    ///  where a button is missing: unresolved conflicts with no host wired to
    ///  <see cref="ResolveConflictsRequested"/>. That hint points at the commit dialog,
    ///  which really does resolve conflicts today (<c>CommitDialog.cs:2267</c>), rather
    ///  than sending the user to a terminal.
    /// </summary>
    private static string HintFor(RepositoryOperation operation, bool hasConflicts, bool canResolve) => operation switch
    {
        RepositoryOperation.Merge => hasConflicts && !canResolve
            ? T("Resolve the conflicts from the Commit tab, then use Continue.")
            : string.Empty,
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

    // ---- merge actions ----------------------------------------------------------------

    // The host owns the conflict dialog, for the same reason it owns the bisect panel.
    private void OnResolve() => ResolveConflictsRequested?.Invoke();

    private void OnContinue() => _ = RunMergeAsync(
        T("InteractiveGitActionControl/ContinueButton.Text", "Continue"),
        "merge --continue",
        confirm: null,
        (service, repo, emit) => service.Continue(repo, emit));

    private void OnAbort() => _ = RunMergeAsync(
        T("InteractiveGitActionControl/AbortButton.Text", "Abort"),
        "merge --abort",
        // Upstream aborts straight away (InteractiveGitActionControl.cs:221). This port
        // asks first: the command throws the merge away AND rewrites the working tree,
        // so a mis-click costs work that git keeps no reflog of. Same reasoning as the
        // other destructive paths of this port (ResetChangesDialog, force-delete branch).
        confirm: T("Abort the merge?\n\nThe merge is discarded and every file goes back to the state it had before the merge started. Conflict resolutions you have not committed are lost."),
        (service, repo, emit) => service.Abort(repo, emit));

    /// <summary>
    ///  Runs one merge-session command through the port's process dialog — the same
    ///  surface every other git command of this port reports through, and upstream's own
    ///  choice for these two (<c>FormProcess.ShowDialog</c>,
    ///  <c>InteractiveGitActionControl.cs:196,221</c>). Non-interactive: neither command
    ///  can ever ask a question, and <c>--continue</c> is pinned to a no-op editor by
    ///  <see cref="MergeSessionService"/>.
    ///  <para>Whatever happens, the banner re-reads the repository afterwards and tells
    ///  the host to refresh, so the bar shows the new state (or disappears) without the
    ///  user touching anything. Never throws: this runs from a click handler.</para>
    /// </summary>
    private async Task RunMergeAsync(
        string label,
        string command,
        string? confirm,
        Func<MergeSessionService, string, Action<string>, MergeCommandResult> operation)
    {
        if (_repoPath is not { Length: > 0 } repo
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        try
        {
            if (confirm is { Length: > 0 } prompt && !await ConfirmAsync(owner, prompt, label))
            {
                return;
            }

            EnableMergeButtons(false);

            await GitProcessDialog.RunStreamingAsync(
                owner,
                $"{label} (git {command})",
                emit =>
                {
                    using IDisposable? guard = SuspendWatcher?.Invoke();
                    MergeCommandResult result = operation(_merge, repo, emit);
                    return new GitProcessOutcome(result.Success, result.Output);
                },
                interactive: false);
        }
        catch (Exception ex)
        {
            // A throw here would be a port bug, not a git failure (git failures come
            // back as an exit code the process dialog already shows). Say it in the bar
            // rather than taking the app down from a click handler.
            _actionHint.Text = FirstLine(ex.Message);
            _actionHint.IsVisible = _actionHint.Text.Length > 0;
        }
        finally
        {
            EnableMergeButtons(true);
            Refresh();
            RepositoryChanged?.Invoke();
        }
    }

    private void EnableMergeButtons(bool enabled)
    {
        _resolve.IsEnabled = enabled;
        _continue.IsEnabled = enabled;
        _abort.IsEnabled = enabled;
    }

    // A yes/no modal, the same hand-built shape the rest of this port uses (Avalonia
    // ships no message box) — modelled on BisectDialog.ConfirmAsync:456.
    private async Task<bool> ConfirmAsync(Window owner, string message, string caption)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Confirm"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("FormCommit/Cancel.Text", "Cancel"), IsCancel = true };

        Window dialog = new()
        {
            Title = caption,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", "#252526"),
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    Foreground = Brush("App.Text", "#DCDCDC"),
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };

        DialogKeys.InstallEscapeClose(dialog);
        await dialog.ShowDialog(owner);
        return await tcs.Task;
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
    private static Border MakeBar(Control message, Control trailing, out Border accentStripe)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("4,*,Auto"),
        };

        Border stripe = new()
        {
            Background = Brush("App.Accent", "#007ACC"),
        };

        accentStripe = stripe;

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
        _resolve.Content = T("InteractiveGitActionControl/ResolveButton.Text", "Resolve...");
        _continue.Content = T("InteractiveGitActionControl/ContinueButton.Text", "Continue");
        _abort.Content = T("InteractiveGitActionControl/AbortButton.Text", "Abort");
        Refresh();
    });

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
