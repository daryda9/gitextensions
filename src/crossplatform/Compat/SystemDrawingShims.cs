// Minimal cross-platform stand-ins for the System.Drawing GDI types used by the
// reusable Git Extensions core (GitCommands / Extensibility). System.Drawing
// primitives (Point, Size, Color, Rectangle) come from System.Drawing.Primitives
// in the shared framework and are NOT redeclared here.
//
// These shims exist so the git-logic assemblies COMPILE on Linux. The Avalonia
// front-end does not drive the WinForms/GDI code paths; where a value is needed
// (e.g. font settings round-trip) the shim carries enough state to be correct.

namespace System.Drawing;

[Flags]
public enum FontStyle
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strikeout = 8,
}

public sealed class FontFamily
{
    public FontFamily(string name) => Name = name;
    public string Name { get; }
}

public sealed class Font
{
    public Font(string familyName, float size, FontStyle style = FontStyle.Regular)
    {
        FontFamily = new FontFamily(familyName);
        Size = size;
        Style = style;
    }

    public Font(FontFamily family, float size, FontStyle style = FontStyle.Regular)
    {
        FontFamily = family;
        Size = size;
        Style = style;
    }

    public FontFamily FontFamily { get; }
    public string Name => FontFamily.Name;
    public float Size { get; }
    public FontStyle Style { get; }
    public bool Bold => (Style & FontStyle.Bold) != 0;
    public bool Italic => (Style & FontStyle.Italic) != 0;

    public Font Clone() => new(FontFamily, Size, Style);
}

public static class SystemFonts
{
    // No installed-font enumeration on the shim; a sane default keeps settings
    // round-tripping. The Avalonia UI uses its own fonts for rendering.
    public static Font DefaultFont => new("Sans", 9f);
    public static Font MessageBoxFont => new("Sans", 9f);
}

// Opaque handles — only used as nullable field/parameter types in the core.
public class Image : IDisposable
{
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public virtual void Dispose() => GC.SuppressFinalize(this);
}

public sealed class Bitmap : Image
{
    public Bitmap(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

public sealed class Icon : IDisposable
{
    // Windows-only in the real framework; on Linux we cannot extract shell icons.
    public static Icon? ExtractAssociatedIcon(string filePath) => null;
    public void Dispose() => GC.SuppressFinalize(this);
}

public sealed class Graphics : IDisposable
{
    // No GDI on Linux; rough estimate sufficient for the fallback measure paths.
    public SizeF MeasureString(string? text, Font? font)
    {
        int len = text?.Length ?? 0;
        float size = font?.Size ?? 9f;
        return new SizeF(len * size * 0.6f, size * 1.4f);
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
