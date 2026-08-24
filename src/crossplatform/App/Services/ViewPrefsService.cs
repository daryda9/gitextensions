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

    /// <summary>The changed-file list's git-grep search box (see <see cref="FindInFilesPrefs"/>).</summary>
    public FindInFilesPrefs FindInFiles { get; set; } = new();

    /// <summary>Left repository-objects panel filters (see <see cref="LeftPanelPrefs"/>).</summary>
    public LeftPanelPrefs LeftPanel { get; set; } = new();

    /// <summary>Built-in three-way merge editor (see <see cref="MergeToolPrefs"/>).</summary>
    public MergeToolPrefs Merge { get; set; } = new();

    /// <summary>Revision-grid column widths (see <see cref="GridColumnPrefs"/>).</summary>
    public GridColumnPrefs GridColumns { get; set; } = new();

    /// <summary>
    ///  Recently used advanced revision filters, most recent first, without
    ///  duplicates and capped at <see cref="ViewPrefsService.MaxRevisionFilterMru"/>.
    /// </summary>
    public List<RevisionFilterMruEntry> RevisionFilterMru { get; set; } = [];

    /// <summary>
    ///  Ids of the commands most recently run from the command palette
    ///  (<c>Views.CommandPaletteWindow</c>), most recent first, without duplicates and
    ///  capped at <see cref="ViewPrefsService.MaxCommandPaletteMru"/>.
    ///
    ///  <para>The ids are <see cref="PaletteEntry.Id"/> — an XLIFF key or a
    ///  <see cref="BrowseCommand"/> name — deliberately NOT the caption the palette
    ///  shows: a caption changes with the interface language, and a list keyed by it
    ///  would silently empty itself the first time the user switches catalogue.</para>
    /// </summary>
    public List<string> CommandPaletteMru { get; set; } = [];

    /// <summary>
    ///  Whether each illustrative help panel (<see cref="HelpImagePanel"/>) is
    ///  expanded, keyed by <see cref="HelpImageSpec.Id"/> — the port's home for what
    ///  upstream stores as <c>AppSettings.SetBool("HelpIsExpanded" + id)</c>. A map
    ///  rather than one property per dialog because the panel is shared chrome: merge,
    ///  pull and rebase each get their own remembered state without this class
    ///  learning their names. An ABSENT key means "expanded" (the designer default),
    ///  which is why the panel checks for presence instead of reading a bool.
    ///
    ///  <para>It lives here and not in <c>UiState</c> for the reason spelled out on
    ///  <see cref="ViewPrefsService"/>: the panels sit inside modal dialogs that are
    ///  gone long before <c>MainWindow.PersistLayout()</c> reserialises that file and
    ///  reverts anything written behind its back.</para>
    /// </summary>
    public Dictionary<string, bool> HelpPanels { get; set; } = [];

    /// <summary>
    ///  Whether the process dialog closes itself when the operation SUCCEEDS — the
    ///  port's home for upstream's <c>AppSettings.CloseProcessDialog</c>
    ///  (<c>src/app/GitCommands/Settings/AppSettings.cs:1336</c>, key
    ///  <c>"closeprocessdialog"</c>). It is the inverse of the <c>Keep dialog open</c>
    ///  checkbox in <see cref="GitProcessDialog"/>, exactly as upstream's
    ///  <c>FormStatus.KeepDialogOpen_CheckedChanged</c> writes it
    ///  (<c>src/app/GitUI/HelperDialogs/FormStatus.cs:276</c>).
    ///
    ///  <para>ONE global flag, not one per command: that is upstream's semantics, and
    ///  the port follows it. Defaults to <see langword="false"/> (dialog stays open),
    ///  matching <c>GetBool("closeprocessdialog", false)</c>.</para>
    ///
    ///  <para>It lives in this file rather than <c>ui-state.json</c> for the reason
    ///  given on <see cref="ViewPrefsService"/>: the process dialog is modal and long
    ///  gone before <c>MainWindow.PersistLayout()</c> reserialises that file, which
    ///  would revert the write.</para>
    /// </summary>
    public bool CloseProcessDialog { get; set; }
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

    /// <summary>
    ///  <see cref="DiffViewerOptions.InlineDiff"/> — the <c>a|b</c> intra-line marks.
    ///  Defaults to <see langword="true"/> to match that property's own default, per the
    ///  rule stated on this class: this record is also what a first run writes.
    /// </summary>
    public bool InlineDiff { get; set; } = true;

    /// <summary><see cref="DiffDisplayOptions.EncodingName"/>.</summary>
    public string EncodingName { get; set; } = DiffTextService.DefaultEncodingName;

    /// <summary><see cref="DiffDisplayOptions.ContextLines"/> — <c>-U&lt;n&gt;</c>.</summary>
    public int ContextLines { get; set; } = DiffDisplayOptions.DefaultContextLines;

    /// <summary><see cref="DiffDisplayOptions.FontSize"/> — the zoom level.</summary>
    public double FontSize { get; set; } = DiffDisplayOptions.DefaultFontSize;
}

/// <summary>
///  What the built-in merge editor (<c>Views.MergeToolWindow</c>) remembers between
///  sessions.
///
///  <para>A reading preference, exactly like <see cref="DiffPrefs.InlineDiff"/> and
///  persisted for the same reason: a user who turns the intra-line marks off — or who
///  works in the "against BASE" reading — has to find that reading again at the next
///  conflict, or the control reads as not working. Nothing that decides what gets
///  written to the work tree is remembered here, and nothing ever will be: a merge
///  tool that carried a decision across sessions would be answering for the file it
///  has not read yet.</para>
/// </summary>
public sealed class MergeToolPrefs
{
    /// <summary>
    ///  Which intra-line comparison the merge editor shows: <c>"Sides"</c>
    ///  (LOCAL ↔ REMOTE, the default), <c>"Base"</c> (each side ↔ BASE) or
    ///  <c>"Off"</c>.
    ///
    ///  <para>A NAME and not the combo's index, for the reason
    ///  <see cref="LeftPanelPrefs.SortKey"/> is one: an index is a fact about the order
    ///  of a control's items, so inserting a reading at the top would silently
    ///  reinterpret every file already written. An unknown name collapses to the
    ///  default in <see cref="ViewPrefsService.Sanitize"/>.</para>
    /// </summary>
    public string InlineMode { get; set; } = "Sides";

    /// <summary>
    ///  How much of the window's height the <b>MERGE RESULT</b> pane gets, as a
    ///  fraction of the two resizable panes together (the rest goes to the three
    ///  read-only source panes above it). Where the user last dragged the splitter.
    ///
    ///  <para>Persisted because the answer is per person and per screen, and because
    ///  the default it replaced was measurably wrong for the case that matters most:
    ///  a conflict big enough to need editing by hand. At the old 3:2 split in favour
    ///  of the SOURCES, an eighteen-line conflict showed about four lines of the one
    ///  pane that can be typed in — measured on screen, and the reason a user reached
    ///  for kdiff3 on exactly that conflict. A fraction and not a pixel height, so it
    ///  survives a different window size or screen.</para>
    /// </summary>
    public double ResultShare { get; set; } = DefaultResultShare;

    /// <summary>Half each: the result pane is where the work happens, the sources are reference.</summary>
    public const double DefaultResultShare = 0.5;

    /// <summary>Narrowest either pane may be squeezed to by a remembered value.</summary>
    public const double MinResultShare = 0.15;

    /// <summary>Widest the result pane may be by a remembered value; the sources keep the rest.</summary>
    public const double MaxResultShare = 0.85;
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
///  The changed-file list's "Find in commit files using git-grep" box: whether it is
///  open, and the two switches of its drop-down.
///
///  <para>Upstream keeps the same three in <c>AppSettings</c>
///  (<c>ShowFindInCommitFilesGitGrep</c>, <c>GitGrepIgnoreCase</c>,
///  <c>GitGrepMatchWholeWord</c>). They live here rather than in <c>UiState</c> for
///  the reason spelled out on <see cref="ViewPrefsService"/>: the search box belongs
///  to a control that several windows instantiate (the main Diff pane and the commit
///  dialog's own), so a write must not wait for — nor be reverted by —
///  <c>MainWindow.PersistLayout()</c>.</para>
///
///  <para><see cref="MatchCase"/> is the affirmative of upstream's
///  <c>GitGrepIgnoreCase</c>: this file describes what the menu item says, not what
///  the git switch does, so a reader of the JSON does not have to invert it in their
///  head. <see cref="GitGrepService"/> performs the inversion at the one point where
///  git is called.</para>
/// </summary>
public sealed class FindInFilesPrefs
{
    /// <summary>Whether the inline search box is shown above the list.</summary>
    public bool Show { get; set; }

    /// <summary>Case-sensitive matching (the inverse of <c>--ignore-case</c>).</summary>
    public bool MatchCase { get; set; }

    /// <summary><c>--word-regexp</c>: the pattern must match whole words.</summary>
    public bool WholeWord { get; set; }

    // The search TEXT is deliberately not persisted, and neither does upstream keep
    // it across sessions: reopening the app on a pre-filled search would show a
    // "grep:" section the user did not ask for, over a revision they have not chosen
    // yet.
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
/// <summary>
///  The widths the user dragged the revision grid's columns to.
/// </summary>
/// <remarks>
///  <para>Only the fixed-width columns are here. The SUBJECT column has no width of its
///  own — it takes whatever the others leave, which is upstream's arrangement too (its
///  message column is the <c>Fill</c> one), so dragging any divider is what resizes the
///  subject.</para>
///
///  <para>Here rather than in <c>UiState</c> for the reason on
///  <see cref="ViewPrefsService"/>, and because a second grid exists: the file-history
///  window builds one of its own, and it should open with the columns the user sized in
///  the main window rather than with the defaults.</para>
/// </remarks>
public sealed class GridColumnPrefs
{
    /// <summary>Author column, 0 = never dragged (use the built-in default).</summary>
    public double Author { get; set; }

    /// <summary>Date column, 0 = never dragged.</summary>
    public double Date { get; set; }

    /// <summary>Commit-id column, 0 = never dragged.</summary>
    public double Hash { get; set; }
}

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
    ///
    ///  <para><see cref="JsonIgnoreAttribute"/> because it is derived: System.Text.Json
    ///  serialises get-only properties too, and a computed flag written into the file
    ///  would read as state that can disagree with the criteria next to it.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
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
///
///  <para><b>Why the writing is not a plain load-mutate-save.</b> Every preference
///  surface in the app writes THIS file, and the app runs as more than one process on
///  purpose: the merge editor is modal, so comparing two merges means two instances.
///  A read-modify-write with no interlock loses an unrelated preference silently —
///  instance A loads, instance B loads, A writes, B writes the copy it loaded before
///  A's change existed, and A's change is gone with nothing logged anywhere. Two
///  defences, because they cover different failures:</para>
///
///  <list type="number">
///   <item>
///    <description>
///     A <c>view-prefs.json.lock</c> sidecar held with <see cref="FileShare.None"/>
///     across the whole load-mutate-save. A SIDECAR and not the file itself: the write
///     replaces the target by <c>rename</c>, so a lock taken on the old inode would
///     protect nothing, and locking the JSON for writing would also fail every
///     concurrent <see cref="Load"/>. On Linux this is a <c>flock</c>, which the kernel
///     drops when the owning process dies — so an instance killed mid-write leaves no
///     stale lock to expire, and this class needs no lock-breaking heuristic.
///    </description>
///   </item>
///   <item>
///    <description>
///     The mutation is applied to a state re-read INSIDE that lock, never to whatever
///     the caller loaded earlier. <see cref="Update"/>'s delegate is already a delta —
///     it touches only the group its surface owns — so this costs nothing and is what
///     keeps the file correct even where the lock is not honoured (a filesystem with no
///     working <c>flock</c>, or the deliberate lockless fall-back below).
///    </description>
///   </item>
///  </list>
///
///  <para><b>Why it never blocks the caller.</b> The lock is attempted exactly once,
///  without waiting. Uncontended — which is every write in a single running instance —
///  the save happens inline as before, so the value is on disk before the method
///  returns and still survives a hard kill. Contended, the mutation is handed to a
///  background pump that does the waiting there instead, so a second instance can never
///  make this one's UI thread stall on a file. The cost of that choice is a window of a
///  few milliseconds, in the contended case only, in which a process killed right then
///  loses the write; the alternative was letting another process decide how long our UI
///  freezes, which is worse.</para>
///
///  <para>The write itself is a temp file plus <c>rename</c>, which is a separate
///  concern from the lost update and worth doing on its own: it is what stops a process
///  that dies mid-write from leaving a truncated file, which <see cref="Load"/> would
///  silently read as "no preferences at all".</para>
/// </summary>
public sealed class ViewPrefsService
{
    /// <summary>How many advanced revision filters the MRU keeps.</summary>
    public const int MaxRevisionFilterMru = 15;

    /// <summary>How many commands the command palette's MRU keeps. Larger than the
    /// filter MRU because it competes with nothing for screen space: the palette only
    /// uses it to order a list the user is already scrolling.</summary>
    public const int MaxCommandPaletteMru = 20;

    /// <summary>
    ///  Raised after any instance has written the file, on the thread that wrote it —
    ///  the same contract as <see cref="CommitInfoSettingsService.Changed"/>, so a
    ///  surface that has a second editor can re-read instead of holding a stale copy.
    /// </summary>
    public static event Action? Changed;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    ///  Everything the shared file machinery needs to know about this document. Built
    ///  once and static, because <see cref="JsonSettingsFile{T}.For"/> keeps the first
    ///  model it is given for a path.
    /// </summary>
    private static readonly JsonSettingsModel<ViewPrefs> Model = new(
        static () => new ViewPrefs(),
        static text => JsonSerializer.Deserialize<ViewPrefs>(text, Options),
        static prefs => JsonSerializer.Serialize(prefs, Options),
        Sanitize,
        "saving view preferences",
        static () => Changed?.Invoke());

    private readonly JsonSettingsFile<ViewPrefs> _file;

    public ViewPrefsService() => _file = JsonSettingsFile<ViewPrefs>.For(ResolvePath(), Model);

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _file.Path;

    /// <summary>
    ///  Loads persisted preferences; returns defaults if absent or unreadable.
    ///
    ///  <para>Any mutation this process has queued but not yet written is replayed onto
    ///  what the file says, so a surface that saves and immediately reads back sees its
    ///  own change even when the write was deferred. Replaying a mutation that has in
    ///  fact just landed is harmless: every one of them sets a group or promotes an MRU
    ///  entry, which are idempotent.</para>
    /// </summary>
    public ViewPrefs Load() => _file.Load();

    /// <summary>
    ///  Replaces the WHOLE file with <paramref name="prefs"/>; best-effort (never
    ///  throws). Prefer <see cref="Update"/>: this overload cannot merge, so it reverts
    ///  any group another instance changed meanwhile — it exists for the caller that
    ///  genuinely owns the entire document. The object becomes the service's from this
    ///  call on and must not be mutated afterwards, since the write may be deferred.
    /// </summary>
    public void Save(ViewPrefs prefs) => _file.Save(prefs);

    /// <summary>
    ///  Applies <paramref name="mutate"/> to the file's current contents and writes the
    ///  result back — the only safe way for one surface to update its own group without
    ///  reverting another surface's group written meanwhile (the MRU is appended to by a
    ///  dialog while the diff toolbar is being toggled, possibly in another instance).
    ///
    ///  <para>The delegate is a DELTA, not a whole document, and that is what makes the
    ///  merge possible: it is handed a state read inside the interlock, at the last
    ///  moment before the write, so nothing the caller read earlier can go stale.</para>
    ///
    ///  <para><b>The one thing a caller must respect.</b> The delegate can therefore run
    ///  later, and on another thread, than the call that queued it, and it may run more
    ///  than once (a preview inside <see cref="Load"/> replays it). So it must SET what
    ///  it means to save, out of state that still means the same thing when it runs — a
    ///  local captured before the call, not a loop variable and not something a later
    ///  edit reinterprets. Every call site in the app already does exactly that, and the
    ///  regression suite under <c>Tests/ViewPrefsRegression</c> demonstrates what
    ///  closing over a mutating variable costs.</para>
    /// </summary>
    public void Update(Action<ViewPrefs> mutate) => _file.Update(mutate);

    /// <summary>
    ///  Waits until every deferred write of this file has reached the disk, and reports
    ///  whether it got there within <paramref name="timeout"/>.
    ///
    ///  <para>For tests and for a deliberate shutdown only. The UI never needs it: a
    ///  write is deferred only when another instance holds the lock, and the pump
    ///  finishes on its own. It BLOCKS, so it must not be called on the UI thread.</para>
    /// </summary>
    public bool Flush(TimeSpan timeout) => _file.Flush(timeout);

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
        p.FindInFiles ??= new FindInFilesPrefs();
        p.LeftPanel ??= new LeftPanelPrefs();
        p.Merge ??= new MergeToolPrefs();
        p.RevisionFilterMru ??= [];
        p.CommandPaletteMru ??= [];
        p.HelpPanels ??= [];

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

        // An unknown reading collapses to the default rather than to "Off": a file
        // written by a later build must not open the merge editor with its marks gone
        // and no way to tell that from a bug.
        p.Merge.InlineMode = p.Merge.InlineMode?.Trim() switch
        {
            "Base" => "Base",
            "Off" => "Off",
            _ => "Sides",
        };

        // A hand-edited (or NaN) share would collapse one pane to nothing, with no way
        // to drag it back: clamped, and anything not a number goes to the default.
        p.Merge.ResultShare = double.IsFinite(p.Merge.ResultShare)
            ? Math.Clamp(p.Merge.ResultShare, MergeToolPrefs.MinResultShare, MergeToolPrefs.MaxResultShare)
            : MergeToolPrefs.DefaultResultShare;

        // A hand-edited file could carry nulls inside the list, or more entries than
        // the cap; both would reach the flyout that lists them.
        p.RevisionFilterMru.RemoveAll(e => e is null || e.IsEmpty);
        if (p.RevisionFilterMru.Count > MaxRevisionFilterMru)
        {
            p.RevisionFilterMru.RemoveRange(
                MaxRevisionFilterMru, p.RevisionFilterMru.Count - MaxRevisionFilterMru);
        }

        // Same treatment for the palette's ids, plus a blank filter: a blank id would
        // match no command and would only take a slot away from a real one.
        p.CommandPaletteMru.RemoveAll(string.IsNullOrWhiteSpace);
        if (p.CommandPaletteMru.Count > MaxCommandPaletteMru)
        {
            p.CommandPaletteMru.RemoveRange(
                MaxCommandPaletteMru, p.CommandPaletteMru.Count - MaxCommandPaletteMru);
        }

        return p;
    }

    private static string ResolvePath() => SettingsPaths.Resolve("view-prefs.json");
}
