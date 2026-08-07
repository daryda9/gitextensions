using Avalonia;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The density values that a <see cref="Avalonia.Styling.Style"/> cannot reach, per
///  app style.
///
///  <para><b>Why this exists at all.</b> The modern density (M96) lives in
///  <see cref="ModernStyles"/>, installed and removed as a block, which is what makes it
///  modern-only without a second table of classic numbers. That mechanism covers every
///  control whose padding nobody writes by hand. It does NOT cover the app's own bar
///  buttons: those are built by helper methods that assign <c>Padding</c> as a
///  <em>local value</em>, and a local value beats every Style. Their density therefore
///  has to be a value the helper asks for, and that value has to know the style — which
///  is exactly what this class is, and nothing more.</para>
///
///  <para><b>The values are the base-4 grid, not extra air</b> (see
///  <see cref="Metrics.Density"/>): the vertical figures round DOWN or stay, so a bar
///  gains at most the 2px that separates 2 from the grid. The classic values are the
///  literals the views carried before M96, to the pixel.</para>
///
///  <para><b>Liveness.</b> <see cref="Views.MainToolbar"/> rebuilds its strip on
///  <c>ThemeManager.StyleChanged</c>, so its buttons re-read these on a style switch. A
///  view without a rebuild hook picks the new value up the next time it is constructed —
///  a dialog on its next opening, a panel on its next refresh. That is a deliberate
///  limit and not a defect worth a rebuild hook per view: the difference is 1–2px on a
///  button the user is not looking at during the switch.</para>
/// </summary>
public static class StyleDensity
{
    private static bool Modern => ThemeManager.CurrentStyle == AppStyle.Modern;

    /// <summary>
    ///  A flat bar button (toolbar strip, tree header, commit-dialog strip):
    ///  <b>4,4</b> modern, 4,2 classic. The horizontal figure was already on the grid;
    ///  the vertical one was the single off-grid value repeated most often in the
    ///  chrome.
    /// </summary>
    public static Thickness BarButton => Modern
        ? new Thickness(Metrics.Space.Xs, Metrics.Space.Xs)
        : new Thickness(Metrics.Space.Xs, 2);

    /// <summary>
    ///  A captioned bar button — one carrying text rather than a glyph, so it needs its
    ///  side air: <b>8,4</b> modern, 8,3 classic.
    /// </summary>
    public static Thickness BarButtonWide => Modern
        ? new Thickness(Metrics.Space.Sm, Metrics.Space.Xs)
        : new Thickness(Metrics.Space.Sm, 3);

    /// <summary>
    ///  The outline around a content pane — a file list, a diff, anything that is a
    ///  surface inside a window: <b>none</b> modern, 1px classic.
    ///
    ///  <para>Modern chrome separates surfaces by their COLOUR, not by a rule around
    ///  each one: <c>MainWindow</c> draws no <c>App.Border</c> at all, and
    ///  its panes read as panes because a panel sits on a darker window. A dialog that
    ///  boxes every pane instead ends up with a grid of pale lines over a dark ground —
    ///  which is exactly what the commit dialog looked like before M107. Classic keeps
    ///  the 1px box: framed panes ARE the 2015 look.</para>
    ///
    ///  <para>A pane that takes this outline must therefore also take a surface
    ///  background (<c>App.Panel</c>), or in modern it has nothing left to separate
    ///  it from the window.</para>
    /// </summary>
    public static Thickness PaneOutline => Modern ? default : new Thickness(1);
}
