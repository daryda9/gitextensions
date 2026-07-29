using System.Text;
using System.Text.RegularExpressions;
using GitCommands;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  One patch of a <c>git am</c> series, as the upstream <c>GitUI.PatchFile</c>
///  (<c>src/app/GitUI/UserControls/PatchFile.cs</c>) models it: the mail headers
///  read out of the numbered patch copy git keeps in the rebase directory, plus
///  the three state flags the grid renders through <see cref="Status"/>.
/// </summary>
public sealed class AmPatchFile
{
    /// <summary>The numbered file name inside the rebase directory ("0001", "0002", …).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Full path of that numbered copy — what the patch viewer opens.</summary>
    public string FullName { get; init; } = string.Empty;

    public string? Author { get; init; }

    public string? Subject { get; init; }

    public string? Date { get; init; }

    /// <summary>Already committed by this am session (its number is below <c>next</c>).</summary>
    public bool IsApplied { get; init; }

    /// <summary>The patch git is applying right now (its number equals <c>next</c>).</summary>
    public bool IsNext { get; init; }

    /// <summary>Set when the user skipped this patch in this dialog's session.</summary>
    public bool IsSkipped { get; set; }

    /// <summary>
    ///  Exactly upstream <c>PatchFile.Status</c> (same precedence: Skipped, Applied,
    ///  "Applying…", then "the numbered copy is gone ⇒ Applied", else pending).
    /// </summary>
    public string Status
    {
        get
        {
            if (IsSkipped)
            {
                return "Skipped";
            }

            if (IsApplied)
            {
                return "Applied";
            }

            if (IsNext)
            {
                return "Applying…";
            }

            if (!string.IsNullOrEmpty(FullName) && !File.Exists(FullName))
            {
                return "Applied";
            }

            return string.Empty;
        }
    }
}

/// <summary>
///  Snapshot of the <c>git am</c> state machine for one repository — the data
///  upstream's <c>FormApplyPatch.EnableButtons()</c> reads to decide which
///  commands are live.
/// </summary>
/// <param name="InProgress">
///  <c>Module.InTheMiddleOfPatch()</c>: an <c>am</c> session is open (the rebase
///  directory exists and it is not a <c>rebase</c>). While true only
///  Resolved / Skip / Abort make sense.
/// </param>
/// <param name="InConflictedMerge">
///  <c>Module.InTheMiddleOfConflictedMerge()</c>: the index has unmerged entries,
///  which <c>am --3way</c> produces when a patch conflicts. Upstream enables
///  "Solve conflicts" and DISABLES "Conflicts resolved" while this holds — the
///  user must stage the resolution first.
/// </param>
/// <param name="RebaseDir">The resolved rebase directory, or "" when there is none.</param>
/// <param name="Patches">The series, oldest first; empty when no session is open.</param>
public sealed record AmSessionState(
    bool InProgress,
    bool InConflictedMerge,
    string RebaseDir,
    IReadOnlyList<AmPatchFile> Patches)
{
    public static AmSessionState None { get; } = new(false, false, string.Empty, Array.Empty<AmPatchFile>());

    /// <summary>The patch git stopped on, i.e. the one Skip / Resolved acts upon.</summary>
    public AmPatchFile? Current => Patches.FirstOrDefault(p => p.IsNext);
}

/// <summary>
///  The <c>git am</c> state machine of upstream <c>FormApplyPatch</c> +
///  <c>PatchGrid</c>, ported. This type owns two things:
///  <list type="bullet">
///   <item><b>reading the state</b> (<see cref="Read"/>): whether a session is in
///    progress, whether the index is conflicted, and the per-patch status list —
///    taken from the core APIs (<c>InTheMiddleOfPatch</c>,
///    <c>InTheMiddleOfConflictedMerge</c>, <c>GetRebaseDir</c>) and from the
///    numbered patch copies git writes into that directory, which is exactly what
///    <c>PatchGrid.GetRebasePatchFiles()</c> does;</item>
///   <item><b>building the git argument strings</b> for Apply / Resolved / Skip /
///    Abort, byte-for-byte the ones <c>GitCommands.Git.Commands</c> produces
///    (<c>Commands.Arguments.cs:11,52,61,615,634</c>) — note every one of them
///    carries <c>--3way</c>.</item>
///  </list>
///  Every method is synchronous and must run off the UI thread; the argument
///  builders are pure. Nothing here executes git: the caller feeds the argument
///  string to <see cref="GitStreamRunner"/> so the user sees the output live.
/// </summary>
public sealed class AmSessionService
{
    // Same header split as upstream PatchGrid.HeadersRegex.
    private static readonly Regex HeaderRegex =
        new(@"^(?<key>[-A-Za-z0-9]+)(?::[ \t]*)(?<value>.*)$", RegexOptions.ExplicitCapture | RegexOptions.Compiled);

    // RFC 2047 encoded-word, e.g. =?utf-8?q?Fix=20the=20thing?= / =?UTF-8?B?...?=
    private static readonly Regex EncodedWordRegex =
        new(@"=\?(?<charset>[\w-]+)\?(?<enc>[bBqQ])\?(?<text>[^?]*)\?=", RegexOptions.Compiled);

    /// <summary>
    ///  Reads the current am state of <paramref name="repoPath"/>. Never throws:
    ///  on any failure it degrades to the on-disk check
    ///  (<c>.git/rebase-apply</c>), because a dialog that cannot read the state
    ///  must show "no session" rather than crash the UI.
    /// </summary>
    public AmSessionState Read(string repoPath)
    {
        string rebaseDir;
        bool inProgress;
        bool conflicted;

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            rebaseDir = module.GetRebaseDir();
            inProgress = module.InTheMiddleOfPatch();
            conflicted = inProgress && module.InTheMiddleOfConflictedMerge(throwOnErrorExit: false);
        }
        catch (Exception)
        {
            rebaseDir = FallbackRebaseDir(repoPath);
            inProgress = rebaseDir.Length > 0 && !File.Exists(rebaseDir + "rebasing");
            conflicted = false;
        }

        if (!inProgress || rebaseDir.Length == 0)
        {
            return AmSessionState.None;
        }

        return new AmSessionState(true, conflicted, rebaseDir, ReadPatches(rebaseDir));
    }

    /// <summary>
    ///  Port of <c>PatchGrid.GetRebasePatchFiles()</c>: every file in the rebase
    ///  directory whose name is a number is one patch of the series; the file
    ///  <c>next</c> holds the number git is applying, so everything below it is
    ///  applied and that one is "Applying…". Author / Subject / Date come from the
    ///  patch's own mail headers.
    /// </summary>
    private static IReadOnlyList<AmPatchFile> ReadPatches(string rebaseDir)
    {
        int next = 0;
        try
        {
            string nextFile = rebaseDir + "next";
            if (File.Exists(nextFile) && int.TryParse(File.ReadAllText(nextFile).Trim(), out int parsed))
            {
                next = parsed;
            }
        }
        catch (Exception)
        {
            next = 0;
        }

        string[] files;
        try
        {
            files = Directory.Exists(rebaseDir) ? Directory.GetFiles(rebaseDir) : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<AmPatchFile>();
        }

        List<AmPatchFile> patches = [];
        foreach (string fullName in files)
        {
            string name = Path.GetFileName(fullName);
            if (!int.TryParse(name, out int number))
            {
                continue;
            }

            (string? author, string? subject, string? date) = ReadHeaders(fullName);

            patches.Add(new AmPatchFile
            {
                Name = name,
                FullName = fullName,
                Author = author,
                Subject = subject,
                Date = date,
                IsApplied = number < next,
                IsNext = number == next,
            });
        }

        // Directory.GetFiles gives no ordering guarantee; the series only reads
        // correctly in numeric order (0001, 0002, …), which for the fixed-width
        // names git writes is the same as ordinal.
        patches.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return patches;
    }

    // Mail headers of one patch: only the first header block matters (upstream
    // stops at the blank line, or as soon as it has all three fields).
    private static (string? Author, string? Subject, string? Date) ReadHeaders(string path)
    {
        string? author = null;
        string? subject = null;
        string? date = null;

        try
        {
            string? key = null;
            StringBuilder value = new();

            foreach (string line in File.ReadLines(path))
            {
                Match match = HeaderRegex.Match(line);

                if (key is null)
                {
                    // Skip anything before the first header (git writes "From <sha> …").
                    if (!string.IsNullOrWhiteSpace(line) && !match.Success)
                    {
                        continue;
                    }
                }
                else if (string.IsNullOrWhiteSpace(line) || match.Success)
                {
                    // The previous header is complete (continuation lines are folded in below).
                    Store(key, DecodeHeader(value.ToString()), ref author, ref subject, ref date);
                }

                if (match.Success)
                {
                    key = match.Groups["key"].Value;
                    value.Clear();
                    value.Append(match.Groups["value"].Value);
                }
                else if (line.Length > 0 && key is not null)
                {
                    // Folded continuation of the header being read.
                    value.Append(line.Trim());
                }

                if (string.IsNullOrEmpty(line) ||
                    (author is not null && subject is not null && date is not null))
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // A patch we cannot read still belongs in the grid, just without headers.
        }

        return (author, subject, date);

        static void Store(string key, string value, ref string? author, ref string? subject, ref string? date)
        {
            switch (key)
            {
                case "From":
                    // "Name <mail@host>" → "Name", as upstream does.
                    int angle = value.IndexOf('<');
                    author = angle > 0 ? value[..angle].Trim() : value.Trim();
                    break;
                case "Date":
                    // Drop the numeric timezone tail, as upstream does.
                    int plus = value.IndexOf('+');
                    date = plus > 0 ? value[..plus].Trim() : value.Trim();
                    break;
                case "Subject":
                    subject = value.Trim();
                    break;
            }
        }
    }

    /// <summary>
    ///  Decodes RFC 2047 encoded-words (<c>=?utf-8?q?…?=</c> / <c>?b?</c>), which is
    ///  how <c>git format-patch</c> writes any non-ASCII author or subject. Upstream
    ///  leans on <c>System.Net.Mail</c> + <c>RFC2047Decoder</c> for this; the port
    ///  decodes the two encodings git actually emits and leaves anything else as-is.
    /// </summary>
    internal static string DecodeHeader(string value)
    {
        if (value.Length == 0 || !value.Contains("=?", StringComparison.Ordinal))
        {
            return value;
        }

        return EncodedWordRegex.Replace(value, match =>
        {
            try
            {
                Encoding encoding = Encoding.GetEncoding(match.Groups["charset"].Value);
                string text = match.Groups["text"].Value;

                if (match.Groups["enc"].Value is "b" or "B")
                {
                    return encoding.GetString(Convert.FromBase64String(text));
                }

                // Quoted-printable: "_" is a space, "=XX" a hex byte.
                List<byte> bytes = [];
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '_')
                    {
                        bytes.Add((byte)' ');
                    }
                    else if (text[i] == '=' && i + 2 < text.Length
                             && byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                    {
                        bytes.Add(b);
                        i += 2;
                    }
                    else
                    {
                        bytes.Add((byte)text[i]);
                    }
                }

                return encoding.GetString(bytes.ToArray());
            }
            catch (Exception)
            {
                return match.Value;
            }
        });
    }

    private static string FallbackRebaseDir(string repoPath)
    {
        foreach (string candidate in new[] { "rebase-merge", "rebase-apply", "rebase" })
        {
            string dir = Path.Combine(repoPath, ".git", candidate);
            if (Directory.Exists(dir))
            {
                return dir + Path.DirectorySeparatorChar;
            }
        }

        return string.Empty;
    }

    /// <summary>
    ///  The files a patch directory contributes to <c>git am</c>, in name order.
    ///  Upstream (<c>GitModule.ApplyPatch</c>) takes <em>every</em> file in the
    ///  directory — no extension filter — and streams them into git's stdin; the
    ///  port passes them as arguments instead (see the class remarks of
    ///  <c>ApplyPatchDialog</c>), so the same set is used, only sorted, because
    ///  <c>Directory.GetFiles</c> order is unspecified and the series must be
    ///  applied oldest-first.
    /// </summary>
    public static IReadOnlyList<string> PatchFilesInDirectory(string dir)
    {
        string[] files = Directory.GetFiles(dir);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    // ---- argument builders: literal equivalents of GitCommands.Git.Commands ----

    /// <summary><c>am --3way [--signoff] [--ignore-whitespace] "&lt;file&gt;"</c> (<c>Commands.ApplyMailboxPatch</c>).</summary>
    public static string ApplyMailboxArguments(bool signOff, bool ignoreWhitespace, string? patchFile = null)
    {
        StringBuilder args = new("am --3way");
        if (signOff)
        {
            args.Append(" --signoff");
        }

        if (ignoreWhitespace)
        {
            args.Append(" --ignore-whitespace");
        }

        if (!string.IsNullOrEmpty(patchFile))
        {
            args.Append(' ').Append(Quote(patchFile));
        }

        return args.ToString();
    }

    /// <summary>The mailbox form with a whole series of files appended.</summary>
    public static string ApplyMailboxArguments(bool signOff, bool ignoreWhitespace, IReadOnlyList<string> patchFiles)
    {
        StringBuilder args = new(ApplyMailboxArguments(signOff, ignoreWhitespace));
        foreach (string file in patchFiles)
        {
            args.Append(' ').Append(Quote(file));
        }

        return args.ToString();
    }

    /// <summary><c>apply [--ignore-whitespace] "&lt;file&gt;"</c> (<c>Commands.ApplyDiffPatch</c>).</summary>
    public static string ApplyDiffArguments(bool ignoreWhitespace, string patchFile)
        => ignoreWhitespace
            ? $"apply --ignore-whitespace {Quote(patchFile)}"
            : $"apply {Quote(patchFile)}";

    /// <summary><c>am --3way --resolved</c> (<c>Commands.Resolved</c>).</summary>
    public static string ResolvedArguments => "am --3way --resolved";

    /// <summary><c>am --3way --skip</c> (<c>Commands.Skip</c>).</summary>
    public static string SkipArguments => "am --3way --skip";

    /// <summary><c>am --3way --abort</c> (<c>Commands.Abort</c>).</summary>
    public static string AbortArguments => "am --3way --abort";

    /// <summary><c>add -A</c> — what upstream's "Add files" button reaches through <c>FormAddFiles</c>.</summary>
    public static string StageAllArguments => "add -A";

    private static string Quote(string path) => "\"" + path + "\"";
}
