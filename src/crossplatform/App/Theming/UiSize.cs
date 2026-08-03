namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  How large the whole interface is drawn, chosen from Settings → Appearance.
///
///  <para><b>This is a zoom, not a font setting.</b> <see cref="UiSize.Normal"/> is
///  the baseline the port is built at — after M81 that baseline is upstream's own
///  12px chrome (see <see cref="ModernStyles"/>) — and every other member is a
///  uniform scale factor applied to the entire visual tree of every window. Font
///  sizes, paddings, row heights, icon boxes and the custom-drawn revision graph all
///  move together, because they are all scaled by the same transform rather than each
///  being re-derived from a font size.</para>
///
///  <para><b>Why a closed list and not a slider.</b> A free scale invites values that
///  land text on half pixels for no benefit, and it has no name to put in a settings
///  file or a bug report. Four named steps cover the reason the option exists (the
///  port reads slightly larger or smaller than the user's other tools) without
///  turning the window into a zoom control.</para>
///
///  <para>Orthogonal to both <see cref="AppStyle"/> and
///  <see cref="Avalonia.Styling.ThemeVariant"/>: the transform is applied above the
///  styled tree, so every size renders correctly in Classic and Modern alike.</para>
/// </summary>
public enum UiSize
{
    /// <summary>90% — denser than the baseline.</summary>
    Small,

    /// <summary>100% — the baseline, matching upstream's 12px chrome.</summary>
    Normal,

    /// <summary>110%.</summary>
    Large,

    /// <summary>125% — the largest offered step.</summary>
    VeryLarge,
}

/// <summary>
///  The scale factor of each <see cref="UiSize"/> and its persisted name, kept
///  together so the list of sizes exists in exactly one place.
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
    ///  The scale factor. <see cref="UiSize.Normal"/> is exactly 1.0 — the option must
    ///  be free at its default, i.e. no transform is installed and not a single pixel
    ///  moves relative to a build that never had the option.
    /// </summary>
    public static double Scale(UiSize size) => size switch
    {
        UiSize.Small => 0.90,
        UiSize.Large => 1.10,
        UiSize.VeryLarge => 1.25,
        _ => 1.0,
    };

    /// <summary>The label shown in the Settings combo (and in the View menu).</summary>
    public static string Label(UiSize size) => size switch
    {
        UiSize.Small => "Small (90%)",
        UiSize.Large => "Large (110%)",
        UiSize.VeryLarge => "Very large (125%)",
        _ => "Normal (100%)",
    };

    /// <summary>The name written to <c>ui-state.json</c>.</summary>
    public static string Name(UiSize size) => size.ToString();

    /// <summary>
    ///  Parses a persisted name, falling back to <see cref="UiSize.Normal"/> for
    ///  anything unrecognised — same rule as <c>Theme</c> and <c>Style</c>, so a
    ///  hand-edited or older file can never produce a size the app cannot draw.
    /// </summary>
    public static UiSize Parse(string? name)
        => Enum.TryParse(name, ignoreCase: true, out UiSize size) && Array.IndexOf(All, size) >= 0
            ? size
            : UiSize.Normal;
}
