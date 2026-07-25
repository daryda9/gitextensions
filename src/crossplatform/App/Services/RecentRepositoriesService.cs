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
///  <para>The stored history rots: entries survive after their directory is
///  deleted (notably the ephemeral <c>.claude/worktrees/agent-*</c> checkouts
///  this port is developed in), and the raw list can hold duplicates that only
///  differ by a trailing separator. <see cref="LoadAsync"/> therefore prunes the
///  list and writes the pruned version back, so the rot is removed for good
///  instead of merely hidden at display time. All filesystem probing happens off
///  the UI thread (dead paths can stall a <c>stat</c>).</para>
/// </summary>
public sealed class RecentRepositoriesService
{
    /// <summary>
    ///  Returns the recent repository paths, most-recent first, with stale
    ///  entries dropped. Any pruning is persisted back to the core MRU.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadAsync()
    {
        IList<Repository> history = await RepositoryHistoryManager.Locals.LoadRecentHistoryAsync();

        // Probing the filesystem must never happen on the UI thread: a path that
        // pointed at a removed mount/worktree can block for seconds in stat().
        (List<string> paths, List<Repository> kept, bool changed) = await Task.Run(() =>
        {
            List<string> keptPaths = new(history.Count);
            List<Repository> keptRepos = new(history.Count);
            HashSet<string> seen = new(StringComparer.Ordinal);
            bool pruned = false;

            foreach (Repository repository in history)
            {
                string? path = Normalize(repository.Path);
                if (path is null || !seen.Add(path) || !IsUsableRepository(path))
                {
                    pruned = true;
                    continue;
                }

                keptPaths.Add(path);

                // Re-emit with the normalised path so the stored list converges
                // too (categories/anchors are preserved).
                if (!string.Equals(repository.Path, path, StringComparison.Ordinal))
                {
                    pruned = true;
                    repository.Path = path;
                }

                keptRepos.Add(repository);
            }

            return (keptPaths, keptRepos, pruned);
        });

        if (changed)
        {
            try
            {
                await RepositoryHistoryManager.Locals.SaveRecentHistoryAsync(kept);
            }
            catch
            {
                // Best-effort cleanup: a failed write must not hide the list.
            }
        }

        return paths;
    }

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
