using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Owns the app's palette brushes and swaps them between a dark and a light
///  theme — and, since M78, between the classic and the modern style — at
///  runtime.
///
///  <para>The brushes are created once and registered in
///  <see cref="Application.Resources"/>; views capture those instances. To
///  switch theme we mutate each brush's <see cref="SolidColorBrush.Color"/> in
///  place, so every control referencing a brush repaints live — no need for
///  DynamicResource bindings throughout the UI.</para>
///
///  <para>The two axes are orthogonal: <see cref="AppStyle"/> x
///  <see cref="ThemeVariant"/> gives four palettes, and every one of the
///  <see cref="Keys"/> must exist in all four. A missing key is silent —
///  <c>Brush(...)</c> falls back to black and <c>B(...)</c> to null — which is
///  the M62 class of bug that cost two milestones; <see cref="Palette"/> is the
///  only place a dictionary is chosen, and the loop below skips nothing.</para>
/// </summary>
public static class ThemeManager
{
    // Resource keys used across the app (see the B(...) helpers in the views).
    private static readonly string[] Keys =
    [
        "App.Window", "App.Panel", "App.PanelAlt", "App.Toolbar", "App.Border", "App.Rule",
        "App.Text", "App.TextDim", "App.Accent", "App.AccentFill", "App.Selection", "App.GraphGreen",
        "App.Control", "App.Foreground", "App.PanelBackground",
        "App.DiffAdded", "App.DiffRemoved",
        "App.TokenKeyword", "App.TokenString", "App.TokenComment",
        "App.TokenNumber", "App.TokenPreprocessor",
        "App.ConsoleBackground", "App.ConsoleForeground",
        "App.RepoStateClean", "App.RepoStateDirty", "App.RepoStateDirtySubmodules",
        "App.RepoStateMixed", "App.RepoStateStaged", "App.RepoStateUntrackedOnly",
        "App.RefPillBg", "App.RefBranch", "App.RefRemote", "App.RefTag", "App.RefNote",
        "App.AuthoredTint",
        "App.Link",
        "App.HoverRow", "App.Hover", "App.Pressed", "App.BorderStrong",
        "App.IconGreen", "App.IconRed", "App.IconBlue",
        "App.IconAmber", "App.IconPurple", "App.IconCyan",
    ];

    // ------------------------------------------------------------------
    //  CLASSIC — the palette as it stood before M77 (commit a38eb4ab4).
    //  Values are verbatim, comments included where they record a measurement:
    //  they are the reasoning of M62/M67/M70 and re-deriving them would only
    //  lose it. The two keys that did not exist yet, App.AccentFill and
    //  App.Link, are new here and carry their own derivation.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, Color> ClassicDark = new()
    {
        ["App.Window"] = Color.Parse("#1E1E1E"),
        ["App.Panel"] = Color.Parse("#252526"),
        ["App.PanelAlt"] = Color.Parse("#2D2D30"),
        ["App.Toolbar"] = Color.Parse("#333337"),
        ["App.Border"] = Color.Parse("#3F3F46"),

        // DELIBERATELY the same value as App.Border in the Classic family, which is
        // the pre-M77 look and keeps its own line weight — the same standing decision
        // that left Classic's tabs and bar buttons alone. It cannot take the modern
        // seam anyway: App.Toolbar is LIGHTER than the rule there (#333337 against a
        // #323236 that would land at 1.20:1 on App.Panel), so a thinned line would
        // vanish on every toolbar in the family rather than merely quieten.
        ["App.Rule"] = Color.Parse("#3F3F46"),
        ["App.Text"] = Color.Parse("#DCDCDC"),
        ["App.TextDim"] = Color.Parse("#9B9B9B"),
        ["App.Accent"] = Color.Parse("#007ACC"),

        // NEW in M78: the classic family never had a separate fill, because the
        // selected grid row is a M77-era surface. #007ACC itself does not serve:
        // it carries white at 4.51:1 but the row's other two inks fail on it —
        // the dimmed #DFECFA at 3.76:1 and, at 3.34:1, only just clears the 3:1
        // a non-text marker owes. #0068B0 is the same hue (204.5 deg against
        // 204.1) taken one step darker, and is the LIGHTEST value of that hue
        // which clears all three: white 5.82:1, #DFECFA 4.86:1, #9CF0B8 4.32:1.
        // Those are, to the hundredth, the same three figures the modern
        // #215BDD scores — the two styles differ in tint, not in legibility.
        ["App.AccentFill"] = Color.Parse("#0068B0"),
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

        // Syntax-highlighting ink (DiffView + FileTreeView). Both views carried the
        // same five literals "in the same key as the diff colours", tuned for a dark
        // background; the light theme therefore rendered highlighted code at
        // 1.53–2.97:1. Registering them per theme is the fix — and it is not a
        // toggle-only path: FileTreeView highlights unconditionally
        // (RenderContent(..., highlight: !binary)), so on the light theme the File
        // tree tab was unreadable by default with no way to turn it off.
        //
        // The dark trio+pair below are the ORIGINAL literals, except Comment and
        // Preprocessor, which are lifted by a barely-perceptible amount (ΔE*ab 4.8
        // and 3.1). Reason: the highlighter repaints +/- lines over a background
        // TINT (AddedTint/RemovedTint, alpha 0x28), so the effective background is
        // not #1E1E1E but #2A392C / #3C2A2A — and against those #7E9E7E measured
        // 4.12:1 and #C586C0 4.39:1, i.e. the DARK theme also failed AA on exactly
        // the lines syntax highlighting exists to colour. Every token now clears
        // 4.6:1 against the worst of the five surfaces it can land on
        // (App.Window, App.Panel, App.PanelAlt and the two diff tints).
        //
        // Kept apart on purpose, not just legible: the five are checked pairwise in
        // CIE L*a*b* under normal, deuteranope and protanope simulation, against the
        // five surfaces a token can land on (App.Window, App.Panel, App.PanelAlt and
        // the two diff tints composited over App.Window).
        //
        // This family's weak point was String↔Comment: ΔE 6.45 under protanopia — two
        // tokens a red-blind reader genuinely cannot tell apart, and the pair that
        // matters most, since string literals and comments are what a diff is full of.
        // The Modern dark family was re-solved for exactly this and left Classic
        // behind; Classic is solved here the same way, as a constrained family rather
        // than one colour at a time (moving Comment alone tops out at ΔE 15.6 and
        // turns it teal, which loses the token's identity).
        //
        // Constraints: hue within 14° of the value each token had, ΔE ≤ 16 from it,
        // contrast ≥ 4.6:1 on all five surfaces. The separation is bought with
        // LIGHTNESS, the only axis that survives simulation — the green/olive/rust
        // cluster collapses in hue for a colour-blind reader — so Number stays the
        // brightest of the five and Comment the most recessed, exactly as before.
        //
        //   pair                  before (norm/deut/prot)   after
        //   String↔Comment          36.96 / 14.05 /  6.45    37.92 / 30.52 / 25.52
        //   Keyword↔Preprocessor    34.22 / 23.43 / 16.72    30.64 / 33.65 / 24.54
        //   Comment↔Number          15.12 / 15.11 / 14.69    28.58 / 24.77 / 26.99
        //
        // Worst pair over all ten pairs and all three simulations: 6.45 → 24.54.
        ["App.TokenKeyword"] = Color.Parse("#8EA4FF"),
        ["App.TokenString"] = Color.Parse("#CE965F"),
        ["App.TokenComment"] = Color.Parse("#92A48F"),
        ["App.TokenNumber"] = Color.Parse("#BCE6B0"),
        ["App.TokenPreprocessor"] = Color.Parse("#C39DCA"),

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

        // The git-notes chip. It used to be the ONE badge with hard-coded colours — an
        // opaque brown fill carrying pale amber text — which made it the odd one out
        // on a light row, where every other badge is an outline pill on the pill
        // surface, and left its own text/fill pair at 5.34:1, the weakest contrast on
        // the row. Now it is an outline pill like the others, so it needs only an ink.
        // Violet, not another amber: the ink has to be told apart from Tag as well as
        // read, and it clears ΔE 48 from the nearest of the other three refs under
        // normal, deuteranope AND protanope simulation.
        ["App.RefNote"] = Color.Parse("#9B8FD6"),
        ["App.AuthoredTint"] = Color.Parse("#9B8FD6"),
        ["App.RefBranch"] = Color.Parse("#5FBF6B"),
        ["App.RefRemote"] = Color.Parse("#EE908A"),
        ["App.RefTag"] = Color.Parse("#D9A226"),

        // NEW in M78: hyperlink ink. The classic family had no link colour and the
        // call sites borrowed App.Accent, which M74 recorded as the open defect —
        // #007ACC measures 3.70 / 3.40 / 3.04 / 2.79:1 on Window / Panel / PanelAlt
        // / Toolbar, i.e. under AA on every surface a link lands on and under 3:1 on
        // the toolbar. "Classic" means the pre-M77 LOOK, not the inherited failure,
        // so the link gets its own value: the same 204 deg hue lightened until it
        // clears 4.5:1 on the worst of the four, 4.75:1 on the toolbar.
        ["App.Link"] = Color.Parse("#4DA6E8"),

        // NEW in M93. Row under the pointer in the revision grid. It used to reuse
        // App.PanelAlt, which IS the colour of every second row: hovering a dark row
        // changed nothing at all and hovering a light one just looked like the stripe.
        // So the hover row is the only row background with a HUE — App.Panel pulled
        // 10% toward #38BDF8 — which no stripe can be confused with. Held to AA for
        // both inks it carries: App.Text 9.33:1, App.TextDim 4.61:1, and 8.13:1 for
        // the green ref marker.
        ["App.HoverRow"] = Color.Parse("#27343B"),

        // Pointer-over / pressed surface for the flat toolbar buttons, which used to
        // borrow App.PanelAlt (hover) and App.Panel (pressed) — both DARKER than the
        // toolbar they sit on, so "under the pointer" read as a hole rather than a
        // lift. Same rule as ModernStyles' derived states: the surface pulled 10% and
        // 20% toward the ink, which inverts by itself between the two themes.
        // App.Text measures 7.07:1 on hover and 5.42:1 on pressed.
        ["App.Hover"] = Color.Parse("#444448"),
        ["App.Pressed"] = Color.Parse("#555558"),

        // NEW in M94. Outline for a control whose border is the ONLY thing that
        // delimits it — WCAG 1.4.11 asks 3:1 of a non-text indicator, and App.Border
        // measures 1.08:1 (modern dark) to 1.37:1 on the surfaces a control lands on.
        // In the CLASSIC family it is deliberately the SAME value as App.Border:
        // classic is defined as the look before M79, and a crisp outline round every
        // input would be a new look, not the old one. The strong value lives in the
        // modern families only, exactly like ModernStyles' own borderStrong.
        ["App.BorderStrong"] = Color.Parse("#3F3F46"),

        // The icon accents (M103). The classic style draws the 2015 PNGs and never
        // reads them, but an unregistered key keeps whatever the modern palette last
        // left in the brush, so the family is registered here too, at the modern
        // values — a switch to Classic and back therefore changes nothing.
        ["App.IconGreen"] = Color.Parse("#5BC46B"),
        ["App.IconRed"] = Color.Parse("#E06C6C"),
        ["App.IconBlue"] = Color.Parse("#5B9CFF"),
        ["App.IconAmber"] = Color.Parse("#E0A73C"),
        ["App.IconPurple"] = Color.Parse("#B197E1"),
        ["App.IconCyan"] = Color.Parse("#37B6C9"),
    };

    private static readonly Dictionary<string, Color> ClassicLight = new()
    {
        ["App.Window"] = Color.Parse("#F3F3F3"),
        ["App.Panel"] = Color.Parse("#FFFFFF"),
        ["App.PanelAlt"] = Color.Parse("#ECECEC"),
        ["App.Toolbar"] = Color.Parse("#E4E4E4"),
        ["App.Border"] = Color.Parse("#C4C4C4"),

        // Classic keeps its own line weight here too, see the dark block.
        ["App.Rule"] = Color.Parse("#C4C4C4"),
        ["App.Text"] = Color.Parse("#1E1E1E"),
        ["App.TextDim"] = Color.Parse("#6A6A6A"),
        ["App.Accent"] = Color.Parse("#007ACC"),

        // See the dark block. The fill is variant-invariant in the classic family
        // for the same reason App.Accent is: the classic accent was a single
        // #007ACC in both themes, and the inks the selected row carries (white,
        // #DFECFA, #9CF0B8) do not change with the variant either, so the same
        // #0068B0 satisfies both. The modern family splits the two because its
        // accent already differs per variant.
        ["App.AccentFill"] = Color.Parse("#0068B0"),
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

        // Light-theme syntax ink (see the dark block for the whole story). Each value
        // KEEPS its dark counterpart's hue (±5°) and darkens until it clears AA on the
        // worst light surface it can land on — the removed-line tint #F0DEDE, which is
        // darker than the white panel and is therefore the binding constraint, not
        // #FFFFFF. Measured minimum over {#FFFFFF, #F3F3F3, #ECECEC, #DEECDF,
        // #F0DEDE}: 5.48 keyword / 4.64 string / 4.82 comment / 8.33 number /
        // 5.35 preprocessor, up from 1.63 / 2.04 / 2.29 / 1.31 / 2.15. (Full
        // per-surface table in NOTES.md.)
        //
        // Comment stays the low-chroma grey-green it is in the dark theme, so it still
        // reads as the recessed token; Number is the darkest, which is what buys the
        // colour-blind separation described above.
        ["App.TokenKeyword"] = Color.Parse("#1B47DA"),
        ["App.TokenString"] = Color.Parse("#A94304"),
        ["App.TokenComment"] = Color.Parse("#536556"),
        ["App.TokenNumber"] = Color.Parse("#264517"),
        ["App.TokenPreprocessor"] = Color.Parse("#9B18A0"),

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

        // The git-notes chip. It used to be the ONE badge with hard-coded colours — an
        // opaque brown fill carrying pale amber text — which made it the odd one out
        // on a light row, where every other badge is an outline pill on the pill
        // surface, and left its own text/fill pair at 5.34:1, the weakest contrast on
        // the row. Now it is an outline pill like the others, so it needs only an ink.
        // Violet, not another amber: the ink has to be told apart from Tag as well as
        // read, and it clears ΔE 48 from the nearest of the other three refs under
        // normal, deuteranope AND protanope simulation.
        ["App.RefNote"] = Color.Parse("#5A2D8A"),
        ["App.AuthoredTint"] = Color.Parse("#5A2D8A"),
        ["App.RefBranch"] = Color.Parse("#256B29"),
        ["App.RefRemote"] = Color.Parse("#A83226"),
        ["App.RefTag"] = Color.Parse("#7E5800"),

        // NEW in M78: hyperlink ink, light half (see the dark block). The borrowed
        // #007ACC measured 4.51 / 4.06 / 3.82 / 3.55:1 on Panel / Window / PanelAlt /
        // Toolbar — AA only on pure white, which is the one surface a link rarely
        // sits alone on. #0067AF is the same hue darkened to the lightest value that
        // clears 4.5:1 everywhere: 5.90 / 5.32 / 4.99 / 4.64:1.
        ["App.Link"] = Color.Parse("#0067AF"),

        // NEW in M93 (see the dark block for the reasoning). Light half: the hue is
        // the same #38BDF8, mixed into white at 22% — the darkest step that keeps the
        // dimmed ink at AA (App.TextDim 4.55:1) and the green marker at the 4.54:1 it
        // already had on the stripe; App.Text 14.01:1.
        ["App.HoverRow"] = Color.Parse("#D3F0FD"),
        ["App.Hover"] = Color.Parse("#D0D0D0"),
        ["App.Pressed"] = Color.Parse("#BCBCBC"),

        // Classic keeps its own separator value here, see the dark block.
        ["App.BorderStrong"] = Color.Parse("#C4C4C4"),

        // The icon accents (M103, see the classic dark block): unused by this style,
        // registered so the brushes never keep the other palette's values.
        ["App.IconGreen"] = Color.Parse("#217A32"),
        ["App.IconRed"] = Color.Parse("#B03A3A"),
        ["App.IconBlue"] = Color.Parse("#1D4ED8"),
        ["App.IconAmber"] = Color.Parse("#8A5900"),
        ["App.IconPurple"] = Color.Parse("#7541D6"),
        ["App.IconCyan"] = Color.Parse("#0F6C7A"),
    };

    // ------------------------------------------------------------------
    //  MODERN — the M77 palette.
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, Color> ModernDark = new()
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
        //     (the last pair is gone since M95, see App.Toolbar below)
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

        // M95: the same value as App.Panel. The ramp above gave the bars their own
        // step (#2F3038), and it showed: the menu strip and the toolbar were a pale
        // band across the top of a dark window, and the menu row was split in two
        // tones because the Menu control paints its own App.Panel background while
        // the strip behind it painted App.Toolbar. Modern chrome is flat — the bar is
        // the same surface as the content and the 1px bottom rule on the strip is
        // what says where it ends, exactly as App.Control (input surfaces) has been
        // the same value as App.Panel since M77. The lightest surface disappearing
        // only RAISES every contrast figure measured against it (App.TextDim
        // 4.70 -> 5.75:1, App.Border 1.23 -> 1.58:1), so nothing below is invalidated
        // downward. Classic keeps its own #333337 bar: that band IS the 2015 look.
        ["App.Toolbar"] = Color.Parse("#1C1D21"),
        ["App.Border"] = Color.Parse("#3C3E47"),

        // The SEAM between two panes of one window — a toolbar and the list under it,
        // two columns either side of a splitter — as opposed to App.Border, which is
        // the EDGE OF A THING: a flyout card floating over the content, a box drawn
        // round a group in a dialog. VS Code keeps the same two apart for the same
        // reason (panel.border #2B2B2B against menu.border #454545), and measuring a
        // screenshot of it is where these numbers come from: its seam reads 1.16:1 on
        // the editor surface and 1.25:1 on the panel one. App.Border was drawing them
        // at 1.58:1 here — a third again as strong as the reference — which is what
        // "make the lines thinner" turned out to mean once measured.
        //
        // 1.19:1 on App.Panel and on App.Toolbar (the same value in this family),
        // 1.30:1 on App.Window, 1.06:1 on App.PanelAlt. The spread is the reference's
        // own: one colour read against whichever of two surfaces it happens to divide.
        ["App.Rule"] = Color.Parse("#2A2B32"),

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

        // The git-notes chip. It used to be the ONE badge with hard-coded colours — an
        // opaque brown fill carrying pale amber text — which made it the odd one out
        // on a light row, where every other badge is an outline pill on the pill
        // surface, and left its own text/fill pair at 5.34:1, the weakest contrast on
        // the row. Now it is an outline pill like the others, so it needs only an ink.
        // Violet, not another amber: the ink has to be told apart from Tag as well as
        // read, and it clears ΔE 48 from the nearest of the other three refs under
        // normal, deuteranope AND protanope simulation.
        ["App.RefNote"] = Color.Parse("#9B8FD6"),
        ["App.AuthoredTint"] = Color.Parse("#9B8FD6"),
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

        // NEW in M93. The hovered row is the only row background with a hue: App.Panel
        // pulled 14% toward #38BDF8. App.PanelAlt cannot serve — it is the colour of
        // every second row, so hover on an odd row was literally invisible. AA on both
        // inks (App.Text 10.30:1, App.TextDim 4.68:1) and 8.30:1 on the ref marker,
        // and 1.14:1 against the alternate stripe, which is a hue change on top.
        ["App.HoverRow"] = Color.Parse("#20333F"),

        // Flat-toolbar-button states, the toolbar pulled 10% / 20% toward the ink —
        // the same derivation ModernStyles uses for every other control, so a toolbar
        // button and a real Button now lift by the same amount. Before this they
        // borrowed App.PanelAlt / App.Panel, i.e. two surfaces DARKER than the toolbar.
        ["App.Hover"] = Color.Parse("#41424A"),
        ["App.Pressed"] = Color.Parse("#53545B"),

        // NEW in M94. App.Border pulled 45% toward the ink — the same derivation
        // ModernStyles applies to the Fluent border keys, kept here as a palette entry
        // because ~39 call sites draw their own control chrome and cannot reach a
        // brush that lives inside that file. 45% is the FLOOR: it measures 3.30:1
        // against the worst of the five surfaces a control border lands on (Window,
        // Panel, PanelAlt, Toolbar, Selection), where 40% gives 2.92:1. App.Border
        // itself measures 1.08:1 there and stays what it is — a separator.
        ["App.BorderStrong"] = Color.Parse("#88898F"),

        // NEW in M103: the icon accent family, i.e. the colours the modern vector
        // glyphs are painted with when "Colour the icons" is on. Six roles, not one per
        // icon: create/add, destroy/remove, transfer, stash-and-tag, staging, and the
        // structural refs. The rest of the set stays App.Text, so colour marks the
        // icons that carry a MEANING and the chrome stays quiet.
        //
        // Non-text markers owe 3:1; each of these clears 4.5:1 against the worst of
        // App.Window / App.Panel / App.PanelAlt, so they hold on the toolbar, in a menu
        // and on a hovered row alike. Red and green — the pair red-green colour
        // blindness collapses — are separated by luminance as well (4.63:1 against
        // 6.77:1), and the glyphs they paint already differ in SHAPE (a plus against a
        // minus, a bin against a check): the colour reinforces the meaning, it never
        // carries it alone.
        ["App.IconGreen"] = Color.Parse("#5BC46B"),   // 6.77:1
        ["App.IconRed"] = Color.Parse("#E06C6C"),     // 4.63:1
        ["App.IconBlue"] = Color.Parse("#5B9CFF"),    // 5.42:1
        ["App.IconAmber"] = Color.Parse("#E0A73C"),   // 6.92:1
        ["App.IconPurple"] = Color.Parse("#B197E1"),  // 5.94:1
        ["App.IconCyan"] = Color.Parse("#37B6C9"),    // 6.17:1
    };

    private static readonly Dictionary<string, Color> ModernLight = new()
    {
        // The light ramp, same treatment (M77). App.Panel was #FFFFFF — pure paper
        // white, the light-theme half of the same 2015 signature — and is now #FDFDFD:
        // still the lightest surface in the theme and still reads as white, but it is
        // no longer the clipping ceiling, which is what let the whole ramp be spaced
        // deliberately instead of hanging off the top of the range.
        //
        // Same two structural checks as the dark ramp. Adjacent separation
        // 1.110/1.181/1.076 -> 1.089/1.169/1.085 (the last pair gone since M95), and
        // the border holds its old
        // visibility exactly: 1.57/1.74/1.48/1.37 -> 1.60/1.74/1.49/1.37 against
        // Window/Panel/PanelAlt/Toolbar. The brief's suggested #DFDFE3 border measured
        // 1.31 on the panel — a 25% loss that made panel edges vanish — so the border
        // follows the measurement and stays near its old weight, only cooled.
        ["App.Window"] = Color.Parse("#F3F3F6"),
        ["App.Panel"] = Color.Parse("#FDFDFD"),
        ["App.PanelAlt"] = Color.Parse("#EBEBEF"),

        // M95, light half of the same move: flat chrome, App.Toolbar = App.Panel. The
        // grey band was less loud here than in the dark theme, but the two-tone menu
        // row was identical, and a theme pair whose chrome is flat on one side only is
        // two designs. Contrasts against this surface can only rise (App.TextDim
        // 4.67 -> 5.29:1).
        ["App.Toolbar"] = Color.Parse("#FDFDFD"),
        ["App.Border"] = Color.Parse("#C2C2CB"),

        // Light half of the seam (see the dark block). 1.20:1 on App.Panel and
        // App.Toolbar, 1.10:1 on App.Window, 1.03:1 on App.PanelAlt.
        ["App.Rule"] = Color.Parse("#E8E8EC"),

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

        // The git-notes chip. It used to be the ONE badge with hard-coded colours — an
        // opaque brown fill carrying pale amber text — which made it the odd one out
        // on a light row, where every other badge is an outline pill on the pill
        // surface, and left its own text/fill pair at 5.34:1, the weakest contrast on
        // the row. Now it is an outline pill like the others, so it needs only an ink.
        // Violet, not another amber: the ink has to be told apart from Tag as well as
        // read, and it clears ΔE 48 from the nearest of the other three refs under
        // normal, deuteranope AND protanope simulation.
        ["App.RefNote"] = Color.Parse("#5A2D8A"),
        ["App.AuthoredTint"] = Color.Parse("#5A2D8A"),
        ["App.RefBranch"] = Color.Parse("#256A29"),
        ["App.RefRemote"] = Color.Parse("#A93226"),
        ["App.RefTag"] = Color.Parse("#7B5600"),

        // Hyperlink ink (see the dark block). 5.50:1 on the worst of App.Window /
        // App.Panel / App.PanelAlt / App.Toolbar, against 4.06:1 for the App.Accent the
        // links borrow today.
        ["App.Link"] = Color.Parse("#1A4FC4"),

        // NEW in M93 (see the dark block). Light half: #38BDF8 mixed into App.Panel at
        // 22%. App.TextDim 5.03:1, the green marker 4.46:1 (it was 4.51:1 on the
        // stripe), App.Text 14.30:1.
        ["App.HoverRow"] = Color.Parse("#D2EFFC"),
        ["App.Hover"] = Color.Parse("#CECED4"),
        ["App.Pressed"] = Color.Parse("#BABAC0"),

        // NEW in M94 (see the dark block). 3.32:1 on the worst of the five surfaces,
        // against 1.32:1 for App.Border.
        ["App.BorderStrong"] = Color.Parse("#77777E"),

        // NEW in M103 (see the dark block). Same six roles taken to values that clear
        // 4.5:1 against the light surfaces, which on white means darkened hues rather
        // than the dark theme's lightened ones.
        ["App.IconGreen"] = Color.Parse("#217A32"),   // 4.54:1
        ["App.IconRed"] = Color.Parse("#B03A3A"),     // 5.03:1
        ["App.IconBlue"] = Color.Parse("#1D4ED8"),    // 5.64:1
        ["App.IconAmber"] = Color.Parse("#8A5900"),   // 5.03:1
        ["App.IconPurple"] = Color.Parse("#7541D6"),  // 5.07:1
        ["App.IconCyan"] = Color.Parse("#0F6C7A"),    // 5.12:1
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    /// <summary>The style currently applied.</summary>
    public static AppStyle CurrentStyle { get; private set; } = AppStyle.Modern;

    /// <summary>The theme variant currently applied.</summary>
    public static ThemeVariant CurrentVariant { get; private set; } = ThemeVariant.Dark;

    /// <summary>
    ///  Whether the modern vector glyphs are painted in their accent role
    ///  (<see cref="Icons.AccentOf"/>) rather than all in <c>App.Text</c>.
    ///
    ///  <para>Only the modern style is affected: the classic style draws the 2015
    ///  PNGs, whose colours are baked into the bitmaps and were never ours to
    ///  choose. Turning it off is lossless — no icon means anything by its colour
    ///  alone (see the role table in <see cref="Icons"/>).</para>
    /// </summary>
    public static bool ColoredIcons { get; private set; } = true;

    /// <summary>
    ///  Switches icon colouring live. Raises <see cref="StyleChanged"/>, which is
    ///  what every glyph on screen listens to — the icons repaint in place, nothing
    ///  is rebuilt, exactly as for a style switch.
    /// </summary>
    public static void SetColoredIcons(bool colored)
    {
        if (colored == ColoredIcons)
        {
            return;
        }

        ColoredIcons = colored;
        StyleChanged?.Invoke();
    }

    /// <summary>
    ///  Raised after a style change has been fully applied — palette mutated,
    ///  control styles swapped — or after <see cref="SetColoredIcons"/> changed how
    ///  the glyphs are tinted. Listeners only have to invalidate themselves.
    ///
    ///  <para>This is a STATIC event: anything that subscribes from a control
    ///  must unsubscribe when it detaches, or a recycling list will grow the
    ///  invocation list without bound (see <c>GlyphIcon</c> in IconLoader).</para>
    /// </summary>
    public static event Action? StyleChanged;

    private static Dictionary<string, Color> Palette(AppStyle style, ThemeVariant variant)
    {
        bool light = variant == ThemeVariant.Light;
        return style == AppStyle.Classic
            ? (light ? ClassicLight : ClassicDark)
            : (light ? ModernLight : ModernDark);
    }

    /// <summary>Creates the palette brushes and registers them; applies the modern dark theme.</summary>
    public static void Initialize(Application app)
    {
        Dictionary<string, Color> seed = Palette(AppStyle.Modern, ThemeVariant.Dark);
        foreach (string key in Keys)
        {
            SolidColorBrush brush = new(seed[key]);
            Brushes[key] = brush;
            app.Resources[key] = brush;
        }

        Apply(ThemeVariant.Dark, AppStyle.Modern);
    }

    /// <summary>Switches the palette variant live, keeping the current style.</summary>
    public static void Apply(ThemeVariant variant) => Apply(variant, CurrentStyle);

    /// <summary>Switches the palette variant and the style live.</summary>
    public static void Apply(ThemeVariant variant, AppStyle style)
    {
        bool styleChanged = style != CurrentStyle;

        CurrentVariant = variant;
        CurrentStyle = style;

        Dictionary<string, Color> colors = Palette(style, variant);
        foreach (string key in Keys)
        {
            if (Brushes.TryGetValue(key, out SolidColorBrush? brush) && colors.TryGetValue(key, out Color c))
            {
                // Mutated in place on purpose: replacing the instance would strand
                // every view (and ManagedFileChooserTheming) that captured it.
                brush.Color = c;
            }
        }

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = variant;

            if (styleChanged)
            {
                ModernStyles.Apply(app, style);
            }
        }

        if (styleChanged)
        {
            StyleChanged?.Invoke();
        }
    }
}
