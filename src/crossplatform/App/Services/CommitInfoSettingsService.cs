using System.Text.Json;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The visibility toggles of the commit-info panel's context menu — the port's
///  equivalent of upstream's <c>AppSettings.CommitInfoShow*</c> family, which
///  <c>GitUI/CommitInfo/CommitInfo.cs</c> reads back on every refresh
///  (<c>CommitInfo.cs</c> ≈ lines 309-313) and writes from the checked menu items.
///
///  <para>Defaults mirror upstream: local containing branches and containing tags
///  are shown, remote ones are not, annotated-tag messages and the
///  "derives from tag" line are shown.</para>
/// </summary>
public sealed class CommitInfoSettings
{
    /// <summary>Show the "Contained in branches" section for local branches.</summary>
    public bool ShowContainedInBranchesLocal { get; set; } = true;

    /// <summary>Also list remote-tracking branches that contain the commit.</summary>
    public bool ShowContainedInBranchesRemote { get; set; }

    /// <summary>
    ///  List remote-tracking branches only when no local branch contains the
    ///  commit. Independent of <see cref="ShowContainedInBranchesRemote"/>:
    ///  upstream fetches remotes when either flag is set and then suppresses
    ///  them at format time as soon as a local branch shows up
    ///  (<c>RefsFormatter.FilterAndFormatBranches</c>).
    /// </summary>
    public bool ShowContainedInBranchesRemoteIfNoLocal { get; set; }

    /// <summary>Show the "Contained in tags" section.</summary>
    public bool ShowContainedInTags { get; set; } = true;

    /// <summary>Show the message body of annotated tags pointing at the commit.</summary>
    public bool ShowAnnotatedTagsMessages { get; set; } = true;

    /// <summary>Show the "Derives from tag" line (<c>git describe</c>).</summary>
    public bool ShowTagThisCommitDerivesFrom { get; set; } = true;

    /// <summary>A copy, so a menu can mutate a draft without affecting the live state.</summary>
    public CommitInfoSettings Clone() => (CommitInfoSettings)MemberwiseClone();
}

/// <summary>
///  Reads/writes <see cref="CommitInfoSettings"/> as JSON next to
///  <see cref="UiState"/> — <c>$XDG_CONFIG_HOME/GitExtensions.Avalonia/commit-info.json</c>
///  — tolerating a missing or corrupt file by returning defaults.
///
///  <para>These live in their own file rather than as fields of
///  <see cref="UiState"/> on purpose: <c>MainWindow</c> keeps a single
///  <see cref="UiState"/> instance loaded at start-up and serialises the whole
///  object again when it closes, so a write performed by a view that does not
///  share that instance would be silently reverted on exit. A separate file has
///  no such last-writer-wins hazard, and every write is immediate, so the state
///  survives even a hard kill.</para>
/// </summary>
public sealed class CommitInfoSettingsService
{
    /// <summary>
    ///  Raised after any instance has written the file, on the thread that wrote it.
    ///
    ///  <para>The toggles now have two editors — the commit-info panel's context menu
    ///  and the Settings dialog — and each holds its own <see cref="CommitInfoSettings"/>
    ///  instance. Without this, the panel would keep rendering from the copy it loaded
    ///  at start-up and would write that stale copy back over the dialog's at its next
    ///  own toggle: the same last-writer-wins trap <see cref="UiState"/> has. Listening
    ///  to this and re-loading makes the file the single source of truth.</para>
    /// </summary>
    public static event Action? Changed;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public CommitInfoSettingsService() => _path = ResolvePath();

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _path;

    /// <summary>Loads persisted toggles; returns defaults if absent or unreadable.</summary>
    public CommitInfoSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                CommitInfoSettings? loaded =
                    JsonSerializer.Deserialize<CommitInfoSettings>(File.ReadAllText(_path), Options);
                if (loaded is not null)
                {
                    return Sanitize(loaded);
                }
            }
        }
        catch
        {
            // Missing/corrupt/unreadable → defaults.
        }

        return new CommitInfoSettings();
    }

    /// <summary>Writes the given toggles; best-effort (never throws).</summary>
    public void Save(CommitInfoSettings settings)
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

        // Announced even if the write failed: the in-memory intent still changed, and a
        // listener re-reading the old file simply keeps what it had.
        Changed?.Invoke();
    }

    // Nothing to clamp: every field is a bool, so a corrupt JSON value cannot
    // survive deserialisation as anything but false. The three branch flags are
    // deliberately left independent of one another, as upstream's three separate
    // checkboxes are — the interaction between them is a rendering rule, not a
    // stored invariant.
    private static CommitInfoSettings Sanitize(CommitInfoSettings s) => s;

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

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "commit-info.json");
    }
}
