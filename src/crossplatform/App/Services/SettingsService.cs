using System.Text.Json;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  App-level preferences that are neither git config nor UI layout.
///
///  <para>Currently this holds the default pull action (merge / rebase /
///  fetch-only). It is stored in its own small JSON file rather than being
///  folded into <see cref="UiState"/>: <c>UiStateService</c> is owned/edited by
///  another part of the port, so adding a field there would risk a merge
///  conflict. Keeping this separate is the least-conflicting choice — the
///  file lives alongside <c>ui-state.json</c> under the user's config dir:
///  <c>GitExtensions.Avalonia/app-settings.json</c>.</para>
/// </summary>
public sealed class AppPreferences
{
    /// <summary>
    ///  Default pull action: "merge", "rebase" or "fetch".
    ///
    ///  <para><b>Superseded and no longer read by any view.</b> The toolbar's Pull
    ///  split button reads <see cref="UiState.DefaultPullAction"/> (which also covers
    ///  FetchAll / FetchPruneAll), and the Settings dialog now writes that one, so
    ///  choosing an action finally affects the toolbar. This property is kept only so
    ///  an existing <c>app-settings.json</c> still deserialises; do not wire it to
    ///  anything.</para>
    /// </summary>
    public string DefaultPullAction { get; set; } = "merge";

    /// <summary>
    ///  What the checkout dialog pre-selects for pending local changes:
    ///  "DontChange", "Merge", "Reset" or "Stash" (the names of
    ///  <c>LocalChangesAction</c>; upstream's "Set as default" checkbox).
    ///  <c>null</c>/absent means "ask, pre-selecting Don't change".
    /// </summary>
    public string DefaultCheckoutLocalChangesAction { get; set; } = "DontChange";

    /// <summary>
    ///  Commit dialog: close it after EVERY commit
    ///  (<c>AppSettings.CloseCommitDialogAfterCommit</c>).
    /// </summary>
    public bool CloseCommitDialogAfterCommit { get; set; }

    /// <summary>
    ///  Commit dialog: close it once the commit leaves NOTHING unstaged
    ///  (<c>AppSettings.CloseCommitDialogAfterLastCommit</c>). Upstream defaults this
    ///  one to on, and only consults it when the "after every commit" option is off.
    /// </summary>
    public bool CloseCommitDialogAfterLastCommit { get; set; } = true;

    /// <summary>
    ///  Commit dialog: reload the file lists whenever the window is activated
    ///  (<c>AppSettings.RefreshArtificialCommitOnApplicationActivated</c>), so edits
    ///  made in an editor while the dialog was in the background show up.
    /// </summary>
    public bool RefreshCommitDialogOnFocus { get; set; }

    /// <summary>
    ///  Commit dialog: selecting the message box also selects the staged list
    ///  (<c>AppSettings.CommitDialogSelectStagedOnEnterMessage</c>), so the diff pane
    ///  shows what is about to be committed while the message is typed.
    /// </summary>
    public bool CommitDialogSelectStagedOnEnterMessage { get; set; }
}

/// <summary>Reads/writes <see cref="AppPreferences"/>, tolerating a missing or
/// corrupt file by returning defaults.</summary>
public sealed class SettingsService
{
    /// <summary>Allowed pull-action tokens (order = display order).</summary>
    public static readonly IReadOnlyList<string> PullActions = new[] { "merge", "rebase", "fetch" };

    /// <summary>Allowed local-changes tokens (names of <c>LocalChangesAction</c>).</summary>
    public static readonly IReadOnlyList<string> CheckoutLocalChangesActions =
        new[] { "DontChange", "Merge", "Reset", "Stash" };

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsService() => _path = ResolvePath();

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _path;

    /// <summary>Loads persisted settings; returns defaults if absent/unreadable.</summary>
    public AppPreferences Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                AppPreferences? s = JsonSerializer.Deserialize<AppPreferences>(json, Options);
                if (s is not null)
                {
                    return Sanitize(s);
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → defaults.
        }

        return new AppPreferences();
    }

    /// <summary>Writes the given settings; best-effort (never throws).</summary>
    public void Save(AppPreferences settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(Sanitize(settings), Options));
        }
        catch
        {
            // Persistence is best-effort; a failure must not crash the app.
        }
    }

    private static AppPreferences Sanitize(AppPreferences s)
    {
        if (!PullActions.Contains(s.DefaultPullAction))
        {
            s.DefaultPullAction = "merge";
        }

        if (!CheckoutLocalChangesActions.Contains(s.DefaultCheckoutLocalChangesAction))
        {
            s.DefaultCheckoutLocalChangesAction = "DontChange";
        }

        return s;
    }

    private static string ResolvePath()
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "app-settings.json");
    }
}
