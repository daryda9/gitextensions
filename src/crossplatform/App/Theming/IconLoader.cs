using System.Collections.Concurrent;
using System.Diagnostics;
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
