using GitCommands;
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
public sealed record WorktreeRow(string Path, string Head, string Branch, bool IsBare, bool IsDetached)
{
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

        void Flush()
        {
            if (path.Length > 0)
            {
                rows.Add(new WorktreeRow(path, head, branch, bare, detached));
            }

            path = string.Empty;
            head = string.Empty;
            branch = string.Empty;
            bare = false;
            detached = false;
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
            }
        }

        Flush();
        return rows;
    }

    /// <summary>
    ///  Adds a worktree at <paramref name="path"/>. When <paramref name="branch"/>
    ///  is non-empty it is used as the checkout target (<c>git worktree add &lt;path&gt; &lt;branch&gt;</c>);
    ///  otherwise git creates a new branch named after the path.
    /// </summary>
    public WorktreeOpResult AddWorktree(string repoPath, string path, string branch)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        string target = path?.Trim() ?? string.Empty;
        if (target.Length == 0)
        {
            return new WorktreeOpResult(false, "Worktree path cannot be empty.");
        }

        GitArgumentBuilder args = new("worktree")
        {
            "add",
            target.Quote(),
        };

        string reference = branch?.Trim() ?? string.Empty;
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

    private static WorktreeOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new WorktreeOpResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
