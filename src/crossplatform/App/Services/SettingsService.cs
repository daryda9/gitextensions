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

    /// <summary>
    ///  Skip the "There are unresolved merge conflicts, solve conflicts now?"
    ///  question and go straight to the resolve dialog
    ///  (<c>AppSettings.DontConfirmResolveConflicts</c>). Read by
    ///  <see cref="ConflictFlow.HandleAsync"/>.
    ///
    ///  <para>File-only for now. Upstream drives it from the <b>Confirmations</b>
    ///  settings page (<c>ConfirmationsSettingsPage.cs:36</c>, inverted:
    ///  <c>chkResolveConflicts</c> checked = ask), and the port has <b>no</b>
    ///  Confirmations page at all — none of that page's seventeen checkboxes is
    ///  ported. Adding this one alone would be a page of one, so the flag lives in
    ///  <c>app-settings.json</c> until that page exists. Off by default, i.e. the
    ///  question is asked, exactly like upstream.</para>
    /// </summary>
    public bool DontConfirmResolveConflicts { get; set; }

    /// <summary>
    ///  Commit message editor: soft-wrap long lines instead of scrolling sideways
    ///  (<c>AppSettings.MessageEditorWordWrap</c>, default off upstream).
    ///
    ///  <para>Off is also what the validation marks want: with wrapping on, one logical
    ///  line covers several visual rows and the over-limit band drawn by
    ///  <see cref="TextRulerOverlay"/> would sit on the wrong row. The overlay therefore
    ///  disables itself while this is on rather than drawing something misleading.</para>
    /// </summary>
    public bool CommitMessageWordWrap { get; set; }

    /// <summary>
    ///  Maximum length of the commit message's SUBJECT line, 0 = no limit
    ///  (<c>AppSettings.CommitValidationMaxCntCharsFirstLine</c>). Drives both the
    ///  ruler/mark drawn behind the editor and the question asked before committing.
    /// </summary>
    public int CommitValidationFirstLineMaxChars { get; set; }

    /// <summary>
    ///  Maximum length of every OTHER line of the message, 0 = no limit
    ///  (<c>AppSettings.CommitValidationMaxCntCharsPerLine</c>). Also the column the
    ///  auto-wrap breaks at.
    /// </summary>
    public int CommitValidationMaxCharsPerLine { get; set; }

    /// <summary>
    ///  Keep the second line of the message empty, inserting one if the user starts
    ///  typing the body right under the subject
    ///  (<c>AppSettings.CommitValidationSecondLineMustBeEmpty</c>).
    /// </summary>
    public bool CommitValidationSecondLineMustBeEmpty { get; set; }

    /// <summary>
    ///  Re-flow a body line that grows past <see cref="CommitValidationMaxCharsPerLine"/>
    ///  onto the next line while typing (<c>AppSettings.CommitValidationAutoWrap</c>,
    ///  default on). Inert while the per-line limit is 0, exactly as upstream.
    /// </summary>
    public bool CommitValidationAutoWrap { get; set; } = true;

    /// <summary>
    ///  Paint the part of a line that exceeds its limit
    ///  (<c>AppSettings.MarkIllFormedLinesInCommitMsg</c>, default on). Upstream colours
    ///  the text; the port paints a translucent band behind it, which survives the
    ///  theme swap and does not fight the caret.
    /// </summary>
    public bool MarkIllFormedCommitLines { get; set; } = true;

    /// <summary>
    ///  How many earlier commit messages the commit dialog's message menu offers
    ///  (<c>AppSettings.CommitDialogNumberOfPreviousMessages</c>, default 6).
    /// </summary>
    public int CommitDialogNumberOfPreviousMessages { get; set; } = 6;
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

        // Clamped rather than rejected: a hand-edited file with a silly number must not
        // make the editor unusable, and 0 keeps its meaning of "no limit".
        s.CommitValidationFirstLineMaxChars = Math.Clamp(s.CommitValidationFirstLineMaxChars, 0, 999);
        s.CommitValidationMaxCharsPerLine = Math.Clamp(s.CommitValidationMaxCharsPerLine, 0, 999);
        s.CommitDialogNumberOfPreviousMessages = Math.Clamp(s.CommitDialogNumberOfPreviousMessages, 0, 50);

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
