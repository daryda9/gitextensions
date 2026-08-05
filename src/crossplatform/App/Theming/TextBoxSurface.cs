using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Pins a <see cref="TextBox"/> to a chosen background/foreground across
///  <em>every</em> visual state.
///
///  <para>Why this is needed: setting <c>Background</c>/<c>Foreground</c> on a
///  <see cref="TextBox"/> only fixes the <em>normal</em> state. The Fluent control
///  theme repaints the template's border element from theme resources on
///  <c>:pointerover</c>, <c>:focus</c> and <c>:disabled</c>
///  (<c>TextControlBackgroundPointerOver</c>, <c>TextControlBackgroundFocused</c>, …),
///  and a style setter beats a local value — so clicking a TextBox that carries a
///  deliberately non-theme background made it snap to the theme's colour while the
///  local foreground stayed put. On the dark theme that turned the beige process
///  console black with near-black text (unreadable).</para>
///
///  <para>The fix is to publish the very keys the control theme looks up into the
///  <see cref="StyledElement.Resources"/> of the <em>instance</em>. A
///  <c>DynamicResource</c> in a setter resolves against the styled element's
///  resource chain, and the template children walk up to the TextBox, so the
///  per-instance values win in the state styles too, without touching the app-wide
///  theme.</para>
///
///  <para>Brushes are stored by reference. <see cref="ThemeManager"/> switches theme
///  by mutating its palette brushes in place, so a surface built from
///  <c>App.*</c> brushes keeps following the theme after a hot switch.</para>
/// </summary>
public static class TextBoxSurface
{
    // Selection: the app accent is identical in both themes (#007ACC), so pairing
    // it with white text stays readable whatever the theme and survives a hot
    // theme switch without recomputing anything.
    private static readonly IBrush DefaultSelectionForeground = Brushes.White;

    /// <summary>
    ///  Makes <paramref name="box"/> keep <paramref name="background"/> and
    ///  <paramref name="foreground"/> in the normal, pointer-over, focused and
    ///  disabled states, along with its border and its text-selection colours.
    /// </summary>
    /// <param name="box">The text box to pin.</param>
    /// <param name="background">Background for every state.</param>
    /// <param name="foreground">Text (and caret) colour for every state.</param>
    /// <param name="border">
    ///  Border brush for every state; defaults to the box's current
    ///  <see cref="TemplatedControl.BorderBrush"/>, else <c>App.BorderStrong</c>
    ///  (<c>App.Border</c> only if that key is missing).
    /// </param>
    /// <param name="selectionBackground">
    ///  Selection highlight; defaults to <c>App.Accent</c>.
    /// </param>
    /// <param name="selectionForeground">
    ///  Selected-text colour; defaults to white (readable on the accent).
    /// </param>
    /// <param name="placeholderForeground">
    ///  Watermark colour; defaults to <c>App.TextDim</c>, else
    ///  <paramref name="foreground"/>.
    /// </param>
    public static T Apply<T>(
        T box,
        IBrush background,
        IBrush foreground,
        IBrush? border = null,
        IBrush? selectionBackground = null,
        IBrush? selectionForeground = null,
        IBrush? placeholderForeground = null)
        where T : TextBox
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(foreground);

        // App.BorderStrong before App.Border: a text box is delimited by its outline
        // alone, and App.Border measures 1.08:1 (modern dark) on the surfaces it lands
        // on — WCAG 1.4.11 asks 3:1 of a non-text indicator. The Fluent keys were raised
        // for the same reason, but they never reach a pinned box: these per-instance
        // resources are exactly what out-shouts them. In the classic families
        // App.BorderStrong IS App.Border, so the classic look is unchanged.
        border ??= box.BorderBrush ?? Resource("App.BorderStrong") ?? Resource("App.Border") ?? foreground;
        selectionBackground ??= Resource("App.Accent") ?? Brushes.SteelBlue;
        selectionForeground ??= DefaultSelectionForeground;
        placeholderForeground ??= Resource("App.TextDim") ?? foreground;

        // Normal state (plain local values).
        box.Background = background;
        box.Foreground = foreground;
        box.CaretBrush = foreground;
        box.BorderBrush = border;
        box.SelectionBrush = selectionBackground;
        box.SelectionForegroundBrush = selectionForeground;

        // The state-dependent lookups performed by the control theme. Set them all,
        // per state, on this instance only. Keys the theme in use does not consume
        // are simply inert, which keeps this forward/backward compatible across
        // Avalonia versions.
        foreach (string state in States)
        {
            box.Resources[$"TextControlBackground{state}"] = background;
            box.Resources[$"TextControlForeground{state}"] = foreground;
            box.Resources[$"TextControlBorderBrush{state}"] = border;
            box.Resources[$"TextControlPlaceholderForeground{state}"] = placeholderForeground;
        }

        // Selection is a single key rather than a per-state one. Despite the
        // "Color" suffix the control theme feeds it to TextBox.SelectionBrush, so
        // it wants an IBrush, not a Color.
        box.Resources["TextControlSelectionHighlightColor"] = selectionBackground;

        // Focus also thickens the border (TextControlBorderThemeThicknessFocused),
        // which shifts the text. Pin the thickness to whatever the box asked for.
        box.Resources["TextControlBorderThemeThickness"] = box.BorderThickness;
        box.Resources["TextControlBorderThemeThicknessFocused"] = box.BorderThickness;

        return box;
    }

    /// <summary>"" is the normal state; the rest match the Fluent key suffixes.</summary>
    private static readonly string[] States = ["", "PointerOver", "Focused", "Disabled"];

    private static IBrush? Resource(string key)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush brush
            ? brush
            : null;
}
