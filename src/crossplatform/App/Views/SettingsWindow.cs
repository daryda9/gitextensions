using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
///   <item>Default pull action (the five actions the toolbar's Pull split button
///    offers), persisted in <see cref="UiState.DefaultPullAction"/> — the value the
///    split button itself reads.</item>
///   <item>Default theme (Light / Dark), persisted via <see cref="UiStateService"/>
///    and applied live through <see cref="ThemeManager"/>.</item>
///  </list>
///
///  <para><b>Buttons.</b> OK applies + persists everything and closes, Apply does
///  the same without closing, Cancel discards — reverting a live theme preview back
///  to the theme that was active on open (or to the last Apply).</para>
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

    // One three-state checkbox per GitConfigChoices entry, same order.
    private readonly CheckBox[] _gitConfigChecks;

    // The level the identity fields currently show, so a level change can be told
    // from the initial load and the fields reloaded for the new level.
    private GitSettingLevel _loadedLevel = GitSettingLevel.Local;
    private bool _loadingIdentity;
    private bool _loadingGitConfig;

    // Category panels, shown one at a time in the right pane.
    private readonly Panel _identityPanel;
    private readonly Panel _gitConfigPanel;
    private readonly Panel _behaviourPanel;
    private readonly Panel _appearancePanel;

    // The level selector, hoisted out of the identity panel: it now governs every
    // page that reads/writes git config (identity AND the advanced keys), exactly as
    // upstream's SettingsPageHeader governs a whole GitConfigBaseSettingsPage.
    private readonly Control _levelHeader;

    // Every caption re-applies itself from here, so a language switch needs no
    // rebuild of the control tree (and no reload of the user's pending edits).
    private readonly List<Action> _relabel = [];

    // The theme to restore when the dialog is dismissed without applying.
    private string _revertTheme;

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
    private const string BehaviourKey = "GeneralSettingsPage/groupBoxBehaviour.Text";
    private const string BehaviourText = "Behaviour";
    private const string AppearanceKey = "AppearanceSettingsPage/$this.Text";
    private const string AppearanceText = "Appearance";

    public SettingsWindow(string? repoPath, string? currentPullAction = null, Action<string>? pullActionChanged = null)
    {
        _repoPath = repoPath;
        _currentPullAction = currentPullAction;
        _pullActionChanged = pullActionChanged;

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

        _identityPanel = CategoryPanel(
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

        _gitConfigPanel = CategoryPanel(
            GitConfigKey, GitConfigText,
            null, "Written directly into the git config at the level chosen above, so git "
                + "itself obeys them — inside this app and outside it. Each box has three "
                + "states: checked (the key is set to true), unchecked (set to false) and "
                + "the third, filled state (the key is not set at all, so a wider config "
                + "level or git's own default decides).",
            text,
            dim,
            gitConfigFields);

        _pullAction = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach ((string _, string key, string label) in PullChoices)
        {
            ComboBoxItem item = new();
            Localize(item, key, label);
            _pullAction.Items.Add(item);
        }

        _behaviourPanel = CategoryPanel(
            BehaviourKey, BehaviourText,
            null, "Chooses what the Pull command does by default in this app.",
            text,
            dim,
            Field("GeneralSettingsPage/lblDefaultPullAction.Text", "Default pull action", _pullAction, dim));

        _theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };

        // "Dark" and "Light" have no upstream trans-unit and read the same in most
        // languages, so they are plain items.
        _theme.Items.Add(new ComboBoxItem { Content = "Dark" });
        _theme.Items.Add(new ComboBoxItem { Content = "Light" });
        _theme.SelectionChanged += (_, _) => PreviewTheme();

        _appearancePanel = CategoryPanel(
            AppearanceKey, AppearanceText,
            null, "The application colour theme. The choice is applied immediately as a "
                + "preview and persisted on OK or Apply (reverted on Cancel).",
            text,
            dim,
            Field("ColorsSettingsPage/gbTheme.Text", "Theme", _theme, dim));

        Grid rightPane = new();
        rightPane.Children.Add(_identityPanel);
        rightPane.Children.Add(_gitConfigPanel);
        rightPane.Children.Add(_behaviourPanel);
        rightPane.Children.Add(_appearancePanel);

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
        categories.Items.Add(CategoryItem(BehaviourKey, BehaviourText));
        categories.Items.Add(CategoryItem(AppearanceKey, AppearanceText));
        categories.SelectionChanged += (_, _) =>
        {
            _identityPanel.IsVisible = categories.SelectedIndex == 0;
            _gitConfigPanel.IsVisible = categories.SelectedIndex == 1;
            _behaviourPanel.IsVisible = categories.SelectedIndex == 2;
            _appearancePanel.IsVisible = categories.SelectedIndex == 3;

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

        // Load current values.
        _revertTheme = LoadValues();

        ApplyTitle();
        TranslationService.LanguageChanged += OnLanguageChanged;

        // Revert a live theme preview if the window is closed without applying.
        Closing += (_, _) =>
        {
            TranslationService.LanguageChanged -= OnLanguageChanged;
            if (!_applied)
            {
                ThemeManager.Apply(_revertTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);
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
    /// </summary>
    public static Task ShowAsync(
        Window owner, string? repoPath, string? currentPullAction = null, Action<string>? pullActionChanged = null)
        => new SettingsWindow(repoPath, currentPullAction, pullActionChanged).ShowDialog(owner);

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

    // Reads git identity, pull action and theme into the controls; returns the
    // theme that was active on open (for the Cancel revert).
    private string LoadValues()
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

        // Theme.
        UiState ui = _uiStateService.Load();
        _theme.SelectedIndex = ui.Theme == "Light" ? 1 : 0;
        return ui.Theme;
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

    // Applies the theme preview live as the combo changes.
    private void PreviewTheme()
    {
        bool light = _theme.SelectedIndex == 1;
        ThemeManager.Apply(light ? ThemeVariant.Light : ThemeVariant.Dark);
    }

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

        // ---- Default pull action: UiState is what the toolbar reads.
        string pullAction = PullChoices[Math.Max(0, _pullAction.SelectedIndex)].Token;

        // ---- Theme: persist + apply (already previewed live).
        UiState ui = _uiStateService.Load();
        ui.Theme = _theme.SelectedIndex == 1 ? "Light" : "Dark";
        ui.DefaultPullAction = pullAction;
        _uiStateService.Save(ui);
        ThemeManager.Apply(ui.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);

        // The host owns the live UiState instance and re-serialises it on close, which
        // would otherwise overwrite the value just written to the file. Telling it
        // makes the change effective immediately AND survive the exit save.
        _pullActionChanged?.Invoke(pullAction);

        // An applied theme is the new baseline: a later Cancel must not undo it.
        _applied = true;
        _revertTheme = ui.Theme;
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
