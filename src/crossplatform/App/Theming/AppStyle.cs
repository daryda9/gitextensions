namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The two visual families the app can wear, chosen from Settings.
///
///  <para><see cref="Classic"/> is the look the port had up to and including
///  M76: the VS-2015 neutral greys with <c>#007ACC</c> on top, and the 2015 PNG
///  icon set drawn as bitmaps. <see cref="Modern"/> is what M77 introduced: the
///  cool-cast neutral ramp, the re-derived ink families, and the monochrome
///  vector glyphs tinted from the palette.</para>
///
///  <para>The style is orthogonal to <see cref="Avalonia.Styling.ThemeVariant"/>:
///  all four combinations (Classic/Modern x Dark/Light) are valid and each has
///  its own palette dictionary in <see cref="ThemeManager"/>.</para>
/// </summary>
public enum AppStyle
{
    /// <summary>The pre-M77 look: 2015 greys, #007ACC accent, PNG icons.</summary>
    Classic,

    /// <summary>The M77 look: cool neutral ramp, vector glyph icons.</summary>
    Modern,
}
