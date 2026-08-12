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
        // App.Hover / App.Pressed, not App.PanelAlt / App.Panel: those two are DARKER
        // than a bar, so a button under the pointer reads as a hole punched in it
        // instead of a lift.
        IBrush hover = Brush("App.Hover", "#444448");
        IBrush pressed = Brush("App.Pressed", "#555558");

        Add<Button>(styles, hover, pressed);
        Add<ToggleButton>(styles, hover, pressed);
    }

    /// <summary>The class an action button must carry to get the look below.</summary>
    internal const string ActionClass = "actionbtn";

    /// <summary>
    ///  Installs the look for the buttons that are NOT on a bar — a dialog's own
    ///  actions, which stand alone on a panel.
    ///
    ///  <para>Fluent gives them the 3:1 outline the modern palette maps for a button
    ///  sitting ON a toolbar, where fill and ground are the same colour and the border
    ///  is the only thing that says where the button is. On a dialog they are not: the
    ///  fill already differs from the ground, so the outline adds a pale rectangle per
    ///  button and nothing else — five of them down the side of the commit dialog. This
    ///  drops the border and raises the fill by one step instead, which is what says
    ///  "button" on every modern surface.</para>
    /// </summary>
    internal static void ApplyActions(Styles styles)
    {
        IBrush rest = Brush("App.PanelAlt", "#2D2D30");
        IBrush hover = Brush("App.Hover", "#444448");
        IBrush pressed = Brush("App.Pressed", "#555558");

        styles.Add(ActionPresenter(null,
            new Setter(ContentPresenter.BackgroundProperty, rest),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
            new Setter(ContentPresenter.CornerRadiusProperty, Metrics.Radius.SmCorner)));

        styles.Add(ActionPresenter(":pointerover",
            new Setter(ContentPresenter.BackgroundProperty, hover),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)));

        styles.Add(ActionPresenter(":pressed",
            new Setter(ContentPresenter.BackgroundProperty, pressed),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)));
    }

    /// <summary>The class a button inside a drop-down must carry to look like a menu
    /// entry.</summary>
    internal const string MenuClass = "menubtn";

    /// <summary>
    ///  Installs the look of a MENU entry for buttons carrying <see cref="MenuClass"/>:
    ///  no fill and no outline at rest, a rounded fill under the pointer — the pill
    ///  <see cref="ModernStyles"/> gives a real <see cref="MenuItem"/>.
    ///
    ///  <para><b>Why it must be installed on the Application.</b> A flyout's content
    ///  lives in a pop-up root of its own, so styles declared on the view that OWNS the
    ///  flyout never reach it; only the application's do. That is also why this cannot
    ///  simply reuse <see cref="Apply"/>, which each bar installs on itself.</para>
    ///
    ///  <para>The port builds a few drop-downs out of plain buttons instead of
    ///  <see cref="MenuItem"/>s (the grid's Go-to card, which mixes commands with a hash
    ///  box). Those buttons were carrying Fluent's default chrome — a filled, outlined
    ///  rectangle each — so a card of four commands read as four boxes stacked inside a
    ///  frame, next to context menus that draw the same commands as flat rows.</para>
    /// </summary>
    internal static void ApplyMenus(Styles styles)
    {
        IBrush hover = Brush("App.Hover", "#444448");
        IBrush pressed = Brush("App.Pressed", "#555558");

        styles.Add(MenuPresenter(null,
            new Setter(ContentPresenter.BackgroundProperty, Fade(hover)),
            new Setter(Animatable.TransitionsProperty, new Transitions()),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
            new Setter(ContentPresenter.CornerRadiusProperty, Metrics.Radius.SmCorner)));

        styles.Add(MenuPresenter(":pointerover",
            new Setter(ContentPresenter.BackgroundProperty, hover),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)));

        styles.Add(MenuPresenter(":pressed",
            new Setter(ContentPresenter.BackgroundProperty, pressed),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)));
    }

    private static Style MenuPresenter(string? state, params Setter[] setters)
        => Presenter<Button>(state, MenuClass, setters);

    private static Style ActionPresenter(string? state, params Setter[] setters)
    {
        Style style = new(x =>
        {
            Selector selector = x.OfType<Button>().Class(ActionClass);
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

    // The three live states differ from rest by their FILL ALONE — no outline is added
    // on hover, press or latch. The outline was doing no work the fill was not already
    // doing (a button at rest has none, so it never told the user where the button was),
    // and on a strip of adjacent small buttons it drew a rectangle around each one the
    // moment the pointer crossed it. The fills carry it on their own: App.Pressed reads
    // 2.23:1 against the modern dark bar and 1.90:1 against the light one, App.Hover
    // 1.69 / 1.54:1 — the level a toolbar's own hover and active fills sit at.
    private static void Add<T>(Styles styles, IBrush hover, IBrush pressed)
        where T : TemplatedControl
    {
        styles.Add(Presenter<T>(null, new Setter(ContentPresenter.BackgroundProperty, Fade(hover)),
            new Setter(Animatable.TransitionsProperty, new Transitions()),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
            new Setter(ContentPresenter.CornerRadiusProperty, Metrics.Radius.SmCorner)));

        styles.Add(Presenter<T>(":pointerover",
            new Setter(ContentPresenter.BackgroundProperty, hover),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)));

        styles.Add(Presenter<T>(":pressed",
            new Setter(ContentPresenter.BackgroundProperty, pressed),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)));

        // Only a ToggleButton has it, but the selector costs nothing on the others.
        styles.Add(Presenter<T>(":checked",
            new Setter(ContentPresenter.BackgroundProperty, pressed),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent)));
    }

    // The Fluent template paints a button's chrome through its inner ContentPresenter,
    // so that is the part to style — the button's own Background property is not what
    // ends up on screen.
    private static Style Presenter<T>(string? state, params Setter[] setters)
        where T : TemplatedControl
        => Presenter<T>(state, Class, setters);

    private static Style Presenter<T>(string? state, string cssClass, params Setter[] setters)
        where T : TemplatedControl
    {
        Style style = new(x =>
        {
            Selector selector = x.OfType<T>().Class(cssClass);
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
