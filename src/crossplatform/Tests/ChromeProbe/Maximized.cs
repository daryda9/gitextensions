using Avalonia.Controls;

// This project inherits the WinForms-shim implicit usings, so a bare Screen is
// System.Windows.Forms'.
using Screen = Avalonia.Platform.Screen;

namespace ChromeProbe;

/// <summary>
///  What a maximised window with an extended client area actually measures.
///
///  <para>A Windows window whose client area is extended into the frame is oversized
///  when maximised — by the frame thickness on every side — so the top of whatever the
///  app draws there ends up above the screen unless the content is padded by
///  <see cref="TopLevel.OffScreenMargin"/>. Whether that is happening HERE, and by how
///  much, is a number, so this reads the number rather than reasoning about it.</para>
/// </summary>
internal static class Maximized
{
    internal static void Report(Window window, string label)
    {
        Screen? screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;

        Console.WriteLine($"--- {label}");
        Console.WriteLine($"    extendedIntoDecorations : {window.IsExtendedIntoWindowDecorations}");
        Console.WriteLine($"    windowState             : {window.WindowState}");
        Console.WriteLine($"    offScreenMargin         : {window.OffScreenMargin}");
        Console.WriteLine($"    frameSize               : {window.FrameSize}");
        Console.WriteLine($"    clientSize              : {window.ClientSize}");
        Console.WriteLine($"    position                : {window.Position}");

        if (screen is not null)
        {
            Console.WriteLine($"    screen bounds           : {screen.Bounds}");
            Console.WriteLine($"    screen working area     : {screen.WorkingArea}");
            Console.WriteLine($"    scaling                 : {screen.Scaling}");

            // The thing that decides whether anything is off screen: where the window
            // starts against where the usable area starts.
            Console.WriteLine(
                $"    overhang top/left       : {screen.WorkingArea.Y - window.Position.Y} / "
                + $"{screen.WorkingArea.X - window.Position.X} px");
        }
    }
}
