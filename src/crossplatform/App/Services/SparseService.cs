using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Result of a sparse-checkout operation: a success flag plus the full
///  textual output (git's stdout/stderr, or a short status message). The
///  output is surfaced to the user verbatim.
/// </summary>
public sealed record SparseResult(bool Success, string Output);

/// <summary>
///  Wraps the core git <c>sparse-checkout</c> plumbing for the Avalonia port:
///  read the current pattern set (<c>list</c>), enable cone-mode sparse checkout
///  (<c>init --cone</c>), set the tracked patterns (<c>set</c>) and disable the
///  feature (<c>disable</c>).
///
///  Every operation reuses the Git Extensions core (<see cref="GitModule"/>) via
///  <see cref="GitContext.CreateModule"/>, exactly like
///  <see cref="MaintenanceService"/>. All methods are synchronous, are meant to
///  be called off the UI thread, and never throw for an ordinary git failure —
///  the failure is reported through <see cref="SparseResult"/>.
/// </summary>
public sealed class SparseService
{
    /// <summary>
    ///  Reads the current sparse-checkout pattern set (<c>git sparse-checkout
    ///  list</c>). When sparse checkout is not enabled git exits successfully with
    ///  no output; the caller treats an empty successful result as "disabled".
    /// </summary>
    public SparseResult List(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("sparse-checkout") { "list" });

    /// <summary>
    ///  Enables cone-mode sparse checkout (<c>git sparse-checkout init --cone</c>).
    /// </summary>
    public SparseResult Enable(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("sparse-checkout") { "init", "--cone" });

    /// <summary>
    ///  Sets the tracked directories/patterns (<c>git sparse-checkout set
    ///  &lt;patterns&gt;</c>). Each entry is passed as a separate argument.
    /// </summary>
    public SparseResult SetPatterns(string repoPath, IReadOnlyList<string> patterns)
    {
        GitArgumentBuilder args = new("sparse-checkout") { "set" };
        foreach (string pattern in patterns)
        {
            args.Add(pattern);
        }

        return Run(repoPath, args);
    }

    /// <summary>
    ///  Disables sparse checkout and restores the full working tree
    ///  (<c>git sparse-checkout disable</c>).
    /// </summary>
    public SparseResult Disable(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("sparse-checkout") { "disable" });

    private static SparseResult Run(string repoPath, GitArgumentBuilder args)
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

            return new SparseResult(result.ExitedSuccessfully, output);
        }
        catch (Exception ex)
        {
            return new SparseResult(false, ex.Message);
        }
    }
}
