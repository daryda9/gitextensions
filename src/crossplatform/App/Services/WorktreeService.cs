using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single linked-worktree entry, projected for display in the Avalonia
///  repo-objects tree. Parsed from <c>git worktree list --porcelain</c>.
/// </summary>
/// <param name="Path">Absolute path of the worktree's working directory.</param>
/// <param name="Head">Abbreviated commit the worktree is checked out at, or empty when bare.</param>
/// <param name="Branch">
///  Short branch name the worktree is on (e.g. <c>main</c>), or empty when the
///  worktree is detached or bare.
/// </param>
/// <param name="IsBare">True for the bare main worktree.</param>
/// <param name="IsDetached">True when the worktree is on a detached HEAD.</param>
/// <param name="IsMain">
///  True for the MAIN worktree — the first record git reports. It owns the
///  repository and can never be removed with <c>git worktree remove</c>.
/// </param>
/// <param name="PrunableReason">
///  Non-empty when git reported the <c>prunable</c> attribute for this entry: the
///  working directory is gone (deleted by hand) and only its administrative files
///  survive, so the entry is stale and <c>git worktree prune</c> would drop it.
///  Holds git's own reason text when it supplied one.
/// </param>
public sealed record WorktreeRow(
    string Path,
    string Head,
    string Branch,
    bool IsBare,
    bool IsDetached,
    bool IsMain = false,
    string PrunableReason = "")
{
    /// <summary>True when this entry is stale — see <see cref="PrunableReason"/>.</summary>
    public bool IsPrunable => PrunableReason.Length > 0;

    public string Display
    {
        get
        {
            string name = System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
            if (name.Length == 0)
            {
                name = Path;
            }

            string state = IsBare ? "bare"
                : Branch.Length > 0 ? Branch
                : IsDetached ? $"detached @ {Head}"
                : Head;

            return state.Length > 0 ? $"{name} [{state}]" : name;
        }
    }

    public override string ToString() => Display;

    /// <summary>
    ///  True when <paramref name="candidate"/> is this same working directory
    ///  (used to spot the worktree the app currently has open). Compares fully
    ///  resolved paths, ignoring a trailing separator — the same normalisation
    ///  the Windows dialog does in <c>IsCurrentlyOpenedWorktree</c>.
    /// </summary>
    public bool IsSamePath(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || Path.Length == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(Path), Normalize(candidate), StringComparison.Ordinal);
        }
        catch (Exception)
        {
            // Unresolvable path (deleted worktree, permission) → fall back to text.
            return string.Equals(Path, candidate, StringComparison.Ordinal);
        }

        static string Normalize(string p)
            => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p));
    }
}

/// <summary>
///  Result of a mutating worktree operation (add / remove / prune).
/// </summary>
public sealed record WorktreeOpResult(bool Success, string Output);

/// <summary>
///  Lists and manages linked git worktrees by reusing the Git Extensions core
///  (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>. All
///  methods are synchronous and are meant to be called off the UI thread,
///  mirroring the other Avalonia services (e.g. <see cref="SubmoduleService"/>).
/// </summary>
public sealed class WorktreeService
{
    /// <summary>
    ///  Lists the repository's worktrees (including the main one) by parsing
    ///  <c>git worktree list --porcelain</c>. Returns an empty list on failure.
    /// </summary>
    public IReadOnlyList<WorktreeRow> ListWorktrees(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        GitArgumentBuilder args = new("worktree")
        {
            "list",
            "--porcelain",
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return [];
        }

        List<WorktreeRow> rows = [];

        string path = string.Empty;
        string head = string.Empty;
        string branch = string.Empty;
        bool bare = false;
        bool detached = false;
        string prunable = string.Empty;

        void Flush()
        {
            if (path.Length > 0)
            {
                // git always reports the main worktree first, so the very first
                // record we emit is the main one.
                rows.Add(new WorktreeRow(path, head, branch, bare, detached, IsMain: rows.Count == 0, prunable));
            }

            path = string.Empty;
            head = string.Empty;
            branch = string.Empty;
            bare = false;
            detached = false;
            prunable = string.Empty;
        }

        // Records are separated by a blank line; each attribute is on its own
        // line as "<key>" or "<key> <value>".
        foreach (string raw in result.StandardOutput.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            int space = line.IndexOf(' ');
            string key = space > 0 ? line[..space] : line;
            string value = space > 0 ? line[(space + 1)..] : string.Empty;

            switch (key)
            {
                case "worktree":
                    // A new record begins; flush any pending one defensively.
                    Flush();
                    path = value;
                    break;
                case "HEAD":
                    head = value.Length > 8 ? value[..8] : value;
                    break;
                case "branch":
                    branch = ShortBranch(value);
                    break;
                case "bare":
                    bare = true;
                    break;
                case "detached":
                    detached = true;
                    break;
                case "prunable":
                    // Bare "prunable", or "prunable <reason>" (e.g. "gitdir file
                    // points to non-existent location"). Either way the entry is
                    // stale; keep the reason when git gave one so the UI can show it.
                    prunable = value.Length > 0 ? value : "stale";
                    break;
            }
        }

        Flush();
        return rows;
    }

    /// <summary>
    ///  Adds a worktree at <paramref name="path"/>. When <paramref name="branch"/>
    ///  is non-empty it is used as the checkout target (<c>git worktree add &lt;path&gt; &lt;branch&gt;</c>);
    ///  otherwise a new branch is created, named after the path and normalised to git's
    ///  ref rules (<see cref="NewBranchName"/>) rather than left to git's own raw guess.
    /// </summary>
    public WorktreeOpResult AddWorktree(string repoPath, string path, string branch)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string target = path?.Trim() ?? string.Empty;
        if (target.Length == 0)
        {
            return new WorktreeOpResult(false, "Worktree path cannot be empty.");
        }

        GitArgumentBuilder args = new("worktree") { "add" };

        string reference = branch?.Trim() ?? string.Empty;
        if (reference.Length == 0)
        {
            // No ref given: git would name the new branch after the last path segment
            // AS TYPED, and a segment with a space (or any other character
            // git check-ref-format rejects) makes the whole add fail. Upstream hit the
            // same wall in its own dialog and fixed it by normalising the name before
            // handing it to git (6c302d839); the port has no separate name field, so
            // the name it derives is what gets normalised.
            string derived = NewBranchName(target);
            if (derived.Length > 0)
            {
                args.Add("-b");
                args.Add(derived.Quote());
            }
        }

        args.Add(target.Quote());

        if (reference.Length > 0)
        {
            args.Add(reference);
        }

        return Run(module, args);
    }

    /// <summary>
    ///  Removes the worktree at <paramref name="path"/> (<c>git worktree remove &lt;path&gt;</c>).
    /// </summary>
    public WorktreeOpResult RemoveWorktree(string repoPath, string path)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string target = path?.Trim() ?? string.Empty;
        if (target.Length == 0)
        {
            return new WorktreeOpResult(false, "Worktree path cannot be empty.");
        }

        GitArgumentBuilder args = new("worktree")
        {
            "remove",
            target.Quote(),
        };
        return Run(module, args);
    }

    /// <summary>
    ///  Prunes stale worktree administrative files (<c>git worktree prune</c>).
    /// </summary>
    public WorktreeOpResult PruneWorktrees(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("worktree") { "prune" };
        return Run(module, args);
    }

    // Turns "refs/heads/main" into "main"; leaves other refs untouched.
    private static string ShortBranch(string reference)
        => reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;

    /// <summary>
    ///  The branch name <c>git worktree add</c> should create for a worktree at
    ///  <paramref name="path"/>: git's own choice — the last path segment — put through
    ///  the core's <c>git check-ref-format</c> normaliser, so a path like
    ///  <c>~/work/my feature</c> yields <c>my_feature</c> instead of failing the add.
    ///  Empty when there is nothing usable to derive, in which case the caller leaves
    ///  the naming to git exactly as before.
    /// </summary>
    internal static string NewBranchName(string path)
    {
        try
        {
            string leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (leaf.Length == 0)
            {
                return string.Empty;
            }

            string normalised = GitContext.BranchNameNormaliser()
                .Normalise(leaf, new GitBranchNameOptions(AppSettings.AutoNormaliseSymbol));

            // A name the normaliser cannot rescue (all-invalid input) is worse than no
            // name: let git decide and report its own error.
            return normalised.Trim();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static WorktreeOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorktreeOpResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
