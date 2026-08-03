using Avalonia;
using Avalonia.Styling;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Applies the chosen <see cref="UiSize"/>, and owns the current size the way
///  <see cref="ThemeManager"/> owns the theme and the style.
///
///  <para><b>The size is the app's text size.</b> Three theme resource keys are written,
///  nothing else — no control is touched, no visual tree is modified. Every Fluent
///  <c>ControlTheme</c> reads its content size from
///  <see cref="ChromeFontSizeKey">ControlContentThemeFontSize</see> through a dynamic
///  resource, so the whole chrome follows one write, live, including the parts that live
///  in their own visual root. Measured against Fluent 11.3.14 with the key at 12 and at
///  15: <c>Button</c>, <c>TextBox</c>, <c>CheckBox</c>, <c>ComboBox</c>, <c>TreeView</c>,
///  <c>ListBox</c>, a bare <c>TextBlock</c>, <c>ListBoxItem</c>, <c>TreeViewItem</c>,
///  <c>ComboBoxItem</c> and <c>MenuItem</c> at both levels all report the value written.</para>
///
///  <para><b>What it provably does NOT scale, and why that is stated in the UI.</b> The
///  views assign literal font sizes in <b>137</b> places (77 of them <c>12</c>) — the
///  revision grid rows, the diff, the file lists — and those literals are
///  <see cref="Metrics.Text"/> compile-time constants read once when a view is built, so
///  they cannot follow a resource and do not move. Fluent's control heights are partly
///  fixed minimums as well (a <c>TextBox</c> measures 32px tall at font 12 and at font
///  15; a <c>Button</c> does grow, 23px to 25px). The Appearance page therefore says in
///  one line that this option changes the interface text and not the grid, diff or file
///  lists, rather than letting the option look like a zoom.</para>
///
///  <para><b>This replaced a per-window layout transform (M84).</b> The earlier
///  implementation wrapped every window's content in a <c>LayoutTransformControl</c>
///  installed by an app-wide style. It was chosen for coherence — one factor over the
///  whole measured tree — but it did not deliver it: popup content is not a descendant of
///  the window's content, so menus, dropdowns and tooltips stayed at 100% while the
///  window scaled, which is a worse mismatch than the one above. It also mutated the
///  content tree from inside a styling callback, and produced three defects in a row: a
///  crash on every window (M82), a blank main window (M83), and a dropped transform on
///  windows filled after being shown. Writing a resource has none of that surface: it
///  cannot orphan a control, it cannot throw, and it reaches popups precisely because
///  they read application resources like everything else.</para>
///
///  <para><b>Why the size is not a third argument to
///  <see cref="ThemeManager.Apply(ThemeVariant, AppStyle)"/>.</b> M80's rule was that no
///  call site may pass a literal for a dimension the user did not touch, and the cost of
///  that rule grows with every dimension bolted onto the same call. The size shares
///  nothing with the palette, so it gets its own owner and its own single-argument
///  <see cref="Apply(UiSize)"/>, and the theme/style call sites are left as M80 left
///  them.</para>
/// </summary>
public static class UiScaling
{
    /// <summary>The active size. Changed only through <see cref="Apply(UiSize)"/>.</summary>
    public static UiSize CurrentSize { get; private set; } = UiSize.Normal;

    /// <summary>
    ///  The Fluent resource every control template reads its content font size from.
    ///  Fluent 11.3.14 sets it to <b>14</b>; upstream Git Extensions draws its chrome in
    ///  <c>SystemFonts.MessageBoxFont</c> — Segoe UI 9pt, i.e. <b>12px</b> at 100% DPI
    ///  (<c>AppSettings.Font</c>, GitCommands/Settings/AppSettings.cs:1550), which is
    ///  what <see cref="UiSize.Normal"/> writes.
    /// </summary>
    private const string ChromeFontSizeKey = "ControlContentThemeFontSize";

    // Fluent keeps tooltips on their own key (default 12). Left behind, a tooltip would
    // be the one piece of chrome still at the baseline while everything around it moved.
    private const string ToolTipFontSizeKey = "ToolTipContentThemeFontSize";

    // Fluent's own tab header size is 24 — oversized for a dense tool, which is why the
    // port has always overridden it. Since M84 the override IS this key (ModernStyles no
    // longer sets FontSize on TabItem), so the tab strip follows the option instead of
    // being pinned to a literal 12 while the rest of the chrome moved.
    private const string TabHeaderFontSizeKey = "TabItemHeaderFontSize";

    /// <summary>
    ///  Sets the interface text size. Applies live to every open window and to every
    ///  window opened afterwards, because the keys are read through dynamic resources.
    ///
    ///  <para>Call it once at start-up to install the baseline — <see cref="UiSize.Normal"/>
    ///  is not a no-op, it is what brings Fluent's 14 down to upstream's 12 — and again
    ///  whenever the user chooses a size.</para>
    /// </summary>
    public static void Apply(UiSize size)
    {
        CurrentSize = size;

        if (Application.Current is not Application app)
        {
            // Before Initialize there is nothing to write to. CurrentSize is still
            // recorded, so the start-up call installs the right value when it comes.
            return;
        }

        double px = UiSizes.FontSize(size);
        app.Resources[ChromeFontSizeKey] = px;
        app.Resources[ToolTipFontSizeKey] = px;
        app.Resources[TabHeaderFontSizeKey] = px;
    }
}
