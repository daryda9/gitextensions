using System.Text;
using GitCommands;
using GitExtUtils;
using GitExtensions.Extensibility;

namespace GitExtensions.Avalonia.Services;

/// <summary>What one row of a side-by-side comparison is.</summary>
public enum DiffRowKind
{
    /// <summary>Present and identical on both sides.</summary>
    Equal,

    /// <summary>Present on both sides and different: a replaced line.</summary>
    Changed,

    /// <summary>Only on the left: the right side dropped it.</summary>
    Removed,

    /// <summary>Only on the right: the left side did not have it.</summary>
    Added,
}

/// <summary>
///  One aligned row. A side is <see langword="null"/> when the row exists only on
///  the other one — that is the filler that keeps the two panes level with each
///  other, and it is why the line numbers cannot come from the document.
/// </summary>
public sealed record DiffRow(DiffRowKind Kind, string? Left, string? Right, int LeftLine, int RightLine);

/// <summary>
///  Two versions of one file, aligned line by line for a side-by-side view.
/// </summary>
public sealed record DiffDocument(
    string Path,
    string LeftLabel,
    string RightLabel,
    IReadOnlyList<DiffRow> Rows)
{
    /// <summary>Row indexes where a run of non-equal rows begins.</summary>
    public IReadOnlyList<int> Hunks { get; } = Starts(Rows);

    private static int[] Starts(IReadOnlyList<DiffRow> rows)
    {
        List<int> starts = [];
        bool inside = false;
        for (int i = 0; i < rows.Count; i++)
        {
            bool changed = rows[i].Kind != DiffRowKind.Equal;
            if (changed && !inside)
            {
                starts.Add(i);
            }

            inside = changed;
        }

        return [.. starts];
    }
}

/// <summary>
///  The backend of the built-in difftool: it takes two versions of a file and
///  returns them aligned, so the app can compare without shelling out to
///  <c>git difftool</c>.
///
///  <para><b>The diff is git's, exactly as the merge is</b>
///  (<see cref="MergeToolService"/>). The two versions are written to temporary
///  files and compared with <c>git diff --no-index -U0</c>; only the hunk headers
///  are read back, and the alignment is built from them. Two reasons for
///  <c>--no-index</c> rather than a revision-range diff: it is the same code path
///  whichever side is a commit, the work tree, or nothing at all (an added or
///  deleted file), and it keeps this class from having to know about renames —
///  the caller already resolved which blob is which.</para>
///
///  <para><c>-U0</c> because context lines are not wanted here: the view shows the
///  whole file, so every line outside a hunk is context by construction.</para>
/// </summary>
public sealed class DiffToolService
{
    /// <summary>
    ///  Aligns <paramref name="left"/> against <paramref name="right"/>.
    ///  <paramref name="histogram"/> mirrors the user's diff preference, so the
    ///  built-in view splits hunks the same way the patch pane does.
    /// </summary>
    public async Task<(DiffDocument? Document, string? Error)> PrepareAsync(
        string repoPath,
        string path,
        string leftLabel,
        string rightLabel,
        string left,
        string right,
        bool histogram)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gitext-diff-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(dir);
            string leftFile = Path.Combine(dir, "LEFT");
            string rightFile = Path.Combine(dir, "RIGHT");
            await File.WriteAllTextAsync(leftFile, left, new UTF8Encoding(false));
            await File.WriteAllTextAsync(rightFile, right, new UTF8Encoding(false));

            GitArgumentBuilder args = new("diff")
            {
                "--no-index",
                "--no-color",
                "-U0",
                { histogram, "--histogram" },
                "--",
                leftFile.Quote(),
                rightFile.Quote(),
            };

            // --no-index exits 1 when the files differ, which is the normal case:
            // only a code above that is a real failure.
            ExecutionResult result = GitContext.CreateModule(repoPath)
                .GitExecutable.Execute(args, throwOnErrorExit: false);
            if (result.ExitCode is not int code || code > 1)
            {
                return (null, "git diff could not compare this file.\n\n" + result.AllOutput);
            }

            return (new DiffDocument(
                path,
                leftLabel,
                rightLabel,
                Align(
                    MergeToolService.SplitLines(left),
                    MergeToolService.SplitLines(right),
                    ParseHunks(result.StandardOutput))), null);
        }
        catch (Exception ex)
        {
            return (null, $"Could not compare {path}: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A leftover temp directory is not worth failing a comparison over.
            }
        }
    }

    /// <summary>One <c>@@ -a,b +c,d @@</c> header, as line ranges.</summary>
    internal readonly record struct Hunk(int OldStart, int OldCount, int NewStart, int NewCount);

    /// <summary>
    ///  Reads the hunk headers out of a unified diff.
    ///
    ///  <para>A count is omitted when it is 1 (<c>@@ -7 +7,2 @@</c>), which is the
    ///  form that trips a naive parser, and a count of <b>zero</b> shifts the
    ///  meaning of the start: for a pure insertion git writes the line the new text
    ///  goes <i>after</i>, not the line it starts at. <see cref="Align"/> handles
    ///  that; here the numbers are taken verbatim.</para>
    /// </summary>
    internal static IReadOnlyList<Hunk> ParseHunks(string diff)
    {
        List<Hunk> hunks = [];
        foreach (string line in diff.Split('\n'))
        {
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            int close = line.IndexOf("@@", 2, StringComparison.Ordinal);
            if (close < 0)
            {
                continue;
            }

            string[] parts = line[2..close].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            (int oldStart, int oldCount) = Range(parts[0]);
            (int newStart, int newCount) = Range(parts[1]);
            hunks.Add(new Hunk(oldStart, oldCount, newStart, newCount));
        }

        return hunks;

        static (int Start, int Count) Range(string text)
        {
            string body = text[1..];
            int comma = body.IndexOf(',');
            if (comma < 0)
            {
                return (int.TryParse(body, out int only) ? only : 0, 1);
            }

            return (
                int.TryParse(body[..comma], out int start) ? start : 0,
                int.TryParse(body[(comma + 1)..], out int count) ? count : 0);
        }
    }

    /// <summary>
    ///  Builds the aligned rows from the two files and the hunks between them.
    ///
    ///  <para>Inside a hunk the two sides are paired row by row for as far as both
    ///  reach — those rows are <see cref="DiffRowKind.Changed"/>, a replacement —
    ///  and whatever is left over on one side becomes
    ///  <see cref="DiffRowKind.Removed"/> or <see cref="DiffRowKind.Added"/> against
    ///  filler. Pairing rather than stacking all removals above all additions is
    ///  what puts a changed line opposite the line it changed from, which is the
    ///  whole point of looking at two panes instead of a patch.</para>
    /// </summary>
    internal static IReadOnlyList<DiffRow> Align(
        IReadOnlyList<string> left, IReadOnlyList<string> right, IReadOnlyList<Hunk> hunks)
    {
        List<DiffRow> rows = [];
        int l = 0;
        int r = 0;

        foreach (Hunk hunk in hunks)
        {
            // A zero-length old range means "insert after this line", so the equal
            // run includes it; otherwise the hunk starts ON that line.
            int stop = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            while (l < stop && l < left.Count && r < right.Count)
            {
                rows.Add(new DiffRow(DiffRowKind.Equal, left[l], right[r], l + 1, r + 1));
                l++;
                r++;
            }

            int paired = Math.Min(hunk.OldCount, hunk.NewCount);
            for (int i = 0; i < paired && l < left.Count && r < right.Count; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Changed, left[l], right[r], l + 1, r + 1));
                l++;
                r++;
            }

            for (int i = paired; i < hunk.OldCount && l < left.Count; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Removed, left[l], null, l + 1, 0));
                l++;
            }

            for (int i = paired; i < hunk.NewCount && r < right.Count; i++)
            {
                rows.Add(new DiffRow(DiffRowKind.Added, null, right[r], 0, r + 1));
                r++;
            }
        }

        while (l < left.Count && r < right.Count)
        {
            rows.Add(new DiffRow(DiffRowKind.Equal, left[l], right[r], l + 1, r + 1));
            l++;
            r++;
        }

        // Anything still left is a tail git did not report as a hunk, which happens
        // when one side simply ends earlier. Showing it beats dropping it.
        while (l < left.Count)
        {
            rows.Add(new DiffRow(DiffRowKind.Removed, left[l], null, l + 1, 0));
            l++;
        }

        while (r < right.Count)
        {
            rows.Add(new DiffRow(DiffRowKind.Added, null, right[r], 0, r + 1));
            r++;
        }

        return rows;
    }
}
