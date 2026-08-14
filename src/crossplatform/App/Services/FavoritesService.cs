using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  One favorite repository: its path and the free-form category it was filed
///  under.
///
///  <para>Upstream models a category exactly this way — a nullable string on the
///  repository entry itself (<c>GitCommands/UserRepositoryHistory/Repository.cs</c>,
///  <c>public string? Category { get; set; }</c>) — and not as a container object
///  with children. The set of categories is therefore only ever "the distinct
///  non-blank values currently in use": filing the last repository out of a
///  category deletes that category, which is what upstream's
///  <c>UserRepositoriesList.GetCategories()</c> does too.</para>
/// </summary>
/// <param name="Path">Absolute repository path, as stored.</param>
/// <param name="Category">
///  The category, or <c>null</c>/blank for an uncategorised favorite. Upstream
///  cannot create one of these through its UI (assigning a blank category
///  un-favorites the repository instead), but legacy-migrated data can carry one,
///  so both this type and the menu that renders it tolerate it.
/// </param>
public sealed record FavoriteRepo(string Path, string? Category)
{
    /// <summary>True when this favorite is filed under a real category.</summary>
    public bool HasCategory => !string.IsNullOrWhiteSpace(Category);
}

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

    /// <summary>
    ///  Everything the shared file machinery needs to know about this document. Built
    ///  once and static, because <see cref="JsonSettingsFile{T}.For"/> keeps the first
    ///  model it is given for a path.
    /// </summary>
    private static readonly JsonSettingsModel<List<FavoriteRepo>> Model = new(
        static () => [],
        Parse,
        Render,
        Clean,
        "saving favorites");

    private readonly JsonSettingsFile<List<FavoriteRepo>> _file;

    public FavoritesService() => _file = JsonSettingsFile<List<FavoriteRepo>>.For(ResolvePath(), Model);

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _file.Path;

    /// <summary>Loads the favorite repository paths (in the stored order).</summary>
    public IReadOnlyList<string> Load()
    {
        List<string> paths = new();
        foreach (FavoriteRepo entry in LoadEntries())
        {
            paths.Add(entry.Path);
        }

        return paths;
    }

    /// <summary>
    ///  Loads the favorites with their categories, in the stored order.
    ///
    ///  <para>Two on-disk shapes are accepted, because this file predates
    ///  categories: a bare string is an uncategorised favorite (the original
    ///  format, still written for entries that have no category) and an object
    ///  <c>{ "path": …, "category": … }</c> is a categorised one. Anything else in
    ///  the array is skipped rather than treated as a fatal parse error, so one bad
    ///  entry cannot cost the user the rest of the list.</para>
    /// </summary>
    public IReadOnlyList<FavoriteRepo> LoadEntries() => _file.Load();

    /// <summary>Waits for deferred writes to reach the disk. Tests and shutdown only; blocks.</summary>
    public bool Flush(TimeSpan timeout) => _file.Flush(timeout);

    /// <summary>
    ///  Adds <paramref name="repoPath"/> as a favorite (moved/kept at the top),
    ///  de-duplicating case-insensitively. Returns the updated paths.
    ///
    ///  <para>An existing entry keeps the category it was already filed under: this
    ///  is the "make/keep favorite" gesture, not a re-filing one.</para>
    /// </summary>
    public IReadOnlyList<string> Add(string repoPath)
    {
        string normalized = Normalize(repoPath);
        if (normalized.Length == 0)
        {
            return Load();
        }

        // A delta rather than a load-mutate-save: the list it edits is read at write time,
        // so a repository another instance favorited meanwhile is still there afterwards.
        // Idempotent, as the merge requires — removing then inserting at the top lands on
        // the same list however many times it runs.
        _file.Update(list =>
        {
            string? category = Find(list, normalized)?.Category;
            list.RemoveAll(e => SamePath(e.Path, normalized));
            list.Insert(0, new FavoriteRepo(normalized, category));
        });

        return Load();
    }

    /// <summary>Removes <paramref name="repoPath"/> from favorites; returns the updated paths.</summary>
    public IReadOnlyList<string> Remove(string repoPath)
    {
        string normalized = Normalize(repoPath);
        _file.Update(list => list.RemoveAll(e => SamePath(e.Path, normalized)));
        return Load();
    }

    /// <summary>
    ///  Files <paramref name="repoPath"/> under <paramref name="category"/>,
    ///  following upstream's <c>LocalRepositoryManager.AssignCategoryAsync</c>
    ///  semantics exactly, because there the category *is* the favorite flag:
    ///  <list type="bullet">
    ///   <item>not yet a favorite + a real category → becomes a favorite in it;</item>
    ///   <item>not yet a favorite + a blank category → nothing happens;</item>
    ///   <item>already a favorite + a real category → re-filed;</item>
    ///   <item>already a favorite + a blank category → <b>un-favorited</b>.</item>
    ///  </list>
    ///  Returns the updated entries.
    /// </summary>
    public IReadOnlyList<FavoriteRepo> AssignCategory(string repoPath, string? category)
    {
        string normalized = Normalize(repoPath);
        if (normalized.Length == 0)
        {
            return LoadEntries();
        }

        string? trimmed = string.IsNullOrWhiteSpace(category) ? null : category.Trim();

        _file.Update(list =>
        {
            FavoriteRepo? existing = Find(list, normalized);

            if (existing is null)
            {
                if (trimmed is not null)
                {
                    list.Insert(0, new FavoriteRepo(normalized, trimmed));
                }
            }
            else if (trimmed is null)
            {
                list.RemoveAll(e => SamePath(e.Path, normalized));
            }
            else
            {
                list[list.IndexOf(existing)] = existing with { Category = trimmed };
            }
        });

        return LoadEntries();
    }

    /// <summary>
    ///  The categories currently in use: the distinct non-blank values, ordered the
    ///  way the menus show them. Upstream has no separate category store either.
    /// </summary>
    public IReadOnlyList<string> Categories()
    {
        List<string> names = new();
        foreach (FavoriteRepo entry in LoadEntries())
        {
            if (entry.Category is { } category
                && entry.HasCategory
                && !names.Exists(n => string.Equals(n, category, StringComparison.CurrentCulture)))
            {
                names.Add(category);
            }
        }

        names.Sort(StringComparer.CurrentCulture);
        return names;
    }

    /// <summary>
    ///  The category <paramref name="repoPath"/> is filed under, or <c>null</c> when
    ///  it is uncategorised or not a favorite at all.
    /// </summary>
    public string? CategoryOf(string repoPath)
        => Find(new List<FavoriteRepo>(LoadEntries()), Normalize(repoPath))?.Category;

    /// <summary>True when <paramref name="repoPath"/> is already a favorite.</summary>
    public bool Contains(string repoPath)
        => Find(new List<FavoriteRepo>(LoadEntries()), Normalize(repoPath)) is not null;

    // Text to entries. Anything that is not an array, and any array element that is
    // neither of the two shapes, is skipped rather than treated as a fatal parse error, so
    // one bad entry cannot cost the user the rest of the list.
    private static List<FavoriteRepo>? Parse(string text)
    {
        if (JsonNode.Parse(text) is not JsonArray array)
        {
            return null;
        }

        List<FavoriteRepo> entries = new();
        foreach (JsonNode? node in array)
        {
            switch (node)
            {
                case JsonValue value when value.TryGetValue(out string? path):
                    entries.Add(new FavoriteRepo(path, null));
                    break;

                case JsonObject obj:
                    string? objPath = obj.TryGetPropertyValue("path", out JsonNode? p)
                        ? p?.GetValue<string>()
                        : null;
                    string? category = obj.TryGetPropertyValue("category", out JsonNode? c)
                        ? c?.GetValue<string>()
                        : null;
                    if (objPath is not null)
                    {
                        entries.Add(new FavoriteRepo(objPath, category));
                    }

                    break;
            }
        }

        return entries;
    }

    // An uncategorised entry is written as a bare string, which is exactly the
    // pre-category format: a user who never files anything keeps a file older builds can
    // still read, and no spurious rewrite on first launch.
    private static string Render(List<FavoriteRepo> list)
    {
        JsonArray array = new();
        foreach (FavoriteRepo entry in list)
        {
            if (entry.HasCategory)
            {
                array.Add(new JsonObject
                {
                    ["path"] = entry.Path,
                    ["category"] = entry.Category,
                });
            }
            else
            {
                array.Add(entry.Path);
            }
        }

        return array.ToJsonString(Options);
    }

    private static FavoriteRepo? Find(List<FavoriteRepo> list, string normalizedPath)
        => normalizedPath.Length == 0
            ? null
            : list.Find(e => SamePath(e.Path, normalizedPath));

    private static bool SamePath(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Drops blank and duplicate paths and trims categories. Applied on the way in AND on
    // the way out, so neither a hand-edited file nor a merged mutation can leave the list
    // with two entries for one repository.
    private static List<FavoriteRepo> Clean(IEnumerable<FavoriteRepo> list)
    {
        List<FavoriteRepo> cleaned = new();
        foreach (FavoriteRepo entry in list)
        {
            string normalized = Normalize(entry.Path);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (!cleaned.Exists(e => SamePath(e.Path, normalized)))
            {
                string? category = string.IsNullOrWhiteSpace(entry.Category)
                    ? null
                    : entry.Category.Trim();
                cleaned.Add(new FavoriteRepo(normalized, category));
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

    private static string ResolvePath() => SettingsPaths.Resolve("favorites.json");
}
