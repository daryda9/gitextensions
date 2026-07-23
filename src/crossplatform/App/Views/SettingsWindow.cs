using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
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
///  <para>Save applies + persists everything; Cancel discards, reverting a live
///  theme preview back to the theme that was active on open.</para>
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

    // The theme active when the window opened; restored on Cancel.
    private readonly string _originalTheme;

    private bool _saved;

    private static readonly (string Token, string Label)[] PullChoices =
    [
        ("merge", "Merge (git pull)"),
        ("rebase", "Rebase (git pull --rebase)"),
        ("fetch", "Fetch only (no merge/rebase)"),
    ];

    public SettingsWindow(string? repoPath)
    {
        _repoPath = repoPath;

        IBrush window = Resource("App.Window", "#1E1E1E");
        IBrush panel = Resource("App.Panel", "#252526");
        IBrush border = Resource("App.Border", "#3F3F46");
        IBrush text = Resource("App.Text", "#DCDCDC");
        IBrush dim = Resource("App.TextDim", "#9B9B9B");

        Title = "Settings";
        Width = 640;
        Height = 440;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        // ---- Category panels (right pane content) --------------------------
        _userName = new TextBox { Watermark = "Your name" };
        _userEmail = new TextBox { Watermark = "you@example.com" };
        _identityPanel = CategoryPanel(
            "Git identity",
            "Stored with git config (repository-local). If a value is empty the "
                + "effective/global setting is shown.",
            text,
            dim,
            Field("Name (user.name)", _userName, dim),
            Field("Email (user.email)", _userEmail, dim));

        _pullAction = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        foreach ((string _, string label) in PullChoices)
        {
            _pullAction.Items.Add(new ComboBoxItem { Content = label });
        }

        _behaviourPanel = CategoryPanel(
            "Pull behaviour",
            "Chooses what the Pull command does by default in this app.",
            text,
            dim,
            Field("Default pull action", _pullAction, dim));

        _theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        _theme.Items.Add(new ComboBoxItem { Content = "Dark" });
        _theme.Items.Add(new ComboBoxItem { Content = "Light" });
        _theme.SelectionChanged += (_, _) => PreviewTheme();

        _appearancePanel = CategoryPanel(
            "Appearance",
            "The application colour theme. The choice is applied immediately as a "
                + "preview and persisted on Save (reverted on Cancel).",
            text,
            dim,
            Field("Default theme", _theme, dim));

        Grid rightPane = new();
        rightPane.Children.Add(_identityPanel);
        rightPane.Children.Add(_behaviourPanel);
        rightPane.Children.Add(_appearancePanel);

        // ---- Left category list -------------------------------------------
        ListBox categories = new()
        {
            Background = panel,
            BorderThickness = new Thickness(0),
            Width = 170,
        };
        categories.Items.Add(new ListBoxItem { Content = "Git identity" });
        categories.Items.Add(new ListBoxItem { Content = "Pull behaviour" });
        categories.Items.Add(new ListBoxItem { Content = "Appearance" });
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

        // ---- Save / Cancel -------------------------------------------------
        Button save = new() { Content = "Save", IsDefault = true, MinWidth = 84 };
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => { ApplyAndSave(); _saved = true; Close(); };
        cancel.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 10, 16, 12),
            Children = { save, cancel },
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
        Grid.SetColumn(rightPane, 1);
        body.Children.Add(categoryBox);
        body.Children.Add(rightPane);

        DockPanel root = new() { Background = window };
        DockPanel.SetDock(buttonBar, Dock.Bottom);
        root.Children.Add(buttonBar);
        root.Children.Add(body);
        Content = root;

        // Load current values.
        _originalTheme = LoadValues();

        // Revert a live theme preview if the window is closed without saving.
        Closing += (_, _) =>
        {
            if (!_saved)
            {
                ThemeManager.Apply(_originalTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark);
            }
        };
    }

    /// <summary>Shows the Settings dialog modally over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner, string? repoPath)
        => new SettingsWindow(repoPath).ShowDialog(owner);

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

    // Builds a category panel: heading, description and its labelled fields.
    private static Panel CategoryPanel(string heading, string description, IBrush text, IBrush dim, params Control[] fields)
    {
        StackPanel stack = new() { Margin = new Thickness(20), Spacing = 14 };
        stack.Children.Add(new TextBlock
        {
            Text = heading,
            Foreground = text,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -6, 0, 0),
        });
        foreach (Control field in fields)
        {
            stack.Children.Add(field);
        }

        return stack;
    }

    // A label above its editor control.
    private static Control Field(string label, Control editor, IBrush dim)
    {
        StackPanel field = new() { Spacing = 4 };
        field.Children.Add(new TextBlock { Text = label, Foreground = dim, FontSize = 12 });
        field.Children.Add(editor);
        return field;
    }

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
