using System.Diagnostics;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The third value of the Appearance ▸ Theme setting: <b>"System"</b>, which resolves
///  to <see cref="ThemeVariant.Light"/> or <see cref="ThemeVariant.Dark"/> from the
///  desktop's own preference and keeps following it while the app runs.
/// </summary>
/// <remarks>
///  <para><b>Where the preference comes from.</b>
///  <see cref="IPlatformSettings.GetColorValues"/> — on Linux that is the XDG desktop
///  portal, <c>org.freedesktop.appearance color-scheme</c> (what GNOME writes when
///  <c>org.gnome.desktop.interface color-scheme</c> is set to <c>prefer-dark</c>), and
///  on Windows and macOS the platform's own API. Reading it through Avalonia rather
///  than through gsettings is what keeps the port's one code path correct on all
///  three.</para>
///
///  <para><b>Why a class of its own and not a fourth <see cref="ThemeVariant"/>.</b>
///  "System" is not a palette: <see cref="ThemeManager"/> only ever applies a concrete
///  variant, and every caller that needs a variant asks <see cref="VariantOf"/> for one.
///  What this class adds is the SUBSCRIPTION — the theme has to change again later,
///  without the user touching the setting — and that is state the palette must not own.</para>
///
///  <para><b>Liveness.</b> The desktop's change arrives on
///  <see cref="IPlatformSettings.ColorValuesChanged"/>, which Avalonia may raise off
///  the UI thread; the re-apply is posted to the dispatcher because it mutates the
///  palette brushes every view is bound to. It is a no-op unless the user's choice is
///  "System" — an explicit Dark or Light is an explicit answer and must not move.</para>
/// </remarks>
internal static class SystemTheme
{
    /// <summary>The value stored in <c>UiState.Theme</c> for "follow the desktop".</summary>
    internal const string Name = "System";

    private static bool _following;
    private static bool _hooked;

    // What the desktop preferred the last time this app ran (from UiState), and what it
    // has been CONFIRMED to prefer during this one.
    //
    // Both exist because of a startup race that is visible to the eye: on Linux the
    // preference arrives from the portal over DBus, asynchronously, and until it does
    // Avalonia answers with its own default (Light). The first window is built well
    // before that, so a dark desktop got a white flash. Seeding the first answer with
    // last run's value removes the flash, and the confirmation — the portal's reply, or
    // the reconcile below when there is no reply — is what keeps the seed from
    // outliving a preference the user has since changed.
    private static ThemeVariant? _seed;
    private static ThemeVariant? _confirmed;

    /// <summary>
    ///  The variant a stored <c>UiState.Theme</c> value means. "Light" and "Dark" are
    ///  themselves; anything else — <see cref="Name"/>, or a hand-edited value the
    ///  normaliser has not seen — follows the desktop.
    /// </summary>
    internal static ThemeVariant VariantOf(string theme)
        => theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => Current(),
        };

    /// <summary>
    ///  The desktop's preference: the confirmed value if this run has one, otherwise
    ///  last run's, otherwise whatever the platform says right now.
    /// </summary>
    internal static ThemeVariant Current() => _confirmed ?? _seed ?? Platform();

    /// <summary>
    ///  The value to store for the next run, as a <c>UiState.Theme</c>-style name. Never
    ///  "System": this is the answer, not the question.
    /// </summary>
    internal static string LastSeenName => Current() == ThemeVariant.Light ? "Light" : "Dark";

    /// <summary>
    ///  Hands in what the desktop preferred at the end of the last run, before the first
    ///  appearance is applied. Ignored once the platform has confirmed a value.
    /// </summary>
    internal static void Seed(string name)
        => _seed = name switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => null,
        };

    /// <summary>
    ///  What the platform says right now. Falls back to
    ///  <see cref="ThemeVariant.Dark"/> — the port's own default — when there is no
    ///  platform to ask (before <c>Initialize</c>, or in the headless harnesses).
    /// </summary>
    private static ThemeVariant Platform()
    {
        try
        {
            return Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
        catch (Exception)
        {
            // A portal that is absent or slow to answer must not stop the app from
            // starting: the platform throws here on some setups, and a theme is not
            // worth a crash.
            return ThemeVariant.Dark;
        }
    }

    /// <summary>
    ///  Starts or stops following the desktop. Called with the live setting every time
    ///  the appearance is applied, so it is also what a switch back to an explicit
    ///  Dark or Light turns off.
    /// </summary>
    internal static void Follow(bool follow)
    {
        bool changed = follow != _following;
        _following = follow;

        if (follow)
        {
            Hook();
        }

        // One line per actual change of mind, not per apply: the appearance is applied
        // on every settings preview, and the log is here to answer "did it read the
        // desktop, or fall back?" — which only a change can make unclear.
        if (changed)
        {
            Log(follow ? $"following the desktop, which prefers {Current()}" : "no longer following the desktop");
        }
    }

    private static void Log(string message)
    {
        string line = $"[Theme] {message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }

    // Subscribed once and never removed: the target is static and lives as long as the
    // process, so there is nothing to leak, and the handler itself is a no-op while
    // the user's choice is explicit.
    private static void Hook()
    {
        if (_hooked || Application.Current?.PlatformSettings is not { } settings)
        {
            return;
        }

        _hooked = true;
        settings.ColorValuesChanged += (_, values) =>
        {
            ThemeVariant variant = values.ThemeVariant == PlatformThemeVariant.Light
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            // Recorded even while the user's choice is explicit: the value is what the
            // desktop prefers, which is worth knowing (and seeding the next run with)
            // whether or not this run is following it.
            _confirmed = variant;

            // Nothing to do when it says what is already on screen: the portal's first
            // reply usually only confirms the seed, and re-applying an identical palette
            // would repaint every window for nothing.
            if (!_following || ThemeManager.CurrentVariant == variant)
            {
                return;
            }

            Log($"the desktop switched to {variant}");
            Dispatcher.UIThread.Post(() => ThemeManager.Apply(variant));
        };

        // The reconcile. ColorValuesChanged only fires when the platform's answer
        // DIFFERS from what Avalonia already held, so a desktop that agrees with
        // Avalonia's default is silent — and a seed from the last run would then never
        // be corrected. One late look settles it: by now the portal has answered, and
        // if it never will, the platform's own default is the honest answer.
        DispatcherTimer.RunOnce(
            () =>
            {
                if (_confirmed is not null)
                {
                    return;
                }

                ThemeVariant variant = Platform();
                _confirmed = variant;
                if (_following && ThemeManager.CurrentVariant != variant)
                {
                    Log($"the desktop prefers {variant} after all");
                    ThemeManager.Apply(variant);
                }
            },
            TimeSpan.FromSeconds(1));
    }
}
