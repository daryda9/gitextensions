using System.Diagnostics;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

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
    IReadOnlyList<WorkingDirFileRow> Unstaged,
    IReadOnlyList<string> Conflicts);

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
            [.. unstaged.Select(f => ToRow(f, isStaged: false))],
            ListConflicts(module));
    }

    /// <summary>
    ///  Lists the conflicted (unmerged) paths in the working directory via
    ///  <c>git diff --name-only --diff-filter=U</c>. Empty when the repository is
    ///  not in a conflicted state (e.g. no merge/rebase in progress).
    /// </summary>
    public IReadOnlyList<string> ListConflicts(string repoPath)
        => ListConflicts(GitContext.CreateModule(repoPath));

    private static IReadOnlyList<string> ListConflicts(GitModule module)
    {
        GitArgumentBuilder args = new("diff")
        {
            "--name-only",
            "--diff-filter=U",
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return [];
        }

        return [.. result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)];
    }

    /// <summary>
    ///  Launches the user's configured merge tool for <paramref name="path"/>
    ///  (<c>git mergetool --no-prompt -- &lt;path&gt;</c>), detached and
    ///  non-blocking so the UI thread is never held while the (interactive) tool
    ///  runs. When no merge tool is configured, git is not launched — instead the
    ///  configured-tool check fails and a descriptive message is returned so the
    ///  view can surface it. Never throws.
    /// </summary>
    public WorkingDirCommitResult LaunchMergetool(string repoPath, string path)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // A detached launch can't capture git's "no tool configured" message,
        // so pre-check the config and surface a message ourselves instead.
        GitArgumentBuilder configArgs = new("config")
        {
            "--get",
            "merge.tool",
        };
        ExecutionResult configResult = module.GitExecutable.Execute(configArgs, throwOnErrorExit: false);
        if (!configResult.ExitedSuccessfully || configResult.StandardOutput.Trim().Length == 0)
        {
            return new WorkingDirCommitResult(false,
                "No merge tool configured. Set one with 'git config merge.tool <tool>' (e.g. vimdiff, meld, kdiff3).");
        }

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                UseShellExecute = false,
                WorkingDirectory = repoPath,
            };
            psi.ArgumentList.Add("mergetool");
            psi.ArgumentList.Add("--no-prompt");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(path);

            Process? proc = Process.Start(psi);
            return proc is null
                ? new WorkingDirCommitResult(false, "Could not start git mergetool.")
                : new WorkingDirCommitResult(true, $"Launched merge tool for {path}.");
        }
        catch (Exception ex)
        {
            return new WorkingDirCommitResult(false, "Could not launch merge tool: " + ex.Message);
        }
    }

    /// <summary>
    ///  Marks a conflicted file as resolved by staging it (<c>git add -- &lt;path&gt;</c>).
    /// </summary>
    public WorkingDirCommitResult MarkResolved(string repoPath, string path)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("add") { "--", path };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  Resolves a conflict by keeping our version (<c>git checkout --ours</c>)
    ///  then staging it.
    /// </summary>
    public WorkingDirCommitResult TakeOurs(string repoPath, string path)
        => TakeSide(repoPath, path, ours: true);

    /// <summary>
    ///  Resolves a conflict by keeping their version (<c>git checkout --theirs</c>)
    ///  then staging it.
    /// </summary>
    public WorkingDirCommitResult TakeTheirs(string repoPath, string path)
        => TakeSide(repoPath, path, ours: false);

    private static WorkingDirCommitResult TakeSide(string repoPath, string path, bool ours)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        GitArgumentBuilder checkoutArgs = new("checkout")
        {
            ours ? "--ours" : "--theirs",
            "--",
            path,
        };
        ExecutionResult checkout = module.GitExecutable.Execute(checkoutArgs, throwOnErrorExit: false);
        if (!checkout.ExitedSuccessfully)
        {
            return new WorkingDirCommitResult(false, checkout.AllOutput);
        }

        GitArgumentBuilder addArgs = new("add") { "--", path };
        ExecutionResult add = module.GitExecutable.Execute(addArgs, throwOnErrorExit: false);
        return new WorkingDirCommitResult(add.ExitedSuccessfully, add.AllOutput);
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

    /// <summary>
    ///  Discards uncommitted modifications to TRACKED files. Destructive — callers
    ///  MUST confirm first. Never touches untracked files (that is what
    ///  <see cref="Clean"/> is for): a hard reset and <c>git checkout -- .</c> both
    ///  leave files git is not tracking alone.
    ///  <para>
    ///  When <paramref name="includeStaged"/> is <see langword="true"/> (the primary
    ///  action) this resets both the work tree and the index to <c>HEAD</c> via
    ///  <c>git reset --hard HEAD</c>, so staged and unstaged tracked changes are all
    ///  discarded. When <see langword="false"/>, only the work tree is reverted to
    ///  the index (<c>git checkout -- .</c>), leaving anything already staged intact.
    ///  </para>
    /// </summary>
    public WorkingDirCommitResult ResetChanges(string repoPath, bool includeStaged)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        if (includeStaged)
        {
            // reset --hard HEAD: discard tracked worktree + index changes, but never
            // untracked files. Uses the core command builder (as UndoLastCommit does).
            ArgumentString args = Commands.Reset(ResetMode.Hard, "HEAD");
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
        }

        // Work-tree only: restore tracked files from the index, leaving staged
        // changes and untracked files untouched.
        GitArgumentBuilder checkoutArgs = new("checkout") { "--", "." };
        ExecutionResult checkout = module.GitExecutable.Execute(checkoutArgs, throwOnErrorExit: false);
        return new WorkingDirCommitResult(checkout.ExitedSuccessfully, checkout.AllOutput);
    }

    /// <summary>
    ///  Discards uncommitted modifications to a SINGLE tracked file
    ///  (<c>git checkout -- &lt;path&gt;</c>), reverting its work-tree content to the
    ///  index. Destructive — callers MUST confirm first. Does not remove untracked
    ///  files. Never throws: git errors are returned in the result.
    /// </summary>
    public WorkingDirCommitResult ResetFile(string repoPath, string path)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("checkout") { "--", path };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  Appends <paramref name="pattern"/> as a new line to <c>&lt;repo&gt;/.gitignore</c>,
    ///  creating the file when it does not exist. This is a plain file append — no git
    ///  command is involved. Duplicate lines (matching an existing trimmed line) are
    ///  skipped, and a trailing newline is always ensured so the pattern lands on its
    ///  own line. Never throws: I/O failures are returned in the result.
    /// </summary>
    public WorkingDirCommitResult AddToGitignore(string repoPath, string pattern)
    {
        pattern = (pattern ?? string.Empty).Trim();
        if (pattern.Length == 0)
        {
            return new WorkingDirCommitResult(false, "Empty ignore pattern.");
        }

        try
        {
            string gitignore = System.IO.Path.Combine(repoPath, ".gitignore");
            string existing = File.Exists(gitignore) ? File.ReadAllText(gitignore) : string.Empty;

            // Dedupe against existing trimmed lines so re-adding the same pattern is a no-op.
            bool alreadyPresent = existing
                .Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .Any(l => l == pattern);
            if (alreadyPresent)
            {
                return new WorkingDirCommitResult(true, $"'{pattern}' is already in .gitignore.");
            }

            // Ensure the existing content ends with a newline before appending, so the
            // new pattern is on its own line; always end the file with a trailing newline.
            System.Text.StringBuilder sb = new(existing);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
            {
                sb.Append('\n');
            }

            sb.Append(pattern).Append('\n');
            File.WriteAllText(gitignore, sb.ToString());
            return new WorkingDirCommitResult(true, $"Added '{pattern}' to .gitignore.");
        }
        catch (Exception ex)
        {
            return new WorkingDirCommitResult(false, "Could not update .gitignore: " + ex.Message);
        }
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
