using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Services;

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
    string CommitDate,
    string CommitDateRelative,
    IReadOnlyList<string> ParentHashes,
    IReadOnlyList<string> ChildHashes,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> Tags,
    string DescribeTag,
    string Subject,
    string Message)
{
    /// <summary>Parent hashes joined for inline display; empty for a root commit.</summary>
    public string ParentsDisplay => string.Join("  ", ParentHashes);

    /// <summary><see langword="true"/> when the author and committer identities differ.</summary>
    public bool CommitterDiffers =>
        !string.IsNullOrEmpty(Committer) && !string.Equals(Author, Committer, StringComparison.Ordinal);
}

/// <summary>
///  Loads full metadata for a single commit by reusing the Git Extensions core
///  (<see cref="GitContext.CreateModule"/> + <see cref="RevisionReader"/>), the
///  same code path the Windows app uses. Enrichment data (children, containing
///  branches/tags, describe) is gathered via extra git invocations; every one is
///  best-effort and never throws.
/// </summary>
public sealed class CommitDetailService
{
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
        IReadOnlyList<string> branches = LoadRefs(module, new GitArgumentBuilder("branch")
        {
            "--contains", fullHash, "--format=%(refname:short)",
        }, cancellationToken);
        IReadOnlyList<string> tags = LoadRefs(module, new GitArgumentBuilder("tag")
        {
            "--contains", fullHash,
        }, cancellationToken);
        string describe = LoadDescribe(module, fullHash, cancellationToken);

        return new CommitDetailInfo(
            Hash: fullHash,
            ShortHash: revision.ObjectId.ToShortString(),
            Author: author,
            AuthorName: authorName,
            AuthorEmail: authorEmail,
            AuthorDate: FormatDate(revision.AuthorDate),
            AuthorDateRelative: FormatRelative(revision.AuthorDate),
            Committer: committer,
            CommitDate: FormatDate(revision.CommitDate),
            CommitDateRelative: FormatRelative(revision.CommitDate),
            ParentHashes: parents,
            ChildHashes: children,
            Branches: branches,
            Tags: tags,
            DescribeTag: describe,
            Subject: subject,
            Message: message);
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

    private static string LoadDescribe(GitModule module, string fullHash, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("describe") { "--tags", fullHash },
                throwOnErrorExit: false);
            return result.ExitedSuccessfully ? result.StandardOutput.Trim() : string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return string.Empty;
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
