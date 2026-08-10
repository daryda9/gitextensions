using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Extensibility.Plugins;
using GitExtensions.Extensibility.Settings;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A native Avalonia editor for an <see cref="IGitPlugin"/>'s typed settings.
///
///  <para>Rather than porting the WinForms <c>ISettingControlBinding</c> framework,
///  this window inspects each <see cref="ISetting"/> by its <b>runtime type</b> and
///  maps it to an Avalonia control — <see cref="BoolSetting"/>→CheckBox,
///  <see cref="StringSetting"/>→TextBox, <c>NumberSetting&lt;T&gt;</c>→numeric
///  TextBox, <see cref="ChoiceSetting"/>→ComboBox, and a path-style string→TextBox
///  with a Browse button. Values are loaded and saved through each setting's
///  <c>this[SettingsSource]</c> indexer against the plugin's container source, so
///  they persist to git config exactly as the WinForms host would write them.</para>
/// </summary>
public sealed class PluginSettingsWindow : Theming.ZoomWindow
{
    private readonly IGitPlugin _plugin;
    private readonly SettingsSource _source;

    // Per-setting save closures, populated as each control is built.
    private readonly List<Action> _savers = [];

    // Promoted from locals for ApplyTranslations. The "no settings" note is optional:
    // it only exists for a plugin that exposes nothing editable.
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly TextBlock? _noSettings;

    // Every Browse button built by WithBrowse, so a language change re-captions them
    // all — there is one per path-shaped setting, not just one per window.
    private readonly List<Button> _browseButtons = [];

    private bool _saved;

    public PluginSettingsWindow(IGitPlugin plugin, SettingsSource source)
    {
        _plugin = plugin;
        _source = source;

        IBrush window = Resource("App.Window", "#1E1E1E");
        IBrush border = Resource("App.Border", "#3F3F46");
        IBrush text = Resource("App.Text", "#DCDCDC");
        IBrush dim = Resource("App.TextDim", "#9B9B9B");

        Width = 560;
        Height = 460;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        StackPanel fields = new() { Margin = new Thickness(20), Spacing = 16 };
        fields.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            Foreground = text,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        });
        if (!string.IsNullOrWhiteSpace(plugin.Description) && plugin.Description != plugin.Name)
        {
            fields.Children.Add(new TextBlock
            {
                Text = plugin.Description,
                Foreground = dim,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        int rendered = 0;
        foreach (ISetting setting in plugin.GetSettings())
        {
            Control? field = BuildField(setting, text, dim);
            if (field is not null)
            {
                fields.Children.Add(field);
                rendered++;
            }
        }

        if (rendered == 0)
        {
            _noSettings = new TextBlock
            {
                Foreground = dim,
                FontStyle = FontStyle.Italic,
            };
            fields.Children.Add(_noSettings);
        }

        ScrollViewer scroller = new()
        {
            Content = fields,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        _save = new Button { IsDefault = true, MinWidth = 84 };
        _cancel = new Button { IsCancel = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        _save.Click += (_, _) => { Persist(); _saved = true; Close(); };
        _cancel.Click += (_, _) => Close();

        Border buttonBar = new()
        {
            Background = window,
            BorderBrush = border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 10, 16, 12),
                Children = { _save, _cancel },
            },
        };

        DockPanel root = new() { Background = window };
        DockPanel.SetDock(buttonBar, Dock.Bottom);
        root.Children.Add(buttonBar);
        root.Children.Add(scroller);
        Content = root;
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>True if the user pressed Save (values were written to the source).</summary>
    public bool Saved => _saved;

    /// <summary>Shows the plugin settings dialog modally over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner, IGitPlugin plugin, SettingsSource source)
        => new PluginSettingsWindow(plugin, source).ShowDialog(owner);

    // Maps a setting to an Avalonia editor by runtime type, wiring a load + a save
    // closure. Returns null for setting types we do not render (e.g. PseudoSetting).
    private Control? BuildField(ISetting setting, IBrush text, IBrush dim)
    {
        switch (setting)
        {
            case BoolSetting b:
            {
                CheckBox box = new()
                {
                    Content = b.Caption,
                    Foreground = text,
                    IsChecked = b.ValueOrDefault(_source),
                };
                _savers.Add(() => b[_source] = box.IsChecked);
                return box;
            }

            case ChoiceSetting c:
            {
                ComboBox combo = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
                foreach (string value in c.Values)
                {
                    combo.Items.Add(new ComboBoxItem { Content = value });
                }

                string? current = c.ValueOrDefault(_source);
                int index = current is null ? -1 : IndexOf(c.Values, current);
                combo.SelectedIndex = index >= 0 ? index : (c.Values.Count > 0 ? 0 : -1);
                _savers.Add(() =>
                {
                    if (combo.SelectedIndex >= 0 && combo.SelectedIndex < c.Values.Count)
                    {
                        c[_source] = c.Values[combo.SelectedIndex];
                    }
                });
                return Labelled(c.Caption, combo, dim);
            }

            case StringSetting s:
            {
                TextBox tb = new() { Text = s.ValueOrDefault(_source) };
                _savers.Add(() => s[_source] = tb.Text);

                // A path-style setting name gets a Browse button beside the field.
                return LooksLikePath(s.Name) || LooksLikePath(s.Caption)
                    ? Labelled(s.Caption, WithBrowse(tb), dim)
                    : Labelled(s.Caption, tb, dim);
            }

            default:
                // NumberSetting<T> is generic, so it is matched reflectively below.
                if (IsNumberSetting(setting, out INumberBridge? number) && number is not null)
                {
                    TextBox tb = new() { Text = number.GetValueString(_source) };
                    _savers.Add(() => number.SetValueString(_source, tb.Text));
                    return Labelled(number.Caption, tb, dim);
                }

                // Unknown / non-rendered setting types (e.g. PseudoSetting) are skipped.
                return null;
        }
    }

    // A file-picker Browse button attached to the right of a path TextBox.
    private Control WithBrowse(TextBox tb)
    {
        Button browse = new() { MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        _browseButtons.Add(browse);
        // A cancelled or failed file picker must not crash the dialog: Async.Run is
        // what turns a throw from the storage provider into a logged line.
        browse.Click += (_, _) => Async.Run(
            async () =>
            {
                TopLevel? top = GetTopLevel(this);
                if (top is null)
                {
                    return;
                }

                IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions { AllowMultiple = false, Title = T("Choose a file") });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
                {
                    tb.Text = path;
                }
            },
            "choosing a file for a plugin setting");

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(tb, 0);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(tb);
        grid.Children.Add(browse);
        return grid;
    }

    private void Persist()
    {
        foreach (Action saver in _savers)
        {
            try
            {
                saver();
            }
            catch
            {
                // Best-effort: one bad value must not abort the rest of the save.
            }
        }
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        // The plugin's own name and description, and every setting caption, come from
        // the plugin assembly: they are its strings, not Git Extensions', and there is
        // no catalogue entry that could translate them. Only the frame is ours.
        Title = TranslationService.TFormat(
            null, "{0} — {1}", _plugin.Name, T("FormSettings/$this.Text", "settings"));

        _save.Content = T("FormGitAttributes/Save.Text", "Save");
        _cancel.Content = T("TranslatedStrings/_cancelText.Text", "Cancel");

        foreach (Button browse in _browseButtons)
        {
            browse.Content = T("FormFilePrompt/btnBrowse.Text", "Browse…");
        }

        if (_noSettings is not null)
        {
            // Upstream says the same thing in PluginSettingsPage/labelNoSettings.
            _noSettings.Text = T(
                "PluginSettingsPage/labelNoSettings.Text", "This plugin exposes no editable settings.");
        }
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // A caption above its editor control.
    private static Control Labelled(string caption, Control editor, IBrush dim)
    {
        StackPanel field = new() { Spacing = 4 };
        field.Children.Add(new TextBlock { Text = caption, Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        field.Children.Add(editor);
        return field;
    }

    private static int IndexOf(IList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool LooksLikePath(string? s)
        => s is not null && (s.Contains("path", StringComparison.OrdinalIgnoreCase)
                             || s.Contains("file", StringComparison.OrdinalIgnoreCase)
                             || s.Contains("folder", StringComparison.OrdinalIgnoreCase)
                             || s.Contains("directory", StringComparison.OrdinalIgnoreCase));

    // Recognises NumberSetting<T> (a generic type) via reflection and adapts its
    // string-based indexer, without the window taking a compile-time dependency on
    // any particular T.
    private static bool IsNumberSetting(ISetting setting, out INumberBridge? bridge)
    {
        Type type = setting.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NumberSetting<>))
        {
            bridge = new NumberBridge(setting, type);
            return true;
        }

        bridge = null;
        return false;
    }

    private interface INumberBridge
    {
        string Caption { get; }
        string GetValueString(SettingsSource source);
        void SetValueString(SettingsSource source, string? value);
    }

    // Reflective adapter over NumberSetting<T>'s object? this[SettingsSource] indexer
    // and Caption property, storing values as their string form.
    private sealed class NumberBridge : INumberBridge
    {
        private readonly object _setting;
        private readonly System.Reflection.PropertyInfo _indexer;

        public NumberBridge(object setting, Type type)
        {
            _setting = setting;
            Caption = (string)type.GetProperty(nameof(ISetting.Caption))!.GetValue(setting)!;
            _indexer = type.GetProperty("Item")!;
        }

        public string Caption { get; }

        public string GetValueString(SettingsSource source)
            => _indexer.GetValue(_setting, [source])?.ToString() ?? string.Empty;

        public void SetValueString(SettingsSource source, string? value)
        {
            // Store as the raw string; NumberSetting persists value?.ToString(), so a
            // numeric string round-trips. Blank clears the setting.
            object? boxed = string.IsNullOrWhiteSpace(value) ? null : value;
            _indexer.SetValue(_setting, boxed, [source]);
        }
    }

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
