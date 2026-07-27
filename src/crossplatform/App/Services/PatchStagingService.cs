using System.Text;
using GitCommands;
using GitCommands.Patches;
using GitExtensions.Extensibility;

namespace GitExtensions.Avalonia.Services;

/// <summary>What a generated patch is meant to do.</summary>
public enum PatchStagingAction
{
    /// <summary>Move the selected work-tree lines into the index (<c>git add -p</c>).</summary>
    Stage,

    /// <summary>Take the selected lines back out of the index (<c>git reset -p</c>).</summary>
    Unstage,

    /// <summary>Throw the selected work-tree lines away (<c>git checkout -p</c>). Destructive.</summary>
    DiscardWorkTree,
}

public sealed record PatchStagingResult(bool Success, string Output);

/// <summary>
///  Per-hunk / per-line staging, i.e. the part of <c>git add -p</c> the port was
///  missing: a patch is built from a character range inside a unified diff and fed
///  to <c>git apply</c> on stdin.
/// </summary>
/// <remarks>
///  <para>
///   The patch construction itself is NOT reimplemented here. It is delegated to
///   <see cref="PatchManager"/> from the shared core (<c>GitCommands.Patches</c>),
///   which the Avalonia app already compiles in through
///   <c>Core.GitCommands.csproj</c>. That type is plain .NET — no WinForms, no
///   <c>System.Drawing</c>, no UI thread — it only takes the diff text plus a
///   selection range and returns the patch bytes, so it is reusable verbatim. It
///   is also where all the nasty corner cases already live (sub-chunks, recomputed
///   <c>@@</c> counters, <c>\ No newline at end of file</c>, the
///   <c>--- /dev/null</c> fix-up for added files, the rename fix-up), which is
///   exactly the code one does not want to write twice.
///  </para>
///  <para>
///   <b>The diff must be clean.</b> A patch can only be built from a diff produced
///   without any display-oriented option: no colour, no <c>-w</c>, no
///   <c>--word-diff</c>, no external differ, no textconv. <see cref="LoadDiff"/> is
///   the only supported source, and the caller must render exactly the string it
///   returns so that selection offsets line up with the patch source.
///  </para>
///  <para>
///   Everything here blocks (the core's <c>Execute</c> is sync-over-async), so it
///   must never be called from the UI thread — see HANDOFF §3 / bug M43.
///  </para>
/// </remarks>
public static class PatchStagingService
{
    /// <summary>
    ///  Encoding used BOTH to decode <c>git diff</c> and to re-encode the patch body,
    ///  so the bytes make a round trip. Content that is not valid UTF-8 decodes to
    ///  U+FFFD and is refused up front by <see cref="Apply"/> rather than silently
    ///  producing a patch whose context no longer matches.
    /// </summary>
    private static readonly UTF8Encoding PatchEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    ///  Options that force a plain, machine-readable unified diff. Without these a
    ///  user's <c>diff.external</c> / <c>diff.*.textconv</c> / <c>color.diff</c>
    ///  configuration would leak into the text the patch is cut from.
    /// </summary>
    private const string CleanDiffFlags =
        "--patch --no-color --no-ext-diff --no-textconv --unified=3 --src-prefix=a/ --dst-prefix=b/";

    /// <summary>
    ///  The clean unified diff of one file, work tree (<paramref name="staged"/> =
    ///  <see langword="false"/>) or index. Returns an empty string when there is
    ///  nothing to show (untracked files, for instance, never appear in a diff).
    /// </summary>
    public static string LoadDiff(string repoPath, string path, bool staged)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string args = staged
            ? $"diff --cached {CleanDiffFlags} -- \"{path}\""
            : $"diff {CleanDiffFlags} -- \"{path}\"";

        ExecutionResult result = module.GitExecutable.Execute(
            args,
            outputEncoding: PatchEncoding,
            throwOnErrorExit: false);

        // StandardOutput only: stderr mixed into the text would corrupt the patch
        // source. It is still worth surfacing when git produced nothing else.
        string stdout = result.StandardOutput ?? string.Empty;
        return stdout.Length > 0 ? stdout : (result.AllOutput ?? string.Empty);
    }

    /// <summary>
    ///  Builds a patch out of <paramref name="selectionLength"/> characters of
    ///  <paramref name="diffText"/> starting at <paramref name="selectionStart"/>
    ///  and applies it. <paramref name="diffText"/> must be exactly what
    ///  <see cref="LoadDiff"/> returned and the offsets must be relative to it.
    /// </summary>
    /// <param name="isNewFile">
    ///  The file is an addition on the side being patched (<c>--- /dev/null</c> in
    ///  the header). <see cref="PatchManager"/> rewrites the header for it.
    /// </param>
    /// <param name="isRenamed">The file is a rename on the side being patched.</param>
    public static PatchStagingResult Apply(
        string repoPath,
        string diffText,
        int selectionStart,
        int selectionLength,
        PatchStagingAction action,
        bool isNewFile,
        bool isRenamed)
    {
        if (string.IsNullOrEmpty(diffText) || !diffText.Contains("@@", StringComparison.Ordinal))
        {
            // No hunks at all: binary file, pure mode change, or an empty diff.
            return new PatchStagingResult(false, NoHunksMessage);
        }

        if (diffText.Contains('�'))
        {
            // The diff did not survive the UTF-8 round trip, so the patch bytes
            // would not match what is in the index. Refuse instead of letting
            // `git apply` chew on a mangled context.
            return new PatchStagingResult(false, NotUtf8Message);
        }

        if (selectionLength <= 0)
        {
            return new PatchStagingResult(false, NoSelectionMessage);
        }

        byte[]? patch;
        try
        {
            patch = action switch
            {
                // Work tree -> index. The "a" side of the diff is the index, so the
                // patch is built with isIndex: false and applied forward.
                PatchStagingAction.Stage => PatchManager.GetSelectedLinesAsPatch(
                    diffText, selectionStart, selectionLength,
                    isIndex: false, PatchEncoding, reset: false, isNewFile, isRenamed),

                // Index -> work tree. Built against the index side and applied in
                // reverse, exactly like the WinForms viewer does.
                PatchStagingAction.Unstage => PatchManager.GetSelectedLinesAsPatch(
                    diffText, selectionStart, selectionLength,
                    isIndex: true, PatchEncoding, reset: false, isNewFile, isRenamed),

                // Throw the lines away. PatchManager already emits an INVERTED patch
                // here, which is why the git side below adds no --reverse.
                _ => PatchManager.GetResetWorkTreeLinesAsPatch(
                    diffText, selectionStart, selectionLength, PatchEncoding),
            };
        }
        catch (Exception ex)
        {
            return new PatchStagingResult(false, ex.Message);
        }

        if (patch is null || patch.Length == 0)
        {
            // The selection touched only hunk headers / context, so nothing to do.
            return new PatchStagingResult(false, NoSelectionMessage);
        }

        string args = action switch
        {
            PatchStagingAction.Stage => "apply --cached --index --whitespace=nowarn",
            PatchStagingAction.Unstage => "apply --cached --index --whitespace=nowarn --reverse",
            _ => "apply --whitespace=nowarn",
        };

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            ExecutionResult result = module.GitExecutable.Execute(
                args,
                inputWriter => inputWriter.BaseStream.Write(patch),
                throwOnErrorExit: false);

            string output = (result.AllOutput ?? string.Empty).Trim();
            return result.ExitedSuccessfully && !output.StartsWith("error:", StringComparison.Ordinal)
                ? new PatchStagingResult(true, output)
                : new PatchStagingResult(false, output.Length > 0 ? output : $"git {args} failed");
        }
        catch (Exception ex)
        {
            return new PatchStagingResult(false, ex.Message);
        }
    }

    // Kept as plain English constants: the dialog translates them through its own
    // T(...) helper, the service stays free of any UI dependency.
    public const string NoHunksMessage = "This file has no text hunks to patch (binary, or nothing changed).";
    public const string NotUtf8Message = "The diff is not valid UTF-8; line staging is not available for this file.";
    public const string NoSelectionMessage = "Select one or more diff lines first.";
}
