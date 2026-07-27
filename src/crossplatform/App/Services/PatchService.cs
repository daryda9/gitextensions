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
    ///  Applies the patch at <paramref name="patchFile"/>, choosing the command the
    ///  way the original Git Extensions <c>FormApplyPatch</c> does: the file is
    ///  sniffed (see <see cref="IsDiffFile"/>, a port of <c>FormApplyPatch.IsDiffFile</c>)
    ///  and a raw <c>git diff</c> goes through <c>git apply</c>, while a mailbox /
    ///  <c>git format-patch</c> file goes through <c>git am</c> (which preserves the
    ///  original author and message).
    ///
    ///  <para>
    ///   An <c>am</c> session already in progress (ours or the user's, e.g. a rebase
    ///   or an earlier <c>git am</c> stopped on a conflict) is never touched: the
    ///   operation refuses to start and says so. <c>git am --abort</c> is only issued
    ///   for a session THIS call started and left mid-apply — the previous code
    ///   aborted blindly and so could destroy unrelated in-flight work.
    ///  </para>
    /// </summary>
    public PatchResult ApplyPatch(string repoPath, string patchFile)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // Raw diff (git diff -p / "Index:" style) → git apply. No mailbox headers,
        // so git am would fail and there is nothing to gain from trying it.
        if (IsDiffFile(patchFile))
        {
            ExecutionResult applyOnly = module.GitExecutable.Execute(
                new GitArgumentBuilder("apply") { patchFile.Quote() },
                throwOnErrorExit: false);

            return new PatchResult(
                applyOnly.ExitedSuccessfully,
                (applyOnly.ExitedSuccessfully
                    ? "the file is a raw diff; git apply succeeded:\n"
                    : "the file is a raw diff; git apply failed:\n") + applyOnly.AllOutput,
                Array.Empty<string>());
        }

        // Mailbox/format-patch file → git am. Refuse when a patch session is already
        // open: `git am <file>` would fail anyway ("previous rebase directory …"),
        // and we must not clean up a session we did not create.
        if (InTheMiddleOfPatch(module))
        {
            return new PatchResult(
                false,
                "A `git am` / rebase session is already in progress in this repository "
                + "(.git/rebase-apply exists). Finish it (git am --continue / --skip) or "
                + "abort it yourself (git am --abort) before applying another patch — "
                + "this operation will not touch it.",
                Array.Empty<string>());
        }

        ExecutionResult am = module.GitExecutable.Execute(
            new GitArgumentBuilder("am") { patchFile.Quote() },
            throwOnErrorExit: false);
        if (am.ExitedSuccessfully)
        {
            return new PatchResult(true, "git am succeeded:\n" + am.AllOutput, Array.Empty<string>());
        }

        // am failed. If it stopped mid-apply it left a session behind — that one IS
        // ours, so aborting it is safe and restores the pre-call state. Report the
        // failure; we deliberately do NOT retry with `git apply`, because the file is
        // a mailbox and a fallback would silently drop its author/message metadata.
        string message = "git am failed:\n" + am.AllOutput;
        if (InTheMiddleOfPatch(module))
        {
            ExecutionResult abort = module.GitExecutable.Execute(
                new GitArgumentBuilder("am") { "--abort" },
                throwOnErrorExit: false);
            message += abort.ExitedSuccessfully
                ? "\n\nThe half-applied am session this operation started was aborted; the repository is unchanged."
                : "\n\nThe am session this operation started could NOT be aborted:\n" + abort.AllOutput;
        }

        return new PatchResult(false, message, Array.Empty<string>());
    }

    /// <summary>
    ///  Whether a <c>git am</c> / rebase-apply session is in progress. Uses the core
    ///  <see cref="GitModule.InTheMiddleOfPatch"/> when it can be reached, and falls
    ///  back to the same on-disk check (<c>.git/rebase-apply</c>) if it throws.
    /// </summary>
    private static bool InTheMiddleOfPatch(GitModule module)
    {
        try
        {
            return module.InTheMiddleOfPatch();
        }
        catch (Exception)
        {
            try
            {
                return Directory.Exists(Path.Combine(module.WorkingDir, ".git", "rebase-apply"));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // Port of FormApplyPatch.IsDiffFile: look at the first line only — enough to
    // tell a raw diff from a mailbox-formatted patch. Never throws; on any problem
    // it returns false, i.e. the file is treated as a mailbox (git am), exactly
    // like upstream.
    private static bool IsDiffFile(string path)
    {
        try
        {
            using StreamReader reader = new(path);
            string? line = reader.ReadLine();
            return line is not null
                && (line.StartsWith("diff ", StringComparison.Ordinal)
                    || line.StartsWith("Index: ", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
