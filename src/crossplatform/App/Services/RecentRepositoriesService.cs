using GitCommands.UserRepositoryHistory;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Recent-repositories (MRU) access for the Avalonia port.
///
///  This reuses the Git Extensions core <see cref="RepositoryHistoryManager"/>
///  (the very same MRU the Windows app persists), rather than a bespoke JSON
///  file. That manager persists through <c>AppSettings</c>, which the port has
///  already proven to work headless on Linux (milestone M3 reads/writes core
///  settings). Reusing it means the Linux app shares one recent-repositories
///  list with the rest of the reused core instead of forking a second store.
///
///  <para>The stored history rots in two different ways, and they are NOT
///  treated alike:</para>
///  <list type="bullet">
///   <item>Noise the user never created — duplicates that differ only by a
///    trailing separator, and the ephemeral <c>.claude/worktrees/agent-*</c>
///    checkouts this port is developed in — is dropped silently and the pruned
///    list is written back.</item>
///   <item>Entries whose directory is simply gone are <em>kept</em> and reported
///    with <see cref="RecentRepositoryEntry.Exists"/> set to <see
///    langword="false"/>. Upstream does the same (it flags them with an error
///    icon and only removes them once the user confirms,
///    <c>InvalidRepositoryRemover</c>); deleting them behind the user's back
///    also made "Remove missing projects from the list" unreachable, since
///    nothing missing ever survived loading.</item>
///  </list>
///  <para>All filesystem probing happens off the UI thread (dead paths can stall
///  a <c>stat</c>).</para>
/// </summary>
public sealed class RecentRepositoriesService
{
    /// <summary>
    ///  One MRU entry: its normalised path and whether it still resolves to a
    ///  git working copy on disk.
    /// </summary>
    public sealed record RecentRepositoryEntry(string Path, bool Exists);

    /// <summary>
    ///  Returns the recent repositories, most-recent first, each flagged with
    ///  whether it still exists. Duplicates and ephemeral agent worktrees are
    ///  dropped (and the pruning persisted); missing repositories are kept so the
    ///  caller can show them as broken and offer to remove them.
    /// </summary>
    public async Task<IReadOnlyList<RecentRepositoryEntry>> LoadEntriesAsync()
    {
        IList<Repository> history = await RepositoryHistoryManager.Locals.LoadRecentHistoryAsync();

        (List<RecentRepositoryEntry> entries, List<Repository> kept, bool changed) = await Task.Run(() =>
        {
            List<RecentRepositoryEntry> result = new(history.Count);
            List<Repository> keptRepos = new(history.Count);
            HashSet<string> seen = new(StringComparer.Ordinal);
            bool pruned = false;

            foreach (Repository repository in history)
            {
                string? path = Normalize(repository.Path);
                if (path is null || !seen.Add(path) || IsEphemeralWorktree(path))
                {
                    pruned = true;
                    continue;
                }

                result.Add(new RecentRepositoryEntry(path, IsUsableRepository(path)));

                // Re-emit with the normalised path so the stored list converges
                // too (categories/anchors are preserved).
                if (!string.Equals(repository.Path, path, StringComparison.Ordinal))
                {
                    pruned = true;
                    repository.Path = path;
                }

                keptRepos.Add(repository);
            }

            return (result, keptRepos, pruned);
        });

        if (changed)
        {
            await SaveAsync(kept);
        }

        return entries;
    }

    /// <summary>
    ///  Drops <paramref name="repoPath"/> from the MRU (upstream's "Remove project
    ///  from the list"). Missing entries can be removed too — that is the point.
    /// </summary>
    /// <returns><see langword="true"/> when an entry was actually removed.</returns>
    public async Task<bool> RemoveAsync(string repoPath)
    {
        string? target = Normalize(repoPath);
        if (target is null)
        {
            return false;
        }

        IList<Repository> history = await RepositoryHistoryManager.Locals.LoadRecentHistoryAsync();
        List<Repository> kept = history
            .Where(r => !string.Equals(Normalize(r.Path), target, StringComparison.Ordinal))
            .ToList();

        if (kept.Count == history.Count)
        {
            return false;
        }

        await SaveAsync(kept);
        return true;
    }

    /// <summary>
    ///  Drops every entry whose directory no longer holds a git working copy
    ///  (upstream's "Remove missing projects from the list"). The probing runs off
    ///  the UI thread.
    /// </summary>
    /// <returns>How many entries were removed.</returns>
    public async Task<int> RemoveMissingAsync()
    {
        IList<Repository> history = await RepositoryHistoryManager.Locals.LoadRecentHistoryAsync();

        List<Repository> kept = await Task.Run(() => history
            .Where(r => Normalize(r.Path) is string p && IsUsableRepository(p))
            .ToList());

        int removed = history.Count - kept.Count;
        if (removed > 0)
        {
            await SaveAsync(kept);
        }

        return removed;
    }

    /// <summary>
    ///  Reads the checked-out branch of <paramref name="repoPath"/> straight from
    ///  <c>.git/HEAD</c>, returning a short hash for a detached HEAD and
    ///  <see langword="null"/> when nothing can be read.
    ///
    ///  <para>Upstream shows the same value on every tile and gets it from a cache
    ///  warmed in parallel (<c>RepositoryHistoryUIService</c>). Parsing the HEAD
    ///  file costs a single small read instead of a <c>git</c> process per row,
    ///  which is what makes a per-row branch name affordable at all — but it is
    ///  still I/O, so callers must stay off the UI thread.</para>
    /// </summary>
    public static string? ReadCurrentBranch(string repoPath)
    {
        try
        {
            string dotGit = Path.Combine(repoPath, ".git");
            string gitDir = dotGit;

            if (File.Exists(dotGit))
            {
                // Worktree or submodule: ".git" is a file pointing at the real dir.
                string content = File.ReadAllText(dotGit).Trim();
                const string prefix = "gitdir:";
                if (!content.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }

                string target = content[prefix.Length..].Trim();
                gitDir = Path.IsPathRooted(target) ? target : Path.Combine(repoPath, target);
            }
            else if (!Directory.Exists(dotGit))
            {
                return null;
            }

            string headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile))
            {
                return null;
            }

            string head = File.ReadAllText(headFile).Trim();
            const string refPrefix = "ref: refs/heads/";
            if (head.StartsWith(refPrefix, StringComparison.Ordinal))
            {
                return head[refPrefix.Length..].Trim();
            }

            // Detached HEAD: upstream shows the (short) commit it points at.
            return head.Length >= 7 ? $"({head[..7]})" : null;
        }
        catch
        {
            // Unreadable/permission denied: no branch to show, never a failure.
            return null;
        }
    }

    private static async Task SaveAsync(IList<Repository> repositories)
    {
        try
        {
            await RepositoryHistoryManager.Locals.SaveRecentHistoryAsync(repositories);
        }
        catch
        {
            // Best-effort: a failed write must never take the list down with it.
        }
    }

    /// <summary>
    ///  Returns the recent repository paths, most-recent first, for callers that
    ///  do not care whether an entry still exists. Missing repositories are part
    ///  of the result — see <see cref="LoadEntriesAsync"/> for the reasoning.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadAsync()
        => (await LoadEntriesAsync()).Select(e => e.Path).ToList();

    /// <summary>
    ///  Records <paramref name="repoPath"/> as the most-recently used repository.
    ///  Ephemeral agent worktrees are never recorded.
    /// </summary>
    public async Task AddAsync(string repoPath)
    {
        string? path = Normalize(repoPath);
        if (path is null || IsEphemeralWorktree(path))
        {
            return;
        }

        // The core manager trims/normalises and moves the entry to the top.
        await RepositoryHistoryManager.Locals.AddAsMostRecentAsync(path);
    }

    /// <summary>
    ///  Trims trailing separators and resolves relative paths to a full path.
    ///  Returns <see langword="null"/> for input that cannot be a repository root.
    /// </summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.Trim();
        string stripped = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (stripped.Length == 0)
        {
            // A bare root ("/"): keep as-is rather than collapsing to empty.
            stripped = trimmed;
        }

        try
        {
            string full = Path.GetFullPath(stripped);
            string clean = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return clean.Length == 0 ? full : clean;
        }
        catch
        {
            // Malformed path (invalid characters, too long, ...): unusable.
            return null;
        }
    }

    /// <summary>
    ///  A history entry is worth keeping only when it still exists on disk and
    ///  still looks like a git working copy, and is not one of the throw-away
    ///  agent worktrees created under <c>.claude/worktrees</c>.
    /// </summary>
    private static bool IsUsableRepository(string path)
    {
        if (IsEphemeralWorktree(path))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return false;
            }

            // ".git" is a directory in a normal clone and a file in a worktree
            // or submodule; either counts as a valid repository root.
            string dotGit = Path.Combine(path, ".git");
            return Directory.Exists(dotGit) || File.Exists(dotGit);
        }
        catch
        {
            // Permission denied / IO error: treat as unusable.
            return false;
        }
    }

    /// <summary>
    ///  Detects paths living under a <c>.claude/worktrees</c> directory, i.e. the
    ///  disposable per-agent checkouts that must never linger in the MRU.
    /// </summary>
    private static bool IsEphemeralWorktree(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], ".claude", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[i + 1], "worktrees", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
