using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The two typefaces the app draws with — upstream's <c>AppSettings.Font</c> and
///  <c>AppSettings.MonospaceFont</c>, which the port had no way of setting and, worse,
///  was getting wrong.
///
///  <para><b>The bug this fixes.</b> Twenty-seven places asked for the family
///  <c>"monospace,Consolas,Menlo"</c>. On Linux "monospace" is an <i>fontconfig alias</i>,
///  not a family name, and Skia resolves families by name: none of the three exists here,
///  so every one of those surfaces — the diff, the commit message editor, the console,
///  the blame gutter — silently rendered in the proportional default. It was measured on
///  screen, with an editor line of "iiiiiiiiii" followed by "WWWWWWWWWW": the two halves
///  came out different widths.</para>
///
///  <para>So the default is not a name but a SEARCH: the first family of
///  <see cref="MonospaceCandidates"/> that the font manager actually reports. A name in
///  the settings wins over the search, and an unresolvable name falls back to it rather
///  than to nothing — a typo in a settings file must not cost the user their diff.</para>
/// </summary>
public static class AppFonts
{
    /// <summary>
    ///  Fixed-width families to try, in order: the three Linux desktops ship at least one
    ///  of the first four, and the last two cover a macOS/Windows checkout of this port.
    /// </summary>
    private static readonly string[] MonospaceCandidates =
    [
        "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Ubuntu Mono",
        "Menlo", "Consolas",
    ];

    private static FontFamily? _monospace;
    private static double _monospaceSize;
    private static FontFamily? _ui;
    private static double _uiSize;

    /// <summary>The fixed-width family, resolved once per process (see <see cref="Reload"/>).</summary>
    public static FontFamily Monospace
    {
        get
        {
            if (_monospace is not null)
            {
                return _monospace;
            }

            (FontFamily family, bool resolved) = ResolveMonospace(Prefs().MonospaceFontFamily);

            // Only a REAL match is cached. An unresolved answer means the font manager
            // had nothing to say yet (very early startup), and caching that would leave
            // the whole app proportional until it restarts.
            if (resolved)
            {
                _monospace = family;
            }

            return family;
        }
    }

    /// <summary>Point size for monospaced surfaces, 0 = leave each surface's own size alone.</summary>
    public static double MonospaceSize
    {
        get
        {
            EnsureSizes();
            return _monospaceSize;
        }
    }

    /// <summary>
    ///  The interface family, or <see langword="null"/> to let Avalonia pick the system
    ///  default. Applied to each window as it is created (<see cref="ZoomWindow"/>), which
    ///  is how it reaches every control without a style per control type.
    /// </summary>
    public static FontFamily? Ui
    {
        get
        {
            EnsureSizes();
            return _ui;
        }
    }

    /// <summary>Interface point size, 0 = the theme's own.</summary>
    public static double UiSize
    {
        get
        {
            EnsureSizes();
            return _uiSize;
        }
    }

    /// <summary>
    ///  Forgets the resolved families so the next read picks up a changed setting.
    ///  Windows already built keep what they were given: a font change re-flows every
    ///  layout in the app, and doing that under the user's pointer is worse than asking
    ///  them to reopen the window.
    /// </summary>
    public static void Reload()
    {
        _monospace = null;
        _ui = null;
        _monospaceSize = 0;
        _uiSize = 0;
    }

    private static void EnsureSizes()
    {
        if (_ui is not null || _uiSize > 0)
        {
            return;
        }

        AppPreferences prefs = Prefs();
        _uiSize = prefs.UiFontSize;
        _monospaceSize = prefs.MonospaceFontSize;
        _ui = prefs.UiFontFamily is { Length: > 0 } name && Exists(name) ? new FontFamily(name) : null;
    }

    private static AppPreferences Prefs()
    {
        try
        {
            return new SettingsService().Load();
        }
        catch
        {
            // A font must never be the reason the app cannot start.
            return new AppPreferences();
        }
    }

    private static (FontFamily Family, bool Resolved) ResolveMonospace(string configured)
    {
        if (configured is { Length: > 0 } && Exists(configured))
        {
            return (new FontFamily(configured), true);
        }

        foreach (string candidate in MonospaceCandidates)
        {
            if (Exists(candidate))
            {
                return (new FontFamily(candidate), true);
            }
        }

        // Nothing matched — keep the historical string, which at least documents the
        // intent, rather than inventing a family that certainly does not exist.
        return (new FontFamily("monospace,Consolas,Menlo"), false);
    }

    /// <summary>Whether the font manager reports a family by that exact name.</summary>
    public static bool Exists(string family)
    {
        try
        {
            foreach (FontFamily installed in FontManager.Current.SystemFonts)
            {
                if (string.Equals(installed.Name, family, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // No font manager yet (very early startup): treat as "not found" and let the
            // caller fall back.
        }

        return false;
    }

    /// <summary>Every installed family name, sorted, for the Settings drop-downs.</summary>
    public static IReadOnlyList<string> InstalledFamilies()
    {
        try
        {
            return [.. FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch
        {
            return [];
        }
    }
}
