using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitCommands;
using GitCommands.Settings;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;
using GitExtensions.Extensibility.Configurations;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A pragmatic, native Settings dialog for the Avalonia / Linux port, loosely
///  echoing the WinForms <c>FormSettings</c> layout (a left category list with a
///  right editing panel) — but deliberately NOT a port of the
///  <c>ISettingControlBinding</c> framework. It exposes a small set of real,
///  working settings:
///
///  <list type="number">
///   <item>Git identity (<c>user.name</c> / <c>user.email</c>) read/written via
///    the reused core config surface (<see cref="GitConfigSettings"/>) at the level
///    the user picks — <i>Local for current repository</i> or <i>Global for all
///    repositories</i>, a cut-down port of upstream's
///    <c>SettingsPageHeader</c> level selector.</item>
///   <item>The eight tri-state keys of upstream's <c>GitConfigAdvancedSettingsPage</c>
///    (<c>pull.rebase</c>, <c>fetch.prune</c>, the two autostashes, …), written into
///    git config at the same chosen level. They need no consumer inside the port —
///    the consumer is git.</item>
///   <item>The three blame switches of <c>BlameViewerSettingsPage</c>
///    (<c>-w</c> / <c>-M</c> / <c>-C</c>), which change what <c>git blame</c> itself
///    computes — see <see cref="BlameOptions"/>.</item>
///   <item>Three settings the port already consumed but never let anyone change:
///    automatic refresh (<see cref="UiState.AutoRefresh"/>, which decides whether the
///    repository watcher follows the repo), the checkout dialog's default for local
///    changes (<see cref="AppPreferences.DefaultCheckoutLocalChangesAction"/>) and the
///    commit-info panel's visibility toggles
///    (<see cref="CommitInfoSettingsService"/>).</item>
///   <item>A Hotkeys page over <see cref="HotkeyService"/>: every command with its
///    gesture, recorded by pressing the combination, with duplicate detection and
///    "Reset all" (upstream's <c>HotkeysSettingsPage</c> + <c>ControlHotkeys</c>).</item>
///   <item>Default pull action (the five actions the toolbar's Pull split button
///    offers), persisted in <see cref="UiState.DefaultPullAction"/> — the value the
///    split button itself reads.</item>
///   <item>Default theme (Light / Dark) and visual style (Modern / Classic), two
///    independent choices persisted via <see cref="UiStateService"/> and applied live
///    through <see cref="ThemeManager"/>.</item>
///  </list>
///
///  <para><b>Buttons.</b> OK applies + persists everything and closes, Apply does
///  the same without closing, Cancel discards — reverting a live appearance preview
///  back to the theme AND style that were active on open (or to the last Apply).</para>
///
///  <para><b>Translation.</b> This is the port's most text-dense window, so every
///  caption goes through <see cref="TranslationService"/> and is registered in
///  <see cref="_relabel"/>, letting <see cref="Retranslate"/> re-word the whole
///  dialog in place when the language changes. Keys point at the upstream form each
///  control corresponds to: <c>FormSettings</c> for the window title and the
///  OK/Cancel/Apply bar, <c>GitConfigSettingsPage</c> for the identity fields,
///  <c>GeneralSettingsPage</c> for the pull behaviour, <c>AppearanceSettingsPage</c>
///  and <c>ColorsSettingsPage</c> for the theme, <c>SettingsPageHeader</c> for the
///  "Settings source" note. The bespoke prose of this port (the per-category
///  descriptions and the "Git identity" category name) has no upstream trans-unit,
///  so it uses the one-argument overload and stays English until a catalogue gains
///  the strings.</para>
///
///  <para><b>Layout.</b> Translated captions are markedly longer than the English
///  ones ("Default pull action" → "Azione predefinita per il pull"), so nothing
///  here is sized to the English text: the category column is <c>Auto</c> with a
///  minimum width, every label wraps, the editing pane scrolls, the button bar
///  wraps, and the window carries a <see cref="Window.MinWidth"/> that keeps the
///  three buttons inside the frame.</para>
/// </summary>
public sealed class SettingsWindow : Theming.ZoomWindow
{
    private readonly string? _repoPath;
    private readonly UiStateService _uiStateService = new();

    private readonly TextBox _userName;
    private readonly TextBox _userEmail;
    private readonly ComboBox _settingsLevel;
    private readonly ComboBox _pullAction;
    private readonly ComboBox _theme;
    private readonly ComboBox _style;
    private readonly ComboBox _uiSize;
    private readonly ComboBox _titleBar;
    private readonly ComboBox _repoTabs;
    private readonly CheckBox _coloredIcons;

    // One three-state checkbox per GitConfigChoices entry, same order.
    private readonly CheckBox[] _gitConfigChecks;

    // The level the identity fields currently show, so a level change can be told
    // from the initial load and the fields reloaded for the new level.
    private GitSettingLevel _loadedLevel = GitSettingLevel.Local;
    private bool _loadingIdentity;
    private bool _loadingGitConfig;

    // The three blame switches (BlameViewerSettingsPage). They are not stored by this
    // dialog: BlameOptions is a view of upstream's AppSettings, which is exactly what
    // the core's GitModule.Blame reads while building the command line.
    // ---- Hotkeys page ------------------------------------------------------
    // The service whose map is edited. The host passes the live instance (the one the
    // window installed its key handler from and the one the toolbar and menu read their
    // gesture captions off); with none passed this is a fresh instance over the same
    // hotkeys.json, so the edit still persists — it just cannot take effect until the
    // next start. See the MainWindow wiring note on ShowAsync.
    private readonly HotkeyService _hotkeys;
    private readonly bool _hotkeysAreLive;

    // The pending edits: command → gesture, with null meaning "cleared". Applied in one
    // go, so Cancel really discards and a half-finished remapping never reaches the
    // running window.
    // Keyed by SCOPE + command name, so the window's own commands (typed by enum) and
    // the six per-control scopes (named by upstream's strings) live in one draft, one
    // list of rows and one capture state. The Browse rows carry the enum's name.
    private readonly Dictionary<(HotkeyScope Scope, string Command), HotkeyGesture?> _hotkeyDraft = [];
    private readonly List<((HotkeyScope Scope, string Command) Id, TextBlock Name, Button Gesture)> _hotkeyRows = [];

    // The command whose next keystroke is being captured, if any.
    private (HotkeyScope Scope, string Command)? _capturing;
    private TextBlock _hotkeyWarning = null!;

    // Behaviour page, beyond the pull action.
    private readonly CheckBox _autoRefresh;

    // The terminal command line. A TextBox and not a drop-down of candidates: the
    // point of the setting is the emulator the probe list does NOT know about.
    private readonly TextBox _terminalCommand;
    private readonly ComboBox _checkoutLocalChanges;

    // ---- Commit page: the seven message-editor settings upstream keeps in
    // AppSettings and in FormCommitTemplateSettings, none of which the port had a
    // consumer for until now.
    private readonly CheckBox _messageWordWrap;
    private readonly CheckBox _messageSecondLineEmpty;
    private readonly CheckBox _messageAutoWrap;
    private readonly CheckBox _messageMarkIllFormed;
    private readonly TextBox _messageFirstLineLimit;
    private readonly TextBox _messageLineLimit;
    private readonly TextBox _previousMessageCount;

    // ---- Diff viewer page: seven more settings that had no consumer.
    private readonly TextBox _diffRulerPosition;
    private readonly CheckBox _eolAsGlyph;
    private readonly CheckBox _continuousScroll;
    private readonly TextBox _continuousScrollDelay;
    private readonly CheckBox _omitUninteresting;
    private readonly CheckBox _histogramDiff;
    private readonly CheckBox _diffAllParents;

    // ---- Revision graph page: seven more.
    private readonly CheckBox _nonRelativesTextGray;
    private readonly CheckBox _alternateRowColor;
    private readonly CheckBox _multicolorBranches;
    private readonly CheckBox _colorPerBranch;
    private readonly CheckBox _colorAtRemoteMirror;
    private readonly CheckBox _straightenDiagonals;
    private readonly TextBox _straightenLimit;
    private readonly CheckBox _highlightAuthored;
    private readonly CheckBox _gridTooltips;

    // ---- Stash / checkout / push page: six more.
    private readonly CheckBox _untrackedManualStash;
    private readonly CheckBox _untrackedAutoStash;
    private readonly ComboBox _popAfterCheckout;
    private readonly ComboBox _popAfterPull;
    private readonly CheckBox _rebaseAutoStash;
    private readonly ComboBox _recursiveSubmodules;

    // ---- Dashboard / paths page: four more.
    private readonly TextBox _recentHistorySize;
    private readonly CheckBox _sortRecentRepos;
    private readonly ComboBox _shorteningStrategy;
    private readonly ComboBox _truncatePathMethod;

    // ---- Fonts, on the Appearance page: the last two.
    private readonly ComboBox _uiFont;
    private readonly TextBox _uiFontSize;
    private readonly ComboBox _monospaceFont;
    private readonly TextBox _monospaceFontSize;

    // ---- GitHub page. The token is NOT one of these fields in the usual sense: the
    // box is write-only. It starts empty whatever is stored, typing into it replaces
    // the stored token, and leaving it empty changes nothing — so the token never has
    // to be rendered, copied into a control's Text, or written to app-settings.json.
    private readonly TextBox _githubHost = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _githubToken = new() { PasswordChar = '•', MinWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _githubTokenNote = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _githubIssueMessages = new();
    private readonly TextBox _githubIssueCount = NumberBox();

    // ---- Scripts page: the user scripts and their editor. The list is edited in place
    // and only written on OK/Apply, so Cancel really cancels.
    private readonly ListBox _scriptList = new() { MinHeight = 150 };
    private readonly TextBox _scriptName = new();
    private readonly TextBox _scriptCommand = new();
    private readonly TextBox _scriptArguments = new();
    private readonly ComboBox _scriptEvent = new() { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _scriptEnabled = new();
    private readonly CheckBox _scriptAsk = new();
    private readonly CheckBox _scriptBackground = new();
    private readonly CheckBox _scriptInGridMenu = new();
    private readonly List<UserScript> _scripts = [];
    private bool _loadingScript;

    // Commit-info page: one checkbox per CommitInfoSettings flag, same order as
    // CommitInfoChoices.
    private readonly CheckBox[] _commitInfoChecks;
    private readonly CommitInfoSettingsService _commitInfoService = new();

    private readonly CheckBox _blameIgnoreWhitespace;
    private readonly CheckBox _blameDetectCopyInFile;
    private readonly CheckBox _blameDetectCopyInAll;

    // Category panels, shown one at a time in the right pane — index-aligned with the
    // left list, so a new category is one entry in each.
    private readonly List<Panel> _pages = [];

    // The level selector, hoisted out of the identity panel: it now governs every
    // page that reads/writes git config (identity AND the advanced keys), exactly as
    // upstream's SettingsPageHeader governs a whole GitConfigBaseSettingsPage.
    private readonly Control _levelHeader;

    // Every caption re-applies itself from here, so a language switch needs no
    // rebuild of the control tree (and no reload of the user's pending edits).
    private readonly List<Action> _relabel = [];

    // The theme and style to restore when the dialog is dismissed without applying.
    // Both are captured together and restored together: a preview always applies the
    // pair, so a Cancel that put back only one would leave the other one previewed.
    private string _revertTheme;
    private string _revertStyle;

    // Same contract as the two above, for the UI size. Its own field rather than a
    // third element of a tuple: UiScaling.Apply takes one argument, so there is no
    // pair to keep together here (see Theming/UiScaling for why the size is not a
    // third argument to ThemeManager.Apply).
    private UiSize _revertUiSize;

    // Same contract again, for icon colouring: its own field because ThemeManager
    // takes it through its own call, not as part of the theme/style pair.
    private bool _revertColoredIcons;

    // And again for where the menu sits. NOT part of the theme/style pair either: the
    // arrangement is independent of the visual style, so it is previewed, reverted and
    // persisted on its own (see Theming/WindowChrome).
    private bool _revertMergedTitleBar;

    // And once more for the repository tab strip, for exactly the same reason: how many
    // repositories a window holds is independent of style, size and title bar, so it is
    // previewed, reverted and persisted on its own (see Theming/RepoTabsOption).
    private bool _revertRepoTabs;

    private bool _applied;

    // The default pull action the host is using right now (its in-memory UiState may
    // be ahead of the file, because the toolbar's "set as default" writes the shared
    // instance and only the host saves it). Null means "read it from the file".
    private readonly string? _currentPullAction;

    // Raised with the chosen GitPullAction name when the user applies. MainWindow
    // keeps ONE UiState instance and re-serialises it in full on close, so a write to
    // the file from here would be undone at exit: the host has to update its own
    // instance, which is what this callback is for.
    private readonly Action<string>? _pullActionChanged;

    // The automatic-refresh flag the host is using right now, and the callback that
    // hands a new value back to it. Same reason as the pull action: AutoRefresh lives in
    // UiState, MainWindow keeps ONE instance of it and re-serialises the whole object on
    // close, so a write from here would be undone at exit — and the repository watcher
    // is started from that instance, so only the host can act on the change.
    private readonly bool? _currentAutoRefresh;
    private readonly Action<bool>? _autoRefreshChanged;

    // Raised on the UI thread after the blame switches have been written, so a Blame
    // tab that is already showing a file can re-run it (BlameView.ReloadBlameOptions).
    // Optional: without it the new switches simply take effect at the next blame.
    private readonly Action? _blameOptionsChanged;

    // The default-pull-action choices.
    //
    // The tokens are the names of GitExtensions.Extensibility.Git.GitPullAction,
    // because the value written here is UiState.DefaultPullAction — the one the
    // toolbar's Pull split button actually reads and writes (UiStateService.cs:98,
    // MainToolbar.cs:627). This combo used to write AppPreferences.DefaultPullAction
    // ("merge"/"rebase"/"fetch"), which nothing consumed: choosing here had no effect
    // on the toolbar at all.
    //
    // All five actions the split button offers are listed, with the split button's
    // own captions (MainToolbar.cs:822-829), so the two places cannot disagree.
    private static readonly (string Token, string Key, string Label)[] PullChoices =
    [
        ("Merge", "FormBrowse/_pullMerge.Text", "Pull - merge"),
        ("Rebase", "FormBrowse/_pullRebase.Text", "Pull - rebase"),
        ("Fetch", "FormBrowse/_pullFetch.Text", "Fetch"),
        ("FetchAll", "FormBrowse/_pullFetchAll.Text", "Fetch all"),
        ("FetchPruneAll", "FormBrowse/_pullFetchPruneAll.Text", "Fetch and prune all"),
    ];

    // Git config levels this dialog can read and write. Upstream's page header offers
    // Effective / Local / Global / Distributed / System (SettingsPageHeader.cs); the
    // port carries the two that are actionable for an identity — the repository's own
    // config and the user's global one. Without Global, the identity of a fresh repo
    // could not be configured at all.
    private static readonly (GitSettingLevel Level, string Key, string Label)[] LevelChoices =
    [
        (GitSettingLevel.Local, "SettingsPageHeader/LocalRB.Text", "Local for current repository"),
        (GitSettingLevel.Global, "SettingsPageHeader/GlobalRB.Text", "Global for all repositories"),
    ];

    // The eight tri-state git config keys of upstream's "Git config advanced" page
    // (GitConfigAdvancedSettingsPage.cs:19-26), with that page's own captions so the
    // two cannot drift.
    //
    // Tri-state is the whole point: "set to true", "set to false" and "not set" are
    // three different things to git — an unset pull.rebase inherits whatever a wider
    // config level says, while an explicit false pins it. Applying therefore writes
    // "true"/"false" for a determinate box and *unsets* the key for an indeterminate
    // one (GitConfigSettings.SetValue(name, null) → "git config --unset").
    //
    // These need no consumer inside the port: the consumer is git itself, on every
    // pull/fetch/merge/rebase the app runs.
    private static readonly (string Key, string TransKey, string Label)[] GitConfigChoices =
    [
        ("pull.rebase", "GitConfigAdvancedSettingsPage/checkBoxPullRebase.Text",
            "Rebase local branch when pulling (instead of merge)"),
        ("fetch.prune", "GitConfigAdvancedSettingsPage/checkBoxFetchPrune.Text",
            "Prune remote branches during fetch"),
        ("merge.autostash", "GitConfigAdvancedSettingsPage/checkboxMergeAutoStash.Text",
            "Automatically stash before doing a merge"),
        ("rebase.autostash", "GitConfigAdvancedSettingsPage/checkBoxRebaseAutostash.Text",
            "Automatically stash before doing a rebase"),
        ("rebase.autosquash", "GitConfigAdvancedSettingsPage/checkBoxRebaseAutosquash.Text",
            "Automatically squash commits when doing an interactive rebase"),
        ("rebase.updaterefs", "GitConfigAdvancedSettingsPage/checkBoxUpdateRefs.Text",
            "Rebase also dependent branches"),
        ("rerere.enabled", "GitConfigAdvancedSettingsPage/checkBoxReReReEnabled.Text",
            "Reuse recorded resolution of conflicted merges"),
        ("rerere.autoupdate", "GitConfigAdvancedSettingsPage/checkBoxReReReAutoUpdate.Text",
            "Automatically apply recorded resolution of conflicted merges"),
    ];

    // Category names, shared by the left list and the panel heading so the two can
    // never drift apart.
    // The port's own wording: no upstream trans-unit, hence a null key.
    private const string? IdentityKey = null;
    private const string IdentityText = "Git identity";
    private const string GitConfigKey = "GitConfigAdvancedSettingsPage/$this.Text";
    private const string GitConfigText = "Git config advanced";
    private const string BlameKey = "BlameViewerSettingsPage/groupBoxBlameSettings.Text";
    private const string BlameText = "Blame settings";
    private const string CommitInfoKey = "CommitInfo/$this.Text";
    private const string CommitInfoText = "Commit info";
    private const string HotkeysKey = "HotkeysSettingsPage/$this.Text";
    private const string HotkeysText = "Hotkeys";

    // What the checkout dialog pre-selects for pending local changes. Tokens are the
    // names of LocalChangesAction, i.e. exactly what AppPreferences stores and
    // CheckoutBranchDialog parses back (CheckoutBranchDialog.cs:204). Captions are the
    // dialog's own, so the two places cannot describe the same choice differently.
    private static readonly (string Token, string Key, string Label)[] CheckoutChoices =
    [
        ("DontChange", "FormCheckoutBranch/rbDontChange.Text", "Don't change"),
        ("Merge", "FormCheckoutBranch/rbMerge.Text", "Merge"),
        ("Reset", "FormCheckoutBranch/rbReset.Text", "Reset"),
        ("Stash", "FormCheckoutBranch/rbStash.Text", "Stash"),
    ];

    // The commit-info panel's visibility toggles, with the panel's own captions
    // (CommitDetailView's context menu builds them from the same keys).
    private static readonly (string Key, string Label)[] CommitInfoChoices =
    [
        ("CommitInfo/showContainedInBranchesToolStripMenuItem.Text",
            "Show local branches containing this commit"),
        ("CommitInfo/showContainedInBranchesRemoteToolStripMenuItem.Text",
            "Show remote branches containing this commit"),
        ("CommitInfo/showContainedInBranchesRemoteIfNoLocalToolStripMenuItem.Text",
            "Show remote branches only when no local branch contains this commit"),
        ("CommitInfo/showContainedInTagsToolStripMenuItem.Text", "Show tags containing this commit"),
        ("CommitInfo/showMessagesOfAnnotatedTagsToolStripMenuItem.Text", "Show messages of annotated tags"),
        ("CommitInfo/showTagThisCommitDerivesFromMenuItem.Text",
            "Show the most recent tag this commit derives from"),
    ];
    private const string BehaviourKey = "GeneralSettingsPage/groupBoxBehaviour.Text";
    private const string BehaviourText = "Behaviour";
    private const string AppearanceKey = "AppearanceSettingsPage/$this.Text";
    private const string AppearanceText = "Appearance";
    private const string CommitKey = "FormCommit/$this.Text";
    private const string CommitText = "Commit";
    private const string DiffViewerKey = "DiffViewerSettingsPage/$this.Text";
    private const string DiffViewerText = "Diff viewer";
    private const string GraphKey = "DetailedSettingsPage/tlpnlRevisionGraph.Text";
    private const string GraphText = "Revision graph";
    private const string StashKey = "FormStash/$this.Text";
    private const string StashText = "Stash and checkout";
    private const string DashboardKey = "Dashboard/$this.Text";
    private const string DashboardText = "Dashboard and paths";
    private const string ScriptsKey = "ScriptsSettingsPage/$this.Text";
    private const string ScriptsText = "Scripts";

    // No trans-unit to borrow: upstream's GitHub settings live in the plugin's own
    // generated settings page, which has no translated caption of its own.
    private const string GitHubKey = null;
    private const string GitHubText = "GitHub";

    // The events a script can be bound to, in the order upstream's combo lists them.
    // Tokens are the names of UserScriptEvent, so the file stays readable.
    private static readonly (UserScriptEvent Event, string Label)[] ScriptEvents =
    [
        (UserScriptEvent.None, "(never — run it by hand)"),
        (UserScriptEvent.ShowInUserMenuBar, "Show it in the Tools menu"),
        (UserScriptEvent.BeforeCommit, "Before a commit"),
        (UserScriptEvent.AfterCommit, "After a commit"),
        (UserScriptEvent.BeforePush, "Before a push"),
        (UserScriptEvent.AfterPush, "After a push"),
        (UserScriptEvent.BeforePull, "Before a pull"),
        (UserScriptEvent.AfterPull, "After a pull"),
        (UserScriptEvent.BeforeFetch, "Before a fetch"),
        (UserScriptEvent.AfterFetch, "After a fetch"),
        (UserScriptEvent.BeforeCheckout, "Before a checkout"),
        (UserScriptEvent.AfterCheckout, "After a checkout"),
        (UserScriptEvent.BeforeMerge, "Before a merge"),
        (UserScriptEvent.AfterMerge, "After a merge"),
    ];

    // The three answers of the two auto-pop drop-downs, in AskAlwaysNever order.
    private static readonly (string Key, string Label)[] AskChoices =
    [
        (null!, "Ask each time"),
        (null!, "Always"),
        (null!, "Never"),
    ];

    public SettingsWindow(
        string? repoPath,
        string? currentPullAction = null,
        Action<string>? pullActionChanged = null,
        Action? blameOptionsChanged = null,
        bool? currentAutoRefresh = null,
        Action<bool>? autoRefreshChanged = null,
        HotkeyService? hotkeys = null)
    {
        _hotkeysAreLive = hotkeys is not null;
        _hotkeys = hotkeys ?? HotkeyService.Shared;
        _repoPath = repoPath;
        _currentPullAction = currentPullAction;
        _pullActionChanged = pullActionChanged;
        _blameOptionsChanged = blameOptionsChanged;
        _currentAutoRefresh = currentAutoRefresh;
        _autoRefreshChanged = autoRefreshChanged;

        IBrush window = Resource("App.Window", "#1E1E1E");
        IBrush panel = Resource("App.Panel", "#252526");
        IBrush border = Resource("App.Border", "#3F3F46");
        IBrush text = Resource("App.Text", "#DCDCDC");
        IBrush dim = Resource("App.TextDim", "#9B9B9B");

        Width = 680;
        Height = 460;

        // Enough for the widest translated button row plus the category column.
        MinWidth = 520;
        MinHeight = 320;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        // ---- Category panels (right pane content) --------------------------
        _userName = new TextBox();
        _userEmail = new TextBox { Watermark = "you@example.com" };
        LocalizeWatermark(_userName, null, "Your name");

        _settingsLevel = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach ((GitSettingLevel _, string key, string label) in LevelChoices)
        {
            ComboBoxItem item = new();
            Localize(item, key, label);
            _settingsLevel.Items.Add(item);
        }

        // With no repository open only the global config is reachable.
        _settingsLevel.SelectedIndex = 0;
        _settingsLevel.SelectionChanged += (_, _) => OnLevelChanged();

        // Shared by every git-config-backed page, so switching page keeps the level.
        _levelHeader = Field("SettingsPageHeader/label1.Text", "Settings source:", _settingsLevel, dim);
        _levelHeader.Margin = new Thickness(20, 16, 20, 0);

        Panel identityPanel = CategoryPanel(
            IdentityKey, IdentityText,
            null, "Stored with git config at the level chosen above. \"Local\" writes the "
                + "current repository's config, \"Global\" your user-wide config "
                + "(~/.gitconfig) — the one a brand-new repository inherits. Clearing a "
                + "field removes the entry from the chosen level.",
            text,
            dim,
            Field("GitConfigSettingsPage/label3.Text", "User name", _userName, dim),
            Field("GitConfigSettingsPage/label4.Text", "User email", _userEmail, dim));

        // ---- Git config advanced: eight tri-state keys, written straight to git.
        _gitConfigChecks = new CheckBox[GitConfigChoices.Length];
        Control[] gitConfigFields = new Control[GitConfigChoices.Length];
        for (int i = 0; i < GitConfigChoices.Length; i++)
        {
            (string key, string transKey, string label) = GitConfigChoices[i];

            // IsThreeState makes the box cycle unchecked → checked → indeterminate,
            // which is exactly the "false / true / not set" triple git understands.
            CheckBox box = new() { IsThreeState = true };
            Localize(box, transKey, label, $" [{key}]");
            _gitConfigChecks[i] = box;
            gitConfigFields[i] = box;
        }

        Panel gitConfigPanel = CategoryPanel(
            GitConfigKey, GitConfigText,
            null, "Written directly into the git config at the level chosen above, so git "
                + "itself obeys them — inside this app and outside it. Each box has three "
                + "states: checked (the key is set to true), unchecked (set to false) and "
                + "the third, filled state (the key is not set at all, so a wider config "
                + "level or git's own default decides).",
            text,
            dim,
            gitConfigFields);

        // ---- Blame: the three switches that change git blame's own output.
        // Not stored here — BlameOptions writes the AppSettings entries the core's
        // GitModule.Blame reads (GitModule.cs:3278-3280), so BlameView picks them up on
        // its next run with no extra plumbing.
        BlameOptions blame = BlameOptions.Load();
        _blameIgnoreWhitespace = new CheckBox { IsChecked = blame.IgnoreWhitespace };
        Localize(_blameIgnoreWhitespace, "BlameViewerSettingsPage/cbIgnoreWhitespace.Text", "Ignore whitespace");
        _blameDetectCopyInFile = new CheckBox { IsChecked = blame.DetectCopyInFile };
        Localize(
            _blameDetectCopyInFile,
            "BlameViewerSettingsPage/cbDetectMoveAndCopyInThisFile.Text",
            "Detect moved or copied lines within blamed file");
        _blameDetectCopyInAll = new CheckBox { IsChecked = blame.DetectCopyInAll };
        Localize(
            _blameDetectCopyInAll,
            "BlameViewerSettingsPage/cbDetectMoveAndCopyInAllFiles.Text",
            "Detect moved or copied lines from all files in same commit");

        Panel blamePanel = CategoryPanel(
            BlameKey, BlameText,
            null, "Extra switches handed to git blame itself (-w, -M, -C): they change "
                + "which commit a line is attributed to, so a re-indentation or a moved "
                + "block stops hiding the commit that actually wrote the line. The Blame "
                + "tab's context menu carries the same three, and re-blames at once when "
                + "one is toggled there.",
            text,
            dim,
            _blameIgnoreWhitespace,
            _blameDetectCopyInFile,
            _blameDetectCopyInAll);

        _pullAction = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach ((string _, string key, string label) in PullChoices)
        {
            ComboBoxItem item = new();
            Localize(item, key, label);
            _pullAction.Items.Add(item);
        }

        // Automatic refresh: already persisted in UiState and already consumed by
        // MainWindow (it decides whether the repository watcher follows the repo at
        // all) — it simply had no UI, so it could only be changed by editing
        // ui-state.json by hand.
        _autoRefresh = new CheckBox();
        _terminalCommand = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 360,
            Watermark = "x-terminal-emulator",
        };
        Localize(
            _autoRefresh,
            "FormBrowse/toolStripMenuItemReloadRevisions.Text",
            "Refresh automatically when the repository changes on disk");

        // Checkout's "local changes" default: already in AppPreferences and already
        // read by CheckoutBranchDialog on every checkout, but only writable through
        // that dialog's own "set as default" box.
        _checkoutLocalChanges = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach ((string _, string key, string label) in CheckoutChoices)
        {
            ComboBoxItem item = new();
            Localize(item, key, label);
            _checkoutLocalChanges.Items.Add(item);
        }

        Panel behaviourPanel = CategoryPanel(
            BehaviourKey, BehaviourText,
            null, "What the Pull command does by default, whether the app follows the "
                + "repository on disk, what the checkout dialog pre-selects when the "
                + "working tree is dirty, and which command opens a terminal.",
            text,
            dim,
            Field("GeneralSettingsPage/lblDefaultPullAction.Text", "Default pull action", _pullAction, dim),
            _autoRefresh,
            Field(
                "FormCheckoutBranch/lblLocalChanges.Text",
                "Local changes when checking out a branch",
                _checkoutLocalChanges,
                dim),
            // No upstream trans-unit: on Windows the terminal is Git bash at a known
            // path and there is no such setting to borrow an id from.
            Field(null, "Terminal command", _terminalCommand, dim,
                "The command that opens a terminal. Leave it empty to keep probing the "
                    + "known emulators (x-terminal-emulator, gnome-terminal, konsole, "
                    + "kitty, foot, xterm and a dozen more, in that order). Name a "
                    + "command to use an emulator the list cannot drive — Warp, for "
                    + "instance, answers to x-terminal-emulator but rejects the \"-e\" "
                    + "the list passes it. Two placeholders are substituted if present: "
                    + "{dir} for the directory to open in and {shell} for the shell "
                    + "picked in the Terminal drop-down; without them the directory is "
                    + "still the working directory of the new process and the emulator "
                    + "starts the login shell. A command that will not start falls back "
                    + "to the probe list."));

        // ---- Commit info: the panel's own visibility toggles, exposed here too.
        // Same store the panel writes from its context menu; saving raises
        // CommitInfoSettingsService.Changed, which every open panel listens to.
        _commitInfoChecks = new CheckBox[CommitInfoChoices.Length];
        Control[] commitInfoFields = new Control[CommitInfoChoices.Length];
        for (int i = 0; i < CommitInfoChoices.Length; i++)
        {
            CheckBox box = new();
            Localize(box, CommitInfoChoices[i].Key, CommitInfoChoices[i].Label);
            _commitInfoChecks[i] = box;
            commitInfoFields[i] = box;
        }

        Panel commitInfoPanel = CategoryPanel(
            CommitInfoKey, CommitInfoText,
            null, "Which extra sections the commit details panel shows under a commit. The "
                + "panel's own context menu carries the same toggles; changing them here "
                + "updates an open panel straight away.",
            text,
            dim,
            commitInfoFields);

        _theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };

        // "Dark" and "Light" have no upstream trans-unit and read the same in most
        // languages, so they are plain items. "System" is first because it is the
        // default: it follows the desktop's light/dark preference and keeps following
        // it, where the other two are explicit and final (see Theming/SystemTheme).
        _theme.Items.Add(new ComboBoxItem { Content = "System" });
        _theme.Items.Add(new ComboBoxItem { Content = "Dark" });
        _theme.Items.Add(new ComboBoxItem { Content = "Light" });
        _theme.SelectionChanged += (_, _) => PreviewAppearance();

        _style = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };

        // Like the theme items: no upstream trans-unit for either name.
        _style.Items.Add(new ComboBoxItem { Content = "Modern" });
        _style.Items.Add(new ComboBoxItem { Content = "Classic" });
        _style.SelectionChanged += (_, _) => PreviewAppearance();

        _uiSize = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };

        // Built from UiSizes.All so the combo cannot fall out of step with the sizes the
        // engine actually knows how to apply; the item order IS the enum order, which is
        // what makes SelectedIndex usable as the size below.
        foreach (UiSize size in UiSizes.All)
        {
            _uiSize.Items.Add(new ComboBoxItem { Content = UiSizes.Label(size) });
        }

        _uiSize.SelectionChanged += (_, _) => PreviewUiSize();

        // Where the menu sits. Merged first because it is the default. No upstream
        // trans-unit: upstream is WinForms on Windows and has no such choice.
        _titleBar = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        _titleBar.Items.Add(new ComboBoxItem { Content = "Menu in the title bar" });
        _titleBar.Items.Add(new ComboBoxItem { Content = "Separate menu bar" });
        _titleBar.SelectionChanged += (_, _) => PreviewTitleBar();

        // How many repositories a window holds. Tabs first because it is the default.
        // No upstream trans-unit either: upstream opens one repository per window and
        // has no such choice.
        _repoTabs = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        _repoTabs.Items.Add(new ComboBoxItem { Content = "Tabs" });
        _repoTabs.Items.Add(new ComboBoxItem { Content = "Single repository" });
        _repoTabs.SelectionChanged += (_, _) => PreviewRepoTabs();

        // No upstream trans-unit: upstream ships coloured bitmaps and has nothing to
        // toggle, so the caption is a literal like "Style" and "UI size".
        // ---- Fonts. The lists are the families the font manager actually reports, with
        // an empty first entry meaning "let the app choose" — which for the monospaced
        // one is a real search, not a guess (see AppFonts).
        _uiFont = FontCombo();
        _monospaceFont = FontCombo();
        _uiFontSize = NumberBox();
        _monospaceFontSize = NumberBox();

        _coloredIcons = new CheckBox { Content = "Colour the icons" };
        _coloredIcons.IsCheckedChanged += (_, _) => PreviewIconColors();

        StackPanel coloredIconsField = new() { Spacing = 4 };
        coloredIconsField.Children.Add(_coloredIcons);
        coloredIconsField.Children.Add(new TextBlock
        {
            Text = "Paints the modern style's icons by what the command does — green to "
                + "create, red to delete, blue to talk to a remote, purple for the index, "
                + "cyan for branches and submodules, amber for stashes and tags. Icons "
                + "with no such role, and the whole classic icon set, are unaffected. "
                + "Turning it off loses nothing: no icon means anything by its colour "
                + "alone.",
            Foreground = dim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        Panel appearancePanel = CategoryPanel(
            AppearanceKey, AppearanceText,
            null, "The application colour theme, its visual style — \"Modern\" for the "
                + "current vector icons and neutral palette, \"Classic\" for the earlier "
                + "look — how large the interface is drawn (\"Standard\" matches the "
                + "original Git Extensions), where the menu sits and whether one window "
                + "holds several repositories. They are all "
                + "independent, so any combination works, and all of them are applied "
                + "immediately as a preview and persisted on OK or Apply (reverted on "
                + "Cancel).",
            text,
            dim,
            Field("ColorsSettingsPage/gbTheme.Text", "Theme", _theme, dim),
            // No upstream trans-unit carries either label, so both are plain literals —
            // the same choice M80 made for "Style". Upstream has no equivalent setting
            // (its only scaling control is the high-DPI auto-scale checkbox), so there is
            // no id to borrow and no translated target to inherit.
            Field(null, "Style", _style, dim),
            // REWRITTEN in M86, because the old note became a lie. Until M84 this option
            // scaled three font resources, so the note had to warn that the grid, the diff
            // and the file lists did NOT follow it. It is now a real zoom of the whole
            // window, so they do — and what is worth warning about instead is the one cost
            // the zoom brought with it: popups are drawn inside the window so that they
            // scale with it, which means they cannot spill past its edges.
            Field(null, "UI size", _uiSize, dim,
                "Zooms the whole interface — text, icons, spacing, toolbars, the revision "
                    + "grid, the diff and the file lists together. Applied immediately, no "
                    + "restart needed. Because menus and drop-downs are drawn inside the "
                    + "window so that they scale with it, they cannot extend past its "
                    + "edges: in a small dialog they open into less room than before."),
            Field(null, "Title bar", _titleBar, dim,
                "\"Menu in the title bar\" — the default — draws the window's own title "
                    + "bar: the menu, the window caption and the minimise, maximise and "
                    + "close buttons share one row, and entries that do not fit move into "
                    + "a \"…\" that reappears as the window is widened. \"Separate menu "
                    + "bar\" keeps the desktop's title bar and puts the menu on the row "
                    + "below it. Independent of the style above — either arrangement works "
                    + "in Modern and in Classic — and applied immediately, no restart."),
            Field(null, "Repository tabs", _repoTabs, dim,
                "\"Tabs\" — the default — keeps every repository and submodule you open "
                    + "in one window, on a strip of tabs at the top of it, so switching "
                    + "between them costs a click and each one keeps its own selected "
                    + "commit. \"Single repository\" hides the strip and gives the window "
                    + "one repository at a time, as it worked before. The open tabs are "
                    + "remembered across restarts either way, and the choice is applied "
                    + "immediately, no restart."),
            coloredIconsField,
            Field(
                "AppearanceFontsSettingsPage/lblFont.Text",
                "Interface font",
                _uiFont,
                dim,
                "Applied to windows opened from now on. Leave it empty for the system "
                    + "font."),
            Field(null, "Interface font size", _uiFontSize, dim, "0 keeps the theme's own size."),
            Field(
                "AppearanceFontsSettingsPage/lblMonospaceFont.Text",
                "Fixed-width font",
                _monospaceFont,
                dim,
                "Used by the diff, the commit message editor, the console and the blame "
                    + "gutter. Empty picks the first fixed-width family installed — the "
                    + "port used to ask for \"monospace\", which is an fontconfig alias "
                    + "and not a family name, so none of those surfaces was actually "
                    + "fixed-width."),
            Field(null, "Fixed-width font size", _monospaceFontSize, dim,
                "0 keeps each surface's own size (the diff pane's zoom buttons still "
                    + "apply on top)."));

        // ---- Commit page: the message editor's seven settings. Numbers are plain text
        // boxes and not spinners: every one of them means "0 = off", and a spinner's
        // arrows invite clicking to 1, which is a limit of one character.
        _messageWordWrap = new CheckBox();
        _messageSecondLineEmpty = new CheckBox();
        _messageAutoWrap = new CheckBox();
        _messageMarkIllFormed = new CheckBox();
        Localize(_messageWordWrap, null, "Wrap long lines in the message editor");
        Localize(
            _messageSecondLineEmpty,
            "FormCommitTemplateSettings/checkBoxSecondLineEmpty.Text",
            "Second line of the message must be empty");
        Localize(
            _messageAutoWrap,
            "FormCommitTemplateSettings/checkBoxAutoWrap.Text",
            "Wrap the body automatically at the line limit");
        Localize(
            _messageMarkIllFormed,
            "GeneralSettingsPage/chkMarkIllFormedLines.Text",
            "Mark the part of a line that exceeds its limit");

        _messageFirstLineLimit = NumberBox();
        _messageLineLimit = NumberBox();
        _previousMessageCount = NumberBox();

        Panel commitPanel = CategoryPanel(
            CommitKey, CommitText,
            null, "How the commit message editor behaves: the two length limits, what it "
                + "does about them while you type, and how many earlier messages its "
                + "drop-down offers.",
            text,
            dim,
            Field(
                "FormCommitTemplateSettings/labelMaxFirstLineLength.Text",
                "Maximum length of the first line",
                _messageFirstLineLimit,
                dim,
                "0 turns the limit off. Above it, the excess is marked in the editor and "
                    + "committing asks for confirmation — it never refuses. 50 is the "
                    + "conventional subject length."),
            Field(
                "FormCommitTemplateSettings/labelMaxLineLength.Text",
                "Maximum length of the other lines",
                _messageLineLimit,
                dim,
                "0 turns the limit off. This is also the column the ruler stands at and "
                    + "the column the automatic wrap breaks at. 72 is the conventional "
                    + "body width."),
            _messageMarkIllFormed,
            _messageAutoWrap,
            _messageSecondLineEmpty,
            _messageWordWrap,
            Field(
                null,
                "Previous messages offered by the drop-down",
                _previousMessageCount,
                dim,
                "How many of the last commit messages the \"Commit message\" button "
                    + "lists. They are read from the log on each opening, so a large "
                    + "number costs a slower menu, not memory."));

        // ---- Diff viewer page.
        _eolAsGlyph = new CheckBox();
        _continuousScroll = new CheckBox();
        _omitUninteresting = new CheckBox();
        _histogramDiff = new CheckBox();
        _diffAllParents = new CheckBox();
        Localize(
            _eolAsGlyph,
            "AppearanceFontsSettingsPage/ShowEolMarkerAsGlyph.Text",
            "Show the end-of-line mark as a glyph rather than as CRLF / LF");
        Localize(
            _continuousScroll,
            "DiffViewerSettingsPage/chkAutomaticContinuousScroll.Text",
            "Scrolling past the end of a file moves to the next one");
        Localize(
            _omitUninteresting,
            "DiffViewerSettingsPage/chkOmitUninterestingDiff.Text",
            "In a merge, show only the hunks that differ from every parent");
        Localize(
            _histogramDiff,
            "DiffViewerSettingsPage/chkUseHistogramDiffAlgorithm.Text",
            "Use the histogram diff algorithm");
        Localize(
            _diffAllParents,
            "FileStatusList/tsmiShowDiffForAllParents.Text",
            "In a merge, list the changed files once per parent");

        _diffRulerPosition = NumberBox();
        _continuousScrollDelay = NumberBox();

        Panel diffPanel = CategoryPanel(
            DiffViewerKey, DiffViewerText,
            null, "What the diff pane draws and what it asks git for. The two merge "
                + "settings only ever apply to a commit with more than one parent.",
            text,
            dim,
            Field(
                "AppearanceSettingsPage/lblVerticalRulerPosition.Text",
                "Vertical ruler at column",
                _diffRulerPosition,
                dim,
                "0 draws no ruler. The column is measured in characters of the pane's "
                    + "own font."),
            _eolAsGlyph,
            _continuousScroll,
            Field(
                null,
                "Wait before moving on (ms)",
                _continuousScrollDelay,
                dim,
                "How long the patch has to sit at its end before another scroll moves "
                    + "to the next file. The wait is what stops a single flick of the "
                    + "wheel from skipping a file unseen."),
            _diffAllParents,
            _omitUninteresting,
            _histogramDiff);

        // ---- Revision graph page.
        _nonRelativesTextGray = new CheckBox();
        _alternateRowColor = new CheckBox();
        _multicolorBranches = new CheckBox();
        _colorPerBranch = new CheckBox();
        _colorAtRemoteMirror = new CheckBox();
        _straightenDiagonals = new CheckBox();
        _highlightAuthored = new CheckBox();
        _gridTooltips = new CheckBox();
        Localize(
            _nonRelativesTextGray,
            "DetailedSettingsPage/chkDrawNonRelativesTextGray.Text",
            "Also grey the TEXT of a non-relative revision");
        Localize(
            _alternateRowColor,
            "DetailedSettingsPage/chkDrawAlternateBackColor.Text",
            "Give every other row a slightly different background");
        Localize(
            _multicolorBranches,
            "DetailedSettingsPage/chkMulticolorBranches.Text",
            "Colour each branch of the graph differently");
        Localize(
            _colorPerBranch,
            null,
            "…and start a new colour at every branch name");
        Localize(
            _colorAtRemoteMirror,
            null,
            "…including at origin/X when the local X exists, splitting pushed from unpushed");

        // Each depends on the one above it, and the checkbox says so by going grey
        // rather than by silently doing nothing.
        _multicolorBranches.IsCheckedChanged += (_, _) => UpdateGraphColorGates();
        _colorPerBranch.IsCheckedChanged += (_, _) => UpdateGraphColorGates();

        Localize(
            _straightenDiagonals,
            "DetailedSettingsPage/chkStraightenGraphDiagonals.Text",
            "Straighten the diagonals of the graph");
        Localize(
            _highlightAuthored,
            "DetailedSettingsPage/chkHighlightAuthored.Text",
            "Highlight the revisions of the selected revision's author");
        Localize(
            _gridTooltips,
            "DetailedSettingsPage/chkShowRevisionGridTooltips.Text",
            "Show a tooltip on a revision row");

        _straightenLimit = NumberBox();

        Panel graphPanel = CategoryPanel(
            GraphKey, GraphText,
            null, "How the revision grid draws its rows and its DAG. Greying the lanes "
                + "of a non-relative revision stays where it is — the View menu — "
                + "because it is a per-session view, not a preference.",
            text,
            dim,
            _multicolorBranches,
            Indented(_colorPerBranch),
            Indented(_colorAtRemoteMirror, depth: 2),
            _alternateRowColor,
            _highlightAuthored,
            _nonRelativesTextGray,
            _gridTooltips,
            _straightenDiagonals,
            Field(
                null,
                "Skip straightening rows wider than",
                _straightenLimit,
                dim,
                "In segments. The tidy-up costs the square of a row's width, and a row "
                    + "that wide is unreadable either way, so past this many segments "
                    + "the row is left as it is."));

        // ---- Stash / checkout / push page.
        _untrackedManualStash = new CheckBox();
        _untrackedAutoStash = new CheckBox();
        _rebaseAutoStash = new CheckBox();
        Localize(
            _untrackedManualStash,
            "GeneralSettingsPage/chkIncludeUntrackedFilesInManualStash.Text",
            "A stash you ask for also takes the untracked files");
        Localize(
            _untrackedAutoStash,
            "GeneralSettingsPage/chkIncludeUntrackedFilesInAutoStash.Text",
            "A stash made for you before a checkout also takes the untracked files");
        Localize(
            _rebaseAutoStash,
            "FormRebase/chkAutostash.Text",
            "Rebase with --autostash, so a dirty working tree does not stop it");

        _popAfterCheckout = AskAlwaysNeverCombo();
        _popAfterPull = AskAlwaysNeverCombo();

        _recursiveSubmodules = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach (string label in new[] { "None", "Check", "On demand" })
        {
            _recursiveSubmodules.Items.Add(new ComboBoxItem { Content = label });
        }

        Panel stashPanel = CategoryPanel(
            StashKey, StashText,
            null, "What goes into a stash, what happens to it afterwards, and what push "
                + "does about submodules.",
            text,
            dim,
            _untrackedManualStash,
            _untrackedAutoStash,
            Field(
                null,
                "Re-apply the stash after a checkout that made one",
                _popAfterCheckout,
                dim),
            Field(
                null,
                "Re-apply the stash after a pull you stashed for",
                _popAfterPull,
                dim),
            _rebaseAutoStash,
            Field(
                "FormPush/label2.Text",
                "Recursive submodules",
                _recursiveSubmodules,
                dim,
                "What push does when a commit references a submodule commit that is not "
                    + "pushed. \"Check\" refuses the push; \"On demand\" pushes the "
                    + "submodule first; \"None\" says nothing about it. This is the "
                    + "starting value of the same drop-down in the Push dialog."));

        // ---- Dashboard / paths page.
        _recentHistorySize = NumberBox();
        _sortRecentRepos = new CheckBox();
        Localize(
            _sortRecentRepos,
            "AppearanceSettingsPage/chkSortRecentRepos.Text",
            "List the recent repositories alphabetically instead of by last use");

        _shorteningStrategy = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach (string label in new[] { "Full path", "Repository folder only", "Middle elided" })
        {
            _shorteningStrategy.Items.Add(new ComboBoxItem { Content = label });
        }

        _truncatePathMethod = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach (string label in new[] { "Full path", "Trim the start", "File name only" })
        {
            _truncatePathMethod.Items.Add(new ComboBoxItem { Content = label });
        }

        Panel dashboardPanel = CategoryPanel(
            DashboardKey, DashboardText,
            null, "The list of recent repositories, and how a path is shortened when it "
                + "does not fit.",
            text,
            dim,
            Field(
                "AppearanceSettingsPage/lblRecentRepositoriesHistorySize.Text",
                "Repositories kept in the recent list",
                _recentHistorySize,
                dim,
                "Older entries drop off when the list is next written. This is the same "
                    + "number the rest of Git Extensions uses, so it is stored with the "
                    + "history itself rather than with these settings."),
            _sortRecentRepos,
            Field(null, "Path shown under a recent repository", _shorteningStrategy, dim),
            Field(
                "AppearanceSettingsPage/lblTruncateLongFilenames.Text",
                "Path shown in the changed-file list",
                _truncatePathMethod,
                dim,
                "Only applies where the whole path is shown; inside a folder group the "
                    + "directory is already in the header. Upstream's fourth choice, "
                    + "\"Compact\", is a Windows API and is not offered here — its own "
                    + "code falls back to the full path off Windows."));

        Panel scriptsPanel = BuildScriptsPage(text, dim);

        Panel githubPanel = BuildGitHubPage(text, dim);

        Panel hotkeysPanel = BuildHotkeysPage(text, dim);

        // Category order — the left list is built from the same sequence below, so the
        // two cannot fall out of step.
        _pages.Add(identityPanel);
        _pages.Add(gitConfigPanel);
        _pages.Add(blamePanel);
        _pages.Add(commitInfoPanel);
        _pages.Add(hotkeysPanel);
        _pages.Add(behaviourPanel);
        _pages.Add(commitPanel);
        _pages.Add(diffPanel);
        _pages.Add(graphPanel);
        _pages.Add(stashPanel);
        _pages.Add(dashboardPanel);
        _pages.Add(scriptsPanel);
        _pages.Add(githubPanel);
        _pages.Add(appearancePanel);

        Grid rightPane = new();
        foreach (Panel page in _pages)
        {
            rightPane.Children.Add(page);
        }

        // A translated page can be taller than the dialog; scroll rather than clip.
        // The level header scrolls with the pages: eight wrapped checkbox captions are
        // already taller than the dialog, and a pinned header would eat the room they
        // need.
        StackPanel rightStack = new();
        rightStack.Children.Add(_levelHeader);
        rightStack.Children.Add(rightPane);

        ScrollViewer rightScroll = new()
        {
            Content = rightStack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // ---- Left category list -------------------------------------------
        // Auto width with a floor: a longer translated category name widens the
        // column instead of being trimmed.
        ListBox categories = new()
        {
            Background = panel,
            BorderThickness = new Thickness(0),
            MinWidth = 170,
            MaxWidth = 280,
        };
        categories.Items.Add(CategoryItem(IdentityKey, IdentityText));
        categories.Items.Add(CategoryItem(GitConfigKey, GitConfigText));
        categories.Items.Add(CategoryItem(BlameKey, BlameText));
        categories.Items.Add(CategoryItem(CommitInfoKey, CommitInfoText));
        categories.Items.Add(CategoryItem(HotkeysKey, HotkeysText));
        categories.Items.Add(CategoryItem(BehaviourKey, BehaviourText));
        categories.Items.Add(CategoryItem(CommitKey, CommitText));
        categories.Items.Add(CategoryItem(DiffViewerKey, DiffViewerText));
        categories.Items.Add(CategoryItem(GraphKey, GraphText));
        categories.Items.Add(CategoryItem(StashKey, StashText));
        categories.Items.Add(CategoryItem(DashboardKey, DashboardText));
        categories.Items.Add(CategoryItem(ScriptsKey, ScriptsText));
        categories.Items.Add(CategoryItem(GitHubKey, GitHubText));
        categories.Items.Add(CategoryItem(AppearanceKey, AppearanceText));
        categories.SelectionChanged += (_, _) =>
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].IsVisible = categories.SelectedIndex == i;
            }

            // The level selector only means something on the two git-config pages.
            _levelHeader.IsVisible = categories.SelectedIndex is 0 or 1;
        };
        categories.SelectedIndex = 0;

        Border categoryBox = new()
        {
            Background = panel,
            BorderBrush = border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = categories,
        };

        // ---- OK / Cancel / Apply -------------------------------------------
        Button ok = new() { IsDefault = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        Button cancel = new() { IsCancel = true, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        Button apply = new() { MinWidth = 84 };
        Localize(ok, "FormSettings/buttonOk.Text", "OK");
        Localize(cancel, "FormSettings/buttonCancel.Text", "Cancel");
        Localize(apply, "FormSettings/buttonApply.Text", "Apply");

        ok.Click += (_, _) => { ApplyAndSave(); Close(); };
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => ApplyAndSave();

        // WrapPanel, not StackPanel: with long translations and a narrow window the
        // buttons move to a second row instead of overflowing the frame.
        WrapPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 10, 16, 12),
            ItemSpacing = 0,
            LineSpacing = 6,
            Children = { ok, cancel, apply },
        };

        Border buttonBar = new()
        {
            Background = window,
            BorderBrush = border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = buttons,
        };

        Grid body = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Background = window,
        };
        Grid.SetColumn(categoryBox, 0);
        Grid.SetColumn(rightScroll, 1);
        body.Children.Add(categoryBox);
        body.Children.Add(rightScroll);

        DockPanel root = new() { Background = window };
        DockPanel.SetDock(buttonBar, Dock.Bottom);
        root.Children.Add(buttonBar);
        root.Children.Add(body);
        Content = root;
        DialogKeys.EnsureFocusRoute(this);

        // Load current values.
        (_revertTheme, _revertStyle) = LoadValues();

        ApplyTitle();
        TranslationService.LanguageChanged += OnLanguageChanged;

        // Revert a live appearance preview if the window is closed without applying.
        // Theme and style go back in one call, so Cancel undoes both dimensions.
        Closing += (_, _) =>
        {
            TranslationService.LanguageChanged -= OnLanguageChanged;
            if (!_applied)
            {
                SystemTheme.Follow(_revertTheme == SystemTheme.Name);
                ThemeManager.Apply(
                    SystemTheme.VariantOf(_revertTheme),
                    _revertStyle == "Classic" ? AppStyle.Classic : AppStyle.Modern);
                UiScaling.Apply(_revertUiSize);
                ThemeManager.SetColoredIcons(_revertColoredIcons);
                WindowChrome.Apply(_revertMergedTitleBar);
                RepoTabsOption.Apply(_revertRepoTabs);
            }
        };
    }

    /// <summary>
    ///  Shows the Settings dialog modally over <paramref name="owner"/>.
    ///
    ///  <para><paramref name="currentPullAction"/> is the default pull action the host
    ///  is using right now (the name of a <c>GitPullAction</c>); pass it so the combo
    ///  shows the live value rather than the last one written to disk.
    ///  <paramref name="pullActionChanged"/> is invoked on the UI thread with the newly
    ///  chosen action whenever the user applies — the host must use it to update its
    ///  own <see cref="UiState"/> instance (and the toolbar), because that instance is
    ///  re-serialised in full when the window closes.</para>
    ///
    ///  <para><b>Host wiring (MainWindow.OpenSettingsAsync).</b> Four optional arguments
    ///  exist only so the host can keep its own state in step; each is safe to omit, at
    ///  the cost of the change taking effect only at the next start:
    ///  <list type="bullet">
    ///   <item><paramref name="blameOptionsChanged"/> → call
    ///    <c>BlameView.ReloadBlameOptions()</c> on the blame tab.</item>
    ///   <item><paramref name="currentAutoRefresh"/> / <paramref name="autoRefreshChanged"/>
    ///    → pass <c>_uiState.AutoRefresh</c>, and in the callback set it and re-point the
    ///    watcher: <c>_watcher.Watch(on ? _repoPath : null)</c>.</item>
    ///   <item><paramref name="hotkeys"/> → pass the live <c>HotkeyService</c>, and
    ///    subscribe once to its <c>Changed</c> event to re-label the toolbar and menu
    ///    (they print the gestures in their captions).</item>
    ///  </list></para>
    /// </summary>
    public static Task ShowAsync(
        Window owner,
        string? repoPath,
        string? currentPullAction = null,
        Action<string>? pullActionChanged = null,
        Action? blameOptionsChanged = null,
        bool? currentAutoRefresh = null,
        Action<bool>? autoRefreshChanged = null,
        HotkeyService? hotkeys = null)
        => new SettingsWindow(
                repoPath,
                currentPullAction,
                pullActionChanged,
                blameOptionsChanged,
                currentAutoRefresh,
                autoRefreshChanged,
                hotkeys)
            .ShowDialog(owner);

    // ---- translation -------------------------------------------------------

    // The event is raised on whichever thread finished parsing the catalogue.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        ApplyTitle();
        foreach (Action relabel in _relabel)
        {
            relabel();
        }
    }

    private void ApplyTitle() => Title = TranslationService.T("FormSettings/$this.Text", "Settings");

    // Registers a caption so Retranslate can re-apply it later, and applies it now.
    private void Localize(TextBlock block, string? key, string english)
        => Register(() => block.Text = TranslationService.T(key, english));

    // The suffix is appended untranslated — it carries the raw git config key, which
    // upstream also shows verbatim next to each caption
    // (GitConfigAdvancedSettingsPage_Load).
    private void Localize(ContentControl control, string? key, string english, string suffix = "")
        => Register(() => control.Content = TranslationService.T(key, english) + suffix);

    private void LocalizeWatermark(TextBox box, string? key, string english)
        => Register(() => box.Watermark = TranslationService.T(key, english));

    private void Register(Action apply)
    {
        apply();
        _relabel.Add(apply);
    }

    private ListBoxItem CategoryItem(string? key, string english)
    {
        ListBoxItem item = new();
        Localize(item, key, english);
        return item;
    }

    // ---- values ------------------------------------------------------------

    // Reads git identity, pull action, theme and style into the controls; returns the
    // theme/style pair that was active on open (for the Cancel revert).
    private (string Theme, string Style) LoadValues()
    {
        // With no repository open, "Local" has nothing to point at: preselect Global
        // and take Local off the table rather than showing a level that cannot work.
        if (_repoPath is null)
        {
            _settingsLevel.SelectedIndex = 1;
            ((ComboBoxItem)_settingsLevel.Items[0]!).IsEnabled = false;
        }

        LoadGitConfig(SelectedLevel);

        // Default pull action: read from the value the host is actually using, or
        // from UiState if the host did not pass one.
        string action = _currentPullAction ?? _uiStateService.Load().DefaultPullAction;
        int pullIndex = Array.FindIndex(PullChoices, c => c.Token == action);
        _pullAction.SelectedIndex = pullIndex >= 0 ? pullIndex : 0;

        // Automatic refresh: the host's live value when it passed one, since its
        // in-memory UiState is the instance that will be saved at exit.
        UiState ui = _uiStateService.Load();
        _autoRefresh.IsChecked = _currentAutoRefresh ?? ui.AutoRefresh;
        _terminalCommand.Text = ui.TerminalCommand;

        // Checkout default and the commit-editor settings: their own file, so the file
        // is always the truth. One Load() for both — they live in the same record.
        AppPreferences prefs = new SettingsService().Load();
        int checkoutIndex = Array.FindIndex(
            CheckoutChoices, c => c.Token == prefs.DefaultCheckoutLocalChangesAction);
        _checkoutLocalChanges.SelectedIndex = checkoutIndex >= 0 ? checkoutIndex : 0;

        _messageWordWrap.IsChecked = prefs.CommitMessageWordWrap;
        _messageSecondLineEmpty.IsChecked = prefs.CommitValidationSecondLineMustBeEmpty;
        _messageAutoWrap.IsChecked = prefs.CommitValidationAutoWrap;
        _messageMarkIllFormed.IsChecked = prefs.MarkIllFormedCommitLines;
        _messageFirstLineLimit.Text = prefs.CommitValidationFirstLineMaxChars.ToString(CultureInfo.InvariantCulture);
        _messageLineLimit.Text = prefs.CommitValidationMaxCharsPerLine.ToString(CultureInfo.InvariantCulture);
        _previousMessageCount.Text =
            prefs.CommitDialogNumberOfPreviousMessages.ToString(CultureInfo.InvariantCulture);

        _diffRulerPosition.Text = prefs.DiffVerticalRulerPosition.ToString(CultureInfo.InvariantCulture);
        _eolAsGlyph.IsChecked = prefs.ShowEolMarkerAsGlyph;
        _continuousScroll.IsChecked = prefs.DiffContinuousScroll;
        _continuousScrollDelay.Text = prefs.DiffContinuousScrollDelay.ToString(CultureInfo.InvariantCulture);
        _omitUninteresting.IsChecked = prefs.OmitUninterestingDiff;
        _histogramDiff.IsChecked = prefs.UseHistogramDiffAlgorithm;
        _diffAllParents.IsChecked = prefs.ShowDiffForAllParents;

        _nonRelativesTextGray.IsChecked = prefs.GraphDrawNonRelativesTextGray;
        _alternateRowColor.IsChecked = prefs.GraphDrawAlternateBackColor;
        _multicolorBranches.IsChecked = prefs.MulticolorBranches;
        _colorPerBranch.IsChecked = prefs.GraphColorPerBranch;
        _colorAtRemoteMirror.IsChecked = prefs.GraphColorAtRemoteMirror;
        UpdateGraphColorGates();
        _straightenDiagonals.IsChecked = prefs.StraightenGraphDiagonals;
        _straightenLimit.Text = prefs.StraightenGraphSegmentsLimit.ToString(CultureInfo.InvariantCulture);
        _highlightAuthored.IsChecked = prefs.HighlightAuthoredRevisions;
        _gridTooltips.IsChecked = prefs.ShowRevisionGridTooltips;

        _untrackedManualStash.IsChecked = prefs.IncludeUntrackedFilesInManualStash;
        _untrackedAutoStash.IsChecked = prefs.IncludeUntrackedFilesInAutoStash;
        _popAfterCheckout.SelectedIndex = TokenIndex(SettingsService.AskAlwaysNever, prefs.AutoPopStashAfterCheckout);
        _popAfterPull.SelectedIndex = TokenIndex(SettingsService.AskAlwaysNever, prefs.AutoPopStashAfterPull);
        _rebaseAutoStash.IsChecked = prefs.RebaseAutoStash;
        _recursiveSubmodules.SelectedIndex = prefs.RecursiveSubmodules;

        // The size comes from the CORE setting, which is the one that trims the list;
        // app-settings.json only carries what the dialog last wrote.
        _recentHistorySize.Text =
            GitCommands.AppSettings.RecentRepositoriesHistorySize.ToString(CultureInfo.InvariantCulture);
        _sortRecentRepos.IsChecked = prefs.SortRecentRepos;
        _shorteningStrategy.SelectedIndex = TokenIndex(
            SettingsService.ShorteningStrategies, prefs.ShorteningRecentRepoPathStrategy);
        _truncatePathMethod.SelectedIndex = TokenIndex(
            SettingsService.TruncateMethods, prefs.TruncatePathMethod);

        _githubHost.Text = prefs.GitHubHost;
        _githubIssueMessages.IsChecked = prefs.GitHubIssueCommitMessages;
        _githubIssueCount.Text = prefs.GitHubIssueCommitMessageCount.ToString(CultureInfo.InvariantCulture);

        // The box stays empty on purpose; only the note says whether a token exists.
        _githubToken.Text = string.Empty;
        RefreshGitHubTokenNote();

        // Scripts: their own file, edited in place and written on OK/Apply.
        _scripts.Clear();
        _scripts.AddRange(new UserScriptService().Load());
        RebuildScriptList(0);

        SelectFont(_uiFont, prefs.UiFontFamily);
        SelectFont(_monospaceFont, prefs.MonospaceFontFamily);
        _uiFontSize.Text = prefs.UiFontSize.ToString(CultureInfo.InvariantCulture);
        _monospaceFontSize.Text = prefs.MonospaceFontSize.ToString(CultureInfo.InvariantCulture);

        // Commit-info toggles: likewise their own file.
        CommitInfoSettings commitInfo = _commitInfoService.Load();
        bool[] commitInfoValues =
        [
            commitInfo.ShowContainedInBranchesLocal,
            commitInfo.ShowContainedInBranchesRemote,
            commitInfo.ShowContainedInBranchesRemoteIfNoLocal,
            commitInfo.ShowContainedInTags,
            commitInfo.ShowAnnotatedTagsMessages,
            commitInfo.ShowTagThisCommitDerivesFrom,
        ];
        for (int i = 0; i < _commitInfoChecks.Length; i++)
        {
            _commitInfoChecks[i].IsChecked = commitInfoValues[i];
        }

        // Theme and style: the pair the dialog previews from, and the pair Cancel
        // restores to.
        _theme.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeTokens, ui.Theme));
        _style.SelectedIndex = ui.Style == "Classic" ? 1 : 0;

        // The size the dialog previews from, and the one Cancel returns to. Read from
        // the live engine rather than from the file: the host applied it at startup, and
        // it is the engine that says what is on screen right now.
        _revertUiSize = UiScaling.CurrentSize;
        _uiSize.SelectedIndex = Array.IndexOf(UiSizes.All, _revertUiSize);

        // Read from the live engine for the same reason as the size: the host applied
        // it at startup and it is what the icons on screen are drawn with right now.
        _revertColoredIcons = ThemeManager.ColoredIcons;
        _coloredIcons.IsChecked = _revertColoredIcons;

        // Read from the live holder for the same reason again: it is the arrangement the
        // window is wearing right now, which the file may not be if this dialog was
        // opened, previewed and cancelled once already.
        _revertMergedTitleBar = WindowChrome.Merged;
        _titleBar.SelectedIndex = _revertMergedTitleBar ? 0 : 1;

        // Read from the live holder for the same reason as the title bar: it is the
        // arrangement the window is wearing right now, which the file may not be.
        _revertRepoTabs = RepoTabsOption.Enabled;
        _repoTabs.SelectedIndex = _revertRepoTabs ? 0 : 1;
        return (ui.Theme, ui.Style);
    }

    private GitSettingLevel SelectedLevel
        => LevelChoices[Math.Clamp(_settingsLevel.SelectedIndex, 0, LevelChoices.Length - 1)].Level;

    private void OnLevelChanged()
    {
        GitSettingLevel level = SelectedLevel;
        if (level != _loadedLevel)
        {
            LoadGitConfig(level);
        }
    }

    // Reads everything this dialog keeps in git config — user.name / user.email and the
    // eight advanced keys — at the given level. Running git config is blocking, so it
    // happens off the UI thread, through a SINGLE store: GitConfigSettings caches one
    // "git config --list" pass, so ten reads cost one git invocation. The controls are
    // blanked meanwhile so the previous level's values are never mistaken for this
    // one's, and applying is suppressed until the read lands.
    private void LoadGitConfig(GitSettingLevel level)
    {
        _loadedLevel = level;
        _loadingIdentity = true;
        _loadingGitConfig = true;
        _userName.Text = string.Empty;
        _userEmail.Text = string.Empty;
        foreach (CheckBox box in _gitConfigChecks)
        {
            box.IsChecked = null;
        }

        _ = Task.Run(() =>
        {
            string name = string.Empty;
            string email = string.Empty;
            bool?[] flags = new bool?[GitConfigChoices.Length];
            try
            {
                IConfigValueStore store = ConfigStore(level);
                name = store.GetValue("user.name") ?? string.Empty;
                email = store.GetValue("user.email") ?? string.Empty;
                for (int i = 0; i < GitConfigChoices.Length; i++)
                {
                    flags[i] = ToTriState(store.GetValue(GitConfigChoices[i].Key));
                }
            }
            catch (Exception)
            {
                // An unreadable config leaves the fields empty; applying then writes
                // the level from scratch, which is the right outcome anyway.
            }

            Dispatcher.UIThread.Post(() =>
            {
                // A third level change may have started while this one was reading.
                if (_loadedLevel != level)
                {
                    return;
                }

                _userName.Text = name;
                _userEmail.Text = email;
                for (int i = 0; i < _gitConfigChecks.Length; i++)
                {
                    _gitConfigChecks[i].IsChecked = flags[i];
                }

                _loadingIdentity = false;
                _loadingGitConfig = false;
            });
        });
    }

    // git's boolean spellings (git-config(1) "bool"). A missing key — and any value that
    // is not a boolean, e.g. pull.rebase=interactive — reads as indeterminate, and
    // leaving such a box alone leaves the value alone. Upstream's SettingsToPage maps
    // the same way.
    private static bool? ToTriState(string? value)
        => value switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" or "" => false,
            _ => null,
        };

    // A read/write view of one git config level. GitConfigSettings runs
    // "git config --local|--global", i.e. exactly what upstream's settings pages use
    // under their level radio buttons. Blocking: never call on the UI thread.
    private GitConfigSettings ConfigStore(GitSettingLevel level)
    {
        // The global config is reachable without a repository; any existing directory
        // is a valid place to run git from.
        string repo = _repoPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new GitConfigSettings(GitContext.CreateModule(repo).GitExecutable, level);
    }

    // Applies the appearance preview live as either combo changes. Both dimensions
    // are read from the combos and passed together, so changing one never resets the
    // other — the two are orthogonal and all four combinations are reachable.
    private void PreviewAppearance()
    {
        // The preview arms the following too, so picking "System" and then changing the
        // desktop's preference behaves exactly as it will once applied — and picking
        // Dark or Light disarms it, which is what makes the preview honest.
        SystemTheme.Follow(SelectedTheme == SystemTheme.Name);
        ThemeManager.Apply(SelectedVariant, SelectedStyle);
    }

    // Live, like the theme and the style: the size is a theme resource read through a
    // dynamic reference, so every open window re-reads it — this dialog included, which
    // means the preview is also the thing being previewed.
    private void PreviewUiSize() => UiScaling.Apply(SelectedUiSize);

    // Live as well, and cheaper than either: every glyph on screen listens to
    // ThemeManager.StyleChanged and repaints itself, so nothing is rebuilt.
    private void PreviewIconColors() => ThemeManager.SetColoredIcons(_coloredIcons.IsChecked == true);

    // Live as well: the main window listens to WindowChrome.Changed and re-lays its own
    // frame, so the arrangement can be tried on and cancelled like the rest of this page.
    private void PreviewTitleBar() => WindowChrome.Apply(SelectedMergedTitleBar);

    private bool SelectedMergedTitleBar => _titleBar.SelectedIndex != 1;

    // Live as well: the main window listens to RepoTabsOption.Changed and shows or hides
    // its strip, so the arrangement can be tried on and cancelled like the rest of this
    // page. Nothing is closed when the strip is hidden — the tabs are still there.
    private void PreviewRepoTabs() => RepoTabsOption.Apply(SelectedRepoTabs);

    private bool SelectedRepoTabs => _repoTabs.SelectedIndex != 1;

    private UiSize SelectedUiSize
        => UiSizes.All[Math.Max(0, _uiSize.SelectedIndex)];

    // The stored UiState.Theme values, in the order the combo lists them. One table for
    // both directions: the load reads an index out of it, the apply reads the token
    // back, so the two can never drift.
    private static readonly string[] ThemeTokens = [SystemTheme.Name, "Dark", "Light"];

    private string SelectedTheme => ThemeTokens[Math.Max(0, _theme.SelectedIndex)];

    private ThemeVariant SelectedVariant => SystemTheme.VariantOf(SelectedTheme);

    private AppStyle SelectedStyle
        => _style.SelectedIndex == 1 ? AppStyle.Classic : AppStyle.Modern;

    private void ApplyAndSave()
    {
        // ---- Git identity: write user.name / user.email at the chosen level.
        // Skipped while a level change is still loading: the fields are blank then and
        // applying would delete the entries the user never saw.
        GitSettingLevel level = SelectedLevel;
        if (!_loadingIdentity && !_loadingGitConfig && (level == GitSettingLevel.Global || _repoPath is not null))
        {
            string? name = _userName.Text?.Trim();
            string? email = _userEmail.Text?.Trim();

            // Snapshot the tri-states on the UI thread; the write runs off it.
            bool?[] flags = Array.ConvertAll(_gitConfigChecks, box => box.IsChecked);

            // git config is blocking; the dialog must not freeze on a slow repository.
            // Identity and the advanced keys share one store and one Save(), so the two
            // pages can never race each other over the same config file.
            _ = Task.Run(() =>
            {
                try
                {
                    IPersistentConfigValueStore store = ConfigStore(level);

                    // SetValue(null) removes the entry, so an emptied field unsets it
                    // at this level exactly as before — only now at the level the user
                    // picked instead of always --local.
                    store.SetValue("user.name", string.IsNullOrEmpty(name) ? null : name);
                    store.SetValue("user.email", string.IsNullOrEmpty(email) ? null : email);

                    // The tri-state contract: an indeterminate box passes null, which
                    // GitConfigSettings.Save turns into "git config --unset" — writing
                    // "false" instead would be a different thing entirely, pinning the
                    // key against whatever a wider level says.
                    for (int i = 0; i < flags.Length; i++)
                    {
                        store.SetValue(
                            GitConfigChoices[i].Key,
                            flags[i] switch { true => "true", false => "false", _ => null });
                    }

                    store.Save();
                }
                catch (Exception)
                {
                    // Best-effort; a git failure must not lose the other settings.
                }
            });
        }

        // ---- Blame switches: written into the AppSettings entries the core's blame
        // command line is built from. Persisting may touch disk, so it runs off the UI
        // thread; the host is told afterwards so an open Blame tab can re-blame.
        BlameOptions blame = new(
            IgnoreWhitespace: _blameIgnoreWhitespace.IsChecked == true,
            DetectCopyInFile: _blameDetectCopyInFile.IsChecked == true,
            DetectCopyInAll: _blameDetectCopyInAll.IsChecked == true);
        _ = Task.Run(() =>
        {
            blame.Apply();
            Dispatcher.UIThread.Post(() => _blameOptionsChanged?.Invoke());
        });

        // ---- Hotkeys: one atomic apply, which re-indexes the gesture lookup, writes
        // hotkeys.json and raises HotkeyService.Changed so the toolbar and menu can
        // re-print the gestures in their captions. Cheap and local — no git, no reason
        // to leave the UI thread.
        StopCapture();
        _hotkeys.ApplyBindings(_hotkeyDraft
            .Where(p => p.Key.Scope == HotkeyScope.Browse)
            .ToDictionary(p => Enum.Parse<BrowseCommand>(p.Key.Command), p => p.Value));

        foreach (HotkeyScope scope in HotkeyScopes.All.Keys)
        {
            _hotkeys.ApplyScopeBindings(
                scope,
                _hotkeyDraft
                    .Where(p => p.Key.Scope == scope)
                    .ToDictionary(p => p.Key.Command, p => p.Value));
        }

        // ---- Checkout default and commit-info toggles: files of their own, no
        // last-writer-wins hazard with the host's UiState. Saving the commit-info file
        // raises CommitInfoSettingsService.Changed, which is what makes an open commit
        // details panel adopt the change instead of overwriting it later.
        string checkoutAction = CheckoutChoices[Math.Max(0, _checkoutLocalChanges.SelectedIndex)].Token;
        bool[] commitInfo = Array.ConvertAll(_commitInfoChecks, box => box.IsChecked == true);

        // Snapshot the commit-editor fields HERE, on the UI thread: the save below runs
        // off it and must not touch a control.
        bool wordWrap = _messageWordWrap.IsChecked == true;
        bool secondLineEmpty = _messageSecondLineEmpty.IsChecked == true;
        bool autoWrap = _messageAutoWrap.IsChecked == true;
        bool markIllFormed = _messageMarkIllFormed.IsChecked == true;
        int firstLineLimit = Number(_messageFirstLineLimit, 0, 999);
        int lineLimit = Number(_messageLineLimit, 0, 999);
        int previousMessages = Number(_previousMessageCount, 6, 50);
        int rulerPosition = Number(_diffRulerPosition, 0, 999);
        bool eolGlyph = _eolAsGlyph.IsChecked == true;
        bool continuous = _continuousScroll.IsChecked == true;
        int continuousDelay = Number(_continuousScrollDelay, 600, 10_000);
        bool omitUninteresting = _omitUninteresting.IsChecked == true;
        bool histogram = _histogramDiff.IsChecked == true;
        bool allParents = _diffAllParents.IsChecked == true;
        bool textGray = _nonRelativesTextGray.IsChecked == true;
        bool alternateRows = _alternateRowColor.IsChecked == true;
        bool multicolor = _multicolorBranches.IsChecked == true;
        bool colorPerBranch = _colorPerBranch.IsChecked == true;
        bool colorAtRemoteMirror = _colorAtRemoteMirror.IsChecked == true;
        bool straighten = _straightenDiagonals.IsChecked == true;
        int straightenLimit = Number(_straightenLimit, 80, 10_000);
        bool highlightAuthored = _highlightAuthored.IsChecked == true;
        bool gridTooltips = _gridTooltips.IsChecked == true;
        bool untrackedManual = _untrackedManualStash.IsChecked == true;
        bool untrackedAuto = _untrackedAutoStash.IsChecked == true;
        string popAfterCheckout = SettingsService.AskAlwaysNever[Math.Max(0, _popAfterCheckout.SelectedIndex)];
        string popAfterPull = SettingsService.AskAlwaysNever[Math.Max(0, _popAfterPull.SelectedIndex)];
        bool rebaseAutoStash = _rebaseAutoStash.IsChecked == true;
        int recursiveSubmodules = Math.Max(0, _recursiveSubmodules.SelectedIndex);
        int historySize = Number(_recentHistorySize, 30, 500);
        bool sortRecent = _sortRecentRepos.IsChecked == true;
        string shortening = SettingsService.ShorteningStrategies[Math.Max(0, _shorteningStrategy.SelectedIndex)];
        string truncate = SettingsService.TruncateMethods[Math.Max(0, _truncatePathMethod.SelectedIndex)];
        string uiFont = SelectedFont(_uiFont);
        string monospaceFont = SelectedFont(_monospaceFont);
        int uiFontSize = Number(_uiFontSize, 0, 40);
        int monospaceFontSize = Number(_monospaceFontSize, 0, 40);

        string githubHost = (_githubHost.Text ?? string.Empty).Trim();
        bool githubIssues = _githubIssueMessages.IsChecked == true;
        int githubIssueCount = Number(_githubIssueCount, 10, 100);

        // The token is stored HERE, not in the block below: it goes to the credential
        // helper rather than into AppPreferences, and it must be written before the note
        // is refreshed. Emptying the box is not "erase" — that is the Forget button —
        // so an Apply the user did not mean cannot cost them their token.
        string githubToken = (_githubToken.Text ?? string.Empty).Trim();
        if (githubToken.Length > 0)
        {
            GitHubTokenStore.Storage where = GitHubTokenStore.Save(
                new GitHubService(new AppPreferences { GitHubHost = githubHost }).ApiHost, githubToken);
            GitHubService.ForgetLogin();
            _githubToken.Text = string.Empty;
            _githubTokenNote.Text = GitHubTokenStore.Describe(
                new GitHubService(new AppPreferences { GitHubHost = githubHost }).ApiHost, where);
        }

        // Copied out on the UI thread: the save below runs off it, and _scripts keeps
        // being edited as long as this dialog is open (Apply does not close it).
        List<UserScript> scripts = [.. _scripts];
        _ = Task.Run(() =>
        {
            SettingsService settings = new();
            AppPreferences prefs = settings.Load();
            prefs.DefaultCheckoutLocalChangesAction = checkoutAction;
            prefs.CommitMessageWordWrap = wordWrap;
            prefs.CommitValidationSecondLineMustBeEmpty = secondLineEmpty;
            prefs.CommitValidationAutoWrap = autoWrap;
            prefs.MarkIllFormedCommitLines = markIllFormed;
            prefs.CommitValidationFirstLineMaxChars = firstLineLimit;
            prefs.CommitValidationMaxCharsPerLine = lineLimit;
            prefs.CommitDialogNumberOfPreviousMessages = previousMessages;
            prefs.DiffVerticalRulerPosition = rulerPosition;
            prefs.ShowEolMarkerAsGlyph = eolGlyph;
            prefs.DiffContinuousScroll = continuous;
            prefs.DiffContinuousScrollDelay = continuousDelay;
            prefs.OmitUninterestingDiff = omitUninteresting;
            prefs.UseHistogramDiffAlgorithm = histogram;
            prefs.ShowDiffForAllParents = allParents;
            prefs.GraphDrawNonRelativesTextGray = textGray;
            prefs.GraphDrawAlternateBackColor = alternateRows;
            prefs.MulticolorBranches = multicolor;
            prefs.GraphColorPerBranch = colorPerBranch;
            prefs.GraphColorAtRemoteMirror = colorAtRemoteMirror;
            prefs.StraightenGraphDiagonals = straighten;
            prefs.StraightenGraphSegmentsLimit = straightenLimit;
            prefs.HighlightAuthoredRevisions = highlightAuthored;
            prefs.ShowRevisionGridTooltips = gridTooltips;
            prefs.IncludeUntrackedFilesInManualStash = untrackedManual;
            prefs.IncludeUntrackedFilesInAutoStash = untrackedAuto;
            prefs.AutoPopStashAfterCheckout = popAfterCheckout;
            prefs.AutoPopStashAfterPull = popAfterPull;
            prefs.RebaseAutoStash = rebaseAutoStash;
            prefs.RecursiveSubmodules = recursiveSubmodules;
            prefs.RecentRepositoriesHistorySize = historySize;
            prefs.SortRecentRepos = sortRecent;
            prefs.ShorteningRecentRepoPathStrategy = shortening;
            prefs.TruncatePathMethod = truncate;
            prefs.UiFontFamily = uiFont;
            prefs.MonospaceFontFamily = monospaceFont;
            prefs.UiFontSize = uiFontSize;
            prefs.MonospaceFontSize = monospaceFontSize;
            prefs.GitHubHost = githubHost;
            prefs.GitHubIssueCommitMessages = githubIssues;
            prefs.GitHubIssueCommitMessageCount = githubIssueCount;
            settings.Save(prefs);

            // Saving raises UserScriptService.Changed, which is what puts a new script in
            // the Tools menu and in the grid's context menu without a restart.
            new UserScriptService().Save(scripts);

            // Drop the resolved families so the next window built asks again. Windows
            // already open keep theirs: re-flowing every layout under the pointer is
            // worse than a font that arrives with the next dialog.
            Dispatcher.UIThread.Post(Theming.AppFonts.Reload);

            // Written to the core too, because the core is what enforces it when it
            // saves the history (LocalRepositoryManager.AdjustHistorySize).
            GitCommands.AppSettings.RecentRepositoriesHistorySize = Math.Max(1, historySize);

            _commitInfoService.Save(new CommitInfoSettings
            {
                ShowContainedInBranchesLocal = commitInfo[0],
                ShowContainedInBranchesRemote = commitInfo[1],
                ShowContainedInBranchesRemoteIfNoLocal = commitInfo[2],
                ShowContainedInTags = commitInfo[3],
                ShowAnnotatedTagsMessages = commitInfo[4],
                ShowTagThisCommitDerivesFrom = commitInfo[5],
            });
        });

        // ---- Default pull action: UiState is what the toolbar reads.
        string pullAction = PullChoices[Math.Max(0, _pullAction.SelectedIndex)].Token;

        // ---- Theme and style: persist + apply the pair (already previewed live).
        bool autoRefresh = _autoRefresh.IsChecked == true;

        UiState ui = _uiStateService.Load();
        ui.Theme = SelectedTheme;
        ui.Style = _style.SelectedIndex == 1 ? "Classic" : "Modern";
        ui.UiSize = UiSizes.Name(SelectedUiSize);
        ui.ColoredIcons = _coloredIcons.IsChecked == true;
        ui.TitleBar = WindowChrome.Name(SelectedMergedTitleBar);
        ui.RepoTabs = RepoTabsOption.Name(SelectedRepoTabs);
        ui.DefaultPullAction = pullAction;
        ui.AutoRefresh = autoRefresh;

        // Trimmed on the way in: a stray trailing space would make the first token —
        // the executable — the empty string and silently disable the setting.
        ui.TerminalCommand = (_terminalCommand.Text ?? string.Empty).Trim();
        _uiStateService.Save(ui);
        SystemTheme.Follow(ui.Theme == SystemTheme.Name);
        ThemeManager.Apply(SelectedVariant, SelectedStyle);
        UiScaling.Apply(SelectedUiSize);
        ThemeManager.SetColoredIcons(ui.ColoredIcons);
        WindowChrome.Apply(SelectedMergedTitleBar);
        RepoTabsOption.Apply(SelectedRepoTabs);

        // The host owns the live UiState instance and re-serialises it on close, which
        // would otherwise overwrite the value just written to the file. Telling it
        // makes the change effective immediately AND survive the exit save.
        _pullActionChanged?.Invoke(pullAction);

        // Same contract for automatic refresh, which additionally has to start or stop
        // the repository watcher — only the host can do that.
        _autoRefreshChanged?.Invoke(autoRefresh);

        // An applied appearance is the new baseline: a later Cancel must not undo it.
        // Both dimensions move to the baseline together.
        _applied = true;
        _revertTheme = ui.Theme;
        _revertStyle = ui.Style;
        _revertUiSize = SelectedUiSize;
        _revertColoredIcons = ui.ColoredIcons;
        _revertMergedTitleBar = SelectedMergedTitleBar;
        _revertRepoTabs = SelectedRepoTabs;
    }

    // ---- hotkeys ------------------------------------------------------------

    /// <summary>
    ///  Builds the Hotkeys page: one row per command, showing its gesture as a button
    ///  that records the next keystroke, plus a Clear per row, a "Reset all" and a
    ///  conflict warning.
    ///
    ///  <para>Upstream splits this over <c>HotkeysSettingsPage</c> +
    ///  <c>ControlHotkeys</c> and detects duplicates with
    ///  <c>HotkeySettingsManager.IsUniqueKey</c>. The port needs the same check for a
    ///  reason of its own: <see cref="HotkeyService.Reindex"/> resolves a duplicate by
    ///  "first writer wins" (HotkeyService.cs:270), so a clashing binding does not
    ///  fail loudly — one of the two commands just stops responding. Flagging the
    ///  clash here is what keeps that from looking like a bug.</para>
    ///
    ///  <para>Commands are listed under their enum names on purpose: those are exactly
    ///  the keys of <c>hotkeys.json</c>, so what the page shows and what a hand-edited
    ///  file contains cannot diverge.</para>
    /// </summary>
    /// <summary>
    ///  The Scripts page: the list on the left of its editor, plus Add / Duplicate /
    ///  Remove. Upstream's <c>ScriptsSettingsPage</c>, minus the icon picker and the
    ///  PowerShell flag (see <see cref="UserScript"/> for why).
    ///
    ///  <para>Edits are written into <see cref="_scripts"/> as they are typed and saved
    ///  with the rest of the dialog, so Cancel really cancels — upstream writes each
    ///  field straight into its store.</para>
    /// </summary>
    /// <summary>
    ///  The GitHub page — the port of the settings <c>GitHub3Plugin.GetSettings</c>
    ///  yields: the host, the personal access token, the two links to GitHub's token
    ///  pages, and the commit-message issue helper.
    ///
    ///  <para>The token box is <b>write-only</b>. A stored token is never put back into
    ///  it: the page says where it is kept and whether it works, which is everything a
    ///  user needs, while a password rendered into a control is one screenshot away
    ///  from being public. Upstream shows it in clear.</para>
    /// </summary>
    private Panel BuildGitHubPage(IBrush text, IBrush dim)
    {
        Localize(
            _githubIssueMessages,
            null,
            "Offer the issues assigned to me as commit-message templates");

        _githubToken.Watermark = TranslationService.T("Paste a token here to store it");

        Button create = new() { Content = TranslationService.T("Create a token on GitHub…") };
        create.Click += (_, _) => new ExternalToolService().OpenUrl(GitHubForSettings().NewTokenUrl);

        Button manage = new()
        {
            Content = TranslationService.T("Manage my tokens…"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        manage.Click += (_, _) => new ExternalToolService().OpenUrl(GitHubForSettings().ManageTokensUrl);

        Button check = new()
        {
            Content = TranslationService.T("Check the stored token"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        check.Click += (_, _) => Async.Run(CheckGitHubTokenAsync, "checking the GitHub token");

        Button forget = new()
        {
            Content = TranslationService.T("Forget the stored token"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        forget.Click += (_, _) =>
        {
            GitHubTokenStore.Erase(GitHubForSettings().ApiHost);
            GitHubService.ForgetLogin();
            _githubToken.Text = string.Empty;
            RefreshGitHubTokenNote();
        };

        WrapPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 0,
            LineSpacing = 6,
            Children = { create, manage, check, forget },
        };

        _githubTokenNote.Foreground = dim;

        return CategoryPanel(
            GitHubKey, GitHubText,
            null, "Fork, pull requests and \"view in GitHub\" links. The token is a password: "
                + "it is handed to git's credential helper — the desktop keyring — and never "
                + "written into the settings file.",
            text,
            dim,
            Field(
                null,
                "Host",
                _githubHost,
                dim,
                "github.com, or the host name of a GitHub Enterprise install — whose API is "
                    + "then read from https://<host>/api/v3."),
            Field(null, "Personal access token", _githubToken, dim),
            _githubTokenNote,
            buttons,
            _githubIssueMessages,
            Field(
                null,
                "How many issues to offer",
                _githubIssueCount,
                dim,
                "The helper asks GitHub the first time the commit dialog's template menu is "
                    + "opened, and only while the box above is ticked."));
    }

    /// <summary>The service for the host currently TYPED in the box, so the buttons and
    /// the note follow an edit that has not been applied yet.</summary>
    private GitHubService GitHubForSettings()
        => new(new AppPreferences { GitHubHost = (_githubHost.Text ?? string.Empty).Trim() });

    /// <summary>
    ///  Updates the "where is the token kept" line, off the UI thread.
    ///
    ///  <para>Reading it starts <c>git credential fill</c>, and this runs while the
    ///  settings window is being populated. Synchronously, that put a process launch on
    ///  the UI thread — ~100 ms on Windows even when the helper answers immediately, and
    ///  unbounded when it did not: an interactive helper waiting on its own dialog froze
    ///  the whole app until Windows killed it. The helper can no longer ask
    ///  (<c>RunCredential</c>), but a lookup that costs a process still does not belong
    ///  on the thread that has to paint the window.</para>
    /// </summary>
    private void RefreshGitHubTokenNote()
    {
        string apiHost = GitHubForSettings().ApiHost;

        Async.Run(
            async () =>
            {
                (string? token, GitHubTokenStore.Storage from) =
                    await Task.Run(() => GitHubTokenStore.Read(apiHost)).ConfigureAwait(true);

                // Back on the UI thread: Async.Run awaits with the context captured.
                _githubTokenNote.Text = GitHubTokenStore.Describe(
                    apiHost,
                    token is null ? GitHubTokenStore.Storage.None : from);
            },
            "reading where the GitHub token is stored");
    }

    private async Task CheckGitHubTokenAsync()
    {
        GitHubService service = GitHubForSettings();
        if (!service.IsConfigured)
        {
            _githubTokenNote.Text = TranslationService.T("There is no token to check.");
            return;
        }

        _githubTokenNote.Text = TranslationService.T("Asking GitHub…");
        try
        {
            GitHubUser user = await service.CreateClient().GetCurrentUserAsync(CancellationToken.None);
            _githubTokenNote.Text = TranslationService.TFormat(
                null, "The token works: {0} on {1}.", user.Login, service.Host);
        }
        catch (Exception ex)
        {
            _githubTokenNote.Text = ex is GitHubApiException
                ? ex.Message
                : $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private Panel BuildScriptsPage(IBrush text, IBrush dim)
    {
        foreach ((UserScriptEvent _, string label) in ScriptEvents)
        {
            _scriptEvent.Items.Add(new ComboBoxItem { Content = label });
        }

        Localize(_scriptEnabled, "ScriptsSettingsPage/chkEnabled.Text", "Enabled");
        Localize(_scriptAsk, "ScriptsSettingsPage/chkAskConfirmation.Text", "Ask before running it");
        Localize(
            _scriptBackground,
            "ScriptsSettingsPage/chkRunInBackground.Text",
            "Run it without showing the process window");
        Localize(
            _scriptInGridMenu,
            "ScriptsSettingsPage/chkAddToRevisionGridContextMenu.Text",
            "Also show it in the revision grid's context menu");

        _scriptList.SelectionChanged += (_, _) => LoadSelectedScript();
        _scriptName.PropertyChanged += (_, e) => EditScript(e, s => s.Name = _scriptName.Text ?? string.Empty);
        _scriptCommand.PropertyChanged += (_, e) => EditScript(e, s => s.Command = _scriptCommand.Text ?? string.Empty);
        _scriptArguments.PropertyChanged += (_, e) => EditScript(e, s => s.Arguments = _scriptArguments.Text ?? string.Empty);
        _scriptEvent.SelectionChanged += (_, _) => EditScript(
            null, s => s.OnEvent = ScriptEvents[Math.Max(0, _scriptEvent.SelectedIndex)].Event);
        _scriptEnabled.IsCheckedChanged += (_, _) => EditScript(null, s => s.Enabled = _scriptEnabled.IsChecked == true);
        _scriptAsk.IsCheckedChanged += (_, _) => EditScript(null, s => s.AskConfirmation = _scriptAsk.IsChecked == true);
        _scriptBackground.IsCheckedChanged += (_, _) => EditScript(null, s => s.RunInBackground = _scriptBackground.IsChecked == true);
        _scriptInGridMenu.IsCheckedChanged += (_, _) => EditScript(
            null, s => s.AddToRevisionGridContextMenu = _scriptInGridMenu.IsChecked == true);

        Button add = new() { MinWidth = 84, Margin = new Thickness(0, 0, 6, 0) };
        Button duplicate = new() { MinWidth = 84, Margin = new Thickness(0, 0, 6, 0) };
        Button remove = new() { MinWidth = 84 };
        Localize(add, "ScriptsSettingsPage/btnAdd.Text", "Add");
        Localize(duplicate, null, "Duplicate");
        Localize(remove, "ScriptsSettingsPage/btnRemove.Text", "Remove");

        add.Click += (_, _) =>
        {
            _scripts.Add(new UserScript { Name = TranslationService.T("New script") });
            RebuildScriptList(_scripts.Count - 1);
        };
        duplicate.Click += (_, _) =>
        {
            if (SelectedScript() is not { } source)
            {
                return;
            }

            _scripts.Add(new UserScript
            {
                Name = source.Name + " (2)",
                Command = source.Command,
                Arguments = source.Arguments,
                OnEvent = source.OnEvent,
                AskConfirmation = source.AskConfirmation,
                RunInBackground = source.RunInBackground,
                AddToRevisionGridContextMenu = source.AddToRevisionGridContextMenu,

                // A copy starts DISABLED: duplicating a pre-commit hook and having the
                // copy fire on the next commit, before it has been edited, is the one
                // outcome nobody wants.
                Enabled = false,
            });

            RebuildScriptList(_scripts.Count - 1);
        };
        remove.Click += (_, _) =>
        {
            int index = _scriptList.SelectedIndex;
            if (index >= 0 && index < _scripts.Count)
            {
                _scripts.RemoveAt(index);
                RebuildScriptList(Math.Min(index, _scripts.Count - 1));
            }
        };

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(duplicate);
        buttons.Children.Add(remove);

        string placeholders = string.Join(
            ", ",
            UserScriptService.Placeholders.Select(p => "{" + p.Name + "}"));

        return CategoryPanel(
            ScriptsKey, ScriptsText,
            null, "Your own commands, run from the Tools menu or around the operations "
                + "that matter. A script bound to a \"Before…\" event that exits with an "
                + "error STOPS the operation — that is what makes it a check and not a "
                + "log line.",
            text,
            dim,
            _scriptList,
            buttons,
            Field("ScriptsSettingsPage/lblName.Text", "Name", _scriptName, dim),
            Field(
                "ScriptsSettingsPage/lblCommand.Text",
                "Command",
                _scriptCommand,
                dim,
                "The program to run. It is NOT passed to a shell, so a repository name "
                    + "with a space or a semicolon in it cannot turn into extra commands. "
                    + "For a pipeline, name the shell yourself: command \"bash\", "
                    + "arguments \"-c\" \"…\"."),
            Field(
                "ScriptsSettingsPage/lblArguments.Text",
                "Arguments",
                _scriptArguments,
                dim,
                "Split on spaces; put double quotes around anything that must stay one "
                    + "argument. Substituted before running: " + placeholders + "."),
            Field("ScriptsSettingsPage/lblOnEvent.Text", "When", _scriptEvent, dim),
            _scriptEnabled,
            _scriptAsk,
            _scriptBackground,
            _scriptInGridMenu);
    }

    private UserScript? SelectedScript()
        => _scriptList.SelectedIndex >= 0 && _scriptList.SelectedIndex < _scripts.Count
            ? _scripts[_scriptList.SelectedIndex]
            : null;

    // Applies one edit to the selected script. The PropertyChanged overload filters for
    // TextProperty, so a focus or a caret move does not count as an edit.
    private void EditScript(global::Avalonia.AvaloniaPropertyChangedEventArgs? e, Action<UserScript> change)
    {
        if (_loadingScript || (e is not null && e.Property != TextBox.TextProperty))
        {
            return;
        }

        if (SelectedScript() is { } script)
        {
            change(script);
            if (_scriptList.SelectedIndex is int index && index >= 0 && index < _scriptList.ItemCount
                && _scriptList.Items[index] is ListBoxItem item)
            {
                item.Content = ScriptLabel(script);
            }
        }
    }

    private static string ScriptLabel(UserScript script)
    {
        string name = script.Name is { Length: > 0 } n ? n : script.Command;
        return script.Enabled ? name : name + "  —  " + TranslationService.T("disabled");
    }

    // Rebuilds the list box from _scripts and selects one row. The list is rebuilt rather
    // than bound: it is at most a handful of rows, and a binding would need a model with
    // change notification for four checkboxes that are edited in place.
    private void RebuildScriptList(int select)
    {
        _scriptList.Items.Clear();
        foreach (UserScript script in _scripts)
        {
            _scriptList.Items.Add(new ListBoxItem { Content = ScriptLabel(script) });
        }

        _scriptList.SelectedIndex = _scripts.Count == 0 ? -1 : Math.Clamp(select, 0, _scripts.Count - 1);
        LoadSelectedScript();
    }

    private void LoadSelectedScript()
    {
        UserScript? script = SelectedScript();
        _loadingScript = true;
        try
        {
            _scriptName.Text = script?.Name ?? string.Empty;
            _scriptCommand.Text = script?.Command ?? string.Empty;
            _scriptArguments.Text = script?.Arguments ?? string.Empty;
            _scriptEnabled.IsChecked = script?.Enabled ?? false;
            _scriptAsk.IsChecked = script?.AskConfirmation ?? false;
            _scriptBackground.IsChecked = script?.RunInBackground ?? false;
            _scriptInGridMenu.IsChecked = script?.AddToRevisionGridContextMenu ?? false;
            _scriptEvent.SelectedIndex = script is null
                ? 0
                : Math.Max(0, Array.FindIndex(ScriptEvents, c => c.Event == script.OnEvent));
        }
        finally
        {
            _loadingScript = false;
        }

        foreach (Control control in new Control[]
                 {
                     _scriptName, _scriptCommand, _scriptArguments, _scriptEvent,
                     _scriptEnabled, _scriptAsk, _scriptBackground, _scriptInGridMenu,
                 })
        {
            control.IsEnabled = script is not null;
        }
    }

    private Panel BuildHotkeysPage(IBrush text, IBrush dim)
    {
        // Seven scopes in one page: the window's own commands first, then the six
        // per-control ones. A page per scope would hide the thing worth seeing — that
        // the same combination can mean two things depending on where the focus is.
        foreach (BrowseCommand command in Enum.GetValues<BrowseCommand>())
        {
            _hotkeyDraft[(HotkeyScope.Browse, command.ToString())] = _hotkeys.GestureFor(command);
        }

        foreach (HotkeyScope scope in HotkeyScopes.All.Keys)
        {
            foreach ((string command, HotkeyGesture? gesture) in _hotkeys.ScopeBindings(scope))
            {
                _hotkeyDraft[(scope, command)] = gesture;
            }
        }

        Button resetAll = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 110 };
        Localize(resetAll, "HotkeysSettingsPage/btnResetAllHotkeys.Text", "Reset all");
        resetAll.Click += (_, _) =>
        {
            StopCapture();
            foreach (BrowseCommand command in Enum.GetValues<BrowseCommand>())
            {
                _hotkeyDraft[(HotkeyScope.Browse, command.ToString())] =
                    HotkeyService.Defaults.TryGetValue(command, out HotkeyGesture g) ? g : null;
            }

            foreach ((HotkeyScope scope, IReadOnlyDictionary<string, HotkeyGesture> defaults) in HotkeyScopes.All)
            {
                foreach ((string command, HotkeyGesture gesture) in defaults)
                {
                    _hotkeyDraft[(scope, command)] = gesture;
                }
            }

            RefreshHotkeyRows();
        };

        _hotkeyWarning = new TextBlock
        {
            Foreground = Resource("App.DiffRemoved", "#CE5C5C"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        StackPanel rows = new() { Spacing = 2 };
        AddHotkeySection(rows, HotkeyScope.Browse, text,
            [.. Enum.GetValues<BrowseCommand>().Select(c => c.ToString())]);
        foreach ((HotkeyScope scope, IReadOnlyDictionary<string, HotkeyGesture> defaults) in HotkeyScopes.All)
        {
            AddHotkeySection(rows, scope, text, [.. defaults.Keys]);
        }

        // Recording listens on the window in the tunnelling phase, for the same reason
        // HotkeyService itself does: a bubbling handler would never see the keys the
        // focused button swallows (Space and Enter, among others), so those could never
        // be assigned. handledEventsToo for the same reason.
        AddHandler(KeyDownEvent, OnHotkeyCapture, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The row captions ("None", "Press a key…", the conflict warning) are built here
        // rather than by Localize, so they are re-applied through the same list a
        // language switch walks.
        Register(RefreshHotkeyRows);

        string note = _hotkeysAreLive
            ? "Click a gesture, then press the combination to assign — Esc cancels, "
                + "Backspace clears. Changes apply when you press OK or Apply. A "
                + "combination may be reused in different scopes: which one answers "
                + "depends on what has the focus, and a focused view wins over the main "
                + "window's own binding for the same combination."
            : "Click a gesture, then press the combination to assign — Esc cancels, "
                + "Backspace clears. Changes are saved to hotkeys.json but only take "
                + "effect at the next start, because this dialog was not given the "
                + "running keyboard map.";

        return CategoryPanel(
            HotkeysKey, HotkeysText,
            null, note,
            text,
            dim,
            resetAll,
            _hotkeyWarning,
            rows);
    }

    // One scope: a heading, then a row per command.
    private void AddHotkeySection(
        StackPanel rows, HotkeyScope scope, IBrush text, IReadOnlyList<string> commands)
    {
        rows.Children.Add(new TextBlock
        {
            Text = HotkeyScopes.Title(scope),
            Foreground = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, rows.Children.Count == 0 ? 0 : 14, 0, 4),
        });

        foreach (string command in commands)
        {
            (HotkeyScope Scope, string Command) id = (scope, command);

            Button gesture = new() { MinWidth = 170, HorizontalContentAlignment = HorizontalAlignment.Center };
            gesture.Click += (_, _) => StartCapture(id);

            Button clear = new() { MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
            Localize(clear, "HotkeysSettingsPage/btnClearHotkey.Text", "Clear");
            clear.Click += (_, _) =>
            {
                StopCapture();
                _hotkeyDraft[id] = null;
                RefreshHotkeyRows();
            };

            TextBlock name = new()
            {
                Text = command,
                Foreground = text,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0),
            };

            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(gesture, 1);
            Grid.SetColumn(clear, 2);
            row.Children.Add(name);
            row.Children.Add(gesture);
            row.Children.Add(clear);
            rows.Children.Add(row);

            _hotkeyRows.Add((id, name, gesture));
        }
    }

    private void StartCapture((HotkeyScope Scope, string Command) id)
    {
        _capturing = id;
        RefreshHotkeyRows();
    }

    private void StopCapture()
    {
        _capturing = null;
        RefreshHotkeyRows();
    }

    // While recording, every keystroke belongs to the page: it is swallowed and turned
    // into the gesture of the command being recorded.
    private void OnHotkeyCapture(object? sender, KeyEventArgs e)
    {
        if (_capturing is not { } id)
        {
            return;
        }

        // A modifier on its own is not a gesture; keep waiting for the real key. Without
        // this, pressing Ctrl before the letter would assign "Ctrl+LeftCtrl".
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopCapture();
            return;
        }

        if (e.Key == Key.Back)
        {
            _hotkeyDraft[id] = null;
            StopCapture();
            return;
        }

        _hotkeyDraft[id] = new HotkeyGesture(e.Key, e.KeyModifiers);
        StopCapture();
    }

    // Re-labels every row from the draft and re-runs the duplicate check.
    private void RefreshHotkeyRows()
    {
        // A clash matters WITHIN a scope — there, whichever command the lookup happens
        // to see first is the only one that works. ACROSS scopes it is legitimate and
        // intended: F3 is "next match" in the viewer and "open with difftool" in the file
        // list, exactly as upstream. That includes the main window: it dispatches first,
        // but it asks the focused view whether the gesture is one of ITS scope's before
        // acting (MainWindow.IsGestureOwnedByFocusedView), so the overlap resolves by
        // focus instead of by luck. Flagging those would have painted a dozen rows red
        // on a default installation — which is exactly what the first version did.
        Dictionary<HotkeyScope, HashSet<HotkeyGesture>> seen = [];
        HashSet<(HotkeyScope, HotkeyGesture)> duplicates = [];

        foreach (((HotkeyScope scope, string _), HotkeyGesture? gesture) in _hotkeyDraft)
        {
            if (gesture is not { } g)
            {
                continue;
            }

            if (!seen.TryGetValue(scope, out HashSet<HotkeyGesture>? set))
            {
                set = [];
                seen[scope] = set;
            }

            if (!set.Add(g))
            {
                duplicates.Add((scope, g));
            }
        }

        IBrush conflict = Resource("App.DiffRemoved", "#CE5C5C");
        IBrush normal = Resource("App.Text", "#DCDCDC");

        foreach (((HotkeyScope Scope, string Command) id, TextBlock name, Button button) in _hotkeyRows)
        {
            HotkeyGesture? gesture = _hotkeyDraft.GetValueOrDefault(id);
            bool recording = _capturing == id;
            button.Content = recording
                ? TranslationService.T("HotkeysSettingsPage/lblPressKey.Text", "Press a key…")
                : gesture?.ToString() ?? TranslationService.T("HotkeysSettingsPage/lblNone.Text", "None");

            // The command name carries the colour as well as the gesture button: a
            // focused or hovered button takes its foreground from the theme's own
            // template, which was swallowing the mark on the row just edited.
            IBrush rowBrush = gesture is { } g2 && duplicates.Contains((id.Scope, g2)) ? conflict : normal;
            button.Foreground = rowBrush;
            name.Foreground = rowBrush;
        }

        if (duplicates.Count == 0)
        {
            _hotkeyWarning.IsVisible = false;
            return;
        }

        _hotkeyWarning.IsVisible = true;
        _hotkeyWarning.Text = string.Format(
            TranslationService.T(
                "These shortcuts are assigned twice within the same scope ({0}). Only one "
                + "of them will respond — give the others a different combination."),
            string.Join(", ", duplicates.Select(d => d.Item2.ToString()).Distinct().Order()));
    }

    // ---- layout building blocks -------------------------------------------

    // Builds a category panel: heading, description and its labelled fields.
    private Panel CategoryPanel(
        string? headingKey, string headingText,
        string? descriptionKey, string descriptionText,
        IBrush text, IBrush dim, params Control[] fields)
    {
        StackPanel stack = new() { Margin = new Thickness(20), Spacing = 14 };

        TextBlock heading = new()
        {
            Foreground = text,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        Localize(heading, headingKey, headingText);
        stack.Children.Add(heading);

        TextBlock description = new()
        {
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -6, 0, 0),
        };
        Localize(description, descriptionKey, descriptionText);
        stack.Children.Add(description);

        foreach (Control field in fields)
        {
            stack.Children.Add(field);
        }

        return stack;
    }

    // A label above its editor control. The label wraps: several translations of
    // these captions are half again as long as the English.
    // A small left-aligned box for a whole number. Narrow on purpose: the width tells
    // the user a count is expected, which no watermark would.
    // A drop-down of the installed families, with an empty first entry for "let the app
    // choose". Built from the font manager, so it can only offer fonts that resolve.
    private static ComboBox FontCombo()
    {
        ComboBox combo = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        combo.Items.Add(new ComboBoxItem { Content = string.Empty });
        foreach (string family in Theming.AppFonts.InstalledFamilies())
        {
            combo.Items.Add(new ComboBoxItem { Content = family });
        }

        return combo;
    }

    // Puts the selection on <paramref name="family"/>, or on the empty entry when it is
    // not installed — an uninstalled name in the file is not worth showing as if it
    // were in force, since AppFonts ignores it too.
    private static void SelectFont(ComboBox combo, string family)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && string.Equals(item.Content as string, family, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string SelectedFont(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty;

    // Where a stored token sits in its list; 0 for anything unrecognised, which is what
    // SettingsService.Sanitize would have written back anyway.
    private static int TokenIndex(IReadOnlyList<string> tokens, string token)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], token, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
    }

    // A drop-down of Ask / Always / Never, in SettingsService.AskAlwaysNever order so
    // the index IS the stored token's index.
    private ComboBox AskAlwaysNeverCombo()
    {
        ComboBox combo = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach ((string key, string label) in AskChoices)
        {
            ComboBoxItem item = new();
            Localize(item, key, label);
            combo.Items.Add(item);
        }

        return combo;
    }

    private static TextBox NumberBox() =>
        new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 90 };

    // Reads a NumberBox. Anything unparsable — blank, letters, a negative — falls back
    // to <paramref name="fallback"/>, so a mistyped field cannot wipe a working limit.
    private static int Number(TextBox box, int fallback, int max) =>
        int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        && value >= 0
            ? Math.Min(value, max)
            : fallback;

    /// <summary>
    ///  A control shifted right, for a sub-option that only means something while the
    ///  one above it is on. The indent is the whole affordance: it says "this belongs
    ///  to that" without a group box, which is what every other page here avoids.
    /// </summary>
    private static Control Indented(Control control, int depth = 1)
    {
        control.Margin = new Thickness(22 * depth, -8, 0, 0);
        return control;
    }

    // The two sub-options of the graph palette, each live only while its parent is.
    private void UpdateGraphColorGates()
    {
        _colorPerBranch.IsEnabled = _multicolorBranches.IsChecked == true;
        _colorAtRemoteMirror.IsEnabled = _colorPerBranch.IsEnabled && _colorPerBranch.IsChecked == true;
    }

    private Control Field(string? labelKey, string labelText, Control editor, IBrush dim)
    {
        StackPanel field = new() { Spacing = 4 };
        TextBlock label = new() { Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localize(label, labelKey, labelText);
        field.Children.Add(label);
        field.Children.Add(editor);
        return field;
    }

    /// <summary>
    ///  A <see cref="Field"/> with one line of small print under the editor, for a control
    ///  whose effect is narrower than its name suggests. The note is a plain English
    ///  literal, like the labels around it: upstream has no equivalent setting, so there is
    ///  no trans-unit id to borrow (same call M80 made for "Style").
    /// </summary>
    private Control Field(string? labelKey, string labelText, Control editor, IBrush dim, string note)
    {
        StackPanel field = (StackPanel)Field(labelKey, labelText, editor, dim);
        field.Children.Add(new TextBlock
        {
            Text = note,
            Foreground = dim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        return field;
    }

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
