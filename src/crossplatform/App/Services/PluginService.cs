using GitExtensions.Avalonia.Plugins;
using GitExtensions.Extensibility.Plugins;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Discovers and holds the set of available <see cref="IGitPlugin"/>s for the
///  Avalonia/Linux host, and gives each one an <see cref="AvaloniaSettingsContainer"/>
///  so its typed settings can be edited and persisted.
///
///  <para><b>Loading strategy — direct in-code registration.</b> The porting plan
///  warned that the WinForms VS-MEF discovery is fragile in this environment, and it
///  is worse than fragile: calling <c>ManagedExtensibility.Initialise</c> registers a
///  process-wide <c>AppDomain.AssemblyResolve</c> handler that, the moment MEF's
///  <c>PartDiscovery.Combine</c> tries to load a localized exception-string satellite
///  assembly, recurses into itself (<c>FileVersionInfo.GetVersionInfo</c> → PE
///  metadata read → another satellite resolve → …) and overflows the stack. A
///  <see cref="StackOverflowException"/> is uncatchable, so the MEF path cannot even
///  be attempted safely — the act of initialising it crashes the app.</para>
///
///  <para>The service therefore uses a hard-coded in-code registration of the
///  built-in plugins. This keeps the whole pipeline (menu → run off-thread →
///  settings editor → persistence) fully working; wiring real file-scan discovery
///  is left for when a Linux-safe loader replaces <c>ManagedExtensibility</c>.
///  <see cref="LoadStrategy"/> records the path used.</para>
/// </summary>
public sealed class PluginService
{
    private static readonly Lazy<PluginService> _instance = new(() => new PluginService());

    /// <summary>Process-wide singleton (MEF initialisation is a once-per-process step).</summary>
    public static PluginService Instance => _instance.Value;

    /// <summary>The discovered plugins, each with a settings container attached.</summary>
    public IReadOnlyList<IGitPlugin> Plugins { get; }

    /// <summary>Human-readable note of which discovery path was used (for logging/UI).</summary>
    public string LoadStrategy { get; }

    private PluginService()
    {
        (Plugins, LoadStrategy) = Discover();

        // Give every plugin its own settings container up front. The source is
        // attached later, per open repository, via AttachSettingsSource / Register.
        foreach (IGitPlugin plugin in Plugins)
        {
            plugin.SettingsContainer ??= new AvaloniaSettingsContainer();
        }
    }

    private static (IReadOnlyList<IGitPlugin>, string) Discover()
    {
        // Direct in-code registration. VS-MEF (ManagedExtensibility) is deliberately
        // NOT used: initialising it overflows the stack in this environment (see the
        // class remarks), and a StackOverflowException cannot be caught.
        IGitPlugin[] plugins = [new SampleGreetPlugin()];
        return (plugins, $"direct in-code registration ({plugins.Length} plugin(s))");
    }
}
