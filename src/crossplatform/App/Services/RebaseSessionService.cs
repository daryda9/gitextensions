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
///  A <c>rebase -i</c>, i.e. the <c>interactive</c> marker exists in the rebase
///  directory. Only affects wording: the commands are identical either way.
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
    string RebaseDir = "")
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
///  <para><b>Not here on purpose:</b> <c>git rebase --edit-todo</c>
///  (<c>Commands.EditTodoRebase()</c>, <c>FormRebase.cs:304</c>). Upstream backs it with a
///  grid that reorders, drops and squashes the pending commits; that is a subsystem of its
///  own, not a button, and shipping the raw command with no editor behind it would hit the
///  very <c>GIT_EDITOR</c> hang described above. See <c>NOTES.md</c>.</para>
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
                Interactive: File.Exists(Path.Combine(dir, "interactive")),
                HasUnresolvedConflicts: HasUnresolvedConflicts(repoPath),
                Step: step,
                TotalSteps: total,
                HeadName: ReadRefName(Path.Combine(dir, "head-name")),
                Onto: ReadHash(Path.Combine(dir, "onto")),
                StoppedSha: ReadHash(Path.Combine(dir, "stopped-sha")),
                RebaseDir: dir);
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
