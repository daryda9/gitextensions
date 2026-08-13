using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The image counterpart of <see cref="DiffToolWindow"/>: two versions of one
///  binary image, compared <b>as images</b> rather than as the unreadable text a
///  textual diff makes of a PNG.
///
///  <para><b>Original work for the port.</b> Upstream Git Extensions shows the two
///  versions in a WinForms picture box pair; there is nothing to port, and on Linux
///  there was until now no configured external tool either — comparing two PNGs was
///  simply impossible. The three modes are the three questions one actually asks of
///  a changed image, and each answers one the others cannot:</para>
///
///  <list type="bullet">
///   <item><description><b>Side by side</b> — what changed in content. Its zoom and
///     scroll are <em>shared</em>, because two panes that pan independently stop being
///     a comparison after the first drag.</description></item>
///   <item><description><b>Overlay</b> — a shift of a few pixels. Nothing else shows
///     it: side by side the two look identical, and the difference mode turns a shift
///     into two ghost outlines without saying which way it moved.</description></item>
///   <item><description><b>Difference</b> — <em>where</em> it changed, down to the
///     pixel, including changes too faint for the eye to find by scanning.</description></item>
///  </list>
///
///  <para><b>Never interpolate.</b> Past 1:1 every image control here is set to
///  <see cref="BitmapInterpolationMode.None"/>: at 8× a pixel must stay a square,
///  since the whole reason to zoom that far is to see <em>which</em> pixel changed,
///  and a smooth resampler averages exactly that away.</para>
///
///  <para><b>Mismatched sizes are reported, not refused.</b> A resized image is a
///  perfectly ordinary commit; the difference is computed over the union of the two
///  frames, aligned top-left, and the area that exists in only one of them is painted
///  in its own colour and named in the information bar.</para>
/// </summary>
public sealed class ImageDiffWindow : ZoomWindow
{
    /// <summary>Zoom bounds. 32× is where one source pixel fills a small tile.</summary>
    private const double MinZoom = 0.05;
    private const double MaxZoom = 32;

    /// <summary>
    ///  The pixel budget for the difference pass. 16 megapixels is a 4000×4000 image:
    ///  the loop itself is fast, but the three Bgra buffers it needs are 64 MB each, and
    ///  a window that quietly allocates 200 MB is worse than one that says no.
    /// </summary>
    private const long MaxDiffPixels = 16_000_000;

    private readonly ImageSide _left;
    private readonly ImageSide _right;

    private readonly Border _host = new();
    private readonly TextBlock _info = new();
    private readonly TextBlock _zoomLabel = new();
    private readonly Slider _opacity;
    private readonly StackPanel _opacityBox;
    private readonly ToggleButton _sideBySideButton;
    private readonly ToggleButton _overlayButton;
    private readonly ToggleButton _differenceButton;

    private readonly List<Pane> _panes = [];

    private ViewMode _mode = ViewMode.SideBySide;
    private double _zoom = 1;
    private bool _fit = true;
    private bool _syncing;

    private DiffResult? _diff;
    private bool _diffRequested;

    private ImageDiffWindow(ImageSide left, ImageSide right)
    {
        _left = left;
        _right = right;

        Title = T("Compare images") + " — " + left.Title + " ↔ " + right.Title;
        Width = 1180;
        Height = 760;
        MinWidth = 560;
        MinHeight = 360;
        Background = Brush("App.Window", Brushes.Black);

        _opacity = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0.5,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = T("Opacity of the second image"),
        };
        _opacity.PropertyChanged += OnOpacityChanged;

        _opacityBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            IsVisible = false,
            Children =
            {
                Caption(left.Title),
                _opacity,
                Caption(right.Title),
            },
        };

        _sideBySideButton = ModeButton(T("Side by side"), ViewMode.SideBySide);
        _overlayButton = ModeButton(T("Overlay"), ViewMode.Overlay);
        _differenceButton = ModeButton(T("Difference"), ViewMode.Difference);

        Content = BuildLayout();

        // The host has no size until the first layout pass, so "fit" cannot be computed
        // in the constructor; it is computed from the first SizeChanged instead.
        _host.SizeChanged += (_, _) =>
        {
            if (_fit)
            {
                ApplyFit();
            }
        };

        SetMode(ViewMode.SideBySide);
    }

    /// <summary>
    ///  Opens the comparison and returns <see langword="null"/> when it was shown, or a
    ///  message explaining why it could not be.
    ///
    ///  <para>Either side may be <see langword="null"/> — an added file has no "before",
    ///  a deleted one has no "after" — and either side may fail to decode, which is what
    ///  an SVG or an exotic TIFF does here: that side is reported in the information bar
    ///  and the other is still shown. Only when <b>neither</b> side yields an image is
    ///  there nothing to open, and then this returns the reason instead of putting an
    ///  empty window on screen.</para>
    ///
    ///  <para>Decoding happens on the thread pool: a large PNG costs tens of
    ///  milliseconds, and the caller is a menu handler on the UI thread.</para>
    /// </summary>
    /// <param name="owner">The window the dialog is modal to.</param>
    /// <param name="left">The "before" bytes, or <see langword="null"/> when absent.</param>
    /// <param name="right">The "after" bytes, or <see langword="null"/> when absent.</param>
    /// <param name="leftTitle">Label for the left version, e.g. <c>HEAD:icon.png</c>.</param>
    /// <param name="rightTitle">Label for the right version, e.g. <c>Working tree</c>.</param>
    public static async Task<string?> ShowAsync(
        Window owner,
        byte[]? left,
        byte[]? right,
        string leftTitle,
        string rightTitle)
    {
        (ImageSide one, ImageSide two) = await Task.Run(
            () => (Decode(left, leftTitle), Decode(right, rightTitle)));

        if (one.Image is null && two.Image is null)
        {
            string reason = one.Error ?? two.Error ?? TranslationService.T("both versions are absent");
            return TranslationService.T("Neither version could be shown as an image") + ": " + reason + ".";
        }

        ImageDiffWindow window = new(one, two);
        await window.ShowDialog(owner);
        return null;
    }

    private Control BuildLayout()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };

        Grid bar = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs),
        };

        StackPanel modes = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            Children =
            {
                _sideBySideButton,
                _overlayButton,
                _differenceButton,
                new Border { Width = Metrics.Space.Sm },
                ToolButton(T("Fit"), T("Scale the image to the window"), Fit),
                ToolButton("1:1", T("One image pixel per screen pixel"), () => SetZoom(1, anchor: null)),
                _zoomLabel,
            },
        };
        _zoomLabel.VerticalAlignment = VerticalAlignment.Center;
        _zoomLabel.Foreground = Brush("App.TextDim", Brushes.Gray);
        _zoomLabel.Margin = new Thickness(Metrics.Space.Xs, 0, 0, 0);
        bar.Children.Add(modes);

        Grid.SetColumn(_opacityBox, 1);
        _opacityBox.HorizontalAlignment = HorizontalAlignment.Center;
        bar.Children.Add(_opacityBox);

        // A read-only window still needs a way out that does not depend on the window
        // manager drawing a title bar: IsCancel gives it Escape as well.
        Button close = new()
        {
            Content = T("Close"),
            Padding = Metrics.Density.ButtonPadding,
            IsCancel = true,
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        bar.Children.Add(close);

        Border toolbar = new()
        {
            Background = Brush("App.Toolbar", Brushes.DimGray),
            BorderBrush = Rule(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        _host.Background = Brush("App.Panel", Brushes.Black);
        Grid.SetRow(_host, 1);
        root.Children.Add(_host);

        _info.Foreground = Brush("App.TextDim", Brushes.Gray);
        _info.FontSize = Metrics.Text.Caption;
        _info.TextWrapping = TextWrapping.Wrap;
        _info.Margin = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs);

        Border status = new()
        {
            Background = Brush("App.Toolbar", Brushes.DimGray),
            BorderBrush = Rule(),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _info,
        };
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        return root;
    }

    private void SetMode(ViewMode mode)
    {
        _mode = mode;
        _sideBySideButton.IsChecked = mode == ViewMode.SideBySide;
        _overlayButton.IsChecked = mode == ViewMode.Overlay;
        _differenceButton.IsChecked = mode == ViewMode.Difference;
        _opacityBox.IsVisible = mode == ViewMode.Overlay;

        if (mode == ViewMode.Difference)
        {
            RequestDifference();
        }

        Rebuild();
    }

    /// <summary>
    ///  Rebuilds the pane area for the current mode. Cheap enough to do on every mode
    ///  change (three controls and a bitmap reference), and it keeps each mode's layout
    ///  written once instead of as a set of visibility flags over one shared tree.
    /// </summary>
    private void Rebuild()
    {
        _panes.Clear();

        Control content = _mode switch
        {
            ViewMode.Overlay => BuildOverlay(),
            ViewMode.Difference => BuildDifference(),
            _ => BuildSideBySide(),
        };

        _host.Child = content;
        UpdateInfo();

        // Sizes depend on the zoom, and "fit" depends on a viewport that does not exist
        // until this new tree has been laid out once.
        if (_fit)
        {
            Dispatcher.UIThread.Post(ApplyFit, DispatcherPriority.Loaded);
        }
        else
        {
            ApplyZoom();
        }
    }

    private Control BuildSideBySide()
    {
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,1,*"),
        };

        Control leftPane = TitledPane(_left);
        Control rightPane = TitledPane(_right);
        Grid.SetColumn(rightPane, 2);

        Border separator = new() { Background = Rule() };
        Grid.SetColumn(separator, 1);

        grid.Children.Add(leftPane);
        grid.Children.Add(separator);
        grid.Children.Add(rightPane);
        return grid;
    }

    private Control TitledPane(ImageSide side)
    {
        Grid grid = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        TextBlock title = new()
        {
            Text = side.Title,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            FontWeight = Metrics.Text.ActiveWeight,
            FontSize = Metrics.Text.Body,
            Margin = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        grid.Children.Add(title);

        Control body = side.Image is { } bitmap
            ? Viewport(Layer(bitmap, opacity: 1), bitmap.PixelSize)
            : Message(side.Absent ? T("This version does not exist.") : Undecodable(side));
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private Control BuildOverlay()
    {
        if (_left.Image is null && _right.Image is null)
        {
            return Message(T("There is no image to overlay."));
        }

        PixelSize size = Union();
        Grid stack = new();

        if (_left.Image is { } under)
        {
            stack.Children.Add(Layer(under, opacity: 1));
        }

        if (_right.Image is { } over)
        {
            Image top = Layer(over, opacity: _opacity.Value);
            top.Name = OverlayLayerName;
            stack.Children.Add(top);
        }

        return Viewport(stack, size);
    }

    private Control BuildDifference()
    {
        if (_diff is null)
        {
            return Message(T("Comparing pixel by pixel…"));
        }

        if (_diff.Bitmap is null)
        {
            return Message(_diff.Message ?? T("The difference could not be computed."));
        }

        Image image = Layer(_diff.Bitmap, opacity: 1);
        return Viewport(image, _diff.Bitmap.PixelSize);
    }

    /// <summary>
    ///  A scrolling viewport over <paramref name="content"/>, with the checkerboard
    ///  behind it and the wheel bound to zoom.
    ///
    ///  <para>The checkerboard fills the <em>viewport</em> and not the scrolled content
    ///  on purpose: as content it would have to be drawn at the zoomed size, and at 32×
    ///  over a 4000px image that is a quarter of a million squares per frame.</para>
    /// </summary>
    private Control Viewport(Control content, PixelSize size)
    {
        ScrollViewer viewer = new()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Content = new Panel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Children = { content },
            },
        };

        // Tunnelling: the ScrollViewer consumes the wheel on the bubbling route, so a
        // handler added there would only ever see the events it did not want to scroll.
        viewer.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        viewer.ScrollChanged += OnScrollChanged;

        _panes.Add(new Pane(viewer, content, size));

        return new Panel
        {
            Children =
            {
                new CheckerBoard(),
                viewer,
            },
        };
    }

    private static Image Layer(Bitmap bitmap, double opacity)
    {
        Image image = new()
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = opacity,
        };

        // The point of zooming past 1:1 is to see which pixel changed; a smooth
        // resampler averages precisely that information away.
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        return image;
    }

    private Control Message(string text) => new TextBlock
    {
        Text = text,
        Foreground = Brush("App.TextDim", Brushes.Gray),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 460,
        TextAlignment = TextAlignment.Center,
        Margin = Metrics.Space.All(Metrics.Space.Lg),
    };

    private PixelSize Union()
    {
        PixelSize left = _left.Image?.PixelSize ?? new PixelSize(0, 0);
        PixelSize right = _right.Image?.PixelSize ?? new PixelSize(0, 0);
        return new PixelSize(Math.Max(left.Width, right.Width), Math.Max(left.Height, right.Height));
    }

    private void ApplyZoom()
    {
        foreach (Pane pane in _panes)
        {
            pane.Content.Width = Math.Max(1, pane.Size.Width * _zoom);
            pane.Content.Height = Math.Max(1, pane.Size.Height * _zoom);
        }

        _zoomLabel.Text = (_zoom * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%"
            + (_fit ? " (" + T("fit") + ")" : string.Empty);
    }

    private void Fit()
    {
        _fit = true;
        ApplyFit();
    }

    private void ApplyFit()
    {
        double zoom = MaxZoom;
        bool measured = false;

        foreach (Pane pane in _panes)
        {
            Size viewport = pane.Viewer.Bounds.Size;
            if (viewport.Width <= 1 || viewport.Height <= 1 || pane.Size.Width == 0 || pane.Size.Height == 0)
            {
                continue;
            }

            // A couple of pixels of slack, so "fit" does not itself produce scrollbars
            // whose width then makes the content no longer fit.
            zoom = Math.Min(zoom, (viewport.Width - 2) / pane.Size.Width);
            zoom = Math.Min(zoom, (viewport.Height - 2) / pane.Size.Height);
            measured = true;
        }

        if (!measured)
        {
            return;
        }

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyZoom();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        double factor = Math.Pow(1.2, e.Delta.Y);
        Point pointer = e.GetPosition(viewer);
        SetZoom(_zoom * factor, (viewer, pointer));

        // Otherwise the ScrollViewer would also scroll: the gesture would zoom AND pan,
        // and the pixel one was aiming at would be gone.
        e.Handled = true;
    }

    /// <summary>
    ///  Applies a zoom, keeping the image point under <paramref name="anchor"/> where it
    ///  is. Without the anchor, zooming in on a detail walks it out of the viewport and
    ///  the user chases it with the scrollbars.
    /// </summary>
    private void SetZoom(double zoom, (ScrollViewer Viewer, Point Position)? anchor)
    {
        double clamped = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(clamped - _zoom) < 0.0001)
        {
            return;
        }

        Vector before = anchor is { } a ? a.Viewer.Offset : default;
        double previous = _zoom;
        _fit = false;
        _zoom = clamped;
        ApplyZoom();

        if (anchor is not { } target)
        {
            return;
        }

        // The content has not been re-measured yet, so the new offset can only be applied
        // once layout has caught up with the new size.
        Dispatcher.UIThread.Post(
            () =>
            {
                Vector content = (before + (Vector)target.Position) / previous;
                Vector offset = (content * _zoom) - (Vector)target.Position;
                ScrollTo(new Vector(Math.Max(0, offset.X), Math.Max(0, offset.Y)));
            },
            DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncing || sender is not ScrollViewer viewer)
        {
            return;
        }

        ScrollTo(viewer.Offset, viewer);
    }

    /// <summary>
    ///  Moves every pane to the same offset. Sharing the offset (rather than a ratio) is
    ///  what makes side-by-side a comparison: the two panes show the same rectangle of
    ///  the same image space, so a feature sits at the same place on screen in both.
    /// </summary>
    private void ScrollTo(Vector offset, ScrollViewer? except = null)
    {
        _syncing = true;
        try
        {
            foreach (Pane pane in _panes)
            {
                if (!ReferenceEquals(pane.Viewer, except))
                {
                    pane.Viewer.Offset = offset;
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnOpacityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty || _mode != ViewMode.Overlay)
        {
            return;
        }

        foreach (Pane pane in _panes)
        {
            if (pane.Content is Panel stack)
            {
                foreach (Control child in stack.Children)
                {
                    if (child.Name == OverlayLayerName)
                    {
                        child.Opacity = _opacity.Value;
                    }
                }
            }
        }
    }

    private void RequestDifference()
    {
        if (_diffRequested)
        {
            return;
        }

        _diffRequested = true;
        Async.OffUi(
            () => Difference(_left, _right),
            result =>
            {
                _diff = result;
                if (_mode == ViewMode.Difference)
                {
                    Rebuild();
                }
            },
            "image difference");
    }

    /// <summary>
    ///  The comparison as the window needs it: whatever happens inside, a
    ///  <see cref="DiffResult"/> comes out.
    ///
    ///  <para>The caller is <see cref="Async.OffUi"/>, which reports a fault to the
    ///  console and then never calls back — so an exception in the pass below would not
    ///  merely lose the difference, it would leave the pane saying "Comparing pixel by
    ///  pixel…" until the window is closed. A decoder this port has never met must
    ///  produce a sentence, not a spinner.</para>
    /// </summary>
    private static DiffResult Difference(ImageSide left, ImageSide right)
    {
        try
        {
            return Subtract(left, right);
        }
        catch (Exception ex)
        {
            return new DiffResult(
                null,
                0,
                0,
                false,
                TranslationService.T("The difference could not be computed") + ": " + ex.Message + ".");
        }
    }

    /// <summary>
    ///  The per-pixel comparison, run on the thread pool: a 16 megapixel pass is a few
    ///  hundred milliseconds and three large buffers, neither of which belongs on the UI
    ///  thread.
    ///
    ///  <para>Identical pixels are painted near-black rather than left transparent: over
    ///  the checkerboard, "unchanged" would otherwise be the busiest part of the picture.
    ///  Differences are amplified (×3 with a floor) because a one-level change in a
    ///  single channel is invisible when drawn at its true magnitude, and finding those
    ///  is the entire purpose of this mode.</para>
    /// </summary>
    private static DiffResult Subtract(ImageSide left, ImageSide right)
    {
        Bitmap? one = left.Image;
        Bitmap? two = right.Image;
        if (one is null || two is null)
        {
            return new DiffResult(
                null,
                0,
                0,
                false,
                TranslationService.T("Only one of the two versions is an image, so there is nothing to subtract."));
        }

        PixelSize a = one.PixelSize;
        PixelSize b = two.PixelSize;
        int width = Math.Max(a.Width, b.Width);
        int height = Math.Max(a.Height, b.Height);
        long total = (long)width * height;

        if (total > MaxDiffPixels)
        {
            return new DiffResult(
                null,
                0,
                total,
                a != b,
                string.Format(
                    CultureInfo.CurrentCulture,
                    TranslationService.T(
                        "The images are too large to compare pixel by pixel ({0:0.#} megapixels; the limit is {1:0.#})."),
                    total / 1_000_000d,
                    MaxDiffPixels / 1_000_000d));
        }

        byte[] first = Pixels(one);
        byte[] second = Pixels(two);

        WriteableBitmap target = new(new PixelSize(width, height), one.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        long differing = 0;

        using (ILockedFramebuffer fb = target.Lock())
        {
            int stride = fb.RowBytes;
            byte[] output = new byte[stride * height];

            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int at = row + (x * 4);
                    bool inFirst = x < a.Width && y < a.Height;
                    bool inSecond = x < b.Width && y < b.Height;

                    if (!inFirst || !inSecond)
                    {
                        // Present in one frame only: its own colour, so a crop or a resize
                        // does not read as "every pixel here changed value".
                        output[at] = OnlyOneSide.B;
                        output[at + 1] = OnlyOneSide.G;
                        output[at + 2] = OnlyOneSide.R;
                        output[at + 3] = 0xFF;
                        differing++;
                        continue;
                    }

                    int p = ((y * a.Width) + x) * 4;
                    int q = ((y * b.Width) + x) * 4;
                    int db = Math.Abs(first[p] - second[q]);
                    int dg = Math.Abs(first[p + 1] - second[q + 1]);
                    int dr = Math.Abs(first[p + 2] - second[q + 2]);
                    int da = Math.Abs(first[p + 3] - second[q + 3]);
                    int worst = Math.Max(Math.Max(db, dg), Math.Max(dr, da));

                    if (worst == 0)
                    {
                        output[at] = Same;
                        output[at + 1] = Same;
                        output[at + 2] = Same;
                        output[at + 3] = 0xFF;
                        continue;
                    }

                    differing++;
                    output[at] = Amplify(Math.Max(db, da));
                    output[at + 1] = Amplify(Math.Max(dg, da));
                    output[at + 2] = Amplify(Math.Max(dr, da));
                    output[at + 3] = 0xFF;
                }
            }

            Marshal.Copy(output, 0, fb.Address, output.Length);
        }

        return new DiffResult(target, differing, total, a != b, null);
    }

    private static byte Amplify(int delta)
        => delta == 0 ? (byte)64 : (byte)Math.Min(255, 64 + (delta * 3));

    /// <summary>
    ///  The bitmap's pixels as tightly packed Bgra8888, whatever the file's own format
    ///  was: the decoder may hand back a palette or a 24-bit surface, and the comparison
    ///  needs one predictable layout.
    ///
    ///  <para><b>Not every decoded bitmap can be read directly.</b> Skia keeps some
    ///  surfaces in a colour type Avalonia has no <see cref="PixelFormat"/> for — a
    ///  16-bit-per-channel greyscale PNG comes back as Skia's <c>Gray8</c>, whose
    ///  <see cref="Bitmap.Format"/> is <see langword="null"/> — and
    ///  <see cref="Bitmap.CopyPixels(ILockedFramebuffer, AlphaFormat)"/> throws
    ///  <see cref="NotSupportedException"/> for exactly those. Measured, not assumed:
    ///  before this fallback existed, the difference mode over a 16-bit greyscale PNG
    ///  left the pane on "Comparing pixel by pixel…" for ever, with the exception
    ///  visible only in the console. Drawing the bitmap once through the renderer
    ///  converts it — the render target is Bgra8888 by construction — which is slower
    ///  than a copy but only happens for the surfaces that cannot be copied.</para>
    /// </summary>
    private static byte[] Pixels(Bitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        WriteableBitmap scratch = new(size, bitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer fb = scratch.Lock())
        {
            try
            {
                bitmap.CopyPixels(fb, AlphaFormat.Unpremul);
            }
            catch (NotSupportedException)
            {
                using RenderTargetBitmap converted = new(size, bitmap.Dpi);
                using (DrawingContext context = converted.CreateDrawingContext())
                {
                    context.DrawImage(bitmap, new Rect(0, 0, size.Width, size.Height));
                }

                converted.CopyPixels(fb, AlphaFormat.Unpremul);
            }

            byte[] buffer = new byte[size.Width * size.Height * 4];
            int stride = size.Width * 4;
            for (int y = 0; y < size.Height; y++)
            {
                Marshal.Copy(fb.Address + (y * fb.RowBytes), buffer, y * stride, stride);
            }

            return buffer;
        }
    }

    private void UpdateInfo()
    {
        List<string> parts =
        [
            Describe(_left),
            Describe(_right),
        ];

        if (_left.Image is { } a && _right.Image is { } b)
        {
            parts.Add(a.PixelSize == b.PixelSize
                ? T("same dimensions")
                : T("DIMENSIONS DIFFER") + " ("
                    + Dimensions(a.PixelSize) + " → " + Dimensions(b.PixelSize) + ")");

            if (_left.ByteCount != _right.ByteCount)
            {
                long delta = _right.ByteCount - _left.ByteCount;
                parts.Add(T("file size differs by") + " "
                    + (delta > 0 ? "+" : "-") + Bytes(Math.Abs(delta)));
            }
            else
            {
                parts.Add(T("same file size"));
            }
        }

        if (_mode == ViewMode.Difference && _diff is { } diff)
        {
            if (diff.Bitmap is null)
            {
                parts.Add(diff.Message ?? T("no difference computed"));
            }
            else
            {
                double percent = diff.Total == 0 ? 0 : diff.Differing * 100d / diff.Total;
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    T("{0:N0} of {1:N0} pixels differ ({2:0.###}%)"),
                    diff.Differing,
                    diff.Total,
                    percent));

                if (diff.SizeMismatch)
                {
                    parts.Add(T("aligned top-left over the union of both frames; the area present in only one is marked in orange"));
                }
            }
        }

        _info.Text = string.Join("   •   ", parts);
    }

    private string Describe(ImageSide side)
    {
        if (side.Absent)
        {
            return side.Title + ": " + T("absent");
        }

        if (side.Image is not { } bitmap)
        {
            return side.Title + ": " + T("not decodable")
                + (side.Format is null ? string.Empty : " (" + side.Format + ")")
                + ", " + Bytes(side.ByteCount);
        }

        return side.Title + ": " + Dimensions(bitmap.PixelSize) + ", " + Bytes(side.ByteCount)
            + (side.Contents is null ? string.Empty : ", " + side.Contents);
    }

    /// <summary>
    ///  Why a side that is there could not be shown. Names the format when the bytes
    ///  announce one, because "this ICO could not be decoded" points at the file while a
    ///  bare decoder message points at nothing; and says so plainly when they announce
    ///  none, since that is the honest answer for the SVG or the TIFF this window is
    ///  occasionally handed.
    /// </summary>
    private string Undecodable(ImageSide side)
    {
        string reason = side.Error ?? T("the decoder gave no reason");
        return side.Format is { } format
            ? string.Format(CultureInfo.CurrentCulture, T("This {0} could not be decoded"), format) + ": " + reason + "."
            : T("These bytes are not an image this window can decode") + ": " + reason + ".";
    }

    private static string Dimensions(PixelSize size)
        => size.Width.ToString(CultureInfo.CurrentCulture) + "×" + size.Height.ToString(CultureInfo.CurrentCulture);

    private static string Bytes(long count)
        => count < 1024
            ? count.ToString(CultureInfo.CurrentCulture) + " B"
            : count < 1024 * 1024
                ? (count / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " kB"
                : (count / (1024d * 1024)).ToString("0.##", CultureInfo.CurrentCulture) + " MB";

    private static ImageSide Decode(byte[]? bytes, string title)
    {
        if (bytes is null)
        {
            return new ImageSide(title, 0, null, true, null, null, null);
        }

        // The same sniffer the diff view used to decide whether to offer this window at
        // all (ImageFormats is the only one in the port). Here it is not a decision but a
        // NAME: it turns "Unable to load bitmap from provided data" into "this JPEG could
        // not be decoded", which is the difference between a user suspecting the viewer
        // and a user suspecting the file.
        string? format = ImageFormats.Detect(bytes);

        try
        {
            using MemoryStream stream = new(bytes);
            Bitmap bitmap = new(stream);
            return new ImageSide(title, bytes.Length, bitmap, false, null, format, Contents(bytes, format));
        }
        catch (Exception ex)
        {
            // Anything the platform decoder refuses lands here — SVG, an exotic TIFF, a
            // truncated file. It is a normal outcome of "diff this binary file", not a
            // failure of the window, so it is carried as text instead of thrown.
            return new ImageSide(title, bytes.Length, null, false, ex.Message, format, null);
        }
    }

    /// <summary>
    ///  What the container holds beyond the one picture that got decoded, or
    ///  <see langword="null"/> when it holds nothing more.
    ///
    ///  <para><b>Why this exists.</b> Three of the formats this window is offered for are
    ///  containers, and the renderer silently takes one image out of them: an .ico with
    ///  six sizes is shown at whichever size Skia picked (measured: the largest, whatever
    ///  the directory order — 256×256 for an icon whose first entry is 16×16), and an
    ///  animated GIF or WEBP is shown as its first frame with nothing to distinguish it
    ///  from a still. The window cannot decode the other entries, and an image diff has
    ///  no business growing a frame player; what it can do is stop presenting a part as
    ///  the whole. Hence one clause in the information bar, from the bytes themselves —
    ///  the decoder is not asked, because it is precisely the decoder's silence that is
    ///  the problem.</para>
    /// </summary>
    private static string? Contents(byte[] bytes, string? format)
    {
        List<string> parts = [];

        int count = format switch
        {
            "ICO" => IcoEntries(bytes),
            "GIF" => GifFrames(bytes),
            "WEBP" => WebpFrames(bytes),
            "PNG" => ApngFrames(bytes),
            _ => 1,
        };

        if (count > 1)
        {
            parts.Add(string.Format(
                CultureInfo.CurrentCulture,
                format == "ICO"
                    ? TranslationService.T("one of {0} sizes in the file")
                    : TranslationService.T("frame 1 of {0}"),
                count));
        }

        // A 16-bit PNG is decoded to 8 bits per channel and the low byte is DISCARDED —
        // measured: two such files differing in the low byte of every single pixel come
        // out of the decoder byte-identical, and the difference mode then reports "0
        // pixels differ" about two files git has just told the user are different. The
        // window cannot show what the renderer threw away; it can refuse to let that
        // zero pass as the whole truth.
        if (format == "PNG" && bytes.Length > 24 && bytes[24] > 8)
        {
            parts.Add(TranslationService.T("16 bits per channel, compared at 8"));
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Images in an ICO directory, from the count the header declares.</summary>
    private static int IcoEntries(byte[] bytes)
        => bytes.Length < 6 ? 1 : bytes[4] | (bytes[5] << 8);

    /// <summary>
    ///  Frames in a GIF, by walking the block structure rather than by counting 0x2C
    ///  bytes: an image descriptor's introducer is an ordinary byte value that occurs all
    ///  over compressed data, and a frame count that is wrong is worse than none.
    /// </summary>
    private static int GifFrames(byte[] bytes)
    {
        int at = 13;
        if (bytes.Length <= at)
        {
            return 1;
        }

        // A global colour table, when present, sits between the screen descriptor and the
        // first block; its size is encoded in the low three bits of the flags byte.
        if ((bytes[10] & 0x80) != 0)
        {
            at += 3 * (1 << ((bytes[10] & 0x07) + 1));
        }

        int frames = 0;
        while (at < bytes.Length)
        {
            byte block = bytes[at++];
            if (block == 0x3B)
            {
                break;
            }

            if (block == 0x21)
            {
                // Extension: a label, then sub-blocks. Nothing here needs to know which
                // extension it is; the loop only has to step over it.
                at++;
                at = SkipSubBlocks(bytes, at);
                continue;
            }

            if (block != 0x2C)
            {
                break;
            }

            frames++;
            if (at + 9 > bytes.Length)
            {
                break;
            }

            byte flags = bytes[at + 8];
            at += 9;
            if ((flags & 0x80) != 0)
            {
                at += 3 * (1 << ((flags & 0x07) + 1));
            }

            at++;                       // LZW minimum code size
            at = SkipSubBlocks(bytes, at);
        }

        return Math.Max(1, frames);
    }

    /// <summary>Steps over a run of GIF data sub-blocks, ending on the zero terminator.</summary>
    private static int SkipSubBlocks(byte[] bytes, int at)
    {
        while (at < bytes.Length && bytes[at] != 0)
        {
            at += bytes[at] + 1;
        }

        return at + 1;
    }

    /// <summary>
    ///  Frames in a WEBP, from the ANMF chunks of the RIFF container. A still WEBP has a
    ///  single VP8/VP8L chunk and no ANMF at all.
    /// </summary>
    private static int WebpFrames(byte[] bytes)
    {
        int frames = 0;
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            uint size = BitConverter.ToUInt32(bytes, at + 4);
            if (bytes[at] == (byte)'A' && bytes[at + 1] == (byte)'N' && bytes[at + 2] == (byte)'M' && bytes[at + 3] == (byte)'F')
            {
                frames++;
            }

            // Chunk payloads are padded to an even length, and the pad byte is not counted
            // in the size field: forgetting it desynchronises the walk after one odd chunk.
            at += 8 + (int)size + ((size & 1) == 0 ? 0 : 1);
        }

        return Math.Max(1, frames);
    }

    /// <summary>
    ///  Frames of an animated PNG, from the <c>acTL</c> chunk. A plain PNG has none, and
    ///  the renderer shows an APNG as its still image — the same half-truth as a GIF.
    /// </summary>
    private static int ApngFrames(byte[] bytes)
    {
        int at = 8;
        while (at + 12 <= bytes.Length)
        {
            int length = (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
            if (length < 0)
            {
                break;
            }

            if (bytes[at + 4] == (byte)'a' && bytes[at + 5] == (byte)'c' && bytes[at + 6] == (byte)'T' && bytes[at + 7] == (byte)'L'
                && at + 12 <= bytes.Length)
            {
                return (bytes[at + 8] << 24) | (bytes[at + 9] << 16) | (bytes[at + 10] << 8) | bytes[at + 11];
            }

            // acTL is required to precede IDAT, so there is no reason to walk the pixel
            // data of every ordinary PNG looking for a chunk that cannot be there.
            if (bytes[at + 4] == (byte)'I' && bytes[at + 5] == (byte)'D' && bytes[at + 6] == (byte)'A' && bytes[at + 7] == (byte)'T')
            {
                break;
            }

            at += 12 + length;
        }

        return 1;
    }

    private ToggleButton ModeButton(string caption, ViewMode mode)
    {
        ToggleButton button = new()
        {
            Content = caption,
            Padding = Metrics.Density.ButtonPadding,
        };

        // Click, not IsCheckedChanged: SetMode writes IsChecked on all three, and
        // reacting to the property would re-enter it twice for every switch.
        button.Click += (_, _) => SetMode(mode);
        return button;
    }

    private Button ToolButton(string caption, string tip, Action action)
    {
        Button button = new()
        {
            Content = caption,
            Padding = Metrics.Density.ButtonPadding,
            [ToolTip.TipProperty] = tip,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = Brush("App.TextDim", Brushes.Gray),
        FontSize = Metrics.Text.Caption,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 220,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static IBrush Rule()
        => Icons.Tint("App.Rule") ?? Brush("App.Border", Brushes.Gray);

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);

    private const string OverlayLayerName = "OverlayTopLayer";

    /// <summary>Identical pixels: dark, but not the pure black of an empty pane.</summary>
    private const byte Same = 0x14;

    /// <summary>The colour of the area that exists in only one of the two frames.</summary>
    private static readonly Color OnlyOneSide = Color.FromRgb(0xFF, 0x9A, 0x1F);

    private enum ViewMode
    {
        SideBySide,
        Overlay,
        Difference,
    }

    private sealed record Pane(ScrollViewer Viewer, Control Content, PixelSize Size);

    private sealed record DiffResult(
        WriteableBitmap? Bitmap,
        long Differing,
        long Total,
        bool SizeMismatch,
        string? Message);

    private sealed record ImageSide(
        string Title,
        int ByteCount,
        Bitmap? Image,
        bool Absent,
        string? Error,
        string? Format,
        string? Contents);
}

/// <summary>
///  The grey chequerboard every image tool puts behind a picture, for the one reason
///  that matters here: without it a fully transparent PNG and a fully white one are
///  the same rectangle, and "the background became opaque" is exactly the kind of
///  change this window exists to reveal.
/// </summary>
internal sealed class CheckerBoard : Control
{
    private const double Cell = 8;

    public CheckerBoard()
    {
        // Custom drawing in this port must be clipped: without it the last partial
        // square of a row is painted outside the control's bounds.
        ClipToBounds = true;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        Color b = (Application.Current?.Resources["App.Panel"] as ISolidColorBrush)?.Color
            ?? Color.FromRgb(0x2A, 0x2A, 0x2A);

        // The second tone is derived from the theme's panel colour rather than fixed, so
        // the board stays a faint texture in both a light and a dark theme instead of
        // becoming the loudest thing on screen in one of them.
        Color alt = b.R + b.G + b.B > 3 * 128
            ? Color.FromRgb(Darker(b.R), Darker(b.G), Darker(b.B))
            : Color.FromRgb(Lighter(b.R), Lighter(b.G), Lighter(b.B));

        context.FillRectangle(new SolidColorBrush(b), new Rect(Bounds.Size));

        SolidColorBrush second = new(alt);
        int columns = (int)Math.Ceiling(Bounds.Width / Cell);
        int rows = (int)Math.Ceiling(Bounds.Height / Cell);
        for (int y = 0; y < rows; y++)
        {
            for (int x = (y % 2 == 0) ? 0 : 1; x < columns; x += 2)
            {
                context.FillRectangle(second, new Rect(x * Cell, y * Cell, Cell, Cell));
            }
        }
    }

    private static byte Darker(byte value) => (byte)Math.Max(0, value - 18);

    private static byte Lighter(byte value) => (byte)Math.Min(255, value + 18);
}
