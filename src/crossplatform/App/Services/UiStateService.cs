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
    ///  "FileTree", "Gpg", "Console", "Output", "Stash", "Blame", "History") rather
    ///  than an index: the Diff tab is removed from the strip while split view is
    ///  on, so positions are not stable across sessions.
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

    /// <summary>Right area: revision-grid row star weight.</summary>
    public double RevisionsStar { get; set; } = 3;

    /// <summary>Right area: bottom detail-panel row star weight.</summary>
    public double BottomStar { get; set; } = 2;

    /// <summary>Commit-info: detail row star weight (top of the info/diff split).</summary>
    public double DetailStar { get; set; } = 2;

    /// <summary>Commit-info: diff row star weight (bottom of the info/diff split).</summary>
    public double DiffStar { get; set; } = 3;

    /// <summary>Whether the bottom panel shows commit detail and diff side by
    /// side (split view on) instead of the diff in its own tab.</summary>
    public bool SplitView { get; set; }

    /// <summary>"Light" or "Dark".</summary>
    public string Theme { get; set; } = "Dark";

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
        s.RevisionsStar = Clamp(s.RevisionsStar, 0.1, 1000, 3);
        s.BottomStar = Clamp(s.BottomStar, 0.1, 1000, 2);
        s.DetailStar = Clamp(s.DetailStar, 0.1, 1000, 2);
        s.DiffStar = Clamp(s.DiffStar, 0.1, 1000, 3);
        s.Theme = s.Theme == "Light" ? "Light" : "Dark";
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
