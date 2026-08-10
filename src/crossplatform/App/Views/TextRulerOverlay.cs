using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A column ruler, and a band over whatever runs past a length limit, painted BEHIND a
///  monospaced <see cref="TextBox"/>.
///
///  <para>Upstream colours the offending characters themselves, because its editor is a
///  <c>RichTextBox</c> and can. Avalonia's <see cref="TextBox"/> has one foreground for
///  the whole document, so the port draws the marks on a sibling control sitting under
///  the text instead. That turns out better than the original: the band survives a theme
///  swap, and it never fights the selection or the caret for the pixel.</para>
///
///  <para>Positions are MEASURED, not derived from a column times a character width. The
///  obvious implementation — one advance per column, since the editor asks for a
///  monospace face — is wrong on this platform: the family "monospace" is an fontconfig
///  alias, not a family name, so Skia does not resolve it and the box renders in the
///  proportional default. Measuring the prefix of each line keeps the mark on the right
///  characters whatever the box ends up rendering in, and costs one text measurement per
///  OVER-LIMIT line, of which a commit message has approximately none.</para>
///
///  <para>The overlay is only correct while the box is NOT wrapping — with wrapping on,
///  one logical line spans several rows and every row below the first would be off by a
///  whole line. It draws nothing in that case rather than drawing a lie
///  (see <see cref="Wrapping"/>).</para>
/// </summary>
public sealed class TextRulerOverlay : Control
{
    // The text origin inside the box: its Padding plus the one-pixel border Avalonia's
    // TextBox template puts around the presenter. Mirrored here rather than read back
    // from the template, which would need the box to be laid out first.
    private readonly Thickness _textOrigin;

    private readonly TextBox _box;

    private ScrollViewer? _scroll;

    /// <param name="box">
    ///  The editor to shadow. The overlay listens to its text and to the scroll offset of
    ///  the <see cref="ScrollViewer"/> in its template, so it stays glued to the content
    ///  while the user scrolls.
    /// </param>
    /// <param name="textOrigin">Where the first character sits inside the box.</param>
    public TextRulerOverlay(TextBox box, Thickness textOrigin)
    {
        _box = box;
        _textOrigin = textOrigin;
        IsHitTestVisible = false;
        ClipToBounds = true;

        _box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                InvalidateVisual();
            }
        };

        // The ScrollViewer only exists once the template has been applied, which happens
        // after the first layout pass — hence the hook here rather than in the ctor body.
        _box.TemplateApplied += (_, _) => AttachScroll();
        AttachScroll();
    }

    /// <summary>Column of the vertical ruler, 0 = none.</summary>
    public int RulerColumn { get; set; }

    /// <summary>Maximum length of line 1, 0 = unlimited.</summary>
    public int FirstLineLimit { get; set; }

    /// <summary>Maximum length of every other line, 0 = unlimited.</summary>
    public int OtherLineLimit { get; set; }

    /// <summary>Whether the over-limit band is painted at all.</summary>
    public bool MarkIllFormed { get; set; } = true;

    /// <summary>
    ///  Whether the shadowed box soft-wraps. While it does, the overlay stands down: see
    ///  the class remarks.
    /// </summary>
    public bool Wrapping { get; set; }

    public override void Render(DrawingContext context)
    {
        if (Wrapping)
        {
            return;
        }

        Typeface face = new(_box.FontFamily, _box.FontStyle, _box.FontWeight);
        double size = _box.FontSize;
        if (size <= 0)
        {
            return;
        }

        // The ruler still needs ONE reference width, since a column has no other meaning
        // for a proportional face: "0", the reference advance of every fixed-width family
        // shipped on Linux and a fair average elsewhere. Line height comes from the same
        // probe, and is exact — every line of the box uses one typeface.
        double advance = Measure("0", face, size, out double lineHeight);
        if (advance <= 0 || lineHeight <= 0)
        {
            return;
        }

        double scrollX = _scroll?.Offset.X ?? 0;
        double scrollY = _scroll?.Offset.Y ?? 0;
        double left = _textOrigin.Left - scrollX;

        if (MarkIllFormed && (FirstLineLimit > 0 || OtherLineLimit > 0))
        {
            // The palette has no "band" colour of its own: take the removed-line red and
            // drop its alpha, so the mark tracks the theme without a fifth token to keep
            // in sync across the four palettes.
            IBrush band = Translucent("App.DiffRemoved", Color.FromRgb(200, 70, 70), 0x40);
            string[] lines = (_box.Text ?? string.Empty).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int limit = i == 0 ? FirstLineLimit : OtherLineLimit;
                string line = lines[i].TrimEnd('\r');
                if (limit <= 0 || line.Length <= limit)
                {
                    continue;
                }

                double y = _textOrigin.Top + (i * lineHeight) - scrollY;
                if (y + lineHeight < 0 || y > Bounds.Height)
                {
                    continue;
                }

                double kept = Measure(line[..limit], face, size, out _);
                double whole = Measure(line, face, size, out _);
                context.FillRectangle(band, new Rect(left + kept, y, whole - kept, lineHeight));
            }
        }

        if (RulerColumn > 0)
        {
            double x = left + (RulerColumn * advance);
            if (x >= 0 && x <= Bounds.Width)
            {
                // Half a pixel so the one-pixel line lands ON a device pixel instead of
                // straddling two and rendering as a grey smear.
                context.DrawLine(
                    new Pen(ThemeBrush("App.Border", Color.FromArgb(90, 128, 128, 128)), 1),
                    new Point(Math.Round(x) + 0.5, 0),
                    new Point(Math.Round(x) + 0.5, Bounds.Height));
            }
        }
    }

    private static double Measure(string text, Typeface face, double size, out double height)
    {
        FormattedText measured = new(
            text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, face, size, Brushes.Black);
        height = measured.Height;
        return measured.Width;
    }

    private static IBrush Translucent(string key, Color fallback, byte alpha)
    {
        Color colour = ThemeBrush(key, fallback) is ISolidColorBrush solid ? solid.Color : fallback;
        return new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
    }

    private static IBrush ThemeBrush(string key, Color fallback) =>
        Application.Current?.Resources.TryGetResource(key, null, out object? found) == true
        && found is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    private void AttachScroll()
    {
        ScrollViewer? found = _box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (found is null || ReferenceEquals(found, _scroll))
        {
            return;
        }

        _scroll = found;
        _scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty)
            {
                InvalidateVisual();
            }
        };
    }
}
