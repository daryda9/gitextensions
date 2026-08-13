using GitCommands;
using GitExtUtils;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  How rerere came to be active (or not) for a repository.
/// </summary>
public enum RerereActivation
{
    /// <summary><c>rerere.enabled</c> is explicitly false: rerere is off no matter what is on disk.</summary>
    DisabledByConfig,

    /// <summary>Nothing turns it on: no <c>rerere.enabled</c>, no <c>rr-cache</c> directory.</summary>
    NotConfigured,

    /// <summary><c>rerere.enabled</c> is explicitly true.</summary>
    EnabledByConfig,

    /// <summary>
    ///  <c>rerere.enabled</c> is unset but <c>&lt;git-dir&gt;/rr-cache</c> exists, which git
    ///  treats as consent. This is the case the UI must not hide: rerere is recording and
    ///  replaying without a single line of configuration to explain why.
    /// </summary>
    EnabledByCacheDirectory,
}

/// <summary>
///  The tri-state configuration behind rerere, kept unflattened on purpose.
///
///  <para><b>Why <see cref="Enabled"/> is a <see cref="Nullable{T}"/>.</b> "unset" and "false"
///  are genuinely different answers here. With <c>rerere.enabled</c> unset git falls back to
///  "is there an <c>rr-cache</c> directory?", so a repository where someone once ran rerere
///  keeps replaying resolutions forever with no config to point at. With <c>rerere.enabled</c>
///  explicitly false the directory is ignored. Measured on git 2.43: an unset key with
///  <c>.git/rr-cache</c> present recorded a preimage; setting the key to false with the same
///  directory in place recorded nothing. Collapsing null into false would make the UI claim
///  "off" for a repository that is actively rewriting the user's conflicts.</para>
/// </summary>
/// <param name="Enabled"><c>rerere.enabled</c>: null when the key is not set anywhere.</param>
/// <param name="AutoUpdate"><c>rerere.autoupdate</c>: null when not set (git's default is false).</param>
/// <param name="CacheDirectoryExists">Whether <c>&lt;git-dir&gt;/rr-cache</c> exists.</param>
/// <param name="GitDirectory">
///  The resolved <b>per-worktree</b> git directory, or null when it could not be
///  determined. Never assume <c>repo/.git</c>: in a linked worktree it is
///  <c>main/.git/worktrees/&lt;name&gt;</c> and in a submodule <c>super/.git/modules/&lt;name&gt;</c>.
///  This is where <c>MERGE_RR</c> lives.
/// </param>
/// <param name="CommonGitDirectory">
///  The <b>common</b> git directory — the same as <paramref name="GitDirectory"/> everywhere
///  except in a linked worktree, where it is the main repository's <c>.git</c>. This is where
///  <c>rr-cache</c> lives, and the two must not be confused: measured on git 2.43 in a linked
///  worktree, <c>MERGE_RR</c> was written to <c>main/.git/worktrees/link/MERGE_RR</c> while the
///  resolution was read from and recorded into <c>main/.git/rr-cache</c>, with no
///  <c>rr-cache</c> under the worktree's own directory at all.
/// </param>
public sealed record RerereConfiguration(
    bool? Enabled,
    bool? AutoUpdate,
    bool CacheDirectoryExists,
    string? GitDirectory,
    string? CommonGitDirectory)
{
    /// <summary>Which of the four activation cases this repository is in.</summary>
    public RerereActivation Activation => Enabled switch
    {
        false => RerereActivation.DisabledByConfig,
        true => RerereActivation.EnabledByConfig,
        _ when CacheDirectoryExists => RerereActivation.EnabledByCacheDirectory,
        _ => RerereActivation.NotConfigured,
    };

    /// <summary>True when git will record and replay resolutions in this repository.</summary>
    public bool IsActive => Activation is RerereActivation.EnabledByConfig or RerereActivation.EnabledByCacheDirectory;

    /// <summary>
    ///  Whether a replayed resolution is staged automatically. Unset means false: git leaves
    ///  the path unmerged in the index even after writing the remembered content into the work
    ///  tree, so the user still has to <c>git add</c> it. That extra step is the only moment at
    ///  which a wrong remembered resolution can still be caught, which is why turning this on
    ///  deserves its own switch rather than riding along with <see cref="Enabled"/>.
    /// </summary>
    public bool AutoUpdateEffective => AutoUpdate ?? false;

    /// <summary>
    ///  The <c>rr-cache</c> path, or null when the git directory is unknown. Built from
    ///  <see cref="CommonGitDirectory"/>, because the cache is shared by every worktree of the
    ///  repository — a resolution recorded in a linked worktree is replayed in the main one.
    /// </summary>
    public string? CacheDirectory => CommonGitDirectory is null ? null : Path.Combine(CommonGitDirectory, "rr-cache");

    /// <summary>
    ///  True when this work tree is a linked worktree, i.e. the cache shown is shared with other
    ///  checkouts of the same repository. Worth saying out loud in the cache window: "forget"
    ///  there also un-remembers the resolution for every other worktree.
    /// </summary>
    public bool IsLinkedWorktree =>
        GitDirectory is not null
        && CommonGitDirectory is not null
        && !string.Equals(GitDirectory, CommonGitDirectory, StringComparison.Ordinal);
}

/// <summary>
///  One recorded conflict resolution in <c>&lt;git-dir&gt;/rr-cache</c>.
///
///  <para><b>A cache directory is not one resolution.</b> The directory name is the hash of the
///  <i>conflict shape</i>, and several different paths can produce the identical shape — three
///  files edited the same way land in one directory as <c>preimage</c>, <c>preimage.1</c>,
///  <c>preimage.2</c> with a matching postimage each. Those are <b>variants</b>, and each is an
///  independently forgettable resolution, so this record models a variant and not a directory.
///  Observed on git 2.43 with three identically-conflicting files.</para>
/// </summary>
/// <param name="ConflictId">
///  The variant's identity as git writes it in <c>MERGE_RR</c>: the hash for variant 0,
///  <c>&lt;hash&gt;.&lt;n&gt;</c> otherwise.
/// </param>
/// <param name="Hash">The conflict-shape hash, i.e. the cache directory's name.</param>
/// <param name="Variant">0 for the unsuffixed files, n for the <c>.n</c> suffix.</param>
/// <param name="HasPostimage">
///  True when a <b>completed</b> resolution is stored. A variant with only a preimage is a
///  conflict git has seen but that was never resolved to the end; it will not be replayed.
/// </param>
/// <param name="HasThisimage">
///  True while a merge is in flight: <c>thisimage</c> is the scratch copy of the conflict
///  currently being resolved, and it disappears once the session is over.
/// </param>
/// <param name="LastWriteTimeUtc">
///  Newest write among the variant's files — the age of the resolution, which is the one signal
///  that tells a resolution recorded on purpose last week from one recorded by accident today.
/// </param>
/// <param name="PreimageBytes">Size of the recorded conflict, for a rough "how big was this".</param>
/// <param name="PostimageBytes">Size of the recorded resolution, 0 when there is none.</param>
/// <param name="DirectoryPath">Absolute path of the cache directory holding this variant.</param>
public sealed record RerereCacheEntry(
    string ConflictId,
    string Hash,
    int Variant,
    bool HasPostimage,
    bool HasThisimage,
    DateTime LastWriteTimeUtc,
    long PreimageBytes,
    long PostimageBytes,
    string DirectoryPath)
{
    /// <summary>The hash abbreviated for display, mirroring how the port shortens object ids.</summary>
    public string ShortHash => Hash.Length >= 8 ? Hash[..8] : Hash;

    public override string ToString() => ConflictId;
}

/// <summary>
///  Which conflict-producing operation is stopped in this work tree right now.
///
///  <para><b>Why rerere cares.</b> A merge conflicts once; the other four are <i>stepwise</i> —
///  git stops, the user resolves, git goes on to the next commit and can hit the same conflict
///  again immediately. Measured on git 2.43 with <c>git cherry-pick master..topic</c> over two
///  commits whose conflicts have the identical shape: step one printed
///  <c>Risoluzione per 'a.txt' registrata</c> and step two, in the <b>same</b> run, printed
///  <c>Risolto conflitto in 'b.txt' usando la risoluzione precedente</c>. Telling that user that
///  the resolution "will be replayed next time" describes the wrong horizon.</para>
/// </summary>
public enum RerereOperation
{
    /// <summary>Nothing in flight, or nothing this can recognise.</summary>
    None,

    /// <summary>An ordinary merge: <c>MERGE_HEAD</c>.</summary>
    Merge,

    /// <summary>
    ///  A stopped rebase: <c>rebase-merge/</c> (merge backend, including <c>-i</c>), or
    ///  <c>rebase-apply/</c> without the <c>applying</c> marker (the <c>--apply</c> backend).
    /// </summary>
    Rebase,

    /// <summary>A stopped cherry-pick, single or a sequencer range: <c>CHERRY_PICK_HEAD</c>.</summary>
    CherryPick,

    /// <summary>A stopped revert, single or a sequencer range: <c>REVERT_HEAD</c>.</summary>
    Revert,

    /// <summary>
    ///  <c>git am</c> stopped on a patch: <c>rebase-apply/</c> <b>with</b> the <c>applying</c>
    ///  marker. Only <c>am -3</c> ever reaches rerere — measured, a plain <c>git am</c> that
    ///  fails leaves no unmerged index entry, no <c>MERGE_RR</c> and no preimage at all.
    /// </summary>
    ApplyMailbox,
}

/// <summary>Outcome of a rerere action, with git's own output for display.</summary>
public sealed record RerereActionResult(bool Success, string Message);

/// <summary>
///  Everything about rerere in a repository at one instant, so the view can fill itself from a
///  single background call instead of four.
/// </summary>
/// <param name="Configuration">Config state and the resolved git directory.</param>
/// <param name="RecordedPaths"><c>git rerere status</c> — paths with a preimage in this conflict.</param>
/// <param name="RemainingPaths"><c>git rerere remaining</c> — paths still to resolve by hand.</param>
/// <param name="ReplayedDiff"><c>git rerere diff</c> — the work rerere has already done, empty when none.</param>
/// <param name="ActiveConflicts">Conflict id → path for the merge in progress, from <c>MERGE_RR</c>.</param>
public sealed record RerereSnapshot(
    RerereConfiguration Configuration,
    IReadOnlyList<string> RecordedPaths,
    IReadOnlyList<string> RemainingPaths,
    string ReplayedDiff,
    IReadOnlyDictionary<string, string> ActiveConflicts);

/// <summary>
///  <c>git rerere</c> — <i>reuse recorded resolution</i> — exposed as a service.
///
///  <para><b>Why this deserves a UI at all.</b> rerere remembers how a conflict was resolved and
///  replays that resolution the next time the same conflict <i>shape</i> appears — the same two
///  sides against the same base, wherever they turn up. That is the difference between resolving
///  one conflict and resolving it thirty times. It has shipped with git for two decades, it is
///  off by default, and essentially no graphical client surfaces it — so the feature that most
///  reduces the cost of a hard rebase is the one nobody knows exists.</para>
///
///  <para><b>What "the same shape" excludes, because the folklore gets it wrong.</b> "Rebase a
///  branch whose three commits all rewrite the same line" is the example everyone gives, and it
///  is the one case where rerere does <b>nothing</b>: measured on git 2.43, after resolving step
///  one your resolution becomes the new <i>ours</i>, so step two is <c>RESOLVED</c> vs
///  <c>TOPIC-B</c> — a conflict git has never seen, new preimage, no replay, three times over.
///  The replay fires where the conflict genuinely recurs: several commits of the series hitting
///  the same hunk with the same content, one edit repeated across many files (verified: three
///  files with the identical clash, resolved once at step one and replayed at steps two and
///  three), or the same rebase run again after an abort. The UI must not promise more.</para>
///
///  <para><b>Rebase behaves like merge everywhere this service looks.</b> Measured mid-rebase on
///  git 2.43: <c>rev-parse --absolute-git-dir</c> still answers the ordinary git dir (the rebase
///  state lives in <c>rebase-merge/</c> below it); <c>MERGE_RR</c> exists and holds the same
///  <c>&lt;id&gt;\t&lt;path&gt;\0</c> records, rewritten at every stop; <c>status</c>,
///  <c>remaining</c> and <c>diff</c> answer exactly as in a merge, including going all-empty with
///  <c>MERGE_RR</c> truncated to zero bytes after a complete replay while the index is still
///  unmerged; <c>forget</c> behaves identically; and <c>rerere.autoupdate</c> really does stage
///  the replayed path (<c>ls-files -u</c> empty, path at <c>M </c>) — the rebase still stops on
///  that commit, only with nothing left unmerged to show. So no method here needs a rebase
///  branch; what needed correcting was the wording around them.</para>
///
///  <para><b>Why it is dangerous.</b> The replay is silent and unconditional. A resolution
///  recorded wrongly once is reapplied to every future occurrence of that conflict, and with
///  <c>rerere.autoupdate</c> on it is staged too — so a bad merge can be committed without the
///  conflict ever being shown again. The damage looks exactly like a clean merge. That is why
///  this service treats <see cref="Forget"/> and the cache listing as first-class rather than as
///  an afterthought: the cure for a bad recorded resolution is knowing it is in there and being
///  able to drop it, and neither is possible if rerere stays a black box.</para>
///
///  <para><b>Parsing rules.</b> Every command here is chosen for a locale-independent output.
///  git's console messages are translated — on the machine this was written on the merge said
///  "CONFLITTO (contenuto)" and "Risoluzione per 'f.txt' registrata" — so nothing is matched
///  against them. <c>rerere status</c>/<c>remaining</c> emit one raw path per line and, verified
///  under <c>core.quotePath=true</c>, do <b>not</b> apply path quoting; there is no <c>-z</c>
///  option (git 2.43 rejects it), so a path containing a newline is the one case this cannot
///  represent — accepted, as git itself offers no better channel.</para>
///
///  <para>All methods are synchronous and block; call them from <see cref="Task.Run"/>, never on
///  the UI thread. None of them throw.</para>
/// </summary>
public sealed class RerereService
{
    private const string EnabledKey = "rerere.enabled";
    private const string AutoUpdateKey = "rerere.autoupdate";

    /// <summary>
    ///  Reads <c>rerere.enabled</c> and <c>rerere.autoupdate</c> plus the presence of the cache
    ///  directory. <c>git config --get</c> is used (not <c>--type=bool</c>): the typed form exits
    ///  128 and prints a fatal error when the value is not a boolean, which would turn a typo in
    ///  the user's config into a failure of the whole panel instead of an unknown value.
    /// </summary>
    public RerereConfiguration GetConfiguration(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string? gitDir = GetGitDirectory(module);

        // The cache lives in the COMMON directory. Asking the per-worktree directory instead
        // reported "not configured" for a linked worktree in which git was demonstrably
        // replaying resolutions out of the main repository's rr-cache — the panel then hid the
        // banner, the cache button and the "rerere already did this" notice for a file rerere
        // had just rewritten.
        string? commonDir = GetCommonGitDirectory(module) ?? gitDir;
        bool cacheExists = commonDir is not null && Directory.Exists(Path.Combine(commonDir, "rr-cache"));

        return new RerereConfiguration(
            ParseBool(ReadConfig(module, EnabledKey)),
            ParseBool(ReadConfig(module, AutoUpdateKey)),
            cacheExists,
            gitDir,
            commonDir);
    }

    /// <summary>
    ///  Which stepwise operation is stopped here, read from the marker files git leaves in the
    ///  <b>per-worktree</b> git directory (each linked worktree has its own set, so a rebase in
    ///  one is invisible to the other — verified by the layout, and the discriminating case is
    ///  <c>MERGE_RR</c>, which is per-worktree too).
    ///
    ///  <para>Observed on git 2.43, one operation at a time and never two markers at once:
    ///  <c>git rebase</c> and <c>git rebase -i</c> leave <c>rebase-merge/</c>;
    ///  <c>git rebase --apply</c> leaves <c>rebase-apply/</c> <i>without</i> an <c>applying</c>
    ///  file; <c>git am</c> leaves <c>rebase-apply/</c> <i>with</i> one; a cherry-pick leaves
    ///  <c>CHERRY_PICK_HEAD</c> (plus <c>sequencer/</c> when it is a range) and a revert
    ///  <c>REVERT_HEAD</c>. A rebase notably does <b>not</b> set <c>CHERRY_PICK_HEAD</c>, so the
    ///  order below is a safety net rather than a necessity.</para>
    /// </summary>
    public RerereOperation GetOperation(string repoPath)
    {
        string? gitDir = GetGitDirectory(GitContext.CreateModule(repoPath));
        if (gitDir is null)
        {
            return RerereOperation.None;
        }

        try
        {
            if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")))
            {
                return RerereOperation.Rebase;
            }

            if (Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            {
                return File.Exists(Path.Combine(gitDir, "rebase-apply", "applying"))
                    ? RerereOperation.ApplyMailbox
                    : RerereOperation.Rebase;
            }

            if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            {
                return RerereOperation.CherryPick;
            }

            if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD")))
            {
                return RerereOperation.Revert;
            }

            return File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))
                ? RerereOperation.Merge
                : RerereOperation.None;
        }
        catch (IOException)
        {
            // An operation finishing underneath us: "nothing recognisable" is the honest answer,
            // and it only costs the caller the generic wording.
            return RerereOperation.None;
        }
        catch (UnauthorizedAccessException)
        {
            return RerereOperation.None;
        }
    }

    /// <summary>
    ///  Writes <c>rerere.enabled</c> into the repository's local config, or removes the key when
    ///  <paramref name="enabled"/> is null.
    ///
    ///  <para>Unsetting is not the same as writing false — see <see cref="RerereConfiguration"/>:
    ///  with an existing <c>rr-cache</c> directory an unset key still means <i>on</i>. Offer the
    ///  three states, or offer only true/false and always write one of them.</para>
    /// </summary>
    public RerereActionResult SetEnabled(string repoPath, bool? enabled)
        => WriteConfig(repoPath, EnabledKey, enabled);

    /// <summary>
    ///  Writes <c>rerere.autoupdate</c> (null removes the key). With it on, a replayed resolution
    ///  is staged for you — verified: after the replay <c>git ls-files -u</c> was empty and the
    ///  path showed as <c>M </c>. Convenient on a rebase, and the single point at which a wrong
    ///  remembered resolution would still have been visible, so the UI should say so.
    /// </summary>
    public RerereActionResult SetAutoUpdate(string repoPath, bool? autoUpdate)
        => WriteConfig(repoPath, AutoUpdateKey, autoUpdate);

    /// <summary>
    ///  <c>git rerere status</c> — the paths in the current conflict for which rerere holds a
    ///  preimage, i.e. the ones it is watching and will record once resolved.
    ///
    ///  <para>Do not read an empty list as "rerere did nothing". When a resolution is replayed in
    ///  full the path leaves this list immediately: measured, a replayed merge reported empty
    ///  <c>status</c>, empty <c>remaining</c> and empty <c>diff</c> while the index was still
    ///  unmerged. Empty means "rerere has nothing pending here", not "rerere is idle".</para>
    /// </summary>
    public IReadOnlyList<string> GetStatus(string repoPath) => RunPathList(repoPath, "status");

    /// <summary>
    ///  <c>git rerere remaining</c> — the paths rerere could <b>not</b> resolve, so the ones the
    ///  user actually has to open. This is the list the conflict view should drive, because it is
    ///  <see cref="GetStatus"/> minus the work rerere already did.
    /// </summary>
    public IReadOnlyList<string> GetRemaining(string repoPath) => RunPathList(repoPath, "remaining");

    /// <summary>
    ///  <c>git rerere diff</c> — a unified diff from the recorded conflict to the work tree as it
    ///  stands: concretely, the part of the merge the user does <b>not</b> have to redo.
    ///
    ///  <para>Two readings, both observed. On a fresh conflict it shows the preimage against the
    ///  markers currently in the file (only the branch labels differ), which is noise. After a
    ///  resolution has been applied it shows the markers collapsing into the resolved text —
    ///  which is the interesting one, and the reason to show this at all. Empty is normal and
    ///  means "nothing in flight"; the view should treat empty as "no diff to show" rather than
    ///  as an error.</para>
    /// </summary>
    public string GetReplayedDiff(string repoPath)
    {
        ExecutionResult result = Run(GitContext.CreateModule(repoPath), new GitArgumentBuilder("rerere") { "diff" });
        return result.ExitedSuccessfully ? result.StandardOutput : string.Empty;
    }

    /// <summary>
    ///  <c>git rerere forget &lt;path&gt;…</c> — drops the recorded resolution for those paths and
    ///  puts the conflict back the way it came out of the merge.
    ///
    ///  <para><b>The safety valve, and it needs a confirmation.</b> This is what undoes a wrongly
    ///  recorded resolution, and it can be destructive in the small: the user's edits to that file
    ///  may be replaced by the conflict markers again — but <i>not always</i>, so the caller must
    ///  check rather than announce it. Measured on git 2.43, forgetting a path that had been
    ///  replayed but not staged: the postimage was gone from the cache and the path was back in
    ///  <c>status</c>, <c>remaining</c> and <c>diff</c>, yet the work-tree file still held the
    ///  resolved text, with no markers anywhere in it. What <c>forget</c> guarantees is that the
    ///  conflict is armed again, not that the file on disk is rewritten.</para>
    ///
    ///  <para><b>Offer it only while a conflict is in flight</b> — a merge or a stopped rebase,
    ///  which behave the same here. git will run it outside one, but the
    ///  result does not stick, and the way it fails is nasty. <c>forget</c> restores the conflict
    ///  markers into the <i>work tree</i>; with a merge in progress that is exactly what the user
    ///  wants to see. With no merge in progress there is nothing to restore, the file keeps the
    ///  resolved text, and the next rerere invocation records that text straight back into the
    ///  cache. Measured: forgetting a path outside a merge removed its <c>postimage</c>, and one
    ///  unrelated <c>git rerere forget</c> later the identical <c>postimage</c> was back. So the
    ///  user presses "forget", sees a success, and the wrong resolution survives.</para>
    ///
    ///  <para>It also says nothing useful when the path has no recorded resolution:
    ///  <c>git rerere forget nosuch.txt</c> exits 0 in silence, and for a path whose current
    ///  conflict shape has only a preimage it prints <c>error: no remembered resolution for
    ///  'f.txt'</c> on stderr and <b>still exits 0</b> (measured mid-rebase). A true result is
    ///  therefore not proof that anything was forgotten — the caller should re-list the cache,
    ///  show the user what actually changed, and pass this message through rather than
    ///  translating a zero exit code into "done".</para>
    /// </summary>
    public RerereActionResult Forget(string repoPath, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return new RerereActionResult(false, "No path given to forget.");
        }

        GitArgumentBuilder args = new("rerere") { "forget", "--" };
        foreach (string path in paths)
        {
            args.Add(Quote(path));
        }

        ExecutionResult result = Run(GitContext.CreateModule(repoPath), args);
        return new RerereActionResult(result.ExitedSuccessfully, result.AllOutput.Trim());
    }

    /// <summary>Forgets a single path. See <see cref="Forget(string, IReadOnlyList{string})"/>.</summary>
    public RerereActionResult Forget(string repoPath, string path) => Forget(repoPath, [path]);

    /// <summary>
    ///  <c>git rerere gc</c> — expires stale cache entries by age
    ///  (<c>gc.rerereResolved</c>, 60 days, for resolutions that were used;
    ///  <c>gc.rerereUnresolved</c>, 15 days, for conflicts that were never resolved).
    ///
    ///  <para>Worth exposing because it is the only <i>non</i>-destructive way to shrink the cache:
    ///  it drops what git already considers expired and nothing else. It is not a way to get rid
    ///  of one bad resolution — that is <see cref="Forget"/>. Note it is a no-op for anything
    ///  recent, so a UI that promises "clean up" here will look broken; label it as "expire old
    ///  entries" and report the before/after count from <see cref="ListCache"/>.</para>
    /// </summary>
    public RerereActionResult Gc(string repoPath)
    {
        ExecutionResult result = Run(GitContext.CreateModule(repoPath), new GitArgumentBuilder("rerere") { "gc" });
        return new RerereActionResult(result.ExitedSuccessfully, result.AllOutput.Trim());
    }

    /// <summary>
    ///  Lists what is actually stored in <c>&lt;git-dir&gt;/rr-cache</c>, newest first.
    ///
    ///  <para>Read from disk, not from git: there is no porcelain that enumerates the cache, and
    ///  the layout (one directory per conflict hash holding <c>preimage</c>/<c>postimage</c>/
    ///  <c>thisimage</c>, optionally suffixed <c>.1</c>, <c>.2</c>… for variants) is stable and
    ///  documented. The point is to make the cache inspectable: a user who is about to be handed
    ///  the same resolution forever should be able to see that it is in there.</para>
    ///
    ///  <para>The directory comes from <c>git rev-parse --git-common-dir</c>, never from
    ///  <c>repoPath + "/.git"</c> and — this is the part that was wrong — never from
    ///  <c>--absolute-git-dir</c> either. In a linked worktree the two differ, and only the
    ///  common one has an <c>rr-cache</c>: measured, a merge resolved inside
    ///  <c>wt/link</c> wrote <c>main/.git/rr-cache/&lt;hash&gt;/postimage</c> while
    ///  <c>main/.git/worktrees/link/</c> held only <c>MERGE_RR</c>. In a submodule the two are
    ///  equal (<c>super/.git/modules/&lt;name&gt;</c>, verified mid-rebase), and the
    ///  superproject's own <c>.git</c> has no <c>rr-cache</c>, so the submodule's own cache is
    ///  what gets listed — which is what git uses there.</para>
    /// </summary>
    public IReadOnlyList<RerereCacheEntry> ListCache(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string? gitDir = GetCommonGitDirectory(module) ?? GetGitDirectory(module);
        if (gitDir is null)
        {
            return [];
        }

        string cacheDir = Path.Combine(gitDir, "rr-cache");
        if (!Directory.Exists(cacheDir))
        {
            return [];
        }

        List<RerereCacheEntry> entries = [];
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(cacheDir);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string dir in dirs)
        {
            string hash = Path.GetFileName(dir);

            // Variants share a directory, so the set of variant numbers has to be discovered
            // from the file names rather than assumed to be {0}.
            SortedSet<int> variants = [];
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                foreach (string stem in (string[])["preimage", "postimage", "thisimage"])
                {
                    if (name == stem)
                    {
                        variants.Add(0);
                    }
                    else if (name.StartsWith(stem + ".", StringComparison.Ordinal)
                             && int.TryParse(name[(stem.Length + 1)..], out int variant))
                    {
                        variants.Add(variant);
                    }
                }
            }

            foreach (int variant in variants)
            {
                string suffix = variant == 0 ? string.Empty : $".{variant}";
                FileInfo pre = new(Path.Combine(dir, $"preimage{suffix}"));
                FileInfo post = new(Path.Combine(dir, $"postimage{suffix}"));
                FileInfo self = new(Path.Combine(dir, $"thisimage{suffix}"));

                DateTime stamp = DateTime.MinValue;
                foreach (FileInfo info in (FileInfo[])[pre, post, self])
                {
                    if (info.Exists && info.LastWriteTimeUtc > stamp)
                    {
                        stamp = info.LastWriteTimeUtc;
                    }
                }

                entries.Add(new RerereCacheEntry(
                    ConflictId: hash + suffix,
                    Hash: hash,
                    Variant: variant,
                    HasPostimage: post.Exists,
                    HasThisimage: self.Exists,
                    LastWriteTimeUtc: stamp,
                    PreimageBytes: pre.Exists ? pre.Length : 0,
                    PostimageBytes: post.Exists ? post.Length : 0,
                    DirectoryPath: dir));
            }
        }

        entries.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        return entries;
    }

    /// <summary>
    ///  Reads <c>&lt;git-dir&gt;/MERGE_RR</c>, the map from conflict id to path for the merge that
    ///  is happening right now. Empty when no merge is in flight.
    ///
    ///  <para><b>Per-worktree, unlike the cache.</b> This one really does come from
    ///  <c>--absolute-git-dir</c>: in a linked worktree git wrote
    ///  <c>main/.git/worktrees/link/MERGE_RR</c>, so two worktrees of the same repository can be
    ///  stopped on two different conflicts while sharing one <c>rr-cache</c>, and reading the
    ///  common directory here would report the other checkout's conflict.</para>
    ///
    ///  <para>This is what makes <see cref="ListCache"/> readable: on its own a cache entry is a
    ///  40-character hash, but joined with this map the view can say "this stored resolution is
    ///  the one being applied to <c>src/Foo.cs</c>". The format is
    ///  <c>&lt;conflict-id&gt;\t&lt;path&gt;\0</c>, verified byte for byte, and it carries the
    ///  variant suffix, so three identically-conflicting files appeared as <c>&lt;hash&gt;</c>,
    ///  <c>&lt;hash&gt;.1</c> and <c>&lt;hash&gt;.2</c>. NUL-separated, so paths with spaces need
    ///  no decoding.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> GetActiveConflicts(string repoPath)
    {
        string? gitDir = GetGitDirectory(GitContext.CreateModule(repoPath));
        if (gitDir is null)
        {
            return new Dictionary<string, string>();
        }

        string mergeRr = Path.Combine(gitDir, "MERGE_RR");
        Dictionary<string, string> map = [];
        try
        {
            if (!File.Exists(mergeRr))
            {
                return map;
            }

            // Read as raw bytes and decode as UTF-8: git writes the path verbatim, and going
            // through File.ReadAllText with the ambient encoding would mangle non-ASCII names.
            string content = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(mergeRr));
            foreach (string record in content.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = record.IndexOf('\t');
                if (tab > 0 && tab < record.Length - 1)
                {
                    map[record[..tab]] = record[(tab + 1)..];
                }
            }
        }
        catch (IOException)
        {
            // A merge finishing underneath us removes the file; an empty map is the right answer.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return map;
    }

    /// <summary>
    ///  One background call that fills the whole panel: config, both path lists, the replayed
    ///  diff and the active conflict map. Grouped here so the view never fires five git processes
    ///  from five separate handlers and renders a half-consistent picture.
    /// </summary>
    public RerereSnapshot GetSnapshot(string repoPath)
    {
        RerereConfiguration configuration = GetConfiguration(repoPath);
        return new RerereSnapshot(
            configuration,
            GetStatus(repoPath),
            GetRemaining(repoPath),
            GetReplayedDiff(repoPath),
            GetActiveConflicts(repoPath));
    }

    /// <summary>
    ///  The absolute git directory. <c>--absolute-git-dir</c> rather than <c>--git-dir</c>: the
    ///  latter answers a bare relative <c>.git</c> when run from the work tree root, which only
    ///  resolves correctly by accident.
    /// </summary>
    private static string? GetGitDirectory(GitModule module)
    {
        ExecutionResult result = Run(module, new GitArgumentBuilder("rev-parse") { "--absolute-git-dir" });
        if (!result.ExitedSuccessfully)
        {
            return null;
        }

        string dir = result.StandardOutput.Trim();
        return dir.Length == 0 ? null : dir;
    }

    /// <summary>
    ///  The common git directory — the one holding <c>rr-cache</c>, which every worktree of the
    ///  repository shares.
    ///
    ///  <para><c>--path-format=absolute</c> is not optional: plain <c>--git-common-dir</c> answers
    ///  a path relative to the current directory (<c>../.git</c> when run one level down from the
    ///  work tree root, measured), which would silently point the cache listing at nothing. Null
    ///  when git is too old for the option (added in 2.31), and the caller then falls back to the
    ///  per-worktree directory: identical outside a linked worktree, and no worse than the
    ///  behaviour that shipped before.</para>
    /// </summary>
    private static string? GetCommonGitDirectory(GitModule module)
    {
        ExecutionResult result = Run(
            module,
            new GitArgumentBuilder("rev-parse") { "--path-format=absolute", "--git-common-dir" });
        if (!result.ExitedSuccessfully)
        {
            return null;
        }

        string dir = result.StandardOutput.Trim();
        return dir.Length == 0 ? null : dir;
    }

    private IReadOnlyList<string> RunPathList(string repoPath, string subcommand)
    {
        ExecutionResult result = Run(GitContext.CreateModule(repoPath), new GitArgumentBuilder("rerere") { subcommand });
        if (!result.ExitedSuccessfully)
        {
            return [];
        }

        return [.. result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)];
    }

    private static RerereActionResult WriteConfig(string repoPath, string key, bool? value)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = value is null
            ? new("config") { "--local", "--unset", key }
            : new("config") { "--local", key, value.Value ? "true" : "false" };

        ExecutionResult result = Run(module, args);

        // `--unset` of a key that was never there exits 5. Nothing is wrong: the requested end
        // state (key absent) already holds, and reporting a failure would only confuse.
        bool success = result.ExitedSuccessfully || (value is null && result.ExitCode == 5);
        return new RerereActionResult(success, result.AllOutput.Trim());
    }

    private static string? ReadConfig(GitModule module, string key)
    {
        ExecutionResult result = Run(module, new GitArgumentBuilder("config") { "--get", key });

        // Exit 1 is "key not set" and is the answer we want to keep distinguishable, so it maps
        // to null rather than to an empty string.
        return result.ExitedSuccessfully ? result.StandardOutput.Trim() : null;
    }

    /// <summary>
    ///  git's boolean vocabulary, which is wider than "true"/"false": <c>yes</c>/<c>no</c>,
    ///  <c>on</c>/<c>off</c>, <c>1</c>/<c>0</c>, and a key written with no value at all
    ///  (<c>[rerere]\n\tenabled</c>), which git reads as true. Anything else is a malformed value
    ///  and comes back as null — the same as unset, which is how git would behave once it
    ///  refused to parse it anyway.
    /// </summary>
    private static bool? ParseBool(string? value) => value?.ToLowerInvariant() switch
    {
        null => null,
        "" or "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null,
    };

    private static ExecutionResult Run(GitModule module, ArgumentString args)
        => module.GitExecutable.Execute(args, throwOnErrorExit: false);

    // GitArgumentBuilder re-splits its arguments on spaces, so any path must arrive quoted.
    private static string Quote(string path) => (path.ToPosixPath() ?? path).Quote();
}
