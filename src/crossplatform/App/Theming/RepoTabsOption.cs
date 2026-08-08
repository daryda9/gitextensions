namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  How many repositories one window holds: a strip of tabs across the top, VS Code
///  style, or a single repository at a time as the window has always worked.
/// </summary>
/// <remarks>
///  <para><b>Independent of <see cref="AppStyle"/> and of <see cref="WindowChrome"/>.</b>
///  It is a layout choice, not a palette one, and the strip is drawn from the live
///  palette, so it reads correctly in Classic and in Modern and every combination with
///  the two title-bar arrangements is valid. Nothing here consults
///  <see cref="ThemeManager.CurrentStyle"/> and nothing should.</para>
///
///  <para>Shaped like <see cref="WindowChrome"/> on purpose: one static holder of the
///  live value plus a change event, so the Settings dialog can preview it and the main
///  window can act on it without either knowing about the other.</para>
/// </remarks>
internal static class RepoTabsOption
{
    /// <summary>The stored value for the tab strip — the default.</summary>
    internal const string TabsName = "Tabs";

    /// <summary>The stored value for one repository per window.</summary>
    internal const string SingleName = "Single";

    /// <summary>
    ///  True while the window carries the tab strip. The default, and what an absent or
    ///  unrecognised stored value reads as (see <see cref="Parse"/>).
    /// </summary>
    internal static bool Enabled { get; private set; } = true;

    /// <summary>Raised when <see cref="Enabled"/> actually changes.</summary>
    internal static event Action? Changed;

    /// <summary>Switches the arrangement live, and tells the window if it moved.</summary>
    internal static void Apply(bool enabled)
    {
        if (enabled == Enabled)
        {
            return;
        }

        Enabled = enabled;
        Changed?.Invoke();
    }

    /// <summary>
    ///  Reads a stored value. ONLY the exact <see cref="SingleName"/> means one
    ///  repository per window: null, empty and anything unknown mean the tab strip,
    ///  which is what makes a state file written before this option existed open in the
    ///  new arrangement rather than the old one.
    /// </summary>
    internal static bool Parse(string? stored)
        => !string.Equals(stored, SingleName, StringComparison.Ordinal);

    /// <summary>The stored value for a live one.</summary>
    internal static string Name(bool enabled) => enabled ? TabsName : SingleName;
}
