using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>The outcome of one merge-session command: git's exit code, plus everything it printed.</summary>
/// <param name="Success">True when git exited with code 0. The only success signal used here.</param>
/// <param name="Output">Everything git wrote, in order, for the process dialog and the log.</param>
public sealed record MergeCommandResult(bool Success, string Output)
{
    /// <summary>
    ///  Set when git stopped because it wanted the merge message written, and never
    ///  together with <see cref="Success"/>. The caller asks the user and answers with
    ///  <see cref="MergeSessionService.ContinueWithMessage"/>.
    /// </summary>
    public MergeMessageRequest? Pending { get; init; }
}

/// <summary>
///  Git is waiting for the message of the merge commit it is about to make.
/// </summary>
/// <param name="Template">
///  What git itself prepared — "Merge branch 'x'", or whatever <c>MERGE_MSG</c> holds
///  after a <c>--no-commit</c> merge the user has been editing — with git's own comment
///  legend already removed. It is the prefill of the box, so that saving unchanged
///  produces exactly the commit git would have made on its own.
/// </param>
public sealed record MergeMessageRequest(string Template);

/// <summary>
///  The two commands that end a merge that stopped half-way — <c>git merge --abort</c>
///  and <c>git merge --continue</c> — plus the "are there still unresolved conflicts"
///  probe the banner needs to tell upstream's two merge states apart.
///
///  <para><b>What it ports.</b> Upstream's notification bar runs exactly these two
///  through <c>FormProcess</c>: <c>InteractiveGitActionControl.cs:196</c>
///  (<c>Commands.ContinueMerge()</c>) and <c>:221</c> (<c>Commands.AbortMerge()</c>),
///  choosing between "resolve" and "continue" from
///  <c>Module.InTheMiddleOfConflictedMerge()</c> (<c>:82</c>). This port had neither
///  command behind an API: the only <c>--abort</c> calls in <c>App/Services</c> were
///  private clean-up paths inside <c>CommitEditService</c> and <c>PatchService</c>, so
///  <see cref="Views.RepositoryProgressBanner"/> could only print the command for the
///  user to type in a terminal.</para>
///
///  <para><b>Success is structural, never textual.</b> Only the exit code decides, and
///  the conflict probe reads the index through
///  <c>git diff --name-only --diff-filter=U</c>. Nothing here matches an English
///  message: git on this machine is localised, so message matching would silently stop
///  working.</para>
///
///  <para><b>How <c>--continue</c> gets its message.</b>
///  <c>git merge --continue</c> opens an editor on the prepared <c>MERGE_MSG</c>, and
///  this port has no editor wired to git: an inherited <c>vi</c> would hang the process
///  dialog forever with no visible prompt. Until M213 the answer was
///  <c>GIT_EDITOR=true</c>, which accepts git's prepared message unchanged — safe, but
///  it silently took the choice away: the one commit of a merge, the place where a
///  reviewer looks for <i>why</i> the two branches came together and how the conflicts
///  were settled, could not be described from this app at all.</para>
///
///  <para>So it now does what the rebase does since M205 (see
///  <c>RebaseSessionService</c>): the editor <b>refuses</b>, git's prepared text is
///  captured, and the caller shows it in a box. Answering finishes the merge with that
///  message; cancelling leaves the merge exactly as it was, with Continue, Abort and the
///  banner all still live — which is why the refusing editor is safe to use here at all,
///  measured: with the editor exiting 1, <c>MERGE_HEAD</c> survived and the index kept
///  the staged resolutions.</para>
///
///  <para>Every method blocks until git exits: call them from a background task (they
///  are built for <see cref="Views.GitProcessDialog.RunStreamingAsync"/>), never from
///  the UI thread.</para>
/// </summary>
public sealed class MergeSessionService
{
    /// <summary>
    ///  True when the merge still has paths the user has to resolve, i.e. the index has
    ///  unmerged entries. This is what separates upstream's "… in progress with merge
    ///  conflicts." from its plain "… in progress." (there, the merge is done and only
    ///  the commit is missing).
    ///  <para>Delegates to <see cref="WorkingDirectoryService.ListConflicts"/>, which
    ///  already runs <c>git diff --name-only --diff-filter=U</c> with
    ///  <c>throwOnErrorExit: false</c>. Returns false rather than throwing, because the
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
    ///  <c>git merge --abort</c>: throws the merge away and puts the working tree back
    ///  where it was before it started. Destructive — the caller confirms first.
    /// </summary>
    public MergeCommandResult Abort(string repoPath, Action<string> emit)
        => Run(repoPath, "merge --abort", emit, env: null);

    /// <summary>
    ///  <c>git merge --continue</c>: records the merge commit once every conflict is
    ///  resolved and staged.
    ///
    ///  <para>Comes back with <see cref="MergeCommandResult.Pending"/> rather than a
    ///  success, because git asks for the message first: see the class remarks, and
    ///  answer with <see cref="ContinueWithMessage"/>.</para>
    /// </summary>
    public MergeCommandResult Continue(string repoPath, Action<string> emit)
    {
        string capture = Path.Combine(Path.GetTempPath(), "gex-mergemsg-" + Guid.NewGuid().ToString("N"));
        string script = string.Empty;

        try
        {
            // The body is a constant and the capture path travels in the environment, so
            // no path is ever interpolated into shell text. `exit 1` is what makes git
            // treat the message as unwritten: it stops, keeps MERGE_HEAD and leaves the
            // index alone (measured on git 2.43).
            script = GitScriptedEditor.WriteScript(
                "git stripspace --strip-comments < \"$1\" > \"$GEX_MERGE_CAPTURE\"\n" +
                "exit 1\n");

            MergeCommandResult result = Run(
                repoPath,
                "merge --continue",
                emit,
                new Dictionary<string, string?>
                {
                    ["GIT_EDITOR"] = GitScriptedEditor.Quote(script),
                    ["GEX_MERGE_CAPTURE"] = capture,
                });

            // The capture file exists only if git actually opened the message editor, and
            // a success cannot have gone through it (the script always exits 1) — so the
            // two conditions together are the exact signal "git is waiting for a message".
            // A --continue that fails for any other reason (unresolved paths still in the
            // index, no merge in progress) never reaches the editor and is reported as the
            // plain failure it is.
            if (result.Success || !File.Exists(capture))
            {
                return result;
            }

            return result with { Pending = new MergeMessageRequest(File.ReadAllText(capture)) };
        }
        catch (Exception ex)
        {
            emit(ex.Message);
            return new MergeCommandResult(false, ex.Message);
        }
        finally
        {
            GitScriptedEditor.TryDelete(capture);
            GitScriptedEditor.TryDelete(script);
        }
    }

    /// <summary>
    ///  Records the merge commit with <paramref name="message"/> — the answer to a
    ///  <see cref="MergeCommandResult.Pending"/> request.
    ///
    ///  <para><c>git commit</c> rather than a second <c>merge --continue</c>, because with
    ///  <c>MERGE_HEAD</c> present the two are the same command: measured, the commit has
    ///  both parents, <c>MERGE_HEAD</c> is gone afterwards and the branch is merged. The
    ///  plain form is preferred for what it can be given —
    ///  <c>--cleanup=whitespace</c>.</para>
    ///
    ///  <para><b>The message never touches a command line or a script body.</b> It is
    ///  written to a temp file whose path travels in the <i>environment</i> to a scripted
    ///  <c>GIT_EDITOR</c> that copies it over git's buffer, so a message full of quotes,
    ///  newlines and non-ASCII — and a temp directory with spaces in its name — are all
    ///  just bytes. UTF-8 without BOM, which is what git reads.</para>
    ///
    ///  <para><c>--cleanup=whitespace</c> where git's own editor path would use
    ///  <c>strip</c>: git strips <c>#</c> lines because <i>its</i> buffer is full of its
    ///  own legend, and that legend was already removed before the text was shown.
    ///  Everything in the box was typed by the user, so a line they began with <c>#</c>
    ///  ("#1234 merged for the release") is content and must survive.</para>
    /// </summary>
    public MergeCommandResult ContinueWithMessage(string repoPath, string message, Action<string> emit)
    {
        string file = Path.Combine(Path.GetTempPath(), "gex-mergemsg-" + Guid.NewGuid().ToString("N"));
        string script = string.Empty;

        try
        {
            File.WriteAllText(file, message);
            script = GitScriptedEditor.WriteScript("cat \"$GEX_MERGE_MESSAGE\" > \"$1\"\n");

            return Run(
                repoPath,
                "commit --cleanup=whitespace",
                emit,
                new Dictionary<string, string?>
                {
                    ["GIT_EDITOR"] = GitScriptedEditor.Quote(script),
                    ["GEX_MERGE_MESSAGE"] = file,
                });
        }
        catch (Exception ex)
        {
            emit(ex.Message);
            return new MergeCommandResult(false, ex.Message);
        }
        finally
        {
            GitScriptedEditor.TryDelete(file);
            GitScriptedEditor.TryDelete(script);
        }
    }

    private static MergeCommandResult Run(
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

        return new MergeCommandResult(exit == 0, log.ToString());
    }
}
