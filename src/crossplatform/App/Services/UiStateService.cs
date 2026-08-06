using System.Text.Json;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Persisted UI layout/preferences for the Avalonia port: window size, the
///  three splitter panel sizes, and the chosen light/dark theme.
///
///  <para>Unlike <see cref="RecentRepositoriesService"/> (which reuses the core
///  MRU store), this state is purely presentation-layer for the Linux app, so
///  it lives in its own small JSON file under the user's config directory:
///  <c>$XDG_CONFIG_HOME</c> (or <see cref="Environment.SpecialFolder.ApplicationData"/>,
///  or <c>~/.config</c>) → <c>GitExtensions.Avalonia/ui-state.json</c>.</para>
/// </summary>
public sealed class UiState
{
    /// <summary>Restored window width in device-independent pixels.</summary>
    public double WindowWidth { get; set; } = 1280;

    /// <summary>Restored window height in device-independent pixels.</summary>
    public double WindowHeight { get; set; } = 820;

    /// <summary>
    ///  Restored window position, in physical screen pixels (the unit Avalonia's
    ///  <c>Window.Position</c> uses), or null the first time round — the window then
    ///  opens centred.
    ///
    ///  <para>Always re-validated against the screens actually present at start-up:
    ///  a position saved on a second monitor, or a size saved on a larger screen,
    ///  must not put the window out of reach.</para>
    /// </summary>
    public int? WindowX { get; set; }

    /// <summary>Restored window position (see <see cref="WindowX"/>).</summary>
    public int? WindowY { get; set; }

    /// <summary>
    ///  Whether the window was maximized when it was last closed. The width/height
    ///  and position above then describe its <i>restored</i> (normal) bounds, so
    ///  un-maximizing lands where the user left it.
    /// </summary>
    public bool WindowMaximized { get; set; }

    /// <summary>
    ///  The selected tab of the bottom panel, as a stable key ("Commit", "Diff",
    ///  "FileTree", "Gpg", "Console", "Output", "Blame", "History") rather than an
    ///  index: the Diff tab is removed from the strip while split view is on, so
    ///  positions are not stable across sessions. A "Stash" left behind by an older
    ///  version — the stash panel is a window now — matches nothing and falls back to
    ///  the Commit tab.
    /// </summary>
    public string BottomTab { get; set; } = "Commit";

    /// <summary>
    ///  Whether the app refreshes itself when the repository changes on disk
    ///  (<see cref="RepositoryWatcherService"/>). Off means F5 only.
    /// </summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary>Left repository-tree column width (pixels).</summary>
    ///  <para>This is the width the panel has when it is <i>shown</i>, and it is kept
    ///  even while the panel is collapsed — see <see cref="LeftPanelCollapsed"/>.</para>
    public double TreeWidth { get; set; } = 260;

    /// <summary>
    ///  Whether the left repository-objects panel is collapsed (Ctrl+Alt+C).
    ///
    ///  <para>Deliberately a flag of its own rather than "width == 0": a saved zero
    ///  width is indistinguishable from a corrupt entry and <see cref="UiStateService"/>
    ///  clamps it back to 260, which used to both re-open the panel at the next start
    ///  <i>and</i> lose the width the user had chosen. Upstream persists the collapse
    ///  separately for the same reason (<c>SplitterManager.cs:57-62</c>, registered at
    ///  <c>FormBrowse.cs:2285</c>).</para>
    /// </summary>
    public bool LeftPanelCollapsed { get; set; }

    /// <summary>
    ///  Order of the left panel's category root nodes, as a comma-separated list of
    ///  category ids (<c>branches,remotes,worktrees,tags,submodules,stashes</c>).
    ///
    ///  <para>Written by <c>RepoObjectsTree</c>'s "Move Up" / "Move Down" context-menu
    ///  items, which upstream reorders the <i>categories</i> and persists their indices
    ///  (<c>RepoObjectsTree.ContextActions.cs:61-68</c> gates them to a root node, and
    ///  <c>ReorderTreeNode</c> saves the new order). Unknown or missing ids are ignored
    ///  when the string is applied, so an old or hand-edited file degrades to the
    ///  default order instead of losing a category.</para>
    /// </summary>
    public string LeftPanelCategoryOrder { get; set; } = string.Empty;

    /// <summary>
    ///  Right area: the revision grid's share of the vertical split, as a fraction of
    ///  the split — <see cref="RevisionsStar"/> + <see cref="BottomStar"/> == 1.
    ///
    ///  <para>Stored as a <i>proportion</i>, not as the raw star weight the grid holds
    ///  at runtime: Avalonia's <c>GridSplitter</c> rewrites the star weights of the
    ///  definitions it drags with their current <b>pixel</b> extents (a drag turns
    ///  <c>3*</c>/<c>2*</c> into e.g. <c>199*</c>/<c>525*</c>), so the saved numbers used
    ///  to scale with the window. The ratio between them is what the user actually chose,
    ///  and it is all the layout needs to restore. <see cref="UiStateService"/> normalizes
    ///  the pair on save and on load, so a file written by an older build (pixel-scale,
    ///  or the original <c>3</c>/<c>2</c> weights) restores to exactly the same split.</para>
    /// </summary>
    public double RevisionsStar { get; set; } = 0.6;

    /// <summary>Right area: the bottom detail panel's share of the vertical split
    /// (see <see cref="RevisionsStar"/>).</summary>
    public double BottomStar { get; set; } = 0.4;

    /// <summary>Commit-info: the detail pane's share of the info/diff split
    /// (see <see cref="RevisionsStar"/>); pairs with <see cref="DiffStar"/>.</summary>
    public double DetailStar { get; set; } = 0.4;

    /// <summary>Commit-info: the diff pane's share of the info/diff split
    /// (see <see cref="RevisionsStar"/>); pairs with <see cref="DetailStar"/>.</summary>
    public double DiffStar { get; set; } = 0.6;

    /// <summary>Whether the bottom panel shows commit detail and diff side by
    /// side (split view on) instead of the diff in its own tab.</summary>
    public bool SplitView { get; set; }

    /// <summary>"Light" or "Dark".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    ///  Which visual style the surface uses: "Modern" (the M77 vector icons and
    ///  neutral ramp) or "Classic" (the pre-M77 look).
    ///
    ///  <para>Orthogonal to <see cref="Theme"/> — all four combinations are valid —
    ///  and stored beside it because it is the same kind of Appearance choice.</para>
    /// </summary>
    public string Style { get; set; } = "Modern";

    /// <summary>
    ///  How large the whole interface is drawn: the name of a
    ///  <see cref="GitExtensions.Avalonia.Theming.UiSize"/> member — since M86 either
    ///  <b>"Standard"</b> (zoom 1.0) or <b>"Large"</b> (zoom 1.25).
    ///
    ///  <para>Sits beside <see cref="Theme"/> and <see cref="Style"/> because it is the
    ///  same kind of Appearance choice, and is independent of both: both levels render in
    ///  all four theme/style combinations.</para>
    ///
    ///  <para><b>Files written before M86 hold one of the four older names</b> ("Small",
    ///  "Normal", "Large", "VeryLarge"). They are migrated on read, not rejected —
    ///  <c>UiSizes.Parse</c> owns the mapping, and in particular sends "VeryLarge" to
    ///  "Large" so that upgrading cannot demote a user who had chosen the largest step
    ///  down to the smallest level.</para>
    ///
    ///  <para>"Standard" is not a neutral placeholder — it is the scale the port is built
    ///  at, which is upstream Git Extensions' own, so the default installs no transform at
    ///  all.</para>
    /// </summary>
    public string UiSize { get; set; } = "Standard";

    /// <summary>
    ///  UI language: the base name of a <c>Translation/*.xlf</c> catalogue
    ///  ("Italian", "German", …), or "English" for the untranslated literals.
    ///  Sits next to <see cref="Theme"/> because upstream treats both as
    ///  Appearance settings (<c>FormSettings</c> → Appearance → <c>Settings.Language</c>).
    /// </summary>
    public string Language { get; set; } = "English";

    /// <summary>
    ///  Which action the body of the toolbar's Pull split button performs, as the
    ///  name of a <see cref="GitExtensions.Extensibility.Git.GitPullAction"/> member
    ///  ("Merge", "Rebase", "Fetch", "FetchAll", "FetchPruneAll").
    ///
    ///  <para>The port's equivalent of upstream's <c>AppSettings.DefaultPullAction</c>
    ///  (same default: merge). It is stored as the enum <i>name</i> rather than its
    ///  numeric value so the JSON stays readable and survives a reordering of the
    ///  enum; an unknown or non-actionable value falls back to "Merge".</para>
    /// </summary>
    public string DefaultPullAction { get; set; } = "Merge";

    /// <summary>
    ///  What to do, without asking, when a push is rejected because the branch is
    ///  behind its remote counterpart: the name of a
    ///  <see cref="GitExtensions.Extensibility.Git.GitPullAction"/> member
    ///  ("Default", "Merge", "Rebase") or <c>""</c> — the default — meaning "ask
    ///  every time".
    ///
    ///  <para>The port's equivalent of upstream's nullable
    ///  <c>AppSettings.AutoPullOnPushRejectedAction</c> (<c>AppSettings.cs:1093</c>),
    ///  which the push-rejected dialog's "Don't show again" check box writes. Empty
    ///  string stands in for upstream's <c>null</c>, since the JSON state has no
    ///  nullable-enum convention. Like upstream, only a PULL choice is remembered:
    ///  "Force push with lease" is never made automatic, because silently
    ///  overwriting a remote branch is not something a check box should arm.</para>
    /// </summary>
    public string AutoPullOnPushRejected { get; set; } = string.Empty;

    /// <summary>
    ///  Where the commit-info panel sits, as the name of a
    ///  <c>Views.CommitInfoPosition</c> member ("BelowGraph", "LeftOfGraph",
    ///  "RightOfGraph"). Upstream persists the same choice
    ///  (<c>AppSettings.CommitInfoPosition</c>); the port had the three positions but
    ///  always restarted at <c>BelowGraph</c>.
    ///
    ///  <para>Stored as a string, not the enum: this service must not depend on the
    ///  view layer, and the JSON stays readable. An unknown value falls back to
    ///  "BelowGraph".</para>
    /// </summary>
    public string CommitInfoPosition { get; set; } = "BelowGraph";

    /// <summary>
    ///  The revision grid's "View" options, keyed by the <c>Opt…</c> ids
    ///  <c>Views.RevisionGridView</c> publishes (<c>ShowRemoteBranches</c>,
    ///  <c>GraphColumn</c>, <c>RelativeDate</c>, …): which columns are shown, which
    ///  refs the walk includes, the date mode and the walk order.
    ///
    ///  <para>Upstream keeps the same set in <c>AppSettings</c>
    ///  (<c>AppSettings.cs:568,1165-1177,1247-1281,1286-1326,1330-1354</c>); in this
    ///  port they used to be session-local, so the grid had to be reconfigured at
    ///  every start.</para>
    ///
    ///  <para>A dictionary keyed by the very ids the menus already use, rather than
    ///  one property per toggle: the ids are the single source of truth for that
    ///  surface (grid flyouts and main menu mirror each other through them), the JSON
    ///  stays readable, and adding a toggle later needs no change here. An id this
    ///  build does not know is ignored on load, and a missing one keeps its default,
    ///  so the file survives both directions of a version skew.</para>
    /// </summary>
    public Dictionary<string, bool> GridViewOptions { get; set; } = [];

    /// <summary>
    ///  How many commits one page of the revision walk loads — the port's equivalent
    ///  of upstream's <c>AppSettings.MaxRevisionGraphCommits</c>
    ///  (<c>AppSettings.cs:1402</c>), chosen from the grid's View menu.
    /// </summary>
    public int GridPageSize { get; set; } = 500;

    /// <summary>
    ///  Full path of the repository that was open when the app was last closed, or
    ///  null if none was. Upstream reopens the last repository at start-up
    ///  (<c>AppSettings.RecentWorkingDir</c>); the path is only a hint and is
    ///  validated (it may have been moved or deleted meanwhile).
    /// </summary>
    public string? LastRepoPath { get; set; }
}

/// <summary>Reads/writes <see cref="UiState"/> to a JSON file, tolerating a
/// missing or corrupt file by returning defaults.</summary>
public sealed class UiStateService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public UiStateService() => _path = ResolvePath();

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _path;

    /// <summary>Loads persisted state; returns defaults if absent or unreadable.</summary>
    public UiState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                UiState? state = JsonSerializer.Deserialize<UiState>(json, Options);
                if (state is not null)
                {
                    return Sanitize(state);
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → fall back to defaults below.
        }

        return new UiState();
    }

    /// <summary>Writes the given state; best-effort (never throws).</summary>
    public void Save(UiState state)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(Sanitize(state), Options));
        }
        catch
        {
            // Persistence is best-effort; a failure must not crash the app.
        }
    }

    // Clamp values so a corrupt/zero entry can never collapse a panel or window.
    private static UiState Sanitize(UiState s)
    {
        s.WindowWidth = Clamp(s.WindowWidth, 400, 100000, 1280);
        s.WindowHeight = Clamp(s.WindowHeight, 300, 100000, 820);
        s.TreeWidth = Clamp(s.TreeWidth, 80, 100000, 260);
        // Splits are stored as proportions, so each pair is normalized as a pair (see
        // UiState.RevisionsStar): that is what makes the restore independent of the
        // window size the split was dragged at, and what migrates the pixel-scale
        // values older builds wrote.
        (s.RevisionsStar, s.BottomStar) = NormalizeSplit(s.RevisionsStar, s.BottomStar, 0.6);
        (s.DetailStar, s.DiffStar) = NormalizeSplit(s.DetailStar, s.DiffStar, 0.4);
        s.Theme = s.Theme == "Light" ? "Light" : "Dark";
        s.Style = s.Style == "Classic" ? "Classic" : "Modern";
        // Round-tripped through the enum, so an unknown or hand-edited name lands on
        // "Standard" rather than reaching the zoom (see UiSizes.Parse). This round trip is
        // ALSO where the M86 migration lands on disk: a file holding one of the four older
        // names ("Small"/"Normal"/"Large"/"VeryLarge") is normalised to the two-level
        // vocabulary here and saved back under the new name, so the old value is read
        // correctly once and then never seen again.
        s.UiSize = GitExtensions.Avalonia.Theming.UiSizes.Name(
            GitExtensions.Avalonia.Theming.UiSizes.Parse(s.UiSize));
        s.BottomTab = string.IsNullOrWhiteSpace(s.BottomTab) ? "Commit" : s.BottomTab.Trim();

        // A corrupt coordinate is dropped rather than clamped: with no position the
        // window centres itself, which is always a valid answer. Real clamping to
        // the current screen happens at restore time, where the screens are known.
        s.WindowX = SanePosition(s.WindowX);
        s.WindowY = SanePosition(s.WindowY);
        s.Language = string.IsNullOrWhiteSpace(s.Language) ? "English" : s.Language.Trim();
        s.DefaultPullAction = SanePullAction(s.DefaultPullAction);
        s.CommitInfoPosition = SaneCommitInfoPosition(s.CommitInfoPosition);
        s.LastRepoPath = string.IsNullOrWhiteSpace(s.LastRepoPath) ? null : s.LastRepoPath.Trim();

        // The grid clamps the page size itself, but a corrupt 0 here would otherwise
        // reach the view and be read as "load nothing".
        s.GridPageSize = s.GridPageSize is >= 50 and <= 100000 ? s.GridPageSize : 500;

        // A null from a hand-edited or truncated file must not become a NullReference
        // at the first toggle: an empty map simply means "every option at its default".
        s.GridViewOptions ??= [];
        return s;
    }

    // Only the three positions the layout can actually build are accepted; anything
    // else collapses to the default. The names mirror Views.CommitInfoPosition, which
    // this service deliberately does not reference.
    private static string SaneCommitInfoPosition(string? value)
        => value?.Trim() switch
        {
            "LeftOfGraph" => "LeftOfGraph",
            "RightOfGraph" => "RightOfGraph",
            _ => "BelowGraph",
        };

    // Only the five actions the Pull split button can actually perform are accepted;
    // None/Default (which upstream treats as "not a valid action") and anything
    // unparseable collapse to Merge, upstream's default.
    private static string SanePullAction(string? value)
        => value?.Trim() switch
        {
            "Rebase" => "Rebase",
            "Fetch" => "Fetch",
            "FetchAll" => "FetchAll",
            "FetchPruneAll" => "FetchPruneAll",
            _ => "Merge",
        };

    /// <summary>
    ///  Turns one side of a two-pane split into the pair of proportions
    ///  <c>(first, 1 - first)</c>, preserving the ratio the two values encode.
    ///
    ///  <para>Deliberately ratio-based rather than value-based, because it has to accept
    ///  three generations of the same file without a version stamp and without losing the
    ///  user's split:</para>
    ///  <list type="bullet">
    ///   <item>proportions written by this build (<c>0.27</c>/<c>0.73</c>) — already normal,
    ///     round-trip unchanged;</item>
    ///   <item>pixel-scale star weights written after a splitter drag by an older build
    ///     (<c>199</c>/<c>525</c>) — the ratio is exactly the split that was on screen, so
    ///     dividing by the sum recovers it;</item>
    ///   <item>the original literal weights (<c>3</c>/<c>2</c>) — which normalize to the
    ///     same <c>0.6</c>/<c>0.4</c> they always rendered as.</item>
    ///  </list>
    ///
    ///  <para>Nothing is therefore discarded on migration: a "looks like pixels" heuristic
    ///  (say, "&gt; 10 means pixels, fall back to the default") would be both unnecessary —
    ///  normalizing a pair is correct for every generation — and lossy, since it cannot tell
    ///  a genuine 90/10 split from a corrupt entry. Only a pair that cannot describe a split
    ///  at all (non-finite, negative, or both sides zero) falls back to the default.</para>
    ///
    ///  <para>Each side keeps at least <see cref="MinSplitShare"/> of the split, so neither
    ///  pane can come back invisible with no splitter left to grab.</para>
    /// </summary>
    private static (double First, double Second) NormalizeSplit(double first, double second, double fallback)
    {
        if (!IsUsableShare(first) || !IsUsableShare(second) || first + second <= 0)
        {
            return (fallback, 1 - fallback);
        }

        double share = first / (first + second);
        share = Math.Clamp(share, MinSplitShare, 1 - MinSplitShare);
        return (share, 1 - share);
    }

    /// <summary>Smallest share of a split either pane may be restored at (3%).</summary>
    private const double MinSplitShare = 0.03;

    private static bool IsUsableShare(double v)
        => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

    private static int? SanePosition(int? v)
        => v is null || v.Value < -100000 || v.Value > 100000 ? null : v;

    private static double Clamp(double v, double min, double max, double fallback)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max)
        {
            return fallback;
        }

        return v;
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

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "ui-state.json");
    }
}
