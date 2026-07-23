using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

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

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Load(string name)
        => Cache.GetOrAdd(name, static n =>
        {
            try
            {
                Uri uri = new(Root + n + ".png");
                if (!AssetLoader.Exists(uri))
                {
                    return null;
                }

                return new Bitmap(AssetLoader.Open(uri));
            }
            catch
            {
                return null;
            }
        });

    /// <summary>
    ///  Builds an <see cref="Image"/> control for the named icon at the given
    ///  square size, or <see langword="null"/> if the icon is missing.
    /// </summary>
    public static Image? Image(string name, double size = 16)
    {
        Bitmap? bmp = Load(name);
        return bmp is null
            ? null
            : new Image { Source = bmp, Width = size, Height = size };
    }
}
