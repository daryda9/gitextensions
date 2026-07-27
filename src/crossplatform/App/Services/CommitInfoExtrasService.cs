using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The extra facts the commit-info panel needs only when the corresponding
///  visibility toggle is on: the remote-tracking branches that contain the
///  commit, and the message body of the annotated tags that point at it.
/// </summary>
/// <param name="RemoteBranches">Remote-tracking branches containing the commit ("origin/master", …).</param>
/// <param name="AnnotatedTags">Annotated tags pointing at the commit, in the order git listed them, with their messages.</param>
/// <param name="Notes">The commit's git note (<c>refs/notes/commits</c>), or empty when it has none.</param>
public sealed record CommitInfoExtras(
    IReadOnlyList<string> RemoteBranches,
    IReadOnlyList<CommitInfoAnnotatedTag> AnnotatedTags,
    string Notes)
{
    /// <summary>Nothing loaded — what every failed or skipped lookup yields.</summary>
    public static CommitInfoExtras Empty { get; } = new([], [], string.Empty);
}

/// <summary>An annotated tag and the message stored in its tag object.</summary>
public sealed record CommitInfoAnnotatedTag(string Name, string Message);

/// <summary>
///  Loads <see cref="CommitInfoExtras"/> through the core <see cref="GitModule"/>,
///  the same way <see cref="CommitDetailService"/> gathers its enrichment data:
///  extra git invocations, each best-effort, none of which ever throws (bar
///  cancellation). Must be called off the UI thread.
///
///  <para>Upstream gets the equivalent data from
///  <c>GitModule.GetAllBranchesWhichContainGivenCommit</c> (with
///  <c>getRemote: true</c>) and <c>GitModule.GetTagMessage</c>, driven by
///  <c>CommitInfo.LoadAnnotatedTagInfoAsync</c>; here both are plain
///  <c>git</c> calls so nothing has to be re-plumbed through the shared core.</para>
/// </summary>
public sealed class CommitInfoExtrasService
{
    /// <summary>
    ///  Loads the remote-tracking branches containing <paramref name="commitHash"/>
    ///  and/or the annotated tags pointing at it. Each half is skipped when the
    ///  caller says it is not needed, so an unchecked toggle costs no git call.
    /// </summary>
    public CommitInfoExtras Load(
        string repoPath,
        string commitHash,
        bool wantRemoteBranches,
        bool wantAnnotatedTags,
        CancellationToken cancellationToken = default)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            IReadOnlyList<string> remotes = wantRemoteBranches
                ? LoadRemoteBranches(module, commitHash, cancellationToken)
                : [];
            IReadOnlyList<CommitInfoAnnotatedTag> tags = wantAnnotatedTags
                ? LoadAnnotatedTags(module, commitHash, cancellationToken)
                : [];
            return new CommitInfoExtras(remotes, tags, LoadNotes(module, commitHash, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CommitInfoExtras.Empty;
        }
    }

    /// <summary>
    ///  Replaces the commit's note with <paramref name="text"/> (or removes it when
    ///  the text is blank), without ever spawning an editor:
    ///  <c>git notes add -f -F -</c> reads the message from stdin, so a multi-line
    ///  note written in-app goes straight in. Upstream instead runs
    ///  <c>git notes edit</c>, which opens <c>core.editor</c> — unusable from the
    ///  port, which has no terminal editor to hand the commit over to.
    /// </summary>
    /// <returns>Empty on success, or git's error output.</returns>
    public string SaveNotes(string repoPath, string commitHash, string text)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string note = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

            if (note.Length == 0)
            {
                ExecutionResult removed = module.GitExecutable.Execute(
                    new GitArgumentBuilder("notes") { "remove", "--ignore-missing", commitHash },
                    throwOnErrorExit: false);
                return removed.ExitedSuccessfully ? string.Empty : ErrorOf(removed);
            }

            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("notes") { "add", "-f", "-F", "-", commitHash },
                writeInput: writer => writer.Write(note),
                throwOnErrorExit: false);
            return result.ExitedSuccessfully ? string.Empty : ErrorOf(result);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string ErrorOf(ExecutionResult result)
    {
        string error = result.AllOutput.Trim();
        return error.Length > 0 ? error : $"git exited with code {result.ExitCode}";
    }

    /// <summary>The commit's note, or empty when it has none (a missing note is not an error).</summary>
    public static string LoadNotes(GitModule module, string commitHash, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("notes") { "show", commitHash },
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

    /// <summary>Reads the note of a commit in the repository at <paramref name="repoPath"/>.</summary>
    public string LoadNotes(string repoPath, string commitHash)
    {
        try
        {
            return LoadNotes(GitContext.CreateModule(repoPath), commitHash, CancellationToken.None);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> LoadRemoteBranches(
        GitModule module, string commitHash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ExecutionResult result = module.GitExecutable.Execute(
            new GitArgumentBuilder("branch")
            {
                "-r", "--contains", commitHash, "--format=%(refname:short)",
            },
            throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return [];
        }

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0 && !l.Contains("->", StringComparison.Ordinal))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    ///  Annotated tags whose ref points at the commit. Lightweight tags are
    ///  filtered out by object type — they carry no message, which is exactly
    ///  what upstream's dereference check amounts to.
    /// </summary>
    private static IReadOnlyList<CommitInfoAnnotatedTag> LoadAnnotatedTags(
        GitModule module, string commitHash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ExecutionResult listed = module.GitExecutable.Execute(
            new GitArgumentBuilder("tag")
            {
                "--points-at", commitHash, "--format=%(objecttype)%09%(refname:short)",
            },
            throwOnErrorExit: false);
        if (!listed.ExitedSuccessfully)
        {
            return [];
        }

        List<CommitInfoAnnotatedTag> tags = [];
        foreach (string line in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            // "tag" = an annotated (or signed) tag object; "commit" = lightweight.
            if (!line.AsSpan(0, tab).SequenceEqual("tag"))
            {
                continue;
            }

            string name = line[(tab + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            string message = LoadTagMessage(module, name, token);
            if (message.Length > 0)
            {
                tags.Add(new CommitInfoAnnotatedTag(name, message));
            }
        }

        return tags;
    }

    private static string LoadTagMessage(GitModule module, string tagName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ExecutionResult result = module.GitExecutable.Execute(
            new GitArgumentBuilder("tag")
            {
                "-l", "--format=%(contents)", tagName,
            },
            throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return string.Empty;
        }

        // A signed tag's contents end with the PGP block; upstream shows the
        // message only, so the signature is dropped.
        string text = result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
        int sig = text.IndexOf("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal);
        if (sig >= 0)
        {
            text = text[..sig];
        }

        return text.Trim();
    }
}
