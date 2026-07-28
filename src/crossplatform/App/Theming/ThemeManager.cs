using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Owns the app's palette brushes and swaps them between a dark and a light
///  theme at runtime.
///
///  <para>The brushes are created once and registered in
///  <see cref="Application.Resources"/>; views capture those instances. To
///  switch theme we mutate each brush's <see cref="SolidColorBrush.Color"/> in
///  place, so every control referencing a brush repaints live — no need for
///  DynamicResource bindings throughout the UI.</para>
/// </summary>
public static class ThemeManager
{
    // Resource keys used across the app (see the B(...) helpers in the views).
    private static readonly string[] Keys =
    [
        "App.Window", "App.Panel", "App.PanelAlt", "App.Toolbar", "App.Border",
        "App.Text", "App.TextDim", "App.Accent", "App.Selection", "App.GraphGreen",
        "App.Control",
    ];

    private static readonly Dictionary<string, Color> Dark = new()
    {
        ["App.Window"] = Color.Parse("#1E1E1E"),
        ["App.Panel"] = Color.Parse("#252526"),
        ["App.PanelAlt"] = Color.Parse("#2D2D30"),
        ["App.Toolbar"] = Color.Parse("#333337"),
        ["App.Border"] = Color.Parse("#3F3F46"),
        ["App.Text"] = Color.Parse("#DCDCDC"),
        ["App.TextDim"] = Color.Parse("#9B9B9B"),
        ["App.Accent"] = Color.Parse("#007ACC"),
        ["App.Selection"] = Color.Parse("#094771"),
        ["App.GraphGreen"] = Color.Parse("#4EC9B0"),

        // Input surfaces (text boxes, pickers). Same value as App.Panel: the key was
        // used by ~20 call sites without ever being registered, so Brush("App.Control",
        // Brushes.Black) silently pinned a black surface that never followed the theme —
        // unreadable in the light theme, where the text stays App.Text.
        ["App.Control"] = Color.Parse("#252526"),
    };

    private static readonly Dictionary<string, Color> Light = new()
    {
        ["App.Window"] = Color.Parse("#F3F3F3"),
        ["App.Panel"] = Color.Parse("#FFFFFF"),
        ["App.PanelAlt"] = Color.Parse("#ECECEC"),
        ["App.Toolbar"] = Color.Parse("#E4E4E4"),
        ["App.Border"] = Color.Parse("#C4C4C4"),
        ["App.Text"] = Color.Parse("#1E1E1E"),
        ["App.TextDim"] = Color.Parse("#6A6A6A"),
        ["App.Accent"] = Color.Parse("#007ACC"),
        ["App.Selection"] = Color.Parse("#CBE3F7"),
        ["App.GraphGreen"] = Color.Parse("#1E7D5A"),
        ["App.Control"] = Color.Parse("#FFFFFF"),
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    /// <summary>Creates the palette brushes and registers them; applies the dark theme.</summary>
    public static void Initialize(Application app)
    {
        foreach (string key in Keys)
        {
            SolidColorBrush brush = new(Dark[key]);
            Brushes[key] = brush;
            app.Resources[key] = brush;
        }

        Apply(ThemeVariant.Dark);
    }

    /// <summary>Switches the palette (and the Fluent theme variant) live.</summary>
    public static void Apply(ThemeVariant variant)
    {
        Dictionary<string, Color> colors = variant == ThemeVariant.Light ? Light : Dark;
        foreach (string key in Keys)
        {
            if (Brushes.TryGetValue(key, out SolidColorBrush? brush) && colors.TryGetValue(key, out Color c))
            {
                brush.Color = c;
            }
        }

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = variant;
        }
    }
}
