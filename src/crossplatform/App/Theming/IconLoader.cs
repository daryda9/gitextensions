using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Loads the reused Git Extensions PNG icons (linked into this assembly under
///  <c>avares://GitExtensions.Avalonia/Assets/Icons/</c>) by base file name,
///  e.g. <c>IconLoader.Load("Push")</c>. Results are cached; a missing icon
///  returns <see langword="null"/> so callers degrade to text-only.
/// </summary>
public static class IconLoader
{
    private const string Root = "avares://GitExtensions.Avalonia/Assets/Icons/";

    // Ordinal, deliberately: the avares: lookup below is case-sensitive, so "star"
    // and "Star" have different outcomes and must not share a cache entry. With an
    // ignore-case comparer whichever spelling was requested first would decide for
    // both, making a miscapitalised name resolve or not depending on load order.
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    // Names already reported as missing, so the diagnostic is emitted once per name
    // rather than once per call site — several views ask for the same icon, and the
    // toolbar re-asks on every rebuild.
    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);

    public static Bitmap? Load(string name)
        => Cache.GetOrAdd(name, static n =>
        {
            try
            {
                Uri uri = new(Root + n + ".png");
                if (!AssetLoader.Exists(uri))
                {
                    // The URI is case-sensitive and a miss is not an error anywhere
                    // else in this class, so without this line a mistyped or
                    // miscapitalised name simply draws nothing, silently.
                    Warn(n, "no such asset (the name is case-sensitive)");
                    return null;
                }

                return new Bitmap(AssetLoader.Open(uri));
            }
            catch (Exception ex)
            {
                // A missing or corrupt icon must never take a view down with it.
                Warn(n, ex.Message);
                return null;
            }
        });

    private static void Warn(string name, string reason)
    {
        if (!Reported.TryAdd(name, 0))
        {
            return;
        }

        string line = $"[IconLoader] icon '{name}' did not resolve ({Root}{name}.png): {reason}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }

    // Names that had no vector glyph and were served from the PNG set, so the
    // remaining raster coverage is measurable from a single run's output rather
    // than by reading every call site.
    private static readonly ConcurrentDictionary<string, byte> FellBackToRaster = new(StringComparer.Ordinal);

    private static void NoteRasterFallback(string name)
    {
        if (!FellBackToRaster.TryAdd(name, 0))
        {
            return;
        }

        string line = $"[IconLoader] icon '{name}' has no vector glyph, drawing the 2015 PNG instead";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }

    /// <summary>
    ///  Builds an <see cref="Image"/> control for the named icon at the given
    ///  square size, or <see langword="null"/> if the icon is missing.
    ///  <para>
    ///   Names carried by <see cref="Icons"/> are drawn as monochrome vector
    ///   glyphs tinted from the palette; every other name still resolves to its
    ///   original PNG, which is what lets the glyph set grow one batch at a time
    ///   without any call site changing.
    ///  </para>
    /// </summary>
    /// <param name="tintKey">
    ///  Palette resource key the glyph is painted with — one of
    ///  <see cref="Icons.Text"/>, <see cref="Icons.TextDim"/> or
    ///  <see cref="Icons.Accent"/>. Ignored by the PNG fallback, whose colours
    ///  are baked into the bitmap.
    /// </param>
    public static Image? Image(string name, double size = 16, string tintKey = Icons.Text)
    {
        if (Icons.Get(name) is { } glyph)
        {
            return new GlyphIcon(glyph, tintKey) { Width = size, Height = size };
        }

        NoteRasterFallback(name);

        Bitmap? bmp = Load(name);
        return bmp is null
            ? null
            : new Image { Source = bmp, Width = size, Height = size };
    }

    /// <summary>
    ///  Re-points an icon control built earlier by <see cref="Image(string, double, string)"/>
    ///  at a different icon name, keeping it a vector glyph when the new name has one.
    ///
    ///  <para>Three toolbar icons change with state (commit-info position, shell,
    ///  default pull action) and used to do it by assigning a <see cref="Bitmap"/>
    ///  straight onto <see cref="global::Avalonia.Controls.Image.Source"/>. That
    ///  works — which is the problem: the bitmap wins, and the icon silently
    ///  reverted to its 2015 PNG on the first refresh while every icon around it
    ///  stayed a glyph.</para>
    /// </summary>
    public static void Retarget(Image? target, string name, string tintKey = Icons.Text)
    {
        if (target is null)
        {
            return;
        }

        if (Icons.Get(name) is { } glyph)
        {
            if (target is GlyphIcon icon)
            {
                icon.SetGlyph(glyph, tintKey);
                return;
            }

            // The control was built from a PNG (its first name had no glyph) and
            // cannot grow the glyph's tint plumbing after the fact. Rare, and it
            // only costs this one icon its vector form until the view rebuilds.
            NoteRasterFallback(name);
        }
        else
        {
            NoteRasterFallback(name);
        }

        if (Load(name) is { } bmp)
        {
            target.Source = bmp;
        }
    }
}

/// <summary>
///  An <see cref="Image"/> whose source is a vector glyph from <see cref="Icons"/>
///  rather than a bitmap. It is an <c>Image</c> and not a bare
///  <see cref="Control"/> because call sites pattern-match the result of
///  <see cref="IconLoader.Image"/> as one, and three of them replace the
///  <see cref="Image.Source"/> of an icon they built earlier — assigning a
///  bitmap over the glyph just works, and those icons revert to their PNG.
/// </summary>
internal sealed class GlyphIcon : global::Avalonia.Controls.Image
{
    private readonly GlyphSource _glyph;
    private AvaloniaObject? _observed;

    /// <summary>
    ///  Swaps the drawn glyph in place, keeping the resolved tint and its
    ///  subscription. Used by <see cref="IconLoader.Retarget"/> for the toolbar
    ///  icons that change with state.
    /// </summary>
    internal void SetGlyph(Geometry geometry, string? tintKey = null)
    {
        _glyph.SetGeometry(geometry);

        if (tintKey is not null && _glyph.RetintNeeded(tintKey))
        {
            // The tint brush is a different instance now, so the subscription that
            // follows the theme has to move with it.
            if (_observed is not null)
            {
                _observed.PropertyChanged -= OnTintChanged;
                _observed = null;
            }

            _glyph.SetTintKey(tintKey);
            if (_glyph.Tint is AvaloniaObject observable)
            {
                _observed = observable;
                observable.PropertyChanged += OnTintChanged;
            }
        }

        // Source did not change identity, so nothing else asks for a repaint.
        InvalidateVisual();
    }

    internal GlyphIcon(Geometry geometry, string tintKey)
    {
        _glyph = new GlyphSource(geometry, tintKey);
        Source = _glyph;

        // Custom-drawn content does not clip to its bounds by default, and the
        // round join on an outermost stroke reaches half a stroke past the grid.
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Resolved here, not in the constructor: an icon can be built before the
        // palette is in Application.Resources.
        _glyph.Resolve(TextElement.GetForeground(this));

        // ThemeManager recolours by mutating the Color of the brush instances it
        // handed out, so holding the instance is all it takes to follow the
        // theme. Nothing invalidates this control when that happens, though —
        // the brush is not a styled property of it — hence the subscription.
        if (_glyph.Tint is AvaloniaObject observable)
        {
            _observed = observable;
            observable.PropertyChanged += OnTintChanged;
        }

        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnTintChanged;
            _observed = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTintChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name is "Color")
        {
            InvalidateVisual();
        }
    }
}

/// <summary>
///  Draws one <see cref="Icons"/> glyph as a stroked path in the palette tint.
/// </summary>
internal sealed class GlyphSource : IImage
{
    // The grid every glyph in Icons is authored on, and the stroke width in
    // that grid's units so it scales with the icon.
    private const double Grid = 24.0;
    private const double Stroke = 2.0;

    private string _tintKey;
    private Geometry _geometry;
    private IBrush? _inherited;
    private bool _resolved;

    internal GlyphSource(Geometry geometry, string tintKey)
    {
        _geometry = geometry;
        _tintKey = tintKey;
    }

    // Swapped in place by GlyphIcon.SetGlyph so a state-driven icon keeps the tint
    // it already resolved and the subscription that follows the theme.
    internal void SetGeometry(Geometry geometry) => _geometry = geometry;

    internal bool RetintNeeded(string tintKey) => !string.Equals(_tintKey, tintKey, StringComparison.Ordinal);

    // Only for icons whose colour is itself state (the Commit button's repo state):
    // the key changes, so the tint has to be resolved again from the palette.
    internal void SetTintKey(string tintKey)
    {
        _tintKey = tintKey;
        if (_resolved)
        {
            Tint = Icons.Tint(_tintKey);
        }
    }

    internal IBrush? Tint { get; private set; }

    public Size Size => new(Grid, Grid);

    internal void Resolve(IBrush? inherited)
    {
        _inherited = inherited;
        Tint = Icons.Tint(_tintKey);
        _resolved = true;
    }

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (destRect.Width <= 0 || destRect.Height <= 0)
        {
            return;
        }

        if (!_resolved)
        {
            Tint = Icons.Tint(_tintKey);
        }

        // Falls back to the inherited text foreground rather than a fixed
        // colour, so a missing palette key still tracks the theme.
        IBrush brush = Tint ?? _inherited ?? Brushes.Gray;

        double scale = Math.Min(destRect.Width / sourceRect.Width, destRect.Height / sourceRect.Height);
        double drawn = Grid * scale;
        Matrix transform =
            Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(
                destRect.X + ((destRect.Width - drawn) / 2),
                destRect.Y + ((destRect.Height - drawn) / 2));

        Pen pen = new(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

        using (context.PushClip(destRect))
        using (context.PushTransform(transform))
        {
            context.DrawGeometry(null, pen, _geometry);
        }
    }
}

