using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Which diagrams a help panel shows and under which key it remembers whether it is
///  expanded — the port's equivalent of setting <c>Image1</c>, <c>Image2</c> and
///  <c>UniqueIsExpandedSettingsId</c> on upstream's
///  <c>HelpImageDisplayUserControl</c> from a designer file.
/// </summary>
/// <param name="Id">
///  Stable key for the expanded/collapsed state, upstream's
///  <c>UniqueIsExpandedSettingsId</c> (it writes <c>"HelpIsExpanded" + id</c>).
/// </param>
/// <param name="Image1">
///  Base file name of the always-visible diagram under
///  <c>avares://GitExtensions.Avalonia/Assets/Help/</c>, e.g. "HelpCommandMerge".
///  Case-sensitive.
/// </param>
/// <param name="Image2">
///  Optional second diagram, shown only while the pointer is over the panel and only
///  when <see cref="HelpImagePanel.ShowImage2OnHover"/> is on.
/// </param>
public sealed record HelpImageSpec(string Id, string Image1, string? Image2 = null);

/// <summary>
///  The decoded, theme-corrected diagrams plus the persisted expanded flag — produced
///  OFF the UI thread by <see cref="HelpImagePanel.Prepare"/> and handed to the
///  panel's constructor, the same split <c>MergeDialog.ShowAsync</c> uses for its git
///  data. Decoding two 289×373 PNGs and rewriting every pixel's lightness is real
///  work and has no business running on the UI thread.
/// </summary>
public sealed record HelpImageAssets(Bitmap? Image1, Bitmap? Image2, bool IsExpanded);

/// <summary>
///  The illustrative help column of the merge/pull/rebase dialogs — the port of
///  upstream's <c>HelpImageDisplayUserControl</c> (screenshot
///  <c>00_merge window.png</c>): a <c>Hide help</c> link, an optional one-line hover
///  notice and the diagram itself; collapsed, all of that is replaced by a
///  <c>Show help</c> button and the column shrinks to that button's width.
///
///  <para><b>Why a button for "Show help" and a link for "Hide help".</b> Not a
///  stylistic slip — upstream does the same and says why in a source comment: a button
///  has a constant width across languages, which matters because the collapsed column
///  is sized to it, while the expanded column is sized to the image and can afford a
///  link.</para>
///
///  <para><b>Dark theme.</b> The two PNGs are line diagrams on an opaque WHITE
///  background (81% of <c>HelpCommandMerge.png</c>'s pixels are pure white), so
///  dropping them unaltered into a <c>#1E1E1E</c> dialog paints a 289×373 lightbox —
///  measured at 16.67:1 against the window, the same defect round 11/12 removed from
///  the managed picker (M70) and the ref pills (M67). So the port reimplements the
///  part of upstream's <c>LightnessCorrection</c> it needs (see
///  <see cref="CorrectLightness"/>), which remaps every pixel's lightness into the
///  theme's own text→background band while keeping hue and saturation: white lands
///  exactly on the window colour (1.00:1 — no slab at all) and the black legend text
///  becomes <c>#DCDCDC</c> at 12.16:1. In a light theme the correction is SKIPPED, as
///  upstream skips it for its default theme, and for a measured reason: untransformed,
///  the white background sits at 1.11:1 against <c>#F3F3F3</c> (an invisible seam) and
///  the white-on-colour node labels keep their original 8.59–10.95:1, whereas
///  transforming them would drop those to 4.32–6.33:1. Numbers in
///  <c>src/crossplatform/NOTES.md</c>.</para>
///
///  <para>The correction is computed once, for the theme in force when the owning
///  dialog opens. Switching theme underneath an open modal is not handled — the port
///  has no theme-changed event to subscribe to, and none of the dialogs that host this
///  panel survive a trip through the settings dialog.</para>
/// </summary>
public sealed class HelpImagePanel : UserControl
{
    private const string Root = "avares://GitExtensions.Avalonia/Assets/Help/";

    private readonly HelpImageSpec _spec;
    private readonly Bitmap? _image1;
    private readonly Bitmap? _image2;

    private readonly Image _picture;
    private readonly Button _hideLink;
    private readonly Button _showButton;
    private readonly TextBlock _hoverNotice;
    private readonly StackPanel _expandedBody;

    private bool _isExpanded;
    private bool _showImage2OnHover;
    private bool _isHovering;

    public HelpImagePanel(HelpImageSpec spec, HelpImageAssets assets)
    {
        _spec = spec;
        _image1 = assets.Image1;
        _image2 = assets.Image2;
        _isExpanded = assets.IsExpanded;

        // The panel is the hover target for the Image1/Image2 swap, and a control with
        // no background is not hit-testable in Avalonia: without this the pointer
        // "enters" only the pixels of the Image child, and never the padding around it.
        Background = Brush("App.Window", Brushes.DimGray);

        _picture = new Image
        {
            Source = _image1,

            // 1:1, like upstream's PictureBox in AutoSize mode: these are pixel
            // diagrams with baked-in 6 pt legend text, and any scaling smears it.
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _hideLink = LinkButton();
        _hideLink.Click += (_, _) => IsExpanded = false;

        _showButton = new Button
        {
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush("App.Control", Brushes.DimGray),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4),

            // A TextBlock, not a string: a string Content eats '_' as an access key,
            // and a translation of "Show help" may well contain one.
            Content = new TextBlock { Text = string.Empty, TextWrapping = TextWrapping.Wrap },
        };
        _showButton.Click += (_, _) => IsExpanded = true;

        _hoverNotice = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = TextWrapping.Wrap,

            // App.Text, not App.TextDim: this line is the only hint that a second
            // diagram exists, and upstream draws it in the ordinary label colour.
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Margin = new Thickness(0, 2, 0, 6),
        };

        _expandedBody = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _hideLink, _hoverNotice, _picture },
        };

        Content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { _expandedBody, _showButton },
        };

        PointerEntered += (_, _) => SetHovering(true);
        PointerExited += (_, _) => SetHovering(false);

        ApplyExpandedState();
    }

    /// <summary>
    ///  Raised after the user expanded or collapsed the panel, so the owning dialog can
    ///  grow or shrink by <see cref="CollapsedWidth"/>↔<see cref="ExpandedWidth"/> the
    ///  way upstream's <c>UpdateControlSize</c> resizes its <c>Form</c>.
    /// </summary>
    public event Action? ExpandedChanged;

    /// <summary>Width the column occupies expanded (the diagram's own width plus padding).</summary>
    public double ExpandedWidth => (_image1?.PixelSize.Width ?? 289) + 16;

    /// <summary>Width the column occupies collapsed — just the "Show help" button.</summary>
    public double CollapsedWidth => 108;

    /// <summary>Current width of the column, for the owner's geometry arithmetic.</summary>
    public double CurrentWidth => _isExpanded ? ExpandedWidth : CollapsedWidth;

    /// <summary>
    ///  Whether the diagram is showing. Setting it persists the choice under
    ///  <see cref="HelpImageSpec.Id"/> and raises <see cref="ExpandedChanged"/>.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            ApplyExpandedState();
            Persist(value);
            ExpandedChanged?.Invoke();
        }
    }

    /// <summary>
    ///  Upstream's <c>IsOnHoverShowImage2</c>: when on, hovering the panel swaps in
    ///  <see cref="HelpImageSpec.Image2"/> and the hover notice line becomes visible.
    ///  <c>FormMergeBranch</c> turns this on exactly while the fast-forward radio is
    ///  selected (<c>FormMergeBranch.cs:155-163</c>) — the second diagram describes the
    ///  fast-forward outcome, which the other radio forbids.
    /// </summary>
    public bool ShowImage2OnHover
    {
        get => _showImage2OnHover;
        set
        {
            _showImage2OnHover = value;
            ApplyExpandedState();
            UpdateImage();
        }
    }

    /// <summary>Fluent gives <see cref="UserControl"/> a template; without this the panel lays out at zero height.</summary>
    protected override Type StyleKeyOverride => typeof(UserControl);

    /// <summary>
    ///  Reads the two palette colours the lightness correction needs. MUST be called on
    ///  the UI thread (it touches <see cref="Application.Current"/>'s resources); the
    ///  result is then safe to hand to <see cref="Prepare"/> on a worker.
    /// </summary>
    public static (Color Text, Color Window) ReadPalette() => (
        ColorOf("App.Text", Color.FromRgb(0xDC, 0xDC, 0xDC)),
        ColorOf("App.Window", Color.FromRgb(0x1E, 0x1E, 0x1E)));

    /// <summary>
    ///  Decodes the diagrams, applies the dark-theme lightness correction and reads the
    ///  persisted expanded flag. Call OFF the UI thread.
    /// </summary>
    public static HelpImageAssets Prepare(HelpImageSpec spec, (Color Text, Color Window) palette)
    {
        // A panel the user has never touched starts EXPANDED, upstream's designer
        // default — so an absent key must not collapse to `false`.
        bool expanded = true;
        try
        {
            if (new ViewPrefsService().Load().HelpPanels.TryGetValue(spec.Id, out bool stored))
            {
                expanded = stored;
            }
        }
        catch (Exception)
        {
            // Unreadable prefs → the default.
        }

        return new HelpImageAssets(
            LoadDiagram(spec.Image1, palette),
            spec.Image2 is null ? null : LoadDiagram(spec.Image2, palette),
            expanded);
    }

    // --- State ------------------------------------------------------------

    private void SetHovering(bool hovering)
    {
        if (!_showImage2OnHover || _isHovering == hovering)
        {
            return;
        }

        _isHovering = hovering;
        UpdateImage();
    }

    private void UpdateImage()
        => _picture.Source = _showImage2OnHover && _isHovering && _image2 is not null ? _image2 : _image1;

    private void ApplyExpandedState()
    {
        _expandedBody.IsVisible = _isExpanded;
        _showButton.IsVisible = !_isExpanded;

        // Upstream: labelHoverText.Visible = IsOnHoverShowImage2, and only while
        // expanded — the notice describes a hover over an image that is not there.
        _hoverNotice.IsVisible = _isExpanded && _showImage2OnHover && _image2 is not null;

        Width = CurrentWidth;

        if (!_isExpanded)
        {
            // Leaving the panel by collapsing it never fires PointerExited, so the
            // hover state would stick and the re-expanded panel would open on Image2.
            _isHovering = false;
            UpdateImage();
        }
    }

    // Off the UI thread: Update() reads, mutates and rewrites view-prefs.json.
    private void Persist(bool value)
    {
        string id = _spec.Id;
        _ = Task.Run(() =>
        {
            try
            {
                new ViewPrefsService().Update(p => p.HelpPanels[id] = value);
            }
            catch (Exception)
            {
                // Remembering the panel state is best-effort.
            }
        });
    }

    /// <summary>Sets the two literals upstream translates as a link and a button caption.</summary>
    public void ApplyTranslations(string hideText, string showText, string hoverNotice)
    {
        if (_hideLink.Content is TextBlock hide)
        {
            hide.Text = hideText;
        }

        if (_showButton.Content is TextBlock show)
        {
            show.Text = showText;
        }

        _hoverNotice.Text = hoverNotice;
    }

    // --- Image pipeline ---------------------------------------------------

    private static Bitmap? LoadDiagram(string name, (Color Text, Color Window) palette)
    {
        Uri uri = new(Root + name + ".png");
        try
        {
            if (!AssetLoader.Exists(uri))
            {
                // The avares: lookup is case-sensitive and a miss otherwise renders a
                // blank column in silence — the failure mode that hid the About logo
                // until M69.
                Console.WriteLine($"[HelpImagePanel] '{name}' did not resolve ({uri})");
                return null;
            }

            using Stream stream = AssetLoader.Open(uri);
            Bitmap source = new(stream);

            double textL = Lightness(palette.Text);
            double windowL = Lightness(palette.Window);

            // Upstream's ColorHelper.AdaptLightness returns the original for its default
            // (light) theme. Same rule, expressed from the palette instead of a theme
            // name: correct only when the window is DARKER than the text.
            return windowL < textL ? CorrectLightness(source, textL, windowL) : source;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HelpImagePanel] '{name}' failed to load ({uri}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///  Reimplementation, for the port, of the pixel loop upstream runs as
    ///  <c>GitExtUtils.GitUI.Theming.LightnessCorrection</c> (excluded from the
    ///  cross-platform build, being System.Drawing + WinForms system colours).
    ///
    ///  <para>Per pixel: RGB → HSL, lightness pushed through a per-hue gamma into
    ///  PERCEIVED lightness, linearly remapped from [0,1] onto
    ///  [<paramref name="textL"/>, <paramref name="windowL"/>], pushed back through the
    ///  inverse gamma and converted to RGB with hue intact. The gamma step is not
    ///  optional decoration: plain HSL calls pure blue "50% light", so a naive inversion
    ///  leaves the blue commit nodes dark while flipping their white labels to dark —
    ///  measured at 1.89:1, illegible. With the gamma the same pair comes out at
    ///  9.09:1.</para>
    /// </summary>
    private static Bitmap CorrectLightness(Bitmap source, double textL, double windowL)
    {
        PixelSize size = source.PixelSize;
        WriteableBitmap target = new(size, source.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using (ILockedFramebuffer fb = target.Lock())
        {
            source.CopyPixels(fb, AlphaFormat.Unpremul);

            int bytes = fb.RowBytes * size.Height;
            byte[] buffer = new byte[bytes];
            Marshal.Copy(fb.Address, buffer, 0, bytes);

            for (int i = 0; i + 3 < bytes; i += 4)
            {
                // Bgra8888. Alpha is carried through untouched: the merge diagram has a
                // few hundred near-opaque antialiasing pixels and no real transparency.
                Color corrected = Correct(
                    Color.FromRgb(buffer[i + 2], buffer[i + 1], buffer[i]), textL, windowL);
                buffer[i] = corrected.B;
                buffer[i + 1] = corrected.G;
                buffer[i + 2] = corrected.R;
            }

            Marshal.Copy(buffer, 0, fb.Address, bytes);
        }

        return target;
    }

    private static Color Correct(Color c, double textL, double windowL)
    {
        (double h, double s, double l) = ToHsl(c);
        double gamma = Gamma(c);

        double perceived = Clamp01(GammaTransform(l, gamma));

        // LightnessCorrection.TransformS: a nearly black pixel can carry a high
        // mathematical saturation whose hue nobody can see; brightening it without this
        // damping turns dark antialiasing fringes into coloured confetti.
        double sat = perceived > 0.1 ? s : s * perceived / 0.1;

        double remapped = Clamp01(textL + (perceived * (windowL - textL)));
        return FromHsl(h, sat, Clamp01(GammaTransform(remapped, 1d / gamma)));
    }

    // ColorHelper.Gamma: how much brighter a hue looks than its HSL lightness claims.
    // The weights are upstream's.
    private static double Gamma(Color c)
    {
        if (c.R == c.G && c.G == c.B)
        {
            return 1d;
        }

        double weighted = (c.R * 0.8) + (c.G * 1.75) + (c.B * 0.45);
        return weighted <= 0 ? 1d : (c.R + c.G + c.B) / weighted;
    }

    private static double GammaTransform(double l, double gamma)
        => l < gamma / (gamma + 1) ? l / gamma : 1 + (gamma * (l - 1));

    private static double Lightness(Color c) => ToHsl(c).L;

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255d;
        double g = c.G / 255d;
        double b = c.B / 255d;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;

        if (max <= min)
        {
            return (0d, 0d, l);
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (max == r)
        {
            h = ((g - b) / d) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2;
        }
        else
        {
            h = ((r - g) / d) + 4;
        }

        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            byte v = Component(l);
            return Color.FromRgb(v, v, v);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        double p = (2 * l) - q;
        return Color.FromRgb(
            Component(Hue2Rgb(p, q, h + (1 / 3d))),
            Component(Hue2Rgb(p, q, h)),
            Component(Hue2Rgb(p, q, h - (1 / 3d))));

        static double Hue2Rgb(double p, double q, double t)
        {
            if (t < 0)
            {
                t++;
            }

            if (t > 1)
            {
                t--;
            }

            if (t < 1 / 6d)
            {
                return p + ((q - p) * 6 * t);
            }

            if (t < 1 / 2d)
            {
                return q;
            }

            if (t < 2 / 3d)
            {
                return p + ((q - p) * ((2 / 3d) - t) * 6);
            }

            return p;
        }
    }

    private static byte Component(double v) => (byte)Math.Clamp(Math.Floor(v * 256), 0, 255);

    private static double Clamp01(double v) => double.IsNaN(v) ? 0d : Math.Clamp(v, 0d, 1d);

    // --- Chrome -----------------------------------------------------------

    // The same link shape ResolveConflictsDialog uses for its manual link: a chromeless
    // Button, so it is focusable and keyboard-activatable, wearing a TextBlock that
    // looks like a WinForms LinkLabel.
    private Button LinkButton() => new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Left,
        Cursor = new Cursor(StandardCursorType.Hand),
        Content = new TextBlock
        {
            Text = string.Empty,
            // App.Link, not App.Accent: this is link ink, and the accent is calibrated
            // as a fill — 3.70:1 on App.Window in classic dark, under the 4.5:1 text
            // floor. The fallback is the modern-dark App.Link so a missing key degrades
            // to a text-grade blue instead of back to the fill-grade one.
            Foreground = Brush("App.Link", new SolidColorBrush(Color.Parse("#5B9CFF"))),
            TextDecorations = TextDecorations.Underline,
        },
    };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;

    private static Color ColorOf(string key, Color fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true
            && value is ISolidColorBrush brush
                ? brush.Color
                : fallback;
}
