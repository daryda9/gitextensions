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
        "App.Control", "App.Foreground", "App.PanelBackground",
        "App.DiffAdded", "App.DiffRemoved",
        "App.ConsoleBackground", "App.ConsoleForeground",
        "App.RepoStateClean", "App.RepoStateDirty", "App.RepoStateDirtySubmodules",
        "App.RepoStateMixed", "App.RepoStateStaged", "App.RepoStateUntrackedOnly",
        "App.RefPillBg", "App.RefBranch", "App.RefRemote", "App.RefTag",
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

        // Aliases of an existing key, registered because the call sites already read
        // them (same M62 trap as App.Control: an unregistered key silently pins the
        // fallback, which does not follow the theme).
        //   App.Foreground      — synonym of App.Text used by CommitDialog (9 sites).
        //     Its fallback was Brushes.Gainsboro (#DCDCDC): correct by accident in the
        //     dark theme, 1.24:1 against the light window. Same value as App.Text, so
        //     the dark theme is pixel-identical to before.
        //   App.PanelBackground — synonym of App.PanelAlt used by CleanupDialog's
        //     confirmation bar; its #2A2A2E fallback held a dark bar under App.Text ink
        //     in the light theme (1.17:1). #2D2D30 vs #2A2A2E is 1.04:1, invisible.
        ["App.Foreground"] = Color.Parse("#DCDCDC"),
        ["App.PanelBackground"] = Color.Parse("#2D2D30"),

        // Diff ink. The dark values are the ones DiffView already paints its own added
        // and removed lines with (#6AC776 / #E06C6C), so the CommitDialog diff pane
        // stops drifting to LimeGreen/OrangeRed and matches the real diff view.
        ["App.DiffAdded"] = Color.Parse("#6AC776"),
        ["App.DiffRemoved"] = Color.Parse("#E06C6C"),

        // Commit-button accents, one per upstream RepoState (RepoStateVisualiser). The
        // dark values ARE the upstream ones: on the dark toolbar they already read.
        ["App.RepoStateClean"] = Color.Parse("#8A8A8A"),
        ["App.RepoStateDirty"] = Color.Parse("#FFA07A"),
        ["App.RepoStateDirtySubmodules"] = Color.Parse("#FFA500"),
        ["App.RepoStateMixed"] = Color.Parse("#E6A700"),
        ["App.RepoStateStaged"] = Color.Parse("#87CEFA"),
        ["App.RepoStateUntrackedOnly"] = Color.Parse("#8A63D2"),

        // The transcript boxes of CleanupDialog and CloneDialog. M62 left these two
        // unregistered on the grounds that a theme-invariant dark terminal matched the
        // process dialog's fixed beige. Measuring the family says otherwise: of the nine
        // read-only monospace output surfaces pinned by TextBoxSurface, seven already
        // read App.Panel/App.PanelAlt + App.Text, and only the process dialog is
        // deliberately fixed — and its beige (#ECE9D8) sits 1.10:1 from the light
        // window, so it blends there, whereas #111111 was a black slab in a light
        // dialog. Aliased to App.PanelAlt/App.Text, matching OutputView and
        // SubmodulesDialog, the closest siblings (raw git output in a monospace box).
        // Contrast stays far above threshold: 12.24:1 before, 10.01:1 dark / 14.11:1 light.
        ["App.ConsoleBackground"] = Color.Parse("#2D2D30"),
        ["App.ConsoleForeground"] = Color.Parse("#DCDCDC"),

        // The revision grid's three ref pills (RevisionGridView.BuildRefBadge): local
        // branch, remote-tracking branch, tag. Each value is the OUTLINE and the GLYPH
        // colour at once, on App.RefPillBg.
        //
        // They were hard-coded (#2E7D32 / #C0392B / #B8860B, "tuned toward the original
        // GitExtensions palette") and therefore theme-blind: those three values were
        // picked against a white pill and, measured on a real screenshot, scored
        // 5.13 / 5.44 / 3.25:1 in the light theme but 2.99 / 2.82 / 4.71:1 in the dark
        // one — so the dark theme actually failed WCAG AA on TWO of the three, not one.
        // Registering them per theme is the whole point: a single triple cannot serve
        // both backgrounds.
        //
        // The dark trio is a light tint of each hue, measured on #252526 at
        // 6.67 / 6.56 / 6.67:1 — deliberately one narrow band, so no pill reads as the
        // weak one of the family.
        ["App.RefPillBg"] = Color.Parse("#252526"),
        ["App.RefBranch"] = Color.Parse("#5FBF6B"),
        ["App.RefRemote"] = Color.Parse("#EE908A"),
        ["App.RefTag"] = Color.Parse("#D9A226"),
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
        ["App.Foreground"] = Color.Parse("#1E1E1E"),
        ["App.PanelBackground"] = Color.Parse("#ECECEC"),

        // Light-theme diff ink. The dark greens/reds are unreadable on a white panel
        // (#6AC776 → 2.09:1, #E06C6C → 3.22:1), so they darken the way App.GraphGreen
        // already does (#4EC9B0 → #1E7D5A). App.DiffAdded reuses that very value;
        // App.DiffRemoved is the same brick-red hue as #E06C6C/#CE5C5C taken darker,
        // because the palette registers no red at all and #CE5C5C only reaches 3.95:1.
        // Measured on #FFFFFF: 5.08:1 and 5.98:1.
        ["App.DiffAdded"] = Color.Parse("#1E7D5A"),
        ["App.DiffRemoved"] = Color.Parse("#B03A3A"),
        ["App.ConsoleBackground"] = Color.Parse("#ECECEC"),
        ["App.ConsoleForeground"] = Color.Parse("#1E1E1E"),

        // The upstream RepoState colours were picked for the light WinForms toolbar of
        // Windows, yet MainToolbar paints them as the Commit CAPTION's foreground, where
        // they are normal text and need 4.5:1. Measured on the light toolbar (#E4E4E4)
        // they ranged from 1.35:1 (Staged) to 3.44:1 (UntrackedOnly) — four of six below
        // even 3:1. These keep each hue and darken it to just over 4.6:1 there.
        ["App.RepoStateClean"] = Color.Parse("#636363"),
        ["App.RepoStateDirty"] = Color.Parse("#994F31"),
        ["App.RepoStateDirtySubmodules"] = Color.Parse("#8A5900"),
        ["App.RepoStateMixed"] = Color.Parse("#825E00"),
        ["App.RepoStateStaged"] = Color.Parse("#366887"),
        ["App.RepoStateUntrackedOnly"] = Color.Parse("#7743D6"),

        // Ref pills, light theme (see the dark block for the whole story). Each hue is
        // kept and darkened until it clears AA on the white pill: measured on a real
        // screenshot at 6.53 / 6.67 / 6.40:1, the same narrow band as the dark trio, so
        // the terna reads as one family in both themes. The tag is the one that moved
        // most (#B8860B was 3.25:1) because amber is the hue that fights a white
        // background hardest — at AA it necessarily lands on a dark olive.
        ["App.RefPillBg"] = Color.Parse("#FFFFFF"),
        ["App.RefBranch"] = Color.Parse("#256B29"),
        ["App.RefRemote"] = Color.Parse("#A83226"),
        ["App.RefTag"] = Color.Parse("#7E5800"),
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
