using System.ComponentModel.Composition;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Plugins;
using GitExtensions.Extensibility.Settings;
using GitExtUtils;

namespace GitExtensions.Avalonia.Plugins;

/// <summary>
///  A real port of the WinForms <c>BackgroundFetch</c> plugin adapted to the
///  Avalonia/Linux plugin model. It validates the full port pipeline for a plugin
///  that does background work: it is registered by <see cref="Services.PluginService"/>,
///  exposes typed settings edited through <see cref="Views.PluginSettingsWindow"/>,
///  and — on <see cref="Register"/> — spins up an off-thread periodic timer that runs
///  <c>git fetch</c> (optionally <c>git fetch --all</c>) against the plugin's
///  <see cref="IGitModule"/> at the configured interval. <see cref="Unregister"/>
///  stops it and <see cref="Execute"/> triggers an immediate fetch.
///
///  <para>Unlike the original, this port avoids System.Reactive and the WinForms
///  settings dialog: the timer is a <see cref="PeriodicTimer"/> driven from a
///  background <see cref="Task"/>, and all fetch errors are swallowed (it is a
///  background op, and the UI thread is never touched).</para>
/// </summary>
[Export(typeof(IGitPlugin))]
public sealed class BackgroundFetchPlugin : GitPluginBase
{
    // Persisted under these names via the plugin's SettingsSource (git config,
    // effective settings) — the names double as storage keys and captions.
    private readonly NumberSetting<int> _fetchIntervalMinutes =
        new("BackgroundFetch.FetchIntervalMinutes", "Fetch interval (minutes) — 0 disables periodic fetch", 5);

    private readonly BoolSetting _fetchAllRemotes =
        new("BackgroundFetch.FetchAllRemotes", "Fetch all remotes (git fetch --all)", true);

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IGitModule? _module;

    /// <summary>
    ///  The message produced by the most recent <see cref="Execute"/>. The host
    ///  (MainWindow) reads this after running the plugin and surfaces it in the
    ///  status bar, standing in for the WinForms MessageBox the original plugins use.
    /// </summary>
    public string? LastResult { get; private set; }

    public BackgroundFetchPlugin()
        : base(hasSettings: true)
    {
        Id = new Guid("D19A7905-8AAD-4271-ACA9-817669B94A1D");
        Name = "Periodic background fetch";
        Description = "Periodically runs 'git fetch' in the background";
    }

    public override IEnumerable<ISetting> GetSettings()
    {
        yield return _fetchIntervalMinutes;
        yield return _fetchAllRemotes;
    }

    public override void Register(IGitUICommands gitUiCommands)
    {
        // Pushes the repository-backed settings source into the container so the
        // typed setting indexers can read/write git config.
        base.Register(gitUiCommands);

        _module = gitUiCommands.Module;
        StartTimer();
    }

    public override void Unregister(IGitUICommands gitUiCommands)
    {
        StopTimer();
        _module = null;
        base.Unregister(gitUiCommands);
    }

    public override bool Execute(GitUIEventArgs args)
    {
        // An explicit run from the Plugins menu triggers an immediate fetch on the
        // live module and reports the command that was run.
        bool fetchAll = ReadFetchAll();
        string command = BuildFetchCommand(fetchAll);
        RunFetch(args.GitModule, fetchAll);
        LastResult = $"Background fetch: ran '{command}'.";

        // Ask the host to refresh the revision grid so fetched refs are visible.
        return true;
    }

    /// <summary>
    ///  Builds the git command this plugin will run, as a human-readable string
    ///  (<c>git fetch</c> or <c>git fetch --all</c>). Kept separate so the logic is
    ///  trivially testable/inspectable.
    /// </summary>
    internal static string BuildFetchCommand(bool fetchAll)
        => fetchAll ? "git fetch --all" : "git fetch";

    private bool ReadFetchAll()
    {
        try
        {
            return SettingsContainer is { } container && ((AvaloniaSettingsContainer)container).HasSettingsSource
                ? _fetchAllRemotes.ValueOrDefault(Settings)
                : _fetchAllRemotes.DefaultValue;
        }
        catch
        {
            return _fetchAllRemotes.DefaultValue;
        }
    }

    private int ReadIntervalMinutes()
    {
        try
        {
            return SettingsContainer is { } container && ((AvaloniaSettingsContainer)container).HasSettingsSource
                ? _fetchIntervalMinutes.ValueOrDefault(Settings)
                : _fetchIntervalMinutes.DefaultValue;
        }
        catch
        {
            return _fetchIntervalMinutes.DefaultValue;
        }
    }

    private void StartTimer()
    {
        lock (_sync)
        {
            StopTimerNoLock();

            int minutes = ReadIntervalMinutes();
            if (minutes <= 0 || _module is null)
            {
                // 0 (or invalid) disables periodic fetch; Execute still works on demand.
                return;
            }

            IGitModule module = _module;
            TimeSpan period = TimeSpan.FromMinutes(minutes);
            CancellationTokenSource cts = new();
            _cts = cts;
            _loop = Task.Run(() => RunLoopAsync(module, period, cts.Token));
        }
    }

    private async Task RunLoopAsync(IGitModule module, TimeSpan period, CancellationToken token)
    {
        using PeriodicTimer timer = new(period);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                RunFetch(module, ReadFetchAll());
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via Unregister.
        }
        catch
        {
            // Background op — never surface loop failures.
        }
    }

    private void StopTimer()
    {
        lock (_sync)
        {
            StopTimerNoLock();
        }
    }

    private void StopTimerNoLock()
    {
        if (_cts is { } cts)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // Ignore disposal races.
            }

            _cts = null;
            _loop = null;
        }
    }

    private static void RunFetch(IGitModule module, bool fetchAll)
    {
        try
        {
            GitArgumentBuilder args = new("fetch");
            if (fetchAll)
            {
                args.Add("--all");
            }

            // git fetch writes its progress to standard error; we do not need the
            // output here. throwOnErrorExit:false keeps failures from bubbling up —
            // this is a background operation and errors are deliberately swallowed.
            module.GitExecutable.Execute(args, throwOnErrorExit: false);
        }
        catch
        {
            // Ignore background errors (offline, no remote, auth prompts, …).
        }
    }
}
