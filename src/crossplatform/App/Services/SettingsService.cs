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

    /// <summary>
    ///  Column the diff viewer draws a vertical rule at, 0 = none
    ///  (<c>AppSettings.DiffVerticalRulerPosition</c>, upstream default 0).
    /// </summary>
    public int DiffVerticalRulerPosition { get; set; }

    /// <summary>
    ///  With non-printing characters shown, whether a line ending is a GLYPH (¶) or
    ///  the words CRLF / LF / CR (<c>AppSettings.ShowEolMarkerAsGlyph</c>, default on).
    ///  Inert while the ¶ toggle of the diff toolbar is off, exactly as upstream:
    ///  <c>FileViewer.ToggleNonPrintingChars</c> only consults it when showing.
    /// </summary>
    public bool ShowEolMarkerAsGlyph { get; set; } = true;

    /// <summary>
    ///  Move to the next file when the diff is scrolled past its end
    ///  (<c>AppSettings.AutomaticContinuousScroll</c>, default off).
    /// </summary>
    public bool DiffContinuousScroll { get; set; }

    /// <summary>
    ///  How long the diff has to sit at its end before a further scroll moves on
    ///  (<c>AppSettings.AutomaticContinuousScrollDelay</c>, default 600 ms). The delay
    ///  is what stops one flick of the wheel from skipping a file the user never saw.
    /// </summary>
    public int DiffContinuousScrollDelay { get; set; } = 600;

    /// <summary>
    ///  For a MERGE commit, ask git for the condensed combined diff (<c>--cc</c>,
    ///  hunks that differ from every parent) instead of the full one (<c>-c -p</c>)
    ///  — <c>AppSettings.OmitUninterestingDiff</c>, default on.
    /// </summary>
    public bool OmitUninterestingDiff { get; set; } = true;

    /// <summary>
    ///  Ask git for the histogram diff algorithm (<c>--histogram</c>) instead of its
    ///  default Myers (<c>AppSettings.UseHistogramDiffAlgorithm</c>, default off).
    ///  Slower, and usually produces the more readable hunk boundaries.
    /// </summary>
    public bool UseHistogramDiffAlgorithm { get; set; }

    /// <summary>
    ///  For a MERGE commit, list the changed files once per parent instead of only
    ///  against the first (<c>AppSettings.ShowDiffForAllParents</c>, default on
    ///  upstream). Each group carries its own revision pair, so clicking a file under
    ///  "Diff with parent 2" shows that parent's patch and not the first parent's.
    /// </summary>
    public bool ShowDiffForAllParents { get; set; } = true;

    /// <summary>
    ///  Dim the TEXT of a non-relative revision as well as its graph lanes
    ///  (<c>AppSettings.RevisionGraphDrawNonRelativesTextGray</c>, default on).
    ///  Upstream keeps this apart from the lane graying, and so does the port now: the
    ///  two used to be one flag here, which made "gray the lanes" unavailable without
    ///  also fading every subject.
    /// </summary>
    public bool GraphDrawNonRelativesTextGray { get; set; } = true;

    /// <summary>
    ///  Give every other row a slightly different background
    ///  (<c>AppSettings.RevisionGraphDrawAlternateBackColor</c>, default on). The port
    ///  striped unconditionally before this.
    /// </summary>
    public bool GraphDrawAlternateBackColor { get; set; } = true;

    /// <summary>
    ///  Colour each graph lane by its branch (<c>AppSettings.MulticolorBranches</c>,
    ///  default on). Off draws the whole DAG in one foreground colour, which is what
    ///  a reader who finds the palette noisy — or who cannot tell its hues apart —
    ///  wants.
    /// </summary>
    public bool MulticolorBranches { get; set; } = true;

    /// <summary>
    ///  Straighten a lane that shifts by one column between two rows, so the line meets
    ///  its other half instead of kinking (<c>AppSettings.StraightenGraphDiagonals</c>,
    ///  default on).
    /// </summary>
    public bool StraightenGraphDiagonals { get; set; } = true;

    /// <summary>
    ///  Rows with more than this many segments are left un-straightened
    ///  (<c>AppSettings.StraightenGraphSegmentsLimit</c>, default 80). Upstream's reason
    ///  is cost, and it is the same here: the pass is O(segments²) per row boundary, and
    ///  a row that wide is unreadable with or without the tidy-up.
    /// </summary>
    public int StraightenGraphSegmentsLimit { get; set; } = 80;

    /// <summary>
    ///  Highlight the rows written by the AUTHOR of the selected revision
    ///  (<c>AppSettings.HighlightAuthoredRevisions</c>, default on) — upstream's
    ///  <c>AuthorHighlighting</c>, which is how one scans a branch for one person's
    ///  commits without filtering the grid down to them.
    /// </summary>
    public bool HighlightAuthoredRevisions { get; set; } = true;

    /// <summary>
    ///  Show a tooltip on a revision row (<c>AppSettings.ShowRevisionGridTooltips</c>,
    ///  default off upstream). The port had none at all; it now offers the full message
    ///  and the author, which is exactly what the truncated columns cannot show.
    /// </summary>
    public bool ShowRevisionGridTooltips { get; set; }

    /// <summary>
    ///  Include untracked files in a stash the USER asked for
    ///  (<c>AppSettings.IncludeUntrackedFilesInManualStash</c>, default off). The port
    ///  hard-coded this per call site and disagreed with itself: the toolbar and the
    ///  menu said no, the left tree said yes.
    /// </summary>
    public bool IncludeUntrackedFilesInManualStash { get; set; }

    /// <summary>
    ///  Include untracked files in a stash the APP makes on the user's behalf, before
    ///  a checkout with local changes
    ///  (<c>AppSettings.IncludeUntrackedFilesInAutoStash</c>, default off).
    /// </summary>
    public bool IncludeUntrackedFilesInAutoStash { get; set; }

    /// <summary>
    ///  What to do with the auto-stash after a checkout that created one:
    ///  <see cref="SettingsService.AskAlwaysNever"/> — "Ask" (upstream's <c>null</c>,
    ///  i.e. the question with a "don't ask again" box), "Always" or "Never"
    ///  (<c>AppSettings.AutoPopStashAfterCheckoutBranch</c>).
    /// </summary>
    public string AutoPopStashAfterCheckout { get; set; } = "Ask";

    /// <summary>
    ///  The same, for the stash the Pull dialog's "Stash changes" button made before
    ///  pulling (<c>AppSettings.AutoPopStashAfterPull</c>).
    /// </summary>
    public string AutoPopStashAfterPull { get; set; } = "Ask";

    /// <summary>
    ///  Pass <c>--autostash</c> to rebase (<c>AppSettings.RebaseAutoStash</c>, default
    ///  off), so a dirty working tree does not stop the rebase before it starts.
    /// </summary>
    public bool RebaseAutoStash { get; set; }

    /// <summary>
    ///  What push does about submodules (<c>AppSettings.RecursiveSubmodules</c>,
    ///  default 1): 0 = nothing, 1 = <c>--recurse-submodules=check</c> (refuse if a
    ///  submodule commit is not pushed), 2 = <c>=on-demand</c> (push them first).
    /// </summary>
    public int RecursiveSubmodules { get; set; } = 1;

    /// <summary>
    ///  How many repositories the recent list keeps
    ///  (<c>AppSettings.RecentRepositoriesHistorySize</c>, default 30). Written
    ///  straight into the core setting, because the core is what trims the list when
    ///  it saves it (<c>LocalRepositoryManager.AdjustHistorySize</c>) — a copy here
    ///  would be a second, disagreeing answer.
    /// </summary>
    public int RecentRepositoriesHistorySize { get; set; } = 30;

    /// <summary>
    ///  List the recent repositories in alphabetical order instead of
    ///  most-recent-first (<c>AppSettings.SortRecentRepos</c>, default off).
    /// </summary>
    public bool SortRecentRepos { get; set; }

    /// <summary>
    ///  How a long path is shortened in the recent list, by the NAME of
    ///  <c>ShorteningRecentRepoPathStrategy</c>: "None", "MostSignDir" (the repository
    ///  folder alone) or "MiddleDots" (the middle elided).
    /// </summary>
    public string ShorteningRecentRepoPathStrategy { get; set; } = "None";

    /// <summary>
    ///  How a path is shown in the changed-file list, by the name of
    ///  <c>TruncatePathMethod</c>: "None", "TrimStart" or "FileNameOnly".
    ///
    ///  <para>Upstream's fourth value, <c>Compact</c>, is not offered: it calls the
    ///  Win32 path-compacting API and its own code falls back to <c>None</c> off
    ///  Windows (<c>PathFormatter.cs:39</c>). Offering a choice that does nothing here
    ///  would be a fake button, which is the thing this round exists to remove.</para>
    /// </summary>
    public string TruncatePathMethod { get; set; } = "None";

    /// <summary>
    ///  How many commands the Output tab and the command-log window show, newest last
    ///  (<c>AppSettings.OutputHistoryDepth</c>, default 20).
    ///
    ///  <para>A DISPLAY depth, not a storage one: what is recorded is the core's
    ///  process-global <c>CommandLog</c>, whose own 500-entry cap belongs to the core
    ///  and is none of the port's business. Upstream's number does the same job for its
    ///  <c>OutputHistoryModel</c>.</para>
    /// </summary>
    public int OutputHistoryDepth { get; set; } = 20;

    /// <summary>
    ///  How long the revision grid's type-to-search keeps collecting keystrokes before
    ///  it forgets them, in milliseconds
    ///  (<c>AppSettings.RevisionGridQuickSearchTimeout</c>, default 4000). The port used
    ///  a hard-coded 3000.
    /// </summary>
    public int RevisionGridQuickSearchTimeout { get; set; } = 4000;

    /// <summary>
    ///  The fixed-width family for the diff, the commit editor, the console and the
    ///  blame gutter (<c>AppSettings.MonospaceFont</c>). Empty means "find one", which
    ///  is what <see cref="Theming.AppFonts"/> does — and had to start doing, because
    ///  the family the port asked for did not exist on this platform.
    /// </summary>
    public string MonospaceFontFamily { get; set; } = string.Empty;

    /// <summary>Point size for those surfaces; 0 leaves each one's own size alone.</summary>
    public double MonospaceFontSize { get; set; }

    /// <summary>
    ///  The interface family (<c>AppSettings.Font</c>). Empty = the system default.
    /// </summary>
    public string UiFontFamily { get; set; } = string.Empty;

    /// <summary>Interface point size; 0 = the theme's own.</summary>
    public double UiFontSize { get; set; }

    /// <summary>
    ///  The GitHub host the repository-host integration talks to
    ///  (<c>GitHub3Plugin.GitHubHost</c>, default <c>github.com</c>). Anything else is
    ///  taken to be a GitHub Enterprise install, whose API lives at
    ///  <c>https://&lt;host&gt;/api/v3</c>.
    ///
    ///  <para>The token is deliberately NOT here: see <see cref="GitHubTokenStore"/>.
    ///  This file is plain JSON that people copy between machines.</para>
    /// </summary>
    public string GitHubHost { get; set; } = "github.com";

    /// <summary>
    ///  Offer the issues assigned to you as commit-message templates
    ///  (<c>GitHub3Plugin.IssueCommitMessageHelperEnabled</c>, default on upstream —
    ///  off here, because upstream's plugin is only loaded when the user enables it,
    ///  whereas this integration is always present and would otherwise make an
    ///  unasked-for network call the first time the commit dialog opens).
    /// </summary>
    public bool GitHubIssueCommitMessages { get; set; }

    /// <summary>
    ///  How many assigned issues that helper offers
    ///  (<c>GitHub3Plugin.IssueCommitMessageHelperMaxCount</c>, default 10).
    /// </summary>
    public int GitHubIssueCommitMessageCount { get; set; } = 10;
}

/// <summary>Reads/writes <see cref="AppPreferences"/>, tolerating a missing or
/// corrupt file by returning defaults.</summary>
public sealed class SettingsService
{
    /// <summary>Allowed pull-action tokens (order = display order).</summary>
    public static readonly IReadOnlyList<string> PullActions = new[] { "merge", "rebase", "fetch" };

    /// <summary>
    ///  The three answers to "should the app do this by itself?": ask every time
    ///  (upstream's null), always, never. Ordered for display.
    /// </summary>
    public static readonly IReadOnlyList<string> AskAlwaysNever = new[] { "Ask", "Always", "Never" };

    /// <summary>Names of <c>ShorteningRecentRepoPathStrategy</c>, in display order.</summary>
    public static readonly IReadOnlyList<string> ShorteningStrategies =
        new[] { "None", "MostSignDir", "MiddleDots" };

    /// <summary>Names of <c>TruncatePathMethod</c> the port can honour; see the property.</summary>
    public static readonly IReadOnlyList<string> TruncateMethods =
        new[] { "None", "TrimStart", "FileNameOnly" };

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

    /// <summary>
    ///  Raised after a successful <see cref="Save"/>, so a view already on screen adopts
    ///  the new answer instead of showing the old one until the next start.
    ///
    ///  <para>Raised on WHATEVER thread saved — the Settings dialog saves off the UI
    ///  thread on purpose — so a subscriber that touches controls must marshal. Static
    ///  because the writer and the readers each build their own service instance; a
    ///  subscriber that outlives its window must unsubscribe, or it keeps that window
    ///  alive.</para>
    /// </summary>
    public static event Action? Changed;

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
            return;
        }

        // Outside the try: a subscriber that throws is a bug in the subscriber, not a
        // failed save, and swallowing it here would hide it forever.
        Changed?.Invoke();
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
        s.DiffVerticalRulerPosition = Math.Clamp(s.DiffVerticalRulerPosition, 0, 999);

        // Floored, not clamped to 0: a delay of zero would turn one flick of the wheel
        // into a jump through several files.
        s.DiffContinuousScrollDelay = Math.Clamp(s.DiffContinuousScrollDelay, 100, 10_000);

        // A limit of 0 would disable straightening through the back door, which is what
        // the diagonals flag is for; 1 is the smallest value that still means something.
        s.StraightenGraphSegmentsLimit = Math.Clamp(s.StraightenGraphSegmentsLimit, 1, 10_000);
        s.RecursiveSubmodules = Math.Clamp(s.RecursiveSubmodules, 0, 2);

        // Upstream's own floor is 1: a size of 0 would empty the list on every save.
        s.RecentRepositoriesHistorySize = Math.Clamp(s.RecentRepositoriesHistorySize, 1, 500);
        s.OutputHistoryDepth = Math.Clamp(s.OutputHistoryDepth, 1, 500);

        // Floored at half a second: below that the second keystroke of a two-letter
        // search would already be a new search.
        s.RevisionGridQuickSearchTimeout = Math.Clamp(s.RevisionGridQuickSearchTimeout, 500, 30_000);

        // 0 keeps its meaning of "unset"; anything else is held between sizes that can
        // still be read and still fit a dialog.
        s.MonospaceFontSize = s.MonospaceFontSize <= 0 ? 0 : Math.Clamp(s.MonospaceFontSize, 6, 40);
        s.UiFontSize = s.UiFontSize <= 0 ? 0 : Math.Clamp(s.UiFontSize, 6, 40);

        if (!ShorteningStrategies.Contains(s.ShorteningRecentRepoPathStrategy))
        {
            s.ShorteningRecentRepoPathStrategy = "None";
        }

        if (!TruncateMethods.Contains(s.TruncatePathMethod))
        {
            s.TruncatePathMethod = "None";
        }

        if (!AskAlwaysNever.Contains(s.AutoPopStashAfterCheckout))
        {
            s.AutoPopStashAfterCheckout = "Ask";
        }

        if (!AskAlwaysNever.Contains(s.AutoPopStashAfterPull))
        {
            s.AutoPopStashAfterPull = "Ask";
        }

        // A blank host would build the URL "https:///api/v3"; a host with a scheme or a
        // path in it would build a nonsense one. Only the bare name is kept.
        s.GitHubHost = s.GitHubHost?.Trim().TrimEnd('/') ?? string.Empty;
        int scheme = s.GitHubHost.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s.GitHubHost = s.GitHubHost[(scheme + 3)..];
        }

        int slash = s.GitHubHost.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            s.GitHubHost = s.GitHubHost[..slash];
        }

        if (s.GitHubHost.Length == 0)
        {
            s.GitHubHost = "github.com";
        }

        s.GitHubIssueCommitMessageCount = Math.Clamp(s.GitHubIssueCommitMessageCount, 1, 100);

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
