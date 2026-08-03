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
public sealed class SettingsWindow : Window
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
    private readonly Dictionary<BrowseCommand, HotkeyGesture?> _hotkeyDraft = [];
    private readonly List<(BrowseCommand Command, TextBlock Name, Button Gesture)> _hotkeyRows = [];

    // The command whose next keystroke is being captured, if any.
    private BrowseCommand? _capturing;
    private TextBlock _hotkeyWarning = null!;

    // Behaviour page, beyond the pull action.
    private readonly CheckBox _autoRefresh;
    private readonly ComboBox _checkoutLocalChanges;

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
        _hotkeys = hotkeys ?? new HotkeyService();
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
                + "repository on disk, and what the checkout dialog pre-selects when the "
                + "working tree is dirty.",
            text,
            dim,
            Field("GeneralSettingsPage/lblDefaultPullAction.Text", "Default pull action", _pullAction, dim),
            _autoRefresh,
            Field(
                "FormCheckoutBranch/lblLocalChanges.Text",
                "Local changes when checking out a branch",
                _checkoutLocalChanges,
                dim));

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
        // languages, so they are plain items.
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

        Panel appearancePanel = CategoryPanel(
            AppearanceKey, AppearanceText,
            null, "The application colour theme, its visual style — \"Modern\" for the "
                + "current vector icons and neutral palette, \"Classic\" for the earlier "
                + "look — and how large the interface is drawn. \"Normal\" matches the "
                + "original Git Extensions; the other sizes scale the whole window, text "
                + "and spacing together. The three are independent, so any combination "
                + "works, and all of them are applied immediately as a preview and "
                + "persisted on OK or Apply (reverted on Cancel).",
            text,
            dim,
            Field("ColorsSettingsPage/gbTheme.Text", "Theme", _theme, dim),
            // No upstream trans-unit carries either label, so both are plain literals —
            // the same choice M80 made for "Style". Upstream has no equivalent setting
            // (its only scaling control is the high-DPI auto-scale checkbox), so there is
            // no id to borrow and no translated target to inherit.
            Field(null, "Style", _style, dim),
            Field(null, "UI size", _uiSize, dim));

        Panel hotkeysPanel = BuildHotkeysPage(text, dim);

        // Category order — the left list is built from the same sequence below, so the
        // two cannot fall out of step.
        _pages.Add(identityPanel);
        _pages.Add(gitConfigPanel);
        _pages.Add(blamePanel);
        _pages.Add(commitInfoPanel);
        _pages.Add(hotkeysPanel);
        _pages.Add(behaviourPanel);
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
                ThemeManager.Apply(
                    _revertTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark,
                    _revertStyle == "Classic" ? AppStyle.Classic : AppStyle.Modern);
                UiScaling.Apply(_revertUiSize);
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

        // Checkout default: its own file, so the file is always the truth.
        string checkoutAction = new SettingsService().Load().DefaultCheckoutLocalChangesAction;
        int checkoutIndex = Array.FindIndex(CheckoutChoices, c => c.Token == checkoutAction);
        _checkoutLocalChanges.SelectedIndex = checkoutIndex >= 0 ? checkoutIndex : 0;

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
        _theme.SelectedIndex = ui.Theme == "Light" ? 1 : 0;
        _style.SelectedIndex = ui.Style == "Classic" ? 1 : 0;

        // The size the dialog previews from, and the one Cancel returns to. Read from
        // the live engine rather than from the file: the host applied it at startup, and
        // it is the engine that says what is on screen right now.
        _revertUiSize = UiScaling.CurrentSize;
        _uiSize.SelectedIndex = Array.IndexOf(UiSizes.All, _revertUiSize);
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
        => ThemeManager.Apply(SelectedVariant, SelectedStyle);

    // Live, like the theme and the style: UiScaling re-scales the open windows in place,
    // this dialog included, so the preview is also the thing being previewed.
    private void PreviewUiSize() => UiScaling.Apply(SelectedUiSize);

    private UiSize SelectedUiSize
        => UiSizes.All[Math.Max(0, _uiSize.SelectedIndex)];

    private ThemeVariant SelectedVariant
        => _theme.SelectedIndex == 1 ? ThemeVariant.Light : ThemeVariant.Dark;

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
        _hotkeys.ApplyBindings(_hotkeyDraft);

        // ---- Checkout default and commit-info toggles: files of their own, no
        // last-writer-wins hazard with the host's UiState. Saving the commit-info file
        // raises CommitInfoSettingsService.Changed, which is what makes an open commit
        // details panel adopt the change instead of overwriting it later.
        string checkoutAction = CheckoutChoices[Math.Max(0, _checkoutLocalChanges.SelectedIndex)].Token;
        bool[] commitInfo = Array.ConvertAll(_commitInfoChecks, box => box.IsChecked == true);
        _ = Task.Run(() =>
        {
            SettingsService settings = new();
            AppPreferences prefs = settings.Load();
            prefs.DefaultCheckoutLocalChangesAction = checkoutAction;
            settings.Save(prefs);

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
        ui.Theme = _theme.SelectedIndex == 1 ? "Light" : "Dark";
        ui.Style = _style.SelectedIndex == 1 ? "Classic" : "Modern";
        ui.UiSize = UiSizes.Name(SelectedUiSize);
        ui.DefaultPullAction = pullAction;
        ui.AutoRefresh = autoRefresh;
        _uiStateService.Save(ui);
        ThemeManager.Apply(SelectedVariant, SelectedStyle);
        UiScaling.Apply(SelectedUiSize);

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
    private Panel BuildHotkeysPage(IBrush text, IBrush dim)
    {
        foreach (BrowseCommand command in Enum.GetValues<BrowseCommand>())
        {
            _hotkeyDraft[command] = _hotkeys.GestureFor(command);
        }

        Button resetAll = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 110 };
        Localize(resetAll, "HotkeysSettingsPage/btnResetAllHotkeys.Text", "Reset all");
        resetAll.Click += (_, _) =>
        {
            StopCapture();
            foreach (BrowseCommand command in Enum.GetValues<BrowseCommand>())
            {
                _hotkeyDraft[command] =
                    HotkeyService.Defaults.TryGetValue(command, out HotkeyGesture g) ? g : null;
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
        foreach (BrowseCommand command in Enum.GetValues<BrowseCommand>())
        {
            BrowseCommand captured = command;

            Button gesture = new() { MinWidth = 170, HorizontalContentAlignment = HorizontalAlignment.Center };
            gesture.Click += (_, _) => StartCapture(captured);

            Button clear = new() { MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
            Localize(clear, "HotkeysSettingsPage/btnClearHotkey.Text", "Clear");
            clear.Click += (_, _) =>
            {
                StopCapture();
                _hotkeyDraft[captured] = null;
                RefreshHotkeyRows();
            };

            TextBlock name = new()
            {
                Text = captured.ToString(),
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

            _hotkeyRows.Add((captured, name, gesture));
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
                + "Backspace clears. Changes apply when you press OK or Apply."
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

    private void StartCapture(BrowseCommand command)
    {
        _capturing = command;
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
        if (_capturing is not { } command)
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
            _hotkeyDraft[command] = null;
            StopCapture();
            return;
        }

        _hotkeyDraft[command] = new HotkeyGesture(e.Key, e.KeyModifiers);
        StopCapture();
    }

    // Re-labels every row from the draft and re-runs the duplicate check.
    private void RefreshHotkeyRows()
    {
        // A gesture used by more than one command: whichever of them Reindex happens to
        // see first would be the only one that works.
        HashSet<HotkeyGesture> seen = [];
        HashSet<HotkeyGesture> duplicates = [];
        foreach (HotkeyGesture? gesture in _hotkeyDraft.Values)
        {
            if (gesture is { } g && !seen.Add(g))
            {
                duplicates.Add(g);
            }
        }

        IBrush conflict = Resource("App.DiffRemoved", "#CE5C5C");
        IBrush normal = Resource("App.Text", "#DCDCDC");

        foreach ((BrowseCommand command, TextBlock name, Button button) in _hotkeyRows)
        {
            HotkeyGesture? gesture = _hotkeyDraft.GetValueOrDefault(command);
            bool recording = _capturing == command;
            button.Content = recording
                ? TranslationService.T("HotkeysSettingsPage/lblPressKey.Text", "Press a key…")
                : gesture?.ToString() ?? TranslationService.T("HotkeysSettingsPage/lblNone.Text", "None");

            // The command name carries the colour as well as the gesture button: a
            // focused or hovered button takes its foreground from the theme's own
            // template, which was swallowing the mark on the row just edited.
            IBrush rowBrush = gesture is { } g2 && duplicates.Contains(g2) ? conflict : normal;
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
                "The same shortcut is assigned to more than one command ({0}). Only one of "
                + "them will respond — give the others a different combination."),
            string.Join(", ", duplicates.Select(d => d.ToString()).Order()));
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
    private Control Field(string? labelKey, string labelText, Control editor, IBrush dim)
    {
        StackPanel field = new() { Spacing = 4 };
        TextBlock label = new() { Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localize(label, labelKey, labelText);
        field.Children.Add(label);
        field.Children.Add(editor);
        return field;
    }

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
