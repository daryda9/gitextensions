using System.Text.RegularExpressions;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  How the changed-files list groups its rows, mirroring the upstream
///  <c>DiffListSortType</c> (<c>FileStatusList</c>'s <c>btnByPath</c> /
///  <c>btnByExtension</c> / <c>btnByStatus</c> plus the flat/tree split of
///  <c>btnAsTree</c>).
/// </summary>
public enum DiffFileGroupMode
{
    /// <summary>No group nodes at all — the plain list of full paths.</summary>
    None,

    /// <summary>One group per directory (<c>Group by file path</c>).</summary>
    Path,

    /// <summary>One group per file extension (<c>Group by file extension</c>).</summary>
    Extension,

    /// <summary>One group per change kind (<c>Group by file status</c>).</summary>
    Status,
}

/// <summary>A row of the changed-files list: either a group header or a file.</summary>
public abstract class FileListNode
{
    /// <summary>Indentation level, 0 for a top-level row.</summary>
    public int Level { get; init; }
}

/// <summary>A collapsible group header row, carrying the number of files below it.</summary>
public sealed class FileListGroupNode : FileListNode
{
    /// <summary>Stable identity of the group, used to remember its collapsed state.</summary>
    public required string Key { get; init; }

    /// <summary>What the header row shows (already translated / formatted by the caller).</summary>
    public required string Header { get; init; }

    /// <summary>How many files the group holds, including collapsed descendants.</summary>
    public int Count { get; init; }

    /// <summary>Whether the group's content is currently hidden.</summary>
    public bool IsCollapsed { get; init; }
}

/// <summary>A file row.</summary>
public sealed class FileListFileNode : FileListNode
{
    /// <summary>The underlying changed-file row.</summary>
    public required DiffFileRow Row { get; init; }

    /// <summary>The text to show — the full path, or just the file name inside a path tree.</summary>
    public required string Display { get; init; }

    /// <summary>
    ///  Whether this row is a <c>git grep</c> hit rather than a changed file — the
    ///  rows of the search section (<see cref="GitGrepService.SummaryPrefix"/>).
    ///
    ///  <para>A flag on the NODE and not on <c>DiffFileRow</c>: which section a row
    ///  was put in is a fact about this list, not about the file, and the same file
    ///  legitimately appears both as a change and as a hit. It exists so the row can
    ///  be drawn without the M/A/D status glyph, which would claim a modification the
    ///  search says nothing about (upstream swaps the whole icon for
    ///  <c>Images.ViewFile</c> in the same situation).</para>
    /// </summary>
    public bool IsSearchHit { get; init; }
}

/// <summary>
///  The regular-expression file filter of the upstream
///  <c>cboFilterComboBox</c> ("Filter files using a regular expression...").
///
///  <para>A pattern that does not compile is not an error the user must fix
///  before seeing anything: the text then acts as a literal substring filter and
///  <see cref="Error"/> carries the parser's complaint, so the caller can show it
///  discreetly instead of throwing.</para>
/// </summary>
public sealed class DiffFileFilter
{
    /// <summary>The "everything passes" filter.</summary>
    public static DiffFileFilter None { get; } = new(null, null, null, string.Empty);

    private readonly Regex? _regex;
    private readonly string? _literal;

    private DiffFileFilter(Regex? regex, string? literal, string? error, string text)
    {
        _regex = regex;
        _literal = literal;
        Error = error;
        Text = text;
    }

    /// <summary>The raw filter text, as typed.</summary>
    public string Text { get; }

    /// <summary>The regex error, or <see langword="null"/> when the pattern compiled (or is empty).</summary>
    public string? Error { get; }

    /// <summary>Whether this filter can actually remove anything.</summary>
    public bool IsActive => _regex is not null || _literal is not null;

    /// <summary>
    ///  Parses <paramref name="text"/> as a regular expression, degrading to a
    ///  case-insensitive substring match when it does not compile.
    /// </summary>
    public static DiffFileFilter Parse(string? text)
    {
        string pattern = (text ?? string.Empty).Trim();
        if (pattern.Length == 0)
        {
            return None;
        }

        try
        {
            // A user-typed pattern can backtrack badly, so it runs under a timeout
            // rather than being trusted with the UI thread.
            Regex regex = new(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            return new DiffFileFilter(regex, null, null, pattern);
        }
        catch (ArgumentException ex)
        {
            // RegexParseException derives from ArgumentException, so this covers
            // both a malformed pattern and a bad option combination.
            return new DiffFileFilter(null, pattern, ex.Message, pattern);
        }
    }

    /// <summary>Whether <paramref name="path"/> passes the filter.</summary>
    public bool Matches(string path)
    {
        if (_regex is not null)
        {
            try
            {
                return _regex.IsMatch(path);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological pattern must not hide files: keep the row.
                return true;
            }
        }

        if (_literal is not null)
        {
            return path.Contains(_literal, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }
}

/// <summary>
///  Turns the loaded changed-file rows into the rows the list actually shows:
///  filtered, and optionally grouped under collapsible headers — either one level
///  of group nodes ("flat") or a nested folder tree.
/// </summary>
public static class DiffFileListBuilder
{
    /// <summary>Labels the group nodes; supplied by the view so they can be translated there.</summary>
    /// <param name="Key">Stable group identity (never shown).</param>
    /// <param name="Header">The header caption.</param>
    public readonly record struct GroupLabel(string Key, string Header);

    /// <summary>
    ///  Builds the view rows. The result is always a <b>new</b> list instance —
    ///  re-assigning the same instance to <c>ItemsSource</c> leaves the already
    ///  realized containers showing their old visuals.
    /// </summary>
    /// <param name="rows">All loaded changed-file rows.</param>
    /// <param name="filter">The path filter (use <see cref="DiffFileFilter.None"/> for none).</param>
    /// <param name="mode">Which grouping to apply.</param>
    /// <param name="asTree">
    ///  For <see cref="DiffFileGroupMode.Path"/>, nest the directories instead of
    ///  emitting one node per full directory path. Ignored by the other modes,
    ///  which have no hierarchy to nest.
    /// </param>
    /// <param name="grouper">
    ///  Maps a row to its group key/header for <see cref="DiffFileGroupMode.Extension"/>
    ///  and <see cref="DiffFileGroupMode.Status"/>.
    /// </param>
    /// <param name="collapsedKeys">Keys of the groups whose content is hidden.</param>
    /// <param name="countFormat">
    ///  Format of a header, given the group caption ({0}) and its file count ({1}).
    /// </param>
    /// <returns>The rows to show, and how many files (not headers) passed the filter.</returns>
    public static (List<object> Items, int FileCount) Build(
        IReadOnlyList<DiffFileRow> rows,
        DiffFileFilter filter,
        DiffFileGroupMode mode,
        bool asTree,
        Func<DiffFileRow, GroupLabel>? grouper,
        IReadOnlySet<string> collapsedKeys,
        Func<string, int, string> countFormat)
    {
        List<DiffFileRow> visible = [];
        foreach (DiffFileRow row in rows)
        {
            if (filter.Matches(row.Name) ||
                (row.OldName is not null && filter.Matches(row.OldName)))
            {
                visible.Add(row);
            }
        }

        List<object> items = [];

        if (mode == DiffFileGroupMode.None)
        {
            foreach (DiffFileRow row in visible)
            {
                items.Add(new FileListFileNode { Row = row, Display = DisplayName(row, fullPath: true) });
            }

            return (items, visible.Count);
        }

        if (mode == DiffFileGroupMode.Path && asTree)
        {
            EmitPathTree(items, visible, collapsedKeys, countFormat);
            return (items, visible.Count);
        }

        Func<DiffFileRow, GroupLabel> label = grouper ?? PathGroupLabel;

        // Ordered groups, each keeping the incoming row order inside it.
        List<string> order = [];
        Dictionary<string, (string Header, List<DiffFileRow> Rows)> groups = new(StringComparer.Ordinal);

        foreach (DiffFileRow row in visible)
        {
            GroupLabel key = label(row);
            if (!groups.TryGetValue(key.Key, out (string Header, List<DiffFileRow> Rows) bucket))
            {
                bucket = (key.Header, []);
                groups[key.Key] = bucket;
                order.Add(key.Key);
            }

            bucket.Rows.Add(row);
        }

        order.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string key in order)
        {
            (string header, List<DiffFileRow> groupRows) = groups[key];
            bool collapsed = collapsedKeys.Contains(key);

            items.Add(new FileListGroupNode
            {
                Key = key,
                Header = countFormat(header, groupRows.Count),
                Count = groupRows.Count,
                IsCollapsed = collapsed,
                Level = 0,
            });

            if (collapsed)
            {
                continue;
            }

            foreach (DiffFileRow row in groupRows)
            {
                items.Add(new FileListFileNode
                {
                    Row = row,
                    Display = DisplayName(row, fullPath: true),
                    Level = 1,
                });
            }
        }

        return (items, visible.Count);
    }

    /// <summary>The default group label for the path modes: the file's directory.</summary>
    public static GroupLabel PathGroupLabel(DiffFileRow row)
    {
        string dir = Directory(row.Name);
        return new GroupLabel(dir.Length == 0 ? "/" : dir, dir.Length == 0 ? "/" : dir + "/");
    }

    /// <summary>The row's text: its full path, or just the file name inside a tree.</summary>
    public static string DisplayName(DiffFileRow row, bool fullPath)
    {
        // Upstream's TruncatePathMethod (PathFormatter.cs:32): FileNameOnly drops the
        // directories entirely, TrimStart keeps the tail. Both only apply where the
        // full path would otherwise be shown — inside a path GROUP the directory is
        // already in the header, and shortening the leaf again would say nothing.
        string method = fullPath
            ? new SettingsService().Load().TruncatePathMethod
            : "None";

        if (string.Equals(method, "FileNameOnly", StringComparison.Ordinal))
        {
            fullPath = false;
        }

        string name = fullPath ? row.Name : FileName(row.Name);
        if (string.Equals(method, "TrimStart", StringComparison.Ordinal))
        {
            name = TrimStartPath(name);
        }

        if (row.OldName is null || row.OldName == row.Name)
        {
            return name;
        }

        // A rename shows both sides, shortened the same way as the new side.
        string old = fullPath ? row.OldName : FileName(row.OldName);
        return old + " -> " + name;
    }

    // Keeps the last TrimStartSegments segments of a long path and marks the cut with
    // an ellipsis. Upstream trims to the width of the column; there is no column width
    // to consult here (the list is a wrapping TextBlock), so the cut is by segment,
    // which at least never lands mid-name.
    private static string TrimStartPath(string path)
    {
        string[] parts = path.Split('/');
        return parts.Length <= TrimStartSegments
            ? path
            : "…/" + string.Join('/', parts[^TrimStartSegments..]);
    }

    private const int TrimStartSegments = 2;

    // ---- path tree ----

    // Emits nested directory nodes followed by the files of each directory. A
    // collapsed node hides its whole subtree, and its count still reports it.
    private static void EmitPathTree(
        List<object> items,
        List<DiffFileRow> visible,
        IReadOnlySet<string> collapsedKeys,
        Func<string, int, string> countFormat)
    {
        // dir -> the files directly in it, in the incoming order.
        Dictionary<string, List<DiffFileRow>> filesByDir = new(StringComparer.Ordinal);

        // dir -> its immediate child directories.
        Dictionary<string, SortedSet<string>> childDirs = new(StringComparer.Ordinal);

        void EnsureDir(string dir)
        {
            if (!filesByDir.ContainsKey(dir))
            {
                filesByDir[dir] = [];
            }

            if (dir.Length == 0)
            {
                return;
            }

            string parent = Directory(dir);
            if (!childDirs.TryGetValue(parent, out SortedSet<string>? children))
            {
                children = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                childDirs[parent] = children;
            }

            if (children.Add(dir))
            {
                EnsureDir(parent);
            }
        }

        EnsureDir(string.Empty);

        foreach (DiffFileRow row in visible)
        {
            string dir = Directory(row.Name);
            EnsureDir(dir);
            filesByDir[dir].Add(row);
        }

        // Subtree file counts, needed for the header of a collapsed node.
        Dictionary<string, int> subtreeCount = new(StringComparer.Ordinal);

        int CountOf(string dir)
        {
            if (subtreeCount.TryGetValue(dir, out int cached))
            {
                return cached;
            }

            int total = filesByDir.TryGetValue(dir, out List<DiffFileRow>? own) ? own.Count : 0;
            if (childDirs.TryGetValue(dir, out SortedSet<string>? children))
            {
                foreach (string child in children)
                {
                    total += CountOf(child);
                }
            }

            subtreeCount[dir] = total;
            return total;
        }

        void Emit(string dir, int level)
        {
            if (childDirs.TryGetValue(dir, out SortedSet<string>? children))
            {
                foreach (string child in children)
                {
                    bool collapsed = collapsedKeys.Contains(child);
                    items.Add(new FileListGroupNode
                    {
                        Key = child,
                        Header = countFormat(FileName(child), CountOf(child)),
                        Count = CountOf(child),
                        IsCollapsed = collapsed,
                        Level = level,
                    });

                    if (!collapsed)
                    {
                        Emit(child, level + 1);
                    }
                }
            }

            if (!filesByDir.TryGetValue(dir, out List<DiffFileRow>? own))
            {
                return;
            }

            foreach (DiffFileRow row in own)
            {
                items.Add(new FileListFileNode
                {
                    Row = row,
                    Display = DisplayName(row, fullPath: false),
                    Level = level,
                });
            }
        }

        Emit(string.Empty, 0);
    }

    // git paths always use '/', whatever the platform, so these two do not go
    // through System.IO.Path (which would treat '\' as a separator on Windows).
    private static string Directory(string path)
    {
        int at = path.LastIndexOf('/');
        return at < 0 ? string.Empty : path[..at];
    }

    private static string FileName(string path)
    {
        int at = path.LastIndexOf('/');
        return at < 0 ? path : path[(at + 1)..];
    }
}
