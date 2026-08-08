using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The eight resize edges a window loses the moment it drops its system frame.
/// </summary>
/// <remarks>
///  <para>Dropping the frame (<c>SystemDecorations.None</c> — see <see cref="TitleBar"/>
///  for why that is the only mechanism the X11 backend offers) is all-or-nothing: mutter
///  stops drawing the title bar, and with it the invisible border that made the window
///  resizable by dragging its edge. Keyboard resize (Alt+F8) still works, and so does
///  the window menu, but pointer resize has to be handed back — which is what this is.
///  Each strip asks the window manager for a real resize through
///  <see cref="Window.BeginResizeDrag"/> (<c>_NET_WM_MOVERESIZE</c> on X11), so the drag
///  is the compositor's, with its snapping and its edge feedback intact.</para>
///
///  <para>The strips sit ON TOP of the window content, so they are kept as thin as a
///  real border and are switched off while the window is maximized, where there is
///  nothing to drag and they would only steal the outermost pixels of the UI.</para>
/// </remarks>
internal static class ResizeGrips
{
    /// <summary>Thickness of an edge strip, in logical pixels — a WM border's worth.</summary>
    private const double Edge = 5;

    /// <summary>Reach of a corner strip, where two edges are grabbed at once.</summary>
    private const double Corner = 12;

    /// <summary>
    ///  Builds the eight strips for <paramref name="window"/>, ready to be dropped into
    ///  a <see cref="Panel"/> layered over the window's content. The caller owns when
    ///  they are shown: a maximized window has no edge to drag, and the strips would
    ///  then sit over the outermost pixels of the UI for nothing.
    /// </summary>
    internal static IEnumerable<Control> Build(Window window)
    {
        yield return Grip(window, WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, Edge, StandardCursorType.TopSide);
        yield return Grip(window, WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, Edge, StandardCursorType.BottomSide);
        yield return Grip(window, WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, Edge, double.NaN, StandardCursorType.LeftSide);
        yield return Grip(window, WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, Edge, double.NaN, StandardCursorType.RightSide);

        // Corners last, so they are on top of the two edges they overlap.
        yield return Grip(window, WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, Corner, Corner, StandardCursorType.TopLeftCorner);
        yield return Grip(window, WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, Corner, Corner, StandardCursorType.TopRightCorner);
        yield return Grip(window, WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, Corner, Corner, StandardCursorType.BottomLeftCorner);
        yield return Grip(window, WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, Corner, Corner, StandardCursorType.BottomRightCorner);
    }

    private static Control Grip(
        Window window,
        WindowEdge edge,
        HorizontalAlignment horizontal,
        VerticalAlignment vertical,
        double width,
        double height,
        StandardCursorType cursor)
    {
        Border grip = new()
        {
            // Transparent, not null: a null brush is not hit-testable, and an invisible
            // strip that cannot be grabbed is no border at all.
            Background = Brushes.Transparent,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Cursor = new Cursor(cursor),
        };

        if (!double.IsNaN(width))
        {
            grip.Width = width;
        }

        if (!double.IsNaN(height))
        {
            grip.Height = height;
        }

        grip.PointerPressed += (_, e) =>
        {
            if (window.WindowState != WindowState.Normal
                || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                return;
            }

            window.BeginResizeDrag(edge, e);
            e.Handled = true;
        };

        return grip;
    }
}
