using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The diff-viewer options the port was missing, kept next to
///  <see cref="DiffDisplayOptions"/> rather than inside it because that type
///  belongs to <see cref="DiffTextService"/>.
///
///  <para>Three of them are git arguments the upstream <c>FileViewer</c> passes
///  (<c>--ignore-space-at-eol</c>, <c>-b</c>, <c>--text</c>: the
///  <c>ignoreWhitespaceAtEol</c> / <c>ignoreWhiteSpaces</c> buttons and
///  <c>treatAllFilesAsTextToolStripMenuItem</c>); the fourth is the display-only
///  <c>ShowSyntaxHighlightingInDiff</c> setting.</para>
///
///  <para>Like <see cref="DiffTextService.Session"/> this is a process-wide instance
///  shared by every diff view, but since M69 the pair is no longer merely
///  session-scoped: <see cref="EnsureRestored"/> seeds both from
///  <c>view-prefs.json</c> and <see cref="Persist"/> writes them back, so the whole
///  strip comes back as the user left it.</para>
/// </summary>
public sealed class DiffViewerOptions
{
    /// <summary>The instance shared by the diff views.</summary>
    public static DiffViewerOptions Session { get; } = new();

    /// <summary>Upstream <c>ignoreWhitespaceAtEol</c> — <c>git diff --ignore-space-at-eol</c>.</summary>
    public bool IgnoreWhitespaceAtEol { get; set; }

    /// <summary>Upstream <c>ignoreWhiteSpaces</c> — <c>git diff -b</c> (amount of whitespace).</summary>
    public bool IgnoreWhitespaceChange { get; set; }

    /// <summary>Upstream <c>treatAllFilesAsTextToolStripMenuItem</c> — <c>git diff --text</c>.</summary>
    public bool TreatAllFilesAsText { get; set; }

    /// <summary>Upstream <c>showSyntaxHighlighting</c> — colours the patch content.</summary>
    public bool SyntaxHighlighting { get; set; }

    /// <summary>
    ///  Whether the changed words inside a changed line are marked, in both the unified
    ///  patch pane and the side-by-side window (the <c>a|b</c> switch).
    ///
    ///  <para>It is stored HERE rather than next to the renderer that reads it — where
    ///  <c>InlineDiffOptions</c> now only forwards to this property — because this class
    ///  is the one that is written to and read back from <c>view-prefs.json</c>. A
    ///  reading preference the user turns off has to stay off across a restart like every
    ///  other switch on that strip; before this it was session-scoped and came back on at
    ///  every start, which reads as the setting not working.</para>
    ///
    ///  <para>Default <see langword="true"/>, and the mirror property in
    ///  <c>DiffPrefs</c> defaults the same way: the two must agree or the first save on a
    ///  machine with no preferences file would silently flip the marks off.</para>
    /// </summary>
    public bool InlineDiff { get; set; } = true;

    /// <summary>Whether any of the three git flags is on.</summary>
    public bool HasGitFlags => IgnoreWhitespaceAtEol || IgnoreWhitespaceChange || TreatAllFilesAsText;

    private static readonly ViewPrefsService PrefsService = new();

    private static bool _restored;

    /// <summary>
    ///  Seeds <see cref="Session"/> and <see cref="DiffTextService.Session"/> from
    ///  <c>view-prefs.json</c>, once per process.
    ///
    ///  <para>Called at the very top of <c>DiffView</c>'s constructor body rather than
    ///  from a static initialiser of either singleton: the view aliases both objects in
    ///  its FIELD initialisers and only reads their properties in the constructor body
    ///  (to seed each toggle button's checked state), so this is the last moment that is
    ///  both early enough for every reader and independent of the order in which the two
    ///  singletons happen to be touched first. Idempotent, so the second and later views
    ///  — including the ones <c>CommitDialog</c> opens — do not re-read the file and
    ///  cannot clobber a toggle the user has flipped since.</para>
    /// </summary>
    public static void EnsureRestored()
    {
        if (_restored)
        {
            return;
        }

        // Set before loading: a throwing load must not leave the flag clear and have
        // every later view retry (and re-apply defaults over live toggles).
        _restored = true;

        DiffPrefs prefs = PrefsService.Load().Diff;
        DiffDisplayOptions display = DiffTextService.Session;

        display.ShowEntireFile = prefs.ShowEntireFile;
        display.IgnoreWhitespace = prefs.IgnoreWhitespace;
        display.ShowNonPrinting = prefs.ShowNonPrinting;
        display.WordDiff = prefs.WordDiff;
        display.EncodingName = prefs.EncodingName;
        display.ContextLines = prefs.ContextLines;
        display.FontSize = prefs.FontSize;

        Session.IgnoreWhitespaceAtEol = prefs.IgnoreWhitespaceAtEol;
        Session.IgnoreWhitespaceChange = prefs.IgnoreWhitespaceChange;
        Session.TreatAllFilesAsText = prefs.TreatAllFilesAsText;
        Session.SyntaxHighlighting = prefs.SyntaxHighlighting;
        Session.InlineDiff = prefs.InlineDiff;
    }

    /// <summary>
    ///  Writes the current state of both singletons back to <c>view-prefs.json</c>.
    ///  Called after every toolbar/menu change; the write goes through
    ///  <see cref="ViewPrefsService.Update"/> so it cannot revert another surface's
    ///  group (the file also carries the file-history switches, the left panel filters
    ///  and the filter MRU).
    /// </summary>
    public static void Persist()
    {
        DiffDisplayOptions display = DiffTextService.Session;

        PrefsService.Update(prefs => prefs.Diff = new DiffPrefs
        {
            ShowEntireFile = display.ShowEntireFile,
            IgnoreWhitespace = display.IgnoreWhitespace,
            ShowNonPrinting = display.ShowNonPrinting,
            WordDiff = display.WordDiff,
            EncodingName = display.EncodingName,
            ContextLines = display.ContextLines,
            FontSize = display.FontSize,
            IgnoreWhitespaceAtEol = Session.IgnoreWhitespaceAtEol,
            IgnoreWhitespaceChange = Session.IgnoreWhitespaceChange,
            TreatAllFilesAsText = Session.TreatAllFilesAsText,
            SyntaxHighlighting = Session.SyntaxHighlighting,
            InlineDiff = Session.InlineDiff,
        });
    }
}

/// <summary>
///  Runs the same diff as <see cref="DiffTextService"/>, plus the extra git flags
///  of <see cref="DiffViewerOptions"/>.
///
///  <para>It exists because <see cref="DiffTextService.BuildArguments"/> knows a
///  fixed set of options: rather than fork that type, this one takes its argument
///  list and splices the extra flags in, so the two stay in sync by
///  construction. When no extra flag is set it delegates outright.</para>
/// </summary>
public static class ExtendedDiffTextService
{
    /// <summary>
    ///  <see cref="DiffTextService.BuildArguments"/> with the extra flags inserted
    ///  before the revision arguments.
    /// </summary>
    public static List<string> BuildArguments(
        DiffTextRequest request,
        DiffDisplayOptions options,
        DiffViewerOptions extra)
    {
        List<string> args = DiffTextService.BuildArguments(request, options);
        if (!extra.HasGitFlags)
        {
            return args;
        }

        List<string> flags = [];
        if (extra.IgnoreWhitespaceAtEol)
        {
            flags.Add("--ignore-space-at-eol");
        }

        if (extra.IgnoreWhitespaceChange)
        {
            flags.Add("-b");
        }

        if (extra.TreatAllFilesAsText)
        {
            flags.Add("--text");
        }

        // "--find-renames" is emitted by BuildArguments for every request kind and
        // always precedes the revisions, so it is a stable splice point.
        int at = args.IndexOf("--find-renames");
        args.InsertRange(at < 0 ? args.Count : at + 1, flags);

        return args;
    }

    /// <summary>Renders the git command line as text, for the status bar.</summary>
    public static string DescribeCommand(
        DiffTextRequest request,
        DiffDisplayOptions options,
        DiffViewerOptions extra) =>
        "git " + string.Join(' ', BuildArguments(request, options, extra).Select(Quote));

    /// <summary>
    ///  Runs git and returns the patch text, decoded with the encoding named by
    ///  <paramref name="options"/>. Never throws for a failed git run: git's own
    ///  error output is returned as the text, so the pane can show it. Must not be
    ///  called from the UI thread.
    /// </summary>
    public static async Task<string> GetDiffTextAsync(
        DiffTextRequest request,
        DiffDisplayOptions options,
        DiffViewerOptions extra,
        CancellationToken cancellationToken = default)
    {
        if (!extra.HasGitFlags)
        {
            return await DiffTextService.GetDiffTextAsync(request, options, cancellationToken)
                .ConfigureAwait(false);
        }

        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = request.RepoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in BuildArguments(request, options, extra))
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using Process process = new() { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return "Cannot run git: " + ex.Message;
        }

        // Raw bytes, so the caller-selected encoding decides how the file content
        // is interpreted (git has no diff-content encoding switch).
        using MemoryStream stdout = new();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await copy.ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string error = await stderr.ConfigureAwait(false);
        Encoding encoding = DiffTextService.ResolveEncoding(options.EncodingName);
        string text = encoding.GetString(stdout.ToArray());

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.IsNullOrWhiteSpace(error)
                ? "(no textual diff — binary file or no changes)"
                : error;
        }

        return text;
    }

    /// <summary>
    ///  Reads a file's content at a revision as text, decoded with
    ///  <paramref name="encodingName"/> — the data behind "Copy new/old version".
    ///  Pass <see langword="null"/> for <paramref name="rev"/> to read the
    ///  working-tree copy. Must not be called from the UI thread.
    /// </summary>
    public static async Task<string> GetFileTextAsync(
        string repoPath,
        string? rev,
        string path,
        string encodingName,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, rev, path, cancellationToken)
            .ConfigureAwait(false);

        return DiffTextService.ResolveEncoding(encodingName).GetString(bytes);
    }

    /// <summary>
    ///  The first <paramref name="count"/> bytes of a file at a revision (or of the
    ///  working-tree copy when <paramref name="rev"/> is <see langword="null"/>) —
    ///  enough to recognise a format by its magic number and no more.
    ///
    ///  <para>Bounded on purpose: this runs for every file the user clicks, and the
    ///  question it answers ("is this an image?") must not cost a full read of a
    ///  200 MB blob. git is stopped as soon as the header is in hand — a broken pipe
    ///  is the normal end of this call, not a failure.</para>
    ///
    ///  <para>Returns an EMPTY array for a side that does not exist (an added file has
    ///  no old version, a deleted one no new version), because that is a legitimate
    ///  answer here and not an error: it simply is not an image.</para>
    /// </summary>
    public static async Task<byte[]> GetFileHeaderAsync(
        string repoPath,
        string? rev,
        string path,
        int count,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(rev))
            {
                await using FileStream file = new(
                    Path.Combine(repoPath, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                byte[] head = new byte[count];
                int read = await file.ReadAtLeastAsync(head, count, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);

                return head[..read];
            }

            ProcessStartInfo psi = new()
            {
                FileName = "git",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("--no-pager");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.quotepath=false");
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add(rev.EndsWith(':') ? rev + path : rev + ":" + path);

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

            using Process process = new() { StartInfo = psi };
            process.Start();

            try
            {
                byte[] head = new byte[count];
                int read = await process.StandardOutput.BaseStream
                    .ReadAtLeastAsync(head, count, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);

                return head[..read];
            }
            finally
            {
                // Not awaited for exit: the point of this method is not to wait for the
                // whole blob to be written to a pipe nobody is going to drain.
                TryKill(process);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Missing path, missing revision, unreadable file: not an image, and the
            // caller has nothing useful to say about it.
            return [];
        }
    }

    private static string Quote(string arg) =>
        arg.Contains(' ', StringComparison.Ordinal)
            ? string.Format(CultureInfo.InvariantCulture, "\"{0}\"", arg)
            : arg;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process already went away; nothing to clean up.
        }
    }
}

/// <summary>
///  Recognises the raster formats the port's image comparison can decode, from the
///  first bytes of the file.
///
///  <para>From the BYTES and never from the extension, which is the whole point: a
///  <c>.png</c> that is really a patch, a text file or a Git-LFS pointer must fall
///  through to the textual diff, and a screenshot committed without an extension must
///  still be offered as an image. Upstream asks the decoder the same question
///  (<c>FileViewer.IsImage</c> tries to build a bitmap); the port asks it twice — this
///  cheap sniff decides whether to OFFER the window, and the window's own decode
///  decides what it can actually show, so a format that merely starts like an image
///  degrades to "this side could not be decoded" instead of a wrong menu.</para>
///
///  <para><see cref="HeaderLength"/> bytes are enough for every signature below; ICO
///  is the longest at 22.</para>
///
///  <para><b>The only such sniffer in the port.</b> The conflict dialog's guided
///  refusal used to carry a second, hand-rolled copy inside <c>MergeToolService</c>;
///  it had drifted (its ICO test was the four-byte one this class has since dropped),
///  so the same file could be an image in the diff view and not in the merge tool, or
///  the reverse. Two answers to "is this an image?" is a bug waiting for the day the
///  two copies are edited apart, and there is no reason for two: the question has
///  nothing to do with either caller. Hence <see cref="Detect"/>, which returns the
///  NAME because the merge tool has to print it, and <see cref="LooksLikeImage"/> on
///  top of it for the caller that only needs the yes/no.</para>
/// </summary>
public static class ImageFormats
{
    /// <summary>How many leading bytes <see cref="Detect"/> needs at most.</summary>
    public const int HeaderLength = 32;

    /// <summary>Whether <paramref name="header"/> starts with a known image signature.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> header) => Detect(header) is not null;

    /// <summary>
    ///  The name of the format <paramref name="header"/> announces ("PNG", "JPEG",
    ///  "GIF", "WEBP", "BMP", "ICO"), or <see langword="null"/> when the bytes match
    ///  none of them. The name is meant to be shown to the user, so it is spelled the
    ///  way the format is commonly written rather than as an enum member.
    /// </summary>
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (IsPng(header))
        {
            return "PNG";
        }

        if (IsJpeg(header))
        {
            return "JPEG";
        }

        if (IsGif(header))
        {
            return "GIF";
        }

        if (IsWebp(header))
        {
            return "WEBP";
        }

        if (IsBmp(header))
        {
            return "BMP";
        }

        return IsIco(header) ? "ICO" : null;
    }

    // The 0x89 first byte and the CR/LF pair are the signature's own transfer test:
    // a PNG that travelled through a text channel no longer starts with them.
    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // SOI, then the marker introducer of the segment that follows it; every JPEG
    // variant (JFIF, Exif, raw) has one, so three bytes are necessary and enough.
    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];

    private static bool IsPng(ReadOnlySpan<byte> h) => h.StartsWith(PngMagic);

    private static bool IsJpeg(ReadOnlySpan<byte> h) => h.StartsWith(JpegMagic);

    private static bool IsGif(ReadOnlySpan<byte> h) =>
        h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8);

    private static bool IsWebp(ReadOnlySpan<byte> h) =>
        h.Length >= 12 && h.StartsWith("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8);

    // "BM" alone is two bytes of ASCII that plenty of text starts with ("BMW..."), so
    // the rest of the 14-byte file header is checked too: both reserved words are zero
    // in every writer's output, and the offset to the pixel data cannot point before
    // the end of the smallest possible header pair (14 + 12).
    private static bool IsBmp(ReadOnlySpan<byte> h)
    {
        if (h.Length < 14 || !h.StartsWith("BM"u8))
        {
            return false;
        }

        if (h[6] != 0 || h[7] != 0 || h[8] != 0 || h[9] != 0)
        {
            return false;
        }

        uint pixelOffset = (uint)(h[10] | (h[11] << 8) | (h[12] << 16) | (h[13] << 24));
        return pixelOffset is >= 26 and < 1 << 24;
    }

    // Reserved word, type 1 (icon; 2 would be a cursor), and at least one image in the
    // directory. Those six bytes are the weakest signature here — two of them are zero
    // and the other four are a plain little-endian 1 and a count, which is also how a
    // great many binary records happen to start (an MPEG picture start code is literally
    // 00 00 01 00) — so the first directory entry is checked as well, exactly as IsBmp
    // checks the rest of the BMP file header: its reserved byte is zero in every writer's
    // output, it declares no more than one colour plane, and the image it points at
    // cannot begin before the directory that describes it ends. A sweep of ~1.3M files
    // found no counter-example either way, so this costs nothing on real icons (63 real
    // .ico files plus 5 extension-less ones still pass) and takes away the cases where
    // the four-byte version was matching on faith.
    private static bool IsIco(ReadOnlySpan<byte> h)
    {
        // 6 bytes of directory header plus one 16-byte entry: an ICO worth offering is
        // never shorter, and the probe always reads more than this.
        if (h.Length < 22 || h[0] != 0 || h[1] != 0 || h[2] != 1 || h[3] != 0)
        {
            return false;
        }

        int count = h[4] | (h[5] << 8);
        if (count == 0 || h[9] != 0 || (h[10] | (h[11] << 8)) > 1)
        {
            return false;
        }

        uint imageOffset = (uint)(h[18] | (h[19] << 8) | (h[20] << 16) | (h[21] << 24));
        return imageOffset >= 6 + (16 * (uint)count);
    }
}
