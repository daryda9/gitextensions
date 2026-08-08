namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Where the main menu lives: merged into the window's own title bar, or on a row of
///  its own under the desktop's title bar.
/// </summary>
/// <remarks>
///  <para><b>Independent of <see cref="AppStyle"/>.</b> It is a layout choice, not a
///  palette one: both arrangements are drawn from the live palette, so either reads
///  correctly in Classic and in Modern, and all four combinations are valid. Nothing
///  here consults <see cref="ThemeManager.CurrentStyle"/> and nothing should.</para>
///
///  <para>Shaped like <see cref="ThemeManager"/> on purpose: one static holder of the
///  live value plus a change event, so the Settings dialog can preview it and the main
///  window can act on it without either knowing about the other.</para>
/// </remarks>
internal static class WindowChrome
{
    /// <summary>The stored value for the merged bar — the default.</summary>
    internal const string MergedName = "Merged";

    /// <summary>The stored value for the desktop title bar with a menu row below.</summary>
    internal const string StandardName = "Standard";

    /// <summary>
    ///  True while the menu shares the title bar. The default, and what an absent or
    ///  unrecognised stored value reads as (see <see cref="Parse"/>).
    /// </summary>
    internal static bool Merged { get; private set; } = true;

    /// <summary>Raised when <see cref="Merged"/> actually changes.</summary>
    internal static event Action? Changed;

    /// <summary>Switches the arrangement live, and tells the window if it moved.</summary>
    internal static void Apply(bool merged)
    {
        if (merged == Merged)
        {
            return;
        }

        Merged = merged;
        Changed?.Invoke();
    }

    /// <summary>
    ///  Reads a stored value. ONLY the exact <see cref="StandardName"/> means the
    ///  separate menu row: null, empty and anything unknown mean the merged bar, which
    ///  is what makes a state file written before this option existed open in the new
    ///  arrangement rather than the old one.
    /// </summary>
    internal static bool Parse(string? stored)
        => !string.Equals(stored, StandardName, StringComparison.Ordinal);

    /// <summary>The stored value for a live one.</summary>
    internal static string Name(bool merged) => merged ? MergedName : StandardName;
}
