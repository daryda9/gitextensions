using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Archive output formats offered from the revision grid. Mapped to the
///  corresponding <c>git archive --format=</c> value in
///  <see cref="RevertArchiveService.Archive"/>.
///
///  <para>
///  Upstream's <c>FormArchive</c> exposes exactly two, as radio buttons:
///  <c>zip</c> and plain <c>tar</c> (<c>FormArchive.Designer.cs</c>,
///  <c>_NO_TRANSLATE_radioButtonFormatZip</c> / <c>…FormatTar</c>, and
///  <c>FormArchive.cs:131</c> which maps them to the literal strings
///  <c>"zip"</c> / <c>"tar"</c>). <see cref="Tar"/> exists to close that gap.
///  <see cref="TarGz"/> is a deliberate addition on top of upstream: it is what a
///  Linux user reaches for, and <c>git archive</c> supports it natively via its
///  configured <c>tar.tar.gz.command</c>.
///  </para>
/// </summary>
public enum ArchiveFormat
{
    Zip,
    TarGz,

    /// <summary>Plain, uncompressed tar — upstream's second radio button.</summary>
    Tar,
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
    ///  <paramref name="outputPath"/> using <c>git archive --format=&lt;zip|tar|tar.gz&gt;
    ///  -o &lt;path&gt; &lt;hash&gt;</c>. Success requires both a clean git exit and the
    ///  output file existing and non-empty. Never throws.
    ///
    ///  <para>
    ///  Two deliberate divergences from upstream's single <c>string.Format</c>
    ///  (<c>FormArchive.cs:133</c>):
    ///  </para>
    ///  <list type="bullet">
    ///   <item>the pathspec is introduced with <c>--</c>, which upstream omits — without
    ///    it a path that happens to look like a ref is read as one;</item>
    ///   <item>there is <b>no <c>--prefix</c></b>, because upstream has no prefix control
    ///    either: <c>FormArchive</c> exposes only the format radio buttons, the path
    ///    filter and the diff filter. Adding one would be inventing UI, so it is left
    ///    out on purpose.</item>
    ///  </list>
    ///  <para>
    ///  <see cref="GitArgumentBuilder"/> flattens every argument into one command line,
    ///  so a value containing a space would be re-split into two arguments (git then
    ///  exits 0 having silently archived the wrong thing). Both the output path and each
    ///  pathspec are therefore quoted.
    ///  </para>
    /// </summary>
    /// <param name="paths">
    ///  Optional pathspec limiting what goes into the archive — upstream's
    ///  "Files matching these paths" filter and its "only the files changed since
    ///  another revision" mode both end up here (<c>FormArchive.cs:139-158</c>).
    ///  Each entry is quoted and appended after the commit, i.e.
    ///  <c>git archive … &lt;hash&gt; -- &lt;path&gt; …</c>. An empty or null list
    ///  archives the whole tree, as before.
    /// </param>
    public RevertArchiveResult Archive(
        string repoPath,
        string commitHash,
        ArchiveFormat format,
        string outputPath,
        IReadOnlyList<string>? paths = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string formatArg = format switch
        {
            ArchiveFormat.Zip => "zip",
            ArchiveFormat.Tar => "tar",
            _ => "tar.gz",
        };
        GitArgumentBuilder args = new("archive")
        {
            $"--format={formatArg}",
            "-o",
            outputPath.Quote(),
            commitHash,
        };

        // "--" keeps a path that looks like a revision from being read as one.
        if (paths is { Count: > 0 })
        {
            args.Add("--");
            foreach (string path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    args.Add(path.Trim().Quote());
                }
            }
        }

        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);

        bool created = File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        bool ok = result.ExitedSuccessfully && created;
        string output = ok
            ? result.AllOutput
            : $"{result.AllOutput}\n(archive file {(created ? "was written but git reported an error" : "was not created")}: {outputPath})";
        return new RevertArchiveResult(ok, output);
    }

    /// <summary>
    ///  Resolves a user-typed revision expression (a hash prefix, branch, tag,
    ///  <c>HEAD~2</c>, …) to a full commit hash, or <see langword="null"/> when git
    ///  cannot make a commit of it. Used by the archive dialog to validate the
    ///  "changed since another revision" filter before diffing against it.
    /// </summary>
    public static string? ResolveCommit(string repoPath, string revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return null;
        }

        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("rev-parse")
        {
            "--verify",
            "--quiet",
            $"{revision.Trim()}^{{commit}}",
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        string hash = result.StandardOutput.Trim();
        return result.ExitedSuccessfully && hash.Length > 0 ? hash : null;
    }
}
