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
/// </summary>
public sealed class RecentRepositoriesService
{
    /// <summary>
    ///  Returns the recent repository paths, most-recent first.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadAsync()
    {
        IList<Repository> history = await RepositoryHistoryManager.Locals.LoadRecentHistoryAsync();

        List<string> paths = new(history.Count);
        foreach (Repository repository in history)
        {
            string path = repository.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            // The core stores paths with a trailing separator; present clean roots.
            paths.Add(NormalizeForDisplay(path));
        }

        return paths;
    }

    /// <summary>
    ///  Records <paramref name="repoPath"/> as the most-recently used repository.
    /// </summary>
    public async Task AddAsync(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return;
        }

        // The core manager trims/normalises and moves the entry to the top.
        await RepositoryHistoryManager.Locals.AddAsMostRecentAsync(repoPath);
    }

    private static string NormalizeForDisplay(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
