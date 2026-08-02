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
        "App.Text", "App.TextDim", "App.Accent", "App.AccentFill", "App.Selection", "App.GraphGreen",
        "App.Control", "App.Foreground", "App.PanelBackground",
        "App.DiffAdded", "App.DiffRemoved",
        "App.TokenKeyword", "App.TokenString", "App.TokenComment",
        "App.TokenNumber", "App.TokenPreprocessor",
        "App.ConsoleBackground", "App.ConsoleForeground",
        "App.RepoStateClean", "App.RepoStateDirty", "App.RepoStateDirtySubmodules",
        "App.RepoStateMixed", "App.RepoStateStaged", "App.RepoStateUntrackedOnly",
        "App.RefPillBg", "App.RefBranch", "App.RefRemote", "App.RefTag",
        "App.Link",
    ];

    private static readonly Dictionary<string, Color> Dark = new()
    {
        // ---- the neutral ramp (M77) ----
        // The old ramp was VS Code 2015's: #1E1E1E / #252526 / #2D2D30 / #333337,
        // perfectly neutral greys with #007ACC on top. Neutral-to-the-byte greys are
        // what dated the window most after the icons, so every surface now carries a
        // faint cool cast (blue channel 3-9 above red) and the ramp is spaced to a
        // measured rule rather than by eye.
        //
        // Two structural numbers were held at or above the old ones, because they are
        // what makes the panel hierarchy readable and they are easy to lose while
        // chasing a "softer" look:
        //   adjacent surface separation  Window->Panel 1.089 -> 1.084,
        //     Panel->PanelAlt 1.115 -> 1.131, PanelAlt->Toolbar 1.091 -> 1.135
        //   Border against each surface  1.60/1.47/1.31/1.20 -> 1.71/1.58/1.40/1.23
        // The first draft of this ramp (window #17181B, border #2E3037, per the brief)
        // measured 1.066/1.075/1.084 and a border at 1.26 on the panel: the surfaces
        // collapsed into one flat mass and the borders nearly vanished. The values
        // below are the measurement's answer, not the draft's.
        //
        // Every ink family below was re-derived against these surfaces from scratch:
        // changing the ramp invalidates every contrast figure M67 and M70 recorded.
        ["App.Window"] = Color.Parse("#141518"),
        ["App.Panel"] = Color.Parse("#1C1D21"),
        ["App.PanelAlt"] = Color.Parse("#26272D"),
        ["App.Toolbar"] = Color.Parse("#2F3038"),
        ["App.Border"] = Color.Parse("#3C3E47"),

        // Text 8.90 -> 10.34:1, TextDim 4.39 -> 4.70:1, each against the worst of the
        // six surfaces it lands on. TextDim used to fail AA on the diff tints (4.39).
        ["App.Text"] = Color.Parse("#E4E4E7"),
        ["App.TextDim"] = Color.Parse("#9A9AA3"),

        // #007ACC is the other half of the 2015 signature. #3B82F6 is the same
        // "primary blue" role at a contemporary hue; it is a fill/graph colour, not
        // body text, so it is held to 3:1 (3.57:1 on the toolbar, its worst surface)
        // and App.Link below carries the text-grade blue.
        ["App.Accent"] = Color.Parse("#3B82F6"),

        // Accent as a FILL, kept apart from the accent as ink because the two roles
        // pull the tint in opposite directions and no single blue serves both: at
        // #3B82F6 the accent reads 4.58:1 as ink on App.Panel but only 3.68:1 under
        // the white text of a selected grid row, and every blue that carries white
        // text falls under 4.5:1 as ink (#2563EB: 5.17 fill / 3.26 ink). Same split
        // as App.Link. This value is the lightest that clears all three inks the
        // selected row can carry — white 5.82:1, the dimmed #DFECFA 4.86:1, and the
        // #9CF0B8 marker 4.32:1 (non-text, needs 3).
        ["App.AccentFill"] = Color.Parse("#215BDD"),
        ["App.Selection"] = Color.Parse("#1E3A5F"),
        ["App.GraphGreen"] = Color.Parse("#4EC9B0"),

        // Input surfaces (text boxes, pickers). Same value as App.Panel: the key was
        // used by ~20 call sites without ever being registered, so Brush("App.Control",
        // Brushes.Black) silently pinned a black surface that never followed the theme —
        // unreadable in the light theme, where the text stays App.Text.
        ["App.Control"] = Color.Parse("#1C1D21"),

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
        ["App.Foreground"] = Color.Parse("#E4E4E7"),
        ["App.PanelBackground"] = Color.Parse("#26272D"),

        // Diff ink. The dark values are the ones DiffView already paints its own added
        // and removed lines with (#6AC776 / #E06C6C), so the CommitDialog diff pane
        // stops drifting to LimeGreen/OrangeRed and matches the real diff view.
        //
        // Both survive the new ramp unchanged, and one of them stops failing on the way:
        // measured against the surface each actually lands on — its own diff tint —
        // Added went 5.84 -> 6.55:1 and Removed 4.20 -> 4.62:1. Removed was below AA
        // on the old ramp; nothing had to move, the darker window fixed it.
        ["App.DiffAdded"] = Color.Parse("#6AC776"),
        ["App.DiffRemoved"] = Color.Parse("#E06C6C"),

        // Syntax-highlighting ink (DiffView + FileTreeView). Both views carried the
        // same five literals "in the same key as the diff colours", tuned for a dark
        // background; the light theme therefore rendered highlighted code at
        // 1.53–2.97:1. Registering them per theme is the fix — and it is not a
        // toggle-only path: FileTreeView highlights unconditionally
        // (RenderContent(..., highlight: !binary)), so on the light theme the File
        // tree tab was unreadable by default with no way to turn it off.
        //
        // The highlighter repaints +/- lines over a background TINT (AddedTint /
        // RemovedTint, DiffView.cs:73-74, alpha 0x28 and theme-INVARIANT), so the
        // effective background of a coloured diff line is not App.Window. The tints
        // composite over App.Window specifically — DiffView.cs:340-343 backs the diff
        // ScrollViewer with it — so on the new ramp they are #213127 / #342325, where
        // they were #2A392C / #3C2A2A. Every token clears 4.6:1 against the worst of
        // the five surfaces it can land on: App.Window, App.Panel, App.PanelAlt and
        // those two tints. Measuring against #1E1E1E alone gives the wrong number.
        //
        // Kept apart on purpose, not just legible: the five are checked pairwise in
        // CIE L*a*b* under normal, deuteranope and protanope simulation. M70 recorded
        // the dark family's weak point as String↔Comment under protanopia — two tokens
        // a red-blind reader could not tell apart — and left it unfixed. It is fixed
        // here. The five were re-solved as a constrained family: hue held within ±12°
        // of the value it had (so every token keeps its identity), contrast ≥ 4.6:1 on
        // the worst of the five surfaces, and the pairwise minimum ΔE maximised across
        // all three simulations. The whole dark family moved by a mean ΔE of 7.3 —
        // individually near-imperceptible — and the worst pair went from ΔE 6.30 to
        // 24.06. The gain is bought with LIGHTNESS, not hue, which is the only axis
        // that survives simulation: the green/olive/rust cluster collapses in hue for
        // a colour-blind reader, so Number stays the brightest of the five and Comment
        // the most recessed, exactly as before.
        //
        //   pair                  before (norm/deut/prot)   after
        //   String↔Comment          36.96 / 15.77 /  6.30    41.70 / 33.66 / 24.06
        //   Comment↔Number          15.12 / 15.49 / 14.89    24.07 / 24.45 / 24.30
        //   Keyword↔Preprocessor    34.22 / 25.27 / 16.80    37.67 / 35.22 / 24.35
        //
        // Pushing the minimum past ~24 was possible but not taken: beyond that the
        // solver buys separation by driving Number to a near-white neon (#D3FDA1) and
        // the family stops looking like syntax highlighting.
        ["App.TokenKeyword"] = Color.Parse("#79A9F9"),
        ["App.TokenString"] = Color.Parse("#C48B54"),
        ["App.TokenComment"] = Color.Parse("#85A08A"),
        ["App.TokenNumber"] = Color.Parse("#C6DDB4"),
        ["App.TokenPreprocessor"] = Color.Parse("#C78ABD"),

        // Commit-button accents, one per upstream RepoState (RepoStateVisualiser).
        // MainToolbar paints them as the Commit CAPTION's foreground, so they are
        // normal text on App.Toolbar and owe 4.6:1, not 3:1.
        //
        // "On the dark toolbar they already read" — the note this block used to carry —
        // was never true for two of the six, and M67 only ever checked the light theme.
        // Measured on the old dark toolbar #333337: Clean 3.64:1 and UntrackedOnly
        // 2.87:1, both below AA, the second below even 3:1. Those two are lifted (each
        // keeps its hue, only lightness moves); the other four already cleared and are
        // untouched upstream values.
        ["App.RepoStateClean"] = Color.Parse("#A2A2AC"),            // 3.64 -> 5.19:1
        ["App.RepoStateDirty"] = Color.Parse("#FFA07A"),            // 6.33 -> 6.60:1
        ["App.RepoStateDirtySubmodules"] = Color.Parse("#FFA500"),  // 6.37 -> 6.64:1
        ["App.RepoStateMixed"] = Color.Parse("#E6A700"),            // 5.93 -> 6.18:1
        ["App.RepoStateStaged"] = Color.Parse("#87CEFA"),           // 7.33 -> 7.65:1
        ["App.RepoStateUntrackedOnly"] = Color.Parse("#B197E1"),    // 2.87 -> 5.24:1

        // The transcript boxes of CleanupDialog and CloneDialog. M62 left these two
        // unregistered on the grounds that a theme-invariant dark terminal matched the
        // process dialog's fixed beige. Measuring the family says otherwise: of the nine
        // read-only monospace output surfaces pinned by TextBoxSurface, seven already
        // read App.Panel/App.PanelAlt + App.Text, and only the process dialog is
        // deliberately fixed — and its beige (#ECE9D8) sits 1.10:1 from the light
        // window, so it blends there, whereas #111111 was a black slab in a light
        // dialog. Aliased to App.PanelAlt/App.Text, matching OutputView and
        // SubmodulesDialog, the closest siblings (raw git output in a monospace box).
        // Contrast stays far above threshold: 12.24:1 before, now 11.73:1 dark /
        // 14.44:1 light (was 10.01 / 14.11 on the old ramp).
        ["App.ConsoleBackground"] = Color.Parse("#26272D"),
        ["App.ConsoleForeground"] = Color.Parse("#E4E4E7"),

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
        // The dark trio is a light tint of each hue, re-normalised onto the new pill
        // background at 6.50 / 6.50 / 6.53:1 — deliberately one narrow band, so no
        // pill reads as the weak one of the family. Left unchanged on the darker pill
        // they would have drifted up to 7.2-7.3:1 while the light trio sat at 6.4-6.6,
        // so the two themes are pinned to the same band instead of each drifting on
        // its own.
        ["App.RefPillBg"] = Color.Parse("#1C1D21"),
        ["App.RefBranch"] = Color.Parse("#49B656"),
        ["App.RefRemote"] = Color.Parse("#EC837D"),
        ["App.RefTag"] = Color.Parse("#CC9924"),

        // Hyperlink ink. Registered by M77; the residue M74 left open was that links
        // borrowed App.Accent and measured 3.70:1 dark / 4.06:1 light, under AA on the
        // panels and dialogues they land on (e.g. ResolveConflictsDialog.cs:290).
        // App.Accent is a fill colour and cannot serve both jobs — #3B82F6 is 3.57:1 on
        // the toolbar — so link text gets its own value, held to 4.5:1 against the worst
        // of App.Window / App.Panel / App.PanelAlt / App.Toolbar. 4.78:1 here.
        // NOTE: this only registers the key. The call sites still read App.Accent.
        ["App.Link"] = Color.Parse("#5B9CFF"),
    };

    private static readonly Dictionary<string, Color> Light = new()
    {
        // The light ramp, same treatment (M77). App.Panel was #FFFFFF — pure paper
        // white, the light-theme half of the same 2015 signature — and is now #FDFDFD:
        // still the lightest surface in the theme and still reads as white, but it is
        // no longer the clipping ceiling, which is what let the whole ramp be spaced
        // deliberately instead of hanging off the top of the range.
        //
        // Same two structural checks as the dark ramp. Adjacent separation
        // 1.110/1.181/1.076 -> 1.089/1.169/1.085, and the border holds its old
        // visibility exactly: 1.57/1.74/1.48/1.37 -> 1.60/1.74/1.49/1.37 against
        // Window/Panel/PanelAlt/Toolbar. The brief's suggested #DFDFE3 border measured
        // 1.31 on the panel — a 25% loss that made panel edges vanish — so the border
        // follows the measurement and stays near its old weight, only cooled.
        ["App.Window"] = Color.Parse("#F3F3F6"),
        ["App.Panel"] = Color.Parse("#FDFDFD"),
        ["App.PanelAlt"] = Color.Parse("#EBEBEF"),
        ["App.Toolbar"] = Color.Parse("#E2E2E8"),
        ["App.Border"] = Color.Parse("#C2C2CB"),

        // Text 12.87 -> 13.27:1, TextDim 4.17 -> 4.67:1 on the worst of the six
        // surfaces. TextDim failed AA on the removed-line tint before (4.17).
        ["App.Text"] = Color.Parse("#1B1B1F"),
        ["App.TextDim"] = Color.Parse("#62626B"),

        ["App.Accent"] = Color.Parse("#1D4ED8"),

        // See the dark block. The light accent is already dark enough to carry white
        // text (6.70:1), so fill and ink coincide here.
        ["App.AccentFill"] = Color.Parse("#1D4ED8"),
        ["App.Selection"] = Color.Parse("#CFE0F8"),
        ["App.GraphGreen"] = Color.Parse("#1E7D5A"),
        ["App.Control"] = Color.Parse("#FDFDFD"),
        ["App.Foreground"] = Color.Parse("#1B1B1F"),
        ["App.PanelBackground"] = Color.Parse("#EBEBEF"),

        // Light-theme diff ink. The dark greens/reds are unreadable on a white panel
        // (#6AC776 → 2.09:1, #E06C6C → 3.22:1), so they darken the way App.GraphGreen
        // already does (#4EC9B0 → #1E7D5A). App.DiffAdded reuses that very value;
        // App.DiffRemoved is the same brick-red hue as #E06C6C/#CE5C5C taken darker,
        // because the palette registers no red at all and #CE5C5C only reaches 3.95:1.
        //
        // M67 measured these on #FFFFFF (5.08:1 and 5.98:1) — the wrong surface. A
        // DiffAdded glyph lands on the ADDED-LINE TINT, and there #1E7D5A was only
        // 4.15:1: the same trap that block was written to escape, one step short.
        // Re-derived against {Window, Panel, PanelAlt, own tint}: Added 4.15 -> 4.68:1
        // (darkened a hair), Removed 4.61 -> 4.62:1 (already cleared, untouched).
        ["App.DiffAdded"] = Color.Parse("#1C7454"),
        ["App.DiffRemoved"] = Color.Parse("#B03A3A"),

        // Light-theme syntax ink (see the dark block for the whole story). Each value
        // KEEPS its dark counterpart's hue and darkens until it clears AA on the worst
        // light surface it can land on — the removed-line tint, now #F0DEE0, which is
        // darker than the panel and is therefore the binding constraint, not the panel.
        // Measured minimum over {#FDFDFD, #F3F3F6, #EBEBEF, #DEECE2, #F0DEE0}:
        // 5.57 keyword / 4.70 string / 4.80 comment / 9.77 number / 5.33 preprocessor.
        //
        // This family was already the strong one — M70 left it at ΔE ≥ 17.6 across the
        // three simulations — so it barely had to move: mean drift ΔE 2.0, and the
        // worst pair (String↔Number under protanopia) goes 17.62 -> 22.38. Comment
        // stays the low-chroma grey-green it is in the dark theme, so it still reads as
        // the recessed token; Number is the darkest, which is what buys the
        // colour-blind separation. Both families now sit above ΔE 22 in every
        // simulation, where before they were 6.30 (dark) and 17.62 (light).
        ["App.TokenKeyword"] = Color.Parse("#1646D9"),
        ["App.TokenString"] = Color.Parse("#A64407"),
        ["App.TokenComment"] = Color.Parse("#506657"),
        ["App.TokenNumber"] = Color.Parse("#1B3A14"),
        ["App.TokenPreprocessor"] = Color.Parse("#9B19A1"),

        ["App.ConsoleBackground"] = Color.Parse("#EBEBEF"),
        ["App.ConsoleForeground"] = Color.Parse("#1B1B1F"),

        // The upstream RepoState colours were picked for the light WinForms toolbar of
        // Windows, yet MainToolbar paints them as the Commit CAPTION's foreground, where
        // they are normal text and need 4.5:1. Measured on the light toolbar (#E4E4E4)
        // they ranged from 1.35:1 (Staged) to 3.44:1 (UntrackedOnly) — four of six below
        // even 3:1. These keep each hue and darken it to just over 4.6:1 there.
        //
        // The new toolbar is a shade darker than #E4E4E4, which cost every one of them
        // a few hundredths; Mixed (4.65 -> 4.58) and UntrackedOnly (4.64 -> 4.57) fell
        // just under AA and are nudged back. Clean is only re-tinted to match the ramp's
        // cool cast. The band stays deliberately tight, 4.63-4.75:1.
        ["App.RepoStateClean"] = Color.Parse("#61616A"),            // 4.73 -> 4.75:1
        ["App.RepoStateDirty"] = Color.Parse("#994F31"),            // 4.70 -> 4.63:1
        ["App.RepoStateDirtySubmodules"] = Color.Parse("#8A5900"),  // 4.71 -> 4.64:1
        ["App.RepoStateMixed"] = Color.Parse("#805D00"),            // 4.65 -> 4.67:1
        ["App.RepoStateStaged"] = Color.Parse("#366887"),           // 4.74 -> 4.67:1
        ["App.RepoStateUntrackedOnly"] = Color.Parse("#7541D6"),    // 4.64 -> 4.67:1

        // Ref pills, light theme (see the dark block for the whole story). Each hue is
        // kept and darkened until it clears AA on the pill: re-normalised from
        // 6.53 / 6.67 / 6.40 to 6.51 / 6.51 / 6.51:1, which with the dark trio's
        // 6.50 / 6.50 / 6.53 puts all six pills inside a 0.03 band across both themes —
        // no pill reads as the weak one of the family, in either theme. The tag is the
        // one that moved most historically (#B8860B was 3.25:1) because amber is the hue
        // that fights a light background hardest — at AA it necessarily lands on a dark
        // olive.
        ["App.RefPillBg"] = Color.Parse("#FDFDFD"),
        ["App.RefBranch"] = Color.Parse("#256A29"),
        ["App.RefRemote"] = Color.Parse("#A93226"),
        ["App.RefTag"] = Color.Parse("#7B5600"),

        // Hyperlink ink (see the dark block). 5.50:1 on the worst of App.Window /
        // App.Panel / App.PanelAlt / App.Toolbar, against 4.06:1 for the App.Accent the
        // links borrow today.
        ["App.Link"] = Color.Parse("#1A4FC4"),
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
