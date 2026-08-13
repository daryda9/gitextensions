using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Theming;

// Aliased because the implicit `System.IO` of this project makes a bare `Path` mean
// the file-system helper; the shape is the one this file draws with.
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The port's loading indicator: a spinner over a pane whose data is being
///  (re)loaded, layered by the host into the same <see cref="Panel"/> cell as the
///  pane it covers.
/// </summary>
/// <remarks>
///  <para><b>The delay is the whole API.</b> Almost every reload this app does —
///  re-reading the revision list after a checkout, refreshing the left tree after a
///  fetch — finishes in well under a quarter of a second, and a spinner that appears
///  and vanishes in 80 ms does not read as "loading": it reads as a glitch, a flash
///  the eye catches and the brain cannot explain. So <see cref="Show"/> does not
///  paint anything; it ARMS a timer, and only a load that outlives
///  <see cref="Delay"/> ever gets a spinner. A <see cref="Hide"/> that arrives first
///  disarms it and nothing was ever on screen. That is why <see cref="IsBusy"/> is
///  documented as "requested", not "visible": the two are deliberately different
///  states and callers must not assume the second.</para>
///
///  <para><b>Why there is no minimum visible time.</b> The usual companion trick —
///  once shown, stay up for 400 ms so the spinner is not itself a flash — is not
///  needed here and would cost something real: the delay has ALREADY filtered out
///  every short load, so anything that gets painted is a load the user has been
///  waiting on and has looked at. Holding the veil over data that has already
///  arrived would make the pane feel slower than it is, which is the opposite of
///  what this control is for. It goes up late and comes down at once.</para>
///
///  <para><b>The rotation is driven by a timer, not by an <c>Animation</c>.</b> The
///  obvious thing — build an <c>Animation</c> over <c>RotateTransform.Angle</c> and
///  <c>RunAsync</c> it on the transform — does not merely fail to animate, it THROWS:
///  Avalonia's <c>TransformAnimator</c> casts the target to <c>Visual</c>, and a
///  <c>RotateTransform</c> is not one (<c>InvalidCastException</c>, Avalonia 11.3).
///  That is how this spinner sat frozen for so long: the exception went into a
///  fire-and-forget continuation and nothing on screen said anything. Animating the
///  CONTROL's <c>RenderTransform</c> instead is the documented alternative, but it
///  needs a <c>TransformOperations</c> animator that Avalonia 11.3 does not expose to
///  code-behind — and this port has no XAML through which the styling pipeline would
///  register one. A timer that advances the angle is what is left, and for a spinner
///  it is entirely sufficient.</para>
///
///  <para><b>It never runs behind an invisible control.</b> The timer is started when
///  the spinner is actually revealed and stopped by <see cref="Hide"/> (and by the
///  control leaving the visual tree). A timer left ticking on a collapsed control
///  still invalidates its target every frame — pure battery for nothing, multiplied by
///  however many panes the host has wired up.</para>
///
///  <para><b>Clicks are swallowed while the spinner is up</b>, and that is a choice
///  rather than an accident of drawing a filled scrim. The content underneath is
///  STALE by definition — the rows are the ones that are about to be replaced — so a
///  click that lands on "row 12" would select a commit that is about to stop
///  existing at that index. Making the veil hit-test transparent is one property, but
///  it would hand those clicks to exactly the control that cannot honour them.
///  Swallowing costs the user nothing except a click they would have had to redo
///  anyway.</para>
///
///  <para><b>The stale content stays visible.</b> The scrim is translucent on purpose:
///  the user was reading that pane a second ago, and blanking it destroys their place
///  in it. Dimming says "not current" while leaving the shape of the content — and
///  the scroll position, and the selected row — where their eye left it.</para>
///
///  <para><b>Colours are read as live brush instances</b> from
///  <c>Application.Current.Resources</c> (see <see cref="Icons.Tint"/>), the way the
///  other views do: the theme and the Modern/Classic switch recolour those brushes in
///  place, so an overlay painted with the instance follows both, while one painted
///  with a copied colour would freeze at whatever theme was in force when the pane
///  was built.</para>
/// </remarks>
public sealed class BusyOverlay : UserControl
{
    // The spinner's box. 28px is the smallest ring in which a 3px stroke still reads
    // as a ring rather than a smudge, and it stays honest at the "Large" UI size —
    // UiScaling is a layout transform over the whole window, so this box is scaled
    // rather than re-measured and the arc cannot go polygonal on us.
    private const double SpinnerSize = 28;

    // Thick enough to be seen through the scrim, thin enough that the gap in the arc
    // (the thing that makes the rotation legible at all) survives.
    private const double StrokeWidth = 3;

    // How much of the ring the moving arc covers. Three quarters: a full ring shows
    // no rotation, and much less than this reads as a comet rather than a spinner.
    private const double ArcSweep = 270;

    // Low enough that the pane behind is still readable, high enough that it is
    // unmistakably "not the live view". Applied to the Border, not to the brush:
    // the brush instance belongs to the theme and must not be mutated by a view.
    private const double ScrimOpacity = 0.6;

    // One turn per second, linear. Anything faster reads as urgency — this is a
    // background wait, not an alarm — and a constant rate is what makes a rotation
    // look like rotation: a full turn has no start and no end to ease between.
    private static readonly TimeSpan TurnDuration = TimeSpan.FromSeconds(1);

    // 30 fps. Below it the arc visibly steps; above it costs UI-thread work that
    // nobody can see on a 28px ring.
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    // Derived rather than written as a number, so changing either constant above
    // keeps the rate honest instead of quietly changing the speed of the turn.
    private static readonly double DegreesPerFrame =
        360 * (FrameInterval.TotalMilliseconds / TurnDuration.TotalMilliseconds);

    private readonly Border _scrim = new();
    private readonly ShapePath _arc = new();
    private readonly Ellipse _track = new();
    private readonly RotateTransform _rotation = new();
    private readonly DispatcherTimer _reveal;
    private readonly DispatcherTimer _spin;

    /// <summary>Builds a hidden overlay. Nothing is drawn and no timer runs until <see cref="Show"/>.</summary>
    public BusyOverlay()
    {
        // Layered into the host's Panel cell: the overlay has to cover the pane
        // whatever size the pane happens to be, and the spinner has to sit in the
        // middle of it rather than in the middle of the window.
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _track.Width = SpinnerSize;
        _track.Height = SpinnerSize;
        _track.StrokeThickness = StrokeWidth;

        _arc.Width = SpinnerSize;
        _arc.Height = SpinnerSize;
        _arc.StrokeThickness = StrokeWidth;

        // Round caps, because the arc's ends are the only moving edges on screen and a
        // square cut makes the leading end look like it is snagging on something.
        _arc.StrokeLineCap = PenLineCap.Round;
        _arc.Data = BuildArc();

        // The transform is held as a field and the timer advances ITS angle, not a
        // fresh transform per reveal: re-creating one would leave the arc pointing at
        // a transform nothing is turning any more.
        _arc.RenderTransform = _rotation;
        _arc.RenderTransformOrigin = RelativePoint.Center;

        Panel column = new()
        {
            Width = SpinnerSize,
            Height = SpinnerSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _track, _arc },
        };

        // The scrim is its own child under the spinner rather than the background of
        // the control, so ScrimOpacity dims the pane WITHOUT dimming the spinner drawn
        // over it — an Opacity on the root would fade both.
        Content = new Panel { Children = { _scrim, column } };

        _reveal = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _reveal.Tick += (_, _) => Reveal();

        // Render priority so the turn is paced with the frames it is drawn in; at
        // Normal it competes with the very loading work the spinner is reporting on.
        _spin = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _spin.Tick += (_, _) => _rotation.Angle = (_rotation.Angle + DegreesPerFrame) % 360;

        // Invisible costs nothing: Avalonia neither measures nor hit-tests a collapsed
        // control, so an idle pane pays for this overlay exactly once, at construction.
        IsVisible = false;
    }

    /// <summary>
    ///  Whether the overlay is currently requested — NOT whether it is on screen yet.
    ///  A request younger than <see cref="Delay"/> is busy and invisible, which is the
    ///  normal case for the majority of loads.
    /// </summary>
    public bool IsBusy { get; private set; }

    /// <summary>
    ///  How long a load may take before the spinner appears at all. Default 250 ms.
    ///
    ///  <para>250 ms is the usual "the user has started to wonder" threshold, and it is
    ///  also comfortably longer than every reload this app does when the object store
    ///  is warm. Setting it takes effect from the next <see cref="Show"/>: changing the
    ///  interval of a request already in flight would be a way to make an armed
    ///  overlay appear immediately, which is the flash the delay exists to prevent.</para>
    /// </summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///  Requests the spinner.
    ///
    ///  <para>There is deliberately no caption. Every caller of this control passed the
    ///  same word — "Loading…" — under a spinner that already means exactly that, so the
    ///  text said nothing the animation did not and cost a line of layout in every pane.
    ///  A pane that genuinely needs to explain itself should say so in its own content,
    ///  where the words can be specific, rather than through the veil that covers it.</para>
    ///
    ///  <para>Calling it again while busy does NOT restart the delay. A load that
    ///  proceeds in steps is still ONE wait as far as the user is concerned, and
    ///  restarting the timer on each step is how a slow multi-step load ends up never
    ///  showing a spinner at all.</para>
    /// </summary>
    public void Show()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        // Read afresh here rather than at construction: the pane may have been built
        // long before the theme or the Modern/Classic style it is now painted in.
        Paint();

        _reveal.Interval = Delay;
        _reveal.Start();
    }

    /// <summary>
    ///  Withdraws the request: disarms the delay if the spinner never appeared, and
    ///  takes it down together with its animation if it did. Safe to call when not busy,
    ///  which matters — the natural shape at the call site is a <c>finally</c>, and a
    ///  cancelled load reaches it without ever having reached <see cref="Show"/>.
    /// </summary>
    public void Hide()
    {
        IsBusy = false;
        _reveal.Stop();
        StopSpinning();
        IsVisible = false;
    }

    /// <summary>
    ///  Stops the clock when the pane is torn out of the tree — a closed tab, a
    ///  rebuilt layout. Avalonia does not stop an animation for us here, and an
    ///  orphaned overlay left spinning is a timer that nothing will ever call
    ///  <see cref="Hide"/> on.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _reveal.Stop();
        StopSpinning();
    }

    /// <summary>
    ///  Resumes on re-attach: a control that comes back while its load is still in
    ///  flight must come back in the state it left, not frozen mid-turn.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!IsBusy)
        {
            return;
        }

        if (IsVisible)
        {
            StartSpinning();
        }
        else
        {
            _reveal.Interval = Delay;
            _reveal.Start();
        }
    }

    // ---- paint -------------------------------------------------------------

    private void Reveal()
    {
        // One shot: the timer's job was to answer "is this load long enough to be
        // worth a spinner", and it has.
        _reveal.Stop();

        if (!IsBusy)
        {
            return;
        }

        IsVisible = true;
        StartSpinning();
    }

    private void Paint()
    {
        _scrim.Background = B("App.Window");
        _scrim.Opacity = ScrimOpacity;
        _arc.Stroke = B("App.Accent");

        // The track is the faint full ring the arc runs on. It is what tells the eye
        // how far round the arc has got; without it a lone arc on a dim pane reads as
        // a wandering mark rather than as something turning in place.
        _track.Stroke = B("App.Border");
    }

    // ---- rotation ----------------------------------------------------------

    private void StartSpinning()
    {
        // Already turning. Restarting would snap the arc back to twelve o'clock,
        // which is exactly the flicker a re-Show() must not produce.
        if (!_spin.IsEnabled)
        {
            _spin.Start();
        }
    }

    private void StopSpinning()
    {
        if (!_spin.IsEnabled)
        {
            return;
        }

        _spin.Stop();

        // Stopping leaves the angle wherever it got to; the next reveal has to start
        // from twelve o'clock or it looks like a resumed turn of the PREVIOUS load.
        _rotation.Angle = 0;
    }

    // ---- geometry ----------------------------------------------------------

    // A three-quarter arc inscribed in the SpinnerSize box, drawn once. Built as a
    // real arc rather than as a dashed Ellipse because Avalonia measures dashes in
    // multiples of the stroke width: the gap would then change size with the stroke
    // and with the UI zoom, and the ring would quietly become a dotted circle.
    private static Geometry BuildArc()
    {
        // The stroke straddles the path, so the radius is inset by half of it —
        // otherwise the outer half of the ring is clipped by the control's own box.
        double radius = (SpinnerSize - StrokeWidth) / 2;
        double centre = SpinnerSize / 2;

        Point start = new(centre, centre - radius);
        double radians = ArcSweep * Math.PI / 180;
        Point end = new(
            centre + (radius * Math.Sin(radians)),
            centre - (radius * Math.Cos(radians)));

        PathFigure figure = new()
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false,

            // The collections are assigned, not appended to: PathFigure.Segments and
            // PathGeometry.Figures are declared nullable, so a collection initializer
            // would dereference them implicitly and the build is held at zero warnings.
            Segments = new PathSegments
            {
                new ArcSegment
                {
                    Point = end,
                    Size = new Size(radius, radius),
                    IsLargeArc = ArcSweep > 180,
                    SweepDirection = SweepDirection.Clockwise,
                },
            },
        };

        return new PathGeometry { Figures = new PathFigures { figure } };
    }

    private static IBrush B(string key) => Icons.Tint(key) ?? Brushes.Transparent;
}
