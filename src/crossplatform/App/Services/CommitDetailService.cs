using System.Text.RegularExpressions;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  One abbreviated commit hash found inside a commit message, given as a span of
///  <see cref="CommitDetailInfo.Message"/> plus the full hash it resolves to.
///
///  <para>Upstream turns the same spans into <c>gitext://gotocommit/…</c> anchors
///  (<c>CommitDataBodyRenderer.cs:44,50-65</c>): "fixes abc1234" is the single most
///  common cross-reference in a message, and reading it without being able to follow
///  it is the difference between a changelog and a history.</para>
/// </summary>
public sealed record CommitMessageLink(int Start, int Length, string FullHash);

/// <summary>
///  <c>git describe</c> split into the tag it found and how many commits separate it
///  from the commit asked about, so the panel can print "v1.2.0 + 66 commits" (and
///  make the tag a link) instead of the raw <c>v1.2.0-66-g1234abc</c>. Same
///  decomposition as upstream's <c>GitDescribeProvider.Get</c>.
/// </summary>
public sealed record DescribeInfo(string Tag, string CommitCount)
{
    public static readonly DescribeInfo Empty = new(string.Empty, string.Empty);
}

/// <summary>
///  Full metadata for a single commit, projected from a core
///  <see cref="GitRevision"/> for display in the commit-detail view.
///  Field/type names are prefixed with <c>CommitDetail</c> to stay unique
///  across sibling views/services.
/// </summary>
public sealed record CommitDetailInfo(
    string Hash,
    string ShortHash,
    string Author,
    string AuthorName,
    string AuthorEmail,
    string AuthorDate,
    string AuthorDateRelative,
    string Committer,
    string CommitterEmail,
    string CommitDate,
    string CommitDateRelative,
    bool DatesDiffer,
    IReadOnlyList<string> ParentHashes,
    IReadOnlyList<string> ChildHashes,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> Tags,
    string DescribeTag,
    string Subject,
    string Message,
    IReadOnlyList<CommitMessageLink>? MessageLinks = null,
    string DescribeCommitCount = "",
    IReadOnlyDictionary<string, string>? RefHashes = null)
{
    /// <summary>
    ///  Short ref name (branch, remote branch or tag) to the full hash of the commit
    ///  it points at, for every ref in the repository. What makes the branch and tag
    ///  names in this panel navigable, as upstream's
    ///  <c>LinkFactory.CreateBranchLink/CreateTagLink</c> do
    ///  (<c>RefsFormatter.cs:30,41</c>). Annotated tags are already peeled to their
    ///  commit.
    /// </summary>
    public IReadOnlyDictionary<string, string> Refs => RefHashes ?? new Dictionary<string, string>();

    /// <summary>
    ///  The abbreviated hashes inside <see cref="Message"/> that resolve to a commit
    ///  of this repository, in ascending order and never overlapping.
    /// </summary>
    public IReadOnlyList<CommitMessageLink> Links => MessageLinks ?? [];

    /// <summary>Parent hashes joined for inline display; empty for a root commit.</summary>
    public string ParentsDisplay => string.Join("  ", ParentHashes);

    /// <summary><see langword="true"/> when the author and committer identities differ.</summary>
    public bool CommitterDiffers =>
        !string.IsNullOrEmpty(Committer) && !string.Equals(Author, Committer, StringComparison.Ordinal);

    /// <summary>
    ///  The label for the author-date row: upstream renames it to "Author date"
    ///  as soon as the two timestamps differ, keeping the plain "Date" for the
    ///  common case where a commit was never rewritten
    ///  (<c>CommitDataHeaderRenderer.Render</c>).
    /// </summary>
    public bool ShowCommitDate => DatesDiffer;
}

/// <summary>
///  Loads full metadata for a single commit by reusing the Git Extensions core
///  (<see cref="GitContext.CreateModule"/> + <see cref="RevisionReader"/>), the
///  same code path the Windows app uses. Enrichment data (children, containing
///  branches/tags, describe) is gathered via extra git invocations; every one is
///  best-effort and never throws.
/// </summary>
public sealed partial class CommitDetailService
{
    // Upper bound on the distinct hash candidates resolved per message (see FindMessageLinks).
    private const int MaxResolvedCandidates = 64;

    /// <summary>
    ///  Loads author, committer, dates, parents, children, containing branches
    ///  and tags, the nearest describe tag, subject and the full commit message
    ///  for <paramref name="commitHash"/> in the repository at
    ///  <paramref name="repoPath"/>.
    /// </summary>
    /// <returns>The commit detail, or <see langword="null"/> if the commit could not be read.</returns>
    public CommitDetailInfo? LoadCommit(string repoPath, string commitHash, CancellationToken cancellationToken = default)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        RevisionReader reader = new(module);

        GitRevision? revision = reader.GetRevision(commitHash, hasNotes: false, throwOnError: false, cancellationToken);
        if (revision is null)
        {
            return null;
        }

        string fullHash = revision.ObjectId.ToString();

        string[] parents = revision.ParentIds is { Count: > 0 } parentIds
            ? parentIds.Select(p => p.ToString()).ToArray()
            : [];

        string authorName = revision.Author ?? string.Empty;
        string authorEmail = revision.AuthorEmail ?? string.Empty;
        string author = FormatPerson(authorName, authorEmail);
        string committer = FormatPerson(revision.Committer, revision.CommitterEmail);

        string subject = revision.Subject ?? string.Empty;
        string message = revision.Body ?? subject;

        IReadOnlyList<string> children = LoadChildren(module, fullHash, cancellationToken);
        IReadOnlyList<string> branches = LoadBranches(module, fullHash, cancellationToken);

        // Upstream's TagsComparer orders tags by tagger date, newest first
        // (CommitInfo.cs:848-1002); git can do that itself. Lightweight tags have no
        // tagger date and sort last, which is also where upstream leaves them.
        IReadOnlyList<string> tags = LoadRefs(module, new GitArgumentBuilder("tag")
        {
            "--contains", fullHash, "--sort=-taggerdate",
        }, cancellationToken);
        DescribeInfo describe = LoadDescribe(module, revision.ObjectId, cancellationToken);
        IReadOnlyList<CommitMessageLink> messageLinks = FindMessageLinks(module, message, fullHash, cancellationToken);

        return new CommitDetailInfo(
            Hash: fullHash,
            ShortHash: revision.ObjectId.ToShortString(),
            Author: author,
            AuthorName: authorName,
            AuthorEmail: authorEmail,
            AuthorDate: FormatDate(revision.AuthorDate),
            AuthorDateRelative: FormatRelative(revision.AuthorDate),
            Committer: committer,
            CommitterEmail: revision.CommitterEmail ?? string.Empty,
            CommitDate: FormatDate(revision.CommitDate),
            CommitDateRelative: FormatRelative(revision.CommitDate),

            // Upstream compares the timestamps, not the identities: on amend,
            // rebase and cherry-pick the person is unchanged while the dates
            // move apart, and that is exactly when the commit date matters.
            DatesDiffer: !revision.AuthorDate.Equals(revision.CommitDate),
            ParentHashes: parents,
            ChildHashes: children,
            Branches: branches,
            Tags: tags,
            DescribeTag: describe.Tag,
            Subject: subject,
            Message: message,
            MessageLinks: messageLinks,
            DescribeCommitCount: describe.CommitCount,
            RefHashes: LoadRefHashes(module, cancellationToken));
    }

    /// <summary>
    ///  Resolves the direct children of <paramref name="fullHash"/> by scanning
    ///  <c>git rev-list --all --children</c> for the line whose first token is the
    ///  commit; the remaining tokens on that line are the child hashes.
    /// </summary>
    private static IReadOnlyList<string> LoadChildren(GitModule module, string fullHash, CancellationToken token)
    {
        try
        {
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("rev-list") { "--all", "--children" },
                throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return [];
            }

            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 1 && tokens[0] == fullHash)
                {
                    return tokens.Skip(1).ToArray();
                }
            }

            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///  Maps every short ref name to the full hash of the commit it points at, in one
    ///  <c>for-each-ref</c> (not one <c>rev-parse</c> per pill). Annotated tags carry
    ///  their own object hash in <c>%(objectname)</c>, so <c>%(*objectname)</c> — the
    ///  peeled commit — is preferred whenever it is present; without it a tag link
    ///  would point at the tag object and navigate nowhere.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadRefHashes(GitModule module, CancellationToken token)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        try
        {
            token.ThrowIfCancellationRequested();
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("for-each-ref")
                {
                    "--format=%(refname:short)\t%(objectname)\t%(*objectname)",
                    "refs/heads", "refs/remotes", "refs/tags",
                },
                throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return map;
            }

            foreach (string line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0)
                {
                    continue;
                }

                string hash = parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : parts[1];
                if (hash.Length > 0)
                {
                    map[parts[0]] = hash;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A ref list that could not be read means inert names, never an error.
        }

        return map;
    }

    /// <summary>
    ///  The local branches containing <paramref name="fullHash"/>, with the checked-out
    ///  one first.
    ///
    ///  <para>Upstream's <c>BranchComparer</c> (<c>CommitInfo.cs:848-1002</c>) puts the
    ///  current branch first, then applies the configured ordering criteria, then
    ///  locals before remotes. Only the first and the last of those are reproduced
    ///  here: the current branch is what a reader looks for, and locals already precede
    ///  remotes because the two lists are concatenated in that order
    ///  (<c>CommitDetailView.VisibleBranches</c>). The configurable middle rank is left
    ///  for whoever ports <c>AppSettings.BranchOrderingCriteria</c> along with the
    ///  setting that drives it — ordering by a criterion the user cannot see or change
    ///  would be worse than git's own order, not better.</para>
    /// </summary>
    private static IReadOnlyList<string> LoadBranches(GitModule module, string fullHash, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("branch")
                {
                    "--contains", fullHash, "--format=%(HEAD)\t%(refname:short)",
                },
                throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return [];
            }

            List<string> current = [];
            List<string> others = [];
            foreach (string raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = raw.TrimEnd('\r').Split('\t');
                if (parts.Length < 2 || parts[1].Length == 0)
                {
                    continue;
                }

                (parts[0] == "*" ? current : others).Add(parts[1]);
            }

            return [.. current, .. others.Distinct()];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> LoadRefs(GitModule module, GitArgumentBuilder args, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return [];
            }

            return result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.TrimStart('*', ' ', '+'))
                .Where(l => l.Length > 0)
                .Distinct()
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///  Runs <c>git describe</c> exactly as upstream does — <c>--tags --first-parent
    ///  --abbrev=40</c> (<c>GitModule.GetDescribe</c>) — and splits the result the way
    ///  <c>GitDescribeProvider.Get</c> does.
    ///
    ///  <para>The full abbreviation is what makes the split safe: the trailing
    ///  <c>-g&lt;hash&gt;</c> is only stripped when the hash is <i>this</i> commit's,
    ///  so a tag whose own name ends in something like <c>-g0123</c> is left whole
    ///  instead of being mistaken for a describe suffix.</para>
    /// </summary>
    private static DescribeInfo LoadDescribe(GitModule module, ObjectId commitId, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("describe") { "--tags", "--first-parent", "--abbrev=40", commitId },
                throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return DescribeInfo.Empty;
            }

            string description = result.StandardOutput.Trim();
            if (description.Length == 0)
            {
                return DescribeInfo.Empty;
            }

            int hashPos = description.LastIndexOf("-g", StringComparison.OrdinalIgnoreCase);
            if (hashPos < 0 || description[(hashPos + 2)..] != commitId.ToString())
            {
                // The commit is tagged itself, or the "-g…" is part of the tag name.
                return new DescribeInfo(description, string.Empty);
            }

            description = description[..hashPos];
            int countPos = description.LastIndexOf('-');
            return countPos < 0
                ? new DescribeInfo(description, string.Empty)
                : new DescribeInfo(description[..countPos], description[(countPos + 1)..]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DescribeInfo.Empty;
        }
    }

    // Upstream's own candidate pattern (GitRevision.Sha1HashShortRegex): 7-40 hex
    // characters on word boundaries, excluding anything that runs into an "@" so an
    // e-mail address local part is never taken for a hash.
    [GeneratedRegex(@"\b[a-f\d]{7,40}\b(?![^@\s]*@)", RegexOptions.ExplicitCapture)]
    private static partial Regex ShortHashRegex { get; }

    /// <summary>
    ///  Finds every abbreviated hash in <paramref name="message"/> that really names a
    ///  commit of this repository, returning the span to linkify and the full hash to
    ///  navigate to. Blocking; runs on the caller's background thread.
    ///
    ///  <para>Resolution is <c>rev-parse --verify --quiet &lt;prefix&gt;^{commit}</c>,
    ///  i.e. upstream's <c>TryResolvePartialCommitId</c> — a candidate that is merely
    ///  hex-shaped, or that names a blob or a tree, produces no link. Identical
    ///  candidates are resolved once however often they occur, and the commit's own
    ///  hash is skipped: a self-link would navigate nowhere.</para>
    /// </summary>
    private static IReadOnlyList<CommitMessageLink> FindMessageLinks(
        GitModule module, string message, string fullHash, CancellationToken token)
    {
        if (string.IsNullOrEmpty(message))
        {
            return [];
        }

        List<CommitMessageLink> links = [];
        Dictionary<string, string?> resolved = new(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ShortHashRegex.Matches(message))
        {
            token.ThrowIfCancellationRequested();

            string candidate = match.Value;
            if (!resolved.TryGetValue(candidate, out string? full))
            {
                // Each unseen candidate costs one git process. A message that pastes a
                // hex dump would otherwise spawn hundreds while the panel waits, so the
                // lookups stop at a bound no honest message reaches; the text is still
                // shown in full, just without further links.
                if (resolved.Count >= MaxResolvedCandidates)
                {
                    break;
                }

                full = ResolveCommitPrefix(module, candidate);
                resolved[candidate] = full;
            }

            if (full is not null && full != fullHash)
            {
                links.Add(new CommitMessageLink(match.Index, match.Length, full));
            }
        }

        return links;
    }

    private static string? ResolveCommitPrefix(GitModule module, string prefix)
    {
        try
        {
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("rev-parse") { "--verify", "--quiet", $"{prefix}^{{commit}}" },
                throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return null;
            }

            string output = result.StandardOutput.Trim();

            // Upstream requires the resolved hash to start with the prefix: rev-parse
            // also honours refs and @{…} syntax, and a branch that happens to be named
            // like a hex string must not silently become a link to somewhere else.
            return output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && output.Length == 40
                ? output
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatPerson(string? name, string? email)
    {
        name ??= string.Empty;
        return string.IsNullOrEmpty(email) ? name : $"{name} <{email}>";
    }

    private static string FormatDate(DateTime date)
        => date == DateTime.MaxValue || date == DateTime.MinValue
            ? string.Empty
            : date.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Renders a coarse "N units ago" string, mirroring the grid's relative dates.</summary>
    private static string FormatRelative(DateTime date)
    {
        if (date == DateTime.MaxValue || date == DateTime.MinValue)
        {
            return string.Empty;
        }

        DateTime utc = date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();
        TimeSpan span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        double seconds = span.TotalSeconds;
        if (seconds < 60)
        {
            return "just now";
        }

        (double value, string unit) = seconds switch
        {
            < 3600 => (span.TotalMinutes, "minute"),
            < 86400 => (span.TotalHours, "hour"),
            < 2592000 => (span.TotalDays, "day"),
            < 31536000 => (span.TotalDays / 30, "month"),
            _ => (span.TotalDays / 365, "year"),
        };

        int n = (int)value;
        return n <= 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";
    }
}
