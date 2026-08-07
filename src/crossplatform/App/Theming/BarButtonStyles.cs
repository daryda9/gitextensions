using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The look of a bar button — flat, borderless, a fill only under the pointer — as a
///  set of styles any control can install on itself, for buttons carrying the
///  <c>toolbtn</c> class.
/// </summary>
/// <remarks>
///  <para><b>Why it is a class and not a copy.</b> The main toolbar has drawn its
///  buttons this way since M77 and it is the port's bar-button look; every other strip
///  that used Fluent's default instead ended up as a row of little outlined boxes — the
///  commit dialog's two pane toolbars, twelve boxes between them, which is most of what
///  "too many borders" meant in M107. One definition, two call sites, no drift.</para>
///
///  <para><b>The resting fill is the HOVER colour at alpha 0</b>, not
///  <see cref="Brushes.Transparent"/>, and that is not a detail. Transparent is
///  <c>#00FFFFFF</c> — transparent WHITE — and the modern style cross-fades this very
///  property (<see cref="ModernStyles"/>): interpolating from transparent white to the
///  hover fill walks through half-opaque white, which is the flash the strip blinked on
///  every hover. Fading in from the hover colour at alpha 0 makes it a pure opacity
///  ramp, so no third colour is ever on screen in either theme.</para>
///
///  <para>The transitions are then emptied anyway: a strip of small buttons under a
///  moving pointer reads better switching cleanly than smearing between two fills. A
///  style declared on the control wins over one declared on the Application, which is
///  what lets an empty <see cref="Transitions"/> beat the modern style's.</para>
/// </remarks>
internal static class BarButtonStyles
{
    /// <summary>The class a button must carry to get this look.</summary>
    internal const string Class = "toolbtn";

    /// <summary>
    ///  Installs the styles on one control's own <see cref="Styles"/> collection.
    ///  <see cref="ToggleButton"/> is covered as well as <see cref="Button"/>, with a
    ///  checked state that stays visible: the pane toolbars say which grouping is on
    ///  with a latched button, and a flat resting fill would otherwise hide it.
    /// </summary>
    internal static void Apply(Styles styles)
    {
        IBrush border = Brush("App.Border", "#3F3F46");

        // App.Hover / App.Pressed, not App.PanelAlt / App.Panel: those two are DARKER
        // than a bar, so a button under the pointer reads as a hole punched in it
        // instead of a lift.
        IBrush hover = Brush("App.Hover", "#444448");
        IBrush pressed = Brush("App.Pressed", "#555558");

        Add<Button>(styles, hover, pressed, border);
        Add<ToggleButton>(styles, hover, pressed, border);
    }

    private static void Add<T>(Styles styles, IBrush hover, IBrush pressed, IBrush border)
        where T : TemplatedControl
    {
        styles.Add(Presenter<T>(null, new Setter(ContentPresenter.BackgroundProperty, Fade(hover)),
            new Setter(Animatable.TransitionsProperty, new Transitions()),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
            new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(3))));

        styles.Add(Presenter<T>(":pointerover",
            new Setter(ContentPresenter.BackgroundProperty, hover),
            new Setter(ContentPresenter.BorderBrushProperty, border)));

        styles.Add(Presenter<T>(":pressed",
            new Setter(ContentPresenter.BackgroundProperty, pressed),
            new Setter(ContentPresenter.BorderBrushProperty, border)));

        // Only a ToggleButton has it, but the selector costs nothing on the others.
        styles.Add(Presenter<T>(":checked",
            new Setter(ContentPresenter.BackgroundProperty, pressed),
            new Setter(ContentPresenter.BorderBrushProperty, border)));
    }

    // The Fluent template paints a button's chrome through its inner ContentPresenter,
    // so that is the part to style — the button's own Background property is not what
    // ends up on screen.
    private static Style Presenter<T>(string? state, params Setter[] setters)
        where T : TemplatedControl
    {
        Style style = new(x =>
        {
            Selector selector = x.OfType<T>().Class(Class);
            if (state is not null)
            {
                selector = selector.Class(state);
            }

            return selector.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
        });

        foreach (Setter setter in setters)
        {
            style.Setters.Add(setter);
        }

        return style;
    }

    private static IBrush Fade(IBrush brush)
        => brush is ISolidColorBrush s
            ? new SolidColorBrush(Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B))
            : Brushes.Transparent;

    private static IBrush Brush(string key, string fallback)
        => Icons.Tint(key) ?? SolidColorBrush.Parse(fallback);
}
