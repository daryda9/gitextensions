using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Result of a bisect operation: success flag plus the full git output. On a
///  successful mark git prints the next commit to test (or the final
///  "first bad commit"); that text is surfaced to the user via
///  <see cref="Output"/>.
/// </summary>
public sealed record BisectResult(bool Success, string Output);

/// <summary>
///  Drives <c>git bisect</c> by reusing the Git Extensions core
///  (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>. All
///  methods are synchronous and are meant to be called off the UI thread,
///  mirroring the other Avalonia services (e.g. <see cref="WorktreeService"/>).
/// </summary>
public sealed class BisectService
{
    /// <summary>Begins a bisect session (<c>git bisect start</c>).</summary>
    public BisectResult Start(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "start" });

    /// <summary>Marks <paramref name="hash"/> as good (<c>git bisect good &lt;hash&gt;</c>).</summary>
    public BisectResult MarkGood(string repoPath, string hash)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "good", hash });

    /// <summary>Marks <paramref name="hash"/> as bad (<c>git bisect bad &lt;hash&gt;</c>).</summary>
    public BisectResult MarkBad(string repoPath, string hash)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "bad", hash });

    /// <summary>Skips <paramref name="hash"/> (<c>git bisect skip &lt;hash&gt;</c>).</summary>
    public BisectResult Skip(string repoPath, string hash)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "skip", hash });

    /// <summary>Ends the bisect session and restores HEAD (<c>git bisect reset</c>).</summary>
    public BisectResult Reset(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "reset" });

    /// <summary>
    ///  True when a bisect session is in progress. Detected first via the
    ///  <c>.git/BISECT_LOG</c> / <c>.git/BISECT_START</c> marker files (fast,
    ///  handles linked worktrees through the resolved git dir); falls back to
    ///  the exit status of
    ///  <c>git bisect log</c>, which only succeeds mid-session. Never throws.
    /// </summary>
    public bool IsInProgress(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        try
        {
            string gitDir = module.WorkingDirGitDir;
            if (gitDir.Length > 0 &&
                (File.Exists(Path.Combine(gitDir, "BISECT_LOG")) ||
                 File.Exists(Path.Combine(gitDir, "BISECT_START"))))
            {
                return true;
            }
        }
        catch
        {
            // fall through to the git-log probe
        }

        GitArgumentBuilder args = new("bisect") { "log" };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return result.ExitedSuccessfully;
    }

    private static BisectResult Run(string repoPath, GitArgumentBuilder args)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new BisectResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
