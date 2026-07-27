using System.Runtime.CompilerServices;
using GitCommands;

namespace GitExtensions.Avalonia;

/// <summary>
///  Keeps <c>HOME</c> pointing at the real login home for every git child process.
/// </summary>
/// <remarks>
///  <para>
///   The reused core rewrites <c>HOME</c> for the whole process every time it builds an
///   <c>Executable</c> (<c>EnvironmentConfiguration.SetEnvironmentVariables</c>). On Linux
///   its <c>GetDefaultHomeDir()</c> is wrong: it reads <c>HOME</c> from the
///   <c>User</c>/<c>Machine</c> environment targets, which .NET only supports on Windows —
///   on Unix both return <see langword="null"/>, so it falls through to
///   <see cref="Environment.SpecialFolder.Personal"/>, i.e. <c>~/Documents</c>.
///  </para>
///  <para>
///   Consequence: git children looked for <c>~/Documents/.gitconfig</c>, found no
///   <c>credential.helper</c>, and therefore asked for credentials on every push while
///   <c>git credential approve</c> silently stored nothing.
///  </para>
///  <para>
///   Fix without touching the shared core (and thus the Windows build):
///   <c>AppSettings.CustomHomeDir</c> is the *first* branch of the core's
///   <c>ComputeHomeLocation()</c>, so seeding it with the genuine home makes every later
///   recomputation land on the right directory. The home is captured in a
///   <see cref="ModuleInitializerAttribute"/>, which runs before <c>Main</c> and before any
///   core type is touched — by the time the core could clobber <c>HOME</c>, the real value
///   is already recorded.
///  </para>
/// </remarks>
internal static class HomeDirectoryFix
{
    /// <summary>The <c>HOME</c> value as it was before any core code could rewrite it.</summary>
    private static readonly string? OriginalHome = Environment.GetEnvironmentVariable("HOME");

    /// <summary>
    ///  Records the genuine login home so the core resolves <c>HOME</c> to it.
    ///  Never overrides a home the user configured on purpose.
    /// </summary>
    [ModuleInitializer]
    internal static void Apply()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (OriginalHome is not { Length: > 0 } home || !Directory.Exists(home))
        {
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(AppSettings.CustomHomeDir))
            {
                AppSettings.CustomHomeDir = home;
            }

            // The core may already have rewritten HOME while settings were loading.
            Environment.SetEnvironmentVariable("HOME", AppSettings.CustomHomeDir);
        }
        catch (Exception)
        {
            // Settings unavailable (first run, read-only config): keep the inherited HOME
            // rather than failing startup — the user is merely prompted for credentials.
        }
    }
}
