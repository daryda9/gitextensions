using GitCommands;
using GitCommands.Git;
using GitCommands.Git.Tag;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single ref (branch or tag) projected for display in the Avalonia
///  branch/tag view. Independent of the WinForms core UI types.
/// </summary>
public sealed record BranchTagRow(
    string Name,
    bool IsTag,
    bool IsRemote,
    bool IsCurrent,
    string ObjectId)
{
    public string Display
    {
        get
        {
            string marker = IsCurrent ? "* " : "  ";
            string kind = IsTag ? "tag" : IsRemote ? "remote" : "branch";
            return $"{marker}{Name}  ({kind})";
        }
    }

    public override string ToString() => Display;
}

/// <summary>
///  Snapshot of a repository's refs: local + remote branches and tags.
/// </summary>
public sealed record BranchTagListing(
    IReadOnlyList<BranchTagRow> Branches,
    IReadOnlyList<BranchTagRow> Tags);

/// <summary>
///  Result of a mutating branch/tag operation.
/// </summary>
public sealed record BranchTagResult(bool Success, string Output);

/// <summary>
///  Snapshot of the working tree taken before a checkout: whether it is dirty
///  and how many entries <c>git status --porcelain</c> reports (tracked
///  modifications + untracked files). Loaded off the UI thread and handed to
///  the checkout dialog, which must never call git itself.
/// </summary>
public sealed record WorkingTreeState(bool IsDirty, int ChangedCount)
{
    public static readonly WorkingTreeState Clean = new(false, 0);
}

/// <summary>
///  How a remote branch name splits up, and which local branch tracks it:
///  <c>origin/feature/x</c> → remote <c>origin</c>, short name <c>feature/x</c>,
///  tracking branch whatever <c>branch.&lt;x&gt;.merge</c> points at (upstream
///  <c>GitModule.GetLocalTrackingBranchName</c>, which falls back to the short name).
/// </summary>
public sealed record RemoteBranchNaming(string Remote, string ShortName, string TrackingBranch);

/// <summary>
///  Everything the checkout-branch form needs, loaded in one go off the UI thread.
/// </summary>
public sealed record CheckoutBranchData(
    IReadOnlyList<string> LocalBranches,
    IReadOnlyList<string> RemoteBranches,
    IReadOnlyDictionary<string, RemoteBranchNaming> RemoteNaming,
    WorkingTreeState WorkingTree,
    LocalChangesAction DefaultLocalChanges)
{
    public static readonly CheckoutBranchData Empty =
        new([], [], new Dictionary<string, RemoteBranchNaming>(), WorkingTreeState.Clean, LocalChangesAction.DontChange);

    /// <summary>Whether a local branch of that exact name exists (case-insensitive, as upstream).</summary>
    public bool LocalBranchExists(string name)
        => name.Length > 0 && LocalBranches.Any(b => b.Equals(name, StringComparison.OrdinalIgnoreCase));

    public RemoteBranchNaming NamingFor(string remoteBranch)
        => RemoteNaming.TryGetValue(remoteBranch, out RemoteBranchNaming? n) ? n : new RemoteBranchNaming(string.Empty, remoteBranch, remoteBranch);
}

/// <summary>
///  Result of the pre-flight check run before a <c>checkout -B</c>: whether the reset
///  is a fast-forward and the merge base to name in the warning.
/// </summary>
public sealed record ResetFastForwardInfo(bool IsFastForward, string MergeBaseDisplay);

/// <summary>
///  Branch/tag operations (list, checkout, create/delete branch, create/delete
///  tag, merge, rebase) implemented by reusing the Git Extensions core
///  (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>. All
///  methods are synchronous and are meant to be called off the UI thread.
/// </summary>
public sealed class BranchTagService
{
    /// <summary>
    ///  Reads the local + remote branches and the tags for the repository,
    ///  marking the currently checked-out branch.
    /// </summary>
    public BranchTagListing LoadRefs(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string current = module.GetSelectedBranch();
        IReadOnlyList<IGitRef> refs = module.GetRefs(RefsFilter.Heads | RefsFilter.Remotes | RefsFilter.Tags);

        List<BranchTagRow> branches = [];
        List<BranchTagRow> tags = [];

        foreach (IGitRef gitRef in refs)
        {
            string oid = gitRef.ObjectId is { IsZero: false } id ? id.ToString() : string.Empty;
            if (gitRef.IsTag)
            {
                tags.Add(new BranchTagRow(gitRef.Name, IsTag: true, IsRemote: false, IsCurrent: false, oid));
            }
            else
            {
                bool isCurrent = !gitRef.IsRemote && string.Equals(gitRef.Name, current, StringComparison.Ordinal);
                branches.Add(new BranchTagRow(gitRef.Name, IsTag: false, gitRef.IsRemote, isCurrent, oid));
            }
        }

        return new BranchTagListing(branches, tags);
    }

    /// <summary>
    ///  Reads the working-tree state (dirty / number of changed entries) with a
    ///  single <c>git status --porcelain -uall --ignore-submodules=all</c>, the
    ///  same set <see cref="GitModule.IsDirtyDir"/> counts. Synchronous: call it
    ///  off the UI thread.
    /// </summary>
    public WorkingTreeState LoadWorkingTreeState(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("status") { "--porcelain", "-uall", "--ignore-submodules=all" };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return WorkingTreeState.Clean;
        }

        int count = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Trim().Length > 0);

        return new WorkingTreeState(count > 0, count);
    }

    /// <summary>
    ///  Checks out the given branch or revision, deciding what to do with local
    ///  changes:
    ///  <list type="bullet">
    ///   <item><see cref="LocalChangesAction.DontChange"/>: plain checkout — git
    ///    refuses when a modified file would be overwritten.</item>
    ///   <item><see cref="LocalChangesAction.Merge"/>: <c>checkout --merge</c>.</item>
    ///   <item><see cref="LocalChangesAction.Reset"/>: <c>checkout --force</c>,
    ///    which <b>discards</b> the local changes.</item>
    ///   <item><see cref="LocalChangesAction.Stash"/>: <c>git stash push</c>
    ///    first (as upstream's FormCheckoutBranch does — the argument builder has
    ///    no flag for it), then a plain checkout. The stash is left on the stack
    ///    for the user to pop.</item>
    ///  </list>
    /// </summary>
    public BranchTagResult Checkout(
        string repoPath,
        string name,
        LocalChangesAction changesAction = LocalChangesAction.DontChange,
        bool includeUntrackedInStash = true)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string prefix = string.Empty;
        if (changesAction == LocalChangesAction.Stash)
        {
            BranchTagResult stashed = StashLocalChanges(module, name, includeUntrackedInStash);
            if (!stashed.Success)
            {
                return stashed;
            }

            prefix = stashed.Output.TrimEnd() + Environment.NewLine;
        }

        LocalChangesAction checkoutAction = changesAction == LocalChangesAction.Stash
            ? LocalChangesAction.DontChange
            : changesAction;

        BranchTagResult result = Run(module, Commands.Checkout(name, checkoutAction));
        return prefix.Length == 0 ? result : result with { Output = prefix + result.Output };
    }

    /// <summary>
    ///  Checks out the remote branch <paramref name="remote"/>/<paramref name="branch"/>
    ///  the way upstream's <c>StartCheckoutRemoteBranch</c> does — as a <b>local
    ///  branch</b>, not as a detached HEAD.
    ///  <para>A plain <c>git checkout origin/x</c> always detaches, because the
    ///  remote-tracking ref is an unambiguous revision; that is almost never what
    ///  "checkout this remote branch" is meant to do. So:</para>
    ///  <list type="bullet">
    ///   <item>a local branch already named <paramref name="branch"/> is simply
    ///    checked out (git keeps whatever upstream it already tracks);</item>
    ///   <item>otherwise a new local branch of that name is created tracking the
    ///    remote one (<c>checkout -b &lt;branch&gt; --track &lt;remote&gt;/&lt;branch&gt;</c>).</item>
    ///  </list>
    ///  <paramref name="changesAction"/> has the same meaning as in
    ///  <see cref="Checkout"/>, including the <see cref="LocalChangesAction.Stash"/>
    ///  pre-step.
    /// </summary>
    public BranchTagResult CheckoutRemoteBranch(
        string repoPath,
        string remote,
        string branch,
        LocalChangesAction changesAction = LocalChangesAction.DontChange,
        bool includeUntrackedInStash = true)
    {
        string remoteName = remote?.Trim() ?? string.Empty;
        string branchName = branch?.Trim() ?? string.Empty;
        if (remoteName.Length == 0 || branchName.Length == 0)
        {
            return new BranchTagResult(false, "Remote and branch name cannot be empty.");
        }

        GitModule module = GitContext.CreateModule(repoPath);

        bool localExists = module
            .GetRefs(RefsFilter.Heads)
            .Any(r => string.Equals(r.Name, branchName, StringComparison.Ordinal));

        // A local branch of that name already exists: this is an ordinary checkout,
        // so go through Checkout and inherit its whole local-changes handling.
        if (localExists)
        {
            return Checkout(repoPath, branchName, changesAction, includeUntrackedInStash);
        }

        string prefix = string.Empty;
        if (changesAction == LocalChangesAction.Stash)
        {
            BranchTagResult stashed = StashLocalChanges(module, branchName, includeUntrackedInStash);
            if (!stashed.Success)
            {
                return stashed;
            }

            prefix = stashed.Output.TrimEnd() + Environment.NewLine;
        }

        bool merge = changesAction == LocalChangesAction.Merge;
        bool force = changesAction == LocalChangesAction.Reset;
        GitArgumentBuilder args = new("checkout")
        {
            { merge, "--merge" },
            { force, "--force" },
            "-b",
            branchName,
            "--track",
            $"{remoteName}/{branchName}",
        };

        BranchTagResult result = Run(module, args);
        return prefix.Length == 0 ? result : result with { Output = prefix + result.Output };
    }

    /// <summary>
    ///  The full upstream checkout, i.e. the one behind <c>FormCheckoutBranch</c>'s OK
    ///  button: <see cref="Commands.CheckoutBranch"/> with the remote flag, the
    ///  local-changes action and — for a remote branch — the new-branch mode.
    ///  <para>Mapping done by the core argument builder
    ///  (<c>src/app/GitCommands/Git/Commands.cs:10</c>):</para>
    ///  <list type="bullet">
    ///   <item><see cref="CheckoutNewBranchMode.Create"/> → <c>-b &lt;new&gt; --track</c>,
    ///    a brand new local branch tracking the remote one;</item>
    ///   <item><see cref="CheckoutNewBranchMode.Reset"/> → <c>-B &lt;new&gt;</c>, which
    ///    <b>moves</b> an existing local branch onto the remote one (and creates it when
    ///    it does not exist yet);</item>
    ///   <item><see cref="CheckoutNewBranchMode.DontCreate"/> → nothing, so a remote ref
    ///    lands on a <b>detached HEAD</b> — upstream's "Checkout the commit (in detached
    ///    head)".</item>
    ///  </list>
    ///  <para>Both are ignored by the builder unless <paramref name="isRemote"/> is set,
    ///  which is why a local checkout simply falls through to a plain checkout.</para>
    ///  <para><see cref="LocalChangesAction.Stash"/> has no flag in the builder, so — as
    ///  in <see cref="Checkout"/> and in upstream — the stash push happens first and the
    ///  checkout then runs with <see cref="LocalChangesAction.DontChange"/>.</para>
    ///  <para>Existing callers of <see cref="Checkout"/> and
    ///  <see cref="CheckoutRemoteBranch"/> are untouched: this is an additional entry
    ///  point, not a replacement.</para>
    /// </summary>
    public BranchTagResult CheckoutBranch(
        string repoPath,
        string branchName,
        bool isRemote,
        LocalChangesAction changesAction = LocalChangesAction.DontChange,
        CheckoutNewBranchMode newBranchMode = CheckoutNewBranchMode.DontCreate,
        string? newBranchName = null,
        bool includeUntrackedInStash = true)
    {
        string branch = branchName?.Trim() ?? string.Empty;
        if (branch.Length == 0)
        {
            return new BranchTagResult(false, "Branch name cannot be empty.");
        }

        string? newName = newBranchName?.Trim();
        if (isRemote && newBranchMode != CheckoutNewBranchMode.DontCreate && string.IsNullOrEmpty(newName))
        {
            return new BranchTagResult(false, "Custom branch name is empty.");
        }

        GitModule module = GitContext.CreateModule(repoPath);

        string prefix = string.Empty;
        if (changesAction == LocalChangesAction.Stash)
        {
            BranchTagResult stashed = StashLocalChanges(module, branch, includeUntrackedInStash);
            if (!stashed.Success)
            {
                return stashed;
            }

            prefix = stashed.Output.TrimEnd() + Environment.NewLine;
            changesAction = LocalChangesAction.DontChange;
        }

        IGitCommand command = Commands.CheckoutBranch(branch, isRemote, changesAction, newBranchMode, newName);
        BranchTagResult result = Run(module, command.Arguments);
        return prefix.Length == 0 ? result : result with { Output = prefix + result.Output };
    }

    /// <summary>
    ///  Everything <see cref="Views.CheckoutBranchForm"/> needs to be built without
    ///  touching git from the UI thread: the two branch lists, the local branch that
    ///  tracks each remote one, the working-tree state and the stored default action.
    /// </summary>
    public CheckoutBranchData LoadCheckoutBranchData(string repoPath, LocalChangesAction defaultAction)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        List<string> local = [];
        List<string> remote = [];
        foreach (IGitRef gitRef in module.GetRefs(RefsFilter.Heads | RefsFilter.Remotes))
        {
            // "origin/HEAD" is a symbolic alias, never a checkout target — upstream
            // filters it out of the contains-commit list for the same reason.
            if (gitRef.IsRemote)
            {
                if (!gitRef.Name.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    remote.Add(gitRef.Name);
                }
            }
            else
            {
                local.Add(gitRef.Name);
            }
        }

        local.Sort(StringComparer.OrdinalIgnoreCase);
        remote.Sort(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> remotes = module.GetRemoteNames();

        // remote branch -> (remote name, local tracking branch name). Computed here
        // because GetLocalTrackingBranchName reads the local git config.
        Dictionary<string, RemoteBranchNaming> naming = new(StringComparer.Ordinal);
        foreach (string name in remote)
        {
            string remoteName = GitRefName.GetRemoteName(name, remotes);
            string tracking = module.GetLocalTrackingBranchName(remoteName, name) ?? string.Empty;
            string shortName = remoteName.Length > 0 && name.Length > remoteName.Length + 1
                ? name[(remoteName.Length + 1)..]
                : name;
            naming[name] = new RemoteBranchNaming(remoteName, shortName, tracking);
        }

        return new CheckoutBranchData(local, remote, naming, LoadWorkingTreeState(repoPath), defaultAction);
    }

    /// <summary>
    ///  Upstream's <c>lbChanges</c>: the ahead/behind string between the current
    ///  checkout and <paramref name="branch"/>. Empty when there is no checkout yet.
    /// </summary>
    public string GetAheadBehindInfo(string repoPath, string branch)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId current = module.GetCurrentCheckout();
        return current.IsZero ? string.Empty : module.GetCommitCountString(current, branch);
    }

    /// <summary>
    ///  Whether moving <paramref name="localBranch"/> onto <paramref name="remoteBranch"/>
    ///  with <c>checkout -B</c> is a fast-forward. Upstream warns before a non-fast-forward
    ///  reset because it throws away commits (<c>FormCheckoutBranch.cs:293-317</c>): the
    ///  reset is fast-forward exactly when the local tip <b>is</b> the merge base.
    ///  Unknown refs count as fast-forward, so a missing local branch never warns.
    /// </summary>
    public ResetFastForwardInfo GetResetFastForwardInfo(string repoPath, string localBranch, string remoteBranch)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        if (string.IsNullOrWhiteSpace(localBranch) || string.IsNullOrWhiteSpace(remoteBranch))
        {
            return new ResetFastForwardInfo(true, string.Empty);
        }

        ObjectId localId = module.RevParse(localBranch);
        ObjectId remoteId = module.RevParse(remoteBranch);
        if (localId.IsZero || remoteId.IsZero)
        {
            return new ResetFastForwardInfo(true, string.Empty);
        }

        ObjectId mergeBase = module.GetMergeBase(localId, remoteId);
        bool fastForward = localId == mergeBase;
        string display = mergeBase.IsZero ? "merge base" : mergeBase.ToShortString();
        return new ResetFastForwardInfo(fastForward, display);
    }

    /// <summary>
    ///  Creates a branch <paramref name="name"/> at <paramref name="startPoint"/>
    ///  (defaults to HEAD when empty), optionally checking it out.
    /// </summary>
    public BranchTagResult CreateBranch(string repoPath, string name, string startPoint, bool checkout)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string start = string.IsNullOrWhiteSpace(startPoint) ? "HEAD" : startPoint.Trim();
        ObjectId objectId = module.RevParse(start);
        if (objectId.IsZero)
        {
            return new BranchTagResult(false, $"Cannot resolve start point '{start}'.");
        }

        ArgumentString args = Commands.Branch(name, objectId, checkout);
        return Run(module, args);
    }

    /// <summary>
    ///  Creates a tag <paramref name="name"/> on <paramref name="commit"/>
    ///  (defaults to HEAD when empty).
    ///  <para><paramref name="operation"/> selects lightweight / annotated /
    ///  signed (default or specific GPG key); when it is <c>null</c> the legacy
    ///  behaviour applies — annotated if <paramref name="message"/> is non-empty,
    ///  lightweight otherwise. <paramref name="force"/> maps to <c>-f</c>
    ///  (overwrites an existing tag of the same name) and a non-empty
    ///  <paramref name="pushToRemote"/> pushes the tag right after creating it.</para>
    /// </summary>
    public BranchTagResult CreateTag(
        string repoPath,
        string name,
        string commit,
        string message,
        TagOperation? operation = null,
        string signKeyId = "",
        bool force = false,
        string pushToRemote = "")
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string target = string.IsNullOrWhiteSpace(commit) ? "HEAD" : commit.Trim();
        ObjectId objectId = module.RevParse(target);
        if (objectId.IsZero)
        {
            return new BranchTagResult(false, $"Cannot resolve commit '{target}'.");
        }

        message ??= string.Empty;
        TagOperation op = operation
            ?? (message.Trim().Length > 0 ? TagOperation.Annotate : TagOperation.Lightweight);

        // Every operation but "lightweight" carries a message, so git needs the
        // -F file even when the user left the box empty for a signed tag.
        bool needsMessageFile = op != TagOperation.Lightweight;
        GitCreateTagArgs args = new(name, objectId, op, tagMessage: message, signKeyId: signKeyId ?? string.Empty, force: force);

        string? messageFile = null;
        try
        {
            if (needsMessageFile)
            {
                messageFile = System.IO.Path.GetTempFileName();
                File.WriteAllText(messageFile, message);
            }

            IGitCommand command = Commands.CreateTag(args, messageFile, module.GetPathForGitExecution);
            BranchTagResult result = Run(module, command.Arguments);

            if (!result.Success || string.IsNullOrWhiteSpace(pushToRemote))
            {
                return result;
            }

            GitArgumentBuilder pushArgs = new("push")
            {
                pushToRemote.Trim(),
                { force, "--force" },
                "refs/tags/" + name
            };

            BranchTagResult pushed = Run(module, pushArgs);
            return new BranchTagResult(
                pushed.Success,
                (result.Output.TrimEnd() + Environment.NewLine + pushed.Output).TrimStart());
        }
        finally
        {
            if (messageFile is not null)
            {
                try
                {
                    File.Delete(messageFile);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }

    /// <summary>
    ///  Names of the configured remotes, for the "push tag to" picker. Synchronous:
    ///  call it off the UI thread.
    /// </summary>
    public IReadOnlyList<string> LoadRemotes(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            return [.. module.GetRemoteNames()];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///  Renames the local branch <paramref name="oldName"/> to
    ///  <paramref name="newName"/> via <c>git branch -m</c>. Fails gracefully when
    ///  the source branch is missing or the target name already exists.
    /// </summary>
    public BranchTagResult RenameBranch(string repoPath, string oldName, string newName)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string source = oldName?.Trim() ?? string.Empty;
        string target = newName?.Trim() ?? string.Empty;
        if (source.Length == 0 || target.Length == 0)
        {
            return new BranchTagResult(false, "Branch name cannot be empty.");
        }

        IGitRef? branch = module
            .GetRefs(RefsFilter.Heads)
            .FirstOrDefault(r => string.Equals(r.Name, source, StringComparison.Ordinal));

        if (branch is null)
        {
            return new BranchTagResult(false, $"Local branch '{source}' not found.");
        }

        // Plain "git branch -m <old> <new>" (no -M): git itself refuses when the
        // target already exists, and we surface that message via the result DTO.
        GitArgumentBuilder args = new("branch") { "-m", source, target };
        return Run(module, args);
    }

    /// <summary>
    ///  Deletes the local branch <paramref name="name"/> (force skips the merged
    ///  check). Uses the core delete-branch command builder.
    /// </summary>
    public BranchTagResult DeleteBranch(string repoPath, string name, bool force)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        IGitRef? branch = module
            .GetRefs(RefsFilter.Heads)
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        if (branch is null)
        {
            return new BranchTagResult(false, $"Local branch '{name}' not found.");
        }

        IGitCommand command = Commands.DeleteBranch([branch], force);
        return Run(module, command.Arguments);
    }

    /// <summary>
    ///  Deletes the branch <paramref name="branch"/> on the remote
    ///  <paramref name="remote"/> via <c>git push &lt;remote&gt; --delete &lt;branch&gt;</c>.
    ///  <paramref name="branch"/> is the short branch name on the remote (without
    ///  the leading "&lt;remote&gt;/"). Destructive: this affects the remote.
    /// </summary>
    public BranchTagResult DeleteRemoteBranch(string repoPath, string remote, string branch)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string remoteName = remote?.Trim() ?? string.Empty;
        string branchName = branch?.Trim() ?? string.Empty;
        if (remoteName.Length == 0 || branchName.Length == 0)
        {
            return new BranchTagResult(false, "Remote and branch name cannot be empty.");
        }

        GitArgumentBuilder args = new("push") { remoteName, "--delete", branchName };
        return Run(module, args);
    }

    /// <summary>
    ///  Deletes the tag <paramref name="name"/>.
    /// </summary>
    public BranchTagResult DeleteTag(string repoPath, string name)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // Run raw so we can surface git's output in the result DTO
        // (GitModule.DeleteTag returns void and throws on failure).
        GitArgumentBuilder args = new("tag") { "-d", name };
        return Run(module, args);
    }

    /// <summary>
    ///  Merges <paramref name="name"/> into the current branch (auto-commit,
    ///  no editor prompt).
    /// </summary>
    public BranchTagResult MergeBranch(string repoPath, string name)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = Commands.MergeBranch(
            branch: name,
            allowFastForward: true,
            squash: false,
            noCommit: false,
            strategy: string.Empty,
            allowUnrelatedHistories: false,
            mergeCommitFilePath: null,
            getPathForGitExecution: module.GetPathForGitExecution,
            log: null);
        return Run(module, args);
    }

    /// <summary>
    ///  The name of the currently checked-out branch, or an empty string when HEAD
    ///  is detached. Synchronous: call it off the UI thread.
    /// </summary>
    public string GetCurrentBranch(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string current = module.GetSelectedBranch();

        // GitModule reports a detached HEAD as "(no branch)".
        return current.StartsWith('(') ? string.Empty : current;
    }

    /// <summary>
    ///  Moves the local branch <paramref name="branch"/> to <paramref name="commit"/>
    ///  (<c>git branch -f</c>) — the original's "Reset another branch to here".
    ///  Destructive: the branch loses whatever it pointed at. The CURRENT branch is
    ///  refused (git itself refuses it too, but the message is cryptic): resetting
    ///  the checked-out branch is what "Reset current branch to here" is for, and it
    ///  must also decide what happens to the working tree.
    /// </summary>
    public BranchTagResult ResetBranchTo(string repoPath, string branch, string commit)
    {
        string name = branch?.Trim() ?? string.Empty;
        string target = commit?.Trim() ?? string.Empty;
        if (name.Length == 0 || target.Length == 0)
        {
            return new BranchTagResult(false, "Branch name and commit cannot be empty.");
        }

        GitModule module = GitContext.CreateModule(repoPath);
        if (string.Equals(module.GetSelectedBranch(), name, StringComparison.Ordinal))
        {
            return new BranchTagResult(
                false,
                $"'{name}' is the current branch: use \"Reset current branch to here\" instead.");
        }

        IGitRef? existing = module
            .GetRefs(RefsFilter.Heads)
            .FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        if (existing is null)
        {
            return new BranchTagResult(false, $"Local branch '{name}' not found.");
        }

        GitArgumentBuilder args = new("branch") { "-f", name, target };
        return Run(module, args);
    }

    /// <summary>
    ///  Rebases the current branch onto <paramref name="branch"/>.
    /// </summary>
    public BranchTagResult RebaseOnto(string repoPath, string branch)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        Commands.RebaseOptions options = new() { BranchName = branch };
        ArgumentString args = Commands.Rebase(options);
        return Run(module, args);
    }

    // The "stash the local changes first" pre-step shared by Checkout and
    // CheckoutRemoteBranch (upstream's FormCheckoutBranch does the same, because the
    // checkout argument builder has no flag for it). The stash is left on the stack.
    private static BranchTagResult StashLocalChanges(GitModule module, string target, bool includeUntracked)
    {
        GitArgumentBuilder args = new("stash")
        {
            "push",
            { includeUntracked, "--include-untracked" },
            "-m",
            $"Checkout {target} (auto stash)".Quote()
        };

        return Run(module, args);
    }

    private static BranchTagResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new BranchTagResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
