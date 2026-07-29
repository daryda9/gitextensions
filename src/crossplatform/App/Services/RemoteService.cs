using System.Diagnostics;
using System.Text;
using GitCommands;
using GitCommands.Config;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A configured git remote, projected for display in the Avalonia remote panel.
/// </summary>
public sealed record RemoteRow(string Name, string FetchUrl, string PushUrl)
{
    /// <summary>
    ///  The raw <c>remote.&lt;name&gt;.pushurl</c> config value, empty when the key is
    ///  absent. Distinct from <see cref="PushUrl"/> on purpose: that one falls back to
    ///  the fetch URL (which is what a push actually uses when no separate push URL is
    ///  configured), so it cannot tell "no pushurl key" from "pushurl equal to url".
    ///  The Remotes dialog needs that distinction to decide whether the
    ///  "Use separate push URL" box starts ticked — upstream reads the key the same
    ///  way, taking the LAST value when several exist
    ///  (<c>ConfigFileRemoteSettingsManager.LoadRemotes</c>, and the note in
    ///  <c>ConfigFileRemote.cs:40-43</c>).
    /// </summary>
    public string ConfiguredPushUrl { get; init; } = string.Empty;

    public string Display => string.IsNullOrEmpty(FetchUrl) ? Name : $"{Name}  ({FetchUrl})";

    public override string ToString() => Display;
}

/// <summary>
///  One local branch as shown on the Remotes dialog's "Default pull behavior
///  (fetch &amp; merge)" tab: which remote it pulls from and which remote branch it
///  merges with. The pair maps onto <c>branch.&lt;name&gt;.remote</c> and
///  <c>branch.&lt;name&gt;.merge</c>.
///
///  <para><see cref="MergeWith"/> is the SHORT name (<c>main</c>), matching what
///  upstream's grid displays; git stores it fully qualified
///  (<c>refs/heads/main</c>) and <see cref="GitRef.MergeWith"/> does the
///  stripping/qualifying on both sides.</para>
/// </summary>
public sealed record BranchTrackingRow(string Name, string TrackingRemote, string MergeWith)
{
    public override string ToString() => Name;
}

/// <summary>
///  Username / password pair collected from the credentials dialog and used to
///  retry a single remote operation that failed authentication.
/// </summary>
public sealed record GitCredentials(string Username, string Password);

/// <summary>
///  Outcome of a remote operation (fetch / pull / push). <see cref="AuthFailed"/>
///  is set when the git output looks like an authentication failure, signalling
///  the UI to prompt for credentials and retry once.
/// </summary>
public sealed record RemoteOpResult(bool Success, string Output, bool AuthFailed);

/// <summary>
///  Outcome of saving a remote's separate push URL. <see cref="SeparatePushUrlKept"/>
///  is the state the "Use separate push URL" box should end up in: upstream's
///  <c>SaveClick</c> mutates the check box itself when the value turns out to be empty
///  or redundant (<c>FormRemotes.cs:509-513</c>), so the caller has to follow.
/// </summary>
public sealed record PushUrlSaveResult(RemoteOpResult Result, bool SeparatePushUrlKept);

/// <summary>
///  What a fetch/pull does with tags, mirroring the three radio buttons of the
///  original <c>FormPull</c> "Tag options" group. The values map onto the
///  <c>fetchTags</c> tri-state the core <see cref="GitModule.FetchCmd"/> /
///  <see cref="GitModule.PullCmd"/> builders take:
///  <list type="bullet">
///   <item><see cref="Default"/> → <c>null</c>: no tag switch at all, so git
///    follows the remote's <c>tagopt</c> (and otherwise fetches the tags that are
///    reachable from the fetched heads).</item>
///   <item><see cref="None"/> → <c>false</c>: <c>--no-tags</c>.</item>
///   <item><see cref="All"/> → <c>true</c>: <c>--tags</c>.</item>
///  </list>
/// </summary>
public enum PullTagPolicy
{
    /// <summary>Follow <c>tagopt</c> (no explicit tag switch) — upstream's default.</summary>
    Default,

    /// <summary><c>--no-tags</c>.</summary>
    None,

    /// <summary><c>--tags</c>.</summary>
    All,
}

/// <summary>
///  Everything the Pull dialog (and the toolbar's split button) can ask for in a
///  single pull/fetch, replacing the old lone <c>rebase</c> boolean. Defaults
///  reproduce upstream's defaults: merge into the current branch, follow
///  <c>tagopt</c>, no prune, no autostash.
///
///  <para><see cref="Remote"/> is the *source*: a configured remote name, an
///  arbitrary URL (set <see cref="RemoteIsUrl"/> so the credential retry still
///  recognises an http(s) target), or <see cref="AllRemotes"/> for the
///  <c>Fetch all</c> / <c>Fetch and prune all</c> actions.</para>
///
///  <para>Prune only has meaning for a fetch: upstream disables both prune boxes
///  as soon as merge or rebase is selected, and the command builder does the same,
///  so a merge/rebase pull never silently prunes.</para>
/// </summary>
/// <param name="Action">
///  Merge / rebase / fetch-only / fetch-all / fetch-and-prune-all. The upstream
///  enum is reused verbatim (<see cref="GitPullAction"/>);
///  <see cref="GitPullAction.None"/> and <see cref="GitPullAction.Default"/> are
///  treated as <see cref="GitPullAction.Merge"/>, matching <c>FormPull</c>.
/// </param>
public sealed record PullOptions(
    GitPullAction Action = GitPullAction.Merge,
    string Remote = "",
    bool RemoteIsUrl = false,
    string RemoteBranch = "",
    string LocalBranch = "",
    PullTagPolicy Tags = PullTagPolicy.Default,
    bool Prune = false,
    bool PruneTags = false,
    bool AutoStash = false,
    bool Unshallow = false)
{
    /// <summary>
    ///  The pseudo-remote that means "every configured remote" (<c>git fetch --all</c>).
    ///  Upstream shows it in the remotes combo as <c>[ All ]</c> and translates it to
    ///  <c>--all</c> when building the command; the port skips the display alias and
    ///  stores the switch directly.
    /// </summary>
    public const string AllRemotes = "--all";

    /// <summary>True when this is a fetch-only action (no merge, no rebase).</summary>
    public bool IsFetchOnly
        => Action is GitPullAction.Fetch or GitPullAction.FetchAll or GitPullAction.FetchPruneAll;

    /// <summary>True when the action rebases instead of merging.</summary>
    public bool IsRebase => Action == GitPullAction.Rebase;

    /// <summary>
    ///  The effective source string handed to git: <c>--all</c> for the two
    ///  "all remotes" actions, otherwise the remote name / URL as given.
    /// </summary>
    public string EffectiveRemote
        => Action is GitPullAction.FetchAll or GitPullAction.FetchPruneAll
            ? AllRemotes
            : Remote ?? string.Empty;

    /// <summary>The tri-state the core command builders expect (see <see cref="PullTagPolicy"/>).</summary>
    public bool? FetchTags => Tags switch
    {
        PullTagPolicy.None => false,
        PullTagPolicy.All => true,
        _ => null,
    };
}

/// <summary>
///  Remote operations (list remotes, fetch, pull, push) implemented by reusing the
///  Git Extensions core (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>.
///  The fetch/pull command builders are methods on the module; push uses the core
///  <see cref="Commands.Push"/> argument builder. All methods are synchronous and
///  are meant to be called off the UI thread.
///
///  Credentials: on Linux git normally resolves credentials through a configured
///  credential helper, so most operations succeed without a prompt. When an
///  operation fails authentication the caller can retry passing
///  <see cref="GitCredentials"/>; for http/https remotes the credentials are fed to
///  git through a transient, in-memory credential helper installed with per-command
///  <c>-c credential.helper=…</c> config (nothing is written to git config, and the
///  secret is never placed in the remote URL). The helper reads the username /
///  password from environment variables that are set only for the duration of the
///  single command and cleared immediately afterwards, so the secret never appears
///  in the (logged) git command line, the reflog, or the visible process arguments.
///  ssh remotes stay key-based and are left untouched — see the panel's notes.
/// </summary>
public sealed class RemoteService
{
    /// <summary>
    ///  Runs an async git call from a synchronous service method without ever
    ///  deadlocking the caller. Awaiting directly with <c>GetAwaiter().GetResult()</c>
    ///  hangs forever when the caller sits on the Avalonia UI thread: the
    ///  continuation is posted back to that same (now blocked) thread. Hopping to
    ///  the thread pool first detaches the continuation from the UI
    ///  SynchronizationContext. Callers should still stay off the UI thread — this
    ///  only turns a hard hang into a short block.
    /// </summary>
    private static T RunDetached<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>
    ///  Lists the configured remotes (name + fetch/push URLs).
    /// </summary>
    public IReadOnlyList<RemoteRow> ListRemotes(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        IReadOnlyList<Remote> remotes = RunDetached(module.GetRemotesAsync);
        return [.. remotes.Select(r => new RemoteRow(
            r.Name,
            r.FetchUrl ?? string.Empty,
            r.PushUrls.Count > 0 ? r.PushUrls[0] : r.FetchUrl ?? string.Empty)
        {
            ConfiguredPushUrl = ReadPushUrlSetting(module, r.Name),
        })];
    }

    // The raw remote.<name>.pushurl, last value wins — see RemoteRow.ConfiguredPushUrl.
    private static string ReadPushUrlSetting(GitModule module, string remote)
        => module.GetSettings(string.Format(SettingKeyString.RemotePushUrl, remote)).LastOrDefault() ?? string.Empty;

    /// <summary>
    ///  Writes (or removes) a remote's SEPARATE push URL
    ///  (<c>remote.&lt;name&gt;.pushurl</c>), with upstream's exact semantics.
    ///
    ///  <para>Upstream never stores an empty value: <c>UpdateSettings</c>
    ///  (<c>ConfigFileRemoteSettingsManager.cs:454-467</c>) writes the key with
    ///  <c>SetSetting</c> when the value is non-blank and REMOVES it with
    ///  <c>UnsetSetting</c> otherwise. On top of that <c>FormRemotes.SaveClick</c>
    ///  (<c>FormRemotes.cs:509-513</c>) unticks the check box — hence also drops the
    ///  key — in two more cases:</para>
    ///  <list type="bullet">
    ///   <item>the box is ticked but the text is empty;</item>
    ///   <item>the push URL equals the fetch URL, compared case-INSENSITIVELY (a
    ///    separate push URL identical to the fetch URL is redundant).</item>
    ///  </list>
    ///
    ///  <para>Both normalisations happen here so the caller cannot get them wrong,
    ///  and the effective decision is reported back through
    ///  <see cref="PushUrlSaveResult.SeparatePushUrlKept"/> so the dialog can retick /
    ///  untick its box the way upstream's form does.</para>
    /// </summary>
    /// <param name="useSeparatePushUrl">State of the "Use separate push URL" box.</param>
    /// <param name="pushUrl">Contents of the push URL box (trimmed here).</param>
    /// <param name="fetchUrl">The remote's fetch URL, for the redundancy check.</param>
    public PushUrlSaveResult SetSeparatePushUrl(string repoPath, string remote, bool useSeparatePushUrl, string pushUrl, string fetchUrl)
    {
        string name = remote?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return new PushUrlSaveResult(new RemoteOpResult(false, "Remote name cannot be empty.", AuthFailed: false), false);
        }

        string target = pushUrl?.Trim() ?? string.Empty;
        string url = fetchUrl?.Trim() ?? string.Empty;

        // FormRemotes.cs:509-513, verbatim.
        bool keep = useSeparatePushUrl;
        if ((target.Length == 0 && keep)
            || (target.Length > 0 && target.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            keep = false;
        }

        // FormRemotes.cs:526 — the manager receives the URL only when the box survived.
        string? value = keep ? target : null;

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string key = string.Format(SettingKeyString.RemotePushUrl, name);

            // UpdateSettings: blank => UnsetSetting (git config --unset), else SetSetting.
            if (string.IsNullOrWhiteSpace(value))
            {
                module.UnsetSetting(key);
            }
            else
            {
                module.SetSetting(key, value);
            }

            return new PushUrlSaveResult(new RemoteOpResult(true, string.Empty, AuthFailed: false), keep);
        }
        catch (Exception ex)
        {
            return new PushUrlSaveResult(
                new RemoteOpResult(false, ex.GetBaseException().Message, AuthFailed: false),
                keep);
        }
    }

    /// <summary>
    ///  Lists the LOCAL branches with their default-pull configuration, ordered by
    ///  name — the data behind upstream's "Default pull behavior" grid
    ///  (<c>FormRemotes.cs:340-351</c>, which uses <c>GetRefs(RefsFilter.Heads)</c>
    ///  ordered by name).
    /// </summary>
    public IReadOnlyList<BranchTrackingRow> ListBranchTracking(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // The IGitRef getters read git config themselves (GitRef.cs:130-168), so this
        // is always the on-disk truth rather than a cached projection.
        return
        [
            .. module.GetRefs(RefsFilter.Heads)
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new BranchTrackingRow(r.Name, r.TrackingRemote, r.MergeWith)),
        ];
    }

    /// <summary>
    ///  Sets (or clears) a local branch's default pull configuration, delegating to
    ///  the CORE <see cref="IGitRef.TrackingRemote"/> / <see cref="IGitRef.MergeWith"/>
    ///  setters rather than writing the keys by hand, so the port inherits upstream's
    ///  behaviour exactly (<c>GitRef.cs:130-168</c>):
    ///  <list type="bullet">
    ///   <item>an empty remote UNSETS <c>branch.&lt;x&gt;.remote</c>;</item>
    ///   <item>a non-empty remote sets it and, when <c>branch.&lt;x&gt;.merge</c> is
    ///    still empty, AUTO-SEEDS the merge ref to the branch's own name — this is why
    ///    picking a remote in upstream's combo silently fills the "Default merge with"
    ///    column;</item>
    ///   <item>an empty merge-with UNSETS <c>branch.&lt;x&gt;.merge</c>, a non-empty one
    ///    is stored FULLY QUALIFIED (<c>refs/heads/&lt;name&gt;</c>) while the UI shows
    ///    the short name.</item>
    ///  </list>
    ///
    ///  <para><b>Only fields the user actually CHANGED are written, and the remote goes
    ///  first.</b> This is not an optimisation, it is required for correctness. Upstream
    ///  writes each field from its own <c>Validated</c> handler, so leaving the
    ///  "Default merge with" box untouched never calls the <c>MergeWith</c> setter at
    ///  all and the auto-seed above survives. Assigning both fields unconditionally
    ///  instead — as this method first did — sets <c>branch.&lt;x&gt;.remote</c> (which
    ///  seeds <c>.merge</c>) and then immediately UNSETS <c>.merge</c> again with the
    ///  still-empty box value, so picking a remote produced a half-configured branch
    ///  that git cannot pull from. Verified against a real repo: the seeded
    ///  <c>branch.main.merge</c> was missing until this comparison was added.</para>
    /// </summary>
    public RemoteOpResult SetBranchPullConfiguration(string repoPath, string branch, string trackingRemote, string mergeWith)
    {
        string name = branch?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return new RemoteOpResult(false, "Branch name cannot be empty.", AuthFailed: false);
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            IGitRef? head = module.GetRefs(RefsFilter.Heads)
                .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

            if (head is null)
            {
                return new RemoteOpResult(false, $"No local branch named '{name}'.", AuthFailed: false);
            }

            string wantRemote = trackingRemote?.Trim() ?? string.Empty;
            string wantMerge = mergeWith?.Trim() ?? string.Empty;

            // Snapshot BEFORE writing anything: the getters read git config, so reading
            // them after the remote write would already show the auto-seeded merge ref
            // and the comparison below would be meaningless.
            string hadRemote = head.TrackingRemote ?? string.Empty;
            string hadMerge = head.MergeWith ?? string.Empty;

            if (!string.Equals(wantRemote, hadRemote, StringComparison.Ordinal))
            {
                head.TrackingRemote = wantRemote;
            }

            if (!string.Equals(wantMerge, hadMerge, StringComparison.Ordinal))
            {
                head.MergeWith = wantMerge;
            }

            return new RemoteOpResult(true, string.Empty, AuthFailed: false);
        }
        catch (Exception ex)
        {
            return new RemoteOpResult(false, ex.GetBaseException().Message, AuthFailed: false);
        }
    }

    /// <summary>
    ///  The candidate "Default merge with" values for <paramref name="remote"/>: the
    ///  short names of that remote's remote-tracking branches. Mirrors upstream's
    ///  <c>DefaultMergeWithComboDropDown</c> (<c>FormRemotes.cs:669-700</c>), including
    ///  its guard that an unconfigured remote (no <c>remote.&lt;name&gt;.url</c>)
    ///  contributes nothing.
    ///
    ///  <para>One deliberate deviation: upstream selects the refs whose <c>Name</c>
    ///  case-insensitively CONTAINS the remote name, which also matches a remote called
    ///  <c>origin2</c> when <c>origin</c> is selected. Here the ref's own
    ///  <see cref="IGitRef.Remote"/> is compared for equality — the evident intent,
    ///  without the substring bug.</para>
    /// </summary>
    public IReadOnlyList<string> ListMergeWithCandidates(string repoPath, string remote)
    {
        string name = remote?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return [];
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            if (string.IsNullOrEmpty(module.GetSetting(string.Format(SettingKeyString.RemoteUrl, name))))
            {
                return [];
            }

            return
            [
                .. module.GetRefs(RefsFilter.Remotes)
                    .Where(r => string.Equals(r.Remote, name, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.LocalName)
                    .Where(n => !string.IsNullOrEmpty(n) && n != "HEAD")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal),
            ];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///  Adds a new remote: <c>git remote add &lt;name&gt; &lt;url&gt;</c>.
    /// </summary>
    public RemoteOpResult AddRemote(string repoPath, string name, string url)
    {
        string remote = name?.Trim() ?? string.Empty;
        string target = url?.Trim() ?? string.Empty;
        if (remote.Length == 0 || target.Length == 0)
        {
            return new RemoteOpResult(false, "Remote name and URL are required.", AuthFailed: false);
        }

        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("remote") { "add", remote, target };
        return Run(module, args);
    }

    /// <summary>
    ///  Renames a remote: <c>git remote rename &lt;old&gt; &lt;new&gt;</c>.
    /// </summary>
    public RemoteOpResult RenameRemote(string repoPath, string oldName, string newName)
    {
        string source = oldName?.Trim() ?? string.Empty;
        string target = newName?.Trim() ?? string.Empty;
        if (source.Length == 0 || target.Length == 0)
        {
            return new RemoteOpResult(false, "Remote name cannot be empty.", AuthFailed: false);
        }

        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("remote") { "rename", source, target };
        return Run(module, args);
    }

    /// <summary>
    ///  Removes a remote: <c>git remote remove &lt;name&gt;</c>.
    /// </summary>
    public RemoteOpResult RemoveRemote(string repoPath, string name)
    {
        string remote = name?.Trim() ?? string.Empty;
        if (remote.Length == 0)
        {
            return new RemoteOpResult(false, "Remote name cannot be empty.", AuthFailed: false);
        }

        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("remote") { "remove", remote };
        return Run(module, args);
    }

    /// <summary>
    ///  Changes a remote's fetch URL: <c>git remote set-url &lt;name&gt; &lt;url&gt;</c>.
    /// </summary>
    public RemoteOpResult SetRemoteUrl(string repoPath, string name, string url)
    {
        string remote = name?.Trim() ?? string.Empty;
        string target = url?.Trim() ?? string.Empty;
        if (remote.Length == 0 || target.Length == 0)
        {
            return new RemoteOpResult(false, "Remote name and URL are required.", AuthFailed: false);
        }

        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("remote") { "set-url", remote, target };
        return Run(module, args);
    }

    /// <summary>
    ///  Returns the currently checked-out branch name (empty when detached).
    /// </summary>
    public string GetCurrentBranch(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        return module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
    }

    /// <summary>
    ///  Fetches from <paramref name="remote"/> (all its refs).
    /// </summary>
    public RemoteOpResult Fetch(string repoPath, string remote, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = module.FetchCmd(remote, remoteBranch: null, localBranch: null);
        return Run(module, remote, args, credentials, forPush: false);
    }

    /// <summary>
    ///  Pulls from <paramref name="remote"/>, optionally rebasing local commits.
    /// </summary>
    public RemoteOpResult Pull(string repoPath, string remote, bool rebase, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = module.PullCmd(remote, remoteBranch: null, rebase: rebase);
        return Run(module, remote, args, credentials, forPush: false);
    }

    /// <summary>
    ///  Pushes <paramref name="branch"/> to <paramref name="remote"/>. When
    ///  <paramref name="force"/> is set, a <em>safe</em> force push
    ///  (<c>--force-with-lease</c>) is used: the push is rejected if the remote ref
    ///  advanced since we last fetched it, so it cannot silently clobber someone
    ///  else's work the way a plain <c>--force</c> would.
    /// </summary>
    public RemoteOpResult Push(string repoPath, string remote, string branch, bool force, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        if (string.IsNullOrEmpty(branch))
        {
            branch = module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(branch))
        {
            return new RemoteOpResult(false, "No branch to push (detached HEAD?).", AuthFailed: false);
        }

        ArgumentString args = Commands.Push(
            remote: remote,
            fromBranch: branch,
            toBranch: branch,
            force: force ? ForcePushOptions.ForceWithLease : ForcePushOptions.DoNotForce,
            track: true,
            recursiveSubmodules: 0);

        return Run(module, remote, args, credentials, forPush: true);
    }

    /// <summary>
    ///  Streaming variant of <see cref="Fetch"/>: emits every git output line
    ///  (stdout AND stderr, including transfer progress) through
    ///  <paramref name="onOutput"/> as it is produced, and also accumulates the full
    ///  text into the returned <see cref="RemoteOpResult.Output"/>.
    /// </summary>
    public RemoteOpResult FetchStreaming(string repoPath, string remote, Action<string> onOutput, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = module.FetchCmd(remote, remoteBranch: null, localBranch: null);
        return RunStreaming(module, remote, args, onOutput, credentials, forPush: false);
    }

    /// <summary>
    ///  Streaming variant of <see cref="Pull"/> — see <see cref="FetchStreaming"/>.
    ///
    ///  <para>Kept for the existing call sites: it is the two-state (merge / rebase)
    ///  shorthand for the <see cref="PullOptions"/> overload below, and reproduces
    ///  the historical behaviour exactly — including the <c>--no-tags</c> the core
    ///  builder's default <c>fetchTags: false</c> produces. New callers should use
    ///  the <see cref="PullOptions"/> overload, whose tag default is upstream's
    ///  "follow tagopt".</para>
    /// </summary>
    public RemoteOpResult PullStreaming(string repoPath, string remote, bool rebase, Action<string> onOutput, GitCredentials? credentials = null)
        => PullStreaming(
            repoPath,
            new PullOptions(
                Action: rebase ? GitPullAction.Rebase : GitPullAction.Merge,
                Remote: remote,
                Tags: PullTagPolicy.None),
            onOutput,
            credentials);

    /// <summary>
    ///  Streaming pull/fetch with the full option set of the original
    ///  <c>FormPull</c>: merge / rebase / fetch-only (plus the two "all remotes"
    ///  fetch actions), tag policy, prune, autostash, a remote name OR a URL, and
    ///  an explicit remote (and, for a fetch, local) branch.
    /// </summary>
    public RemoteOpResult PullStreaming(string repoPath, PullOptions options, Action<string> onOutput, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = BuildPullArguments(module, options);
        return RunStreaming(module, options.EffectiveRemote, args, onOutput, credentials, forPush: false, remoteIsUrl: options.RemoteIsUrl);
    }

    /// <summary>
    ///  Non-streaming counterpart of <see cref="PullStreaming(string, PullOptions, Action{string}, GitCredentials?)"/>.
    /// </summary>
    public RemoteOpResult Pull(string repoPath, PullOptions options, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = BuildPullArguments(module, options);
        return Run(module, options.EffectiveRemote, args, credentials, forPush: false, remoteIsUrl: options.RemoteIsUrl);
    }

    /// <summary>
    ///  <c>Fetch all</c>: fetches from every configured remote
    ///  (<c>git fetch --progress --all</c>), the command upstream's pull menu entry
    ///  of the same name produces (<see cref="GitPullAction.FetchAll"/> selects
    ///  fetch-only and puts <c>[ All ]</c> in the remotes combo, which the command
    ///  builder turns into <c>--all</c>).
    /// </summary>
    public RemoteOpResult FetchAllStreaming(string repoPath, Action<string> onOutput, GitCredentials? credentials = null)
        => PullStreaming(repoPath, new PullOptions(Action: GitPullAction.FetchAll), onOutput, credentials);

    /// <summary>
    ///  <c>Fetch and prune all</c>: <c>git fetch --progress --all --prune --force</c>.
    ///  Note that upstream deliberately does <em>not</em> add <c>--prune-tags</c>
    ///  here (<c>FormPull</c> sets <c>Prune = true</c> and <c>PruneTags = false</c>
    ///  for <see cref="GitPullAction.FetchPruneAll"/>), so local tags are left alone
    ///  and only stale remote-tracking branches are removed.
    /// </summary>
    public RemoteOpResult FetchAndPruneAllStreaming(string repoPath, Action<string> onOutput, GitCredentials? credentials = null)
        => PullStreaming(repoPath, new PullOptions(Action: GitPullAction.FetchPruneAll, Prune: true), onOutput, credentials);

    // Builds the git command for a pull/fetch described by PullOptions, reusing the
    // core argument builders (FetchCmd / PullCmd) so branch refspecs, tag switches,
    // prune and the fetch-parallel git options are formed exactly as upstream forms
    // them.
    //
    // Two things the core builders cannot express are handled here:
    //  * prune is passed only for a fetch — FormPull disables both prune boxes as
    //    soon as merge or rebase is selected, and PullCmd has no prune parameter;
    //  * --autostash, which PullCmd does not take, is injected right after the
    //    "pull" subcommand token (see InsertOption). Upstream instead runs an
    //    explicit `git stash save` before pulling; the git-native switch is
    //    equivalent, atomic (the stash is re-applied even when the pull fails) and
    //    needs no separate process. It is meaningless for a fetch, and skipped there.
    private static ArgumentString BuildPullArguments(GitModule module, PullOptions options)
    {
        string remote = options.EffectiveRemote;
        string? remoteBranch = string.IsNullOrWhiteSpace(options.RemoteBranch) ? null : options.RemoteBranch;
        string? localBranch = string.IsNullOrWhiteSpace(options.LocalBranch) ? null : options.LocalBranch;

        if (options.IsFetchOnly)
        {
            // --prune-tags implies --prune for git, and upstream keeps the two boxes
            // in step the same way, so a lone "prune tags" still prunes branches.
            bool prune = options.Prune || options.PruneTags;
            return module.FetchCmd(
                remote,
                remoteBranch,
                localBranch,
                options.FetchTags,
                options.Unshallow,
                prune,
                options.PruneTags);
        }

        ArgumentString pull = module.PullCmd(
            remote,
            remoteBranch,
            options.IsRebase,
            options.FetchTags,
            options.Unshallow);

        string arguments = pull.Arguments ?? string.Empty;

        // MERGE MUST SAY SO. The core builder emits `--rebase` for a rebase but
        // nothing at all for a merge, leaving the choice to `pull.rebase`. Since git
        // 2.27 that is fatal exactly when it matters: with divergent branches and
        // `pull.rebase` unset, git refuses with "You have divergent branches and need
        // to specify how to reconcile them" — and divergent branches are the whole
        // reason one pulls here (it is what the push-rejected recovery offers).
        // Passing --no-rebase also makes the menu honest for a user whose config says
        // `pull.rebase = true`: picking "merge" then used to silently rebase.
        if (!options.IsRebase)
        {
            arguments = InsertOption(arguments, "pull", "--no-rebase");
        }

        return options.AutoStash
            ? InsertOption(arguments, "pull", "--autostash")
            : arguments;
    }

    // Inserts <paramref name="option"/> immediately after the <paramref name="subcommand"/>
    // token of a git argument string. The core builders put the per-command `-c …`
    // git options BEFORE the subcommand and the remote/refspec operands after the
    // switches, and `git pull` stops option parsing at its first operand — so an
    // option appended at the end would be read as a refspec. Falls back to
    // prepending nothing if the token is somehow absent (the command still runs,
    // just without the extra option).
    private static string InsertOption(string arguments, string subcommand, string option)
    {
        string[] parts = arguments.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == subcommand)
            {
                return string.Join(' ', parts[..(i + 1)].Append(option).Concat(parts[(i + 1)..]));
            }
        }

        return arguments;
    }

    /// <summary>
    ///  Streaming variant of <see cref="Push"/> — see <see cref="FetchStreaming"/>.
    ///
    ///  <para>Kept for the existing call sites: it is the two-state (plain / safe
    ///  force) shorthand for the <see cref="PushForceMode"/> overload, where
    ///  <c>force: true</c> means <c>--force-with-lease</c>.</para>
    /// </summary>
    public RemoteOpResult PushStreaming(string repoPath, string remote, string branch, bool force, Action<string> onOutput, GitCredentials? credentials = null)
        => PushStreaming(
            repoPath,
            remote,
            branch,
            force ? PushForceMode.WithLease : PushForceMode.None,
            onOutput,
            credentials);

    /// <summary>
    ///  Streaming push with the full three-state force choice — see
    ///  <see cref="PushForceMode"/>. Branches should use
    ///  <see cref="PushForceMode.WithLease"/>; plain <see cref="PushForceMode.Force"/>
    ///  is what tags require, as git cannot lease a tag.
    /// </summary>
    public RemoteOpResult PushStreaming(string repoPath, string remote, string branch, PushForceMode force, Action<string> onOutput, GitCredentials? credentials = null)
        => PushStreaming(repoPath, remote, branch, force, track: true, onOutput, credentials);

    /// <summary>
    ///  Streaming push that also decides whether to write TRACKING configuration.
    /// </summary>
    /// <param name="track">
    ///  Add <c>-u</c>, making the pushed branch track the remote one. Upstream never
    ///  hard-codes this: it comes from the "Replace tracking reference" check box,
    ///  falling back to a probe of the branch's existing upstream and of
    ///  <c>branch.autosetupmerge</c> (<c>FormPush.cs:335-365</c>). The port used to
    ///  pass <c>-u</c> unconditionally here, so every push quietly (re)pointed the
    ///  branch's upstream at whatever remote happened to be selected.
    /// </param>
    public RemoteOpResult PushStreaming(string repoPath, string remote, string branch, PushForceMode force, bool track, Action<string> onOutput, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        if (string.IsNullOrEmpty(branch))
        {
            branch = module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(branch))
        {
            return new RemoteOpResult(false, "No branch to push (detached HEAD?).", AuthFailed: false);
        }

        ArgumentString args = Commands.Push(
            remote: remote,
            fromBranch: branch,
            toBranch: branch,
            force: force switch
            {
                PushForceMode.WithLease => ForcePushOptions.ForceWithLease,
                PushForceMode.Force => ForcePushOptions.Force,
                _ => ForcePushOptions.DoNotForce,
            },
            track: track,
            recursiveSubmodules: 0);

        return RunStreaming(module, remote, args, onOutput, credentials, forPush: true);
    }

    // Streaming counterpart of the private Run overloads: builds the same argument
    // string (optionally wrapped with the transient credential helper for http/https
    // remotes), runs it through GitStreamRunner emitting each line live, and returns
    // the accumulated output. onOutput may be called from a background thread.
    private RemoteOpResult RunStreaming(GitModule module, string remote, ArgumentString args, Action<string> onOutput, GitCredentials? credentials, bool forPush, bool remoteIsUrl = false)
    {
        string argString = args.Arguments ?? string.Empty;
        string echoArgs = argString;
        IReadOnlyDictionary<string, string?>? env = null;

        if (credentials is not null && IsHttpTarget(module, remote, forPush, remoteIsUrl))
        {
            // Mirror RunWithCredentials: a one-shot inline credential helper that
            // reads the secret from env vars, prepended to the argument string. The
            // secret itself is never on the command line.
            string helper = $"!f() {{ test $1 = get && echo username=${UserEnvVar} && echo password=${PassEnvVar}; }}; f";
            argString = $"-c credential.helper= -c \"credential.helper={helper}\" {argString}";
            env = new Dictionary<string, string?>
            {
                [UserEnvVar] = credentials.Username,
                [PassEnvVar] = credentials.Password,
            };
        }

        // Language-independent second opinion on "did this fail on authentication":
        // the credential-helper verbs git uses (get/store/erase) are protocol tokens,
        // not translated messages. See GitAuthProbe.
        using GitAuthProbe probe = GitAuthProbe.Create();
        argString = probe.Decorate(argString);
        env = probe.WithMarker(env);

        StringBuilder sb = new();
        int exit = GitStreamRunner.Run(repoPath: module.WorkingDir, arguments: argString, onLine: line =>
        {
            sb.AppendLine(line);
            onOutput(line);
        }, env: env, echoArguments: echoArgs);

        string output = sb.ToString();

        // Credentials supplied by the in-app dialog worked → hand them to git's own
        // configured credential helper (keyring / store) via `git credential approve`
        // so subsequent operations resolve silently, the way Git Credential Manager
        // behaves on Windows. Best-effort: a missing/failing helper changes nothing.
        if (exit == 0 && credentials is not null)
        {
            ApproveCredentials(module, remote, forPush, credentials, onOutput, remoteIsUrl);
        }

        bool authFailed = LooksLikeAuthFailure(output) || probe.LooksLikeAuthFailure(exit);
        if (authFailed)
        {
            // Also report it to the hosting process dialog, which decides whether to
            // auto-close and hand off to the in-app credentials prompt.
            GitAuthSignal.Report();
        }

        return new RemoteOpResult(exit == 0, output, authFailed);
    }

    // Persists working credentials in git's configured credential helper by piping a
    // credential description to `git credential approve` on stdin. The secret goes
    // through stdin only — never the command line — and no transient helper override
    // is passed, so git routes it to the user's real helper (e.g. libsecret/keyring).
    private void ApproveCredentials(GitModule module, string remote, bool forPush, GitCredentials credentials, Action<string> onOutput, bool remoteIsUrl = false)
    {
        try
        {
            string? url;
            if (remoteIsUrl)
            {
                url = remote;
            }
            else
            {
                RemoteRow? row = ListRemotesFrom(module).FirstOrDefault(r => r.Name == remote);
                url = forPush ? row?.PushUrl : row?.FetchUrl;
            }

            if (string.IsNullOrEmpty(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https"))
            {
                return;
            }

            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = "credential approve",
                WorkingDirectory = module.WorkingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            GitEnvironment.ApplyDiagnosticLocale(psi.Environment);

            using Process proc = new() { StartInfo = psi };
            proc.Start();
            proc.StandardInput.NewLine = "\n";
            proc.StandardInput.WriteLine($"protocol={uri.Scheme}");
            proc.StandardInput.WriteLine($"host={uri.Host}");
            proc.StandardInput.WriteLine($"username={credentials.Username}");
            proc.StandardInput.WriteLine($"password={credentials.Password}");
            proc.StandardInput.WriteLine();
            proc.StandardInput.Close();
            proc.WaitForExit(5000);

            if (proc.HasExited && proc.ExitCode == 0)
            {
                onOutput("Credentials saved to the configured git credential helper.");
            }
        }
        catch (Exception)
        {
            // Best-effort only: never fail the operation because saving failed.
        }
    }

    // Runs a remote command. Without credentials (or for non-http/https remotes)
    // the command runs as-is and git resolves auth through its own configured
    // helpers. With credentials on an http/https remote the command is wrapped with
    // a transient, in-memory credential helper (see <see cref="RunWithCredentials"/>).
    private RemoteOpResult Run(GitModule module, string remote, ArgumentString args, GitCredentials? credentials, bool forPush, bool remoteIsUrl = false)
    {
        if (credentials is not null && IsHttpTarget(module, remote, forPush, remoteIsUrl))
        {
            return RunWithCredentials(module, args, credentials);
        }

        return Run(module, args);
    }

    private static RemoteOpResult Run(GitModule module, ArgumentString args)
    {
        // This path goes through the SHARED core executable, which starts git with the
        // inherited environment and takes no per-command environment: the locale
        // pinning and the probe's marker path therefore have to be published
        // process-wide for the duration of the command (the same technique
        // RunWithCredentials uses for the transient secret).
        using GitAuthProbe probe = GitAuthProbe.Create();
        using IDisposable locale = GitEnvironment.DiagnosticLocaleScope();
        using IDisposable probeEnv = probe.EnterProcessEnvironment();

        ArgumentString probed = probe.Decorate(args.Arguments ?? string.Empty);
        ExecutionResult result = module.GitExecutable.Execute(probed, throwOnErrorExit: false);
        string output = result.AllOutput;
        bool authFailed = LooksLikeAuthFailure(output) || probe.LooksLikeAuthFailure(result.ExitCode ?? -1);
        if (authFailed)
        {
            GitAuthSignal.Report();
        }

        return new RemoteOpResult(result.ExitedSuccessfully, output, authFailed);
    }

    // Environment variable names the transient credential helper reads. Only these
    // *names* ever appear in the (logged) git command line; the secret values live
    // in the process environment for the duration of the single command and are
    // cleared immediately afterwards.
    private const string UserEnvVar = "GE_AVALONIA_CRED_USER";
    private const string PassEnvVar = "GE_AVALONIA_CRED_PASS";

    // Feeds <paramref name="credentials"/> to a single git command through git's own
    // credential mechanism, without ever putting the secret in the remote URL, the
    // command line, or git config.
    //
    // Mechanism: two per-command config entries are prepended to the git invocation:
    //   -c credential.helper=            → clears any inherited/prompting helper
    //   -c credential.helper=!f() {...}  → a one-shot inline helper that echoes the
    //                                      credentials git asks for
    // The inline helper references environment variables ($GE_AVALONIA_CRED_*) rather
    // than embedding the username/password literally, so the secret is NOT part of the
    // argument string that <see cref="Executable"/> logs / stores on the process. The
    // env vars are set just before the command and removed in a finally block.
    private static RemoteOpResult RunWithCredentials(GitModule module, ArgumentString args, GitCredentials credentials)
    {
        // The inline helper only responds to the "get" operation; it echoes the
        // username/password git expects on stdout. No double quotes are used inside
        // the helper body so the whole value can be wrapped in a single double-quoted
        // token when passed as a process argument.
        string helper = $"!f() {{ test $1 = get && echo username=${UserEnvVar} && echo password=${PassEnvVar}; }}; f";
        ArgumentString wrapped = $"-c credential.helper= -c \"credential.helper={helper}\" {args.Arguments}";

        string? previousUser = Environment.GetEnvironmentVariable(UserEnvVar);
        string? previousPass = Environment.GetEnvironmentVariable(PassEnvVar);
        try
        {
            // Child git processes are started with UseShellExecute=false and inherit
            // this process's environment, so setting the vars here makes them visible
            // to the one git command (and its credential helper subshell) only.
            Environment.SetEnvironmentVariable(UserEnvVar, credentials.Username);
            Environment.SetEnvironmentVariable(PassEnvVar, credentials.Password);
            return Run(module, wrapped);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserEnvVar, previousUser);
            Environment.SetEnvironmentVariable(PassEnvVar, previousPass);
        }
    }

    // Detects the common authentication-failure phrases git emits so the UI can
    // prompt for credentials and retry once.
    private static bool LooksLikeAuthFailure(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        string[] markers =
        [
            "Authentication failed",
            "could not read Username",
            "could not read Password",
            "Invalid username or password",
            "remote: Unauthorized",
            "fatal: Authentication",
            "terminal prompts disabled",
        ];

        foreach (string marker in markers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // True when the given remote's URL is http/https, i.e. a remote for which the
    // username/password credential helper is meaningful. ssh/git remotes are
    // key-based and return false (the retry then behaves like the first attempt).
    // Same question as IsHttpRemote, but for a target that may already BE a URL
    // (the Pull dialog's "URL" radio): there is no remote to look up then, so the
    // target itself is inspected. Without this the credential prompt-and-retry
    // would silently do nothing for URL pulls.
    private static bool IsHttpTarget(GitModule module, string target, bool forPush, bool targetIsUrl)
    {
        if (!targetIsUrl)
        {
            return IsHttpRemote(module, target, forPush);
        }

        return Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https";
    }

    private static bool IsHttpRemote(GitModule module, string remote, bool forPush)
    {
        RemoteRow? row = ListRemotesFrom(module).FirstOrDefault(r => r.Name == remote);
        string? url = forPush ? row?.PushUrl : row?.FetchUrl;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https";
    }

    private static IReadOnlyList<RemoteRow> ListRemotesFrom(GitModule module)
    {
        IReadOnlyList<Remote> remotes = RunDetached(module.GetRemotesAsync);
        return [.. remotes.Select(r => new RemoteRow(
            r.Name,
            r.FetchUrl ?? string.Empty,
            r.PushUrls.Count > 0 ? r.PushUrls[0] : r.FetchUrl ?? string.Empty))];
    }
}
