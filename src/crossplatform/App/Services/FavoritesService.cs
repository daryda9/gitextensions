using System.Text.Json;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Persists the user's list of favorite repositories for the Avalonia port.
///
///  <para>The core MRU (<see cref="RecentRepositoriesService"/>) has no separate
///  "favorites" concept the port can lean on, and <see cref="UiState"/> is a
///  fixed layout/preferences record the shell does not own. So — exactly as
///  <see cref="UiStateService"/> does for UI state — favorites live in their own
///  small JSON file under the user's config directory:
///  <c>$XDG_CONFIG_HOME</c> (or <see cref="Environment.SpecialFolder.ApplicationData"/>,
///  or <c>~/.config</c>) → <c>GitExtensions.Avalonia/favorites.json</c>.</para>
///
///  <para>All operations are best-effort: a missing or corrupt file yields an
///  empty list, and a write failure never throws.</para>
/// </summary>
public sealed class FavoritesService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public FavoritesService() => _path = ResolvePath();

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _path;

    /// <summary>Loads the favorite repository paths (in the stored order).</summary>
    public IReadOnlyList<string> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                List<string>? list = JsonSerializer.Deserialize<List<string>>(json, Options);
                if (list is not null)
                {
                    return Clean(list);
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → empty list.
        }

        return Array.Empty<string>();
    }

    /// <summary>
    ///  Adds <paramref name="repoPath"/> as a favorite (moved/kept at the top),
    ///  de-duplicating case-insensitively. Returns the updated list.
    /// </summary>
    public IReadOnlyList<string> Add(string repoPath)
    {
        string normalized = Normalize(repoPath);
        if (normalized.Length == 0)
        {
            return Load();
        }

        List<string> list = new(Load());
        list.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, normalized);
        Save(list);
        return list;
    }

    /// <summary>Removes <paramref name="repoPath"/> from favorites; returns the updated list.</summary>
    public IReadOnlyList<string> Remove(string repoPath)
    {
        string normalized = Normalize(repoPath);
        List<string> list = new(Load());
        list.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        Save(list);
        return list;
    }

    /// <summary>True when <paramref name="repoPath"/> is already a favorite.</summary>
    public bool Contains(string repoPath)
    {
        string normalized = Normalize(repoPath);
        foreach (string p in Load())
        {
            if (string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void Save(IReadOnlyList<string> list)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(Clean(list), Options));
        }
        catch
        {
            // Best-effort; a persistence failure must not crash the app.
        }
    }

    private static List<string> Clean(IEnumerable<string> list)
    {
        List<string> cleaned = new();
        foreach (string entry in list)
        {
            string normalized = Normalize(entry);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (!cleaned.Exists(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                cleaned.Add(normalized);
            }
        }

        return cleaned;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolvePath()
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "favorites.json");
    }
}
