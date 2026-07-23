using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single changed file in the working directory, projected for display in the
///  Avalonia working-directory view. Independent of the WinForms core UI types.
/// </summary>
public sealed record WorkingDirFileRow(string Path, string Status, bool IsStaged)
{
    public string Display => $"{Status}  {Path}";

    public override string ToString() => Display;
}

/// <summary>
///  Result of a commit attempt.
/// </summary>
public sealed record WorkingDirCommitResult(bool Success, string Output);

/// <summary>
///  Snapshot of the working directory: staged (index) and unstaged (work tree)
///  changes.
/// </summary>
public sealed record WorkingDirStatus(
    IReadOnlyList<WorkingDirFileRow> Staged,
    IReadOnlyList<WorkingDirFileRow> Unstaged);

/// <summary>
///  Working-directory operations (status, stage, unstage, commit) implemented by
///  reusing the Git Extensions core (<see cref="GitModule"/>) via
///  <see cref="GitContext.CreateModule"/>. All methods are synchronous and are
///  meant to be called off the UI thread.
/// </summary>
public sealed class WorkingDirectoryService
{
    /// <summary>
    ///  Reads the current staged / unstaged changes for the repository.
    /// </summary>
    public WorkingDirStatus LoadStatus(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        IReadOnlyList<GitItemStatus> staged = module.GetIndexFiles();
        IReadOnlyList<GitItemStatus> unstaged = module.GetWorkTreeFiles();

        return new WorkingDirStatus(
            [.. staged.Select(f => ToRow(f, isStaged: true))],
            [.. unstaged.Select(f => ToRow(f, isStaged: false))]);
    }

    /// <summary>
    ///  Stages the given work-tree files into the index.
    /// </summary>
    public WorkingDirCommitResult Stage(string repoPath, IReadOnlyList<WorkingDirFileRow> files)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        IReadOnlyList<GitItemStatus> items = ResolveWorkTreeItems(module, files);
        bool ok = module.StageFiles(items, out string output);
        return new WorkingDirCommitResult(ok, output);
    }

    /// <summary>
    ///  Unstages the given index files back to the work tree.
    /// </summary>
    public WorkingDirCommitResult Unstage(string repoPath, IReadOnlyList<WorkingDirFileRow> files)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        IReadOnlyList<GitItemStatus> items = ResolveIndexItems(module, files);
        bool ok = module.UnstageFiles(items, out string output);
        return new WorkingDirCommitResult(ok, output);
    }

    /// <summary>
    ///  Commits the currently staged changes using <paramref name="message"/>.
    ///  When <paramref name="amend"/> is <see langword="true"/>, amends the last
    ///  commit. Uses the core commit-command builder run through the module's
    ///  executor.
    /// </summary>
    public WorkingDirCommitResult Commit(string repoPath, string message, bool amend)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string messageFile = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllText(messageFile, message ?? string.Empty);

            ArgumentString args = Commands.Commit(
                amend: amend,
                signOff: false,
                author: string.Empty,
                useExplicitCommitMessage: true,
                commitMessageFile: messageFile,
                getPathForGitExecution: module.GetPathForGitExecution);

            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
        }
        finally
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

    /// <summary>
    ///  Undoes the last commit while keeping its changes staged/in the working
    ///  tree (<c>git reset --soft HEAD~1</c>). Does NOT discard any work. If there
    ///  is no parent commit (e.g. only one commit / a root commit), git fails and
    ///  the error text is returned in the result rather than throwing.
    /// </summary>
    public WorkingDirCommitResult UndoLastCommit(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        ArgumentString args = Commands.Reset(ResetMode.Soft, "HEAD~1");
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  Previews which untracked files/dirs would be removed by a clean, without
    ///  deleting anything (<c>git clean -n -d</c>, plus <c>-x</c> when
    ///  <paramref name="includeIgnored"/> is set). Used to drive the required
    ///  confirmation step before a destructive clean.
    /// </summary>
    public WorkingDirCommitResult CleanDryRun(string repoPath, bool includeIgnored = false)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        CleanMode mode = includeIgnored ? CleanMode.All : CleanMode.OnlyNonIgnored;
        ExecutionResult result = module.Clean(mode, dryRun: true, directories: true);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  Removes untracked files and directories (<c>git clean -f -d</c>, plus
    ///  <c>-x</c> when <paramref name="includeIgnored"/> is set). Destructive —
    ///  callers MUST confirm (see <see cref="CleanDryRun"/>) first.
    /// </summary>
    public WorkingDirCommitResult Clean(string repoPath, bool includeIgnored = false)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        CleanMode mode = includeIgnored ? CleanMode.All : CleanMode.OnlyNonIgnored;
        ExecutionResult result = module.Clean(mode, dryRun: false, directories: true);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    // The core stage/unstage helpers key off GitItemStatus.Name and IsDeleted, so
    // re-resolving from a fresh status snapshot keeps those flags accurate rather
    // than reconstructing GitItemStatus objects from the display rows.
    private static IReadOnlyList<GitItemStatus> ResolveWorkTreeItems(GitModule module, IReadOnlyList<WorkingDirFileRow> rows)
    {
        HashSet<string> wanted = [.. rows.Select(r => r.Path)];
        return [.. module.GetWorkTreeFiles().Where(f => wanted.Contains(f.Name))];
    }

    private static IReadOnlyList<GitItemStatus> ResolveIndexItems(GitModule module, IReadOnlyList<WorkingDirFileRow> rows)
    {
        HashSet<string> wanted = [.. rows.Select(r => r.Path)];
        return [.. module.GetIndexFiles().Where(f => wanted.Contains(f.Name))];
    }

    private static WorkingDirFileRow ToRow(GitItemStatus status, bool isStaged)
        => new(status.Name, DescribeStatus(status), isStaged);

    private static string DescribeStatus(GitItemStatus status)
    {
        if (status.IsNew)
        {
            return "new";
        }

        if (status.IsDeleted)
        {
            return "deleted";
        }

        if (status.IsRenamed)
        {
            return "renamed";
        }

        if (status.IsCopied)
        {
            return "copied";
        }

        if (status.IsUnmerged)
        {
            return "unmerged";
        }

        return "modified";
    }
}
