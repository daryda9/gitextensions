using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The seven repository states the original toolbar's Commit button visualises,
///  a direct port of <c>GitUI.UserControls.RepoStateVisualiser</c>
///  (<c>src/app/GitUI/UserControls/RepoStateVisualiser.cs</c>). The names match the
///  upstream <c>RepoState*</c> PNGs one-to-one, so
///  <see cref="ToolbarStateService.IconFor"/> is a pure name mapping.
/// </summary>
public enum RepoState
{
    /// <summary>Status could not be read (upstream: a null status list).</summary>
    Unknown,

    /// <summary>No changes at all.</summary>
    Clean,

    /// <summary>Unstaged changes to tracked, non-submodule files.</summary>
    Dirty,

    /// <summary>Unstaged changes, and every one of them is a dirty submodule.</summary>
    DirtySubmodules,

    /// <summary>Both staged and unstaged changes.</summary>
    Mixed,

    /// <summary>Staged changes only.</summary>
    Staged,

    /// <summary>Unstaged changes, and every one of them is untracked.</summary>
    UntrackedOnly,
}

/// <summary>
///  The slow-moving, repository-wide facts the toolbar needs in order to show the
///  same enabled/visible/checked state as the original <c>FormBrowse</c> toolbar
///  but cannot compute itself (it performs no git work).
///
///  Written as a record with <c>init</c> properties rather than a positional
///  record on purpose: a positional record's <c>default</c> value ignores the
///  parameter defaults, so <c>default(ToolbarRepoState)</c> would silently mean
///  "invalid working directory, no worktrees, no remotes" — the opposite of the
///  permissive fallback a host wants when it has nothing to say yet.
/// </summary>
public sealed record ToolbarRepoState
{
    /// <summary>Working-directory state driving the Commit button's icon (T5).</summary>
    public RepoState State { get; init; } = RepoState.Unknown;

    /// <summary>Total changed files, i.e. the "Commit (n)" count.</summary>
    public int ChangeCount { get; init; }

    /// <summary>
    ///  Number of stashes for the "Stash (n)" caption (T1). Negative means
    ///  "unknown / do not show a count", which is how the toolbar renders the
    ///  upstream <c>AppSettings.ShowStashCount == false</c> case.
    /// </summary>
    public int StashCount { get; init; } = -1;

    /// <summary>
    ///  Whether the path is a real git working directory. Upstream gates both the
    ///  stash split button and the worktrees button on this.
    /// </summary>
    public bool IsValidWorkingDir { get; init; } = true;

    /// <summary>A bare repository has no working tree, so it cannot be stashed.</summary>
    public bool IsBare { get; init; }

    /// <summary>
    ///  Worktrees attached to this repository. Upstream shows the worktrees button
    ///  only when this is greater than one
    ///  (<c>FormBrowse.InitMenusAndToolbars.cs</c>, <c>UpdateWorktreeToolStripVisibility</c>).
    ///  Negative means "not known yet", which keeps the button visible: hiding a
    ///  working button because nobody has probed yet would be worse than showing one
    ///  that turns out to be redundant.
    /// </summary>
    public int WorktreeCount { get; init; } = -1;

    /// <summary>
    ///  Configured remotes. Upstream hides the drop-down's "Fetch all" entry when
    ///  there is only one (<c>UpdateFetchAllVisibility</c>) — T8. Negative means "not
    ///  known yet" and hides nothing.
    /// </summary>
    public int RemoteCount { get; init; } = -1;

    /// <summary>
    ///  Whether the current branch tracks an upstream branch at all. <c>null</c>
    ///  means "not known yet", and the Push button then infers it from the ahead /
    ///  behind counts it is given.
    /// </summary>
    public bool? HasUpstream { get; init; }

    /// <summary>
    ///  The branch tracks an upstream that no longer exists — git's <c>gone</c>
    ///  marker. The Push button renders it as <see cref="AheadBehindData.GoneSymbol"/>.
    /// </summary>
    public bool UpstreamGone { get; init; }

    /// <summary>
    ///  Whether the git in use understands <c>git stash push --staged</c> (2.35+),
    ///  gating the drop-down's "Stash staged" entry exactly as upstream's
    ///  <c>Module.GitVersion.SupportStashStaged</c> does.
    /// </summary>
    public bool SupportsStashStaged { get; init; } = true;
}

/// <summary>
///  Computes <see cref="ToolbarRepoState"/> for a repository. Every method runs
///  git synchronously and MUST be called off the UI thread (the port's services
///  block sync-over-async internally).
/// </summary>
public sealed class ToolbarStateService
{
    /// <summary>
    ///  Reads every fact <see cref="ToolbarRepoState"/> carries in one pass. Never
    ///  throws: on failure the permissive defaults are returned, so a probe error
    ///  greys nothing out and hides nothing.
    /// </summary>
    public ToolbarRepoState Probe(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return new ToolbarRepoState { IsValidWorkingDir = false };
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);

            bool valid = module.IsValidGitWorkingDir();
            if (!valid)
            {
                return new ToolbarRepoState { IsValidWorkingDir = false };
            }

            bool bare = module.IsBareRepository();

            // Submodule status is included on purpose: without it a repository whose
            // only change is a dirty submodule reads as plain "Dirty" and the
            // DirtySubmodules icon could never appear.
            IReadOnlyList<GitItemStatus>? changed = bare
                ? []
                : SafeStatus(module);

            (RepoState state, int count) = Classify(changed);

            // Upstream skips the stash count for bare repositories (there is nothing
            // to stash), and so does the caption.
            int stashes = bare ? -1 : SafeStashCount(module);

            string branch = SafeBranch(module);
            string upstream = branch.Length == 0 ? string.Empty : SafeUpstream(module, branch);
            bool hasUpstream = upstream.Length > 0;

            return new ToolbarRepoState
            {
                State = state,
                ChangeCount = count,
                StashCount = stashes,
                IsValidWorkingDir = true,
                IsBare = bare,
                WorktreeCount = SafeWorktreeCount(module),
                RemoteCount = SafeRemoteCount(module),
                HasUpstream = hasUpstream,
                UpstreamGone = hasUpstream && !RefExists(module, upstream),
                SupportsStashStaged = SupportsStashStaged(),
            };
        }
        catch
        {
            // A probe must never break a refresh.
            return new ToolbarRepoState();
        }
    }

    /// <summary>
    ///  The <see cref="Theming.IconLoader"/> icon name for a state — the upstream
    ///  <c>RepoState*</c> PNGs, which the app's <c>Assets/Icons</c> glob already
    ///  links from the WinForms resource folder.
    /// </summary>
    public static string IconFor(RepoState state) => state switch
    {
        RepoState.Clean => "RepoStateClean",
        RepoState.Dirty => "RepoStateDirty",
        RepoState.DirtySubmodules => "RepoStateDirtySubmodules",
        RepoState.Mixed => "RepoStateMixed",
        RepoState.Staged => "RepoStateStaged",
        RepoState.UntrackedOnly => "RepoStateUntrackedOnly",
        _ => "RepoStateUnknown",
    };

    /// <summary>
    ///  Classifies a changed-file list exactly as <c>RepoStateVisualiser.Invoke</c>
    ///  does, including its switch order (untracked-only wins over dirty, and
    ///  "every worktree change is a submodule" wins over dirty).
    /// </summary>
    internal static (RepoState State, int Count) Classify(IReadOnlyList<GitItemStatus>? changed)
    {
        if (changed is null)
        {
            return (RepoState.Unknown, 0);
        }

        int indexCount = 0;
        int workTreeSubmodulesCount = 0;
        int notTrackedCount = 0;

        foreach (GitItemStatus status in changed)
        {
            if (status.Staged == StagedStatus.Index)
            {
                indexCount++;
            }

            if (status.Staged == StagedStatus.WorkTree && status.IsSubmodule)
            {
                workTreeSubmodulesCount++;
            }

            if (!status.IsTracked)
            {
                notTrackedCount++;
            }
        }

        int workTreeCount = changed.Count - indexCount;

        RepoState state = (indexCount, workTreeCount) switch
        {
            (0, 0) => RepoState.Clean,
            (0, _) when workTreeCount == notTrackedCount => RepoState.UntrackedOnly,
            (0, _) when workTreeCount != workTreeSubmodulesCount => RepoState.Dirty,
            (0, _) => RepoState.DirtySubmodules,
            (_, 0) => RepoState.Staged,
            (_, _) => RepoState.Mixed,
        };

        return (state, changed.Count);
    }

    private static IReadOnlyList<GitItemStatus>? SafeStatus(GitModule module)
    {
        try
        {
            return module.GetAllChangedFilesWithSubmodulesStatus();
        }
        catch
        {
            // Unknown state (upstream's null status list) rather than a wrong one.
            return null;
        }
    }

    private static int SafeStashCount(GitModule module)
    {
        try
        {
            return module.GetStashes(noLocks: true).Count;
        }
        catch
        {
            return -1;
        }
    }

    private static int SafeWorktreeCount(GitModule module)
    {
        try
        {
            return module.GetWorktrees().Count;
        }
        catch
        {
            // Unknown rather than 1: a failed probe must not hide a working button.
            return -1;
        }
    }

    private static int SafeRemoteCount(GitModule module)
    {
        try
        {
            return module.GetRemoteNames().Count;
        }
        catch
        {
            return -1;
        }
    }

    private static string SafeBranch(GitModule module)
    {
        try
        {
            return module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeUpstream(GitModule module, string branch)
    {
        try
        {
            return module.GetRemoteBranch(branch) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // git's own "does this ref resolve" test. A configured upstream that does not
    // resolve is exactly what for-each-ref reports as "gone".
    private static bool RefExists(GitModule module, string reference)
    {
        try
        {
            GitArgumentBuilder args = new("rev-parse") { "--verify", "--quiet", reference.Quote() };
            return module.GitExecutable.Execute(args, throwOnErrorExit: false).ExitedSuccessfully;
        }
        catch
        {
            // When in doubt, do NOT claim the upstream is gone.
            return true;
        }
    }

    private static bool SupportsStashStaged()
    {
        try
        {
            return GitVersion.Current.SupportStashStaged;
        }
        catch
        {
            // Showing the entry and letting git complain beats hiding a working
            // command because the version probe failed.
            return true;
        }
    }
}
