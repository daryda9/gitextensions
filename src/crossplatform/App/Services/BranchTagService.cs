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
            GitArgumentBuilder stashArgs = new("stash")
            {
                "push",
                { includeUntrackedInStash, "--include-untracked" },
                "-m",
                $"Checkout {name} (auto stash)".Quote()
            };

            BranchTagResult stashed = Run(module, stashArgs);
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
    ///  Rebases the current branch onto <paramref name="branch"/>.
    /// </summary>
    public BranchTagResult RebaseOnto(string repoPath, string branch)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        Commands.RebaseOptions options = new() { BranchName = branch };
        ArgumentString args = Commands.Rebase(options);
        return Run(module, args);
    }

    private static BranchTagResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new BranchTagResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
