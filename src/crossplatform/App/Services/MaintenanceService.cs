using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Result of a repository-maintenance operation: a success flag plus the full
///  textual output (git's stdout/stderr, or a status message for the local
///  file operations). The output is surfaced to the user verbatim.
/// </summary>
public sealed record MaintenanceResult(bool Success, string Output);

/// <summary>
///  Repository "housekeeping" operations for the Avalonia port, mirroring the
///  original GitExtensions "Git maintenance" menu:
///
///  <list type="bullet">
///    <item><description>Compress database — <c>git gc</c></description></item>
///    <item><description>Verify database — <c>git fsck</c></description></item>
///    <item><description>Delete <c>.git/index.lock</c> (a stale lock left by an
///      interrupted git process)</description></item>
///  </list>
///
///  The git-backed operations reuse the Git Extensions core
///  (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>, exactly
///  like <see cref="BisectService"/>. They are synchronous and meant to be
///  called off the UI thread; none of them throw for an ordinary git failure —
///  the failure is reported through <see cref="MaintenanceResult"/>.
/// </summary>
public sealed class MaintenanceService
{
    /// <summary>Compresses the object database (<c>git gc</c>).</summary>
    public MaintenanceResult CompressDatabase(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("gc"));

    /// <summary>Verifies the object database (<c>git fsck</c>).</summary>
    public MaintenanceResult VerifyDatabase(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("fsck"));

    /// <summary>
    ///  Deletes a stale <c>.git/index.lock</c> if present. Handles a missing
    ///  file gracefully (reports that there was nothing to remove) and reports
    ///  any delete failure as an unsuccessful result rather than throwing.
    /// </summary>
    public MaintenanceResult DeleteIndexLock(string repoPath)
    {
        string lockPath;
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string gitDir = module.WorkingDirGitDir;
            if (string.IsNullOrEmpty(gitDir))
            {
                gitDir = Path.Combine(repoPath, ".git");
            }

            lockPath = Path.Combine(gitDir, "index.lock");
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, $"Could not resolve the git directory: {ex.Message}");
        }

        try
        {
            if (!File.Exists(lockPath))
            {
                return new MaintenanceResult(true, $"No lock file to remove ({lockPath} does not exist).");
            }

            File.Delete(lockPath);
            return new MaintenanceResult(true, $"Removed {lockPath}.");
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, $"Could not delete {lockPath}: {ex.Message}");
        }
    }

    /// <summary>
    ///  Resolves the absolute path to the repository's <c>.git/config</c> file
    ///  (for the "Edit .git/config" action). Never throws; falls back to
    ///  <c>&lt;repo&gt;/.git/config</c> if the git directory cannot be resolved.
    /// </summary>
    public string ResolveConfigPath(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string gitDir = module.WorkingDirGitDir;
            if (!string.IsNullOrEmpty(gitDir))
            {
                return Path.Combine(gitDir, "config");
            }
        }
        catch
        {
            // fall through to the conventional location
        }

        return Path.Combine(repoPath, ".git", "config");
    }

    private static MaintenanceResult Run(string repoPath, GitArgumentBuilder args)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            string output = result.AllOutput;
            if (string.IsNullOrWhiteSpace(output))
            {
                output = result.ExitedSuccessfully ? "(completed with no output)" : "(failed with no output)";
            }

            return new MaintenanceResult(result.ExitedSuccessfully, output);
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, ex.Message);
        }
    }
}
