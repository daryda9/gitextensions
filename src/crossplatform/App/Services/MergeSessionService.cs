using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>The outcome of one merge-session command: git's exit code, plus everything it printed.</summary>
/// <param name="Success">True when git exited with code 0. The only success signal used here.</param>
/// <param name="Output">Everything git wrote, in order, for the process dialog and the log.</param>
public sealed record MergeCommandResult(bool Success, string Output);

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
///  <para><b>Why <c>--continue</c> gets <c>GIT_EDITOR=true</c>.</b>
///  <c>git merge --continue</c> opens an editor on the prepared <c>MERGE_MSG</c>.
///  Upstream can afford that because it points git's editor at itself; this port has no
///  editor wired to git, so an inherited <c>vi</c> would hang the process dialog
///  forever with no visible prompt. <c>GIT_EDITOR=true</c> accepts git's own prepared
///  merge message unchanged — the same commit the user would get by saving the editor
///  without touching it. Anyone who wants to write the message by hand still has the
///  commit dialog, which is the port's normal way to finish a resolved merge.</para>
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
    ///  resolved and staged. See the class remarks for <c>GIT_EDITOR</c>.
    /// </summary>
    public MergeCommandResult Continue(string repoPath, Action<string> emit)
        => Run(repoPath, "merge --continue", emit, GitEditorless);

    // "true" is the shell no-op that exits 0: git treats the message file as accepted
    // and unmodified. GIT_EDITOR wins over core.editor and every EDITOR variable, so
    // this needs no repository configuration.
    private static readonly IReadOnlyDictionary<string, string?> GitEditorless
        = new Dictionary<string, string?> { ["GIT_EDITOR"] = "true" };

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
