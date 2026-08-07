using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Makes a scrolling menu behave like one: the wheel moves it by a readable amount,
///  and RESTING on a scroll chevron scrolls, instead of demanding a click per line.
/// </summary>
/// <remarks>
///  <para><b>Why this exists.</b> A menu long enough to scroll (M111 caps the card to the
///  window) is driven by the <see cref="ScrollViewer"/> inside the popup, and Fluent
///  gives it two <see cref="RepeatButton"/> chevrons that only act while PRESSED. Reading
///  a thirty-entry menu then means either clicking the chevron once per line or turning
///  the wheel, which moved by a single line per notch — on a touchpad, whose deltas
///  arrive in fractions of a notch, that reads as a menu that barely moves and then
///  jumps.</para>
///
///  <para><b>How it attaches.</b> As an attached property set from a style
///  (<c>ModernStyles</c>, baseline block) on the ScrollViewer inside a
///  <see cref="MenuItem"/>'s pop-up, so it reaches every menu without any view knowing
///  about it. The handlers live on the ScrollViewer itself and die with it.</para>
/// </remarks>
internal static class MenuScrolling
{
    /// <summary>
    ///  Pixels per wheel notch. Avalonia reports a notch as 1.0 and a touchpad as a
    ///  fraction of it, so one multiplier serves both: three lines per notch is the
    ///  desktop convention, and a third of a line for a small touchpad delta is exactly
    ///  the smooth scrolling that was missing.
    /// </summary>
    private const double WheelStep = 84;

    /// <summary>Pixels per tick while the pointer rests on a chevron, and the tick.</summary>
    private const double HoverStep = 6;

    private static readonly TimeSpan HoverInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    ///  Set to <see langword="true"/> on a menu's <see cref="ScrollViewer"/> to wire the
    ///  behaviour described on this class.
    /// </summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("Enabled", typeof(MenuScrolling));

    public static void SetEnabled(ScrollViewer target, bool value) => target.SetValue(EnabledProperty, value);

    public static bool GetEnabled(ScrollViewer target) => target.GetValue(EnabledProperty);

    static MenuScrolling()
    {
        EnabledProperty.Changed.AddClassHandler<ScrollViewer>((scroll, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                Attach(scroll);
            }
        });
    }

    private static void Attach(ScrollViewer scroll)
    {
        // Tunnelling, so the wheel is handled before the item under the pointer sees it:
        // a MenuItem does not scroll, and whichever ancestor eventually would is not the
        // one the user is pointing at.
        scroll.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        // The chevrons only exist once the ScrollViewer has a template, and a menu popup
        // builds its tree the first time it opens.
        scroll.TemplateApplied += (_, _) => WireChevrons(scroll);
        WireChevrons(scroll);
    }

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.Extent.Height <= scroll.Viewport.Height)
        {
            return;
        }

        double target = scroll.Offset.Y - (e.Delta.Y * WheelStep);
        ScrollTo(scroll, target);
        e.Handled = true;
    }

    // The two chevrons Fluent puts at the ends of a scrolling menu. They are found by
    // position rather than by name — the up one is above the viewport, the down one
    // below — because the names belong to Fluent's template and are not ours to rely on.
    private static void WireChevrons(ScrollViewer scroll)
    {
        foreach (RepeatButton chevron in scroll.GetVisualDescendants().OfType<RepeatButton>())
        {
            if (chevron.GetValue(WiredProperty))
            {
                continue;
            }

            chevron.SetValue(WiredProperty, true);

            DispatcherTimer timer = new() { Interval = HoverInterval };
            timer.Tick += (_, _) => ScrollTo(scroll, scroll.Offset.Y + (Up(scroll, chevron) ? -HoverStep : HoverStep));

            chevron.PointerEntered += (_, _) => timer.Start();
            chevron.PointerExited += (_, _) => timer.Stop();
            chevron.DetachedFromVisualTree += (_, _) => timer.Stop();
        }
    }

    // Above the middle of the ScrollViewer = the "scroll up" chevron. Compared in the
    // ScrollViewer's own coordinates so it holds wherever the popup is on screen.
    private static bool Up(ScrollViewer scroll, Visual chevron)
        => chevron.TranslatePoint(default, scroll) is { } point && point.Y < scroll.Bounds.Height / 2;

    private static void ScrollTo(ScrollViewer scroll, double y)
    {
        double max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = scroll.Offset.WithY(Math.Clamp(y, 0, max));
    }

    /// <summary>Marks a chevron whose handlers are already attached (the template can be
    /// re-applied, and a second set of handlers would double every step).</summary>
    private static readonly AttachedProperty<bool> WiredProperty =
        AvaloniaProperty.RegisterAttached<RepeatButton, bool>("Wired", typeof(MenuScrolling));
}
