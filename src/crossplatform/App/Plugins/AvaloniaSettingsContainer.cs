using GitExtensions.Extensibility.Plugins;
using GitExtensions.Extensibility.Settings;

namespace GitExtensions.Avalonia.Plugins;

/// <summary>
///  Avalonia/Linux implementation of <see cref="IGitPluginSettingsContainer"/>.
///
///  <para>It bridges a plugin's typed settings to the reused core's effective
///  <see cref="SettingsSource"/> for the open repository (produced by
///  <c>GitModule.GetEffectiveSettings()</c> — the same source the WinForms host
///  uses). <see cref="GitPluginBase.Register"/> pushes that source in via
///  <see cref="SetSettingsSource"/>; the typed setting indexers then read/write
///  through it, so values persist to git config exactly as in the original app.</para>
/// </summary>
public sealed class AvaloniaSettingsContainer : IGitPluginSettingsContainer
{
    private SettingsSource? _settingsSource;

    public SettingsSource GetSettingsSource()
    {
        return _settingsSource
            ?? throw new InvalidOperationException(
                "No settings source has been set. Open a repository and register the plugin first.");
    }

    public void SetSettingsSource(SettingsSource? settingsSource)
    {
        _settingsSource = settingsSource;
    }

    /// <summary>Whether a repository-backed settings source is currently available.</summary>
    public bool HasSettingsSource => _settingsSource is not null;
}
