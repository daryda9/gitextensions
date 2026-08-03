using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The app-wide look of the standard controls: hover / pressed / disabled / focus,
///  corners, and the short cross-fades between states.
///
///  <para><b>Nothing here edits a view.</b> Everything lands through two mechanisms
///  that reach every control in the app at once:</para>
///  <list type="number">
///   <item><description><b>Fluent resource keys</b> redefined in
///     <see cref="Application.Resources"/>. This is the same technique
///     <see cref="TextBoxSurface"/> uses per instance and
///     <c>ManagedFileChooserTheming</c> uses globally, and it is the only one that
///     works for <em>states</em>: Fluent paints <c>:pointerover</c>,
///     <c>:pressed</c> and <c>:disabled</c> from its own <c>ControlTheme</c> style
///     setters, which run at <c>BindingPriority.StyleTrigger</c> and beat a local
///     value on the template child (the M62 defect: a locally set background held
///     only in the normal state and snapped to <c>#000000</c> on click). Rather than
///     out-shout those setters, this file changes what they resolve
///     <em>to</em>.</description></item>
///   <item><description><b>Global <see cref="Style"/>s</b> in
///     <see cref="Application.Styles"/>, for the things that are properties of the
///     control itself rather than of a state: corner radius, font size and weight,
///     the focus adorner, and the transition lists.</description></item>
///  </list>
///
///  <para><b>Brushes are taken by reference, never by value.</b>
///  <see cref="ThemeManager"/> switches theme by mutating each palette brush's
///  <see cref="SolidColorBrush.Color"/> in place, so a copied colour would freeze.
///  The derived state brushes (hover, pressed, disabled) are recomputed by
///  <see cref="Derived"/>, which listens to its sources and re-mixes on every theme
///  change — so this file inherits any palette edit for free and never hard-codes a
///  hex value.</para>
///
///  <para><b>The revision grid is deliberately untouched.</b>
///  <c>RevisionGridView</c> is a virtualized <see cref="ListBox"/> whose rows are
///  custom-drawn by <c>RevisionRowView</c>; a recycled container only receives a new
///  <c>DataContext</c>, so a transition on a row would animate from the previous
///  commit's colours to the next one's on every scroll tick — visible smearing, and
///  a brush animation per row per frame. No selector in this file mentions
///  <see cref="ListBoxItem"/>, <see cref="ListBox"/> or any of the grid's types, and
///  the grid's own row styles stay where they are, scoped to the list instance
///  (<c>RevisionGridView.cs:769</c> and <c>:784</c>).</para>
/// </summary>
public static class ModernStyles
{
    /// <summary>The baseline styles, present in BOTH app styles; added once, never removed.</summary>
    private static Styles? _baseline;

    /// <summary>The modern-only styles; built once, added and removed as a block.</summary>
    private static Styles? _modern;

    /// <summary>The modern Fluent overrides, key -> value; built once.</summary>
    private static Dictionary<string, object>? _modernValues;

    /// <summary>
    ///  What <see cref="Application.Resources"/> held for each overridden key BEFORE
    ///  the first modern install. A key absent from the app dictionary is recorded as
    ///  <c>null</c> and REMOVED on restore — removing is what hands the lookup back to
    ///  Fluent's own <c>ControlTheme</c>; writing a guessed Fluent value back would
    ///  pin a colour Fluent is free to change.
    /// </summary>
    private static readonly Dictionary<string, object?> Snapshot = [];

    private static bool _modernInstalled;

    /// <summary>
    ///  Applies (or removes) the modern control surface. Call from
    ///  <see cref="Application.Initialize"/> and from
    ///  <see cref="ThemeManager.Apply(Avalonia.Styling.ThemeVariant, AppStyle)"/>,
    ///  always after <see cref="ThemeManager.Initialize"/> has registered the
    ///  <c>App.*</c> brushes.
    ///
    ///  <para>Idempotent in both directions, and reversible: the Fluent keys are
    ///  restored to what they held before (or removed), and the modern
    ///  <see cref="Style"/>s are taken OUT of <see cref="Application.Styles"/> rather
    ///  than neutralised. The baseline — the TabItem sizing, which lived in
    ///  <c>App.Initialize</c> before M77 — is part of the classic look too, so it is
    ///  installed separately and is never removed.</para>
    ///
    ///  <para><b>The app's text SIZE is not here.</b> It belongs to
    ///  <see cref="UiScaling"/>, which owns the resource keys and the user-facing
    ///  <see cref="UiSize"/> together (M84). This class used to write the 12px baseline
    ///  itself, which meant two owners for one number the moment the size became an
    ///  option.</para>
    /// </summary>
    public static void Apply(Application app, AppStyle style)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (_baseline is null)
        {
            _baseline = BuildBaseline(app);
            app.Styles.Add(_baseline);
        }

        if (style == AppStyle.Modern)
        {
            InstallModern(app);
        }
        else
        {
            RemoveModern(app);
        }
    }

    private static void RemoveModern(Application app)
    {
        if (!_modernInstalled)
        {
            return;
        }

        foreach ((string key, object? previous) in Snapshot)
        {
            if (previous is null)
            {
                app.Resources.Remove(key);
            }
            else
            {
                app.Resources[key] = previous;
            }
        }

        if (_modern is not null)
        {
            app.Styles.Remove(_modern);
        }

        _modernInstalled = false;
    }

    private static void InstallModern(Application app)
    {
        if (_modernInstalled)
        {
            return;
        }

        // Built once and reused: the derived state brushes listen to the palette for
        // the lifetime of the app, so rebuilding them on every toggle would leak one
        // subscription pair per switch.
        _modernValues ??= BuildValues(app);

        if (_modernValues.Count == 0)
        {
            // ThemeManager did not run — see BuildValues. Leave Fluent alone.
            return;
        }

        foreach ((string key, object value) in _modernValues)
        {
            if (!Snapshot.ContainsKey(key))
            {
                Snapshot[key] = app.Resources.TryGetValue(key, out object? existing) ? existing : null;
            }

            app.Resources[key] = value;
        }

        _modern ??= BuildModern(app);
        app.Styles.Add(_modern);
        _modernInstalled = true;
    }

    /// <summary>
    ///  Collects the Fluent state keys the modern surface redefines, from the palette.
    ///  Returns an EMPTY map if the palette is missing.
    /// </summary>
    private static Dictionary<string, object> BuildValues(Application app)
    {
        Dictionary<string, object> map = [];

        // ---- the palette, by reference -------------------------------------------
        SolidColorBrush? window = P(app, "App.Window");
        SolidColorBrush? panel = P(app, "App.Panel");
        SolidColorBrush? toolbar = P(app, "App.Toolbar");
        SolidColorBrush? border = P(app, "App.Border");
        SolidColorBrush? text = P(app, "App.Text");
        SolidColorBrush? textDim = P(app, "App.TextDim");
        SolidColorBrush? accent = P(app, "App.Accent");
        SolidColorBrush? selection = P(app, "App.Selection");
        SolidColorBrush? control = P(app, "App.Control");

        // Missing palette = ThemeManager did not run. Per the M62 rule, do NOT invent
        // fallbacks: a hard-coded colour stops following the theme and is worse than
        // Fluent's own default.
        if (window is null || panel is null || toolbar is null || border is null
            || text is null || textDim is null || accent is null || selection is null
            || control is null)
        {
            return map;
        }

        // ---- derived state surfaces ----------------------------------------------
        // Every state colour is "the base surface, pulled a little toward the ink".
        // That single rule inverts by itself between themes — App.Text is near-white
        // in dark and near-black in light — so hover is a LIFT on the dark theme and
        // a DARKEN on the light one without a second table of values, and without
        // this file knowing a single hex.
        SolidColorBrush surfaceHover = Derived(toolbar, text, 0.10);
        SolidColorBrush surfacePressed = Derived(toolbar, text, 0.20);
        SolidColorBrush surfaceDisabled = Derived(toolbar, window, 0.55);

        SolidColorBrush inputHover = Derived(control, text, 0.07);
        SolidColorBrush inputPressed = Derived(control, text, 0.14);

        SolidColorBrush panelHover = Derived(panel, text, 0.10);
        SolidColorBrush panelPressed = Derived(panel, text, 0.17);

        SolidColorBrush selectionHover = Derived(selection, text, 0.10);
        SolidColorBrush selectionPressed = Derived(selection, text, 0.18);

        // A border that reads as "this control is under the pointer" is a non-text
        // indicator and needs 3:1 of its own. App.Border is a quiet separator — it
        // measures 1.23:1 on the toolbar in the dark theme — so the hover border pulls
        // it 45% toward the ink. 0.45 is the smallest step on the scale that clears
        // 3:1 against ALL THREE surfaces a control border can land on (App.Toolbar,
        // App.Control, App.Selection) in BOTH themes; the worst case is 3.30:1 on the
        // selection surface. At 0.35 the toolbar case was 2.98:1 dark / 2.73:1 light.
        SolidColorBrush borderStrong = Derived(border, text, 0.45);

        // ---- Button ---------------------------------------------------------------
        // Fluent's stock ButtonBackground* are TRANSLUCENT overlays over whatever is
        // behind the button; on a coloured toolbar that drags the label toward the
        // background and the contrast collapses. These are opaque palette surfaces.
        Set(map, "ButtonBackground", toolbar);
        Set(map, "ButtonBackgroundPointerOver", surfaceHover);
        Set(map, "ButtonBackgroundPressed", surfacePressed);
        Set(map, "ButtonBackgroundDisabled", surfaceDisabled);
        Set(map, "ButtonForeground", text);
        Set(map, "ButtonForegroundPointerOver", text);
        Set(map, "ButtonForegroundPressed", text);
        Set(map, "ButtonForegroundDisabled", textDim);
        Set(map, "ButtonBorderBrush", border);
        Set(map, "ButtonBorderBrushPointerOver", borderStrong);
        // Pressed keeps the HOVER border, not the accent. An accent hairline on the
        // pressed fill measures 2.05:1 in the dark theme (#3B82F6 on #53545B) — below
        // the 3:1 a non-text indicator needs, i.e. a promise the colour cannot keep.
        // Pressed is already unmistakable from the fill alone: the background moves by
        // two full steps of the ramp. The accent is reserved for FOCUS, where it is
        // the only signal and where it does measure (3.57:1 worst case).
        Set(map, "ButtonBorderBrushPressed", borderStrong);
        Set(map, "ButtonBorderBrushDisabled", border);

        // ---- ToggleButton ---------------------------------------------------------
        // Unchecked = a button. Checked = the selection surface, i.e. the same
        // "this one is chosen" colour the lists use, so a pressed-in toolbar toggle
        // and a selected row say the same thing.
        Set(map, "ToggleButtonBackground", toolbar);
        Set(map, "ToggleButtonBackgroundPointerOver", surfaceHover);
        Set(map, "ToggleButtonBackgroundPressed", surfacePressed);
        Set(map, "ToggleButtonBackgroundDisabled", surfaceDisabled);
        Set(map, "ToggleButtonBackgroundChecked", selection);
        Set(map, "ToggleButtonBackgroundCheckedPointerOver", selectionHover);
        Set(map, "ToggleButtonBackgroundCheckedPressed", selectionPressed);
        Set(map, "ToggleButtonBackgroundCheckedDisabled", surfaceDisabled);
        Set(map, "ToggleButtonBackgroundIndeterminate", toolbar);
        Set(map, "ToggleButtonBackgroundIndeterminatePointerOver", surfaceHover);
        Set(map, "ToggleButtonBackgroundIndeterminatePressed", surfacePressed);
        Set(map, "ToggleButtonBackgroundIndeterminateDisabled", surfaceDisabled);
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Checked",
                                         "CheckedPointerOver", "CheckedPressed",
                                         "Indeterminate", "IndeterminatePointerOver",
                                         "IndeterminatePressed" })
        {
            Set(map, $"ToggleButtonForeground{state}", text);
        }

        Set(map, "ToggleButtonForegroundDisabled", textDim);
        Set(map, "ToggleButtonForegroundCheckedDisabled", textDim);
        Set(map, "ToggleButtonForegroundIndeterminateDisabled", textDim);
        Set(map, "ToggleButtonBorderBrush", border);
        Set(map, "ToggleButtonBorderBrushPointerOver", borderStrong);
        Set(map, "ToggleButtonBorderBrushPressed", borderStrong);
        Set(map, "ToggleButtonBorderBrushChecked", accent);
        Set(map, "ToggleButtonBorderBrushCheckedPointerOver", accent);
        Set(map, "ToggleButtonBorderBrushCheckedPressed", accent);
        Set(map, "ToggleButtonBorderBrushDisabled", border);
        Set(map, "ToggleButtonBorderBrushCheckedDisabled", border);
        Set(map, "ToggleButtonBorderBrushIndeterminate", border);
        Set(map, "ToggleButtonBorderBrushIndeterminatePointerOver", borderStrong);
        Set(map, "ToggleButtonBorderBrushIndeterminatePressed", borderStrong);
        Set(map, "ToggleButtonBorderBrushIndeterminateDisabled", border);

        // ---- TextBox --------------------------------------------------------------
        // App.Control is the input surface (M62 registered it for exactly this).
        // Focus is the accent border; Fluent already thickens only the BOTTOM edge
        // (TextControlBorderThemeThicknessFocused = 0,0,0,2), which is why the
        // thickness keys are deliberately left alone here — changing them moves the
        // text inside the box on focus.
        Set(map, "TextControlBackground", control);
        Set(map, "TextControlBackgroundPointerOver", inputHover);
        Set(map, "TextControlBackgroundFocused", control);
        Set(map, "TextControlBackgroundDisabled", surfaceDisabled);
        Set(map, "TextControlForeground", text);
        Set(map, "TextControlForegroundPointerOver", text);
        Set(map, "TextControlForegroundFocused", text);
        Set(map, "TextControlForegroundDisabled", textDim);
        Set(map, "TextControlBorderBrush", border);
        Set(map, "TextControlBorderBrushPointerOver", borderStrong);
        Set(map, "TextControlBorderBrushFocused", accent);
        Set(map, "TextControlBorderBrushDisabled", border);
        Set(map, "TextControlPlaceholderForeground", textDim);
        Set(map, "TextControlPlaceholderForegroundPointerOver", textDim);
        Set(map, "TextControlPlaceholderForegroundFocused", textDim);
        Set(map, "TextControlPlaceholderForegroundDisabled", textDim);

        // Despite the "Color" suffix the control theme feeds this to
        // TextBox.SelectionBrush, so it wants an IBrush (TextBoxSurface.cs:100).
        Set(map, "TextControlSelectionHighlightColor", accent);

        // The inline clear/reveal buttons inside a TextBox.
        Set(map, "TextControlButtonBackground", Brushes.Transparent);
        Set(map, "TextControlButtonBackgroundPointerOver", inputHover);
        Set(map, "TextControlButtonBackgroundPressed", inputPressed);
        Set(map, "TextControlButtonForeground", textDim);
        Set(map, "TextControlButtonForegroundPointerOver", text);
        Set(map, "TextControlButtonForegroundPressed", text);

        // ---- ComboBox -------------------------------------------------------------
        Set(map, "ComboBoxBackground", control);
        Set(map, "ComboBoxBackgroundUnfocused", control);
        Set(map, "ComboBoxBackgroundPointerOver", inputHover);
        Set(map, "ComboBoxBackgroundPressed", inputPressed);
        Set(map, "ComboBoxBackgroundDisabled", surfaceDisabled);
        Set(map, "ComboBoxBackgroundBorderBrushUnfocused", border);
        Set(map, "ComboBoxBackgroundBorderBrushFocused", accent);
        Set(map, "ComboBoxBorderBrush", border);
        Set(map, "ComboBoxBorderBrushPointerOver", borderStrong);
        Set(map, "ComboBoxBorderBrushPressed", borderStrong);
        Set(map, "ComboBoxBorderBrushDisabled", border);
        Set(map, "ComboBoxForeground", text);
        Set(map, "ComboBoxForegroundFocused", text);
        Set(map, "ComboBoxForegroundFocusedPressed", text);
        Set(map, "ComboBoxForegroundDisabled", textDim);
        Set(map, "ComboBoxPlaceHolderForeground", textDim);
        Set(map, "ComboBoxPlaceHolderForegroundFocusedPressed", textDim);
        Set(map, "ComboBoxDropDownBackground", panel);
        Set(map, "ComboBoxDropDownBorderBrush", border);
        Set(map, "ComboBoxDropDownGlyphForeground", textDim);
        Set(map, "ComboBoxDropDownGlyphForegroundFocused", text);
        Set(map, "ComboBoxDropDownGlyphForegroundFocusedPressed", text);
        Set(map, "ComboBoxDropDownGlyphForegroundDisabled", textDim);

        // Drop-down rows sit on App.Panel, so their states derive from the panel,
        // not from the toolbar.
        Set(map, "ComboBoxItemBackground", Brushes.Transparent);
        Set(map, "ComboBoxItemBackgroundPointerOver", panelHover);
        Set(map, "ComboBoxItemBackgroundPressed", panelPressed);
        Set(map, "ComboBoxItemBackgroundDisabled", Brushes.Transparent);
        Set(map, "ComboBoxItemBackgroundSelected", selection);
        Set(map, "ComboBoxItemBackgroundSelectedPointerOver", selectionHover);
        Set(map, "ComboBoxItemBackgroundSelectedPressed", selectionPressed);
        Set(map, "ComboBoxItemBackgroundSelectedDisabled", surfaceDisabled);
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Selected",
                                         "SelectedPointerOver", "SelectedPressed" })
        {
            Set(map, $"ComboBoxItemForeground{state}", text);
        }

        Set(map, "ComboBoxItemForegroundDisabled", textDim);
        Set(map, "ComboBoxItemForegroundSelectedDisabled", textDim);
        Set(map, "ComboBoxItemBorderBrushPointerOver", Brushes.Transparent);
        Set(map, "ComboBoxItemBorderBrushPressed", Brushes.Transparent);
        Set(map, "ComboBoxItemBorderBrushSelected", Brushes.Transparent);
        Set(map, "ComboBoxItemBorderBrushSelectedPointerOver", Brushes.Transparent);
        Set(map, "ComboBoxItemBorderBrushSelectedPressed", Brushes.Transparent);
        Set(map, "ComboBoxItemBorderBrushDisabled", Brushes.Transparent);
        Set(map, "ComboBoxItemBorderBrushSelectedDisabled", Brushes.Transparent);

        // ---- MenuItem / menu flyouts ----------------------------------------------
        // Avalonia's MenuItem ControlTheme resolves the MenuFlyoutItem* family for
        // its own states (the menu bar and the context menus share it).
        Set(map, "MenuFlyoutPresenterBackground", panel);
        Set(map, "MenuFlyoutPresenterBorderBrush", border);
        Set(map, "MenuFlyoutItemBackground", Brushes.Transparent);
        Set(map, "MenuFlyoutItemBackgroundPointerOver", panelHover);
        Set(map, "MenuFlyoutItemBackgroundPressed", panelPressed);
        Set(map, "MenuFlyoutItemBackgroundDisabled", Brushes.Transparent);
        Set(map, "MenuFlyoutItemForeground", text);
        Set(map, "MenuFlyoutItemForegroundPointerOver", text);
        Set(map, "MenuFlyoutItemForegroundPressed", text);
        Set(map, "MenuFlyoutItemForegroundDisabled", textDim);
        Set(map, "MenuFlyoutItemKeyboardAcceleratorTextForeground", textDim);
        Set(map, "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", textDim);
        Set(map, "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", textDim);
        Set(map, "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", textDim);
        Set(map, "MenuFlyoutSubItemChevron", textDim);
        Set(map, "MenuFlyoutSubItemChevronPointerOver", text);
        Set(map, "MenuFlyoutSubItemChevronPressed", text);
        Set(map, "MenuFlyoutSubItemChevronSubMenuOpened", text);
        Set(map, "MenuFlyoutSubItemChevronDisabled", textDim);

        // ---- TabItem ---------------------------------------------------------------
        // KEPT, but no longer what paints the app's tabs: BuildTabItem installs a
        // template of its own in the baseline, with the same palette colours, so these
        // keys only matter to a TabItem that somehow resolves Fluent's template. They
        // are left in place because they are the correct values for the modern surface
        // and removing them would leave that fallback on Fluent's stock greys.
        //
        // Unselected tabs are transparent so they take whatever panel they sit on;
        // the SELECTED one is not louder because it is bigger, it is louder because
        // it is the only one with a surface, full-strength ink, a semibold label and
        // an accent pipe (see the :selected style below). That is the
        // "weight and colour before size" rule from Metrics.Text.
        Set(map, "TabItemHeaderBackgroundUnselected", Brushes.Transparent);
        Set(map, "TabItemHeaderBackgroundUnselectedPointerOver", panelHover);
        Set(map, "TabItemHeaderBackgroundUnselectedPressed", panelPressed);
        Set(map, "TabItemHeaderBackgroundSelected", selection);
        Set(map, "TabItemHeaderBackgroundSelectedPointerOver", selectionHover);
        Set(map, "TabItemHeaderBackgroundSelectedPressed", selectionPressed);
        Set(map, "TabItemHeaderBackgroundDisabled", Brushes.Transparent);
        Set(map, "TabItemHeaderForegroundUnselected", textDim);
        Set(map, "TabItemHeaderForegroundUnselectedPointerOver", text);
        Set(map, "TabItemHeaderForegroundUnselectedPressed", text);
        Set(map, "TabItemHeaderForegroundSelected", text);
        Set(map, "TabItemHeaderForegroundSelectedPointerOver", text);
        Set(map, "TabItemHeaderForegroundSelectedPressed", text);
        Set(map, "TabItemHeaderForegroundDisabled", textDim);
        Set(map, "TabItemHeaderSelectedPipeFill", accent);
        Set(map, "TabItemPipeThickness", 2.0);

        // ---- corners ----------------------------------------------------------------
        // ControlCornerRadius is the key every Fluent input resolves (TextBox,
        // ComboBox, spinners, buttons inside templates), so one assignment rounds the
        // whole input family at Radius.Sm. Buttons get Radius.Md through their own
        // style below — they are standalone surfaces, not parts of a dense row.
        Set(map, "ControlCornerRadius", Metrics.Radius.SmCorner);
        Set(map, "OverlayCornerRadius", Metrics.Radius.MdCorner);

        // ---- focus ring ------------------------------------------------------------
        // Fluent's own focus visual is a thin two-tone rectangle that all but vanishes
        // on the dark palette. Replace it, do not remove it: 2px of App.Accent, drawn
        // by the ADORNER layer, which is outside layout — so nothing shifts when a
        // control takes focus (a focus ring made of BorderThickness would move the
        // label inside the button by a pixel).
        Set(map, "SystemControlFocusVisualPrimaryBrush", accent);
        Set(map, "SystemControlFocusVisualSecondaryBrush", accent);
        Set(map, "SystemControlFocusVisualPrimaryThickness", new Thickness(FocusRingThickness));
        Set(map, "SystemControlFocusVisualSecondaryThickness", new Thickness(0));
        Set(map, "UseSystemFocusVisuals", true);

        return map;
    }

    /// <summary>
    ///  The modern-only <see cref="Style"/>s, as one <see cref="Styles"/> collection so
    ///  they can be added to and removed from <see cref="Application.Styles"/> as a
    ///  single block. The baseline styles are NOT in here — see
    ///  <see cref="BuildBaseline"/>.
    /// </summary>
    private static Styles BuildModern(Application app)
    {
        // Same instances the palette registered: the ring follows the theme.
        SolidColorBrush accent = P(app, "App.Accent") ?? new SolidColorBrush(Colors.Transparent);
        SolidColorBrush text = P(app, "App.Text") ?? new SolidColorBrush(Colors.Transparent);

        // Two-tone on purpose, and the second tone is not decoration — it is what makes
        // the ring measurable on BOTH of its edges.
        //
        // The outer 2px of App.Accent is adjacent to whatever CONTAINER the control
        // sits in, and clears 3:1 on every one of them (worst case 3.57:1, the dark
        // toolbar). Its inner edge, though, touches the control's own fill, and a
        // focused+hovered button in the dark theme puts the accent on #41424A at
        // 2.72:1 — a fail. The 1px App.Text hairline inside the ring restores that
        // edge to 5.94:1 at worst (App.Text on the pressed fill), so the indicator as
        // a whole is separated from its surroundings on both sides whatever state the
        // control is in.
        FuncTemplate<Control> focusRing = new(() => new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(FocusRingThickness),
            CornerRadius = Metrics.Radius.MdCorner,
            // The adorner is laid over the control; a small negative margin puts the
            // ring just OUTSIDE the control's own border instead of on top of it, so
            // a focused-and-hovered control still shows both.
            Margin = new Thickness(-FocusRingThickness),
            Child = new Border
            {
                BorderBrush = text,
                BorderThickness = new Thickness(FocusRingHaloThickness),
                CornerRadius = Metrics.Radius.SmCorner,
            },
        });

        return Build(focusRing);
    }

    /// <summary>2px — thick enough to survive a 1px control border next to it.</summary>
    private const double FocusRingThickness = 2;

    /// <summary>1px of App.Text inside the accent, so the ring's inner edge measures
    /// against the control's own fill too.</summary>
    private const double FocusRingHaloThickness = 1;

    /// <summary>
    ///  The global <see cref="Style"/>s, as one <see cref="Styles"/> collection so
    ///  they are registered in a single, ordered block after Fluent.
    /// </summary>
    private static Styles BuildBaseline(Application app)
    {
        Styles styles = [];

        // ---- typography -------------------------------------------------------------
        // THIS IS NOT MODERN. The TabItem style was in App.Initialize before M77, i.e.
        // it is part of the classic look as much as of the modern one, and M77 only
        // relocated it. It is therefore installed once and never removed: if the
        // style-restore took it away, switching to Classic would give a look the app
        // never had — Fluent's oversized tab headers.
        //
        // M81 REMOVED the companion `TextBlock -> FontSize 13` style that used to sit
        // here. Its job was to undo Fluent's 14px default, and the chrome font size
        // resource now does that at the source, for the chrome AND for bare TextBlocks
        // alike (measured: with ControlContentThemeFontSize = 12, an unstyled TextBlock
        // reports 12). Keeping the style would have re-raised prose to 13 while every
        // button and menu item around it sat at upstream's 12 — two sources for one
        // number, disagreeing. The app default comes from one place: UiScaling.
        //
        // NO FontSize SETTER HERE, and that is the point (M84). Fluent's tab headers are
        // oversized for a dense tool (its TabItemHeaderFontSize is 24), so the port has
        // always overridden them — but a literal here overrode the user's chosen size
        // too, and measured it did: with the chrome key at 15 every control reported 15
        // and TabItem alone still reported 12. UiScaling writes TabItemHeaderFontSize
        // instead, which Fluent's own template reads, so the strip lands on upstream's 12
        // at Normal and follows the option everywhere else. Only the weight and the
        // metrics stay here.
        Style tabItem = new(x => x.OfType<TabItem>());
        tabItem.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, Metrics.Text.SubtitleWeight));
        // 12,6: the 6 is off the Space scale (Metrics.Space documents it as one of the
        // two values to retire). Kept exactly as it was because moving this style out
        // of App.Initialize must not change a single pixel; raising it to Space.Sm
        // would make every tab strip in the app 4px taller. It is a one-line change
        // for whoever does the view conversion pass.
        tabItem.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(Metrics.Space.Md, 6)));
        tabItem.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0.0));
        styles.Add(tabItem);

        styles.AddRange(BuildTabItem(app));

        return styles;
    }

    /// <summary>2px — the selected tab's top bar, and the reason the selection marker
    /// can no longer touch the label: it owns a layout ROW of its own.</summary>
    private const double TabSelectedBarThickness = 2;

    /// <summary>
    ///  The tab header look, for BOTH app styles.
    ///
    ///  <para><b>Why a template of our own.</b> Fluent marks the selected tab with
    ///  <c>PART_SelectedPipe</c>, a 2px bar that lives in the SAME <see cref="Panel"/>
    ///  as the content presenter and is simply aligned to the header's inner edge —
    ///  i.e. it is drawn OVER the label's cell, not beside it. That is harmless at
    ///  Fluent's own 48px <c>TabItemMinHeight</c>, where the centred label leaves
    ///  ~17px of slack under it, but this app deliberately sets
    ///  <c>MinHeight = 0</c> and a 6px vertical padding (above) for a dense strip, and
    ///  at that size the pipe lands on the text — the blue line through the tab
    ///  titles. No padding value fixes it: the pipe is positioned against the header
    ///  edge, so growing the padding grows the gap on the wrong side. The fix is
    ///  structural: put the marker in its own <see cref="Grid"/> row, so layout — not
    ///  luck — keeps it off the label at every font size.</para>
    ///
    ///  <para><b>What it looks like, and why.</b> Upstream's WinForms strip
    ///  (<c>TabControlPaintContext</c>) does not mark the selected tab with a hairline
    ///  at all: it FILLS it with the page colour, gives it a border on top and both
    ///  sides but never on the bottom, and grows it a couple of pixels so it merges
    ///  into the page body while its siblings sit behind a line. This template keeps
    ///  that reading — filled surface, border on three sides, open at the bottom —
    ///  and adds an accent bar on the top edge instead of upstream's size bump, so
    ///  nothing in the strip moves when the selection changes.</para>
    ///
    ///  <para>Every colour comes from the palette by reference; if the palette is
    ///  missing the whole block is skipped and Fluent keeps the tabs.</para>
    /// </summary>
    private static Styles BuildTabItem(Application app)
    {
        Styles styles = [];

        SolidColorBrush? window = P(app, "App.Window");
        SolidColorBrush? border = P(app, "App.Border");
        SolidColorBrush? text = P(app, "App.Text");
        SolidColorBrush? textDim = P(app, "App.TextDim");
        SolidColorBrush? accent = P(app, "App.Accent");
        SolidColorBrush? selection = P(app, "App.Selection");

        if (window is null || border is null || text is null
            || textDim is null || accent is null || selection is null)
        {
            return styles;
        }

        // Hover on an UNSELECTED tab: the strip surface pulled toward the ink, the
        // same rule the rest of this file uses, so it inverts by itself between
        // themes. Hover must stay quieter than the selected fill — it is a pointer
        // echo, not a second selection.
        SolidColorBrush stripHover = Derived(window, text, 0.08);
        SolidColorBrush selectionHover = Derived(selection, text, 0.10);

        // Rounded on the two edges that face away from the page body only; the bottom
        // corners stay square because the selected tab is meant to run INTO the body.
        CornerRadius topCorners = new(Metrics.Radius.Sm / 2, Metrics.Radius.Sm / 2, 0, 0);

        FuncControlTemplate<TabItem> template = new((tab, scope) =>
        {
            Border bar = new()
            {
                Name = "PART_SelectedBar",
                Height = TabSelectedBarThickness,
                // Transparent, not collapsed: the row is reserved on EVERY tab, so
                // selecting one does not shift its label by 2px.
                Background = Brushes.Transparent,
            };
            bar.RegisterInNameScope(scope);

            ContentPresenter header = new()
            {
                Name = "PART_ContentPresenter",
                RecognizesAccessKey = true,
            };
            header.RegisterInNameScope(scope);
            header[!ContentPresenter.ContentProperty] = tab[!HeaderedContentControl.HeaderProperty];
            header[!ContentPresenter.ContentTemplateProperty] = tab[!HeaderedContentControl.HeaderTemplateProperty];
            header[!ContentPresenter.PaddingProperty] = tab[!TemplatedControl.PaddingProperty];
            header[!ContentPresenter.HorizontalContentAlignmentProperty] =
                tab[!ContentControl.HorizontalContentAlignmentProperty];
            header[!ContentPresenter.VerticalContentAlignmentProperty] =
                tab[!ContentControl.VerticalContentAlignmentProperty];
            header[!ContentPresenter.FontFamilyProperty] = tab[!TemplatedControl.FontFamilyProperty];
            header[!ContentPresenter.FontSizeProperty] = tab[!TemplatedControl.FontSizeProperty];
            header[!ContentPresenter.FontWeightProperty] = tab[!TemplatedControl.FontWeightProperty];
            header[!ContentPresenter.ForegroundProperty] = tab[!TemplatedControl.ForegroundProperty];

            Grid layout = new()
            {
                RowDefinitions = new RowDefinitions
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(new GridLength(1, GridUnitType.Star)),
                },
            };
            Grid.SetRow(bar, 0);
            Grid.SetRow(header, 1);
            layout.Children.Add(bar);
            layout.Children.Add(header);

            Border root = new()
            {
                Name = "PART_LayoutRoot",
                CornerRadius = topCorners,
                Child = layout,
            };
            root.RegisterInNameScope(scope);
            root[!Border.BackgroundProperty] = tab[!TemplatedControl.BackgroundProperty];
            root[!Border.BorderBrushProperty] = tab[!TemplatedControl.BorderBrushProperty];
            root[!Border.BorderThicknessProperty] = tab[!TemplatedControl.BorderThicknessProperty];
            return root;
        });

        // ---- the control itself: template + the unselected resting state -----------
        // BorderThickness is the same on every tab so that turning the border ON is a
        // colour change, not a layout change: no tab ever moves by a pixel. The bottom
        // edge is 0 on purpose — that is what lets the selected tab join the page.
        Style tab = new(x => x.OfType<TabItem>());
        tab.Setters.Add(new Setter(TemplatedControl.TemplateProperty, template));
        tab.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        tab.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent));
        tab.Setters.Add(new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1, 1, 1, 0)));
        tab.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, textDim));
        styles.Add(tab);

        Style hover = new(x => x.OfType<TabItem>().Class(":pointerover"));
        hover.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, stripHover));
        hover.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, border));
        hover.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, text));
        styles.Add(hover);

        // ---- selected --------------------------------------------------------------
        // Four independent signals, none of them a hairline: the App.Selection fill,
        // the App.Accent border on three sides, the 2px accent bar in its own row, and
        // full-strength ink at SemiBold. App.Text on App.Selection measures 13.9:1
        // (light) and 10.4:1 (dark); the accent border's OUTER edge, which is what
        // locates the tab against the strip, measures 3.95:1 on App.Window light and
        // 3.72:1 dark — the same 3:1 floor the focus ring was held to.
        //
        // The focus ring stays distinct because it is a different SHAPE in a different
        // layer: a 2px accent rectangle with an App.Text halo, drawn all the way round
        // the tab by the adorner layer, over a tab that is already filled or not.
        Style selected = new(x => x.OfType<TabItem>().Class(":selected"));
        selected.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, selection));
        selected.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, accent));
        selected.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, text));
        selected.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, Metrics.Text.ActiveWeight));
        styles.Add(selected);

        Style selectedHover = new(x => x.OfType<TabItem>().Class(":selected").Class(":pointerover"));
        selectedHover.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, selectionHover));
        selectedHover.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, accent));
        styles.Add(selectedHover);

        Style bar = new(x => x.OfType<TabItem>().Class(":selected")
            .Template().OfType<Border>().Name("PART_SelectedBar"));
        bar.Setters.Add(new Setter(Border.BackgroundProperty, accent));
        styles.Add(bar);

        Style disabled = new(x => x.OfType<TabItem>().Class(":disabled"));
        disabled.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        disabled.Setters.Add(new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent));
        disabled.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, textDim));
        styles.Add(disabled);

        return styles;
    }

    /// <summary>
    ///  The global <see cref="Style"/>s that belong to the modern style only.
    /// </summary>
    private static Styles Build(ITemplate<Control> focusRing)
    {
        Styles styles = [];

        // NOTE: the selected tab's weight, fill, border and marker used to be split
        // between here and the Fluent TabItemHeader* keys. They now all live in
        // BuildTabItem, in the BASELINE, because the classic style had the same defect
        // (a hairline pipe drawn across the label) and a bug fix must not depend on
        // which style the user picked.

        // ---- corners ------------------------------------------------------------------
        Style button = new(x => x.OfType<Button>());
        button.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, Metrics.Radius.MdCorner));
        styles.Add(button);

        Style toggle = new(x => x.OfType<ToggleButton>());
        toggle.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, Metrics.Radius.MdCorner));
        styles.Add(toggle);

        Style textBox = new(x => x.OfType<TextBox>());
        textBox.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, Metrics.Radius.SmCorner));
        styles.Add(textBox);

        Style comboBox = new(x => x.OfType<ComboBox>());
        comboBox.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, Metrics.Radius.SmCorner));
        styles.Add(comboBox);

        // ---- focus ring ------------------------------------------------------------
        // FocusAdorner is a property of Control, so a style setter reaches it without
        // touching any template child — and the adorner draws in the adorner layer,
        // so it cannot shift layout.
        Style focusable = new(x => Selectors.Or(
            x.OfType<Button>(),
            x.OfType<ToggleButton>(),
            x.OfType<ComboBox>(),
            x.OfType<TextBox>(),
            x.OfType<TabItem>()));
        focusable.Setters.Add(new Setter(Control.FocusAdornerProperty, focusRing));
        styles.Add(focusable);

        // ---- transitions ---------------------------------------------------------------
        // Only Background / BorderBrush / Foreground / Opacity, per Metrics.Motion:
        // never a layout property. The transitions go on the TEMPLATE CHILD that
        // Fluent actually repaints — the control's own Background never changes on
        // hover, the presenter's does, so a transition on the control would animate
        // nothing.
        //
        // ONE easing, not one per direction. A direction-dependent easing would mean
        // swapping the Transitions collection from the :pointerover style, and that
        // setter is applied by a style instance that activates AFTER Fluent's
        // ControlTheme has already changed the background — the swap would always be
        // one state change late, and the revert order on de-activation is not
        // specified. EaseOut is the one that is felt (it governs the state the user
        // is moving into); Opacity, which is only ever a fade-out here, gets EaseIn.
        styles.Add(PresenterTransitions<Button>());
        styles.Add(PresenterTransitions<ToggleButton>());
        styles.Add(BorderTransitions<TabItem>());
        styles.Add(BorderTransitions<ComboBox>());
        styles.Add(BorderTransitions<TextBox>());
        styles.Add(BorderTransitions<MenuItem>());

        return styles;
    }

    /// <summary>Cross-fades the <see cref="ContentPresenter"/> Fluent repaints for
    /// button-like controls.</summary>
    private static Style PresenterTransitions<T>() where T : TemplatedControl
    {
        Style style = new(x => x.OfType<T>().Template().OfType<ContentPresenter>());
        style.Setters.Add(new Setter(Animatable.TransitionsProperty, new Transitions
        {
            new BrushTransition
            {
                Property = ContentPresenter.BackgroundProperty,
                Duration = Metrics.Motion.Normal,
                Easing = Metrics.Motion.EaseOut,
            },
            new BrushTransition
            {
                Property = ContentPresenter.BorderBrushProperty,
                Duration = Metrics.Motion.Normal,
                Easing = Metrics.Motion.EaseOut,
            },
            new BrushTransition
            {
                Property = ContentPresenter.ForegroundProperty,
                Duration = Metrics.Motion.Normal,
                Easing = Metrics.Motion.EaseOut,
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = Metrics.Motion.Fast,
                Easing = Metrics.Motion.EaseIn,
            },
        }));
        return style;
    }

    /// <summary>Cross-fades the template-root <see cref="Border"/> Fluent repaints for
    /// tabs, inputs and menu items.</summary>
    private static Style BorderTransitions<T>() where T : TemplatedControl
    {
        Style style = new(x => x.OfType<T>().Template().OfType<Border>());
        style.Setters.Add(new Setter(Animatable.TransitionsProperty, new Transitions
        {
            new BrushTransition
            {
                Property = Border.BackgroundProperty,
                Duration = Metrics.Motion.Normal,
                Easing = Metrics.Motion.EaseOut,
            },
            new BrushTransition
            {
                Property = Border.BorderBrushProperty,
                Duration = Metrics.Motion.Normal,
                Easing = Metrics.Motion.EaseOut,
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = Metrics.Motion.Fast,
                Easing = Metrics.Motion.EaseIn,
            },
        }));
        return style;
    }

    // -------------------------------------------------------------------------------
    //  Derived brushes
    // -------------------------------------------------------------------------------

    /// <summary>
    ///  Keeps the derived brushes and their subscriptions alive for the lifetime of
    ///  the app: a derived brush is only correct as long as it is still listening to
    ///  its sources.
    /// </summary>
    private static readonly List<SolidColorBrush> Live = [];

    /// <summary>
    ///  A brush that is always <paramref name="from"/> mixed <paramref name="amount"/>
    ///  of the way toward <paramref name="to"/>, recomputed whenever either source
    ///  changes colour.
    ///
    ///  <para>This is what makes the state colours theme-proof without copying a hex
    ///  and without touching <see cref="ThemeManager"/>: the palette brushes are
    ///  <see cref="AvaloniaObject"/>s, so mutating <c>Color</c> in place raises
    ///  <see cref="AvaloniaObject.PropertyChanged"/>, and the derived brush re-mixes
    ///  from the NEW values. A palette edit by anyone else is inherited for
    ///  free.</para>
    /// </summary>
    private static SolidColorBrush Derived(SolidColorBrush from, SolidColorBrush to, double amount)
    {
        SolidColorBrush result = new(Mix(from.Color, to.Color, amount));

        void Recompute(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == SolidColorBrush.ColorProperty)
            {
                result.Color = Mix(from.Color, to.Color, amount);
            }
        }

        from.PropertyChanged += Recompute;
        to.PropertyChanged += Recompute;

        Live.Add(result);
        return result;
    }

    /// <summary>
    ///  Linear mix in sRGB. Deliberately naive: the amounts used here are 7–55%
    ///  between two colours that are already close in lightness, where a gamma-correct
    ///  mix and a naive one differ by less than one 8-bit step — and every result is
    ///  contrast-measured anyway (see NOTES.md).
    /// </summary>
    private static Color Mix(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            from.A,
            (byte)Math.Round((from.R * (1 - t)) + (to.R * t)),
            (byte)Math.Round((from.G * (1 - t)) + (to.G * t)),
            (byte)Math.Round((from.B * (1 - t)) + (to.B * t)));
    }

    /// <summary>The palette brush for <paramref name="key"/>, or null.</summary>
    private static SolidColorBrush? P(Application app, string key)
        => app.Resources.TryGetResource(key, null, out object? value) ? value as SolidColorBrush : null;

    /// <summary>Records a Fluent resource key the modern surface redefines.</summary>
    private static void Set(Dictionary<string, object> map, string key, object value)
        => map[key] = value;
}
