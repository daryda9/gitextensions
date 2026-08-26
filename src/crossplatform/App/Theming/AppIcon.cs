using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The product logo, as the icon every window of this application carries.
/// </summary>
/// <remarks>
///  <para><b>Why a window needs one at all.</b> The <c>.desktop</c> entry names an icon,
///  but that only covers the <i>launcher</i>: once a window is up, a desktop shell asks
///  the window itself, through the <c>_NET_WM_ICON</c> property X11 clients are expected
///  to set. The port never set <see cref="Window.Icon"/>, so that property was absent
///  (verified with <c>xprop</c>) and the dock fell back to its generic placeholder — the
///  gear the user saw next to a correctly-iconed entry in the application list.</para>
///
///  <para>The second half of the same problem lives in <c>packaging/gitextensions.desktop</c>:
///  a shell also tries to match a window to an installed entry by its <c>WM_CLASS</c>,
///  which Avalonia sets to the assembly name (<c>GitExtensions.Avalonia</c>) and which
///  therefore matched no entry. <c>StartupWMClass</c> states it explicitly, and that is
///  what makes a running window share the launcher's icon and its place in the dock
///  rather than appearing beside it as a stranger.</para>
///
///  <para><b>Installed as a style, not assigned per window.</b> <see cref="Window.Icon"/>
///  is a styled property, so one <c>Style</c> on the application covers every window the
///  port opens — the main one, the dialogs, the stash and file-history windows — and no
///  window opened later can forget it.</para>
/// </remarks>
internal static class AppIcon
{
    // The 256px product mark, linked into the resources as Assets/Icons/GitNext.png
    // (see the csproj). The same bitmap the About dialog shows; the packaged .desktop
    // entry points at the 256px original instead, which is what a launcher wants.
    private static readonly string Asset =
        $"avares://{typeof(AppIcon).Assembly.GetName().Name}/Assets/Icons/GitNext.png";

    private static WindowIcon? _icon;

    /// <summary>
    ///  Adds the window-icon style to <paramref name="styles"/> (the application's own).
    ///  Silent when the asset cannot be loaded: a missing icon is a blemish, not a reason
    ///  to fail the start-up.
    /// </summary>
    internal static void Apply(Styles styles)
    {
        if (Load() is not { } icon)
        {
            return;
        }

        Style style = new(x => x.OfType<Window>());
        style.Setters.Add(new Setter(Window.IconProperty, icon));
        styles.Add(style);
    }

    private static WindowIcon? Load()
    {
        if (_icon is not null)
        {
            return _icon;
        }

        try
        {
            using Stream stream = AssetLoader.Open(new Uri(Asset));
            _icon = new WindowIcon(new Bitmap(stream));
        }
        catch
        {
            // No asset, no icon; the window simply keeps the shell's placeholder.
        }

        return _icon;
    }
}
