namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  How large the interface text is drawn, chosen from Settings → Appearance.
///
///  <para><b>This is the size of the app's chrome text, not a zoom.</b>
///  <see cref="UiSize.Normal"/> is the baseline the port is built at — upstream's own 12px
///  chrome (M81) — and each other member moves that one number. Everything Fluent
///  templates follows it: buttons, text boxes, check boxes, list and tree rows, tab
///  headers, tooltips, menus and combo dropdowns. The revision grid, the diff and the
///  file lists do <b>not</b> follow it: they set literal sizes from
///  <see cref="Metrics.Text"/> when they are built. See <see cref="UiScaling"/> for what
///  is written and for why the earlier layout-transform implementation was removed.</para>
///
///  <para><b>Why a closed list and not a slider.</b> A free scale invites values that land
///  text on fractional pixels for no benefit, and it has no name to put in a settings file
///  or a bug report. Four named steps cover the reason the option exists (the port reads
///  slightly larger or smaller than the user's other tools) without turning the window
///  into a zoom control.</para>
///
///  <para>Orthogonal to both <see cref="AppStyle"/> and
///  <see cref="Avalonia.Styling.ThemeVariant"/>: a font size is not a palette, so every
///  size renders correctly in Classic and Modern alike.</para>
/// </summary>
public enum UiSize
{
    /// <summary>11px — denser than the baseline.</summary>
    Small,

    /// <summary>12px — the baseline, matching upstream's chrome.</summary>
    Normal,

    /// <summary>13px.</summary>
    Large,

    /// <summary>15px — the largest offered step.</summary>
    VeryLarge,
}

/// <summary>
///  The text size of each <see cref="UiSize"/> and its persisted name, kept together so
///  the list of sizes exists in exactly one place.
///
///  <para>The names are what goes into <c>ui-state.json</c>; they are the enum member
///  names, so adding a step needs no separate table.</para>
/// </summary>
public static class UiSizes
{
    /// <summary>The sizes offered in Settings, in the order they are shown.</summary>
    public static readonly UiSize[] All =
        [UiSize.Small, UiSize.Normal, UiSize.Large, UiSize.VeryLarge];

    /// <summary>
    ///  The chrome font size in device-independent pixels.
    ///
    ///  <para><b>WHOLE PIXELS, deliberately.</b> The four steps were specified as 90 /
    ///  100 / 110 / 125% of the 12px baseline, which is 10.8 / 12 / 13.2 / 15. Two of
    ///  those are fractional, and a fractional chrome size propagates into fractional
    ///  control heights and text origins — borders and glyph stems then straddle pixel
    ///  boundaries and the whole shell reads faintly soft, which is the opposite of what
    ///  someone asking for a size change wants. Rounded to whole pixels the steps are
    ///  <b>11 / 12 / 13 / 15</b>, i.e. the realised ratios are 92 / 100 / 108 / 125%
    ///  rather than the nominal ones. Half-pixel steps were considered and rejected: they
    ///  would give 10.8 → 11.0, no closer to nominal than whole pixels, and 13.2 → 13.5,
    ///  which is a fractional size again.</para>
    ///
    ///  <para><see cref="Label"/> prints these pixel values and not the nominal
    ///  percentages, so the UI cannot claim a ratio the app does not draw.</para>
    /// </summary>
    public static double FontSize(UiSize size) => size switch
    {
        UiSize.Small => 11,
        UiSize.Large => 13,
        UiSize.VeryLarge => 15,
        _ => 12,
    };

    /// <summary>The label shown in the Settings combo.</summary>
    public static string Label(UiSize size) => size switch
    {
        UiSize.Small => "Small (11px text)",
        UiSize.Large => "Large (13px text)",
        UiSize.VeryLarge => "Very large (15px text)",
        _ => "Normal (12px text)",
    };

    /// <summary>The name written to <c>ui-state.json</c>.</summary>
    public static string Name(UiSize size) => size.ToString();

    /// <summary>
    ///  Parses a persisted name, falling back to <see cref="UiSize.Normal"/> for anything
    ///  unrecognised — same rule as <c>Theme</c> and <c>Style</c>, so a hand-edited or
    ///  older file can never produce a size the app cannot draw.
    /// </summary>
    public static UiSize Parse(string? name)
        => Enum.TryParse(name, ignoreCase: true, out UiSize size) && Array.IndexOf(All, size) >= 0
            ? size
            : UiSize.Normal;
}
