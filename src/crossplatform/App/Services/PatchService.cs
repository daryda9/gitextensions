using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Result of a patch operation: success flag, the full git output (surfaced to
///  the user), and — for <see cref="PatchService.FormatPatch"/> — the list of
///  patch files git wrote.
/// </summary>
public sealed record PatchResult(bool Success, string Output, IReadOnlyList<string> Files);

/// <summary>
///  Patch operations (format / apply) implemented by reusing the Git Extensions
///  core (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>.
///  Every method is synchronous and meant to run off the UI thread (mirrors
///  <see cref="RevertArchiveService"/>); none of them throw — a failed git run is
///  reported through <see cref="PatchResult"/> with the git output preserved.
/// </summary>
public sealed class PatchService
{
    /// <summary>
    ///  Generates one patch file per commit in the range
    ///  <paramref name="baseRef"/>..HEAD, written into <paramref name="outputDir"/>
    ///  (<c>git format-patch &lt;base&gt;..HEAD -o &lt;outdir&gt;</c>). git prints the
    ///  path of each generated file on stdout; those paths are returned in
    ///  <see cref="PatchResult.Files"/>.
    /// </summary>
    public PatchResult FormatPatch(string repoPath, string baseRef, string outputDir)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            return new PatchResult(false, $"Could not create output directory {outputDir}: {ex.Message}", Array.Empty<string>());
        }

        GitArgumentBuilder args = new("format-patch")
        {
            $"{baseRef}..HEAD",
            "-o",
            outputDir.Quote(),
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);

        IReadOnlyList<string> files = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new PatchResult(result.ExitedSuccessfully, result.AllOutput, files);
    }

    /// <summary>
    ///  Applies the patch at <paramref name="patchFile"/>. First tries
    ///  <c>git am &lt;file&gt;</c> (which preserves author/message for mailbox-format
    ///  patches produced by <c>git format-patch</c>); if that fails — e.g. the file is
    ///  a plain <c>git diff</c> and not a mailbox — the half-started am session is
    ///  aborted (<c>git am --abort</c>) and it falls back to <c>git apply &lt;file&gt;</c>.
    ///  The combined git output is preserved in <see cref="PatchResult.Output"/>.
    /// </summary>
    public PatchResult ApplyPatch(string repoPath, string patchFile)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        GitArgumentBuilder amArgs = new("am") { patchFile.Quote() };
        ExecutionResult am = module.GitExecutable.Execute(amArgs, throwOnErrorExit: false);
        if (am.ExitedSuccessfully)
        {
            return new PatchResult(true, "git am succeeded:\n" + am.AllOutput, Array.Empty<string>());
        }

        // am failed: abort any session it may have left mid-apply (harmless if none
        // is in progress), then fall back to a plain git apply.
        string amOutput = am.AllOutput;
        GitArgumentBuilder abortArgs = new("am") { "--abort" };
        module.GitExecutable.Execute(abortArgs, throwOnErrorExit: false);

        GitArgumentBuilder applyArgs = new("apply") { patchFile.Quote() };
        ExecutionResult apply = module.GitExecutable.Execute(applyArgs, throwOnErrorExit: false);

        string combined = apply.ExitedSuccessfully
            ? "git am did not apply (not a mailbox patch); git apply succeeded:\n" + apply.AllOutput
            : $"git am failed:\n{amOutput}\n\ngit apply also failed:\n{apply.AllOutput}";

        return new PatchResult(apply.ExitedSuccessfully, combined, Array.Empty<string>());
    }
}
