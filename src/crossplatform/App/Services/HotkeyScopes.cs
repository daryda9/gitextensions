using Avalonia.Input;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The keyboard scopes of the app, one per upstream <c>HotkeySettingsName</c>.
///
///  <para>Upstream binds keys per FORM/CONTROL, not per application: the same F3 is
///  "next match" in the file viewer and "open with difftool" in the file list, and which
///  one fires depends on what has the focus. The port had only
///  <see cref="Browse"/> — the window-wide scope of <see cref="BrowseCommand"/> — and
///  every other surface compared keys inline, so those gestures could not be
///  reconfigured at all. These six close that gap.</para>
/// </summary>
public enum HotkeyScope
{
    /// <summary>The window itself — upstream's <c>FormBrowse</c>, i.e. <see cref="BrowseCommand"/>.</summary>
    Browse,

    /// <summary>The revision grid (upstream <c>RevisionGridControl</c>).</summary>
    RevisionGrid,

    /// <summary>The diff/patch viewer (upstream <c>FileViewer</c>).</summary>
    FileViewer,

    /// <summary>The changed-file list of the Diff tab (upstream <c>RevisionDiffControl</c>).</summary>
    RevisionDiff,

    /// <summary>The left repository-objects tree (upstream <c>RepoObjectsTree</c>).</summary>
    RepoObjectsTree,

    /// <summary>The commit dialog (upstream <c>FormCommit</c>).</summary>
    Commit,

    /// <summary>The stash window (upstream <c>FormStash</c>).</summary>
    Stash,
}

/// <summary>
///  The per-scope command tables: upstream's names, upstream's default gestures.
///
///  <para><b>Only commands the port actually performs are listed.</b> Upstream's
///  <c>RevisionGridControl</c> scope has 45 entries, this one has 19 — the missing ones
///  are actions the port does not have (fixup/squash commits, Visual Studio, build
///  server, difftool-on-commits…). Listing them anyway would put rows in the Settings
///  dialog that bind a key to nothing, which is the fake button this port keeps
///  refusing to ship. Each scope's remarks say what was left out and why.</para>
///
///  <para>The names are upstream's <c>Command</c> enum members, verbatim, so a user's
///  Windows configuration and this one can be compared line by line — and so the tables
///  can be diffed against <c>HotkeySettingsManager.CreateDefaultSettings</c> by eye.</para>
/// </summary>
public static class HotkeyScopes
{
    /// <summary>
    ///  Revision grid — upstream <c>HotkeySettingsManager.cs:272-319</c>.
    ///
    ///  <para>Left out because the port has no such action: CompareSelectedCommits /
    ///  CompareToBase / CompareToBranch / CompareToCurrentBranch / CompareToWorkingDirectory
    ///  and SelectAsBaseToCompare / SelectNextForkPointAsDiffBase (the port compares
    ///  through the grid selection instead), CreateAmendCommit / CreateFixupCommit /
    ///  CreateSquashCommit, DeleteRef / RenameRef (they live in the left tree here),
    ///  OpenCommitsWithDifftool, ResetRevisionPathFilter, ToggleAuthorDateCommitDate,
    ///  ToggleDrawNonRelativesGray, ToggleOrderRevisionsByDate, ToggleRevisionGraph,
    ///  ToggleShowGitNotes(Column), ToggleShowRelativeDate, ToggleHideMergeCommits and
    ///  ShowFirstParent / ShowReflogReferences — all of which are View-menu toggles in the
    ///  port, which upstream leaves unbound (<c>Keys.None</c>) anyway.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, HotkeyGesture> RevisionGrid { get; }
        = new Dictionary<string, HotkeyGesture>(StringComparer.Ordinal)
        {
            ["GoToChild"] = new(Key.N, KeyModifiers.Control),
            ["GoToCommit"] = new(Key.G, KeyModifiers.Control | KeyModifiers.Shift),
            ["GoToFirstParent"] = new(Key.Left, KeyModifiers.Control),
            ["GoToMergeBase"] = new(Key.K, KeyModifiers.Control | KeyModifiers.Shift),
            ["GoToParent"] = new(Key.P, KeyModifiers.Control),
            ["NavigateBackward"] = new(Key.Left, KeyModifiers.Alt),
            ["NavigateForward"] = new(Key.Right, KeyModifiers.Alt),
            ["NextQuickSearch"] = new(Key.Down, KeyModifiers.Alt),
            ["PrevQuickSearch"] = new(Key.Up, KeyModifiers.Alt),
            ["ResetRevisionFilter"] = new(Key.I, KeyModifiers.Control | KeyModifiers.Shift),
            ["RevisionFilter"] = new(Key.I, KeyModifiers.Control),
            ["SelectCurrentRevision"] = new(Key.C, KeyModifiers.Control | KeyModifiers.Shift),
            ["ShowAllBranches"] = new(Key.A, KeyModifiers.Control | KeyModifiers.Shift),
            ["ShowCurrentBranchOnly"] = new(Key.U, KeyModifiers.Control | KeyModifiers.Shift),
            ["ShowFilteredBranches"] = new(Key.T, KeyModifiers.Control | KeyModifiers.Shift),
            ["ShowRemoteBranches"] = new(Key.R, KeyModifiers.Control | KeyModifiers.Shift),
            ["ToggleBetweenArtificialAndHeadCommits"] = new(Key.OemBackslash, KeyModifiers.Control),
            ["ToggleHighlightSelectedBranch"] = new(Key.B, KeyModifiers.Control | KeyModifiers.Shift),
            ["ToggleShowTags"] = new(Key.T, KeyModifiers.Control | KeyModifiers.Alt),
        };

    /// <summary>
    ///  Diff/patch viewer — upstream <c>HotkeySettingsManager.cs:321-342</c>.
    ///
    ///  <para>Left out: Replace (the viewer is read-only), ShowDifftastic and
    ///  ShowGitWordColoring (not ported), TreatFileAsText / ShowSyntaxHighlighting /
    ///  IgnoreAllWhitespace (toolbar toggles with no upstream key worth stealing —
    ///  IgnoreAllWhitespace's Ctrl+Shift+W would shadow nothing here but has no single
    ///  control to act on), NextOccurrence / PreviousOccurrence, and the line
    ///  staging trio (StageLines / UnstageLines / ResetLines), which in this port belongs
    ///  to the commit dialog's own patch pane, not to this viewer.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, HotkeyGesture> FileViewer { get; }
        = new Dictionary<string, HotkeyGesture>(StringComparer.Ordinal)
        {
            ["DecreaseNumberOfVisibleLines"] = new(Key.OemMinus, KeyModifiers.Control),
            ["Find"] = new(Key.F, KeyModifiers.Control),
            ["FindNextOrOpenWithDifftool"] = new(Key.F3, KeyModifiers.None),
            ["FindPrevious"] = new(Key.F3, KeyModifiers.Shift),
            ["GoToLine"] = new(Key.G, KeyModifiers.Control),
            ["IncreaseNumberOfVisibleLines"] = new(Key.OemPlus, KeyModifiers.Control),
            ["NextChange"] = new(Key.Down, KeyModifiers.Alt),
            ["PreviousChange"] = new(Key.Up, KeyModifiers.Alt),
            ["ShowEntireFile"] = new(Key.E, KeyModifiers.Control),
        };

    /// <summary>
    ///  Changed-file list of the Diff tab — upstream <c>HotkeySettingsManager.cs:350-375</c>.
    ///
    ///  <para>Only the four the port can do to a file from that list. The rest of
    ///  upstream's twenty-five are either working-directory actions the Diff tab does not
    ///  offer (stage/unstage/reset/delete/ignore/rename), Windows-only (Visual Studio),
    ///  actions the port reaches from the window instead (EditFile, the temp-file pair),
    ///  or duplicates of a window-level gesture the Browse scope already owns.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, HotkeyGesture> RevisionDiff { get; }
        = new Dictionary<string, HotkeyGesture>(StringComparer.Ordinal)
        {
            ["Blame"] = new(Key.B, KeyModifiers.None),
            ["FilterFileInGrid"] = new(Key.F, KeyModifiers.None),
            ["OpenWithDifftool"] = new(Key.F3, KeyModifiers.None),
            ["ShowHistory"] = new(Key.H, KeyModifiers.None),
        };

    /// <summary>
    ///  Left repository-objects tree — upstream <c>HotkeySettingsManager.cs:265-271</c>.
    ///
    ///  <para>Left out: MultiSelect / MultiSelectWithChildren — the port's tree is
    ///  single-selection by design (M97), so the two would bind a key to nothing.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, HotkeyGesture> RepoObjectsTree { get; }
        = new Dictionary<string, HotkeyGesture>(StringComparer.Ordinal)
        {
            ["Delete"] = new(Key.Delete, KeyModifiers.None),
            ["Rename"] = new(Key.F2, KeyModifiers.None),
            ["Search"] = new(Key.F3, KeyModifiers.None),
        };

    /// <summary>
    ///  Commit dialog — upstream <c>HotkeySettingsManager.cs:194-213</c>.
    ///
    ///  <para>Left out: the two ConventionalCommit prefixes and
    ///  AddSelectionToCommitMessage (not ported), OpenWithDifftool, and the six
    ///  SelectNext/SelectPrevious alternatives — plain arrows already walk the two lists
    ///  here, so those would be a second way to do what the list already does.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, HotkeyGesture> Commit { get; }
        = new Dictionary<string, HotkeyGesture>(StringComparer.Ordinal)
        {
            ["CreateBranch"] = new(Key.B, KeyModifiers.Control),
            ["FocusUnstagedFiles"] = new(Key.D1, KeyModifiers.Control),
            ["FocusSelectedDiff"] = new(Key.D2, KeyModifiers.Control),
            ["FocusStagedFiles"] = new(Key.D3, KeyModifiers.Control),
            ["FocusCommitMessage"] = new(Key.D4, KeyModifiers.Control),
            ["Refresh"] = new(Key.F5, KeyModifiers.None),
            ["StageAll"] = new(Key.S, KeyModifiers.Control),
            ["ToggleSelectionFilter"] = new(Key.F, KeyModifiers.Control),
        };

    /// <summary>Stash window — upstream <c>HotkeySettingsManager.cs:376-380</c>, all three.</summary>
    public static IReadOnlyDictionary<string, HotkeyGesture> Stash { get; }
        = new Dictionary<string, HotkeyGesture>(StringComparer.Ordinal)
        {
            ["NextStash"] = new(Key.N, KeyModifiers.Control),
            ["PreviousStash"] = new(Key.P, KeyModifiers.Control),
            ["Refresh"] = new(Key.F5, KeyModifiers.None),
        };

    /// <summary>The six tables above, by scope. <see cref="HotkeyScope.Browse"/> is absent:
    /// it is <see cref="HotkeyService.Defaults"/>, which is typed by enum.</summary>
    public static IReadOnlyDictionary<HotkeyScope, IReadOnlyDictionary<string, HotkeyGesture>> All { get; }
        = new Dictionary<HotkeyScope, IReadOnlyDictionary<string, HotkeyGesture>>
        {
            [HotkeyScope.RevisionGrid] = RevisionGrid,
            [HotkeyScope.FileViewer] = FileViewer,
            [HotkeyScope.RevisionDiff] = RevisionDiff,
            [HotkeyScope.RepoObjectsTree] = RepoObjectsTree,
            [HotkeyScope.Commit] = Commit,
            [HotkeyScope.Stash] = Stash,
        };

    /// <summary>What the Settings dialog calls each scope.</summary>
    public static string Title(HotkeyScope scope) => scope switch
    {
        HotkeyScope.RevisionGrid => TranslationService.T("Revision grid"),
        HotkeyScope.FileViewer => TranslationService.T("Diff viewer"),
        HotkeyScope.RevisionDiff => TranslationService.T("Changed-file list"),
        HotkeyScope.RepoObjectsTree => TranslationService.T("Repository tree"),
        HotkeyScope.Commit => TranslationService.T("FormCommit/$this.Text", "Commit"),
        HotkeyScope.Stash => TranslationService.T("FormStash/$this.Text", "Stash"),
        _ => TranslationService.T("Main window"),
    };
}
