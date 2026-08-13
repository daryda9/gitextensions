using System.Text;
using GitCommands;

namespace GitExtensions.Avalonia.Services;

/// <summary>The outcome of one rebase-session command: git's exit code, plus everything it printed.</summary>
/// <param name="Success">True when git exited with code 0. The only success signal used here.</param>
/// <param name="Output">Everything git wrote, in order, for the process dialog and the log.</param>
public sealed record RebaseCommandResult(bool Success, string Output);

/// <summary>
///  Snapshot of the rebase state machine for one repository — the facts upstream
///  <c>FormRebase.EnableButtons()</c> (<c>FormRebase.cs:151-185</c>) and
///  <c>InteractiveGitActionControl.RefreshGitAction</c> (<c>:83-89,142-146</c>) read to
///  decide which of Continue / Skip / Abort / Solve-conflicts are live.
/// </summary>
/// <param name="InProgress">
///  <c>Module.InTheMiddleOfRebase()</c>: a rebase is stopped between steps. This is the
///  master switch — with it false there is nothing to continue, skip or abort.
/// </param>
/// <param name="Interactive">
///  An <b>explicitly interactive</b> rebase — one the user started as <c>rebase -i</c>.
///
///  <para><b>The obvious test is wrong, and this port used to fail it.</b> The
///  <c>interactive</c> marker file is written by the whole <i>merge</i> backend, which
///  since git 2.26 is the default for a plain <c>git rebase</c> as well. Measured on git
///  2.43: a bare <c>git rebase main</c> leaves <c>rebase-merge/interactive</c> behind and
///  makes <c>git status</c> itself announce a "rebase interattivo in corso". So the marker
///  alone means "merge backend", not "the user asked for a todo list", and the banner
///  used to call every stopped rebase interactive because of it.</para>
///
///  <para><b>What actually separates them</b> is the sibling marker
///  <c>drop_redundant_commits</c>, which git writes when it will silently drop commits
///  that became empty. <c>rebase.c</c> chooses that only when the interactive flag was
///  <i>not</i> given explicitly (an explicit <c>-i</c> stops on such a commit instead), so
///  its <b>absence</b> is the recorded trace of <c>-i</c>. Verified on the field, both
///  ways: <c>rebase -i</c> → no such file; <c>rebase main</c> → file present.
///  The one false negative is <c>rebase -i --empty=drop</c>, which asks for both; it costs
///  a wording change and hides the Edit-todo button, i.e. it fails towards offering less,
///  never towards offering a command that would not work.</para>
/// </param>
/// <param name="PendingSteps">
///  Commands still listed in <c>git-rebase-todo</c> — the steps <c>--edit-todo</c> would
///  put in front of the user. 0 for the apply backend, which keeps no todo at all.
/// </param>
/// <param name="DoneSteps">
///  Commands already taken off the todo and recorded in <c>done</c>. Needed as a fact of
///  its own — not just as the step counter — because it answers "is there a previous
///  commit for a <c>squash</c> to meld into?": with nothing replayed yet git refuses a
///  <c>squash</c>/<c>fixup</c> at the head of the list.
/// </param>
/// <param name="HasUnresolvedConflicts">
///  <c>Module.InTheMiddleOfConflictedMerge()</c>: the index still has unmerged entries.
///  Upstream offers <c>Solve conflicts</c> instead of <c>Continue rebase</c> exactly
///  while this holds (<c>FormRebase.cs:166-167</c>), because <c>git rebase --continue</c>
///  refuses to run with an unmerged index.
/// </param>
/// <param name="Step">
///  1-based number of the step git is stopped on, or 0 when it recorded none. See
///  <see cref="ReadCounts"/> for where the two backends keep it.
/// </param>
/// <param name="TotalSteps">Total number of steps in the series, or 0 when unknown.</param>
/// <param name="HeadName">
///  The branch being rebased (<c>head-name</c>), stripped of <c>refs/heads/</c>, or the
///  literal <c>detached HEAD</c> git writes when the rebase started detached.
/// </param>
/// <param name="Onto">The commit the series is being replayed onto (<c>onto</c>), abbreviated.</param>
/// <param name="StoppedSha">
///  The commit git stopped on (<c>stopped-sha</c>), abbreviated — written when a step
///  stops on a conflict, absent for a plain <c>edit</c>/<c>break</c> stop.
/// </param>
/// <param name="RebaseDir">The resolved rebase directory, or "" when there is no session.</param>
public sealed record RebaseSessionState(
    bool InProgress,
    bool Interactive = false,
    bool HasUnresolvedConflicts = false,
    int Step = 0,
    int TotalSteps = 0,
    string? HeadName = null,
    string? Onto = null,
    string? StoppedSha = null,
    string RebaseDir = "",
    int PendingSteps = 0,
    int DoneSteps = 0)
{
    /// <summary>No rebase in progress — also the answer for "no repository" and for every failure.</summary>
    public static RebaseSessionState None { get; } = new(false);

    /// <summary>True when git recorded a usable "step N of M".</summary>
    public bool HasStepCount => Step > 0 && TotalSteps > 0;

    /// <summary>
    ///  <c>git rebase --continue</c> can run: a rebase is stopped and the index has no
    ///  unmerged path left. Upstream's own rule, expressed as an enable instead of the
    ///  visibility swap it uses (<c>FormRebase.cs:166-167</c>).
    /// </summary>
    public bool CanContinue => InProgress && !HasUnresolvedConflicts;

    /// <summary>
    ///  <c>git rebase --skip</c> can run. Upstream shows Skip for the whole duration of a
    ///  rebase, conflicted or not (<c>FormRebase.cs:168</c>): skipping is how you drop a
    ///  step whose conflict you do not want to resolve.
    /// </summary>
    public bool CanSkip => InProgress;

    /// <summary>
    ///  <c>git rebase --abort</c> can run — for the whole duration, like upstream
    ///  (<c>FormRebase.cs:169</c>). Destructive: the caller confirms first.
    /// </summary>
    public bool CanAbort => InProgress;

    /// <summary>
    ///  <c>git rebase --edit-todo</c> has something to show. Three conditions, all
    ///  necessary: a rebase is stopped, it was started as <c>rebase -i</c> (see
    ///  <see cref="Interactive"/> for why the marker file is not enough), and at least one
    ///  step is still pending — an <c>--edit-todo</c> on an exhausted todo would open an
    ///  empty list whose only possible edit is the destructive one.
    ///  <para>Upstream is looser: <c>FormRebase.EnableButtons</c> shows its Edit-todo
    ///  button for <i>any</i> rebase in progress (<c>FormRebase.cs:165</c>), because its
    ///  button only shells out to git and git's own editor tells the user when there is
    ///  nothing to edit. This port opens a window instead, so it has to know beforehand
    ///  that the window would have rows.</para>
    /// </summary>
    public bool CanEditTodo => InProgress && Interactive && PendingSteps > 0;
}

/// <summary>
///  One line of the rebase todo list, as git handed it over.
/// </summary>
/// <param name="Command">
///  The instruction, spelled out in full even when git wrote the one-letter form
///  (<c>p</c> → <c>pick</c>): the list is read by someone deciding what to change, and
///  <c>s</c> versus <c>f</c> is exactly the distinction that must not be a single letter.
/// </param>
/// <param name="Sha">The abbreviated commit id git wrote, or "" for a step that has none.</param>
/// <param name="Subject">The commit subject git wrote after the id, or "".</param>
/// <param name="Raw">
///  The line exactly as git wrote it. This is what gets handed back for anything this port
///  does not model — <c>exec</c>, <c>break</c>, <c>label</c>, <c>reset</c>, <c>merge</c>,
///  <c>update-ref</c>, and a <c>fixup -C</c> with its flag — so a todo written by another
///  tool survives a round trip through this window byte for byte.
/// </param>
public sealed record RebaseTodoStep(string Command, string Sha, string Subject, string Raw)
{
    /// <summary>
    ///  True when this is one of the six commands that operate on a commit and can
    ///  therefore be swapped for one another. Everything else is carried, not edited.
    /// </summary>
    public bool IsCommitStep => Sha.Length > 0 && RebaseTodo.CommitCommands.Contains(Command);

    /// <summary>The line to write back: rebuilt for a commit step, verbatim for the rest.</summary>
    public string ToLine() => IsCommitStep
        ? string.Concat(Command, " ", Sha, Subject.Length > 0 ? " " + Subject : string.Empty)
        : Raw;
}

/// <summary>The pending todo as read back from git, plus what the caller needs to validate an edit.</summary>
/// <param name="Success">False when git refused to open the todo; <paramref name="Output"/> says why.</param>
/// <param name="Steps">The pending steps, in order, comments and blank lines removed.</param>
/// <param name="DoneSteps">Commands already replayed — see <see cref="RebaseSessionState.DoneSteps"/>.</param>
/// <param name="Output">Everything git printed, for the caller to show verbatim on failure.</param>
/// <param name="FromStorage">
///  True when git refused to hand the list over and the steps were read from
///  <c>rebase-merge/git-rebase-todo</c> instead. See
///  <see cref="RebaseSessionService.ReadTodo"/>: this is the state a todo git already
///  rejected leaves behind, and it is precisely the state the user has to be able to
///  repair. <paramref name="Output"/> still carries git's complaint, which the caller
///  should keep on screen — the list being shown is not a list git has accepted.
/// </param>
public sealed record RebaseTodoList(
    bool Success,
    IReadOnlyList<RebaseTodoStep> Steps,
    int DoneSteps,
    string Output,
    bool FromStorage = false);

/// <summary>The vocabulary of the todo list, as facts about git rather than UI strings.</summary>
public static class RebaseTodo
{
    /// <summary>
    ///  The six commands that take a commit, in git's own order of destructiveness — the
    ///  order its todo-file legend lists them in, so a user who has seen a real todo finds
    ///  them where they expect.
    /// </summary>
    public static readonly IReadOnlyList<string> CommitCommands =
        ["pick", "reword", "edit", "squash", "fixup", "drop"];

    // git accepts one-letter aliases in the todo and writes them when
    // rebase.abbreviateCommands is set; both forms have to parse to the same thing.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["p"] = "pick",
        ["r"] = "reword",
        ["e"] = "edit",
        ["s"] = "squash",
        ["f"] = "fixup",
        ["d"] = "drop",
        ["x"] = "exec",
        ["b"] = "break",
        ["l"] = "label",
        ["t"] = "reset",
        ["m"] = "merge",
        ["u"] = "update-ref",
    };

    /// <summary>Expands git's one-letter command aliases; anything else is returned unchanged.</summary>
    public static string Expand(string command)
        => Aliases.TryGetValue(command, out string? full) ? full : command;
}

/// <summary>
///  The three commands that end a rebase that stopped half-way —
///  <c>git rebase --continue</c>, <c>--skip</c> and <c>--abort</c> — plus the structural
///  read of the rebase state the banner needs to say the truth about it.
///
///  <para><b>What it ports, and what it unblocks.</b> Upstream drives these three from
///  two places: the notification bar
///  (<c>InteractiveGitActionControl.cs:142-146</c> → <c>Commands.ContinueRebase()</c> at
///  <c>:191</c> and <c>Commands.AbortRebase()</c> at <c>:216</c>) and <c>FormRebase</c>
///  (<c>FormRebase.cs:247,270,287</c>). This port had <i>none</i> of them behind an API,
///  which is why round 12 (M72) recorded the rebase as an explicit residue: the banner
///  could recognise a stopped rebase but could only print the command for the user to
///  type in a terminal (<c>RepositoryProgressBanner.HintFor</c>), and the rebase
///  call-sites deliberately did not even <i>ask</i> "solve conflicts now?", because a
///  yes would have left the user mid-rebase with no way to finish it. This type is the
///  missing half.</para>
///
///  <para><b>Everything is structural, nothing is textual.</b> The state comes from the
///  marker files git keeps in <c>rebase-merge</c>/<c>rebase-apply</c> and from
///  <c>git ls-files --unmerged</c> — never from git's messages. Git on this machine is
///  localised (it says <i>"Attualmente stai modificando un commit durante il rebase…"</i>),
///  so message matching would be broken on arrival. Success is likewise only ever the
///  exit code.</para>
///
///  <para><b>Why <c>--continue</c> gets <c>GIT_EDITOR=true</c>.</b> Same trap
///  <see cref="MergeSessionService"/> pays for, and worse here: <c>git rebase --continue</c>
///  opens an editor whenever the step it is finishing needs a commit message — which is
///  <i>every</i> stop on an interactive <c>edit</c>, and every conflicted <c>squash</c>.
///  Upstream can afford it because it points git's editor at itself; this port has no
///  editor wired to git, so the inherited <c>vi</c> would hang the process dialog for
///  ever with no visible prompt and no way out but killing git mid-rebase.
///  <c>GIT_EDITOR=true</c> accepts git's own prepared message unchanged — the same commit
///  the user gets by saving an untouched editor. Anyone who wants to write the message by
///  hand amends afterwards from the commit dialog, which is this port's normal way to
///  write a message. Pinned on <c>--skip</c> too, which continues the series and can hit
///  the same prompt on a later step.</para>
///
///  <para>Every command method blocks until git exits: call them from a background task
///  (they are built for <see cref="Views.GitProcessDialog.RunStreamingAsync"/>), never
///  from the UI thread. <see cref="Read"/> is disk-and-one-process cheap but is also
///  synchronous, and never throws — it is called from a refresh path.</para>
///
///  <para><b>Editing the todo</b> (<c>git rebase --edit-todo</c>,
///  <c>Commands.EditTodoRebase()</c>, <c>FormRebase.cs:304</c>) is <see cref="ReadTodo"/>
///  plus <see cref="WriteTodo"/>, and both go through a <b>scripted
///  <c>GIT_SEQUENCE_EDITOR</c></b> — the shape <see cref="CommitEditService"/> already uses
///  for its autosquash rebases. Upstream can hand the raw command to
///  <c>FormProcess</c> because on Windows it has git's editor pointed back at itself; here
///  the same command on the process dialog's PTY would launch a full-screen editor inside a
///  text box that is not a terminal — the exact defect M183 fixed, whose rule is that a
///  command reaching the PTY must be explicitly editor-less.</para>
///
///  <para>The pair is deliberately a <i>round trip through git</i> rather than a read and a
///  write of <c>rebase-merge/git-rebase-todo</c>: git is the one that prints the list (with
///  the abbreviation and the subject its own configuration asks for), and git is the one
///  that parses, validates and installs the edited text. Writing the file behind git's back
///  would make this port the validator of a format it does not own.</para>
/// </summary>
public sealed class RebaseSessionService
{
    /// <summary>
    ///  Reads the rebase state of <paramref name="repoPath"/>. Answers
    ///  <see cref="RebaseSessionState.None"/> for a missing path, a path that is not a
    ///  repository, and for any failure — the safe direction, since it only means the
    ///  banner offers nothing, which is the behaviour the port had before this unit.
    ///
    ///  <para>The rebase directory is located with <see cref="GitModule.GetRebaseDir"/>
    ///  and the "is a rebase stopped" question asked with
    ///  <see cref="GitModule.InTheMiddleOfRebase"/>, so this port and upstream cannot
    ///  drift apart on the definition. The index is only inspected when a session is
    ///  actually open, so an idle repository still costs no git process.</para>
    /// </summary>
    public RebaseSessionState Read(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return RebaseSessionState.None;
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);

            // GetRebaseDir() returns "" when none of rebase-merge / rebase-apply /
            // rebase exists, and InTheMiddleOfRebase() additionally excludes the `git am`
            // case (rebase-apply with an "applying" marker), which has its own service.
            string dir = module.GetRebaseDir();
            if (dir.Length == 0 || !module.InTheMiddleOfRebase())
            {
                return RebaseSessionState.None;
            }

            (int step, int total) = ReadCounts(dir);

            return new RebaseSessionState(
                InProgress: true,

                // NOT just the "interactive" marker — see RebaseSessionState.Interactive
                // for the measurement that shows the whole merge backend writes it.
                Interactive: File.Exists(Path.Combine(dir, "interactive"))
                    && !File.Exists(Path.Combine(dir, "drop_redundant_commits")),
                HasUnresolvedConflicts: HasUnresolvedConflicts(repoPath),
                Step: step,
                TotalSteps: total,
                HeadName: ReadRefName(Path.Combine(dir, "head-name")),
                Onto: ReadHash(Path.Combine(dir, "onto")),
                StoppedSha: ReadHash(Path.Combine(dir, "stopped-sha")),
                RebaseDir: dir,
                PendingSteps: CountCommands(Path.Combine(dir, "git-rebase-todo")),
                DoneSteps: CountCommands(Path.Combine(dir, "done")));
        }
        catch
        {
            return RebaseSessionState.None;
        }
    }

    /// <summary>
    ///  True when the index still has paths the user has to resolve. This is what
    ///  separates a rebase stopped on a <i>conflict</i> (Continue is impossible until the
    ///  index is clean) from a rebase stopped on purpose — an interactive <c>edit</c> or
    ///  <c>break</c> — where the index is clean and Continue is the only thing needed.
    ///  <para>Delegates to <see cref="WorkingDirectoryService.ListConflicts"/>, the same
    ///  probe <see cref="MergeSessionService.HasUnresolvedConflicts"/> uses, so both bars
    ///  of the banner agree on the definition. Returns false rather than throwing,
    ///  because the caller is a refresh path.</para>
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
    ///  <c>git rebase --continue</c>: commits the step the rebase is stopped on and
    ///  replays the rest of the series. Requires a clean index — see
    ///  <see cref="RebaseSessionState.CanContinue"/>. See the class remarks for
    ///  <c>GIT_EDITOR</c>.
    /// </summary>
    public RebaseCommandResult Continue(string repoPath, Action<string> emit)
        => Run(repoPath, "rebase --continue", emit, GitEditorless);

    /// <summary>
    ///  <c>git rebase --skip</c>: throws away the step the rebase is stopped on — its
    ///  changes do not reach the rebased branch — and replays the rest.
    /// </summary>
    public RebaseCommandResult Skip(string repoPath, Action<string> emit)
        => Run(repoPath, "rebase --skip", emit, GitEditorless);

    /// <summary>
    ///  <c>git rebase --abort</c>: throws the whole rebase away and puts the branch and
    ///  the working tree back on the original head. Destructive — the caller confirms
    ///  first.
    /// </summary>
    public RebaseCommandResult Abort(string repoPath, Action<string> emit)
        => Run(repoPath, "rebase --abort", emit, env: null);

    /// <summary>
    ///  Asks git for the steps still to be replayed, by running
    ///  <c>git rebase --edit-todo</c> with a sequence editor that <b>copies the todo out
    ///  and changes nothing</b>. git then re-installs the very text it produced, so the
    ///  read is a no-op on the repository — verified on the field: the todo file is
    ///  byte-identical afterwards, and a <c>--continue</c> behaves as if nothing happened.
    ///
    ///  <para><b>Why not just read <c>rebase-merge/git-rebase-todo</c>.</b> That file is
    ///  git's storage, not its presentation: it holds full 40-character ids and none of the
    ///  legend. What git hands the sequence editor is the list as git means it to be edited
    ///  — abbreviated to the width the repository configures, subject attached, and in the
    ///  exact form it will accept back. Taking the presentation from git is what lets
    ///  <see cref="WriteTodo"/> hand the text back without this port ever having to know the
    ///  format.</para>
    ///
    ///  <para>Blocks; call from a background task. Never throws — a failure comes back as
    ///  <see cref="RebaseTodoList.Success"/> false with git's own output, which the caller
    ///  is expected to show verbatim rather than paraphrase.</para>
    /// </summary>
    public RebaseTodoList ReadTodo(string repoPath)
    {
        string capture = Path.Combine(Path.GetTempPath(), "gex-todo-" + Guid.NewGuid().ToString("N"));

        // The editor git invokes gets the todo path as $1. Copying it out and exiting 0
        // means "I accepted the list unchanged".
        string script = WriteSequenceEditor($"cat \"$1\" > \"{capture}\"\n");

        try
        {
            // Nothing listens to the lines: the read is silent by design — the user asked
            // for a window, not for a console. Only a failure's text is kept, in Output.
            RebaseCommandResult result = Run(repoPath, "rebase --edit-todo", _ => { }, EditorEnv(script));

            if (!result.Success || !File.Exists(capture))
            {
                // git would not even OPEN the list. Measured cause, and the only one that
                // matters: the session's todo is already invalid — a `rebase -i` whose very
                // first command was a squash exits with "cannot 'squash' without a previous
                // commit" and leaves that todo on disk. git parses the old list before
                // running the editor, so the round trip cannot get past it.
                //
                // That is exactly the state git tells the user to fix WITH --edit-todo, so
                // refusing to show anything here would leave the one dead end this window
                // exists to open. Fall back to git's own file: the list is still git's, only
                // unabbreviated, and the WRITE still goes through git — verified on the
                // field, installing a valid list over an invalid one succeeds (git prints
                // the old list's error and exits 0).
                string stored = StoredTodo(repoPath);
                return File.Exists(stored)
                    ? new RebaseTodoList(true, Parse(File.ReadAllLines(stored)), DoneCount(repoPath), result.Output, FromStorage: true)
                    : new RebaseTodoList(false, [], 0, result.Output);
            }

            return new RebaseTodoList(true, Parse(File.ReadAllLines(capture)), DoneCount(repoPath), result.Output);
        }
        catch (Exception ex)
        {
            return new RebaseTodoList(false, [], 0, ex.Message);
        }
        finally
        {
            TryDelete(script);
            TryDelete(capture);
        }
    }

    /// <summary>
    ///  Hands <paramref name="steps"/> back to git as the new todo, through the same
    ///  <c>--edit-todo</c> round trip: the scripted sequence editor overwrites git's buffer
    ///  with the rendered list and exits 0, and <b>git</b> parses it, checks it and installs
    ///  it. A list git rejects (a <c>squash</c> with nothing before it, an unknown command)
    ///  leaves the session's real todo untouched and comes back as a failure carrying git's
    ///  own message — which is the one to show, in git's own language.
    ///
    ///  <para>What this does <i>not</i> do is any part of the rebase: the steps are queued,
    ///  not run. <c>Continue</c> is still what replays them.</para>
    ///
    ///  <para><b>An empty list is a real instruction, not an error</b>, and a costly one:
    ///  measured on git 2.43, <c>--edit-todo</c> accepts it and the next
    ///  <c>--continue</c> ends the rebase there, leaving the branch at the commit it had
    ///  reached and every remaining commit off it (reachable only through the reflog). The
    ///  caller confirms; this method obeys.</para>
    /// </summary>
    public RebaseCommandResult WriteTodo(string repoPath, IReadOnlyList<RebaseTodoStep> steps, Action<string> emit)
    {
        string file = Path.Combine(Path.GetTempPath(), "gex-todo-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Trailing newline on the last line: git's parser is forgiving about it, but a
            // todo is a line-oriented file and every git-written one ends with it.
            string text = steps.Count == 0
                ? string.Empty
                : string.Join("\n", steps.Select(step => step.ToLine())) + "\n";
            File.WriteAllText(file, text);

            // Overwrite in place ("> $1") rather than replace the file: git opened that
            // path itself and re-reads it, so its identity and mode must survive.
            string script = WriteSequenceEditor($"cat \"{file}\" > \"$1\"\n");
            try
            {
                return Run(repoPath, "rebase --edit-todo", emit, EditorEnv(script));
            }
            finally
            {
                TryDelete(script);
            }
        }
        catch (Exception ex)
        {
            emit(ex.Message);
            return new RebaseCommandResult(false, ex.Message);
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>
    ///  Turns git's todo text into steps. Comments and blank lines are dropped: they are
    ///  git's legend, regenerated on every write, and carrying them back would duplicate
    ///  them. Anything that is not one of the six commit commands keeps its whole line and
    ///  travels through unchanged.
    /// </summary>
    private static List<RebaseTodoStep> Parse(IEnumerable<string> lines)
    {
        List<RebaseTodoStep> steps = [];

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            string[] parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string command = RebaseTodo.Expand(parts[0]);

            // Only the plain "<command> <sha> <subject>" shape is modelled. A flagged form
            // (fixup -C, merge -c) starts its second token with '-' and is carried raw,
            // because rebuilding it would mean owning git's option grammar too.
            bool commitShape = parts.Length >= 2
                && RebaseTodo.CommitCommands.Contains(command)
                && parts[1].Length > 0
                && parts[1][0] != '-';

            steps.Add(commitShape
                ? new RebaseTodoStep(command, parts[1], parts.Length > 2 ? parts[2] : string.Empty, line)
                : new RebaseTodoStep(command, string.Empty, string.Empty, line));
        }

        return steps;
    }

    /// <summary>
    ///  How many commands the session has already replayed. Read on its own (rather than
    ///  taken from <see cref="Read"/>) so the todo window can validate a <c>squash</c> at
    ///  the head of the list against the state git will see, without a second index probe.
    /// </summary>
    private static int DoneCount(string repoPath)
    {
        string dir = RebaseDirOf(repoPath);
        return dir.Length == 0 ? 0 : CountCommands(Path.Combine(dir, "done"));
    }

    /// <summary>git's own todo file — the fallback source, never the one written to.</summary>
    private static string StoredTodo(string repoPath)
    {
        string dir = RebaseDirOf(repoPath);
        return dir.Length == 0 ? string.Empty : Path.Combine(dir, "git-rebase-todo");
    }

    private static string RebaseDirOf(string repoPath)
    {
        try
        {
            return GitContext.CreateModule(repoPath).GetRebaseDir();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///  Writes a throw-away <c>GIT_SEQUENCE_EDITOR</c> script around <paramref name="body"/>,
    ///  which receives the todo path as <c>$1</c>. Same device — and same reasoning — as
    ///  <c>CommitEditService.WriteSequenceEditor</c>; it is duplicated rather than shared
    ///  because the two services script different halves of git and have no other coupling.
    ///  <para>The paths interpolated into the body are this method's own temp names
    ///  (<c>gex-todo-</c> + a GUID under <see cref="Path.GetTempPath"/>), so they carry no
    ///  quote, space or shell metacharacter; nothing user-supplied ever reaches the
    ///  script.</para>
    /// </summary>
    private static string WriteSequenceEditor(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), "gex-seqtodo-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup; a leftover script is harmless.
        }
    }

    /// <summary>
    ///  The environment for an <c>--edit-todo</c>: the scripted sequence editor, plus the
    ///  no-op message editor of <see cref="GitEditorless"/>. The second is belt and braces —
    ///  <c>--edit-todo</c> has no message to write — but this is the command that most
    ///  invites a stray editor, and the M183 rule is that nothing reaching the process
    ///  surface may be able to open one.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> EditorEnv(string sequenceEditor)
        => new Dictionary<string, string?>
        {
            ["GIT_SEQUENCE_EDITOR"] = sequenceEditor,
            ["GIT_EDITOR"] = "true",
        };

    // "true" is the shell no-op that exits 0: git treats the message file as accepted
    // and unmodified. GIT_EDITOR wins over core.editor, GIT_SEQUENCE_EDITOR's siblings
    // and every EDITOR variable, so this needs no repository configuration.
    private static readonly IReadOnlyDictionary<string, string?> GitEditorless
        = new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" };

    /// <summary>
    ///  "Step N of M" for whichever backend is running.
    ///  <list type="bullet">
    ///   <item>The <b>interactive</b>/merge backend keeps the executed commands in
    ///    <c>done</c> and the pending ones in <c>git-rebase-todo</c>, which is how git's
    ///    own <c>wt-status.c</c> counts an interactive rebase: the step is the number of
    ///    commands already taken off the todo, the total is that plus what is left. This
    ///    is preferred because <c>msgnum</c>/<c>end</c> count only the <i>picks</i> and
    ///    are not rewritten for a stop on an <c>edit</c>.</item>
    ///   <item>Otherwise <c>msgnum</c>/<c>end</c> (merge backend) or <c>next</c>/<c>last</c>
    ///    (the apply backend, shared with <c>git am</c>).</item>
    ///  </list>
    ///  Blank lines and <c>#</c> comments are not commands and are not counted. Any
    ///  unreadable file yields 0, which the banner renders as "no step count" rather than
    ///  a wrong one.
    /// </summary>
    private static (int Step, int Total) ReadCounts(string dir)
    {
        int done = CountCommands(Path.Combine(dir, "done"));
        if (done > 0)
        {
            int todo = CountCommands(Path.Combine(dir, "git-rebase-todo"));
            return (done, done + todo);
        }

        int step = ReadNumber(Path.Combine(dir, "msgnum"));
        int total = ReadNumber(Path.Combine(dir, "end"));
        if (step > 0 && total > 0)
        {
            return (step, total);
        }

        return (ReadNumber(Path.Combine(dir, "next")), ReadNumber(Path.Combine(dir, "last")));
    }

    /// <summary>Number of real todo commands in a <c>done</c>/<c>git-rebase-todo</c> file; 0 when absent.</summary>
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

    /// <summary>Reads a small integer marker file; 0 when absent or unreadable.</summary>
    private static int ReadNumber(string path)
    {
        try
        {
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int value)
                ? value
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///  Reads a ref-name marker file and shortens it for display. Git writes
    ///  <c>refs/heads/topic</c>, or the literal <c>detached HEAD</c> when the rebase
    ///  started from a detached head.
    /// </summary>
    private static string? ReadRefName(string path)
    {
        string? text = ReadLine(path);
        const string prefix = "refs/heads/";
        return text is not null && text.StartsWith(prefix, StringComparison.Ordinal)
            ? text[prefix.Length..]
            : text;
    }

    /// <summary>
    ///  Reads a full object id marker file and abbreviates it to 8 characters, the width
    ///  the rest of this port shows short hashes at. Left as-is when it does not look like
    ///  a hash, so an unexpected file content is visible rather than silently truncated.
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

    private static RebaseCommandResult Run(
        string repoPath,
        string arguments,
        Action<string> emit,
        IReadOnlyDictionary<string, string?>? env)
    {
        StringBuilder log = new();

        int exit;
        try
        {
            exit = GitStreamRunner.Run(
                repoPath,
                arguments,
                line =>
                {
                    log.AppendLine(line);
                    emit(line);
                },
                env);
        }
        catch (Exception ex)
        {
            // The runner already swallows process failures into a non-zero exit code;
            // this is the belt-and-braces path so a caller on a refresh-adjacent thread
            // can never see an exception.
            log.AppendLine(ex.Message);
            emit(ex.Message);
            exit = -1;
        }

        return new RebaseCommandResult(exit == 0, log.ToString());
    }
}
