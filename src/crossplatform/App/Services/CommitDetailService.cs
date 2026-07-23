using GitCommands;
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
    string AuthorDate,
    string Committer,
    string CommitDate,
    IReadOnlyList<string> ParentHashes,
    string Subject,
    string Message)
{
    /// <summary>Parent hashes joined for inline display; empty for a root commit.</summary>
    public string ParentsDisplay => string.Join("  ", ParentHashes);
}

/// <summary>
///  Loads full metadata for a single commit by reusing the Git Extensions core
///  (<see cref="GitContext.CreateModule"/> + <see cref="RevisionReader"/>), the
///  same code path the Windows app uses.
/// </summary>
public sealed class CommitDetailService
{
    /// <summary>
    ///  Loads author, committer, dates, parents, subject and the full commit
    ///  message for <paramref name="commitHash"/> in the repository at
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

        string[] parents = revision.ParentIds is { Count: > 0 } parentIds
            ? parentIds.Select(p => p.ToString()).ToArray()
            : [];

        string author = FormatPerson(revision.Author, revision.AuthorEmail);
        string committer = FormatPerson(revision.Committer, revision.CommitterEmail);

        string subject = revision.Subject ?? string.Empty;
        string message = revision.Body ?? subject;

        return new CommitDetailInfo(
            Hash: revision.ObjectId.ToString(),
            ShortHash: revision.ObjectId.ToShortString(),
            Author: author,
            AuthorDate: FormatDate(revision.AuthorDate),
            Committer: committer,
            CommitDate: FormatDate(revision.CommitDate),
            ParentHashes: parents,
            Subject: subject,
            Message: message);
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
}
