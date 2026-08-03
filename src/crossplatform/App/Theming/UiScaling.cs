using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Applies the chosen <see cref="UiSize"/> to every window, and owns the current
///  size the way <see cref="ThemeManager"/> owns the theme and the style.
///
///  <para><b>Why a transform and not a font size.</b> The obvious implementation is to
///  scale the app's default font size and let the controls follow. Measured on this
///  tree, that would be a half-working option: the views assign literal font sizes in
///  <b>137</b> places (77 of them <c>12</c>) — the revision grid rows, the diff, the
///  file lists, i.e. exactly the dense content the option is asked for — and Fluent's
///  control heights are fixed minimums (a <c>TextBox</c> measures 32px tall whether its
///  font is 12 or 15, verified against Fluent 11.3.14). A font-size knob would leave
///  all of that at one size while the labels around it moved. A
///  <see cref="LayoutTransformControl"/> above the window content scales the whole
///  measured tree — literal font sizes, fixed minimums, icon boxes, and the
///  custom-drawn DAG — by one factor, which is the only way the parts stay coherent.</para>
///
///  <para><b>The trade-off accepted.</b> The transform scales rendering as well as
///  layout, so Classic's 16px PNG icons are resampled at 90/110/125% and lose a little
///  crispness (text and the vector glyphs are geometry and stay sharp). That is
///  visible, and it is the price of the option being real for every part of the UI
///  instead of only the parts that read a font size. At
///  <see cref="UiSize.Normal"/> nothing is installed at all, so the default build is
///  pixel-identical to one without the option.</para>
///
///  <para><b>Why the size is not a third argument to
///  <see cref="ThemeManager.Apply(ThemeVariant, AppStyle)"/>.</b> M80's rule was that
///  no call site may pass a literal for a dimension the user did not touch, and the
///  cost of that rule grows with every dimension bolted onto the same call. The size
///  shares nothing with the palette — it is a transform, not a brush — so it gets its
///  own owner and its own single-argument <see cref="Apply(UiSize)"/>, and the
///  theme/style call sites are left exactly as M80 left them.</para>
/// </summary>
public static class UiScaling
{
    /// <summary>The active size. Changed only through <see cref="Apply(UiSize)"/>.</summary>
    public static UiSize CurrentSize { get; private set; } = UiSize.Normal;

    /// <summary>The active scale factor (1.0 at <see cref="UiSize.Normal"/>).</summary>
    public static double CurrentScale => UiSizes.Scale(CurrentSize);

    /// <summary>
    ///  Raised after the size changed and every open window has been re-scaled.
    ///
    ///  <para>STATIC, like <see cref="ThemeManager.StyleChanged"/>: anything that
    ///  subscribes from a control must unsubscribe when it detaches, or the handler
    ///  list grows for the life of the process.</para>
    /// </summary>
    public static event Action? SizeChanged;

    /// <summary>
    ///  Set on every <see cref="Window"/> by the app-wide style
    ///  <see cref="BuildStyles"/> returns. The property itself carries no meaning: its
    ///  change handler is what wraps the window's content, and a style setter is the
    ///  only way to reach every window — including the ones Avalonia opens itself,
    ///  such as the managed file chooser — without editing 34 window classes and
    ///  remembering the 35th.
    /// </summary>
    public static readonly AttachedProperty<bool> ScaledProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("Scaled", typeof(UiScaling));

    // The transform hosts of the windows that are still alive. Weak, because a closed
    // dialog must be collectable: the list is only ever walked to re-scale, and a
    // window that has gone away needs no re-scaling. Compacted on every walk, so a
    // long session does not accumulate dead slots.
    private static readonly List<WeakReference<LayoutTransformControl>> Hosts = [];

    static UiScaling()
    {
        ScaledProperty.Changed.AddClassHandler<Window>((window, args) =>
        {
            if (args.GetNewValue<bool>())
            {
                Attach(window);
            }
        });
    }

    /// <summary>
    ///  The app-wide style that opts every window in. Add it to
    ///  <see cref="Application.Styles"/> once, after the theme; it is never removed, and
    ///  it is style-agnostic (Classic and Modern both go through it).
    /// </summary>
    public static Styles BuildStyles()
    {
        Style window = new(x => x.OfType<Window>());
        window.Setters.Add(new Setter(ScaledProperty, true));
        return [window];
    }

    /// <summary>
    ///  Sets the size that new windows are created at and re-scales the open ones in
    ///  place — live, like the theme and the style, and without rebuilding any view.
    /// </summary>
    public static void Apply(UiSize size)
    {
        CurrentSize = size;
        double scale = UiSizes.Scale(size);

        for (int i = Hosts.Count - 1; i >= 0; i--)
        {
            if (Hosts[i].TryGetTarget(out LayoutTransformControl? host))
            {
                // Assigning the transform is enough: LayoutTransformControl invalidates
                // its own measure, so the window re-lays-out at the new scale on the next
                // layout pass. Verified headless across all four sizes on an already-shown
                // window — an explicit InvalidateMeasure() here changes nothing.
                host.LayoutTransform = Transform(scale);
            }
            else
            {
                Hosts.RemoveAt(i);
            }
        }

        SizeChanged?.Invoke();
    }

    // null at 1.0: LayoutTransformControl with no transform is a pass-through, so the
    // default size costs nothing beyond one extra element in the tree.
    private static ITransform? Transform(double scale)
        => scale == 1.0 ? null : new ScaleTransform(scale, scale);

    private static void Attach(Window window)
    {
        // ORDER MATTERS: the content has to be taken off the window BEFORE it is handed
        // to the host, or Avalonia throws "The Control already has a parent" — a control
        // may only have one logical parent, and assigning Child is what re-parents it.
        object? content = window.Content;
        window.Content = null;

        LayoutTransformControl host = new()
        {
            LayoutTransform = Transform(CurrentScale),
            Child = AsControl(content),
        };

        window.Content = host;
        Hosts.Add(new WeakReference<LayoutTransformControl>(host));

        // A window that assigns Content after it has been styled (or replaces it later,
        // as the settings dialog and the main window both do on a language switch) would
        // otherwise throw the transform away. Re-parent instead of re-wrapping, so the
        // host — and its entry in Hosts — stays the same object.
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == ContentControl.ContentProperty
                && !ReferenceEquals(window.Content, host))
            {
                object? content = window.Content;
                window.Content = host;
                host.Child = AsControl(content);
            }
        };
    }

    // Window.Content is an object: a control goes straight in, anything else (a string,
    // or a value a DataTemplate will render) keeps its templating by going through a
    // ContentControl, because LayoutTransformControl.Child is typed Control.
    private static Control? AsControl(object? content) => content switch
    {
        null => null,
        Control control => control,
        _ => new ContentControl { Content = content },
    };
}
