using System.Diagnostics;
using System.Text;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Configurations;
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
///  The full option set of upstream's <c>FormCleanupRepository</c>, in one value:
///  which class of files to remove (<see cref="Mode"/> → <c>-x</c> / nothing /
///  <c>-X</c>), whether untracked directories go too (<c>-d</c>), whether the
///  same clean is repeated in every submodule, and the include / exclude path
///  filters.
///  <para>
///  <see cref="IncludePaths"/> and <see cref="ExcludePaths"/> are the raw,
///  multi-line text of the two filter boxes; empty lines are dropped.
///  Include lines become quoted pathspecs, exclude lines become
///  <c>--exclude=&lt;line&gt;</c> (spaces turned into the <c>?</c> wildcard, as
///  upstream does, because <c>git clean</c> takes one exclude pattern per option
///  and cannot see through a space).
///  </para>
/// </summary>
public sealed record CleanOptions(
    CleanMode Mode = CleanMode.OnlyNonIgnored,
    bool Directories = true,
    bool CleanSubmodules = false,
    string? IncludePaths = null,
    string? ExcludePaths = null)
{
    /// <summary>Include lines as a single quoted pathspec argument, or null when the filter is unused.</summary>
    public string? IncludeArgument => JoinNonEmptyLines(IncludePaths, line => line.Quote());

    /// <summary>Exclude lines as <c>--exclude=</c> options, or null when the filter is unused.</summary>
    public string? ExcludeArgument => JoinNonEmptyLines(ExcludePaths, line => $"--exclude={line.Replace(" ", "?")}".ToPosixPath());

    private static string? JoinNonEmptyLines(string? text, Func<string, string> project)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string joined = string.Join(
            ' ',
            text.Split('\n')
                .Select(line => line.Trim('\r', ' ', '\t'))
                .Where(line => line.Length > 0)
                .Select(project));

        return joined.Length == 0 ? null : joined;
    }
}

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
        // Quoted: GitArgumentBuilder flattens its arguments into a single command
        // line and git re-splits them, so a path containing a space would arrive as
        // two arguments and the resolve would silently fail.
        GitArgumentBuilder args = new("add") { "--", path.Quote() };
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
            path.Quote(),
        };
        ExecutionResult checkout = module.GitExecutable.Execute(checkoutArgs, throwOnErrorExit: false);
        if (!checkout.ExitedSuccessfully)
        {
            return new WorkingDirCommitResult(false, checkout.AllOutput);
        }

        GitArgumentBuilder addArgs = new("add") { "--", path.Quote() };
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
    ///  The "Submodules … updated" message upstream's
    ///  <c>generateListOfChangesInSubmodulesChangesToolStripMenuItem</c> composes: for every
    ///  STAGED submodule, the commits the pointer moved over, taken from the submodule's own
    ///  log. Empty when nothing staged is a submodule.
    /// </summary>
    /// <param name="repoPath">The super-project.</param>
    /// <param name="stagedPaths">Paths of the staged entries, as the index reports them.</param>
    public string SubmoduleChangesMessage(string repoPath, IReadOnlyList<string> stagedPaths)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ISubmodulesConfigFile config = module.GetSubmodulesConfigFile();

        // Keyed by the .gitmodules SUBSECTION (the submodule's name, which is what the
        // message reads) and valued by its path in the work tree, which is what git wants.
        Dictionary<string, string> modules = [];
        foreach (string path in stagedPaths)
        {
            IConfigSection? section = config.ConfigSections
                .FirstOrDefault(s => string.Equals(s.GetValue("path").Trim(), path, StringComparison.Ordinal));
            if (section?.SubSection is { } name
                && Directory.Exists(Path.Combine(module.WorkingDir, path))
                && !modules.ContainsKey(name.Trim()))
            {
                modules[name.Trim()] = path;
            }
        }

        if (modules.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        text.AppendLine("Submodule" + (modules.Count == 1 ? " " : "s ") + string.Join(", ", modules.Keys) + " updated");
        text.AppendLine();

        foreach ((string name, string path) in modules)
        {
            GitArgumentBuilder diffArgs = new("diff") { "--no-ext-diff", "--cached", "--", path.Quote() };
            string diff = module.GitExecutable.GetOutput(diffArgs);

            const string Marker = "Subproject commit ";
            string from = LineAfter(diff, "-" + Marker);
            string to = LineAfter(diff, "+" + Marker);
            if (from.Length == 0 || to.Length == 0)
            {
                continue;
            }

            text.AppendLine("Submodule " + name + ":");

            // %x20, never a literal space: GitArgumentBuilder concatenates everything into
            // ONE command line, so a space inside --pretty=format: splits the argument.
            GitArgumentBuilder logArgs = new("log")
            {
                "--pretty=format:%x20%x20%x20%x20%m%x20%h%x20-%x20%s",
                "--no-merges",
                $"{from}...{to}".Quote()
            };
            GitModule submodule = GitContext.CreateModule(Path.Combine(module.WorkingDir, path));
            string log = submodule.GitExecutable.GetOutput(logArgs);
            text.AppendLine(log.Length > 0 ? log : "    * Revision changed to " + to[..Math.Min(7, to.Length)]);
            text.AppendLine();
        }

        return text.ToString().TrimEnd() + Environment.NewLine;

        static string LineAfter(string diff, string prefix)
        {
            foreach (string line in diff.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line[prefix.Length..].Trim();
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    ///  The messages of the last <paramref name="count"/> commits of HEAD, newest first,
    ///  for the commit dialog's "Commit message" drop-down (upstream's
    ///  <c>commitMessageToolStripMenuItem</c>, which reads the same
    ///  <c>GetPreviousCommitMessages</c>). <paramref name="authorPattern"/> is the regular
    ///  expression git matches the author against, empty for every author. Blank messages
    ///  are dropped, and a repository with no commits yet simply yields nothing.
    /// </summary>
    public IReadOnlyList<string> PreviousCommitMessages(string repoPath, int count, string authorPattern)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        return [.. module.GetPreviousCommitMessages(count, "HEAD", authorPattern)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message!.TrimEnd('\n'))];
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
    ///  Builds the <c>git clean</c> argument string for <paramref name="options"/>
    ///  (upstream <c>FormCleanupRepository.CleanUp</c>). With
    ///  <paramref name="dryRun"/> the command carries <c>--dry-run</c> and deletes
    ///  nothing; without it, <c>-f</c>.
    ///  <para>
    ///  Returned as a string rather than executed so the caller can stream it through
    ///  <see cref="GitStreamRunner"/> — <c>git clean</c> prints one line per entry and
    ///  the dialog shows them as they arrive. Pure string work: safe on any thread.
    ///  </para>
    /// </summary>
    public static string CleanArguments(CleanOptions options, bool dryRun)
        => Commands.Clean(
            options.Mode,
            dryRun,
            directories: options.Directories,
            paths: options.IncludeArgument,
            excludes: options.ExcludeArgument).ToString();

    /// <summary>
    ///  Builds the <c>git submodule foreach --recursive git clean …</c> argument
    ///  string that repeats the same clean inside every submodule (upstream's
    ///  "Clean submodules" checkbox). The exclude filter is deliberately not passed
    ///  on — upstream's <c>Commands.CleanSubmodules</c> does not take one either,
    ///  since the patterns are relative to the super-project.
    /// </summary>
    public static string CleanSubmodulesArguments(CleanOptions options, bool dryRun)
        => Commands.CleanSubmodules(
            options.Mode,
            dryRun,
            directories: options.Directories,
            paths: options.IncludeArgument).ToString();

    /// <summary>
    ///  Discards uncommitted modifications to TRACKED files, and — only when
    ///  <paramref name="cleanUntracked"/> is set — removes untracked files too.
    ///  Destructive: callers MUST go through <c>ResetChangesDialog</c> first, which is
    ///  also where the untracked decision is made (upstream
    ///  <c>GitUICommands.StartResetChangesDialog</c> / <c>GitModule.ResetAllChanges</c>).
    ///  <para>
    ///  When <paramref name="includeStaged"/> is <see langword="true"/> ("Reset all
    ///  changes") this resets both the work tree and the index to <c>HEAD</c> via
    ///  <c>git reset --hard HEAD</c>. When <see langword="false"/> ("Reset unstaged
    ///  changes"), only the work tree is reverted to the index, leaving anything
    ///  already staged intact.
    ///  </para>
    ///  <para>
    ///  The work-tree revert names its paths EXPLICITLY instead of upstream's
    ///  <c>git checkout -- .</c>: a bare <c>.</c> means "below the current directory",
    ///  which is only equal to the whole repository as long as the process happens to
    ///  sit at its root. <paramref name="trackedPaths"/> are repository-relative and
    ///  come from the rows the dialog counted, so what runs is what the user was
    ///  shown. <c>.</c> remains the fallback when no list is supplied or the list is
    ///  too long for one command line.
    ///  </para>
    /// </summary>
    /// <param name="cleanUntracked">Also run <c>git clean -f -d</c> afterwards.</param>
    /// <param name="trackedPaths">Repository-relative paths of the tracked rows to revert.</param>
    public WorkingDirCommitResult ResetChanges(
        string repoPath,
        bool includeStaged,
        bool cleanUntracked = false,
        IReadOnlyList<string>? trackedPaths = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        WorkingDirCommitResult result = includeStaged
            ? ResetHard(module)
            : RevertWorkTree(module, trackedPaths);

        if (!result.Success || !cleanUntracked)
        {
            return result;
        }

        // Upstream does the same second step (GitModule.ResetAllChanges): the reset
        // itself can never remove a file git is not tracking.
        WorkingDirCommitResult cleaned = Clean(repoPath);
        return new WorkingDirCommitResult(
            cleaned.Success,
            Join(result.Output, cleaned.Output));
    }

    private static WorkingDirCommitResult ResetHard(GitModule module)
    {
        // reset --hard HEAD: discard tracked worktree + index changes. Uses the core
        // command builder (as UndoLastCommit does).
        ArgumentString args = Commands.Reset(ResetMode.Hard, "HEAD");
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>Maximum paths named on one <c>git checkout</c> command line.</summary>
    private const int MaxExplicitPaths = 400;

    private static WorkingDirCommitResult RevertWorkTree(GitModule module, IReadOnlyList<string>? trackedPaths)
    {
        GitArgumentBuilder args = new("checkout") { "--" };
        if (trackedPaths is { Count: > 0 } and { Count: <= MaxExplicitPaths })
        {
            foreach (string path in trackedPaths)
            {
                args.Add(path.Quote());
            }
        }
        else
        {
            args.Add(".");
        }

        ExecutionResult checkout = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorkingDirCommitResult(checkout.ExitedSuccessfully, checkout.AllOutput);
    }

    private static string Join(string? first, string? second)
    {
        string a = (first ?? string.Empty).Trim();
        string b = (second ?? string.Empty).Trim();
        return a.Length == 0 ? b : b.Length == 0 ? a : a + "\n" + b;
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
    ///  Renames / moves ONE file with <c>git mv</c>, so the move is recorded in the
    ///  index instead of showing up as a delete plus an untracked file. Creates the
    ///  destination directory first: <c>git mv</c> refuses to create it itself.
    ///  Never throws.
    /// </summary>
    public WorkingDirCommitResult MoveFile(string repoPath, string path, string newPath)
    {
        newPath = (newPath ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (newPath.Length == 0)
        {
            return new WorkingDirCommitResult(false, "Empty destination path.");
        }

        try
        {
            string full = System.IO.Path.Combine(repoPath, newPath);
            string? parent = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            GitModule module = GitContext.CreateModule(repoPath);
            GitArgumentBuilder args = new("mv") { "--", path, newPath };
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
        }
        catch (Exception ex)
        {
            return new WorkingDirCommitResult(false, "Could not move the file: " + ex.Message);
        }
    }

    /// <summary>
    ///  Deletes ONE file. A TRACKED file goes through <c>git rm -f</c>, so the
    ///  deletion is staged as well; an UNTRACKED one is simply removed from disk,
    ///  which is all git could do with it anyway.
    ///  Destructive — callers MUST confirm first. Never throws.
    /// </summary>
    public WorkingDirCommitResult DeleteFile(string repoPath, string path, bool tracked)
    {
        try
        {
            if (!tracked)
            {
                string full = System.IO.Path.Combine(
                    repoPath,
                    path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    File.Delete(full);
                }

                return new WorkingDirCommitResult(true, $"Deleted '{path}'.");
            }

            GitModule module = GitContext.CreateModule(repoPath);
            GitArgumentBuilder args = new("rm") { "-f", "--", path };
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
        }
        catch (Exception ex)
        {
            return new WorkingDirCommitResult(false, "Could not delete the file: " + ex.Message);
        }
    }

    /// <summary>
    ///  Copies the WORK-TREE version of a file to <paramref name="destination"/> —
    ///  the original's "Save selected as...", which for the commit form's lists is
    ///  always the on-disk version (there is no revision to extract). Never throws.
    /// </summary>
    public WorkingDirCommitResult SaveFileAs(string repoPath, string path, string destination)
    {
        try
        {
            string full = System.IO.Path.Combine(
                repoPath,
                path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                return new WorkingDirCommitResult(false, $"'{path}' does not exist in the working directory.");
            }

            File.Copy(full, destination, overwrite: true);
            return new WorkingDirCommitResult(true, $"Saved '{path}' to '{destination}'.");
        }
        catch (Exception ex)
        {
            return new WorkingDirCommitResult(false, "Could not save the file: " + ex.Message);
        }
    }

    /// <summary>
    ///  Restores a SINGLE tracked file from <c>HEAD</c> (<c>git checkout HEAD --
    ///  &lt;path&gt;</c>), which unlike <see cref="ResetFile"/> also rewrites the
    ///  file's index entry: any staged change to it is dropped too. This is the
    ///  "parent" half of the original's "Reset file(s) to" submenu, whose other half
    ///  ("index") is <see cref="ResetFile"/>.
    ///  Destructive — callers MUST confirm first. Never throws.
    /// </summary>
    public WorkingDirCommitResult ResetFileToHead(string repoPath, string path)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("checkout") { "HEAD", "--", path };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  Appends a pattern to <c>.git/info/exclude</c> — the same thing
    ///  <see cref="AddToGitignore"/> does, but to the repository-local, never
    ///  committed ignore list. Uses the module's own git directory, so it lands in
    ///  the right place inside a worktree or a submodule.
    /// </summary>
    public WorkingDirCommitResult AddToInfoExclude(string repoPath, string pattern)
    {
        pattern = (pattern ?? string.Empty).Trim();
        if (pattern.Length == 0)
        {
            return new WorkingDirCommitResult(false, "Empty ignore pattern.");
        }

        try
        {
            string gitDir = GitContext.CreateModule(repoPath).WorkingDirGitDir;
            string infoDir = System.IO.Path.Combine(gitDir, "info");
            Directory.CreateDirectory(infoDir);
            string file = System.IO.Path.Combine(infoDir, "exclude");
            string existing = File.Exists(file) ? File.ReadAllText(file) : string.Empty;

            if (existing.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Any(l => l == pattern))
            {
                return new WorkingDirCommitResult(true, $"'{pattern}' is already in .git/info/exclude.");
            }

            System.Text.StringBuilder sb = new(existing);
            if (existing.Length > 0 && !existing.EndsWith('\n'))
            {
                sb.Append('\n');
            }

            sb.Append(pattern).Append('\n');
            File.WriteAllText(file, sb.ToString());
            return new WorkingDirCommitResult(true, $"Added '{pattern}' to .git/info/exclude.");
        }
        catch (Exception ex)
        {
            return new WorkingDirCommitResult(false, "Could not update .git/info/exclude: " + ex.Message);
        }
    }

    /// <summary>
    ///  Sets or clears the index bit that makes git ignore local changes to a file:
    ///  <c>--skip-worktree</c> (the sparse-checkout bit, the one to use for a file
    ///  that must keep local edits) or <c>--assume-unchanged</c> (a promise to git
    ///  that the file will not change, purely a stat-cache optimisation).
    /// </summary>
    public WorkingDirCommitResult SetIndexFlag(string repoPath, string path, bool skipWorktree, bool on)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string flag = (skipWorktree, on) switch
        {
            (true, true) => "--skip-worktree",
            (true, false) => "--no-skip-worktree",
            (false, true) => "--assume-unchanged",
            (false, false) => "--no-assume-unchanged",
        };
        GitArgumentBuilder args = new("update-index") { flag, "--", path };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorkingDirCommitResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  The files currently hidden by one of the two bits above. <c>git ls-files -v</c>
    ///  reports <c>S</c> for skip-worktree and a LOWER-CASE tag (<c>h</c>) for
    ///  assume-unchanged; both make the file vanish from <c>git status</c>, and so
    ///  from this dialog's lists, which is why the UI needs a way to list them back.
    /// </summary>
    public IReadOnlyList<string> ListHiddenByIndexFlag(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            GitArgumentBuilder args = new("ls-files") { "-v" };
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return [];
            }

            List<string> hidden = [];
            foreach (string line in (result.StandardOutput ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length < 3 || trimmed[1] != ' ')
                {
                    continue;
                }

                char tag = trimmed[0];
                if (tag == 'S' || char.IsLower(tag))
                {
                    hidden.Add(trimmed[2..]);
                }
            }

            return hidden;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    ///  Clears BOTH bits on every path returned by
    ///  <see cref="ListHiddenByIndexFlag"/>, bringing the files back into the lists.
    /// </summary>
    public WorkingDirCommitResult RestoreHiddenByIndexFlag(string repoPath)
    {
        IReadOnlyList<string> hidden = ListHiddenByIndexFlag(repoPath);
        if (hidden.Count == 0)
        {
            return new WorkingDirCommitResult(true, "No skipped or assumed-unchanged files.");
        }

        GitModule module = GitContext.CreateModule(repoPath);
        foreach (string path in hidden)
        {
            foreach (string flag in new[] { "--no-skip-worktree", "--no-assume-unchanged" })
            {
                GitArgumentBuilder args = new("update-index") { flag, "--", path };
                ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
                if (!result.ExitedSuccessfully)
                {
                    return new WorkingDirCommitResult(false, result.AllOutput);
                }
            }
        }

        return new WorkingDirCommitResult(true, $"Restored {hidden.Count} file(s).");
    }

    /// <summary>
    ///  Opens ONE working-directory file in the configured external difftool, the way
    ///  the shared file-list menu's difftool block does for a revision.
    ///
    ///  <para>Which two sides are compared follows the list the file was picked from:
    ///  an unstaged row is <c>index → work tree</c> (git's own defaults), a staged row
    ///  is <c>HEAD → index</c>. The launch is detached, so the tool stays open and the
    ///  app never waits on it.</para>
    ///
    ///  <para>Fails with a friendly message rather than silently doing nothing when no
    ///  difftool is configured: a detached difftool with no tool set writes its error
    ///  to a console nobody sees.</para>
    /// </summary>
    public WorkingDirCommitResult LaunchDifftool(string repoPath, string path, bool staged, bool isTracked)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);

            bool hasTool =
                !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("diff.guitool")) ||
                !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("diff.tool")) ||
                !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("merge.guitool")) ||
                !string.IsNullOrWhiteSpace(module.GetEffectiveSetting("merge.tool"));
            if (!hasTool)
            {
                return new WorkingDirCommitResult(
                    false,
                    "No external difftool is configured. Set one with e.g. "
                    + "\"git config --global diff.tool <tool>\".");
            }

            if (staged)
            {
                module.OpenWithDifftool(
                    filename: path,
                    firstRevision: "HEAD",
                    secondRevision: GitUIPluginInterfaces.GitRevision.IndexGuid,
                    isTracked: isTracked);
            }
            else
            {
                // Defaults are index → work tree, which is exactly the unstaged diff.
                module.OpenWithDifftool(filename: path, isTracked: isTracked);
            }

            return new WorkingDirCommitResult(true, string.Empty);
        }
        catch (Exception ex)
        {
            return new WorkingDirCommitResult(false, "Could not start the difftool: " + ex.Message);
        }
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
