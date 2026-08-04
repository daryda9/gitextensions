namespace GitExtensions.Avalonia.Services;

/// <summary>
/// Immutable repository-navigation data shared by the objects tree and toolbar.
/// </summary>
public sealed record RepositoryNavigationSnapshot(
    string RepositoryPath,
    SubmoduleHierarchy Submodules,
    IReadOnlyList<WorktreeRow> Worktrees);

/// <summary>
/// Loads navigation data once per normalized repository path. Concurrent callers
/// share the same task; invalidation starts a new generation without allowing an
/// older completion or failure to replace it.
/// </summary>
public sealed class RepositoryNavigationSnapshotService
{
    private readonly Func<string, SubmoduleHierarchy> _discoverSubmodules;
    private readonly Func<string, IReadOnlyList<WorktreeRow>> _listWorktrees;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(PathComparer);
    private readonly Dictionary<string, long> _generations = new(PathComparer);

    public RepositoryNavigationSnapshotService()
        : this(
            repo => new SubmoduleService().DiscoverHierarchy(repo),
            repo => new WorktreeService().ListWorktrees(repo))
    {
    }

    public RepositoryNavigationSnapshotService(
        Func<string, SubmoduleHierarchy> discoverSubmodules,
        Func<string, IReadOnlyList<WorktreeRow>> listWorktrees)
    {
        _discoverSubmodules = discoverSubmodules ?? throw new ArgumentNullException(nameof(discoverSubmodules));
        _listWorktrees = listWorktrees ?? throw new ArgumentNullException(nameof(listWorktrees));
    }

    /// <summary>
    /// Returns the shared load task for this repository generation. Both blocking
    /// delegates always run on worker threads, even when the caller is the UI thread.
    /// </summary>
    public Task<RepositoryNavigationSnapshot> GetAsync(string repositoryPath)
    {
        string path = Normalize(repositoryPath);
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out Entry? existing))
            {
                return existing.Task;
            }

            long generation = _generations.GetValueOrDefault(path);
            Entry entry = new(generation);
            _entries[path] = entry;
            entry.Task = LoadAsync(path, entry);
            return entry.Task;
        }
    }

    /// <summary>Invalidates one path. An in-flight caller may finish, but cannot be cached again.</summary>
    public void Invalidate(string repositoryPath)
    {
        string path = Normalize(repositoryPath);
        lock (_gate)
        {
            _generations[path] = _generations.GetValueOrDefault(path) + 1;
            _entries.Remove(path);
        }
    }

    /// <summary>Invalidates every cached path and generation.</summary>
    public void InvalidateAll()
    {
        lock (_gate)
        {
            foreach (string path in _entries.Keys)
            {
                _generations[path] = _generations.GetValueOrDefault(path) + 1;
            }

            _entries.Clear();
        }
    }

    private async Task<RepositoryNavigationSnapshot> LoadAsync(string path, Entry entry)
    {
        try
        {
            Task<SubmoduleHierarchy> submodulesTask = Task.Run(() => _discoverSubmodules(path));
            Task<IReadOnlyList<WorktreeRow>> worktreesTask = Task.Run(() => _listWorktrees(path));
            await Task.WhenAll(submodulesTask, worktreesTask).ConfigureAwait(false);

            SubmoduleHierarchy hierarchy = await submodulesTask.ConfigureAwait(false);
            IReadOnlyList<SubmoduleRow> nodes = Array.AsReadOnly(hierarchy.Nodes.ToArray());
            IReadOnlyList<WorktreeRow> worktrees = Array.AsReadOnly((await worktreesTask.ConfigureAwait(false)).ToArray());
            return new(path, hierarchy with { Nodes = nodes }, worktrees);
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(path, out Entry? current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(path);
                }
            }

            throw;
        }
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class Entry(long generation)
    {
        public long Generation { get; } = generation;
        public Task<RepositoryNavigationSnapshot> Task { get; set; } = null!;
    }
}
