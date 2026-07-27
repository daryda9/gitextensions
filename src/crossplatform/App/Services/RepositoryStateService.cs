using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

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
}
