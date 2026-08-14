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
///  from <see cref="BisectService"/>; the <b>merge</b> state gets upstream's own three —
///  <c>Resolve...</c>, <c>Continue</c> and <c>Abort</c>
///  (<c>InteractiveGitActionControl.cs:148-152</c>) — behind
///  <see cref="MergeSessionService"/>; the <b>rebase</b> state gets those plus
///  <c>Skip</c> (<c>FormRebase.cs:166-169</c>) behind
///  <see cref="RebaseSessionService"/>; and the <b>cherry-pick</b> and <b>revert</b>
///  states get those plus <c>Quit</c>, behind
///  <see cref="SequencerSessionService"/> — the one service that drives both, because git
///  drives both with one sequencer. Those last two have <b>no upstream to port</b>:
///  upstream's bar knows four states only
///  (<c>InteractiveGitActionControl.GitAction</c>, <c>:22-30</c>) and lets a stopped
///  cherry-pick or revert fall through to the state that offers nothing but
///  <c>Resolve...</c>, so the buttons here are this port's own — see
///  <see cref="SequencerSessionService"/>.</para>
///
///  <para><c>git am</c> keeps its state machine in its own dialog
///  (<see cref="ApplyPatchDialog"/>), so this bar does not grow a second copy of it and
///  still names the git commands in its hint. It contributes <b>one</b> button, and only
///  while the index is unmerged: <c>Resolve…</c>. That was the one real dead end left —
///  a conflicted <c>am</c> could be seen from here and resolved from nowhere, while
///  <see cref="ResolveConflictsDialog"/> had existed (with correct <c>am</c> rerere
///  wording) for milestones. The gate is the unmerged index, which is also exactly the
///  <c>am --3way</c> case: measured, a plain <c>git am</c> that fails leaves nothing
///  unmerged at all, so the button cannot appear for a state it could not help.</para>
///
///  <para><b>The two stopped-session states.</b> Upstream splits a stopped merge — and a
///  stopped rebase — in two by asking <c>Module.InTheMiddleOfConflictedMerge()</c>
///  (<c>InteractiveGitActionControl.cs:82</c>, <c>FormRebase.cs:151</c>): with unresolved
///  paths it says "… with merge conflicts." and offers <c>Resolve...</c>; with a clean
///  index the work itself is done and only the commit is missing, so it offers
///  <c>Continue</c>. Both states offer <c>Abort</c>. This port reads the same fact from
///  the index (<see cref="MergeSessionService.HasUnresolvedConflicts"/>,
///  <see cref="RebaseSessionService.HasUnresolvedConflicts"/>), and pays for that one
///  extra git process <i>only</i> while something is actually stopped — an idle
///  repository still costs nothing. The distinction matters most for the rebase, which
///  routinely stops with a perfectly clean index (an interactive <c>edit</c> or
///  <c>break</c>): telling that user to "resolve the conflicts" — as this bar used to —
///  is simply false, so <see cref="DescribeRebase"/> says the rebase is <i>paused</i>
///  instead.</para>
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
    private readonly RebaseSessionService _rebaseSession = new();
    private readonly SequencerSessionService _sequencer = new();

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

    // The buttons that end a stopped session. Shared by the merge and the rebase state,
    // because upstream's own bar shares them too (InteractiveGitActionControl.cs:142-152
    // adds the same ResolveButton / ContinueButton / AbortButton instances for both) —
    // which of them are up, and what they run, depends on the operation.
    private readonly StackPanel _sessionButtons;
    private readonly Button _editTodo;
    private readonly Button _resolve;
    private readonly Button _continue;
    private readonly Button _skipStep;
    private readonly Button _abort;
    private readonly Button _quit;

    // True while the bar is showing a session that still has unresolved conflicts: the
    // one state upstream paints orange instead of blue.
    private bool _conflicted;

    // The rebase state behind the buttons when the bar is showing a rebase; None
    // otherwise. Read on the refresh thread, used only on the UI thread.
    private RebaseSessionState _rebaseState = RebaseSessionState.None;

    // The cherry-pick/revert state behind the buttons when the bar is showing one of
    // those; None otherwise. Read on the refresh thread, used only on the UI thread — and
    // handed to every command, which is what spells them with the right verb.
    private SequencerSessionState _sequencerState = SequencerSessionState.None;

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

        // Upstream's own caption and position: FormRebase puts Edit todo before the
        // continue/skip/abort run (FormRebase.Designer.cs:160, FormRebase.cs:165). It is the
        // only one of these buttons that changes what the rebase is GOING to do rather than
        // what it does with the step it is on, which is why it leads.
        _editTodo = MakeButton(
            T("FormRebase/btnEditTodo.Text", "Edit todo..."),
            T("Change, reorder or remove the steps this rebase has not replayed yet (git rebase --edit-todo)"),
            OnEditTodo);

        _resolve = MakeButton(
            T("InteractiveGitActionControl/ResolveButton.Text", "Resolve..."),
            T("Open the conflict resolution dialog"),
            OnResolve);

        // The tooltip of these three names the git command they run, which differs per
        // operation, so it is (re)set in Apply rather than fixed here.
        _continue = MakeButton(
            T("InteractiveGitActionControl/ContinueButton.Text", "Continue"),
            string.Empty,
            OnContinue);

        // Upstream's notification bar has no Skip — only FormRebase does
        // (FormRebase.cs:168, "&Skip"). It is offered here because the bar is this port's
        // only rebase surface, and a rebase stopped on a step the user does not want is
        // otherwise a dead end: Continue cannot pass it and Abort throws away the steps
        // already replayed. Shown for the rebase only; `am` has its own dialog.
        // Upstream's own caption is the sentence-long "S&kip currently applying commit"
        // (FormRebase.Designer.cs:187), which does not fit a one-row bar and would fit
        // even less once translated; it is used as the tooltip instead, and the caption
        // matches the bisect bar's own Skip.
        _skipStep = MakeButton(T("Skip"), string.Empty, OnSkipStep);

        _abort = MakeButton(
            T("InteractiveGitActionControl/AbortButton.Text", "Abort"),
            string.Empty,
            OnAbort);

        // Cherry-pick and revert only, and the reason it exists at all is that `--quit` is
        // NOT a quieter `--abort`: it keeps everything the operation has done so far and
        // merely makes git forget it is doing it, where abort puts the repository back.
        // Neither name says that, and a user who guesses wrong loses either the work or
        // the way out — so the caption says which of the two it is, and the tooltip and the
        // confirmation spell out what stays behind (see SetSessionTips and OnQuit).
        // Neither merge nor rebase has the command, so the button is hidden for them.
        _quit = MakeButton(T("Quit (keep changes)"), string.Empty, OnQuit);

        _sessionButtons = new StackPanel
        {
            IsVisible = false,
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _editTodo, _resolve, _continue, _skipStep, _abort, _quit },
        };

        StackPanel actionTrailing = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _actionHint, _sessionButtons },
        };

        _actionBar = MakeBar(_actionText, actionTrailing, out _actionStripe);

        Content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _bisectBar, _actionBar },
        };

        TranslationService.LanguageChanged += OnLanguageChanged;

        // A live theme switch has to repaint the filled strip straight away. The fill and
        // the button faces come from App.* brushes, whose Color the theme manager mutates
        // in place, so those follow by themselves — but the ink on the fill is DERIVED
        // (InkFor picks black or white by measured contrast, and Brushes.Black/White are
        // not theme resources). Without this the ink stayed the previous theme's choice
        // until the next refresh: measured at 3.52:1 on the light theme's rust fill,
        // below the 4.5:1 floor, against 5.97:1 once repainted. ThemeManager.Apply sets
        // Application.RequestedThemeVariant (ThemeManager.cs:246), which is what raises
        // this event; it fires on the UI thread.
        ActualThemeVariantChanged += (_, _) => PaintActionBar(_conflicted);
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
            Apply(
                RepositoryProgress.None,
                BisectSession.None,
                false,
                RebaseSessionState.None,
                SequencerSessionState.None);
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
            // `git am` is asked the same question, through the same probe: a stopped am is
            // the other state whose only way forward is resolving the index. It is worth
            // saying why the probe is the RIGHT gate there and not merely a convenient one:
            // measured on git 2.43, a plain `git am` that fails to apply leaves NOTHING
            // unmerged (`git status --porcelain` and `git ls-files -u` both empty, exit
            // 128) — only `am --3way` produces a real merge, `UU` entries and three index
            // stages. So an unmerged index during an am IS the three-way case, which is
            // also the only one where rerere has anything to say (RerereOperation
            // .ApplyMailbox). Gating on the index therefore cannot offer a conflict dialog
            // for a state that has no conflicts, nor promise rerere where it cannot happen.
            bool hasConflicts = progress.Operation
                    is RepositoryOperation.Merge or RepositoryOperation.ApplyMailbox
                && _merge.HasUnresolvedConflicts(repo);

            // Same discipline again: the rebase state machine is only read while a
            // rebase is actually stopped. Read() itself asks git once (for the index),
            // and everything else it reports comes off the disk.
            RebaseSessionState rebase = IsRebase(progress.Operation)
                ? _rebaseSession.Read(repo)
                : RebaseSessionState.None;

            // And once more: the sequencer is only read while a cherry-pick or a revert is
            // actually stopped. Read() is disk-only except for the applied-step count, and
            // it re-checks the rebase directory itself, so it cannot mistake a rebase step
            // (which also leaves CHERRY_PICK_HEAD behind) for a cherry-pick.
            SequencerSessionState sequencer = IsSequencer(progress.Operation)
                ? _sequencer.Read(repo)
                : SequencerSessionState.None;

            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation)
                {
                    Apply(progress, session, hasConflicts, rebase, sequencer);
                }
            });
        });
    }

    // ---- rendering the state ---------------------------------------------------------

    private void Apply(
        RepositoryProgress progress,
        BisectSession session,
        bool hasConflicts,
        RebaseSessionState rebase,
        SequencerSessionState sequencer)
    {
        _session = session;
        _rebaseState = rebase;
        _sequencerState = sequencer;
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

            // A rebase only counts as actionable once the service confirms the session
            // is really there: the marker directory alone is also what a stopped `git am`
            // leaves behind, and RebaseSessionService.Read is the one that tells them
            // apart (InTheMiddleOfRebase). With InProgress false the bar falls back to
            // the old text-only behaviour rather than offering commands that would fail.
            bool rebasing = IsRebase(progress.Operation) && rebase.InProgress;

            // Same gate as the rebase's, for the same reason: the marker file alone is not
            // proof. CHERRY_PICK_HEAD is also what a stopped rebase step leaves behind, and
            // SequencerSessionService.Read is the one that refuses those. With InProgress
            // false the bar falls back to the old text-only behaviour rather than offering
            // commands that would fail.
            bool sequencing = IsSequencer(progress.Operation) && sequencer.InProgress;

            // `git am` needs no session gate of its own: unlike the rebase and the
            // sequencer, whose marker directories are also written by OTHER operations,
            // RepositoryStateService only reports ApplyMailbox for rebase-apply WITH the
            // `applying` marker — which is git's own definition of a stopped am.
            bool patching = progress.Operation == RepositoryOperation.ApplyMailbox;
            bool canResolve = ResolveConflictsRequested is not null;
            bool conflicts = merge || patching
                ? hasConflicts
                : rebasing
                    ? rebase.HasUnresolvedConflicts
                    : sequencing && sequencer.HasUnresolvedConflicts;

            _actionText.Text = DescribeOperation(progress, conflicts, rebase, sequencer);

            string hint = HintFor(progress.Operation, conflicts, canResolve, rebasing, sequencing);
            _actionHint.Text = hint;
            _actionHint.IsVisible = hint.Length > 0;

            // The am state contributes exactly ONE button, and only when it can do
            // something: the row appears for it solely to carry Resolve…
            bool amResolve = patching && conflicts && canResolve;
            _sessionButtons.IsVisible = merge || rebasing || sequencing || amResolve;

            // Abort is the one button every OTHER state keeps, so it is set here rather
            // than in each branch: the am row is the only one that hides it, and hiding it
            // there must not survive into the next state the bar shows.
            _abort.IsVisible = !amResolve;

            // Only the sequencer has a --quit; offering it anywhere else would be a button
            // for a command that does not exist.
            _quit.IsVisible = sequencing;

            if (merge)
            {
                // Upstream puts Resolve and Continue in the same slot and picks by the
                // index (InteractiveGitActionControl.cs:150); Abort is offered in both
                // states. No subscriber means no dialog to open, so Resolve stays down
                // and the hint above takes over.
                _editTodo.IsVisible = false;
                _resolve.IsVisible = hasConflicts && canResolve;
                _continue.IsVisible = !hasConflicts;
                _skipStep.IsVisible = false;
                SetSessionTips();
                EnableSessionButtons(true);
            }
            else if (rebasing)
            {
                // Upstream swaps Continue for Solve-conflicts (FormRebase.cs:166-167).
                // Here Continue stays put and goes GREY while the index is unmerged, and
                // Resolve... appears next to it: with four buttons on the row a swap would
                // shuffle the other three under the pointer, and a greyed Continue says
                // "this is how the rebase ends, but not yet" — which is the fact the user
                // needs. The enablement itself is upstream's rule, unchanged.
                // Only where there is a todo to edit: an explicit `rebase -i` with steps
                // still pending (RebaseSessionState.CanEditTodo). A plain `git rebase` also
                // runs on the merge backend and also keeps a todo, but the user never asked
                // for a list of steps there — and a rebase whose todo is exhausted has
                // nothing to show but the empty list, whose only possible edit throws the
                // remaining series away.
                _editTodo.IsVisible = rebase.CanEditTodo;
                _resolve.IsVisible = conflicts && canResolve;
                _continue.IsVisible = true;
                _skipStep.IsVisible = true;
                SetSessionTips();
                EnableSessionButtons(true);
            }
            else if (sequencing)
            {
                // The rebase's grammar, unchanged — this bar is not going to grow a fourth
                // one. Continue stays put and goes grey while the index is unmerged (git
                // exits 128 there, measured), Resolve... appears beside it, Abort is always
                // up. Skip only where git has a series to carry on with: on a
                // single-commit pick or revert it is Abort under another name, which is a
                // question the user cannot answer (SequencerSessionState.CanSkip).
                _editTodo.IsVisible = false;
                _resolve.IsVisible = conflicts && canResolve;
                _continue.IsVisible = true;
                _skipStep.IsVisible = sequencer.CanSkip;
                SetSessionTips();
                EnableSessionButtons(true);
            }
            else if (amResolve)
            {
                // Deliberately Resolve… ALONE. The am state machine belongs to
                // ApplyPatchDialog — it is the surface that knows the patch series, which
                // patch git stopped on, and what Skip means for the rest of it — so this bar
                // does not grow a second, thinner copy of it. What it does own is the dead
                // end: before this, a conflicted am offered nothing anywhere, because the
                // dialog had no conflict entry point either (it does now, same dialog).
                // Continue is left off rather than greyed, unlike the rebase's: there is no
                // state of an am in which THIS bar would enable it, so a permanently dead
                // button would be decoration.
                _editTodo.IsVisible = false;
                _resolve.IsVisible = true;
                _continue.IsVisible = false;
                _skipStep.IsVisible = false;
                EnableSessionButtons(true);
            }

            _conflicted = (merge || rebasing || sequencing || patching) && conflicts;
            PaintActionBar(_conflicted);
        }

        IsVisible = progress.IsActive;
    }

    /// <summary>Both rebase flavours; the commands and the buttons are identical.</summary>
    private static bool IsRebase(RepositoryOperation operation)
        => operation is RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive;

    /// <summary>
    ///  The two operations git runs through its sequencer. They share every button and
    ///  every rule; only the verb the commands are spelled with differs, and
    ///  <see cref="SequencerSessionService"/> is the one that knows which.
    /// </summary>
    private static bool IsSequencer(RepositoryOperation operation)
        => operation is RepositoryOperation.CherryPick or RepositoryOperation.Revert;

    /// <summary>
    ///  Names the exact git command each shared button will run, in its tooltip, for the
    ///  operation the bar is currently showing. The captions stay upstream's
    ///  (<c>Continue</c> / <c>Abort</c>), so the tooltip is the only place the difference
    ///  between <c>merge --continue</c> and <c>rebase --continue</c> is visible — and it
    ///  matters, because the two do very different things to the branch.
    /// </summary>
    private void SetSessionTips()
    {
        bool rebase = _rebaseState.InProgress;
        bool sequencer = _sequencerState.InProgress;

        // The verb the user sees has to be the verb git will be given, or the tooltip is
        // describing a command nobody ran.
        string verb = _sequencerState.IsRevert ? "revert" : "cherry-pick";

        ToolTip.SetTip(_continue, sequencer
            ? (_sequencerState.IsRevert
                ? T("Record the revert commit and carry on with the rest of the series (git revert --continue)")
                : T("Record this commit and carry on with the rest of the series (git cherry-pick --continue)"))
            : rebase
                ? T("Commit this step and replay the rest of the series (git rebase --continue)")
                : T("Record the merge commit (git merge --continue)"));

        ToolTip.SetTip(_skipStep, sequencer
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Leave out the commit it stopped on and carry on with the rest — the commits already made stay (git {0} --skip)"),
                verb)
            : T("Skip currently applying commit — its changes are dropped (git rebase --skip)"));

        // Abort and Quit are the pair this bar exists to disambiguate: one PUTS THE
        // REPOSITORY BACK, the other LEAVES IT EXACTLY AS IT IS and only forgets the
        // operation. Both tips are written to be read next to each other, because that is
        // how they are on screen. Measured behaviour, not the manual's wording — see
        // SequencerSessionService.Abort and .Quit.
        // …and Abort has a third reading, which is the one this tip used to get wrong: with
        // the stopped marker gone, git refuses to rewind past the commit the user made by
        // hand, so --abort undoes NOTHING and is byte-for-byte what --quit does. Measured on
        // git 2.43, markerless revert of a three-commit series: `git revert --abort` exited
        // 0 printing only "warning: You seem to have moved HEAD. Not rewinding, check your
        // HEAD!", the log, the work tree and the unmerged index were all untouched, and only
        // .git/sequencer disappeared. The old fixed tip promised the exact opposite —
        // "the commits already made are removed and the files go back" — while the
        // confirmation one click later said, truthfully, that nothing would be undone. A
        // tooltip that contradicts the dialog behind it is the worse of the two, because it
        // is what the user reads while deciding.
        ToolTip.SetTip(_abort, sequencer
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                !_sequencerState.HasStoppedMarker
                    ? T("End the operation. Nothing is undone here — the commit you made by hand moved the branch, so git refuses to rewind past it: no commit is removed and no file is restored, only the steps still to come are dropped (git {0} --abort)")
                    : T("Undo everything: the commits this operation already made are removed and the files go back to how they were before it started (git {0} --abort)"),
                verb)
            : rebase
                ? T("Discard the rebase and put the branch back where it started (git rebase --abort)")
                : T("Discard the merge and restore the working tree (git merge --abort)"));

        ToolTip.SetTip(_quit, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Stop here and change nothing: the commits already made stay, your files stay as they are now — conflict markers included — and git simply forgets the operation (git {0} --quit)"),
            verb));
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
            PaintSessionButtons(onFill: true);
            return;
        }

        _actionBar.Background = Brush("App.Panel", "#252526");
        _actionStripe.Background = Brush("App.Accent", "#007ACC");
        _actionText.Foreground = Brush("App.Text", "#DCDCDC");
        _actionHint.Foreground = Brush("App.TextDim", "#9B9B9B");
        PaintSessionButtons(onFill: false);
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
    private void PaintSessionButtons(bool onFill)
    {
        if (!onFill)
        {
            // Back to whatever the control theme wants: the bar is a normal panel again.
            _sessionButtons.Resources.Clear();
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
            _sessionButtons.Resources[$"ButtonBackground{state}"] = disabled ? face : Hovered(state) ? hover : face;
            _sessionButtons.Resources[$"ButtonForeground{state}"] = disabled ? dim : label;
            _sessionButtons.Resources[$"ButtonBorderBrush{state}"] = edge;
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
    private static string DescribeOperation(
        RepositoryProgress progress,
        bool hasConflicts,
        RebaseSessionState rebase,
        SequencerSessionState sequencer)
    {
        if (IsRebase(progress.Operation) && rebase.InProgress)
        {
            return DescribeRebase(rebase);
        }

        if (IsSequencer(progress.Operation) && sequencer.InProgress)
        {
            return DescribeSequencer(sequencer);
        }

        string headline = progress.Operation switch
        {
            // Upstream's own two merge sentences, with its own operation noun
            // (InteractiveGitActionControl.cs:13,15,19) — so the wording, and the
            // translations behind it, are the ones the user already knows. The states
            // below keep this port's phrasing, because upstream has no sentence for any of
            // them. The cherry-pick and revert lines here are the fallback for a session
            // the sequencer service could not confirm; the confirmed one is
            // DescribeSequencer, which has facts to report instead.
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
    ///  What the bar says about a stopped rebase, built from
    ///  <see cref="RebaseSessionService.Read"/> rather than from the plain marker-file
    ///  scan, because a rebase with buttons has to describe two different situations
    ///  truthfully:
    ///  <list type="bullet">
    ///   <item><b>stopped on a conflict</b> — upstream's own
    ///    "… in progress with merge conflicts." sentence, and the work is to resolve;</item>
    ///   <item><b>stopped on purpose</b> — an interactive <c>edit</c> or <c>break</c> with
    ///    a clean index. This is the case that made the old text wrong: the bar used to
    ///    tell every stopped rebase to "resolve the conflicts", of which there were
    ///    none. Here it says the rebase is <i>paused</i> and names the commit, and the
    ///    only thing needed is Continue.</item>
    ///  </list>
    ///  The step counter and the branch come from git's own marker files; nothing is
    ///  invented, and anything git did not record is simply left out.
    /// </summary>
    private static string DescribeRebase(RebaseSessionState rebase)
    {
        // Upstream's noun is a plain "Rebase" for both flavours
        // (InteractiveGitActionControl.cs:18); the interactive one is called out because
        // it is the flavour that stops without a conflict, which changes what the user
        // has to do next.
        string noun = rebase.Interactive ? T("Interactive rebase") : T("Rebase");

        string headline = rebase.HasUnresolvedConflicts
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("{0} is currently in progress with merge conflicts."),
                noun)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("{0} is paused — no conflicts to resolve."),
                noun);

        if (rebase.HasStepCount)
        {
            headline += "  " + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Step {0} of {1}."),
                rebase.Step,
                rebase.TotalSteps);
        }

        if (rebase.HeadName is { Length: > 0 } branch)
        {
            headline += "  " + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Branch: {0}."),
                branch);
        }

        // Only shown when git recorded it: "stopped-sha" exists for a stop on a
        // conflict, and the merge backend also leaves it for an `edit`.
        if (rebase.StoppedSha is { Length: > 0 } stopped)
        {
            headline += "  " + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Stopped at {0}."),
                stopped);
        }

        return headline;
    }

    /// <summary>
    ///  What the bar says about a stopped cherry-pick or revert, built from
    ///  <see cref="SequencerSessionService.Read"/>. It has the same two situations to tell
    ///  apart as the rebase, and it tells them apart the same way:
    ///  <list type="bullet">
    ///   <item><b>stopped on a conflict</b> — upstream's own
    ///    "… in progress with merge conflicts." sentence, and the work is to resolve;</item>
    ///   <item><b>clean index</b> — everything is staged and the only thing missing is the
    ///    commit, so the bar says the operation is <i>ready to finish</i> rather than
    ///    telling the user to resolve conflicts that are not there.</item>
    ///  </list>
    ///  …and a third one, which only <see cref="SequencerSessionState.HasStoppedMarker"/>
    ///  can tell apart: the stopped step has already been committed by hand and only the
    ///  remainder of the series is outstanding.
    ///  <para>The step counter is only shown for a real series (git keeps no sequencer
    ///  directory for a one-commit operation, so there is no "1 of 1" to invent) whose
    ///  marker is still there to anchor it — see
    ///  <see cref="SequencerSessionState.HasStepCount"/> for the double count that rules the
    ///  third case out — and the commit git stopped on comes from
    ///  <c>CHERRY_PICK_HEAD</c>/<c>REVERT_HEAD</c>, so it too is simply absent there.
    ///  Nothing is guessed: what git did not record is left out.</para>
    /// </summary>
    private static string DescribeSequencer(SequencerSessionState sequencer)
    {
        // "Cherry-pick"/"Revert" as the operation noun, in the slot upstream's own sentence
        // puts "Merge" and "Rebase" in — so the four states read as one family.
        string noun = sequencer.IsRevert ? T("Revert") : T("Cherry-pick");

        string headline = sequencer.HasUnresolvedConflicts
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("{0} is currently in progress with merge conflicts."),
                noun)
            : !sequencer.HasStoppedMarker
                // Third situation, and the reason this branch exists: the step git stopped
                // on has already been committed, by hand, from somewhere else — that is the
                // only way the marker can be gone while the series is still open. Saying
                // "only the commit is missing" here would tell the user to do again what
                // they have just done. What is actually missing is the rest of the series.
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("{0} is in progress — the step it stopped on is already committed; the rest of the series is waiting."),
                    noun)
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("{0} is in progress — nothing left to resolve, only the commit is missing."),
                    noun);

        if (sequencer.HasStepCount)
        {
            headline += "  " + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Step {0} of {1}."),
                sequencer.Step,
                sequencer.TotalSteps);
        }

        if (sequencer.StoppedSha is { Length: > 0 } stopped)
        {
            headline += "  " + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("Stopped at {0}."),
                stopped);
        }

        return headline;
    }

    /// <summary>
    ///  Names the command that finishes or undoes the operation, for the states that
    ///  have no button because this port has no service behind them (see the class
    ///  remarks). Merge and rebase have buttons now, so they contribute a hint only in
    ///  the one case where a button is missing: unresolved conflicts with no host wired
    ///  to <see cref="ResolveConflictsRequested"/>. That hint points at the commit
    ///  dialog, which really does resolve conflicts today
    ///  (<c>CommitDialog.cs:2267</c>), rather than sending the user to a terminal.
    /// </summary>
    private static string HintFor(
        RepositoryOperation operation,
        bool hasConflicts,
        bool canResolve,
        bool rebasing,
        bool sequencing) => operation switch
    {
        RepositoryOperation.Merge => hasConflicts && !canResolve
            ? T("Resolve the conflicts from the Commit tab, then use Continue.")
            : string.Empty,

        // With the session confirmed the buttons speak for themselves; the terminal
        // hint survives only for the case the service could not confirm (see Apply).
        RepositoryOperation.Rebase or RepositoryOperation.RebaseInteractive => rebasing
            ? hasConflicts && !canResolve
                ? T("Resolve the conflicts from the Commit tab, then use Continue.")
                : string.Empty
            : T("Resolve the conflicts, then run: git rebase --continue / --skip / --abort"),

        // The am keeps its commands in ApplyPatchDialog, so the hint's job here is to say
        // where the rest of the way out is — a bar carrying only Resolve… would otherwise
        // leave the user resolved and still stopped. Naming the git commands as well is
        // kept for the state where this port offers no button at all.
        RepositoryOperation.ApplyMailbox => hasConflicts
            ? canResolve
                ? T("Resolve the conflicts, then finish the patch from the Apply patch dialog (\"Conflicts resolved\").")
                : T("Resolve the conflicts from the Commit tab, then finish the patch from the Apply patch dialog (\"Conflicts resolved\").")
            : T("Finish the series from the Apply patch dialog, or run: git am --continue / --skip / --abort"),
        // Same shape as the rebase above: with the session confirmed the buttons speak for
        // themselves, and the terminal hint survives only for a session the service could
        // not confirm (see Apply) — which is now the only way a user can end up reading it.
        RepositoryOperation.CherryPick => sequencing
            ? hasConflicts && !canResolve
                ? T("Resolve the conflicts from the Commit tab, then use Continue.")
                : string.Empty
            : T("Resolve the conflicts, then run: git cherry-pick --continue / --skip / --abort / --quit"),

        RepositoryOperation.Revert => sequencing
            ? hasConflicts && !canResolve
                ? T("Resolve the conflicts from the Commit tab, then use Continue.")
                : string.Empty
            : T("Resolve the conflicts, then run: git revert --continue / --skip / --abort / --quit"),
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

    // ---- merge and rebase actions -----------------------------------------------------

    /// <summary>
    ///  Opens the todo editor — upstream's <c>Edit todo</c> (<c>FormRebase.cs:300</c>).
    ///  <para>Unlike Resolve and the bisect panel this one is <b>not</b> delegated to the
    ///  host: the window edits the rebase's own pending steps and needs nothing but the
    ///  repository and the service the bar already holds, so handing it out would only add
    ///  a wire nobody else pulls. What it can change is the same thing every other button
    ///  here changes, so it ends the same way — re-read the bar, tell the shell.</para>
    /// </summary>
    private void OnEditTodo() => _ = EditTodoAsync();

    private async Task EditTodoAsync()
    {
        if (_repoPath is not { Length: > 0 } repo
            || TopLevel.GetTopLevel(this) is not Window owner
            || !_rebaseState.CanEditTodo)
        {
            return;
        }

        try
        {
            EnableSessionButtons(false);

            // The window's own git calls are wrapped here rather than inside it: suspending
            // the watcher is the host's concern, and --edit-todo rewrites a file the
            // watcher is looking at.
            using IDisposable? guard = SuspendWatcher?.Invoke();
            await RebaseTodoWindow.ShowAsync(owner, repo, _rebaseSession);
        }
        catch (Exception ex)
        {
            // Same reasoning as RunSessionAsync: a throw here is a port bug, and taking the
            // app down from a click handler is not how it should be reported.
            _actionHint.Text = FirstLine(ex.Message);
            _actionHint.IsVisible = _actionHint.Text.Length > 0;
        }
        finally
        {
            EnableSessionButtons(true);
            Refresh();
            RepositoryChanged?.Invoke();
        }
    }

    // The host owns the conflict dialog, for the same reason it owns the bisect panel.
    // One event serves both operations: what the dialog does — stage the resolutions —
    // is the same either way, and the bar's own refresh afterwards is what turns the
    // conflicted state into the continuable one.
    private void OnResolve() => ResolveConflictsRequested?.Invoke();

    // Which git command the shared buttons run is decided here, by the state the last
    // refresh read. Each of the two InProgress flags is only true when the bar is actually
    // showing that confirmed session (see Apply), so a stale click cannot send a rebase
    // command to a merge, nor a cherry-pick command to a revert — the sequencer service
    // re-checks the verb against the state it is handed, too.
    private void OnContinue() => _ = _sequencerState.InProgress
        ? RunSessionAsync(
            T("InteractiveGitActionControl/ContinueButton.Text", "Continue"),
            $"{SequencerVerb} --continue",
            confirm: null,
            (repo, emit) => Outcome(_sequencer.Continue(repo, _sequencerState, emit)))
        : _rebaseState.InProgress
            ? RunSessionAsync(
                T("InteractiveGitActionControl/ContinueButton.Text", "Continue"),
                "rebase --continue",
                confirm: null,
                (repo, emit) => Outcome(RememberPending(_rebaseSession.Continue(repo, emit))),
                onExit: AskForStepMessageAsync)
            : RunSessionAsync(
                T("InteractiveGitActionControl/ContinueButton.Text", "Continue"),
                "merge --continue",
                confirm: null,
                (repo, emit) => Outcome(_merge.Continue(repo, emit)));

    // The verb of the stopped sequencer operation, for the process dialog's own title —
    // the command it announces has to be the command it runs.
    private string SequencerVerb => _sequencerState.IsRevert ? "revert" : "cherry-pick";

    // Rebase and sequencer: the button is hidden in every other state, and for the
    // sequencer only while git has a series left to carry on with (see Apply).
    private void OnSkipStep() => _ = _sequencerState.InProgress
        ? RunSessionAsync(
            T("Skip"),
            $"{SequencerVerb} --skip",
            // Same reasoning as the rebase's confirmation below — cheap to confirm,
            // impossible to undo — but the sentence is the sequencer's own: here the step
            // is a whole commit of the series, and what survives is everything before it.
            confirm: T("Skip this commit?\n\nThe commit it stopped on is left out: it will not be applied, and its changes will not be in your branch. The commits this operation has already made stay, and the rest of the series carries on."),
            (repo, emit) => Outcome(_sequencer.Skip(repo, _sequencerState, emit)))
        : RunSessionAsync(
            T("Skip"),
            "rebase --skip",
            // Not destructive the way an abort is — the rest of the series survives — but the
            // skipped step is gone for good, and upstream's own caption ("Skip currently
            // applying commit") does not say that out loud. Cheap to confirm, expensive to
            // undo. The wording stops short of naming what exactly is lost, because that
            // depends on the step: a `pick` that could not be applied loses its commit
            // outright, while a stop on an interactive `edit` has already applied the commit
            // and only the pending amend is abandoned — verified in GUI on both.
            confirm: T("Skip this step?\n\nThe rebase abandons the step it stopped on and carries on with the rest of the series. What that step was going to change will not be in the rebased branch, and git keeps no way back to it."),
            // Skipping replays every step after this one, so it can reach a `reword` just
            // as Continue can — and must ask for its message the same way.
            (repo, emit) => Outcome(RememberPending(_rebaseSession.Skip(repo, emit))),
            onExit: AskForStepMessageAsync);

    private void OnAbort() => _ = _sequencerState.InProgress
        ? RunSessionAsync(
            T("InteractiveGitActionControl/AbortButton.Text", "Abort"),
            $"{SequencerVerb} --abort",
            // The measured behaviour, in the words of the thing the user loses: it is not
            // only "stop", it is "put back", and it reaches commits that are already on the
            // branch. Written to be told apart from Quit's confirmation at a glance, since
            // the two buttons sit next to each other.
            // …except once the stopped step has been committed by hand, where git refuses to
            // rewind past a commit it did not make ("you seem to have moved HEAD, not
            // rewinding", exit 0, nothing touched — measured; see SequencerSessionService
            // .Abort). Promising a restore that git will not perform is the worst kind of
            // wrong confirmation: the user agrees to lose work and instead loses nothing,
            // and only finds out by inspecting the log afterwards.
            confirm: !_sequencerState.HasStoppedMarker
                // …and with the index STILL unmerged in that markerless state, "nothing is
                // restored" is true but dangerously incomplete: the unresolved conflict
                // survives the abort, and this bar — the only thing on screen that explains
                // why the index is in that shape — disappears with the sequencer directory.
                // Measured on git 2.43, markerless revert with `UU a.txt`: after
                // `git revert --abort` (exit 0, "not rewinding") `git ls-files -u` still
                // listed all three stages of a.txt, and the banner was gone on the next
                // refresh. Quit's confirmation already says this for the same state; Abort
                // must, because here the two commands do literally the same thing.
                ? _sequencerState.HasUnresolvedConflicts
                    ? T("Stop the operation?\n\nGit will not undo anything here: the commit you made by hand moved the branch, so it refuses to rewind past it. No commit is removed and no file is restored, and the conflict you have not resolved yet stays in the index — you will have to finish or undo it by hand, with nothing left on screen to remind you. Only the commits of the series still to come are dropped.")
                    : T("Stop the operation?\n\nGit will not undo anything here: the commit you made by hand moved the branch, so it refuses to rewind past it. Nothing is removed and no file is restored — the operation simply ends, and the commits of the series still to come are dropped.")
                : _sequencerState.IsRevert
                    ? T("Abort the revert?\n\nEverything goes back to how it was before the revert started: the revert commits it has already made are removed, and your files are restored. Conflict resolutions you have not committed are lost.")
                    : T("Abort the cherry-pick?\n\nEverything goes back to how it was before the cherry-pick started: the commits it has already applied are removed, and your files are restored. Conflict resolutions you have not committed are lost."),
            (repo, emit) => Outcome(_sequencer.Abort(repo, _sequencerState, emit)))
        : _rebaseState.InProgress
            ? RunSessionAsync(
                T("InteractiveGitActionControl/AbortButton.Text", "Abort"),
                "rebase --abort",
                confirm: T("Abort the rebase?\n\nEvery step already replayed is thrown away and the branch goes back to the commit it was on before the rebase started. Conflict resolutions you have not committed are lost."),
                (repo, emit) => Outcome(_rebaseSession.Abort(repo, emit)))
            : RunSessionAsync(
                T("InteractiveGitActionControl/AbortButton.Text", "Abort"),
                "merge --abort",
                // Upstream aborts straight away (InteractiveGitActionControl.cs:221). This port
                // asks first: the command throws the merge away AND rewrites the working tree,
                // so a mis-click costs work that git keeps no reflog of. Same reasoning as the
                // other destructive paths of this port (ResetChangesDialog, force-delete branch).
                confirm: T("Abort the merge?\n\nThe merge is discarded and every file goes back to the state it had before the merge started. Conflict resolutions you have not committed are lost."),
                (repo, emit) => Outcome(_merge.Abort(repo, emit)));

    /// <summary>
    ///  Sequencer only. Confirmed like the destructive buttons even though it destroys
    ///  nothing, because what it leaves behind is the surprising part: an unmerged index
    ///  and half-applied files, with nothing left in the repository to explain them — this
    ///  bar itself disappears afterwards, since git no longer reports an operation. The
    ///  wording therefore describes the state the user will be standing in, not the command.
    /// </summary>
    private void OnQuit() => _ = RunSessionAsync(
        T("Quit (keep changes)"),
        $"{SequencerVerb} --quit",
        confirm: _sequencerState.HasUnresolvedConflicts
            ? T("Stop and keep everything as it is?\n\nGit forgets the operation but changes nothing else: the commits it has already made stay on your branch, and your files stay exactly as they are — including the unresolved conflict, which you will then have to finish or undo by hand.")
            // Without the marker the step is already committed and the work tree is
            // typically clean, so the "staged and uncommitted" half of the sentence below
            // would describe files that do not exist. What is true in that state, and all
            // that is true, is that the remaining steps are dropped.
            : !_sequencerState.HasStoppedMarker
                ? T("Stop and keep everything as it is?\n\nGit forgets the operation but changes nothing else: every commit already on your branch stays, and no file is touched. Only the commits of the series still to come are dropped.")
                : T("Stop and keep everything as it is?\n\nGit forgets the operation but changes nothing else: the commits it has already made stay on your branch, and your files stay exactly as they are, staged and uncommitted. Any commits of the series still to come are dropped."),
        (repo, emit) => Outcome(_sequencer.Quit(repo, _sequencerState, emit)));

    // ---- the message a rebase step asks for -------------------------------------------

    /// <summary>
    ///  The message request the last rebase command came back with, handed from the
    ///  background operation to <see cref="AskForStepMessageAsync"/> on the UI thread.
    ///  <para>A field rather than a return value because
    ///  <see cref="GitProcessDialog.RunStreamingAsync"/>'s two halves are typed
    ///  separately: the operation must produce a <see cref="GitProcessOutcome"/>, which
    ///  carries only success and text. The hand-over is safe without a lock — the dialog
    ///  awaits the operation's completion before invoking the exit hook, and that
    ///  completion is the memory barrier — and it is single-shot: the reader clears it.</para>
    /// </summary>
    private RebaseMessageRequest? _pendingMessage;

    private RebaseCommandResult RememberPending(RebaseCommandResult result)
    {
        _pendingMessage = result.Pending;
        return result;
    }

    /// <summary>
    ///  Runs after every attempt of a rebase Continue / Skip, inside the process dialog.
    ///  With no pending message it returns false and the dialog reports the outcome as
    ///  usual; with one, it asks the user for the text and re-runs the command <b>in the
    ///  same window</b> (<see cref="GitProcessDialog.Retry"/>), which is what keeps a
    ///  three-<c>reword</c> series one dialog with three questions instead of six windows.
    ///
    ///  <para>Answering is what makes <c>reword</c> and <c>squash</c> real; see
    ///  <see cref="RebaseSessionService.ContinueWithMessage"/>. <b>Cancelling is a real
    ///  answer too</b>: false is returned, so the dialog settles showing git's own
    ///  "problem with the editor" exit — and the rebase stays stopped, with Continue,
    ///  Skip and Abort all live on this bar. Nothing is left that the app cannot
    ///  describe.</para>
    /// </summary>
    private async Task<bool> AskForStepMessageAsync(GitProcessDialog dialog, GitProcessOutcome outcome)
    {
        RebaseMessageRequest? request = _pendingMessage;
        _pendingMessage = null;

        if (request is null || _repoPath is not { Length: > 0 } repo)
        {
            return false;
        }

        string? message = await PromptMessageAsync(dialog, request);
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        dialog.Retry(
            emit =>
            {
                using IDisposable? guard = SuspendWatcher?.Invoke();
                return Outcome(RememberPending(_rebaseSession.ContinueWithMessage(repo, message, emit)));
            },
            note: T("Writing the message and carrying on (git commit --amend, then git rebase --continue)…"));

        return true;
    }

    /// <summary>
    ///  Asks for the message of the step git stopped on, prefilled with what git itself
    ///  prepared — the commit's current message for a <c>reword</c>, the combined one for
    ///  a <c>squash</c>. Same hand-built shape as the rest of this port's prompts
    ///  (<c>MainWindow.PromptAsync</c>, which is where reword and squash are asked for
    ///  from the revision grid), rather than a new window: what is being edited is a
    ///  commit message, and this port already has one way of asking for one.
    ///  <para>The caption names the todo command, so the user knows whether they are
    ///  renaming one commit or writing the message of a melded pair, and the note under
    ///  the box says what Cancel does — which is the non-obvious part, because git will
    ///  then keep the old message if the rebase is continued.</para>
    /// </summary>
    private async Task<string?> PromptMessageAsync(Window owner, RebaseMessageRequest request)
    {
        TaskCompletionSource<string?> tcs = new();

        TextBox input = new()
        {
            Text = request.Template,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 160,
            FontFamily = Theming.AppFonts.Monospace,
        };

        Button ok = new() { Content = T("OK"), MinWidth = 80, IsDefault = false };
        Button cancel = new()
        {
            Content = T("FormCommit/Cancel.Text", "Cancel"),
            MinWidth = 80,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        Theming.ZoomWindow dialog = new()
        {
            Title = request.Command switch
            {
                "reword" => T("Reword commit"),
                "squash" => T("Squash: combined commit message"),

                // A `fixup -c`, a `merge` step, or a done file we could not read: git asked
                // for a message and we do not know which command for. Say exactly that
                // rather than guess a caption that could name the wrong operation.
                _ => T("Commit message for this rebase step"),
            },
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", "#1E1E1E"),
        };

        ok.Click += (_, _) => { tcs.TrySetResult(input.Text); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = request.Command == "squash"
                        ? T("The rebase is melding this commit into the previous one. This is the message the combined commit will carry.")
                        : T("The rebase stopped to let you write this commit's message."),
                    Foreground = Brush("App.Text", "#DCDCDC"),
                    TextWrapping = TextWrapping.Wrap,
                },
                input,
                new TextBlock
                {
                    Text = T("Cancel leaves the rebase stopped where it is; continuing it afterwards keeps the message the commit already has."),
                    Foreground = Brush("App.TextDim", "#9A9A9A"),
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };

        // No InstallEscapeClose beyond the Cancel button's IsCancel: the box accepts
        // Return as text (it is a commit message), so the usual default-button wiring
        // would swallow the very key the user needs.
        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }

    // The three session services report the same shape through three distinct record types;
    // these keep the runner below indifferent to which one ran.
    private static GitProcessOutcome Outcome(MergeCommandResult result)
        => new(result.Success, result.Output);

    private static GitProcessOutcome Outcome(RebaseCommandResult result)
        => new(result.Success, result.Output);

    private static GitProcessOutcome Outcome(SequencerCommandResult result)
        => new(result.Success, result.Output);

    /// <summary>
    ///  Runs one merge- or rebase-session command through the port's process dialog — the
    ///  same surface every other git command of this port reports through, and upstream's
    ///  own choice for all of them (<c>FormProcess.ShowDialog</c>,
    ///  <c>InteractiveGitActionControl.cs:196,221</c>, <c>FormRebase.cs:247,270,287</c>).
    ///  Non-interactive: none of these commands can ask a question, and the ones that
    ///  would open an editor are pinned to a no-op one by their service.
    ///  <para>Whatever happens, the banner re-reads the repository afterwards and tells
    ///  the host to refresh, so the bar shows the new state (or disappears) without the
    ///  user touching anything. Never throws: this runs from a click handler.</para>
    /// </summary>
    private async Task RunSessionAsync(
        string label,
        string command,
        string? confirm,
        Func<string, Action<string>, GitProcessOutcome> operation,
        Func<GitProcessDialog, GitProcessOutcome, Task<bool>>? onExit = null)
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

            EnableSessionButtons(false);

            await GitProcessDialog.RunStreamingAsync(
                owner,
                $"{label} (git {command})",
                emit =>
                {
                    using IDisposable? guard = SuspendWatcher?.Invoke();
                    return operation(repo, emit);
                },
                onExit: onExit,
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
            EnableSessionButtons(true);
            Refresh();
            RepositoryChanged?.Invoke();
        }
    }

    /// <summary>
    ///  Enables the shared session buttons for what the repository actually allows.
    ///  Passing <see langword="false"/> disables everything for the duration of a command.
    ///  <para>The one conditional rule is upstream's: <c>git rebase --continue</c> refuses
    ///  to run while the index has an unmerged path, so it is greyed out until the last
    ///  conflict is staged (<c>FormRebase.cs:166</c> expresses the same rule by hiding the
    ///  button instead — see <see cref="Apply"/> for why this bar greys it). The merge
    ///  state does not need the rule: there, Continue is not on screen at all while the
    ///  index is conflicted.</para>
    ///  <para>The sequencer obeys the same rule for the same reason, and it was measured
    ///  rather than assumed: <c>git cherry-pick --continue</c> over an unmerged index does
    ///  not fail politely, it exits <b>128</b> with <i>"fatal: exiting because of an
    ///  unresolved conflict"</i>.</para>
    /// </summary>
    private void EnableSessionButtons(bool enabled)
    {
        _editTodo.IsEnabled = enabled && _rebaseState.CanEditTodo;
        _resolve.IsEnabled = enabled;
        _continue.IsEnabled = enabled
            && (!_rebaseState.InProgress || _rebaseState.CanContinue)
            && (!_sequencerState.InProgress || _sequencerState.CanContinue);
        _skipStep.IsEnabled = enabled && (_rebaseState.CanSkip || _sequencerState.CanSkip);
        _abort.IsEnabled = enabled;
        _quit.IsEnabled = enabled;
    }

    // A yes/no modal, the same hand-built shape the rest of this port uses (Avalonia
    // ships no message box) — modelled on BisectDialog.ConfirmAsync:456.
    private async Task<bool> ConfirmAsync(Window owner, string message, string caption)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Confirm"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("FormCommit/Cancel.Text", "Cancel"), IsCancel = true };

        Theming.ZoomWindow dialog = new()
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
            BorderBrush = Brush("App.Rule", "#3F3F46"),
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

        // An empty string is a real tip as far as Avalonia is concerned and would pop an
        // empty box; the shared session buttons pass one deliberately, because their tip
        // names a command that is only known per operation (see SetSessionTips).
        if (tooltip.Length > 0)
        {
            ToolTip.SetTip(button, tooltip);
        }

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
        _editTodo.Content = T("FormRebase/btnEditTodo.Text", "Edit todo...");
        _resolve.Content = T("InteractiveGitActionControl/ResolveButton.Text", "Resolve...");
        _continue.Content = T("InteractiveGitActionControl/ContinueButton.Text", "Continue");
        _skipStep.Content = T("Skip");
        _abort.Content = T("InteractiveGitActionControl/AbortButton.Text", "Abort");
        _quit.Content = T("Quit (keep changes)");
        SetSessionTips();
        Refresh();
    });

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
