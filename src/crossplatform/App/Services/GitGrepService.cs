using GitCommands;
using GitExtUtils;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  What the user typed in the changed-file list's search box, together with the two
///  switches of upstream's <c>btnFindInFilesGitGrep</c> drop-down
///  (<c>tsmiFindUsingMatchCase</c>, <c>tsmiFindUsingWholeWord</c>).
///
///  <para>A record, not three parameters, because the whole thing is what travels to
///  the background thread: a search that is superseded while running must be
///  comparable to the one that replaced it, and its options must not be re-read from
///  UI state after the fact.</para>
/// </summary>
/// <param name="Text">The pattern, exactly as typed (git-grep's own regex dialect).</param>
/// <param name="MatchCase">Case-sensitive matching; upstream stores the INVERSE
///  (<c>AppSettings.GitGrepIgnoreCase</c>), and this side keeps the affirmative form
///  the menu item shows.</param>
/// <param name="WholeWord"><c>--word-regexp</c>.</param>
public sealed record GitGrepQuery(string Text, bool MatchCase, bool WholeWord)
{
    /// <summary>The "search nothing" query — an empty box produces no group at all.</summary>
    public static GitGrepQuery None { get; } = new(string.Empty, MatchCase: false, WholeWord: false);

    /// <summary>
    ///  Whether this query is worth running. Whitespace counts as a pattern for git
    ///  (it matches lines containing a space), but an all-blank box is much more
    ///  likely a leftover than an intent, and upstream treats it as inactive too
    ///  (<c>FindInCommitFilesGitGrepActive</c> is a plain emptiness test on the box,
    ///  which its watermark keeps blank).
    /// </summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
///  The port of upstream's "Find in commit files using git-grep": the file list of
///  <c>git grep --files-with-matches</c> over one revision, plus the matching lines
///  of a single file for the patch pane.
///
///  <para>Neither call parses git's output here. The file list goes through
///  <see cref="GitModule.GetGrepFilesStatus"/> and the per-file matches through
///  <see cref="GitModule.GetGrepFileAsync"/> — the very methods upstream's
///  <c>FileStatusDiffCalculator.GetGrepItemStatuses</c> and
///  <c>GitUIExtensions.ViewChangesAsync</c> use, so the NUL-separated
///  <c>&lt;rev&gt;:&lt;path&gt;</c> format and the argument order stay in one place
///  for both front-ends.</para>
///
///  <para>All calls block and are meant for a background thread.</para>
/// </summary>
public static class GitGrepService
{
    /// <summary>
    ///  What marks a changed-file section as a search result rather than a diff —
    ///  upstream's <c>FileStatusDiffCalculator._grepSummaryPrefix</c>, which its
    ///  <c>IsGrepItemStatuses</c> tests for exactly this way. Kept as the section's
    ///  caption prefix (and not as a flag on the row) because the port's
    ///  <c>DiffFileGroup</c> is the whole of what the list is told about a section,
    ///  and the caption is also what the user reads.
    /// </summary>
    public const string SummaryPrefix = "grep: ";

    // git-grep's switches for the two menu options are chosen by GitModule from
    // AppSettings, and GetGrepFilesStatus takes only the pattern — there is no
    // overload that accepts them. Rather than rebuild the argument list here (which
    // would fork the escaping and the artificial-revision handling that
    // GitModule.GetGrepFiles already gets right), the two settings are pushed into
    // AppSettings immediately before the call and applyAppSettings: true is passed.
    //
    // The SOURCE OF TRUTH stays view-prefs.json (see FindInFilesPrefs): AppSettings
    // is used as the transport into the core, never read back. That also means two
    // searches must not interleave their "set then call" pairs, hence the lock — the
    // UI restarts a search on every keystroke, and a superseded one is only cancelled
    // *inside* git, after its arguments were built.
    private static readonly object GrepSettingsLock = new();

    /// <summary>
    ///  Runs <c>git grep --files-with-matches</c> over <paramref name="commitHash"/>
    ///  and returns the matching files as list rows, or an empty list when the query
    ///  is inactive, the revision is not a real object id, or git found nothing.
    ///
    ///  <para>The rows are <see cref="DiffChangeKind.Modified"/> with no old name and
    ///  <c>IsTracked</c>, as upstream's own grep statuses are: a search hit is not a
    ///  change, so the list is told to draw them without a status glyph (see
    ///  <c>FileStatusListView</c>'s search section).</para>
    /// </summary>
    public static IReadOnlyList<DiffFileRow> Search(
        string repoPath, string commitHash, GitGrepQuery query, CancellationToken cancellationToken)
    {
        if (!query.IsActive || !ObjectId.TryParse(commitHash, out ObjectId commitId))
        {
            return [];
        }

        GitModule module = GitContext.CreateModule(repoPath);

        IReadOnlyList<GitItemStatus> found;
        lock (GrepSettingsLock)
        {
            ApplyQuerySettings(query);
            found = module.GetGrepFilesStatus(commitId, query.Text, applyAppSettings: true, cancellationToken);
        }

        List<DiffFileRow> rows = new(found.Count);
        foreach (GitItemStatus item in found)
        {
            rows.Add(new DiffFileRow(item.Name, OldName: null, DiffChangeKind.Modified, IsTracked: true));
        }

        return rows;
    }

    /// <summary>
    ///  The whole search section, ready for the list: <see cref="Search"/>'s rows
    ///  under the caption upstream builds
    ///  (<c>"grep: " + pattern + " " + &lt;described revision&gt;</c>). Returns
    ///  <see langword="null"/> when there is no section to show — an inactive query —
    ///  which is what the caller passes on to clear the section.
    ///
    ///  <para>A query that simply found nothing still returns a section, with a count
    ///  of zero: "no file contains it" is the answer to the search, and silently
    ///  removing the group would read as "the search did not run".</para>
    /// </summary>
    public static DiffFileGroup? SearchGroup(
        string repoPath, string commitHash, GitGrepQuery query, CancellationToken cancellationToken)
    {
        if (!query.IsActive)
        {
            return null;
        }

        IReadOnlyList<DiffFileRow> rows = Search(repoPath, commitHash, query, cancellationToken);

        // DescribeRevision is what the port's own diff captions use, so the search
        // section names its revision the same way the sections above it do.
        string described = DiffService.DescribeRevision(repoPath, commitHash);
        string caption = described.Length > 0
            ? $"{SummaryPrefix}{query.Text} {described}"
            : $"{SummaryPrefix}{query.Text}";

        return new DiffFileGroup(caption, rows);
    }

    /// <summary>
    ///  The matching lines of ONE file, as git prints them — what the patch pane shows
    ///  for a search hit, because a hit has no patch to show (upstream:
    ///  <c>GitUIExtensions.ViewChangesAsync</c> routes a status with a
    ///  <c>GrepString</c> to <c>GetGrepFileAsync</c> instead of to a diff).
    /// </summary>
    /// <param name="contextLines">Lines of context around each match; the caller
    ///  passes the diff toolbar's own context setting so the two panes agree.</param>
    public static async Task<string> GetMatchesAsync(
        string repoPath,
        string commitHash,
        string path,
        GitGrepQuery query,
        int contextLines,
        CancellationToken cancellationToken)
    {
        if (!query.IsActive || !ObjectId.TryParse(commitHash, out ObjectId commitId))
        {
            return string.Empty;
        }

        GitModule module = GitContext.CreateModule(repoPath);

        // Upstream's FileViewer.GetExtraGrepArguments, minus the toggles the port's
        // patch pane does not apply to a grep listing: "-h" (the file name is already
        // the row the user clicked) and the context width.
        ArgumentString extraArguments = new ArgumentBuilder
        {
            "-h",
            $"--context={contextLines}",
        };

        Task<ExecutionResult> run;
        lock (GrepSettingsLock)
        {
            ApplyQuerySettings(query);

            // useGitColoring: false — the port's editor renders plain text and would
            // print the ANSI escapes verbatim. With colouring off git adds "--column",
            // so each line reads "<line>:<column>:<text>", which is more useful here
            // than the colours would have been.
            run = module.GetGrepFileAsync(
                commitId,
                path,
                extraArguments,
                query.Text,
                useGitColoring: false,
                showFunctionName: true,
                commandConfiguration: GitCommandConfiguration.Default,
                encoding: GitModule.SystemEncoding,
                cancellationToken);
        }

        ExecutionResult result = await run.ConfigureAwait(false);

        // A failed run is reported as its own output rather than thrown: "git grep
        // found nothing in this file" and "git grep failed" both exit non-zero, and
        // an exception would replace the pane with an error banner for the former.
        return result.ExitedSuccessfully ? result.StandardOutput : result.StandardError;
    }

    // Pushes the query's two switches into the settings GitModule reads while it
    // builds the git-grep arguments. GitGrepUserArguments is blanked on purpose: the
    // port does not offer upstream's free-text "Options" item, and a value left in the
    // shared settings file by the Windows build would otherwise silently change the
    // meaning of every search made here.
    private static void ApplyQuerySettings(GitGrepQuery query)
    {
        try
        {
            AppSettings.GitGrepIgnoreCase.Value = !query.MatchCase;
            AppSettings.GitGrepMatchWholeWord.Value = query.WholeWord;
            AppSettings.GitGrepUserArguments.Value = string.Empty;
        }
        catch (Exception)
        {
            // The settings store is not essential to searching: without it git-grep
            // still runs, case-insensitively and without --word-regexp, which is the
            // default of both switches.
        }
    }
}
