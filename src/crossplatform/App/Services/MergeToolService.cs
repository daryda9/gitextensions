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
///  Why a conflict is decidable without asking anybody — that is, why the two
///  sides say the same thing in two spellings rather than two different things.
///
///  <para>Every member here has to survive one question: <b>can taking the
///  proposed side lose meaning?</b> If the answer is "it depends on the
///  language", the case does not belong in this enum. That is why nothing that
///  changes a non-whitespace character is listed: one comma is a real
///  disagreement and only the user may settle it.</para>
/// </summary>
public enum TrivialKind
{
    /// <summary>Not trivial: the two sides really do differ.</summary>
    None,

    /// <summary>
    ///  The two sides are the same text once line terminators are normalised —
    ///  CRLF against LF. The parser splits on <c>\n</c> after folding
    ///  <c>\r\n</c> away, so such a conflict reaches us with two byte-identical
    ///  sides: git conflicted on the <c>\r</c> we no longer carry.
    /// </summary>
    LineEnding,

    /// <summary>Same lines but for spaces or tabs at the end of them.</summary>
    TrailingWhitespace,

    /// <summary>
    ///  Same lines but for the <i>amount</i> of whitespace — reindentation, a
    ///  tab against four spaces, realigned columns. Whitespace that is there on
    ///  one side is there on the other too; only its width changed. Presence is
    ///  never equated with absence, so an indented line and a flush one stay a
    ///  real conflict (in Python that difference is the program).
    /// </summary>
    Whitespace,

    /// <summary>
    ///  Same lines but for blank ones added or removed around them.
    /// </summary>
    BlankLines,

    /// <summary>
    ///  One side is literally the base: it changed nothing here, so the other
    ///  side is the whole of the change and taking it is what a clean merge
    ///  would have done by itself. This is the case where a side is empty and
    ///  the other never moved — a deletion git only flagged because a
    ///  neighbouring hunk touched the same block.
    /// </summary>
    OneSideUnchanged,
}

/// <summary>Which side a <see cref="TrivialKind"/> proposes taking.</summary>
public enum TrivialResolution
{
    /// <summary>Nothing is proposed.</summary>
    None,

    /// <summary>Our version.</summary>
    Ours,

    /// <summary>Their version.</summary>
    Theirs,
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
    /// <summary>Why this conflict needs no decision, or <see cref="TrivialKind.None"/>.</summary>
    public TrivialKind Trivial { get; init; }

    /// <summary>The side <see cref="Trivial"/> proposes taking.</summary>
    public TrivialResolution Proposed { get; init; }

    /// <summary>A region git merged by itself.</summary>
    public static MergeChunk Stable(IReadOnlyList<string> text)
        => new(MergeChunkKind.Stable, text, [], [], []);

    /// <summary>A region only the user can settle.</summary>
    public static MergeChunk Conflict(
        IReadOnlyList<string> ours, IReadOnlyList<string> @base, IReadOnlyList<string> theirs)
    {
        (TrivialKind kind, TrivialResolution proposed) = TrivialConflict.Classify(ours, @base, theirs);
        return new(MergeChunkKind.Conflict, [], ours, @base, theirs)
        {
            Trivial = kind,
            Proposed = proposed,
        };
    }
}

/// <summary>
///  Decides whether a conflict is a disagreement or only two spellings of the
///  same thing.
///
///  <para><b>Why this exists at all.</b> A rebase over a reindented branch, or a
///  colleague whose editor strips trailing spaces, produces conflicts that carry
///  no decision: answering them one by one is noise, and noise is what hides the
///  two or three conflicts that are real. So the classification is not a
///  convenience, it is a way of raising the signal.</para>
///
///  <para><b>Nothing here acts.</b> The class answers a question; whether an
///  answer is applied is the window's business, and only when the user asks for
///  it. A merge tool that quietly rewrote regions on open would be teaching the
///  user not to trust what the screen says.</para>
///
///  <para><b>When both sides are equivalent the proposal is LOCAL.</b> Not a
///  coin toss: LOCAL is the user's own side, the bytes already sitting in the
///  work tree. Choosing it means the file on disk keeps the shape its owner has
///  been looking at all along, and the whitespace convention that survives the
///  merge is the one of the tree being merged INTO — which is the same rule git
///  itself follows everywhere else it has to break a tie.</para>
/// </summary>
internal static class TrivialConflict
{
    /// <summary>
    ///  Classifies one conflict. The order of the tests is the order of
    ///  specificity: the narrowest description of a difference wins, so a
    ///  conflict that is only trailing spaces is never reported as the vaguer
    ///  "spacing".
    /// </summary>
    internal static (TrivialKind Kind, TrivialResolution Proposed) Classify(
        IReadOnlyList<string> ours, IReadOnlyList<string> @base, IReadOnlyList<string> theirs)
    {
        if (Same(ours, theirs, static line => line))
        {
            return (TrivialKind.LineEnding, TrivialResolution.Ours);
        }

        if (Same(ours, theirs, static line => line.TrimEnd(' ', '\t', '\r')))
        {
            return (TrivialKind.TrailingWhitespace, TrivialResolution.Ours);
        }

        if (Same(ours, theirs, Squeeze))
        {
            return (TrivialKind.Whitespace, TrivialResolution.Ours);
        }

        if (Same(WithoutBlanks(ours), WithoutBlanks(theirs), static line => line))
        {
            return (TrivialKind.BlankLines, TrivialResolution.Ours);
        }

        // Checked last because it is the only rule that lets non-whitespace text
        // differ, and it may only do so because one of the two sides is provably
        // silent: it repeats the ancestor line for line.
        if (Same(ours, @base, static line => line))
        {
            return (TrivialKind.OneSideUnchanged, TrivialResolution.Theirs);
        }

        if (Same(theirs, @base, static line => line))
        {
            return (TrivialKind.OneSideUnchanged, TrivialResolution.Ours);
        }

        return (TrivialKind.None, TrivialResolution.None);
    }

    private static bool Same(
        IReadOnlyList<string> left, IReadOnlyList<string> right, Func<string, string> normalise)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(normalise(left[i]), normalise(right[i]), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///  Every run of spaces and tabs becomes one space, and a run at the end of
    ///  the line disappears.
    ///
    ///  <para>Collapsing rather than deleting is the whole safety of the test:
    ///  <c>a b</c> and <c>ab</c> stay different (one is two tokens, the other is
    ///  one), while <c>a  b</c> and <c>a\tb</c> become the same. The leading run
    ///  is collapsed and not dropped for the same reason — indentation may be
    ///  reflowed, but an indented line is not an unindented one.</para>
    /// </summary>
    private static string Squeeze(string line)
    {
        StringBuilder result = new(line.Length);
        bool pending = false;

        foreach (char c in line)
        {
            if (c is ' ' or '\t' or '\r')
            {
                pending = true;
                continue;
            }

            if (pending)
            {
                result.Append(' ');
                pending = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private static IReadOnlyList<string> WithoutBlanks(IReadOnlyList<string> lines)
        => [.. lines.Where(static line => line.Trim(' ', '\t', '\r').Length > 0)];
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

/// <summary>Why the built-in three-way merge refuses a file.</summary>
public enum MergeRefusalReason
{
    /// <summary>The path is a gitlink: the conflict is which commit, not which text.</summary>
    Submodule,

    /// <summary>
    ///  One of the three stages is absent (add/add, delete/modify): there is no
    ///  ancestor to merge against, so the question is "keep it or drop it".
    /// </summary>
    MissingStage,

    /// <summary>At least one side is binary, so lines are not a unit of meaning.</summary>
    Binary,

    /// <summary>A side is larger than <see cref="MergeToolService.MaxMergeBytes"/>.</summary>
    TooLarge,
}

/// <summary>
///  What is known about one side of a refused merge — enough for a user to choose
///  between the two <i>without opening anything</i>, which is the whole point: a
///  refusal that only says "no" leaves them stuck.
/// </summary>
/// <param name="Exists">Whether the stage is present at all.</param>
/// <param name="Sha">The stage's blob (or commit, for a gitlink) id.</param>
/// <param name="Size">Size of the blob in bytes; 0 when absent.</param>
/// <param name="ContentType">A human name for what the bytes are ("PNG image").</param>
/// <param name="ImageFormat">
///  The image format detected from the leading bytes, or null. Sniffed from the
///  content and never from the extension: a <c>.dat</c> written by a camera is
///  still a JPEG, and a <c>.png</c> produced by a build step is often not one.
/// </param>
/// <param name="Date">
///  When this side last changed the path, from the commit that did it. Null when
///  it cannot be attributed (no such ref, or the path is new on that side).
/// </param>
public sealed record MergeSideFacts(
    bool Exists,
    string? Sha,
    long Size,
    string ContentType,
    string? ImageFormat,
    DateTimeOffset? Date)
{
    /// <summary>An absent stage.</summary>
    public static readonly MergeSideFacts Missing = new(false, null, 0, "absent", null, null);

    /// <summary>Whether these bytes can be shown as a picture.</summary>
    public bool IsImage => ImageFormat is not null;
}

/// <summary>
///  A typed "no" from <see cref="MergeToolService"/>: the reason, the sentence to
///  put in front of the user, and the facts the UI needs in order to <b>offer the
///  ways out</b> rather than only report the refusal.
///
///  <para>The reason is an enum and not a parsed string on purpose — the caller
///  branches on it (a submodule goes to the commit chooser, two images go to the
///  image comparison, a plain binary goes to the two side buttons), and branching
///  on prose is how a UI silently stops working when a message is reworded.</para>
/// </summary>
public sealed record MergeRefusal(
    MergeRefusalReason Reason,
    string Message,
    MergeSideFacts Base,
    MergeSideFacts Ours,
    MergeSideFacts Theirs)
{
    /// <summary>
    ///  True when at least one side is a picture, which is when comparing them as
    ///  images has something to show. One is enough: an image added on one side and
    ///  absent on the other is exactly the case where seeing it decides the matter.
    /// </summary>
    public bool AnySideIsImage => Ours.IsImage || Theirs.IsImage;
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
    ///  How much of a side is read to decide what it is. git itself looks at the
    ///  first 8000 bytes to call content binary, and every magic number worth
    ///  knowing is in the first few of them.
    /// </summary>
    private const int SniffBytes = 8000;

    /// <summary>
    ///  The largest side the built-in editor will attempt. Above this the editor is
    ///  not wrong, it is unusable: the whole document is held as lines in memory and
    ///  re-highlighted on every keystroke, so a 100 MB "text" file (a generated dump,
    ///  a database export) freezes the window instead of merging anything.
    /// </summary>
    public const long MaxMergeBytes = 20L * 1024 * 1024;

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
            // The same sentence the typed refusal carries, so the two paths through
            // this class never disagree about what the user is told.
            return (null, (await InspectAsync(repoPath, entry))?.Message
                ?? "This file cannot be merged line by line. Pick a side instead.");
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
            if (IsBinary(ourBytes) || IsBinary(File.ReadAllBytes(@base)) || IsBinary(File.ReadAllBytes(theirs))
                || ourBytes.LongLength > MaxMergeBytes
                || new FileInfo(theirs).Length > MaxMergeBytes)
            {
                return (null, (await InspectAsync(repoPath, entry))?.Message
                    ?? "This file cannot be merged line by line. Pick a side instead.");
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
    ///  Answers "can the built-in editor open this?" — <see langword="null"/> when it
    ///  can, a typed <see cref="MergeRefusal"/> when it cannot.
    ///
    ///  <para>Separate from <see cref="PrepareAsync"/> because a caller that means to
    ///  offer alternatives has to know the answer <b>before</b> opening a window, and
    ///  because the refusal costs three blob reads while a merge costs a merge. The
    ///  facts are gathered even for a "no": they are what the alternatives are built
    ///  from — sizes and dates make the choice between the two sides an informed one
    ///  instead of a coin toss, and the sniffed image format is what decides whether
    ///  comparing them as pictures is on offer.</para>
    /// </summary>
    public async Task<MergeRefusal?> InspectAsync(string repoPath, ConflictEntry entry)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        if (entry.IsSubmodule)
        {
            // Nothing is materialised: a gitlink stage names a commit, and there is no
            // blob behind it to read, size or sniff.
            return new MergeRefusal(
                MergeRefusalReason.Submodule,
                "This is not a file but a link to another repository: what disagrees is which "
                    + "commit of it this project should use, so there are no lines to merge.",
                Pointer(entry.Base),
                Pointer(entry.Ours),
                Pointer(entry.Theirs));
        }

        string dir = Path.Combine(Path.GetTempPath(), "gitext-inspect-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(dir);

            // The dates are two extra git processes and they are worth it: "keep the
            // 4 MB one from last Tuesday" is a decision, "keep LOCAL" is a guess.
            string? theirsRev = FirstExistingRev(module, "MERGE_HEAD", "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD");
            MergeSideFacts @base = await FactsAsync(module, dir, "BASE", entry.Base, null, entry.Path);
            MergeSideFacts ours = await FactsAsync(module, dir, "LOCAL", entry.Ours, "HEAD", entry.Path);
            MergeSideFacts theirs = await FactsAsync(module, dir, "REMOTE", entry.Theirs, theirsRev, entry.Path);

            if (!entry.Base.Exists || !entry.Ours.Exists || !entry.Theirs.Exists)
            {
                // A three-way merge needs a common ancestor to attribute changes to.
                // Without one, "merge" would mean "guess", and the honest question is
                // the simpler one the buttons already ask.
                return new MergeRefusal(
                    MergeRefusalReason.MissingStage,
                    entry.Base.Exists
                        ? "One of you deleted this file while the other was changing it. There is no third "
                            + "version to merge the two into: the question is whether the file stays or goes."
                        : "Both sides created this file independently, so there is no older version they "
                            + "both came from — and without it there is no way to tell which lines are the "
                            + "change and which were always there.",
                    @base,
                    ours,
                    theirs);
            }

            if (ours.Size > MaxMergeBytes || theirs.Size > MaxMergeBytes || @base.Size > MaxMergeBytes)
            {
                return new MergeRefusal(
                    MergeRefusalReason.TooLarge,
                    "This file is too big to edit here: the merge editor keeps the whole text in memory "
                        + "and would stop responding instead of helping you.",
                    @base,
                    ours,
                    theirs);
            }

            if (IsBinaryFile(Path.Combine(dir, "LOCAL"))
                || IsBinaryFile(Path.Combine(dir, "REMOTE"))
                || IsBinaryFile(Path.Combine(dir, "BASE")))
            {
                return new MergeRefusal(
                    MergeRefusalReason.Binary,

                    // An image gets its own sentence: "not text" is true of it but
                    // unhelpful, and the useful thing to say is that the two can be
                    // looked at, which for this one file is a real way forward.
                    ours.IsImage || theirs.IsImage
                        ? "These two versions are pictures. There are no lines to combine — look at them "
                            + "side by side and keep the one you want."
                        : "This file is not text: it has no lines to put side by side, and combining two "
                            + "versions of it line by line would produce a file no program can open.",
                    @base,
                    ours,
                    theirs);
            }

            return null;
        }
        catch (Exception ex)
        {
            // A failure to inspect is not a refusal to merge: the caller falls through
            // to the editor, which reports its own error if it hits the same problem.
            _ = ex;
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A leftover temp directory is not worth failing over.
            }
        }
    }

    /// <summary>
    ///  The raw bytes of one stage, for a viewer that needs the content itself — the
    ///  image comparison. Null when the stage is absent, which the viewer treats as
    ///  "this version does not exist" rather than as an error.
    /// </summary>
    public async Task<byte[]?> ReadStageAsync(string repoPath, ConflictSide side)
    {
        if (!side.Exists || side.Sha is not { Length: > 0 } sha || side.IsSubmodule)
        {
            return null;
        }

        string dir = Path.Combine(Path.GetTempPath(), "gitext-stage-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "stage");
            await GitContext.CreateModule(repoPath).SaveBlobAsAsync(file, sha, CancellationToken.None);
            return File.ReadAllBytes(file);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // As above.
            }
        }
    }

    /// <summary>
    ///  The image format the leading bytes announce, or null. <b>Content, never the
    ///  extension</b>: the extension is a claim by whoever named the file, the magic
    ///  number is what every decoder actually dispatches on.
    /// </summary>
    public static string? DetectImageFormat(byte[]? data)
    {
        if (data is null || data.Length < 12)
        {
            return null;
        }

        if (Starts(data, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return "PNG";
        }

        if (Starts(data, 0xFF, 0xD8, 0xFF))
        {
            return "JPEG";
        }

        if (Starts(data, (byte)'G', (byte)'I', (byte)'F', (byte)'8'))
        {
            return "GIF";
        }

        if (Starts(data, (byte)'B', (byte)'M'))
        {
            return "BMP";
        }

        // RIFF....WEBP — the four size bytes in between are why this one is not a
        // straight prefix test.
        if (Starts(data, (byte)'R', (byte)'I', (byte)'F', (byte)'F')
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return "WEBP";
        }

        if (Starts(data, 0x00, 0x00, 0x01, 0x00))
        {
            return "ICO";
        }

        return null;
    }

    private static bool Starts(byte[] data, params byte[] prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A gitlink stage: a commit id, with nothing to read behind it.</summary>
    private static MergeSideFacts Pointer(ConflictSide side)
        => side.Exists
            ? new MergeSideFacts(true, side.Sha, 0, "commit of the linked repository", null, null)
            : MergeSideFacts.Missing;

    private static async Task<MergeSideFacts> FactsAsync(
        GitModule module, string dir, string name, ConflictSide side, string? rev, string path)
    {
        if (!side.Exists || side.Sha is not { Length: > 0 } sha)
        {
            return MergeSideFacts.Missing;
        }

        string file = Path.Combine(dir, name);
        await module.SaveBlobAsAsync(file, sha, CancellationToken.None);

        long size = new FileInfo(file).Length;
        byte[] head = ReadHead(file);
        string? image = DetectImageFormat(head);

        return new MergeSideFacts(
            true,
            sha,
            size,
            image is null ? DescribeContent(head) : image + " image",
            image,
            rev is null ? null : LastChange(module, rev, path));
    }

    /// <summary>
    ///  A name for content that is not an image, in the words of somebody looking at
    ///  a file manager rather than at a hex dump. Deliberately short of exhaustive:
    ///  the few formats that actually turn up in a conflict, and an honest "not text"
    ///  for everything else — a wrong guess would be worse than no guess.
    /// </summary>
    private static string DescribeContent(byte[] head)
    {
        if (Starts(head, (byte)'%', (byte)'P', (byte)'D', (byte)'F'))
        {
            return "PDF document";
        }

        if (Starts(head, (byte)'P', (byte)'K', 0x03, 0x04))
        {
            // Also .docx/.xlsx/.odt/.jar: they are all zip containers, and saying
            // "zip archive" about one is true rather than misleading.
            return "zip archive (or a document stored as one)";
        }

        if (Starts(head, 0x1F, 0x8B))
        {
            return "gzip archive";
        }

        if (Starts(head, 0x7F, (byte)'E', (byte)'L', (byte)'F'))
        {
            return "Linux program or library";
        }

        if (Starts(head, (byte)'O', (byte)'g', (byte)'g', (byte)'S')
            || Starts(head, (byte)'I', (byte)'D', (byte)'3')
            || Starts(head, (byte)'f', (byte)'L', (byte)'a', (byte)'C'))
        {
            return "sound file";
        }

        return IsBinary(head) ? "not text" : "text";
    }

    /// <summary>
    ///  When <paramref name="rev"/> last touched <paramref name="path"/>. A commit
    ///  date and not the file's timestamp on disk: the stage does not exist on disk,
    ///  and the question the user is really asking is "which of these two is the
    ///  newer piece of work".
    /// </summary>
    private static DateTimeOffset? LastChange(GitModule module, string rev, string path)
    {
        try
        {
            GitArgumentBuilder args = new("log")
            {
                "-1",
                "--format=%cI",
                rev,
                "--",
                path.ToPosixPath()?.Quote() ?? path.Quote(),
            };
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            string text = result.StandardOutput.Trim();
            return DateTimeOffset.TryParse(text, out DateTimeOffset when) ? when : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FirstExistingRev(GitModule module, params string[] candidates)
    {
        foreach (string rev in candidates)
        {
            try
            {
                GitArgumentBuilder args = new("rev-parse") { "--verify", "--quiet", rev };
                ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
                if (result.ExitCode == 0 && result.StandardOutput.Trim().Length > 0)
                {
                    return rev;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    private static byte[] ReadHead(string file)
    {
        using FileStream stream = File.OpenRead(file);
        byte[] buffer = new byte[SniffBytes];
        int read = stream.Read(buffer, 0, buffer.Length);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static bool IsBinaryFile(string file) => File.Exists(file) && IsBinary(ReadHead(file));

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
        // An empty file has NO lines, not one empty line. Split would say
        // otherwise, and the difference is visible: comparing an added file
        // against the side where it does not exist drew a phantom "removed blank
        // line" under the additions.
        if (text.Length == 0)
        {
            return [];
        }

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
