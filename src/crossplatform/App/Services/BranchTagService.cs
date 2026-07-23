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
    ///  Checks out the given branch or revision (leaving local changes untouched).
    /// </summary>
    public BranchTagResult Checkout(string repoPath, string name)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = Commands.Checkout(name, LocalChangesAction.DontChange);
        return Run(module, args);
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
    ///  (defaults to HEAD when empty). A non-empty <paramref name="message"/>
    ///  produces an annotated tag; otherwise a lightweight one.
    /// </summary>
    public BranchTagResult CreateTag(string repoPath, string name, string commit, string message)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string target = string.IsNullOrWhiteSpace(commit) ? "HEAD" : commit.Trim();
        ObjectId objectId = module.RevParse(target);
        if (objectId.IsZero)
        {
            return new BranchTagResult(false, $"Cannot resolve commit '{target}'.");
        }

        bool annotated = !string.IsNullOrWhiteSpace(message);
        TagOperation operation = annotated ? TagOperation.Annotate : TagOperation.Lightweight;
        GitCreateTagArgs args = new(name, objectId, operation, tagMessage: message ?? string.Empty);

        string? messageFile = null;
        try
        {
            if (annotated)
            {
                messageFile = System.IO.Path.GetTempFileName();
                File.WriteAllText(messageFile, message ?? string.Empty);
            }

            IGitCommand command = Commands.CreateTag(args, messageFile, module.GetPathForGitExecution);
            return Run(module, command.Arguments);
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
