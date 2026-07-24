using System.Diagnostics;
using System.Reflection;
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
///  <para>The service therefore registers the built-in plugins directly in code and
///  then discovers any external plugins with a <b>Linux-safe, reflection-based folder
///  loader</b> — no MEF. It scans <c>GitExtensions.Avalonia/plugins/</c> under the XDG
///  config directory for <c>*.dll</c> files, loads each with
///  <see cref="Assembly.LoadFrom(string)"/> inside a try/catch, and reflects for public
///  non-abstract types that implement <see cref="IGitPlugin"/> and expose a public
///  parameterless constructor. Any assembly or type that fails to load is logged and
///  skipped, so a broken plugin can never take down the app. This keeps the whole
///  pipeline (menu → run off-thread → settings editor → persistence) fully working for
///  both built-in and folder-loaded plugins. <see cref="LoadStrategy"/> records the path
///  used.</para>
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
        // 1. Built-in plugins, registered directly in code. VS-MEF
        //    (ManagedExtensibility) is deliberately NOT used anywhere here: initialising
        //    it overflows the stack in this environment (see the class remarks), and a
        //    StackOverflowException cannot be caught.
        var plugins = new List<IGitPlugin> { new SampleGreetPlugin(), new BackgroundFetchPlugin() };
        int builtInCount = plugins.Count;

        // De-dupe by Id: built-ins are added first and win (first-wins policy).
        var seenIds = new HashSet<Guid>(plugins.Select(p => p.Id));

        // 2. External plugins, discovered from the folder via pure System.Reflection.
        string pluginsDir = GetPluginsDirectory();
        int folderCount = 0;
        foreach (IGitPlugin plugin in LoadFolderPlugins(pluginsDir))
        {
            if (seenIds.Add(plugin.Id))
            {
                plugins.Add(plugin);
                folderCount++;
            }
            else
            {
                Log($"skipping duplicate plugin Id {plugin.Id} ({plugin.GetType().FullName})");
            }
        }

        string strategy =
            $"reflection folder loader (no MEF): {builtInCount} built-in + {folderCount} from '{pluginsDir}'";
        return (plugins, strategy);
    }

    /// <summary>
    ///  Resolves the external plugins directory:
    ///  <c>$XDG_CONFIG_HOME</c> (or <c>~/.config</c>) / <c>GitExtensions.Avalonia/plugins</c>.
    /// </summary>
    private static string GetPluginsDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string configHome = !string.IsNullOrWhiteSpace(xdg)
            ? xdg!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");

        return Path.Combine(configHome, "GitExtensions.Avalonia", "plugins");
    }

    /// <summary>
    ///  Scans <paramref name="pluginsDir"/> for <c>*.dll</c> files and instantiates every
    ///  public, non-abstract <see cref="IGitPlugin"/> with a public parameterless
    ///  constructor. Failures at every level (missing folder, bad assembly, reflection
    ///  fault, constructor throw) are logged and skipped — never rethrown — so one broken
    ///  plugin can never bring down the host.
    /// </summary>
    private static IEnumerable<IGitPlugin> LoadFolderPlugins(string pluginsDir)
    {
        if (!Directory.Exists(pluginsDir))
        {
            Log($"plugins directory absent, loading built-ins only: '{pluginsDir}'");
            yield break;
        }

        string[] dllPaths;
        try
        {
            dllPaths = Directory.GetFiles(pluginsDir, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Log($"failed to enumerate '{pluginsDir}': {ex.Message}");
            yield break;
        }

        foreach (string dllPath in dllPaths)
        {
            // Enumerate candidate types per-assembly with full isolation, then yield
            // outside the try/catch (you cannot 'yield' from inside a try with a catch).
            foreach (IGitPlugin plugin in InstantiatePluginsFromAssembly(dllPath))
            {
                yield return plugin;
            }
        }
    }

    private static IReadOnlyList<IGitPlugin> InstantiatePluginsFromAssembly(string dllPath)
    {
        Type[] types;
        try
        {
            Assembly assembly = Assembly.LoadFrom(dllPath);
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Partial success: keep the types that did load, skip the rest.
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            Log($"partial type-load for '{dllPath}': {ex.Message}");
        }
        catch (Exception ex)
        {
            Log($"failed to load assembly '{dllPath}': {ex.Message}");
            return [];
        }

        var result = new List<IGitPlugin>();
        foreach (Type type in types)
        {
            if (!typeof(IGitPlugin).IsAssignableFrom(type)
                || !type.IsClass
                || !type.IsPublic
                || type.IsAbstract
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is IGitPlugin plugin)
                {
                    result.Add(plugin);
                    Log($"loaded plugin '{type.FullName}' from '{dllPath}'");
                }
            }
            catch (Exception ex)
            {
                Log($"failed to instantiate '{type.FullName}' from '{dllPath}': {ex.Message}");
            }
        }

        return result;
    }

    private static void Log(string message)
    {
        string line = $"[PluginService] {message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }
}
