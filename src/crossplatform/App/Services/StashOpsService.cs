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
}

/// <summary>
///  Result of a mutating stash / cherry-pick / reset operation.
/// </summary>
public sealed record StashOpResult(bool Success, string Output);

/// <summary>
///  Stash, cherry-pick and reset operations implemented by reusing the Git
///  Extensions core (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>.
///  All methods are synchronous and are meant to be called off the UI thread.
/// </summary>
public sealed class StashOpsService
{
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
    /// </summary>
    public StashOpResult StashSave(string repoPath, string message, bool includeUntracked)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = Commands.StashSave(
            untracked: includeUntracked,
            keepIndex: false,
            message: message ?? string.Empty,
            selectedFiles: null);
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

    private static ResetMode MapMode(StashResetMode mode) => mode switch
    {
        StashResetMode.Soft => ResetMode.Soft,
        StashResetMode.Hard => ResetMode.Hard,
        _ => ResetMode.Mixed,
    };
}
