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

    /// <summary>
    ///  Everything the shared file machinery needs to know about this document. Built
    ///  once and static, because <see cref="JsonSettingsFile{T}.For"/> keeps the first
    ///  model it is given for a path.
    /// </summary>
    private static readonly JsonSettingsModel<CommitInfoSettings> Model = new(
        static () => new CommitInfoSettings(),
        static text => JsonSerializer.Deserialize<CommitInfoSettings>(text, Options),
        static settings => JsonSerializer.Serialize(settings, Options),
        Sanitize,
        "saving commit-info settings",
        static () => Changed?.Invoke());

    private readonly JsonSettingsFile<CommitInfoSettings> _file;

    public CommitInfoSettingsService()
        => _file = JsonSettingsFile<CommitInfoSettings>.For(ResolvePath(), Model);

    /// <summary>The resolved JSON file path (for diagnostics/tests).</summary>
    public string FilePath => _file.Path;

    /// <summary>Loads persisted toggles; returns defaults if absent or unreadable.</summary>
    public CommitInfoSettings Load() => _file.Load();

    /// <summary>
    ///  Writes the given toggles; best-effort (never throws).
    ///
    ///  <para>Whole-document, and legitimately so: both editors present all six toggles at
    ///  once, so "these are the settings now" is what the user actually means. What the
    ///  shared file adds is that the two editors cannot interleave into each other's write
    ///  and that a kill mid-write cannot leave a file that reads back as defaults.</para>
    /// </summary>
    public void Save(CommitInfoSettings settings) => _file.Save(settings);

    /// <summary>
    ///  Applies <paramref name="mutate"/> to what the file says at write time — for the
    ///  caller that flips ONE toggle and must not revert the other five as they stood in
    ///  another editor. See <see cref="JsonSettingsFile{T}.Update"/> for the rule the
    ///  delegate has to respect.
    /// </summary>
    public void Update(Action<CommitInfoSettings> mutate) => _file.Update(mutate);

    /// <summary>Waits for deferred writes to reach the disk. Tests and shutdown only; blocks.</summary>
    public bool Flush(TimeSpan timeout) => _file.Flush(timeout);

    // Nothing to clamp: every field is a bool, so a corrupt JSON value cannot
    // survive deserialisation as anything but false. The three branch flags are
    // deliberately left independent of one another, as upstream's three separate
    // checkboxes are — the interaction between them is a rendering rule, not a
    // stored invariant.
    private static CommitInfoSettings Sanitize(CommitInfoSettings s) => s;

    private static string ResolvePath() => SettingsPaths.Resolve("commit-info.json");
}
