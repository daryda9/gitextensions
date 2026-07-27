using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

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
///    the reused core <see cref="GitModule"/> config surface (repo-local,
///    falling back to the effective/global value).</item>
///   <item>Default pull action (merge / rebase / fetch-only), persisted via
///    <see cref="SettingsService"/>.</item>
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
    private readonly SettingsService _settingsService = new();
    private readonly UiStateService _uiStateService = new();

    private readonly TextBox _userName;
    private readonly TextBox _userEmail;
    private readonly ComboBox _pullAction;
    private readonly ComboBox _theme;

    // Category panels, shown one at a time in the right pane.
    private readonly Panel _identityPanel;
    private readonly Panel _behaviourPanel;
    private readonly Panel _appearancePanel;

    // Every caption re-applies itself from here, so a language switch needs no
    // rebuild of the control tree (and no reload of the user's pending edits).
    private readonly List<Action> _relabel = [];

    // The theme to restore when the dialog is dismissed without applying.
    private string _revertTheme;

    private bool _applied;

    // The three default-pull-action choices, keyed to the upstream combo entries
    // of GeneralSettingsPage.
    private static readonly (string Token, string Key, string Label)[] PullChoices =
    [
        ("merge", "GeneralSettingsPage/_pullMerge.Text", "Pull - merge"),
        ("rebase", "GeneralSettingsPage/_pullRebase.Text", "Pull - rebase"),
        ("fetch", "GeneralSettingsPage/_fetch.Text", "Fetch"),
    ];

    // Category names, shared by the left list and the panel heading so the two can
    // never drift apart.
    // The port's own wording: no upstream trans-unit, hence a null key.
    private const string? IdentityKey = null;
    private const string IdentityText = "Git identity";
    private const string BehaviourKey = "GeneralSettingsPage/groupBoxBehaviour.Text";
    private const string BehaviourText = "Behaviour";
    private const string AppearanceKey = "AppearanceSettingsPage/$this.Text";
    private const string AppearanceText = "Appearance";

    public SettingsWindow(string? repoPath)
    {
        _repoPath = repoPath;

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

        _identityPanel = CategoryPanel(
            IdentityKey, IdentityText,
            null, "Stored with git config (repository-local). If a value is empty the "
                + "effective/global setting is shown.",
            text,
            dim,
            SettingsSourceNote(dim),
            Field("GitConfigSettingsPage/label3.Text", "User name", _userName, dim),
            Field("GitConfigSettingsPage/label4.Text", "User email", _userEmail, dim));

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
        rightPane.Children.Add(_behaviourPanel);
        rightPane.Children.Add(_appearancePanel);

        // A translated page can be taller than the dialog; scroll rather than clip.
        ScrollViewer rightScroll = new()
        {
            Content = rightPane,
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
        categories.Items.Add(CategoryItem(BehaviourKey, BehaviourText));
        categories.Items.Add(CategoryItem(AppearanceKey, AppearanceText));
        categories.SelectionChanged += (_, _) =>
        {
            _identityPanel.IsVisible = categories.SelectedIndex == 0;
            _behaviourPanel.IsVisible = categories.SelectedIndex == 1;
            _appearancePanel.IsVisible = categories.SelectedIndex == 2;
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

    /// <summary>Shows the Settings dialog modally over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner, string? repoPath)
        => new SettingsWindow(repoPath).ShowDialog(owner);

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

    private void Localize(ContentControl control, string? key, string english)
        => Register(() => control.Content = TranslationService.T(key, english));

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
        // Git identity (repo-local, falling back to the effective/global value).
        if (_repoPath is not null)
        {
            try
            {
                GitModule module = GitContext.CreateModule(_repoPath);
                _userName.Text = module.GetEffectiveSetting("user.name");
                _userEmail.Text = module.GetEffectiveSetting("user.email");
            }
            catch
            {
                // Leave the fields empty if the repo can't be read.
            }
        }

        // Default pull action.
        AppPreferences settings = _settingsService.Load();
        int pullIndex = Array.FindIndex(PullChoices, c => c.Token == settings.DefaultPullAction);
        _pullAction.SelectedIndex = pullIndex >= 0 ? pullIndex : 0;

        // Theme.
        UiState ui = _uiStateService.Load();
        _theme.SelectedIndex = ui.Theme == "Light" ? 1 : 0;
        return ui.Theme;
    }

    // Applies the theme preview live as the combo changes.
    private void PreviewTheme()
    {
        bool light = _theme.SelectedIndex == 1;
        ThemeManager.Apply(light ? ThemeVariant.Light : ThemeVariant.Dark);
    }

    private void ApplyAndSave()
    {
        // ---- Git identity: write repo-local user.name / user.email.
        if (_repoPath is not null)
        {
            try
            {
                GitModule module = GitContext.CreateModule(_repoPath);
                SetOrUnset(module, "user.name", _userName.Text);
                SetOrUnset(module, "user.email", _userEmail.Text);
            }
            catch
            {
                // Best-effort; a git failure must not lose the other settings.
            }
        }

        // ---- Default pull action.
        AppPreferences settings = _settingsService.Load();
        settings.DefaultPullAction = PullChoices[Math.Max(0, _pullAction.SelectedIndex)].Token;
        _settingsService.Save(settings);

        // ---- Theme: persist + apply (already previewed live).
        UiState ui = _uiStateService.Load();
        ui.Theme = _theme.SelectedIndex == 1 ? "Light" : "Dark";
        _uiStateService.Save(ui);
        ThemeManager.Apply(ui.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);

        // An applied theme is the new baseline: a later Cancel must not undo it.
        _applied = true;
        _revertTheme = ui.Theme;
    }

    private static void SetOrUnset(GitModule module, string key, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            module.UnsetSetting(key);
        }
        else
        {
            module.SetSetting(key, value);
        }
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

    // "Settings source: Local for current repository" — the upstream page header,
    // stating where the identity values are written. Both halves are real
    // trans-units, so the note translates in full.
    private Control SettingsSourceNote(IBrush dim)
    {
        WrapPanel note = new() { Orientation = Orientation.Horizontal, ItemSpacing = 6 };

        TextBlock caption = new() { Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localize(caption, "SettingsPageHeader/label1.Text", "Settings source:");

        TextBlock value = new() { Foreground = dim, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Localize(value, "SettingsPageHeader/LocalRB.Text", "Local for current repository");

        note.Children.Add(caption);
        note.Children.Add(value);
        return note;
    }

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
