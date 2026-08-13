using System.Text;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>The outcome of one sequencer command: git's exit code, plus everything it printed.</summary>
/// <param name="Success">True when git exited with code 0. The only success signal used here.</param>
/// <param name="Output">Everything git wrote, in order, for the process dialog and the log.</param>
public sealed record SequencerCommandResult(bool Success, string Output);

/// <summary>
///  Snapshot of git's <i>sequencer</i> state — the one state machine that drives both
///  <c>git cherry-pick</c> and <c>git revert</c>.
///
///  <para>The two operations are modelled together, and served by one service, because
///  git implements them as one: the same <c>sequencer.c</c>, the same
///  <c>.git/sequencer/</c> directory, the same four sub-commands, the same rules about
///  when each is allowed. Only the verb on the command line and the marker file
///  (<c>CHERRY_PICK_HEAD</c> against <c>REVERT_HEAD</c>) differ, so
///  <see cref="Operation"/> carries that one difference and everything else is shared.
///  Two copies of this type would be two chances to fix a bug once.</para>
/// </summary>
/// <param name="InProgress">
///  A cherry-pick or a revert is stopped, waiting for the user. The master switch: with
///  it false there is nothing to continue, skip, abort or quit.
/// </param>
/// <param name="Operation">
///  <see cref="RepositoryOperation.CherryPick"/> or <see cref="RepositoryOperation.Revert"/>
///  — which verb the commands have to be spelled with.
///  <see cref="RepositoryOperation.None"/> when nothing is stopped.
/// </param>
/// <param name="HasUnresolvedConflicts">
///  The index still has unmerged entries. <c>--continue</c> is impossible until this
///  clears: measured, it does not merely fail, it exits <b>128</b> with
///  <i>"fatal: exiting because of an unresolved conflict"</i>.
/// </param>
/// <param name="PendingSteps">
///  Commands still listed in <c>.git/sequencer/todo</c>, <b>including the one git is
///  stopped on</b> (measured: a three-commit pick that fails on the first still lists all
///  three). 0 means there is no sequencer directory at all, which is the recorded trace of
///  a <b>single-commit</b> pick or revert — see <see cref="HasSequence"/>.
/// </param>
/// <param name="AppliedSteps">
///  How many commits this sequence has already created, counted as
///  <c>sequencer/head..HEAD</c>. 0 for a single-commit operation, and for the first step
///  of a sequence.
/// </param>
/// <param name="StoppedSha">
///  The commit git is stopped on — the content of <c>CHERRY_PICK_HEAD</c>/<c>REVERT_HEAD</c>,
///  abbreviated. For a revert this is the commit being <i>undone</i>, not the commit being
///  created. Null when there is no marker to read it from — see
///  <paramref name="HasStoppedMarker"/>; an invented one would name the wrong commit.
/// </param>
/// <param name="SequencerDir">The resolved <c>.git/sequencer</c> path, or "" when there is none.</param>
/// <param name="HasStoppedMarker">
///  True when this session was recognised by <c>CHERRY_PICK_HEAD</c>/<c>REVERT_HEAD</c>,
///  false when only <c>.git/sequencer/todo</c> was left to recognise it by — which is the
///  state a sequence is in after the stopped step has been committed by hand, because git
///  deletes the marker on any commit without ending the sequence
///  (<c>sequencer_post_commit_cleanup</c>). The distinction is not cosmetic: three of the
///  facts below are only true while the marker is there, and each of them was measured
///  rather than reasoned about.
/// </param>
public sealed record SequencerSessionState(
    bool InProgress,
    RepositoryOperation Operation = RepositoryOperation.None,
    bool HasUnresolvedConflicts = false,
    int PendingSteps = 0,
    int AppliedSteps = 0,
    string? StoppedSha = null,
    string SequencerDir = "",
    bool HasStoppedMarker = false)
{
    /// <summary>Nothing stopped — also the answer for "no repository" and for every failure.</summary>
    public static SequencerSessionState None { get; } = new(false);

    /// <summary>True for a revert, false for a cherry-pick. Only meaningful while <see cref="InProgress"/>.</summary>
    public bool IsRevert => Operation == RepositoryOperation.Revert;

    /// <summary>
    ///  True when git recorded a real <i>series</i> — i.e. there is a
    ///  <c>.git/sequencer/todo</c>. Measured on git 2.43: a single-commit
    ///  <c>git revert &lt;sha&gt;</c> that conflicts leaves <b>no</b> sequencer directory at
    ///  all, only <c>REVERT_HEAD</c>; <c>git cherry-pick A B C</c> leaves one with the whole
    ///  list. This is the fact that decides whether <c>--skip</c> is worth offering.
    /// </summary>
    public bool HasSequence => PendingSteps > 0;

    /// <summary>
    ///  True when a "step N of M" can be shown: there is a series <b>and</b> the marker is
    ///  still there to anchor the count.
    ///
    ///  <para><b>Why the marker is part of the condition.</b> The count is "already applied
    ///  plus still listed", and those two sets overlap by exactly one entry once the stopped
    ///  step has been committed by hand: git drops a todo entry only when <c>--continue</c>
    ///  advances past it, never on a plain commit. Measured on git 2.43, three-commit pick
    ///  stopped on the first and committed from a shell: <c>sequencer/head..HEAD</c> was
    ///  <b>1</b> while the todo still listed <b>3</b> — so the counter would have read
    ///  "Step 2 of 4" for a series of three, wrong in both numbers. There is nothing left on
    ///  disk to correct it with (the todo does not say which of its entries is already
    ///  committed), so the counter is suppressed instead of repaired. A missing counter is
    ///  silence; a wrong one is a lie about the repository.</para>
    /// </summary>
    public bool HasStepCount => HasStoppedMarker && PendingSteps > 0;

    /// <summary>1-based number of the step git is stopped on.</summary>
    public int Step => AppliedSteps + 1;

    /// <summary>Steps in the whole series: the ones already created plus the ones still listed.</summary>
    public int TotalSteps => AppliedSteps + PendingSteps;

    /// <summary>
    ///  <c>--continue</c> can run: something is stopped and the index has no unmerged path
    ///  left. Same rule, and same reason, as
    ///  <see cref="RebaseSessionState.CanContinue"/> — git refuses to commit over an
    ///  unmerged index.
    ///  <para>Deliberately not narrowed for the markerless state: measured on git 2.43, a
    ///  three-commit pick whose first step was resolved and committed from a shell replayed
    ///  the remaining two on <c>--continue</c> and exited 0, with the hand-made commit
    ///  neither repeated nor rewritten. Continue is precisely the command git's own hint
    ///  points at there.</para>
    /// </summary>
    public bool CanContinue => InProgress && !HasUnresolvedConflicts;

    /// <summary>
    ///  <c>--skip</c> is worth offering: there is a recorded series, so skipping the step
    ///  git is stopped on leaves something behind to carry on with.
    ///
    ///  <para><b>Why not for a single commit.</b> Measured on git 2.43: on a one-commit
    ///  revert, <c>--skip</c> exits 0, records nothing and restores a clean work tree —
    ///  which is, to the eye, exactly what <c>--abort</c> does, because there is no earlier
    ///  step for the two to disagree about. Two buttons with different names and one
    ///  outcome is a question the user has to answer without being able to. So the button
    ///  is not offered there; <c>Abort</c> already says what happens, in the words that are
    ///  true in every case.</para>
    ///
    ///  <para><b>Why not without the marker.</b> This is the one button whose rule the
    ///  markerless state really does change, and only measuring showed it. From a sequence
    ///  whose stopped step was committed by hand, git 2.43 refuses: <c>--skip</c> exits
    ///  <b>128</b> with <i>"nothing to skip / have you committed already? try
    ///  --continue"</i>, for both <c>cherry-pick</c> and <c>revert</c>, leaving the session
    ///  exactly as it was. There is nothing to skip because the step git would drop has
    ///  already produced its commit. Offering the button there would be offering a
    ///  guaranteed error.</para>
    /// </summary>
    public bool CanSkip => InProgress && HasStoppedMarker && HasSequence;

    /// <summary>
    ///  <c>--abort</c> can run — for the whole duration. Destructive: the caller confirms
    ///  first. See <see cref="SequencerSessionService.Abort"/> for exactly what it undoes,
    ///  including the one state where it undoes <b>nothing</b> and the confirmation has to
    ///  say so (<see cref="HasStoppedMarker"/> false).
    /// </summary>
    public bool CanAbort => InProgress;

    /// <summary>
    ///  <c>--quit</c> can run — for the whole duration. See
    ///  <see cref="SequencerSessionService.Quit"/> for how it differs from
    ///  <see cref="CanAbort"/>, which is the difference the user must not have to guess.
    /// </summary>
    public bool CanQuit => InProgress;
}

/// <summary>
///  The four commands that end a <c>cherry-pick</c> or a <c>revert</c> that stopped
///  half-way — <c>--continue</c>, <c>--skip</c>, <c>--abort</c> and <c>--quit</c> — plus
///  the structural read of git's sequencer the banner needs to say the truth about them.
///
///  <para><b>Original, not ported.</b> Upstream has no equivalent: its notification bar
///  knows exactly four states (<c>InteractiveGitActionControl.GitAction</c> =
///  <c>Bisect | Rebase | Merge | Patch</c>, <c>:22-30</c>) and a stopped cherry-pick or
///  revert falls through <c>RefreshGitAction</c> (<c>:83-104</c>) into the <c>None</c>
///  branch, which shows the bar <i>only</i> if the index happens to be conflicted and then
///  offers nothing but <c>Resolve...</c>. <c>Commands</c> has no
///  <c>ContinueCherryPick</c>/<c>AbortRevert</c> either — upstream drives these two from
///  <c>FormCherryPick</c>/the revert dialog and never from the bar. So the shape below is
///  taken from <see cref="MergeSessionService"/> and <see cref="RebaseSessionService"/>,
///  but the feature is this port's own.</para>
///
///  <para><b>One service for two operations.</b> See
///  <see cref="SequencerSessionState"/>: git runs both through the same state machine, and
///  the only thing this type does with <see cref="SequencerSessionState.Operation"/> is
///  choose the verb.</para>
///
///  <para><b>Everything is structural, nothing is textual.</b> The state comes from git's
///  own marker files (<c>CHERRY_PICK_HEAD</c>, <c>REVERT_HEAD</c>,
///  <c>sequencer/todo</c>, <c>sequencer/head</c>) and from the index; success is only ever
///  the exit code. Git on this machine is localised — it says
///  <i>"CONFLITTO (contenuto): conflitto di merge in a.txt"</i> — so matching an English
///  message would have been broken on arrival.</para>
///
///  <para><b>Why <c>--continue</c> and <c>--skip</c> get <c>GIT_EDITOR=true</c>.</b> Both
///  end in a commit, so both open <c>core.editor</c>. This is the M183 trap, and this port
///  has paid it twice already (<see cref="MergeSessionService"/>,
///  <see cref="RebaseSessionService"/>): a command that can reach the process dialog's PTY
///  must be explicitly editor-less, or git — believing there is a human at a terminal —
///  starts a full-screen editor inside a text box that is not a terminal, which nobody can
///  read and nobody can close. <b>Measured</b>, with <c>core.editor</c> pointed at a
///  recording script and <c>GIT_EDITOR</c> unset: on a pipe neither command launches it
///  (git skips the edit when stdin is not a tty), but <b>on a pty both do</b> —
///  <c>git revert --continue</c> and <c>git cherry-pick --continue</c> alike, printing
///  "waiting for your editor to close the file". With <c>GIT_EDITOR=true</c> on the same
///  pty the editor is never launched and the commit is still recorded, with git's own
///  prepared message. Whoever wants to write that message by hand amends afterwards from
///  the commit dialog, which is this port's normal way to write one.</para>
///
///  <para>Every command method blocks until git exits: call them from a background task
///  (they are built for <see cref="Views.GitProcessDialog.RunStreamingAsync"/>), never
///  from the UI thread. <see cref="Read"/> is synchronous too, and never throws — it is
///  called from a refresh path.</para>
/// </summary>
public sealed class SequencerSessionService
{
    /// <summary>
    ///  Reads the sequencer state of <paramref name="repoPath"/>. Answers
    ///  <see cref="SequencerSessionState.None"/> for a missing path, a path that is not a
    ///  repository, and for any failure — the safe direction, since it only means the
    ///  banner offers nothing, which is the behaviour the port had before this unit.
    ///
    ///  <para><b>A stopped rebase is not a cherry-pick</b>, even though it looks like one
    ///  from the outside: replaying a step leaves <c>CHERRY_PICK_HEAD</c> behind exactly
    ///  as a real pick does. The rebase directory is therefore checked first, with
    ///  <see cref="GitModule.GetRebaseDir"/> — the same probe
    ///  <see cref="RebaseSessionService.Read"/> trusts, so the two services cannot claim
    ///  the same session — and covers <c>git am</c> too, which owns <c>rebase-apply</c>.
    ///  <see cref="RepositoryStateService.GetProgress"/> orders its own tests the same way
    ///  and for the same reason.</para>
    ///
    ///  <para><b>The markers are not the only proof.</b> They are git's fast path and they
    ///  are gone the moment the stopped step is committed by hand, while the sequence lives
    ///  on; so when neither is there the todo file is asked instead, and what it can no
    ///  longer answer is reported as unknown rather than reconstructed — see
    ///  <see cref="SequencerSessionState.HasStoppedMarker"/>.</para>
    ///
    ///  <para>Everything but the applied-step count is read off the disk, and the one git
    ///  process is only spawned once a session is confirmed, so an idle repository still
    ///  costs nothing.</para>
    /// </summary>
    public SequencerSessionState Read(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return SequencerSessionState.None;
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);

            // GetRebaseDir() answers "" only when none of rebase-merge / rebase-apply /
            // rebase exists. Anything else here belongs to the rebase or to `git am`.
            if (module.GetRebaseDir().Length > 0)
            {
                return SequencerSessionState.None;
            }

            string? gitDir = RepositoryWatcherService.ResolveGitDir(repoPath);
            if (string.IsNullOrEmpty(gitDir) || !Directory.Exists(gitDir))
            {
                return SequencerSessionState.None;
            }

            // Which of the two is stopped is a one-file question, and the file is also
            // where git records the commit it is stopped on.
            string cherryPickHead = Path.Combine(gitDir, "CHERRY_PICK_HEAD");
            string revertHead = Path.Combine(gitDir, "REVERT_HEAD");
            string sequencerDir = Path.Combine(gitDir, "sequencer");
            string todo = Path.Combine(sequencerDir, "todo");

            RepositoryOperation operation;
            string? headMarker;
            if (File.Exists(revertHead))
            {
                operation = RepositoryOperation.Revert;
                headMarker = revertHead;
            }
            else if (File.Exists(cherryPickHead))
            {
                operation = RepositoryOperation.CherryPick;
                headMarker = cherryPickHead;
            }
            else
            {
                // No marker, but possibly still a live sequence: git deletes
                // CHERRY_PICK_HEAD/REVERT_HEAD on *any* commit and does not end the
                // operation with it (sequencer_post_commit_cleanup), so committing the
                // conflicted step from a terminal leaves the todo, and `git status`
                // saying "Cherry-pick in progress", with no marker at all.
                //
                // The same fallback, read the same way, as
                // RepositoryStateService.GetProgress — and literally the same code: this
                // method is where it lives, and that one calls it. Two answers to "is a
                // pick or a revert stopped here?" that could drift apart is a bug waiting
                // to happen, and it would be an ugly one, since the state service decides
                // whether the bar appears while this one decides whether it has buttons:
                // disagreement is exactly the text-only dead end this unit exists to
                // close. The verb of the first todo command is also git's own rule
                // (wt-status.c -> sequencer_get_last_command).
                //
                // Ordering is safe: the rebase directory was refused above, and a
                // `rebase -i` keeps its steps in rebase-merge/git-rebase-todo, never here.
                operation = ReadSequencerOperation(todo);
                if (operation == RepositoryOperation.None)
                {
                    return SequencerSessionState.None;
                }

                headMarker = null;
            }

            return new SequencerSessionState(
                InProgress: true,
                Operation: operation,
                HasUnresolvedConflicts: HasUnresolvedConflicts(repoPath),
                PendingSteps: CountCommands(todo),
                AppliedSteps: CountApplied(module, Path.Combine(sequencerDir, "head")),
                // Only ever the marker's own content. Without it the commit git is stopped
                // on is not recorded anywhere this can read — the todo lists the whole
                // series and does not say which entry is current — so the fact is left out
                // rather than guessed from the first line, which would name a commit that
                // has in fact already been applied.
                StoppedSha: headMarker is null ? null : ReadHash(headMarker),
                SequencerDir: Directory.Exists(sequencerDir) ? sequencerDir : string.Empty,
                HasStoppedMarker: headMarker is not null);
        }
        catch
        {
            return SequencerSessionState.None;
        }
    }

    /// <summary>
    ///  True when the index still has paths the user has to resolve. Separates the two
    ///  states the banner has to tell apart: stopped <i>on a conflict</i> (the work is to
    ///  resolve, and <c>--continue</c> would exit 128) from stopped with a clean index
    ///  (everything is staged and only the commit is missing).
    ///  <para>Delegates to <see cref="WorkingDirectoryService.ListConflicts"/>, the same
    ///  probe <see cref="MergeSessionService.HasUnresolvedConflicts"/> and
    ///  <see cref="RebaseSessionService.HasUnresolvedConflicts"/> use, so every bar of the
    ///  banner agrees on the definition. Returns false rather than throwing, because the
    ///  caller is a refresh path.</para>
    /// </summary>
    public bool HasUnresolvedConflicts(string repoPath)
    {
        try
        {
            return new WorkingDirectoryService().ListConflicts(repoPath).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///  <c>git cherry-pick --continue</c> / <c>git revert --continue</c>: records the
    ///  commit for the step git is stopped on and replays whatever is left of the series.
    ///  Requires a clean index — see <see cref="SequencerSessionState.CanContinue"/>. See
    ///  the class remarks for <c>GIT_EDITOR</c>, which this command is the reason for.
    /// </summary>
    public SequencerCommandResult Continue(string repoPath, SequencerSessionState state, Action<string> emit)
        => Run(repoPath, state, "--continue", emit, GitEditorless);

    /// <summary>
    ///  <c>--skip</c>: drops the step git is stopped on — its commit is never created —
    ///  and carries on with the rest of the series. The steps already applied stay.
    ///  <para>Only offered where it means something; see
    ///  <see cref="SequencerSessionState.CanSkip"/>. Gets <c>GIT_EDITOR=true</c> as well,
    ///  because it does not stop at the skip: it goes on to apply the following steps, any
    ///  of which can end in the same commit-with-an-editor.</para>
    /// </summary>
    public SequencerCommandResult Skip(string repoPath, SequencerSessionState state, Action<string> emit)
        => Run(repoPath, state, "--skip", emit, GitEditorless);

    /// <summary>
    ///  <c>--abort</c>: <b>undoes the whole operation</b>. Measured on git 2.43, mid-way
    ///  through a three-commit cherry-pick with the first commit already created: the
    ///  branch went back to <c>sequencer/head</c>, that commit was gone, the file it had
    ///  added was gone from the work tree, and the index was clean. It restores, in one
    ///  word — including work the user did in the conflict and has not committed.
    ///  Destructive: the caller confirms first.
    ///  <para><b>Except when the marker is gone</b> — the state described in
    ///  <see cref="SequencerSessionState.HasStoppedMarker"/>. Measured on git 2.43, same
    ///  three-commit pick with the stopped step committed by hand: <c>--abort</c> exits 0
    ///  but prints <i>"you seem to have moved HEAD, not rewinding"</i> and leaves the branch,
    ///  the files and the index untouched — it only ends the operation, which makes it
    ///  <see cref="Quit"/> under another name. Git's own refusal to rewind past a commit it
    ///  did not make; nothing here can or should override it, but the confirmation the user
    ///  reads must not promise a restore that will not happen.</para>
    /// </summary>
    public SequencerCommandResult Abort(string repoPath, SequencerSessionState state, Action<string> emit)
        => Run(repoPath, state, "--abort", emit, env: null);

    /// <summary>
    ///  <c>--quit</c>: <b>forgets the operation and changes nothing else</b>. This is the
    ///  one that is easy to confuse with <see cref="Abort"/> and behaves in the opposite
    ///  way. Measured on git 2.43 from the same stopped three-commit cherry-pick: the
    ///  commit already created stayed on the branch, the work tree kept the half-applied
    ///  step <i>with its conflict markers still in the file</i>, the index kept its
    ///  unmerged entry — and only <c>CHERRY_PICK_HEAD</c> and <c>.git/sequencer</c>
    ///  disappeared, so git no longer believed anything was in progress.
    ///  <para>It is the "I will finish this by hand" exit, and it is also the one that can
    ///  leave a repository with an unmerged index and nothing to tell the user why. The
    ///  caller confirms, and the confirmation has to say what stays behind, not just what
    ///  stops.</para>
    /// </summary>
    public SequencerCommandResult Quit(string repoPath, SequencerSessionState state, Action<string> emit)
        => Run(repoPath, state, "--quit", emit, env: null);

    /// <summary>
    ///  How many commits this sequence has created so far: the distance from the head git
    ///  recorded when the sequence started (<c>sequencer/head</c>) to the current
    ///  <c>HEAD</c>. Answers 0 when there is no such file, which is the single-commit case,
    ///  and on any failure — a missing count is rendered as "no step count", never as a
    ///  wrong one.
    /// </summary>
    private static int CountApplied(GitModule module, string headFile)
    {
        try
        {
            if (ReadLine(headFile) is not { Length: > 0 } start)
            {
                return 0;
            }

            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("rev-list") { "--count", $"{start}..HEAD" },
                throwOnErrorExit: false);

            return result.ExitedSuccessfully && int.TryParse(result.StandardOutput.Trim(), out int count)
                ? count
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    // "true" is the shell no-op that exits 0: git treats the message file as accepted and
    // unmodified. GIT_EDITOR wins over core.editor and every EDITOR variable, so this needs
    // no repository configuration — which matters, because the repository is the user's.
    private static readonly IReadOnlyDictionary<string, string?> GitEditorless
        = new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" };

    /// <summary>
    ///  Spells <paramref name="option"/> with the verb the stopped operation actually has.
    ///  A state that is not in progress runs nothing at all and reports a failure rather
    ///  than guessing: sending <c>cherry-pick --abort</c> to a repository that is reverting
    ///  is the one mistake this indirection exists to make impossible.
    /// </summary>
    private static SequencerCommandResult Run(
        string repoPath,
        SequencerSessionState state,
        string option,
        Action<string> emit,
        IReadOnlyDictionary<string, string?>? env)
    {
        if (!state.InProgress || state.Operation == RepositoryOperation.None)
        {
            // Not git's failure and not the user's: a stale click, most likely on a session
            // that ended between the last refresh and the button. Say so, exit code style.
            return new SequencerCommandResult(false, string.Empty);
        }

        string verb = state.IsRevert ? "revert" : "cherry-pick";
        StringBuilder log = new();

        int exit;
        try
        {
            exit = GitStreamRunner.Run(
                repoPath,
                $"{verb} {option}",
                line =>
                {
                    log.AppendLine(line);
                    emit(line);
                },
                env);
        }
        catch (Exception ex)
        {
            // The runner already swallows process failures into a non-zero exit code; this
            // is the belt-and-braces path so a caller on a refresh-adjacent thread can
            // never see an exception.
            log.AppendLine(ex.Message);
            emit(ex.Message);
            exit = -1;
        }

        return new SequencerCommandResult(exit == 0, log.ToString());
    }

    /// <summary>
    ///  Which operation a <c>.git/sequencer/todo</c> describes, taken from the verb of its
    ///  first real command. <see cref="RepositoryOperation.None"/> when the file is absent,
    ///  empty, unreadable or spelled with a verb this cannot vouch for.
    ///
    ///  <para>Git writes exactly two verbs into a sequencer todo — <c>pick</c> (also
    ///  abbreviated <c>p</c>, which is what an edited todo can come back as) and
    ///  <c>revert</c>, which has no abbreviation — the single letter <c>r</c> is
    ///  <c>reword</c>, a rebase-only command. Measured on git 2.43: a markerless sequencer
    ///  whose todo began <c>"r 0feabf2 c3"</c> made <c>git status</c> report no operation at
    ///  all, so mapping <c>r</c> to a revert would have named an operation git does not
    ///  believe in. A todo whose first command is neither is
    ///  not something this port knows how to name, and answering
    ///  <see cref="RepositoryOperation.None"/> merely hides the banner, which is the safe
    ///  direction both callers are built on.</para>
    ///
    ///  <para>It lives here, next to the service that has to spell the commands with that
    ///  verb, and <see cref="RepositoryStateService.GetProgress"/> calls it rather than
    ///  keeping a second copy: the two are answering the same question about the same file,
    ///  and if they ever answered it differently the bar would appear without buttons, or
    ///  worse, send <c>cherry-pick --abort</c> to a repository that is reverting.</para>
    ///
    ///  <para><b>The FIRST line, comments included — because that is git's rule, and it
    ///  was measured.</b> This used to skip blank lines and <c>#</c> comments before
    ///  looking for a verb, on the reasoning that git writes none but an editor can leave
    ///  some. Git does not do that: <c>sequencer_get_last_command</c> truncates the buffer
    ///  at the first newline and parses that one line, and <c>parse_insn_line</c> turns a
    ///  blank or <c>#</c> line into <c>TODO_COMMENT</c>, which is neither <c>TODO_PICK</c>
    ///  nor <c>TODO_REVERT</c>, so the classification simply fails. Measured on git 2.43,
    ///  a markerless sequencer whose todo began with <i>"# hand written"</i> and a blank
    ///  line before two <c>p</c> entries: <c>git status</c> answered <i>"nothing to commit,
    ///  working tree clean"</i> — no operation at all — and <c>git cherry-pick &lt;sha&gt;</c>
    ///  started a brand new pick over it instead of refusing with "a cherry-pick is already
    ///  in progress". Skipping the comment made this port announce a cherry-pick that git
    ///  denies, and offer a <c>Continue</c> that git rejects (<c>--continue</c> there exits
    ///  non-zero with the nonsensical <i>"cannot cherry-pick during a revert"</i>, because
    ///  <c>read_populate_todo</c> demands every parsed entry be the requested command and a
    ///  comment is not). Reading the first line and nothing else is the only rule whose
    ///  answer the user's own <c>git status</c> will corroborate.</para>
    ///
    ///  <para>Leading and trailing whitespace on that line <b>is</b> ignored, which is also
    ///  git's rule (<c>parse_insn_line</c> left-trims before matching): measured, a todo
    ///  reading <c>"   p   0feabf2   c3"</c> is reported as a cherry-pick by
    ///  <c>git status</c> and replayed by <c>--continue</c>.</para>
    /// </summary>
    internal static RepositoryOperation ReadSequencerOperation(string todoPath)
    {
        try
        {
            if (!File.Exists(todoPath))
            {
                return RepositoryOperation.None;
            }

            foreach (string raw in File.ReadLines(todoPath))
            {
                string line = raw.Trim();

                // The verb is the first whitespace-delimited word; everything after it is
                // the commit and its subject, which say nothing about the operation. An
                // empty or commented first line falls through the switch to None, exactly
                // as git's own parse fails there.
                int end = line.IndexOfAny([' ', '\t']);
                string verb = end < 0 ? line : line[..end];

                return verb switch
                {
                    "pick" or "p" => RepositoryOperation.CherryPick,
                    "revert" => RepositoryOperation.Revert,
                    _ => RepositoryOperation.None,
                };
            }

            return RepositoryOperation.None;
        }
        catch
        {
            return RepositoryOperation.None;
        }
    }

    /// <summary>
    ///  Number of real commands in a sequencer todo file; 0 when absent. Blank lines and
    ///  <c>#</c> comments are not commands — git writes none in a todo it generated itself,
    ///  but a todo can also come back from an editor.
    /// </summary>
    private static int CountCommands(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            int count = 0;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length > 0 && line[0] != '#')
                {
                    count++;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///  Reads a full object id marker file and abbreviates it to 8 characters, the width
    ///  the rest of this port shows short hashes at. Left as-is when it does not look like
    ///  a hash, so an unexpected content is visible rather than silently truncated.
    /// </summary>
    private static string? ReadHash(string path)
    {
        string? text = ReadLine(path);
        return text is { Length: >= 40 } && text.All(Uri.IsHexDigit) ? text[..8] : text;
    }

    /// <summary>First line of a marker file, trimmed; null when absent, empty or unreadable.</summary>
    private static string? ReadLine(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text = File.ReadAllText(path).Trim();
            if (text.Length == 0)
            {
                return null;
            }

            int newline = text.IndexOf('\n');
            return newline < 0 ? text : text[..newline].Trim();
        }
        catch
        {
            return null;
        }
    }
}
