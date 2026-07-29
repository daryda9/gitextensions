using System.Text.Json;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The view preferences the port used to forget at every start: the diff viewer's
///  toolbar options, the file history's four switches, the left panel's category
///  filters and sort, and the most-recently-used advanced revision filters.
///
///  <para>Each group is a nested object rather than a flat prefix soup, so the file
///  reads as the four surfaces it describes and a group can gain a member without
///  touching the others. A missing group deserialises to <see langword="null"/> and
///  is replaced by its defaults in <see cref="ViewPrefsService.Sanitize"/>, so a
///  file written by an older build keeps working.</para>
/// </summary>
public sealed class ViewPrefs
{
    /// <summary>Diff viewer toolbar state (see <see cref="DiffPrefs"/>).</summary>
    public DiffPrefs Diff { get; set; } = new();

    /// <summary>File history switches (see <see cref="FileHistoryPrefs"/>).</summary>
    public FileHistoryPrefs FileHistory { get; set; } = new();

    /// <summary>Left repository-objects panel filters (see <see cref="LeftPanelPrefs"/>).</summary>
    public LeftPanelPrefs LeftPanel { get; set; } = new();

    /// <summary>
    ///  Recently used advanced revision filters, most recent first, without
    ///  duplicates and capped at <see cref="ViewPrefsService.MaxRevisionFilterMru"/>.
    /// </summary>
    public List<RevisionFilterMruEntry> RevisionFilterMru { get; set; } = [];
}

/// <summary>
///  The diff viewer's toolbar options, which live at runtime in the two process-wide
///  singletons <see cref="DiffTextService.Session"/> and
///  <see cref="DiffViewerOptions.Session"/>.
///
///  <para>Mirrors what upstream keeps in <c>AppSettings</c> for the same strip
///  (<c>IgnoreWhitespaceKind</c>, <c>ShowEntireFile</c>, <c>ShowNonPrintingChars</c>,
///  <c>NumberOfContextLines</c>, <c>ShowSyntaxHighlightingInDiff</c>, the diff font
///  size). The defaults here repeat the two option classes' own C# defaults on
///  purpose: this record is also what a first run writes, so the two must agree or
///  the first save would silently change the viewer.</para>
/// </summary>
public sealed class DiffPrefs
{
    /// <summary><see cref="DiffDisplayOptions.ShowEntireFile"/>.</summary>
    public bool ShowEntireFile { get; set; }

    /// <summary><see cref="DiffDisplayOptions.IgnoreWhitespace"/> — <c>-w</c>.</summary>
    public bool IgnoreWhitespace { get; set; }

    /// <summary><see cref="DiffDisplayOptions.ShowNonPrinting"/>.</summary>
    public bool ShowNonPrinting { get; set; }

    /// <summary><see cref="DiffDisplayOptions.WordDiff"/> — <c>--word-diff</c>.</summary>
    public bool WordDiff { get; set; }

    /// <summary><see cref="DiffViewerOptions.IgnoreWhitespaceAtEol"/> — <c>--ignore-space-at-eol</c>.</summary>
    public bool IgnoreWhitespaceAtEol { get; set; }

    /// <summary><see cref="DiffViewerOptions.IgnoreWhitespaceChange"/> — <c>-b</c>.</summary>
    public bool IgnoreWhitespaceChange { get; set; }

    /// <summary><see cref="DiffViewerOptions.TreatAllFilesAsText"/> — <c>--text</c>.</summary>
    public bool TreatAllFilesAsText { get; set; }

    /// <summary><see cref="DiffViewerOptions.SyntaxHighlighting"/>.</summary>
    public bool SyntaxHighlighting { get; set; }

    /// <summary><see cref="DiffDisplayOptions.EncodingName"/>.</summary>
    public string EncodingName { get; set; } = DiffTextService.DefaultEncodingName;

    /// <summary><see cref="DiffDisplayOptions.ContextLines"/> — <c>-U&lt;n&gt;</c>.</summary>
    public int ContextLines { get; set; } = DiffDisplayOptions.DefaultContextLines;

    /// <summary><see cref="DiffDisplayOptions.FontSize"/> — the zoom level.</summary>
    public double FontSize { get; set; } = DiffDisplayOptions.DefaultFontSize;
}

/// <summary>
///  The file history's four switches — upstream's <c>FormFileHistory</c> menu items,
///  which it persists as <c>AppSettings.FollowRenamesInFileHistory</c>,
///  <c>FollowRenamesInFileHistoryExactOnly</c>, <c>FullHistoryInFileHistory</c> and
///  <c>SimplifyMergesInFileHistory</c>.
///
///  <para>Defaults repeat <see cref="FileHistoryOptions"/>'s own (follow renames on,
///  the rest off), for the same reason as <see cref="DiffPrefs"/>.</para>
/// </summary>
public sealed class FileHistoryPrefs
{
    /// <summary><c>--follow</c>: trace the file across renames.</summary>
    public bool FollowRenames { get; set; } = true;

    /// <summary>Restrict rename/copy detection to identical content.</summary>
    public bool ExactRenamesAndCopiesOnly { get; set; }

    /// <summary><c>--full-history</c>.</summary>
    public bool FullHistory { get; set; }

    /// <summary><c>--simplify-merges</c> (inert unless <see cref="FullHistory"/>).</summary>
    public bool SimplifyMerges { get; set; }
}

/// <summary>
///  Which categories the left repository-objects panel shows, and how it sorts the
///  refs inside them — the port's equivalent of upstream's
///  <c>AppSettings.RepoObjectsTreeShow*</c> family, which <c>RepoObjectsTree</c>
///  reads back when it builds the tree.
///
///  <para>Deliberately does NOT include the panel's search text or its expanded
///  nodes. The search box is a transient cursor over the tree (Escape clears it,
///  Enter/F3 walk the matches), so restoring it would reopen the app on a pruned
///  tree with no visible cause; the expansion set is navigation state, not a filter.
///  The panel's width, collapsed flag and category ORDER stay where they already
///  are — <see cref="UiState"/>, written by <c>MainWindow.PersistLayout</c> — because
///  those are layout owned by the host window.</para>
/// </summary>
public sealed class LeftPanelPrefs
{
    /// <summary>Show the Branches category.</summary>
    public bool ShowBranches { get; set; } = true;

    /// <summary>Show the Remotes category.</summary>
    public bool ShowRemotes { get; set; } = true;

    /// <summary>Show the Worktrees category.</summary>
    public bool ShowWorktrees { get; set; } = true;

    /// <summary>Show the Tags category.</summary>
    public bool ShowTags { get; set; } = true;

    /// <summary>Show the Submodules category.</summary>
    public bool ShowSubmodules { get; set; } = true;

    /// <summary>Show the Stashes category.</summary>
    public bool ShowStashes { get; set; } = true;

    /// <summary>Sort refs by "Name" or "CommitDate" (a <c>Views.RefSortKey</c> name).</summary>
    public string SortKey { get; set; } = "Name";

    /// <summary>"Ascending" or "Descending" (a <c>Views.RefSortOrder</c> name).</summary>
    public string SortOrder { get; set; } = "Ascending";
}

/// <summary>
///  One remembered advanced revision filter: the criteria of a
///  <c>Services.RevisionFilter</c>, flattened into a serialisable shape.
///
///  <para>A DTO of its own rather than the record itself: <c>RevisionFilter</c> has
///  computed members and <c>init</c>-only properties meant for the walk, and pinning
///  the on-disk shape here means a change to the walk's filter type cannot silently
///  reinterpret an old file. <see cref="Equals(object?)"/> is what makes the MRU
///  duplicate-free, so it compares exactly the criteria the user typed.</para>
/// </summary>
public sealed class RevisionFilterMruEntry : IEquatable<RevisionFilterMruEntry>
{
    /// <summary><c>--author=</c>.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary><c>--committer=</c>.</summary>
    public string Committer { get; set; } = string.Empty;

    /// <summary><c>--grep=</c>.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Pickaxe text (<c>-S</c>/<c>-G</c>).</summary>
    public string DiffContent { get; set; } = string.Empty;

    /// <summary><see langword="true"/> → <c>-G</c> (regex), otherwise <c>-S</c>.</summary>
    public bool DiffContentIsRegex { get; set; }

    /// <summary><c>--since=</c>.</summary>
    public string DateFrom { get; set; } = string.Empty;

    /// <summary><c>--until=</c>.</summary>
    public string DateTo { get; set; } = string.Empty;

    /// <summary>Path filter (space separated, quotes honoured).</summary>
    public string PathFilter { get; set; } = string.Empty;

    /// <summary>Hard cap on the walk (0 = none).</summary>
    public int CommitsLimit { get; set; }

    /// <summary>Match case-sensitively.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>Treat the text patterns as git regexes.</summary>
    public bool UseRegex { get; set; }

    /// <summary><c>--no-merges</c>.</summary>
    public bool HideMergeCommits { get; set; }

    /// <summary><c>--first-parent</c>.</summary>
    public bool FirstParentOnly { get; set; }

    /// <summary><c>--simplify-by-decoration</c>.</summary>
    public bool SimplifyByDecoration { get; set; }

    /// <summary>
    ///  True when no criterion is set — the neutral filter, which is never worth
    ///  remembering (it is what "Reset revision filters" produces).
    /// </summary>
    public bool IsEmpty =>
        Author.Length == 0
        && Committer.Length == 0
        && Message.Length == 0
        && DiffContent.Length == 0
        && DateFrom.Length == 0
        && DateTo.Length == 0
        && PathFilter.Length == 0
        && CommitsLimit == 0
        && !HideMergeCommits
        && !FirstParentOnly
        && !SimplifyByDecoration;

    /// <inheritdoc/>
    public bool Equals(RevisionFilterMruEntry? other) =>
        other is not null
        && string.Equals(Author, other.Author, StringComparison.Ordinal)
        && string.Equals(Committer, other.Committer, StringComparison.Ordinal)
        && string.Equals(Message, other.Message, StringComparison.Ordinal)
        && string.Equals(DiffContent, other.DiffContent, StringComparison.Ordinal)
        && DiffContentIsRegex == other.DiffContentIsRegex
        && string.Equals(DateFrom, other.DateFrom, StringComparison.Ordinal)
        && string.Equals(DateTo, other.DateTo, StringComparison.Ordinal)
        && string.Equals(PathFilter, other.PathFilter, StringComparison.Ordinal)
        && CommitsLimit == other.CommitsLimit
        && CaseSensitive == other.CaseSensitive
        && UseRegex == other.UseRegex
        && HideMergeCommits == other.HideMergeCommits
        && FirstParentOnly == other.FirstParentOnly
        && SimplifyByDecoration == other.SimplifyByDecoration;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as RevisionFilterMruEntry);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Author, StringComparer.Ordinal);
        hash.Add(Committer, StringComparer.Ordinal);
        hash.Add(Message, StringComparer.Ordinal);
        hash.Add(DiffContent, StringComparer.Ordinal);
        hash.Add(DiffContentIsRegex);
        hash.Add(DateFrom, StringComparer.Ordinal);
        hash.Add(DateTo, StringComparer.Ordinal);
        hash.Add(PathFilter, StringComparer.Ordinal);
        hash.Add(CommitsLimit);
        hash.Add(CaseSensitive);
        hash.Add(UseRegex);
        hash.Add(HideMergeCommits);
        hash.Add(FirstParentOnly);
        hash.Add(SimplifyByDecoration);
        return hash.ToHashCode();
    }
}

/// <summary>
///  Reads/writes <see cref="ViewPrefs"/> as JSON next to <see cref="UiState"/> —
///  <c>$XDG_CONFIG_HOME/GitExtensions.Avalonia/view-prefs.json</c> — tolerating a
///  missing or corrupt file by returning defaults.
///
///  <para><b>Why a second file and not fields on <see cref="UiState"/>.</b> This
///  follows the precedent <see cref="CommitInfoSettingsService"/> set, for the same
///  reason and more sharply: <c>MainWindow</c> loads ONE <see cref="UiState"/>
///  instance at start-up and serialises that whole object again from
///  <c>PersistLayout()</c> when it closes, so anything a view writes into the same
///  file behind the host's back is reverted on exit (last writer wins). Routing
///  these four groups through the host instead would mean threading a callback into
///  every one of their editors — and three of the four are NOT owned by
///  <c>MainWindow</c>: <c>DiffView</c> and <c>FileHistoryView</c> are each
///  instantiated a second time inside <c>CommitDialog</c>'s standalone windows, and
///  the advanced-filter MRU is written by a modal dialog that has gone away long
///  before the host saves. A separate file has no such hazard, every write is
///  immediate (so the state survives even a hard kill, which skips
///  <c>PersistLayout</c> entirely), and the file stays the single source of truth for
///  the several editors of the same value.</para>
/// </summary>
public sealed class ViewPrefsService
{
    /// <summary>How many advanced revision filters the MRU keeps.</summary>
    public const int MaxRevisionFilterMru = 15;

    /// <summary>
    ///  Raised after any instance has written the file, on the thread that wrote it —
    ///  the same contract as <see cref="CommitInfoSettingsService.Changed"/>, so a
    ///  surface that has a second editor can re-read instead of holding a stale copy.
    /// </summary>
    public static event Action? Changed;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public ViewPrefsService() => _path = ResolvePath();

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _path;

    /// <summary>Loads persisted preferences; returns defaults if absent or unreadable.</summary>
    public ViewPrefs Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                ViewPrefs? loaded = JsonSerializer.Deserialize<ViewPrefs>(File.ReadAllText(_path), Options);
                if (loaded is not null)
                {
                    return Sanitize(loaded);
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → defaults.
        }

        return new ViewPrefs();
    }

    /// <summary>Writes the given preferences; best-effort (never throws).</summary>
    public void Save(ViewPrefs prefs)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(Sanitize(prefs), Options));
        }
        catch
        {
            // Persistence is best-effort; a failure must not crash the app.
        }

        // Announced even if the write failed: the in-memory intent still changed.
        Changed?.Invoke();
    }

    /// <summary>
    ///  Reads the file, hands the loaded object to <paramref name="mutate"/> and
    ///  writes it back — the only safe way for one surface to update its own group
    ///  without reverting another surface's group written meanwhile (the MRU is
    ///  appended to by a dialog while the diff toolbar is being toggled).
    /// </summary>
    public void Update(Action<ViewPrefs> mutate)
    {
        ViewPrefs prefs = Load();
        mutate(prefs);
        Save(prefs);
    }

    /// <summary>
    ///  Puts <paramref name="entry"/> at the head of <paramref name="mru"/>, dropping
    ///  an equal entry from further down (so re-using a filter promotes it instead of
    ///  duplicating it) and trimming the tail to
    ///  <see cref="MaxRevisionFilterMru"/>. An empty entry is ignored.
    /// </summary>
    public static void PushMru(List<RevisionFilterMruEntry> mru, RevisionFilterMruEntry entry)
    {
        if (entry.IsEmpty)
        {
            return;
        }

        mru.RemoveAll(e => entry.Equals(e));
        mru.Insert(0, entry);

        if (mru.Count > MaxRevisionFilterMru)
        {
            mru.RemoveRange(MaxRevisionFilterMru, mru.Count - MaxRevisionFilterMru);
        }
    }

    // Replaces missing groups and clamps the few non-bool values. A corrupt bool
    // cannot survive deserialisation as anything but false, so only the numbers,
    // the encoding name and the two enum names need checking.
    private static ViewPrefs Sanitize(ViewPrefs p)
    {
        p.Diff ??= new DiffPrefs();
        p.FileHistory ??= new FileHistoryPrefs();
        p.LeftPanel ??= new LeftPanelPrefs();
        p.RevisionFilterMru ??= [];

        // An unknown encoding name would leave the toolbar combo with no selection
        // and decode the patch as UTF-8 anyway, so it collapses to the default.
        if (!DiffTextService.EncodingNames.Contains(p.Diff.EncodingName))
        {
            p.Diff.EncodingName = DiffTextService.DefaultEncodingName;
        }

        p.Diff.ContextLines = p.Diff.ContextLines is >= 0 and <= DiffDisplayOptions.MaxContextLines
            ? p.Diff.ContextLines
            : DiffDisplayOptions.DefaultContextLines;

        // Zoom is clamped to the same band the viewer's own zoom command allows
        // (DiffView.Zoom: 6..32): a restored 2 pt or 400 pt diff pane is unusable, and
        // a size the UI itself cannot produce has no business coming back from a file.
        p.Diff.FontSize = p.Diff.FontSize is >= 6 and <= 32
            ? p.Diff.FontSize
            : DiffDisplayOptions.DefaultFontSize;

        p.LeftPanel.SortKey = p.LeftPanel.SortKey?.Trim() == "CommitDate" ? "CommitDate" : "Name";
        p.LeftPanel.SortOrder = p.LeftPanel.SortOrder?.Trim() == "Descending" ? "Descending" : "Ascending";

        // A hand-edited file could carry nulls inside the list, or more entries than
        // the cap; both would reach the flyout that lists them.
        p.RevisionFilterMru.RemoveAll(e => e is null || e.IsEmpty);
        if (p.RevisionFilterMru.Count > MaxRevisionFilterMru)
        {
            p.RevisionFilterMru.RemoveRange(
                MaxRevisionFilterMru, p.RevisionFilterMru.Count - MaxRevisionFilterMru);
        }

        return p;
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

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "view-prefs.json");
    }
}
