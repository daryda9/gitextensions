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
        // In the classic style the PNG is the CHOICE, not a shortfall: the whole point
        // of that style is the 2015 icon set. Reporting it would turn a normal run into
        // several hundred lines of false "missing glyph" diagnostics and destroy the
        // one measurement this log exists for — how much of the icon set still has no
        // vector form.
        if (ThemeManager.CurrentStyle == AppStyle.Classic)
        {
            return;
        }

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
    /// <param name="size">
    ///  Square edge in px. Defaults to <see cref="Metrics.Density.IconSize"/> (16), the
    ///  size the chrome draws every icon at: the 42 call sites that used to repeat the
    ///  literal now say nothing and get it from one place. The one deliberate exception
    ///  passes an explicit size (the 48px product logo in the About dialog).
    /// </param>
    public static Image? Image(string name, double size = Metrics.Density.IconSize, string tintKey = Icons.Text)
    {
        if (Icons.Get(name) is { } glyph)
        {
            // Built the same way in both styles. GlyphIcon carries the NAME as well as
            // the geometry and decides at draw time: the vector in Modern, the 2015 PNG
            // in Classic. That is what makes the switch hot — the views keep the exact
            // control instances they built, nothing is rebuilt.
            return new GlyphIcon(glyph, tintKey, name) { Width = size, Height = size };
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
    /// <param name="classicName">
    ///  Icon to load in the classic style when it is a DIFFERENT icon, not just the
    ///  raster form of the same one. Only the Commit button needs it.
    /// </param>
    public static void Retarget(Image? target, string name, string tintKey = Icons.Text, string? classicName = null)
    {
        if (target is null)
        {
            return;
        }

        if (Icons.Get(name) is { } glyph)
        {
            if (target is GlyphIcon icon)
            {
                // Works in both styles: the new NAME travels with the new geometry, so
                // a classic-styled icon re-points at the new PNG and a modern one at
                // the new vector, from the same call.
                icon.SetGlyph(glyph, tintKey, name, classicName);
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
    private bool _styleObserved;

    /// <summary>
    ///  Swaps the drawn glyph in place, keeping the resolved tint and its
    ///  subscription. Used by <see cref="IconLoader.Retarget"/> for the toolbar
    ///  icons that change with state.
    /// </summary>
    internal void SetGlyph(Geometry geometry, string? tintKey = null, string? name = null, string? classicName = null)
    {
        _glyph.SetGeometry(geometry, name);
        _glyph.SetClassicName(classicName);

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

    internal GlyphIcon(Geometry geometry, string tintKey, string name)
    {
        _glyph = new GlyphSource(geometry, tintKey, name);
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

        // Same discipline as the tint, and it matters more here: ThemeManager.StyleChanged
        // is a STATIC event, so an icon that stayed subscribed after being detached would
        // never be collected and the invocation list would grow for as long as the app
        // runs. The revision grid recycles its row containers on every scroll tick, so
        // "leaks one per detach" means thousands within a session — and every one of them
        // would still be invalidated on each style switch. Paired with the unsubscribe in
        // OnDetachedFromVisualTree below, and re-subscribed here on re-attach.
        if (!_styleObserved)
        {
            ThemeManager.StyleChanged += OnStyleChanged;
            _styleObserved = true;
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

        if (_styleObserved)
        {
            ThemeManager.StyleChanged -= OnStyleChanged;
            _styleObserved = false;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    ///  The style decides glyph-versus-PNG inside <see cref="GlyphSource.Draw"/>, and
    ///  <see cref="Image.Source"/> keeps the same identity across the switch, so
    ///  nothing else would ever ask this control to repaint.
    /// </summary>
    private void OnStyleChanged() => InvalidateVisual();

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

    // The icon's NAME, kept alongside the geometry so the classic style can ask
    // IconLoader for the matching 2015 PNG at draw time. Without it the control
    // would have no way back to the raster set once it was built as a glyph.
    private string _name;

    // The name to load the raster from when the classic style is on, when it differs
    // from the glyph's own name. Only the Commit button needs it: the modern surface
    // says the repo state with ONE glyph plus a tint, where upstream ships seven
    // different bitmaps — so "classic" there means a different icon, not the same
    // icon drawn differently. It is also the only glyph of the ninety with no PNG of
    // its own, which is exactly why this cannot be left to the name.
    private string? _classicName;

    internal GlyphSource(Geometry geometry, string tintKey, string name)
    {
        _geometry = geometry;
        _tintKey = tintKey;
        _name = name;
    }

    internal void SetClassicName(string? name) => _classicName = name;

    // Swapped in place by GlyphIcon.SetGlyph so a state-driven icon keeps the tint
    // it already resolved and the subscription that follows the theme.
    internal void SetGeometry(Geometry geometry, string? name = null)
    {
        _geometry = geometry;
        if (name is not null)
        {
            _name = name;
        }
    }

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

    /// <summary>
    ///  The accent brush this glyph's NAME earns, or <see langword="null"/> when the
    ///  icon has no role, colouring is off, or the call site asked for a tint of its
    ///  own.
    ///
    ///  <para>That last condition is the important one. Three tints are meaning
    ///  already — <see cref="Icons.TextDim"/> for a de-emphasised glyph,
    ///  <see cref="Icons.Accent"/> for one on an accented surface, and the
    ///  <c>App.RepoState*</c> family the Commit button uses to SAY the repository
    ///  state — and a role colour laid over any of them would overwrite information
    ///  with decoration. Only the default <see cref="Icons.Text"/> is up for grabs.</para>
    ///
    ///  <para>Resolved per draw rather than cached: <see cref="ThemeManager"/> hands
    ///  out live brush instances and mutates their colour in place, so a cached
    ///  instance would be correct — but a cached NULL, from an icon built before the
    ///  palette was registered, would be permanent. The lookup is two dictionary
    ///  probes.</para>
    /// </summary>
    private IBrush? Accent()
        => Accented && Icons.AccentOf(_name) is { } key
            ? Icons.Tint(key)
            : null;

    // The two conditions Accent() and Parts() share: colouring is on and the call site
    // did not ask for a tint that is itself meaning (see Accent's remarks).
    private bool Accented
        => ThemeManager.ColoredIcons && string.Equals(_tintKey, Icons.Text, StringComparison.Ordinal);

    /// <summary>
    ///  The bicoloured parts of this glyph, under the same conditions that earn it an
    ///  accent: a glyph forced to a meaning-carrying tint stays in that one colour, and
    ///  with colouring off the whole set is monochrome by definition.
    /// </summary>
    private IReadOnlyList<(Geometry Geometry, string Key)>? Parts()
        => Accented ? Icons.PartsOf(_name) : null;

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (destRect.Width <= 0 || destRect.Height <= 0)
        {
            return;
        }

        // ---- the classic style draws the 2015 bitmap instead ----------------------
        // Decided HERE, per draw, and not at construction time: that is what lets the
        // switch be hot. The control, its layout slot and its Source identity are
        // untouched, so no view is rebuilt and nothing has to be told the style moved
        // beyond the InvalidateVisual GlyphIcon issues on StyleChanged.
        //
        // If the name has no PNG (a glyph drawn for something the 2015 set never had)
        // the vector is drawn anyway: a blank icon would be a worse answer than a
        // slightly modern-looking one.
        if (ThemeManager.CurrentStyle == AppStyle.Classic
            && IconLoader.Load(_classicName ?? Icons.ClassicNameOf(_name) ?? _name) is { } bitmap)
        {
            context.DrawImage(bitmap, destRect);
            return;
        }

        if (!_resolved)
        {
            Tint = Icons.Tint(_tintKey);
        }

        // Falls back to the inherited text foreground rather than a fixed
        // colour, so a missing palette key still tracks the theme.
        IBrush brush = Accent() ?? Tint ?? _inherited ?? Brushes.Gray;

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
            // A bicoloured glyph is the same strokes in the same place, drawn a part at
            // a time so each can take its own hue; a part whose key is missing from the
            // palette falls back to the pen the whole glyph would have used, so a
            // half-registered palette still draws a complete icon.
            if (Parts() is { } parts)
            {
                foreach ((Geometry geometry, string key) in parts)
                {
                    context.DrawGeometry(null, PartPen(key, pen), geometry);
                }

                return;
            }

            context.DrawGeometry(null, pen, _geometry);
        }
    }

    // The pen for one part of a bicoloured glyph: the palette brush for its key, at the
    // same width and joins as the monochrome pen, so the parts assemble into exactly
    // the glyph the single geometry would have drawn.
    private static Pen PartPen(string key, Pen fallback)
        => Icons.Tint(key) is { } brush
            ? new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : fallback;
}

