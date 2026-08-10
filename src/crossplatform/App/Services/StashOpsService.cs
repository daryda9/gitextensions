using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Reset modes exposed by <see cref="StashOpsService"/>, mirroring the subset of
///  the core <see cref="ResetMode"/> that is meaningful for a commit-targeted
///  reset from the revision grid. Kept as a local enum so callers in the Avalonia
///  layer do not need to reference the core enum directly.
/// </summary>
public enum StashResetMode
{
    Soft,
    Mixed,
    Hard,
}

/// <summary>
///  A single stash entry, projected for display in the Avalonia stash panel.
///  Independent of the WinForms core UI types.
/// </summary>
public sealed record StashRow(int Index, string Name, string Message)
{
    public string Display => $"{Name}: {Message}";

    public override string ToString() => Display;
}

/// <summary>
///  Result of a mutating stash / cherry-pick / reset operation.
/// </summary>
public sealed record StashOpResult(bool Success, string Output);

/// <summary>
///  The changed files of the working directory, split the way the upstream stash
///  dialog splits them for its "Current working directory changes" entry: the
///  staged ones under <c>Index</c> and everything else under <c>Workspace</c>
///  (<c>FormStash.LoadGitItemStatuses</c>, which calls <c>SetStashDiffs</c> with
///  those two groups).
/// </summary>
public sealed record StashWorkingDirFiles(
    IReadOnlyList<DiffFileRow> Index,
    IReadOnlyList<DiffFileRow> WorkTree);

/// <summary>
///  Stash, cherry-pick and reset operations implemented by reusing the Git
///  Extensions core (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>.
///  All methods are synchronous and are meant to be called off the UI thread.
/// </summary>
public sealed class StashOpsService
{
    /// <summary>
    ///  Whether a stash the USER asked for takes untracked files with it
    ///  (<c>AppSettings.IncludeUntrackedFilesInManualStash</c>). One place for it: the
    ///  five manual stash sites of the port each hard-coded an answer, and two of them
    ///  disagreed with the other three.
    /// </summary>
    public static bool ManualStashUntracked()
        => new SettingsService().Load().IncludeUntrackedFilesInManualStash;

    /// <summary>
    ///  The same for a stash the app makes on the user's behalf before a checkout
    ///  (<c>AppSettings.IncludeUntrackedFilesInAutoStash</c>). A caller that passes a
    ///  non-null <paramref name="explicitChoice"/> overrides the setting — nobody does
    ///  today, and the parameter exists so a future caller with a reason can.
    /// </summary>
    public static bool AutoStashUntracked(bool? explicitChoice = null)
        => explicitChoice ?? new SettingsService().Load().IncludeUntrackedFilesInAutoStash;

    /// <summary>
    ///  Lists the current stashes (most recent first, i.e. lowest index first).
    /// </summary>
    public IReadOnlyList<StashRow> ListStashes(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        IReadOnlyList<GitStash> stashes = module.GetStashes();
        return [.. stashes.Select(s => new StashRow(s.Index, s.Name, s.Message))];
    }

    /// <summary>
    ///  Saves the current working-directory changes to a new stash. Uses the core
    ///  <see cref="Commands.StashSave"/> builder.
    ///
    ///  <para><paramref name="keepIndex"/> is upstream's <c>StashKeepIndex</c>
    ///  checkbox (<c>--keep-index</c>) and <paramref name="selectedFiles"/> its
    ///  "Stash selected changes" button, which passes the paths picked in the file
    ///  list (<c>FormStash.StashSelectedFiles_Click</c>). A non-empty file list
    ///  makes the core builder emit <c>stash push -- &lt;paths&gt;</c> instead of
    ///  <c>stash save</c>.</para>
    /// </summary>
    public StashOpResult StashSave(
        string repoPath,
        string message,
        bool includeUntracked,
        bool keepIndex = false,
        IReadOnlyList<string>? selectedFiles = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = Commands.StashSave(
            untracked: includeUntracked,
            keepIndex: keepIndex,
            message: message ?? string.Empty,
            selectedFiles: selectedFiles);
        return Run(module, args);
    }

    /// <summary>
    ///  Lists the files a stash touches, the middle pane of the upstream dialog
    ///  (<c>Module.GetStashDiffFiles</c>, <c>FormStash.cs:186</c>): the tracked
    ///  changes of <c>&lt;ref&gt;^..&lt;ref&gt;</c> plus the untracked files that
    ///  were stashed along with them, which live in the third parent
    ///  (<c>&lt;ref&gt;^3</c>).
    ///
    ///  <para>Those third-parent entries come back with
    ///  <see cref="DiffFileRow.IsTracked"/> <see langword="false"/>. That is not
    ///  cosmetic: their patch cannot be produced from <c>&lt;ref&gt;^..&lt;ref&gt;</c>
    ///  — they are in neither tree — so the caller has to diff against
    ///  <c>&lt;ref&gt;^3</c> instead, and the flag is how it tells them apart.</para>
    /// </summary>
    public IReadOnlyList<DiffFileRow> GetStashFiles(string repoPath, string stashRef)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        HashSet<string> untracked = UntrackedStashNames(module, stashRef);

        List<DiffFileRow> rows = [];
        foreach (GitItemStatus item in module.GetStashDiffFiles(stashRef))
        {
            rows.Add(ToRow(item, isTracked: !untracked.Contains(item.Name) && item.IsTracked));
        }

        return rows;
    }

    /// <summary>
    ///  The current working-directory changes, split into the staged (Index) and
    ///  unstaged (Workspace) groups — what the upstream dialog shows for its
    ///  synthetic "Current working directory changes" entry.
    /// </summary>
    public StashWorkingDirFiles GetWorkingDirFiles(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        List<DiffFileRow> index = [];
        List<DiffFileRow> workTree = [];
        foreach (GitItemStatus item in module.GetAllChangedFiles())
        {
            (item.Staged == StagedStatus.Index ? index : workTree).Add(ToRow(item, item.IsTracked));
        }

        return new StashWorkingDirFiles(index, workTree);
    }

    /// <summary>
    ///  The patch of a single working-directory file. Unlike the stash and commit
    ///  cases this cannot go through <c>DiffTextService</c>: the two comparisons
    ///  involved — HEAD against the index (<c>--cached</c>) and the index against
    ///  the work tree — name no revision, and <c>DiffTextRequest</c> always does.
    ///
    ///  <para>An untracked file is in neither side of any such comparison, so it is
    ///  shown as an all-added patch against the empty file.</para>
    /// </summary>
    public string GetWorkingDirFileDiff(string repoPath, DiffFileRow file, bool staged)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        GitArgumentBuilder args;
        if (!file.IsTracked && !staged)
        {
            args = new GitArgumentBuilder("diff")
            {
                "--no-color",
                "--no-index",
                "--",
                "/dev/null",
                file.Name.Quote(),
            };
        }
        else
        {
            args = new GitArgumentBuilder("diff")
            {
                "--no-color",
                "--find-renames",
                { staged, "--cached" },
                "--",
                file.Name.Quote(),
                { !string.IsNullOrEmpty(file.OldName) && file.OldName != file.Name, (file.OldName ?? string.Empty).Quote() },
            };
        }

        // --no-index exits 1 whenever the two sides differ, so the exit code says
        // nothing about success here: the output is the answer either way.
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return result.AllOutput;
    }

    /// <summary>
    ///  Saves the current working-directory changes to a new stash with an explicit
    ///  message, optionally including untracked files. Runs
    ///  <c>git stash push [-u] -m &lt;message&gt;</c> directly so the exact command
    ///  is predictable regardless of the core builder's flag ordering.
    /// </summary>
    public StashOpResult StashSaveMessage(string repoPath, string message, bool includeUntracked)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("stash")
        {
            "push",
            { includeUntracked, "-u" },
            { !string.IsNullOrWhiteSpace(message), "-m" },
            { !string.IsNullOrWhiteSpace(message), message.Quote() },
        };
        return Run(module, args);
    }

    /// <summary>
    ///  Returns the full unified patch for the given stash via
    ///  <c>git stash show -p &lt;ref&gt;</c>. On failure the git output is returned
    ///  as-is so the caller can surface it.
    /// </summary>
    public string GetStashDiff(string repoPath, string stashRef)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("stash") { "show", "-p", stashRef };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return result.AllOutput;
    }

    /// <summary>
    ///  Stashes ONLY the staged (index) changes, leaving unstaged / working-tree
    ///  changes in place. Uses <c>git stash push --staged [-m &lt;message&gt;]</c>
    ///  (requires git 2.35+, which introduced <c>--staged</c>). There is no core
    ///  builder for this, so raw git is run just like apply/pop/drop.
    /// </summary>
    public StashOpResult StashStaged(string repoPath, string message)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("stash")
        {
            "push",
            "--staged",
            { !string.IsNullOrWhiteSpace(message), "-m" },
            { !string.IsNullOrWhiteSpace(message), message.Quote() },
        };
        return Run(module, args);
    }

    /// <summary>
    ///  Applies the given stash, keeping it in the stash list.
    /// </summary>
    public StashOpResult StashApply(string repoPath, string name)
        => RunStash(repoPath, "apply", name);

    /// <summary>
    ///  Applies the given stash and removes it from the stash list.
    /// </summary>
    public StashOpResult StashPop(string repoPath, string name)
        => RunStash(repoPath, "pop", name);

    /// <summary>
    ///  Drops (deletes) the given stash without applying it.
    /// </summary>
    public StashOpResult StashDrop(string repoPath, string name)
        => RunStash(repoPath, "drop", name);

    /// <summary>
    ///  Cherry-picks the commit identified by <paramref name="commitHash"/> onto
    ///  the current branch, committing the result. Uses the core
    ///  <see cref="Commands.CherryPick"/> builder.
    /// </summary>
    public StashOpResult CherryPick(string repoPath, string commitHash)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ObjectId commitId = ObjectId.Parse(commitHash);
        ArgumentString args = Commands.CherryPick(commitId, commit: true, arguments: string.Empty);
        return Run(module, args);
    }

    /// <summary>
    ///  Resets the current branch to <paramref name="commitHash"/> using the given
    ///  mode. Uses the core <see cref="Commands.Reset"/> builder.
    /// </summary>
    public StashOpResult Reset(string repoPath, string commitHash, StashResetMode mode)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = Commands.Reset(MapMode(mode), commitHash);
        return Run(module, args);
    }

    // No core builders exist for stash apply/pop/drop, so run raw git.
    private StashOpResult RunStash(string repoPath, string verb, string name)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("stash") { verb, name };
        return Run(module, args);
    }

    private static StashOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new StashOpResult(result.ExitedSuccessfully, result.AllOutput);
    }

    // The names of the untracked files carried by a stash. They are not part of
    // <ref>^..<ref>: git parks them in the stash's third parent, whose tree is
    // read here the same way the core's GetStashDiffFiles reads it. A stash made
    // without -u has no third parent, and "log <ref>^3" simply fails.
    private static HashSet<string> UntrackedStashNames(GitModule module, string stashRef)
    {
        GitArgumentBuilder args = new("log")
        {
            $"{stashRef}^3".Quote(),
            "--pretty=format:%T",
            "--max-count=1",
        };

        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully ||
            !ObjectId.TryParse(result.StandardOutput.Trim(), out ObjectId treeId))
        {
            return [];
        }

        return [.. module.GetTreeFiles(treeId, full: true).Select(item => item.Name)];
    }

    private static DiffFileRow ToRow(GitItemStatus item, bool isTracked)
        => new(item.Name, item.OldName, MapKind(item), isTracked);

    private static DiffChangeKind MapKind(GitItemStatus item)
    {
        if (item.IsNew)
        {
            return DiffChangeKind.Added;
        }

        if (item.IsDeleted)
        {
            return DiffChangeKind.Deleted;
        }

        if (item.IsRenamed)
        {
            return DiffChangeKind.Renamed;
        }

        if (item.IsCopied)
        {
            return DiffChangeKind.Copied;
        }

        return DiffChangeKind.Modified;
    }

    private static ResetMode MapMode(StashResetMode mode) => mode switch
    {
        StashResetMode.Soft => ResetMode.Soft,
        StashResetMode.Hard => ResetMode.Hard,
        _ => ResetMode.Mixed,
    };
}
