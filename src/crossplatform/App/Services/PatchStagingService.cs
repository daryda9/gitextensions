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
///  A loaded diff, split into what goes on screen and what a patch may be cut from.
/// </summary>
/// <param name="Display">The text to render.</param>
/// <param name="Source">
///  The patch source: byte-for-byte git output, or an EMPTY string when
///  <see cref="Display"/> is not a faithful diff (git error text, or content that
///  had to be truncated), in which case line staging must stay disabled.
/// </param>
public sealed record DiffLoad(string Display, string Source)
{
    public static readonly DiffLoad Empty = new(string.Empty, string.Empty);
}

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
    ///  Line budget for the synthetic diff of an untracked file. Past it the panel
    ///  shows a truncated view (upstream's <c>[Truncated]</c> behaviour) and line
    ///  staging is switched off, because a patch cut from a partial file would
    ///  carry counters git cannot match.
    /// </summary>
    public const int UntrackedMaxLines = 5000;

    /// <summary>Character budget for the same, for very long single lines.</summary>
    public const int UntrackedMaxChars = 512 * 1024;

    /// <summary>
    ///  The clean unified diff of one file, work tree (<paramref name="staged"/> =
    ///  <see langword="false"/>) or index.
    ///  <para>
    ///  An <b>untracked</b> file is not in the index and not in HEAD, so a plain
    ///  <c>git diff</c> has nothing to compare and prints NOTHING — which used to
    ///  leave the diff panel blank with no error at all. For those, git is asked for
    ///  a real patch against <c>/dev/null</c> (<c>diff --no-index</c>), which yields
    ///  the whole file as added lines under a genuine <c>--- /dev/null</c> header,
    ///  the same shape <c>isNewFile</c> line staging already expects. Binary content
    ///  is git's own problem there: it prints <c>Binary files … differ</c> instead of
    ///  raw bytes.
    ///  </para>
    /// </summary>
    /// <param name="untracked">
    ///  The file exists only in the work tree (status "new" on the unstaged side).
    /// </param>
    public static DiffLoad LoadDiff(string repoPath, string path, bool staged, bool untracked = false)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string args = untracked
            ? $"diff --no-index {CleanDiffFlags} -- /dev/null \"{path}\""
            : staged
                ? $"diff --cached {CleanDiffFlags} -- \"{path}\""
                : $"diff {CleanDiffFlags} -- \"{path}\"";

        // `git diff --no-index` exits 1 whenever the files differ, which is the
        // normal case here, so the exit code must not be treated as a failure.
        ExecutionResult result = module.GitExecutable.Execute(
            args,
            outputEncoding: PatchEncoding,
            throwOnErrorExit: false);

        // StandardOutput only: stderr mixed into the text would corrupt the patch
        // source. It is still worth surfacing when git produced nothing else.
        string stdout = result.StandardOutput ?? string.Empty;
        if (stdout.Length == 0)
        {
            // Nothing on stdout: whatever git said went to stderr and is not a diff,
            // so it may be shown but never cut from.
            string message = result.AllOutput ?? string.Empty;
            return new DiffLoad(message, string.Empty);
        }

        return untracked ? Clamp(stdout) : new DiffLoad(stdout, stdout);
    }

    /// <summary>
    ///  Keeps a whole-file "diff" inside the display budget. Truncation happens on a
    ///  line boundary and drops the patch source, so the truncated text can never be
    ///  turned into a patch.
    /// </summary>
    private static DiffLoad Clamp(string diff)
    {
        int cut = -1;
        int lines = 0;
        for (int i = 0; i < diff.Length; i++)
        {
            if (diff[i] != '\n')
            {
                continue;
            }

            lines++;
            if (lines > UntrackedMaxLines || i + 1 > UntrackedMaxChars)
            {
                cut = i + 1;
                break;
            }
        }

        if (cut < 0 && diff.Length <= UntrackedMaxChars)
        {
            return new DiffLoad(diff, diff);
        }

        if (cut < 0)
        {
            // One enormous line: there is no boundary to cut on.
            cut = UntrackedMaxChars;
        }

        return new DiffLoad(diff[..cut] + TruncatedMarker + "\n", string.Empty);
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
    /// <param name="isUntracked">
    ///  The file is not in the index at all (an untracked work-tree file, diffed
    ///  against <c>/dev/null</c> by <see cref="LoadDiff"/>). Its patch must KEEP the
    ///  <c>--- /dev/null</c> / <c>new file mode</c> header so that <c>git apply
    ///  --cached</c> creates the index entry, which is the opposite of what
    ///  <paramref name="isNewFile"/> asks <see cref="PatchManager"/> to do.
    /// </param>
    public static PatchStagingResult Apply(
        string repoPath,
        string diffText,
        int selectionStart,
        int selectionLength,
        PatchStagingAction action,
        bool isNewFile,
        bool isRenamed,
        bool isUntracked = false)
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

        if (isUntracked && action != PatchStagingAction.Stage)
        {
            // There is nothing in the index to take lines back out of, and undoing
            // part of a file git does not know about would need a blob written to the
            // object database first (upstream's GetSelectedLinesAsNewPatch). Say so
            // rather than letting `git apply` fail with "already exists".
            return new PatchStagingResult(false, UntrackedOnlyStageMessage);
        }

        byte[]? patch;
        try
        {
            patch = action switch
            {
                // Work tree -> index. The "a" side of the diff is the index, so the
                // patch is built with isIndex: false and applied forward.
                //
                // isNewFile is deliberately NOT passed for an untracked file:
                // PatchManager's new-file fix-up turns "--- /dev/null" into
                // "--- a/<name>" and drops "new file mode", which is right when the
                // INDEX already has the file (partial unstage) but produces a patch
                // git refuses for a path that is in no index at all.
                PatchStagingAction.Stage => PatchManager.GetSelectedLinesAsPatch(
                    diffText, selectionStart, selectionLength,
                    isIndex: false, PatchEncoding, reset: false, isNewFile && !isUntracked, isRenamed),

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

        // `--index` makes git check the patch against the index entry as well, which
        // is what keeps a partial stage honest — but an UNTRACKED file has no index
        // entry at all, so it fails outright with "does not exist in index". Staging
        // lines of a brand-new file therefore goes in with `--cached` only, which
        // creates the entry (the file ends up "AM": part of it in the index, the rest
        // still only in the work tree). Unstaging is unaffected: a file that is new on
        // the index side IS in the index.
        string index = isUntracked ? string.Empty : " --index";
        string args = action switch
        {
            PatchStagingAction.Stage => $"apply --cached{index} --whitespace=nowarn",
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

    public const string UntrackedOnlyStageMessage =
        "This file is not tracked yet: only staging lines of it is possible.";

    /// <summary>Appended to a clamped whole-file view, like upstream's file viewer.</summary>
    public const string TruncatedMarker = "[Truncated]";
}
