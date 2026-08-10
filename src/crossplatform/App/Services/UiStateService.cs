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

    /// <summary>
    ///  "System", "Light" or "Dark". "System" — the default — follows the desktop's own
    ///  light/dark preference for as long as the app runs (see
    ///  <see cref="GitExtensions.Avalonia.Theming.SystemTheme"/>); the other two are
    ///  explicit answers that never move.
    /// </summary>
    public string Theme { get; set; } = GitExtensions.Avalonia.Theming.SystemTheme.Name;

    /// <summary>
    ///  What the desktop preferred ("Light" or "Dark") when this app last exited — an
    ///  observation, not a setting, and never shown in the UI. It seeds the first window
    ///  of the next run while the platform's real answer is still on its way, which is
    ///  what stops a dark desktop from flashing white at startup.
    /// </summary>
    public string SystemThemeSeen { get; set; } = "Dark";

    /// <summary>
    ///  Which visual style the surface uses: "Modern" (the M77 vector icons and
    ///  neutral ramp) or "Classic" (the pre-M77 look).
    ///
    ///  <para>Orthogonal to <see cref="Theme"/> — all four combinations are valid —
    ///  and stored beside it because it is the same kind of Appearance choice.</para>
    /// </summary>
    public string Style { get; set; } = "Modern";

    /// <summary>
    ///  Where the main menu sits: "Merged" — the default — puts it in the window's own
    ///  title bar next to the caption and the window buttons, VS Code style; "Standard"
    ///  leaves the desktop's title bar alone and keeps the menu on the row below it.
    ///
    ///  <para><b>Independent of <see cref="Style"/>.</b> It is a layout choice, not a
    ///  palette one, and both arrangements are drawn from whichever palette is in force,
    ///  so all four combinations are valid.</para>
    ///
    ///  <para>Anything that is not exactly "Standard" — including the absent key of every
    ///  state file written before this option existed — reads as "Merged"
    ///  (<c>Theming/WindowChrome.Parse</c>), so an upgrade opens in the new arrangement.</para>
    /// </summary>
    public string TitleBar { get; set; } = GitExtensions.Avalonia.Theming.WindowChrome.MergedName;

    /// <summary>
    ///  How many repositories one window holds: "Tabs" — the default — keeps a strip of
    ///  open repositories and submodules across the top, VS Code style; "Single" gives
    ///  the window one repository at a time, as it worked before the strip existed.
    ///
    ///  <para>Stored as a name rather than as a bool for the same reason as
    ///  <see cref="TitleBar"/> and <see cref="Style"/>: the file is meant to be read
    ///  (and hand-edited) by a human, and "RepoTabs": "Single" says what it means where
    ///  "RepoTabs": false would only say which way a flag whose polarity is not written
    ///  down happens to point.</para>
    ///
    ///  <para>Anything that is not exactly "Single" — including the absent key of every
    ///  state file written before this option existed — reads as "Tabs"
    ///  (<c>Theming/RepoTabsOption.Parse</c>), so an upgrade opens in the new
    ///  arrangement.</para>
    /// </summary>
    public string RepoTabs { get; set; } = GitExtensions.Avalonia.Theming.RepoTabsOption.TabsName;

    /// <summary>
    ///  The repositories the tab strip held when the app was last closed, in strip
    ///  order, so a session comes back with the same set of tabs rather than only the
    ///  last one (<see cref="LastRepoPath"/>, which stays the answer while
    ///  <see cref="RepoTabs"/> is "Single").
    ///
    ///  <para>Every path is only a hint and is validated at restore time: a repository
    ///  may have been moved or deleted between two runs, and a tab that cannot be opened
    ///  must be dropped, not fatal. <see cref="UiStateService"/> additionally caps the
    ///  list — see the sanitiser — because nothing else bounds how many entries a
    ///  hand-edited or truncated file can carry into start-up.</para>
    /// </summary>
    public List<RepoTabState> OpenRepoTabs { get; set; } = [];

    /// <summary>
    ///  Path of the tab that was in front, or null for "the first one".
    ///
    ///  <para>A path rather than an index: the sanitiser drops blank and duplicate
    ///  entries from <see cref="OpenRepoTabs"/>, so an index saved beside them would
    ///  quietly point at a different repository after any such repair. It is validated
    ///  against the surviving list for the same reason — an active path that names no
    ///  open tab is dropped rather than left to select nothing.</para>
    /// </summary>
    public string? ActiveRepoTab { get; set; }

    /// <summary>
    ///  Whether the modern style's vector icons are painted in their accent role
    ///  (green for create, red for destroy, blue for transfer…) rather than all in the
    ///  text colour.
    ///
    ///  <para>On by default: the monochrome set was a consequence of how the glyphs are
    ///  drawn, not a choice, and a toolbar of twenty identical grey marks is harder to
    ///  read than a coloured one. Off is lossless — no icon carries meaning by colour
    ///  alone (see the role table in <c>Theming/Icons.cs</c>).</para>
    ///
    ///  <para>Read only by the modern style; the classic one draws the 2015 PNGs, whose
    ///  colours are baked in.</para>
    /// </summary>
    public bool ColoredIcons { get; set; } = true;

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
    ///  The command line to open a terminal, or <c>""</c> — the default — meaning
    ///  "probe the known emulators", which is what the port has always done.
    ///
    ///  <para>Upstream has no counterpart: on Windows the terminal is Git bash at a
    ///  known path. On Linux the candidate list can only ever be a guess, and it
    ///  cannot be complete — Warp, for one, is reachable through
    ///  <c>x-terminal-emulator</c> but rejects the <c>-e</c> that every entry in the
    ///  list passes it (M127). Naming the command is the escape hatch for exactly
    ///  that case.</para>
    ///
    ///  <para>The value is a command line, split on spaces with quoting honoured. Two
    ///  placeholders are substituted when present: <c>{dir}</c> — the directory to
    ///  open in — and <c>{shell}</c> — the shell chosen in the Terminal drop-down.
    ///  Without <c>{dir}</c> the directory is still handed over as the child's
    ///  working directory; without <c>{shell}</c> the emulator starts the login
    ///  shell. A configured command that fails to start falls through to the probe
    ///  list rather than leaving the user with a dead button.</para>
    /// </summary>
    public string TerminalCommand { get; set; } = string.Empty;

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

/// <summary>
///  One entry of <see cref="UiState.OpenRepoTabs"/>: a repository the tab strip had
///  open, plus the little per-tab state that makes reopening it feel like coming back
///  rather than starting again.
///
///  <para>A class of its own rather than a bare list of paths because a tab is more
///  than its path, and a parallel array per field would have to stay index-aligned
///  through the sanitiser's de-duplication — which is exactly the drift this port
///  avoids elsewhere (see <see cref="UiState.ActiveRepoTab"/>).</para>
/// </summary>
public sealed class RepoTabState
{
    /// <summary>
    ///  Full path of the repository or submodule working directory. The identity of the
    ///  tab: the sanitiser de-duplicates on it and <see cref="UiState.ActiveRepoTab"/>
    ///  names a tab by it. Never null — an entry whose path is blank is dropped.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///  Whether the user pinned the tab, VS Code style: pinned tabs are kept when the
    ///  rest of the strip is closed and are never reused for another repository.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>
    ///  The commit that was selected in this tab's grid, or null for "whatever the grid
    ///  lands on". A hint like every path here: the commit may have been rewritten or
    ///  garbage-collected between two runs, so a miss simply selects the default.
    /// </summary>
    public string? SelectedCommit { get; set; }

    /// <summary>
    ///  This tab's bottom-panel tab, in the same vocabulary as
    ///  <see cref="UiState.BottomTab"/>, or null to inherit that global choice — which
    ///  is what every tab restored from a file written before the strip existed does.
    /// </summary>
    public string? BottomTab { get; set; }
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
        // Three values now; anything else lands on "System", which is what a fresh
        // install gets and what a hand-edited file should fall back to — following the
        // desktop is never wrong, where a hard "Dark" can contradict it.
        s.Theme = s.Theme is "Light" or "Dark" ? s.Theme : GitExtensions.Avalonia.Theming.SystemTheme.Name;
        s.SystemThemeSeen = s.SystemThemeSeen == "Light" ? "Light" : "Dark";
        s.Style = s.Style == "Classic" ? "Classic" : "Modern";

        // Round-tripped through the parser, so "absent or unknown means Merged" is
        // stated once and the normalised file always holds one of the two names.
        s.TitleBar = GitExtensions.Avalonia.Theming.WindowChrome.Name(
            GitExtensions.Avalonia.Theming.WindowChrome.Parse(s.TitleBar));

        // Same round trip, same reason: "absent or unknown means Tabs" is stated once,
        // in the parser, and the normalised file always holds one of the two names.
        s.RepoTabs = GitExtensions.Avalonia.Theming.RepoTabsOption.Name(
            GitExtensions.Avalonia.Theming.RepoTabsOption.Parse(s.RepoTabs));
        SanitizeRepoTabs(s);
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

    /// <summary>
    ///  Repairs the restored tab strip in place: no null list, no blank or duplicate
    ///  paths, no unbounded list, and an active path that really names one of the tabs.
    ///
    ///  <para>The cap is the load-bearing part. Every other entry in this file describes
    ///  one window and costs nothing to get wrong, but each surviving tab here is a
    ///  repository the next start-up will open — a truncated, merged or hand-edited file
    ///  must not be able to launch a hundred git processes before the window is even
    ///  visible. Thirty is far past any real strip and still bounded.</para>
    ///
    ///  <para>De-duplication is first-wins and ignores a trailing separator, because the
    ///  same repository reached through the tree and through the recent list can be
    ///  written once with and once without it; two tabs over one working directory would
    ///  then fight over the same watcher. It is deliberately NOT a full path
    ///  canonicalisation (no symlink resolution, no case folding): that touches the disk,
    ///  and this runs on every save.</para>
    /// </summary>
    private static void SanitizeRepoTabs(UiState s)
    {
        // A null from a hand-edited or truncated file must not become a NullReference at
        // the first tab click: no tabs is a valid strip.
        List<RepoTabState> tabs = s.OpenRepoTabs ?? [];
        List<RepoTabState> kept = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (RepoTabState tab in tabs)
        {
            if (tab is null || string.IsNullOrWhiteSpace(tab.Path))
            {
                continue;
            }

            tab.Path = tab.Path.Trim();
            if (!seen.Add(TabKey(tab.Path)))
            {
                continue;
            }

            kept.Add(tab);
            if (kept.Count == MaxRepoTabs)
            {
                break;
            }
        }

        s.OpenRepoTabs = kept;

        string? active = string.IsNullOrWhiteSpace(s.ActiveRepoTab) ? null : s.ActiveRepoTab.Trim();

        // An active path that no longer names a tab — dropped as a duplicate, cut by the
        // cap, or never in the list at all — becomes null, which the strip reads as "the
        // first tab". Left as it was it would select nothing and show an empty window.
        s.ActiveRepoTab = active is not null && seen.Contains(TabKey(active)) ? active : null;
    }

    /// <summary>How many tabs one strip may be restored with (see
    /// <see cref="SanitizeRepoTabs"/> for why there is a limit at all).</summary>
    private const int MaxRepoTabs = 30;

    // The identity a tab is de-duplicated and looked up by: the path without a trailing
    // separator, so "/src/repo" and "/src/repo/" are one tab.
    private static string TabKey(string path)
        => path.Length > 1 ? path.TrimEnd('/') : path;

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
