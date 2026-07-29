using System.Text;
using System.Text.RegularExpressions;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  What kind of object <c>git fsck</c> reported, mirroring upstream's
///  <c>LostObjectType</c> (<c>FormVerify.LostObject.cs:11-18</c>).
/// </summary>
public enum LostObjectKind
{
    /// <summary>A commit — the only kind a recovery BRANCH can point at.</summary>
    Commit,

    /// <summary>A blob (file content).</summary>
    Blob,

    /// <summary>A tree (directory).</summary>
    Tree,

    /// <summary>An annotated tag object.</summary>
    Tag,

    /// <summary>Anything else, including <c>warning in tree …</c> lines.</summary>
    Other,
}

/// <summary>
///  One object <c>git fsck</c> reported as dangling / unreachable / missing, with the
///  metadata upstream's grid shows.
/// </summary>
/// <param name="Kind">Parsed object type.</param>
/// <param name="RawType">
///  The raw fsck phrase (<c>dangling commit</c>, <c>unreachable blob</c>, …), shown
///  verbatim in the Type column exactly as upstream does.
/// </param>
/// <param name="Hash">Full object id.</param>
/// <param name="Author">Commit author (<c>%aN</c>) or tagger; empty for blobs/trees.</param>
/// <param name="Date">Commit/tagger date, or the loose object file's timestamp for a blob.</param>
/// <param name="Subject">Commit subject, or <c>&lt;tag&gt;:&lt;message&gt;</c> for a tag.</param>
/// <param name="Parent">First parent (commits) or the tagged object (tags).</param>
/// <param name="TagName">
///  For an annotated tag, its original name — upstream reuses it to build the recovery
///  tag <c>LOST_FOUND_&lt;name&gt;</c> instead of a bare counter.
/// </param>
public sealed record LostObject(
    LostObjectKind Kind,
    string RawType,
    string Hash,
    string Author,
    DateTime? Date,
    string Subject,
    string Parent,
    string TagName)
{
    /// <summary>Short hash, for display.</summary>
    public string ShortHash => Hash.Length > 10 ? Hash[..10] : Hash;

    /// <summary>
    ///  True when this object can carry a recovery BRANCH. Only a commit can: a branch
    ///  ref must resolve to a commit, so offering "create branch" for a blob would be a
    ///  button that always fails. Upstream gates the same menu entries on the object
    ///  being a commit (<c>FormVerify.cs:505-518</c>).
    /// </summary>
    public bool CanBecomeBranch => Kind == LostObjectKind.Commit;
}

/// <summary>
///  The three <c>git fsck</c> switches upstream exposes as check boxes
///  (<c>FormVerify.GetOptions</c>, <c>FormVerify.cs:406-426</c>). Defaults reproduce
///  upstream's designer defaults: only <see cref="NoReflogs"/> starts on.
/// </summary>
public sealed record VerifyOptions(
    bool Unreachable = false,
    bool FullCheck = false,
    bool NoReflogs = true);

/// <summary>Outcome of an fsck scan: git's raw text plus the parsed objects.</summary>
public sealed record VerifyScanResult(bool Success, string Output, IReadOnlyList<LostObject> Objects);

/// <summary>
///  The "Recover lost objects" engine — the port of upstream's <c>FormVerify</c>
///  (<c>src/app/GitUI/CommandsDialogs/FormVerify.cs</c> and its <c>LostObject</c>
///  partial). It runs <c>git fsck-objects</c>, parses the reported objects, enriches
///  them with author/date/subject, and performs the recovery actions.
///
///  <para><b>Why the parser is reimplemented here.</b> Upstream's parser lives in
///  <c>FormVerify.LostObject.cs</c> under <c>src/app/GitUI</c>, which is the WinForms
///  assembly; the port only compiles <c>GitCommands</c> (see
///  <c>Core.GitCommands.csproj</c>), so the type is genuinely unavailable and the
///  regexes are mirrored rather than reused. They are kept character-for-character
///  equivalent, with two deliberate widenings noted at their definitions.</para>
///
///  <para><b>LOCALE PINNING IS LOAD-BEARING.</b> <c>git fsck</c> writes
///  <c>dangling commit &lt;sha&gt;</c> in ENGLISH only under a C locale; with an
///  Italian git installed (as on this machine) the very same repository reports
///  <c>commit pendente &lt;sha&gt;</c> and <c>commit non raggiungibile &lt;sha&gt;</c>.
///  An unpinned scan therefore parses ZERO objects while exiting 0 — an empty list that
///  looks exactly like a healthy repository. Every fsck call here runs inside
///  <see cref="GitEnvironment.DiagnosticLocaleScope"/>; do not remove it.</para>
///
///  <para>All methods are synchronous and meant to be called off the UI thread.</para>
/// </summary>
public sealed class VerifyService
{
    /// <summary>
    ///  Prefix of the tags upstream creates when recovering objects, and the prefix
    ///  "Delete all LOST_AND_FOUND tags" removes (<c>FormVerify.cs:14</c>).
    /// </summary>
    public const string RecoveredTagPrefix = "LOST_FOUND_";

    // Upstream's line regex (FormVerify.LostObject.cs:22-23). Deliberate widening: the
    // object id is {40,64} rather than a hard-coded {40}, so a SHA-256 repository is
    // parsed too instead of silently yielding an empty list.
    private static readonly Regex _lostObjectLine = new(
        @"^(?<rawtype>(dangling|missing|unreachable) (?<objecttype>commit|blob|tree|tag)|warning in tree) (?<objectid>[0-9a-f]{40,64})(.)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Upstream's commit-metadata regex (FormVerify.LostObject.cs:24-25). U+001F is the
    // field separator; Singleline so a subject containing no newline still matches when
    // the parent list is absent.
    private static readonly Regex _commitMetadata = new(
        "^(?<author>[^\u001f]+)\u001f(?<subject>.*)\u001f(?<date>\\d+)\u001f(?<first_parent>[^ ]+)?( .+)?$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // Upstream's annotated-tag regex (FormVerify.LostObject.cs:26-27).
    private static readonly Regex _tagMetadata = new(
        @"^object (?<parent>.+)\ntype commit\ntag (?<tagname>.+)\ntagger (?<author>.+) <.*> (?<date>.+) .*\n\n(?<subject>.*)\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///  Runs <c>git fsck-objects</c> with <paramref name="options"/> and returns the
    ///  parsed lost objects.
    ///
    ///  <para>The command is built exactly as upstream builds it
    ///  (<c>FormVerify.cs:330</c> + <c>GetOptions</c>): the subcommand
    ///  <c>fsck-objects</c> plus, in this order, <c>--unreachable</c>, <c>--full</c>,
    ///  <c>--no-reflogs</c>. No other switch is added — notably no <c>--dangling</c>,
    ///  because <c>git fsck</c> reports dangling objects by default.</para>
    ///
    ///  <para>fsck exits non-zero when it finds problems, which is the normal case here,
    ///  so the exit code is NOT treated as failure: the parsed list is what matters and
    ///  the raw text is always returned for inspection.</para>
    /// </summary>
    public VerifyScanResult Scan(string repoPath, VerifyOptions options)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string output = RunFsck(module, options, lostFound: false);
            IReadOnlyList<LostObject> objects = Parse(module, output);
            return new VerifyScanResult(true, output, objects);
        }
        catch (Exception ex)
        {
            return new VerifyScanResult(false, ex.Message, []);
        }
    }

    /// <summary>
    ///  Upstream's "Save objects to .git/lost-found": runs
    ///  <c>git fsck-objects --lost-found &lt;options&gt;</c> and lets GIT write the
    ///  files (<c>FormVerify.cs:139-144</c>). Git Extensions never creates or reads
    ///  those files itself; this method likewise only runs the command and then reports
    ///  how many files git left behind, which is the one piece of feedback upstream's
    ///  process dialog gives implicitly.
    /// </summary>
    public MaintenanceResult SaveObjectsToLostFound(string repoPath, VerifyOptions options)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string output = RunFsck(module, options, lostFound: true);

            string lostFoundDir = Path.Combine(GitDir(module, repoPath), "lost-found");
            int files = Directory.Exists(lostFoundDir)
                ? Directory.GetFiles(lostFoundDir, "*", SearchOption.AllDirectories).Length
                : 0;

            StringBuilder sb = new();
            sb.AppendLine($"$ git fsck-objects --lost-found{OptionSuffix(options)}");
            if (output.Length > 0)
            {
                sb.AppendLine(output.TrimEnd());
            }

            sb.AppendLine();
            sb.AppendLine(files > 0
                ? $"{files} file(s) now under {lostFoundDir}."
                : $"git wrote no files under {lostFoundDir} (nothing was recoverable).");

            return new MaintenanceResult(true, sb.ToString());
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, ex.Message);
        }
    }

    /// <summary>
    ///  Creates the recovery tags for <paramref name="objects"/>, reproducing
    ///  upstream's <c>CreateLostFoundTags</c> (<c>FormVerify.cs:446-470</c>): a
    ///  LIGHTWEIGHT tag per object, named <c>LOST_FOUND_&lt;n&gt;</c> with a 1-based
    ///  counter — except for an annotated tag object, which reuses its original name as
    ///  <c>LOST_FOUND_&lt;tagname&gt;</c>.
    ///
    ///  <para>Upstream deletes the existing <c>LOST_FOUND_*</c> tags first
    ///  (<c>FormVerify.cs:192-213</c>) so the counter cannot collide with a previous
    ///  run; <paramref name="deleteExistingFirst"/> keeps that behaviour and is on by
    ///  default.</para>
    /// </summary>
    public MaintenanceResult RecoverAsTags(string repoPath, IReadOnlyList<LostObject> objects, bool deleteExistingFirst = true)
    {
        if (objects.Count == 0)
        {
            return new MaintenanceResult(false, "Select the objects to recover first.");
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            StringBuilder sb = new();

            if (deleteExistingFirst)
            {
                MaintenanceResult cleared = DeleteRecoveredTags(repoPath);
                sb.AppendLine(cleared.Output.TrimEnd());
            }

            int created = 0;
            int counter = 1;
            foreach (LostObject obj in objects)
            {
                string suffix = obj.Kind == LostObjectKind.Tag && obj.TagName.Length > 0
                    ? obj.TagName
                    : counter.ToString();
                string tagName = RecoveredTagPrefix + suffix;

                // `git tag <name> -- <sha>`: the `--` is what upstream's Commands.CreateTag
                // emits, and it keeps a tag name that looks like an option unambiguous.
                GitArgumentBuilder args = new("tag") { tagName, "--", obj.Hash };
                ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);

                if (result.ExitedSuccessfully)
                {
                    created++;
                    sb.AppendLine($"created tag {tagName} -> {obj.ShortHash} ({obj.RawType})");
                }
                else
                {
                    sb.AppendLine($"FAILED {tagName} -> {obj.ShortHash}: {result.AllOutput.Trim()}");
                }

                counter++;
            }

            sb.AppendLine();
            sb.AppendLine($"{created} tag(s) created. Do not forget to delete these tags when finished.");
            return new MaintenanceResult(created > 0, sb.ToString());
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, ex.Message);
        }
    }

    /// <summary>
    ///  Upstream's "Delete all LOST_AND_FOUND tags" (<c>DeleteLostFoundTags</c>,
    ///  <c>FormVerify.cs:472-481</c>): every tag whose SHORT name starts with
    ///  <see cref="RecoveredTagPrefix"/> is removed with <c>git tag -d</c>. Nothing else
    ///  is touched, and no confirmation is asked — matching upstream.
    /// </summary>
    public MaintenanceResult DeleteRecoveredTags(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            List<string> names =
            [
                .. module.GetRefs(RefsFilter.Tags)
                    .Select(r => r.Name)
                    .Where(n => n.StartsWith(RecoveredTagPrefix, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal),
            ];

            if (names.Count == 0)
            {
                return new MaintenanceResult(true, $"No {RecoveredTagPrefix}* tags to delete.");
            }

            StringBuilder sb = new();
            int deleted = 0;
            foreach (string name in names)
            {
                GitArgumentBuilder args = new("tag") { "-d", name };
                ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
                if (result.ExitedSuccessfully)
                {
                    deleted++;
                    sb.AppendLine($"deleted tag {name}");
                }
                else
                {
                    sb.AppendLine($"FAILED to delete {name}: {result.AllOutput.Trim()}");
                }
            }

            sb.AppendLine($"{deleted} of {names.Count} {RecoveredTagPrefix}* tag(s) deleted.");
            return new MaintenanceResult(deleted == names.Count, sb.ToString());
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, ex.Message);
        }
    }

    /// <summary>
    ///  Creates a recovery BRANCH at <paramref name="hash"/> without checking it out —
    ///  the port of upstream's "Create branch" context entry, which opens
    ///  <c>FormCreateBranch</c> on the selected revision (<c>FormVerify.cs:176-184</c>).
    ///  Only meaningful for a commit (see <see cref="LostObject.CanBecomeBranch"/>).
    /// </summary>
    public MaintenanceResult CreateBranchAt(string repoPath, string name, string hash)
    {
        string branch = name?.Trim() ?? string.Empty;
        if (branch.Length == 0)
        {
            return new MaintenanceResult(false, "The branch name cannot be empty.");
        }

        return RunSimple(repoPath, new GitArgumentBuilder("branch") { branch, hash });
    }

    /// <summary>
    ///  Creates an arbitrarily named lightweight tag at <paramref name="hash"/> — the
    ///  port of upstream's "Create tag" context entry (<c>FormVerify.cs:166-174</c>),
    ///  which is the interactive route to a name that is not
    ///  <c>LOST_FOUND_&lt;n&gt;</c>.
    /// </summary>
    public MaintenanceResult CreateTagAt(string repoPath, string name, string hash)
    {
        string tag = name?.Trim() ?? string.Empty;
        if (tag.Length == 0)
        {
            return new MaintenanceResult(false, "The tag name cannot be empty.");
        }

        return RunSimple(repoPath, new GitArgumentBuilder("tag") { tag, "--", hash });
    }

    /// <summary>
    ///  Upstream's "Remove all dangling objects": a plain <c>git prune</c>
    ///  (<c>FormVerify.cs:146-159</c>). DESTRUCTIVE — the caller must confirm first,
    ///  as upstream does with a Yes/No box.
    /// </summary>
    public MaintenanceResult PruneDanglingObjects(string repoPath)
        => RunSimple(repoPath, new GitArgumentBuilder("prune"));

    /// <summary>
    ///  The object's content for the preview / "View" action: <c>git show &lt;id&gt;</c>,
    ///  the same command upstream's <c>ViewCurrentItem</c> uses through
    ///  <c>Module.ShowObject</c>.
    /// </summary>
    public string ShowObject(string repoPath, string hash)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            using IDisposable locale = GitEnvironment.DiagnosticLocaleScope();
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("show") { hash },
                throwOnErrorExit: false);
            return result.AllOutput;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    ///  Writes a lost BLOB's content to <paramref name="targetPath"/> — upstream's
    ///  "Save as…" context entry, which calls <c>Module.SaveBlobAs</c>
    ///  (<c>FormVerify.cs:553-586</c>).
    /// </summary>
    public MaintenanceResult SaveBlobAs(string repoPath, string hash, string targetPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            module.SaveBlobAs(targetPath, hash);
            return new MaintenanceResult(true, $"Saved {hash} to {targetPath}.");
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, $"Could not save {hash}: {ex.Message}");
        }
    }

    /// <summary>
    ///  How many <see cref="RecoveredTagPrefix"/> tags currently exist, so the dialog can
    ///  grey out "Delete all …" instead of offering a no-op.
    /// </summary>
    public int CountRecoveredTags(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            return module.GetRefs(RefsFilter.Tags)
                .Count(r => r.Name.StartsWith(RecoveredTagPrefix, StringComparison.Ordinal));
        }
        catch
        {
            return 0;
        }
    }

    // --- internals --------------------------------------------------------

    // Builds and runs the fsck command. See the class remarks for why the locale scope
    // is mandatory. fsck's non-zero exit is expected (it means "problems found"), so it
    // is not turned into an exception.
    private static string RunFsck(GitModule module, VerifyOptions options, bool lostFound)
    {
        GitArgumentBuilder args = new("fsck-objects")
        {
            { lostFound, "--lost-found" },
            { options.Unreachable, "--unreachable" },
            { options.FullCheck, "--full" },
            { options.NoReflogs, "--no-reflogs" },
        };

        using IDisposable locale = GitEnvironment.DiagnosticLocaleScope();
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return result.AllOutput ?? string.Empty;
    }

    private static string OptionSuffix(VerifyOptions options)
    {
        StringBuilder sb = new();
        if (options.Unreachable)
        {
            sb.Append(" --unreachable");
        }

        if (options.FullCheck)
        {
            sb.Append(" --full");
        }

        if (options.NoReflogs)
        {
            sb.Append(" --no-reflogs");
        }

        return sb.ToString();
    }

    private static MaintenanceResult RunSimple(string repoPath, GitArgumentBuilder args)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            using IDisposable locale = GitEnvironment.DiagnosticLocaleScope();
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            string output = result.AllOutput;
            if (string.IsNullOrWhiteSpace(output))
            {
                output = result.ExitedSuccessfully ? "(completed with no output)" : "(failed with no output)";
            }

            return new MaintenanceResult(result.ExitedSuccessfully, output);
        }
        catch (Exception ex)
        {
            return new MaintenanceResult(false, ex.Message);
        }
    }

    private static string GitDir(GitModule module, string repoPath)
    {
        string gitDir = module.WorkingDirGitDir;
        return string.IsNullOrEmpty(gitDir) ? Path.Combine(repoPath, ".git") : gitDir;
    }

    // Parses fsck's stdout into objects, then enriches them. Lines that do not match
    // are skipped, exactly as upstream skips them.
    private IReadOnlyList<LostObject> Parse(GitModule module, string output)
    {
        List<LostObject> objects = [];

        foreach (string raw in output.Split('\n', '\r'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match match = _lostObjectLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            string rawType = match.Groups["rawtype"].Value;
            string hash = match.Groups["objectid"].Value;
            LostObjectKind kind = match.Groups["objecttype"].Value switch
            {
                "commit" => LostObjectKind.Commit,
                "blob" => LostObjectKind.Blob,
                "tree" => LostObjectKind.Tree,
                "tag" => LostObjectKind.Tag,

                // "warning in tree" matches the outer alternation without an
                // objecttype group — upstream maps it to Other the same way.
                _ => LostObjectKind.Other,
            };

            objects.Add(new LostObject(kind, rawType, hash, string.Empty, null, string.Empty, string.Empty, string.Empty));
        }

        Enrich(module, objects);

        // Upstream orders by date descending (FormVerify.cs:344); nulls last so
        // metadata-less trees do not push the interesting commits down.
        return [.. objects.OrderByDescending(o => o.Date ?? DateTime.MinValue)];
    }

    // Fills in author/date/subject/parent per object type, replacing entries in place.
    private void Enrich(GitModule module, List<LostObject> objects)
    {
        List<int> commits = [];
        for (int i = 0; i < objects.Count; i++)
        {
            switch (objects[i].Kind)
            {
                case LostObjectKind.Commit:
                    commits.Add(i);
                    break;
                case LostObjectKind.Tag:
                    objects[i] = EnrichTag(module, objects[i]);
                    break;
                case LostObjectKind.Blob:
                    objects[i] = EnrichBlob(module, objects[i]);
                    break;
                default:
                    // Trees and "warning in tree" carry no metadata upstream either.
                    break;
            }
        }

        EnrichCommits(module, objects, commits);
    }

    // Commit metadata in ONE git call for all commits, like upstream
    // (FormVerify.cs:346-360). Two safety measures upstream lacks:
    //  * the format uses %x1f rather than a literal U+001F and contains NO SPACE, which
    //    matters because GitArgumentBuilder flattens its arguments into a single string
    //    that is then re-split on spaces — a format with a space in it would arrive as
    //    two arguments and silently lose a column;
    //  * upstream matches output lines to commits POSITIONALLY by index, which desyncs
    //    if git emits an unexpected number of lines. Here a count mismatch falls back to
    //    querying each commit on its own, so a single odd object cannot corrupt every
    //    other row.
    private void EnrichCommits(GitModule module, List<LostObject> objects, List<int> commits)
    {
        if (commits.Count == 0)
        {
            return;
        }

        const string format = "--pretty=format:%aN%x1f%s%x1f%ct%x1f%P";

        // Upstream's batch size: keep the command line well under the OS limit.
        const int batchSize = 30_000 / 41;

        for (int start = 0; start < commits.Count; start += batchSize)
        {
            List<int> slice = [.. commits.Skip(start).Take(batchSize)];
            GitArgumentBuilder args = new("show") { "--quiet", format };
            foreach (int index in slice)
            {
                args.Add(objects[index].Hash);
            }

            string[] lines = Execute(module, args);

            if (lines.Length == slice.Count)
            {
                for (int i = 0; i < slice.Count; i++)
                {
                    objects[slice[i]] = ApplyCommitMetadata(objects[slice[i]], lines[i]);
                }
            }
            else
            {
                foreach (int index in slice)
                {
                    GitArgumentBuilder one = new("show") { "--quiet", format, objects[index].Hash };
                    string[] single = Execute(module, one);
                    if (single.Length > 0)
                    {
                        objects[index] = ApplyCommitMetadata(objects[index], single[0]);
                    }
                }
            }
        }
    }

    private static string[] Execute(GitModule module, GitArgumentBuilder args)
    {
        using IDisposable locale = GitEnvironment.DiagnosticLocaleScope();
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return (result.AllOutput ?? string.Empty)
            .Split('\n')
            .Where(l => l.Length > 0)
            .ToArray();
    }

    private LostObject ApplyCommitMetadata(LostObject obj, string line)
    {
        Match match = _commitMetadata.Match(line.TrimEnd('\r'));
        if (!match.Success)
        {
            return obj;
        }

        DateTime? date = null;
        if (long.TryParse(match.Groups["date"].Value, out long unix))
        {
            date = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        }

        return obj with
        {
            Author = match.Groups["author"].Value,
            Subject = match.Groups["subject"].Value,
            Date = date,

            // Only the FIRST parent, as upstream stores.
            Parent = match.Groups["first_parent"].Success ? match.Groups["first_parent"].Value : string.Empty,
        };
    }

    private LostObject EnrichTag(GitModule module, LostObject obj)
    {
        string body;
        using (GitEnvironment.DiagnosticLocaleScope())
        {
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("cat-file") { "-p", obj.Hash },
                throwOnErrorExit: false);
            body = result.AllOutput ?? string.Empty;
        }

        Match match = _tagMetadata.Match(body.Replace("\r\n", "\n"));
        if (!match.Success)
        {
            return obj;
        }

        DateTime? date = null;
        if (long.TryParse(match.Groups["date"].Value, out long unix))
        {
            date = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        }

        string tagName = match.Groups["tagname"].Value.Trim();
        return obj with
        {
            Author = match.Groups["author"].Value.Trim(),
            Date = date,
            TagName = tagName,

            // Upstream shows "<tagname>:<message>" in the Subject column.
            Subject = $"{tagName}:{match.Groups["subject"].Value}",
            Parent = match.Groups["parent"].Value.Trim(),
        };
    }

    // Blobs carry no date of their own, so upstream falls back to the LOOSE OBJECT
    // FILE's timestamp (FormVerify.LostObject.cs:159-164). Deliberate deviation: it uses
    // FileInfo.CreationTime, which on Linux is frequently unavailable and then reports
    // the epoch; LastWriteTimeUtc is the honest equivalent here because a loose object
    // file is written once and never modified. A PACKED blob has no such file at all and
    // keeps a null date rather than a fabricated one.
    private static LostObject EnrichBlob(GitModule module, LostObject obj)
    {
        try
        {
            string gitDir = module.WorkingDirGitDir;
            if (string.IsNullOrEmpty(gitDir) || obj.Hash.Length < 3)
            {
                return obj;
            }

            string path = Path.Combine(gitDir, "objects", obj.Hash[..2], obj.Hash[2..]);
            FileInfo info = new(path);
            return info.Exists ? obj with { Date = info.LastWriteTime } : obj;
        }
        catch
        {
            return obj;
        }
    }
}
