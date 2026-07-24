using System.ComponentModel.Composition;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Plugins;
using GitExtensions.Extensibility.Settings;

namespace GitExtensions.Avalonia.Plugins;

/// <summary>
///  A minimal, built-in <see cref="IGitPlugin"/> that proves the Avalonia plugin
///  pipeline end to end: it is discovered/registered by
///  <see cref="Services.PluginService"/>, exposes one typed <see cref="BoolSetting"/>
///  rendered by <see cref="Views.PluginSettingsWindow"/>, and — when run from the
///  <b>Plugins</b> menu — reads the live repository through <c>args.GitModule</c>
///  and reports a greeting.
///
///  <para>The <see cref="Export"/> attribute lets VS-MEF discover it in-process
///  (see <see cref="Services.PluginService"/>); if MEF is unavailable the same
///  instance is registered directly, so the class works on either path.</para>
/// </summary>
[Export(typeof(IGitPlugin))]
public sealed class SampleGreetPlugin : GitPluginBase
{
    // Persisted under this name via the plugin's SettingsSource (git config,
    // effective settings). Reused as both the storage key and the caption.
    private readonly BoolSetting _includeBranch =
        new("SampleGreet.IncludeBranchName", "Include the current branch name in the greeting", true);

    /// <summary>
    ///  The message produced by the most recent <see cref="Execute"/>. The host
    ///  (MainWindow) reads this after running the plugin and surfaces it in the
    ///  status bar, standing in for the WinForms MessageBox the original plugins use.
    /// </summary>
    public string? LastResult { get; private set; }

    public SampleGreetPlugin()
        : base(hasSettings: true)
    {
        Id = new Guid("2C0F5B1E-9E42-4E7B-9C1A-7F3D6A0B4E11");
        Name = "Sample Greet";
        Description = "Sample Greet";
    }

    public override IEnumerable<ISetting> GetSettings()
    {
        yield return _includeBranch;
    }

    public override bool Execute(GitUIEventArgs args)
    {
        // Read the live repository through the host-provided module. Whether to
        // include the branch name is driven by the plugin's own persisted setting.
        bool includeBranch = SettingsContainer is not null
            ? _includeBranch.ValueOrDefault(Settings)
            : _includeBranch.DefaultValue;

        string workingDir = args.GitModule.WorkingDir;
        string repoName = Path.GetFileName(workingDir.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(repoName))
        {
            repoName = workingDir;
        }

        if (includeBranch)
        {
            string branch;
            try
            {
                branch = args.GitModule.GetSelectedBranch();
            }
            catch
            {
                branch = "(unknown)";
            }

            LastResult = $"Hello from Sample Greet! Repository '{repoName}' is on branch '{branch}'.";
        }
        else
        {
            LastResult = $"Hello from Sample Greet! Repository '{repoName}'.";
        }

        // Returning true asks the host to refresh the revision grid, exercising the
        // "Execute → RefreshAll" path in MainWindow.
        return true;
    }
}
