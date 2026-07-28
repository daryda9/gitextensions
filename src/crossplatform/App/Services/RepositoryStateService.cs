using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The multi-step git operation a repository is stopped in the middle of.
///  <see cref="None"/> means the work tree is in its ordinary state.
/// </summary>
public enum RepositoryOperation
{
    /// <summary>No multi-step operation is in progress.</summary>
    None,

    /// <summary>A merge stopped before committing — <c>MERGE_HEAD</c> is present.</summary>
    Merge,

    /// <summary>A <c>rebase</c> stopped between steps.</summary>
    Rebase,

    /// <summary>A <c>rebase -i</c> stopped between steps.</summary>
    RebaseInteractive,

    /// <summary>A <c>git am</c> patch series stopped on a patch.</summary>
    ApplyMailbox,

    /// <summary>A cherry-pick stopped — <c>CHERRY_PICK_HEAD</c> is present.</summary>
    CherryPick,

    /// <summary>A revert stopped — <c>REVERT_HEAD</c> is present.</summary>
    Revert,
}

/// <summary>
///  What the repository is in the middle of, as the banner needs to say it.
/// </summary>
/// <param name="Operation">The stopped operation, or <see cref="RepositoryOperation.None"/>.</param>
/// <param name="BisectInProgress">
///  Whether a bisect session is open. Independent of <paramref name="Operation"/>:
///  upstream shows the bisect bar and the git-action bar separately, and a bisect
///  can perfectly well be running while a cherry-pick is stopped.
/// </param>
/// <param name="Step">Step number within the operation, when git records one.</param>
/// <param name="TotalSteps">Total steps, when git records one.</param>
/// <param name="Target">
///  The branch or ref the operation is working on, when git records one
///  (<c>rebase-merge/head-name</c>), already stripped of its <c>refs/heads/</c> prefix.
/// </param>
public sealed record RepositoryProgress(
    RepositoryOperation Operation,
    bool BisectInProgress,
    int Step = 0,
    int TotalSteps = 0,
    string? Target = null)
{
    /// <summary>Nothing in progress — the value used for "no repository" too.</summary>
    public static readonly RepositoryProgress None = new(RepositoryOperation.None, false);

    /// <summary>True when there is anything at all to tell the user about.</summary>
    public bool IsActive => Operation != RepositoryOperation.None || BisectInProgress;

    /// <summary>True when git recorded a usable "step N of M".</summary>
    public bool HasStepCount => Step > 0 && TotalSteps > 0;
}

/// <summary>
///  Repository-wide facts that decide which menu entries make sense, and that no
///  other service of this port exposed.
///
///  <para>The only one needed so far is "is this a bare repository?" — upstream
///  <c>FormBrowse</c> reads it from <c>GitModule.IsBareRepository()</c> and uses it
///  to grey out everything that needs a work tree (commit, stash, clean, the
///  submodule commands, the <c>.gitignore</c>/<c>.gitattributes</c>/<c>.mailmap</c>
///  editors; see <c>FormBrowse.cs:1014-1034</c> and
///  <c>CommandsToolStripMenuItem_DropDownOpening</c>).</para>
///
///  <para>The question is asked of git itself
///  (<c>git rev-parse --is-bare-repository</c>) rather than derived from the paths:
///  the path comparison upstream uses (<c>WorkingDir == GetGitDirectory()</c>) also
///  answers "true" for a plain <c>.git</c> directory opened directly, and it says
///  nothing at all when <c>core.bare</c> disagrees with the layout. The
///  <see cref="GitModule"/> answer is kept as the fallback for the case where the
///  process cannot be run at all.</para>
///
///  <para>Synchronous, like every other service here, and therefore to be called
///  from <see cref="Task.Run"/> — never from the UI thread.</para>
/// </summary>
public sealed class RepositoryStateService
{
    /// <summary>
    ///  True when <paramref name="repoPath"/> is a bare repository (no work tree).
    ///  A path that is not a repository at all, or any failure, yields false: the
    ///  caller then simply does not grey anything out, which is the safe direction —
    ///  a wrongly enabled entry reports git's own error, a wrongly disabled one
    ///  would be an unexplainable dead end.
    /// </summary>
    public bool IsBareRepository(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return false;
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("rev-parse") { "--is-bare-repository" },
                throwOnErrorExit: false);

            if (result.ExitedSuccessfully)
            {
                return string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            }

            // Not a repository, or a git too old for the flag: fall back to the
            // layout comparison FormBrowse itself uses.
            return module.IsBareRepository();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///  Reports the multi-step git operation the repository is stopped in, by reading
    ///  the marker files git itself keeps in the git directory.
    ///
    ///  <para>Upstream <c>FormBrowse</c> keeps two notification bars above the grid —
    ///  <c>notificationBarBisectInProgress</c> and
    ///  <c>notificationBarGitActionInProgress</c>
    ///  (<c>FormBrowse.Designer.cs:650-668</c>, refreshed at
    ///  <c>FormBrowse.cs:1175-1182</c>) — so that a rebase, merge, bisect or
    ///  cherry-pick that stopped on a conflict is visible at a glance. This is the
    ///  detection half of the same idea.</para>
    ///
    ///  <para>The markers are read straight off the disk rather than shelled out for:
    ///  it is what git's own <c>wt-status.c</c> does, it costs no process, and it is
    ///  cheap enough to run on every repository-changed notification. The git
    ///  directory is resolved with
    ///  <see cref="RepositoryWatcherService.ResolveGitDir"/>, which already handles a
    ///  linked worktree's <c>.git</c> file.</para>
    ///
    ///  <para>Never throws and never blocks: on any doubt it answers
    ///  <see cref="RepositoryProgress.None"/>, which merely hides the banner — the
    ///  safe direction, since a missing banner is the behaviour the port had anyway,
    ///  while a wrong one would be an outright lie about the repository.</para>
    ///
    ///  <para>Synchronous like the rest of this class: call it from
    ///  <see cref="Task.Run"/>, never on the UI thread.</para>
    /// </summary>
    public RepositoryProgress GetProgress(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return RepositoryProgress.None;
        }

        try
        {
            string? gitDir = RepositoryWatcherService.ResolveGitDir(repoPath);
            if (string.IsNullOrEmpty(gitDir) || !Directory.Exists(gitDir))
            {
                return RepositoryProgress.None;
            }

            // A bisect is orthogonal to the rest, exactly as upstream treats it.
            bool bisect = File.Exists(Path.Combine(gitDir, "BISECT_LOG"))
                || File.Exists(Path.Combine(gitDir, "BISECT_START"));

            // "rebase-merge" is the modern rebase backend and the interactive one;
            // "rebase-apply" is the am/patch backend, shared with `git am` — the
            // "applying" file is what tells the two apart (git's own test in
            // wt-status.c). Both are checked before the single-file markers because
            // a stopped rebase step also leaves CHERRY_PICK_HEAD behind and would
            // otherwise be reported as a cherry-pick.
            string rebaseMerge = Path.Combine(gitDir, "rebase-merge");
            if (Directory.Exists(rebaseMerge))
            {
                bool interactive = File.Exists(Path.Combine(rebaseMerge, "interactive"));
                return new RepositoryProgress(
                    interactive ? RepositoryOperation.RebaseInteractive : RepositoryOperation.Rebase,
                    bisect,
                    ReadNumber(Path.Combine(rebaseMerge, "msgnum")),
                    ReadNumber(Path.Combine(rebaseMerge, "end")),
                    ReadRefName(Path.Combine(rebaseMerge, "head-name")));
            }

            string rebaseApply = Path.Combine(gitDir, "rebase-apply");
            if (Directory.Exists(rebaseApply))
            {
                bool applying = File.Exists(Path.Combine(rebaseApply, "applying"));
                return new RepositoryProgress(
                    applying ? RepositoryOperation.ApplyMailbox : RepositoryOperation.Rebase,
                    bisect,
                    ReadNumber(Path.Combine(rebaseApply, "next")),
                    ReadNumber(Path.Combine(rebaseApply, "last")),
                    ReadRefName(Path.Combine(rebaseApply, "head-name")));
            }

            if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD")))
            {
                return new RepositoryProgress(RepositoryOperation.Merge, bisect);
            }

            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            {
                return new RepositoryProgress(RepositoryOperation.CherryPick, bisect);
            }

            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            {
                return new RepositoryProgress(RepositoryOperation.Revert, bisect);
            }

            return bisect
                ? new RepositoryProgress(RepositoryOperation.None, true)
                : RepositoryProgress.None;
        }
        catch
        {
            // A vanishing git dir mid-read, a permission problem: say nothing.
            return RepositoryProgress.None;
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

            const string prefix = "refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal)
                ? text[prefix.Length..]
                : text;
        }
        catch
        {
            return null;
        }
    }
}
