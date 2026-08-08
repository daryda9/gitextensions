using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Theming;

// Both are also names in System.IO / System.Drawing territory that the implicit usings
// bring in, so the shapes are named explicitly.
using Path = Avalonia.Controls.Shapes.Path;
using Shape = Avalonia.Controls.Shapes.Shape;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The client-side title bar: the whole main menu, the window caption and the three
///  window buttons, all on the ONE row that the desktop would otherwise have drawn for
///  us — the arrangement VS Code uses.
/// </summary>
/// <remarks>
///  <para><b>Why the app has to draw the buttons.</b> Avalonia 11.3's X11 backend does
///  not implement <c>ExtendClientAreaToDecorationsHint</c> at all: setting it leaves
///  <c>IsExtendedIntoWindowDecorations</c> false and mutter keeps its own 37px frame, so
///  there is no "extended client area" mode to put a menu into. What X11 does honour is
///  <see cref="SystemDecorations"/>: <c>None</c> drops the frame outright. That is
///  all-or-nothing — no frame means no system buttons, no drag area and no resize
///  border — so everything the frame used to provide is rebuilt here and in
///  <c>MainWindow.ApplyWindowChrome</c>.</para>
///
///  <para><b>It is a choice, not a style.</b> This control exists while the Appearance
///  option asks for the merged bar (<see cref="Theming.WindowChrome"/>), in Modern and
///  in Classic alike — every colour here comes from the live palette, so it wears
///  whichever surface is in force. "Separate menu bar" takes it off again and gives the
///  desktop's own title bar back, with the menu on the row below.</para>
///
///  <para><b>The overflow is a measurement.</b> The bar reserves the window buttons and
///  a slice for the caption, hands the remainder to <see cref="MainMenu.FitTo"/>, and
///  the menu itself decides — from the measured width of its own entries — how many go
///  into its "…". Nothing here knows a breakpoint, and a resize simply re-runs the
///  measure.</para>
/// </remarks>
internal sealed class TitleBar : UserControl
{
    /// <summary>
    ///  Bar height. Matches the 37px mutter draws for a normal window, so switching
    ///  styles does not move the whole UI up or down by a few pixels.
    /// </summary>
    internal const double BarHeight = 37;

    /// <summary>Width of one window button — the GNOME/VS Code proportion of the row.</summary>
    private const double ButtonWidth = 46;

    /// <summary>
    ///  How much width the caption is kept, at most, before the menu starts to
    ///  overflow. Enough for a repository name plus a branch; past that the caption
    ///  ellipsises rather than pushing more entries into the "…".
    /// </summary>
    private const double CaptionReserve = 220;

    private readonly Window _window;
    private readonly MainMenu _menu;
    private readonly TextBlock _caption;
    private readonly Panel _buttons;
    private readonly Path _maximizeGlyph;
    private readonly BarLayout _layout;

    internal TitleBar(Window window, MainMenu menu)
    {
        _window = window;
        _menu = menu;

        Height = BarHeight;
        Background = Brush("App.Toolbar", "#333337");
        ClipToBounds = true;
        InstallStyles(Styles);

        // The menu's own background is left alone: it paints App.Toolbar, which is what
        // this row paints too, so it already disappears into the bar — and leaving it in
        // place is what lets the menu go back to the standard layout looking like a menu
        // bar again. Only the height is taken over, so the entries fill the row.
        _menu.VerticalAlignment = VerticalAlignment.Stretch;

        _caption = new TextBlock
        {
            Foreground = Brush("App.TextDim", "#9B9B9B"),
            FontSize = Metrics.Text.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            // The caption is decoration: a screen reader gets the window title from the
            // window itself, and reading it twice per focus change helps nobody.
            IsHitTestVisible = false,
            Text = window.Title,
        };

        Button minimize = WindowButton("M 0,5.5 H 11", () => _window.WindowState = WindowState.Minimized);
        Button maximize = WindowButton(RestoreGlyph, ToggleMaximized);
        Button close = WindowButton("M 0,0 L 10,10 M 10,0 L 0,10", _window.Close);
        close.Classes.Add(CloseClass);
        _maximizeGlyph = (Path)((Decorator)maximize.Content!).Child!;

        _buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Stretch,
            Children = { minimize, maximize, close },
        };

        _layout = new BarLayout(_menu, _caption, _buttons);
        Content = _layout;

        // The caption follows the window's own Title, which MainWindow already keeps in
        // step with the open repository — so "preserve the repo caption" costs one
        // subscription and no second source of truth.
        _window.PropertyChanged += OnWindowPropertyChanged;
        UpdateMaximizeGlyph();
    }

    /// <summary>
    ///  Gives the menu back and stops following the window. Called when the bar is taken
    ///  off — a switch to the separate menu bar. Handing the menu over HERE lets the host add
    ///  it to its own dock: a control has one visual parent, so it has to leave this one
    ///  first, and only this class knows where inside itself it was put.
    /// </summary>
    internal void Detach()
    {
        _window.PropertyChanged -= OnWindowPropertyChanged;
        _layout.Children.Remove(_menu);
        _menu.ClearValue(VerticalAlignmentProperty);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.TitleProperty)
        {
            _caption.Text = _window.Title;
        }
        else if (e.Property == Window.WindowStateProperty)
        {
            UpdateMaximizeGlyph();
        }
    }

    // ---- window moving ---------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // Only the empty parts of the bar are a drag handle: a press that landed on a
        // menu entry or a window button belongs to that control. The caption is not
        // hit-testable, so a press over it arrives here and drags, which is what every
        // desktop does.
        if (e.Handled
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>() is not null
            || (e.Source as Visual)?.FindAncestorOfType<MenuItem>() is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }

        _window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void ToggleMaximized()
        => _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    // The restore glyph is the two offset outlines every desktop draws for "this window
    // is maximized, put it back"; the maximize glyph is the single outline.
    private const string MaximizeGlyph = "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z";
    private const string RestoreGlyph = "M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z M 2.5,2.5 V 0.5 H 9.5 V 7.5 H 7.5";

    private void UpdateMaximizeGlyph()
        => _maximizeGlyph.Data = Geometry.Parse(
            _window.WindowState == WindowState.Maximized ? RestoreGlyph : MaximizeGlyph);

    // ---- the three buttons ------------------------------------------------------------

    /// <summary>The class the close button carries, so its hover can be red.</summary>
    private const string CloseClass = "winclose";

    /// <summary>The class all three carry: a full-height, square-cornered bar button.</summary>
    private const string WindowClass = "winbtn";

    private static Button WindowButton(string glyph, Action action)
    {
        Path path = new()
        {
            Data = Geometry.Parse(glyph),
            Stroke = Brush("App.Text", "#DCDCDC"),
            StrokeThickness = 1,
            Stretch = Stretch.None,
        };

        Button button = new()
        {
            Width = ButtonWidth,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            Focusable = false,
            // A Decorator, not the Path itself: the glyphs are drawn on their own tiny
            // coordinate grid (Stretch.None), so centring is the wrapper's job.
            Content = new Border { Child = path, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        button.Classes.Add(WindowClass);
        button.Click += (_, _) => action();
        return button;
    }

    // Same technique as Theming/BarButtonStyles — Fluent paints a button's chrome
    // through its inner ContentPresenter, so that is the part to set — but square and
    // full height, because a window button reaches the edges of the row it is in.
    private static void InstallStyles(Styles styles)
    {
        IBrush hover = Brush("App.Hover", "#444448");
        IBrush pressed = Brush("App.Pressed", "#555558");

        styles.Add(Chrome(null, WindowClass,
            new Setter(ContentPresenter.BackgroundProperty, Fade(hover)),
            new Setter(Animatable.TransitionsProperty, new Transitions()),
            new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
            new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(0))));
        styles.Add(Chrome(":pointerover", WindowClass, new Setter(ContentPresenter.BackgroundProperty, hover)));
        styles.Add(Chrome(":pressed", WindowClass, new Setter(ContentPresenter.BackgroundProperty, pressed)));

        // Close is red under the pointer on every desktop, and the glyph goes white so
        // it keeps its contrast on that fill in both themes.
        styles.Add(Chrome(":pointerover", CloseClass, new Setter(ContentPresenter.BackgroundProperty, SolidColorBrush.Parse("#C42B1C"))));
        styles.Add(Chrome(":pressed", CloseClass, new Setter(ContentPresenter.BackgroundProperty, SolidColorBrush.Parse("#96271A"))));

        Style closeGlyph = new(x => x.OfType<Button>().Class(CloseClass).Class(":pointerover").Descendant().OfType<Path>());
        closeGlyph.Setters.Add(new Setter(Shape.StrokeProperty, Brushes.White));
        styles.Add(closeGlyph);
    }

    private static Style Chrome(string? state, string cssClass, params Setter[] setters)
    {
        Style style = new(x =>
        {
            Selector selector = x.OfType<Button>().Class(cssClass);
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

    // ---- layout ------------------------------------------------------------------------

    /// <summary>
    ///  Lays the row out as menu | caption | window buttons, and is where the overflow
    ///  budget is decided.
    /// </summary>
    /// <remarks>
    ///  A hand-written panel rather than a Grid: the caption wants to be centred on the
    ///  WINDOW, not on the gap left over between the other two, and it must give that up
    ///  — sliding, then ellipsising — as the menu grows towards it. No arrangement of
    ///  star columns expresses that.
    /// </remarks>
    private sealed class BarLayout : Panel
    {
        private readonly MainMenu _menu;
        private readonly TextBlock _caption;
        private readonly Control _buttons;
        private double _menuWidth;
        private double _captionWidth;

        internal BarLayout(MainMenu menu, TextBlock caption, Control buttons)
        {
            _menu = menu;
            _caption = caption;
            _buttons = buttons;
            ClipToBounds = true;
            Children.Add(menu);
            Children.Add(caption);
            Children.Add(buttons);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;

            _buttons.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            _caption.Measure(new Size(double.PositiveInfinity, availableSize.Height));

            double buttonsWidth = _buttons.DesiredSize.Width;
            double captionWanted = Math.Min(_caption.DesiredSize.Width, CaptionReserve);

            // Measure the menu unconstrained FIRST: that is the pass which gives every
            // entry still on the bar its natural width, and FitTo reads exactly those
            // widths back out. Skipping it would leave the very first fit working off an
            // empty cache.
            _menu.Measure(new Size(double.PositiveInfinity, availableSize.Height));

            double gap = Metrics.Space.Sm;
            double budget = Math.Max(0, width - buttonsWidth - captionWanted - (gap * 2));
            _menuWidth = _menu.FitTo(budget);
            _menu.Measure(new Size(_menuWidth, availableSize.Height));

            _captionWidth = Math.Max(0, Math.Min(
                _caption.DesiredSize.Width,
                width - buttonsWidth - _menuWidth - (gap * 2)));
            _caption.Measure(new Size(_captionWidth, availableSize.Height));

            return new Size(width, availableSize.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double gap = Metrics.Space.Sm;
            double buttonsWidth = _buttons.DesiredSize.Width;

            _menu.Arrange(new Rect(0, 0, _menuWidth, finalSize.Height));
            _buttons.Arrange(new Rect(finalSize.Width - buttonsWidth, 0, buttonsWidth, finalSize.Height));

            // Centred on the WINDOW while that does not run into either neighbour —
            // which is what the eye reads as a title bar. Once the menu is long enough
            // to reach the middle, the caption falls back to the centre of what is left
            // rather than being shoved hard against the menu.
            double left = _menuWidth + gap;
            double right = Math.Max(left, finalSize.Width - buttonsWidth - gap);
            double centred = (finalSize.Width - _captionWidth) / 2;
            double x = centred >= left && centred + _captionWidth <= right
                ? centred
                : left + (Math.Max(0, right - left - _captionWidth) / 2);
            _caption.Arrange(new Rect(x, 0, _captionWidth, finalSize.Height));

            return finalSize;
        }
    }
}
