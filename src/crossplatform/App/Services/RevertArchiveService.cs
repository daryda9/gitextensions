using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Archive output formats offered from the revision grid. Mapped to the
///  corresponding <c>git archive --format=</c> value in
///  <see cref="RevertArchiveService.Archive"/>.
/// </summary>
public enum ArchiveFormat
{
    Zip,
    TarGz,
}

/// <summary>
///  Result of a revert / archive operation: success flag plus the full git
///  output (surfaced to the user on failure or conflict).
/// </summary>
public sealed record RevertArchiveResult(bool Success, string Output);

/// <summary>
///  Commit-targeted "revert" and "archive" operations for the revision grid,
///  implemented by reusing the Git Extensions core (<see cref="GitModule"/>)
///  via <see cref="GitContext.CreateModule"/>. Both methods are synchronous and
///  are meant to be called off the UI thread (mirrors <see cref="StashOpsService"/>).
/// </summary>
public sealed class RevertArchiveService
{
    /// <summary>
    ///  Reverts the commit identified by <paramref name="commitHash"/> on the
    ///  current branch, committing the result (<c>git revert --no-edit &lt;hash&gt;</c>).
    ///  The core <c>Commands.Revert</c> builder emits no <c>--no-edit</c>, which would
    ///  open an editor, so the command is built directly here. A revert that stops on
    ///  a conflict is reported as a failure with the full git output preserved in
    ///  <see cref="RevertArchiveResult.Output"/>; it never throws.
    /// </summary>
    public RevertArchiveResult Revert(string repoPath, string commitHash)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("revert")
        {
            "--no-edit",
            commitHash,
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new RevertArchiveResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  Writes the tree of the commit identified by <paramref name="commitHash"/> to
    ///  <paramref name="outputPath"/> using <c>git archive --format=&lt;zip|tar.gz&gt;
    ///  -o &lt;path&gt; &lt;hash&gt;</c>. Success requires both a clean git exit and the
    ///  output file existing and non-empty. Never throws.
    /// </summary>
    public RevertArchiveResult Archive(string repoPath, string commitHash, ArchiveFormat format, string outputPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string formatArg = format == ArchiveFormat.Zip ? "zip" : "tar.gz";
        GitArgumentBuilder args = new("archive")
        {
            $"--format={formatArg}",
            "-o",
            outputPath.Quote(),
            commitHash,
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);

        bool created = File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        bool ok = result.ExitedSuccessfully && created;
        string output = ok
            ? result.AllOutput
            : $"{result.AllOutput}\n(archive file {(created ? "was written but git reported an error" : "was not created")}: {outputPath})";
        return new RevertArchiveResult(ok, output);
    }
}
