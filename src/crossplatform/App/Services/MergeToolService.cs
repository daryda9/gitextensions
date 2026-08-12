using System.Text;
using GitCommands;
using GitExtUtils;
using GitExtensions.Extensibility;

namespace GitExtensions.Avalonia.Services;

/// <summary>What one region of a three-way merge is.</summary>
public enum MergeChunkKind
{
    /// <summary>
    ///  Text git merged on its own: either untouched by both sides, or changed by
    ///  exactly one of them. It needs no decision and is copied to the result.
    /// </summary>
    Stable,

    /// <summary>Both sides changed the same region differently: the user decides.</summary>
    Conflict,
}

/// <summary>
///  One region of the merged document.
///
///  <para>For <see cref="MergeChunkKind.Stable"/> only <see cref="Text"/> is
///  filled. For <see cref="MergeChunkKind.Conflict"/> the three competing
///  versions are carried instead, so the editor can offer them as buttons rather
///  than making the user retype a side by hand.</para>
/// </summary>
public sealed record MergeChunk(
    MergeChunkKind Kind,
    IReadOnlyList<string> Text,
    IReadOnlyList<string> Ours,
    IReadOnlyList<string> Base,
    IReadOnlyList<string> Theirs)
{
    /// <summary>A region git merged by itself.</summary>
    public static MergeChunk Stable(IReadOnlyList<string> text)
        => new(MergeChunkKind.Stable, text, [], [], []);

    /// <summary>A region only the user can settle.</summary>
    public static MergeChunk Conflict(
        IReadOnlyList<string> ours, IReadOnlyList<string> @base, IReadOnlyList<string> theirs)
        => new(MergeChunkKind.Conflict, [], ours, @base, theirs);
}

/// <summary>
///  A conflicted file prepared for the built-in editor: the three input versions
///  as whole texts (what the reference panes show) plus the merged document split
///  into <see cref="Chunks"/>.
/// </summary>
public sealed record MergeDocument(
    string Path,
    IReadOnlyList<string> OursLines,
    IReadOnlyList<string> BaseLines,
    IReadOnlyList<string> TheirsLines,
    IReadOnlyList<MergeChunk> Chunks,
    Encoding Encoding,
    bool UseCrLf,
    bool EndsWithNewline)
{
    /// <summary>How many regions still need a decision.</summary>
    public int ConflictCount => Chunks.Count(c => c.Kind == MergeChunkKind.Conflict);
}

/// <summary>
///  The backend of the built-in three-way merge editor: it turns one unmerged
///  index entry into a <see cref="MergeDocument"/> and writes the settled result
///  back into the work tree and the index.
///
///  <para><b>The merge itself is git's, not ours.</b> The three stages are written
///  to temporary files and handed to <c>git merge-file --diff3</c>, which is the
///  very engine <c>git merge</c> uses for a file. Re-implementing diff3 here would
///  mean a second, subtly different answer living next to git's for the rest of
///  the project's life; the only thing this class adds is <b>structure</b> — it
///  parses the marker blocks back into typed chunks so the editor can offer
///  per-conflict buttons instead of leaving the user to delete
///  <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> lines by hand.</para>
///
///  <para><c>--diff3</c> rather than the default two-way markers: the base is what
///  makes a conflict readable — without it you see two versions and no way to tell
///  which side <i>added</i> and which side <i>removed</i>.</para>
///
///  <para>All methods block; call them from <see cref="Task.Run"/>.</para>
/// </summary>
public sealed class MergeToolService
{
    private const string OursLabel = "LOCAL";
    private const string BaseLabel = "BASE";
    private const string TheirsLabel = "REMOTE";

    // The marker length git writes with --marker-size=7, i.e. its default.
    private const int MarkerSize = 7;

    /// <summary>
    ///  Builds the merge document for <paramref name="entry"/>, or returns a
    ///  message explaining why it cannot be built (binary content, a missing
    ///  stage, a submodule pointer, a git failure).
    ///
    ///  <para>The temporary files are deleted before returning: everything the
    ///  editor needs is in memory by then, and leaving three copies of the user's
    ///  source in <c>/tmp</c> for the lifetime of a window is not a trade this
    ///  needs to make.</para>
    /// </summary>
    public async Task<(MergeDocument? Document, string? Error)> PrepareAsync(string repoPath, ConflictEntry entry)
    {
        if (!entry.CanThreeWayMerge)
        {
            return (null, entry.IsSubmodule
                ? "This is a submodule pointer, not text: pick a side instead."
                : "One of the three versions does not exist, so there is nothing to merge line by line. "
                    + "Pick a side instead.");
        }

        GitModule module = GitContext.CreateModule(repoPath);
        Encoding encoding = module.FilesEncoding;

        string dir = Path.Combine(Path.GetTempPath(), "gitext-merge-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(dir);

            string ours = Path.Combine(dir, "LOCAL");
            string @base = Path.Combine(dir, "BASE");
            string theirs = Path.Combine(dir, "REMOTE");

            // The stage shas come from ls-files --unmerged, so all three are known
            // to exist here; CanThreeWayMerge above is what guarantees it.
            await module.SaveBlobAsAsync(ours, entry.Ours.Sha!, CancellationToken.None);
            await module.SaveBlobAsAsync(@base, entry.Base.Sha!, CancellationToken.None);
            await module.SaveBlobAsAsync(theirs, entry.Theirs.Sha!, CancellationToken.None);

            byte[] ourBytes = File.ReadAllBytes(ours);
            if (IsBinary(ourBytes) || IsBinary(File.ReadAllBytes(@base)) || IsBinary(File.ReadAllBytes(theirs)))
            {
                return (null, "At least one version is binary. A line-by-line merge cannot say anything "
                    + "useful about it — pick a side instead.");
            }

            // git merge-file writes the result over its FIRST argument unless -p is
            // given. Writing to the file (and reading the bytes back) keeps the
            // result out of the console pipeline, so nothing re-encodes it on the
            // way: the bytes are decoded once, here, with the repository's own
            // files encoding.
            GitArgumentBuilder args = new("merge-file")
            {
                $"--marker-size={MarkerSize}",
                "--diff3",
                "-L",
                OursLabel,
                "-L",
                BaseLabel,
                "-L",
                TheirsLabel,
                ours.Quote(),
                @base.Quote(),
                theirs.Quote(),
            };

            // Exit status is the number of conflicts, so a non-zero code is the
            // normal case here and must not be read as failure. Only the negative
            // code (git reports 255) means the merge could not be attempted.
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            if (result.ExitCode is not int code || code is < 0 or > 127)
            {
                return (null, "git merge-file could not merge this file.\n\n" + result.AllOutput);
            }

            string merged = encoding.GetString(File.ReadAllBytes(ours));

            return (new MergeDocument(
                entry.Path,
                SplitLines(encoding.GetString(ourBytes)),
                SplitLines(encoding.GetString(File.ReadAllBytes(@base))),
                SplitLines(encoding.GetString(File.ReadAllBytes(theirs))),
                Parse(SplitLines(merged)),
                encoding,
                UseCrLf: DominantEolIsCrLf(ourBytes),

                // Whether the file ends in a newline is a property of the FILE, not
                // a convention to impose: one deliberately written without a final
                // newline (git says "\ No newline at end of file" about it) has to
                // come back out that way, or every merge of it adds a line nobody
                // asked for.
                //
                // Read from OUR blob and not from the merged text, because
                // git merge-file always terminates its output — it has to, the
                // closing ">>>>>>>" needs a line of its own — so the merged text
                // says "yes" even when the file said no. Measured, not assumed:
                // merging three files that all end without a newline gives output
                // ending "> t.txt\n".
                EndsWithNewline: ourBytes.Length == 0 || ourBytes[^1] == (byte)'\n'), null);
        }
        catch (Exception ex)
        {
            return (null, $"Could not prepare the merge: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A leftover temp directory is not worth failing a merge over.
            }
        }
    }

    /// <summary>
    ///  Writes <paramref name="text"/> into the work-tree file and stages it, which
    ///  is what marks the path resolved. Line endings are normalised to whatever
    ///  the local version used, so a merge does not silently rewrite every line of
    ///  a CRLF file (or of an LF one on a CRLF checkout).
    /// </summary>
    public ConflictActionResult Save(string repoPath, MergeDocument document, string text)
    {
        try
        {
            string normalised = text.Replace("\r\n", "\n");

            // SplitLines dropped the trailing newline when the document was built,
            // so the editor never shows it and cannot be blamed for its absence.
            // It is restored here ONLY if the file had one: see EndsWithNewline.
            if (document.EndsWithNewline && normalised.Length > 0 && !normalised.EndsWith('\n'))
            {
                normalised += "\n";
            }

            if (document.UseCrLf)
            {
                normalised = normalised.Replace("\n", "\r\n");
            }

            string full = Path.Combine(repoPath, document.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, document.Encoding.GetBytes(normalised));
        }
        catch (Exception ex)
        {
            return new ConflictActionResult(false, $"Could not write {document.Path}: {ex.Message}");
        }

        return new ConflictService().MarkResolved(repoPath, document.Path);
    }

    /// <summary>
    ///  Splits merged output into the marker blocks git wrote and the runs of text
    ///  between them.
    ///
    ///  <para>The shape recognised is exactly the one <c>--diff3</c> produces:
    ///  <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>, ours, <c>|||||||</c>, base,
    ///  <c>=======</c>, theirs, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>. A block that
    ///  runs off the end of the file without its closing marker — which git does
    ///  not write, but a file the user has already hand-edited can contain — is
    ///  kept as plain text rather than dropped, because losing a user's lines to a
    ///  parser is the one outcome a merge tool may never have.</para>
    /// </summary>
    internal static IReadOnlyList<MergeChunk> Parse(IReadOnlyList<string> merged)
    {
        List<MergeChunk> chunks = [];
        List<string> stable = [];

        for (int i = 0; i < merged.Count; i++)
        {
            if (!IsMarker(merged[i], '<'))
            {
                stable.Add(merged[i]);
                continue;
            }

            int mid = Find(merged, i + 1, '|');
            int sep = Find(merged, mid < 0 ? i + 1 : mid + 1, '=');
            int end = Find(merged, sep < 0 ? i + 1 : sep + 1, '>');
            if (sep < 0 || end < 0)
            {
                stable.Add(merged[i]);
                continue;
            }

            if (stable.Count > 0)
            {
                chunks.Add(MergeChunk.Stable([.. stable]));
                stable.Clear();
            }

            // Without --diff3 there is no ||||||| line; treat the base as empty
            // rather than refusing, so the parser also survives a file whose
            // markers were written with a different merge.conflictStyle.
            int oursEnd = mid < 0 ? sep : mid;
            chunks.Add(MergeChunk.Conflict(
                Slice(merged, i + 1, oursEnd),
                mid < 0 ? [] : Slice(merged, mid + 1, sep),
                Slice(merged, sep + 1, end)));

            i = end;
        }

        if (stable.Count > 0)
        {
            chunks.Add(MergeChunk.Stable([.. stable]));
        }

        return chunks;
    }

    private static int Find(IReadOnlyList<string> lines, int from, char marker)
    {
        for (int i = Math.Max(from, 0); i < lines.Count; i++)
        {
            if (IsMarker(lines[i], marker))
            {
                return i;
            }

            // A nested "<<<<<<<" means the block never closed; stop rather than
            // pairing markers across two different conflicts.
            if (marker != '<' && IsMarker(lines[i], '<'))
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    ///  A conflict marker is <see cref="MarkerSize"/> repetitions of the character
    ///  followed by end-of-line or a space (the label). The length test matters:
    ///  a line of <c>=====</c> underlining a heading in Markdown is not a marker.
    /// </summary>
    private static bool IsMarker(string line, char marker)
    {
        if (line.Length < MarkerSize)
        {
            return false;
        }

        for (int i = 0; i < MarkerSize; i++)
        {
            if (line[i] != marker)
            {
                return false;
            }
        }

        return line.Length == MarkerSize || line[MarkerSize] == ' ';
    }

    private static string[] Slice(IReadOnlyList<string> lines, int start, int end)
    {
        string[] slice = new string[Math.Max(end - start, 0)];
        for (int i = 0; i < slice.Length; i++)
        {
            slice[i] = lines[start + i];
        }

        return slice;
    }

    /// <summary>
    ///  Splits into lines without inventing a trailing empty one: a file ending in
    ///  a newline has that newline restored on save by the join, so counting it as
    ///  an extra line here would add a blank line on every round trip.
    /// </summary>
    internal static IReadOnlyList<string> SplitLines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>
    ///  Whether a NUL byte appears in the first 8000 bytes — the same test git
    ///  itself uses to call content binary.
    /// </summary>
    private static bool IsBinary(byte[] data)
        => Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 8000)) >= 0;

    private static bool DominantEolIsCrLf(byte[] data)
    {
        int crlf = 0;
        int lf = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n')
            {
                continue;
            }

            if (i > 0 && data[i - 1] == (byte)'\r')
            {
                crlf++;
            }
            else
            {
                lf++;
            }
        }

        return crlf > lf;
    }
}
