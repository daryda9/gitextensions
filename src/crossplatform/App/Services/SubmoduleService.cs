using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single submodule entry, projected for display in the Avalonia repo-objects
///  tree. Independent of the WinForms core UI types.
/// </summary>
/// <param name="Path">
///  Local path of the submodule relative to the TOP-level super-project, in posix form.
///  For a submodule of a submodule this is the whole chain
///  (<c>ai-server/core/graphs/tasks</c>), which is what <c>git submodule status
///  --recursive</c> and <see cref="GitModule.GetSubmodulesLocalPaths(bool)"/> report.
/// </param>
/// <param name="ShortSha">Abbreviated commit the submodule is checked out at, or empty when unknown.</param>
/// <param name="Status">
///  Coarse working state derived from <c>git submodule status</c>:
///  <c>Initialized</c>, <c>NotInitialized</c> (leading <c>-</c>), <c>OutOfDate</c>
///  (leading <c>+</c>), or <c>Unknown</c>.
/// </param>
public sealed record SubmoduleRow(string Path, string ShortSha, SubmoduleState Status)
{
    /// <summary>
    ///  Path of the repository that DECLARES this submodule, relative to the top-level
    ///  super-project (empty for a submodule of the top-level repository itself).
    ///  <c>git submodule update -- &lt;path&gt;</c> only accepts a submodule of the
    ///  repository it runs in, so a nested submodule has to be operated on from here
    ///  and not from the top-level working directory.
    /// </summary>
    public string ParentPath { get; init; } = string.Empty;

    /// <summary>
    ///  Path of the submodule as its own super-project declares it — i.e.
    ///  <see cref="Path"/> minus <see cref="ParentPath"/>. This is the argument to give
    ///  to git when running inside <see cref="ParentPath"/>.
    /// </summary>
    public string PathInParent { get; init; } = string.Empty;

    /// <summary>
    ///  Branch the submodule's own HEAD points at, or empty when the submodule is not
    ///  initialized or is on a detached HEAD (which upstream shows as "no branch").
    /// </summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>Last path segment, the name shown in a hierarchical tree.</summary>
    public string Name
    {
        get
        {
            int slash = Path.LastIndexOf('/');
            return slash < 0 ? Path : Path[(slash + 1)..];
        }
    }

    public string Display => ShortSha.Length > 0 ? $"{Path} @ {ShortSha}" : Path;

    public override string ToString() => Display;
}

/// <summary>
///  Working state of a submodule as reported by <c>git submodule status</c>.
/// </summary>
public enum SubmoduleState
{
    Unknown,
    Initialized,
    NotInitialized,
    OutOfDate,
}

/// <summary>
///  Result of a mutating submodule operation (update / update all).
/// </summary>
public sealed record SubmoduleOpResult(bool Success, string Output);

/// <summary>
///  Lists and updates submodules by reusing the Git Extensions core
///  (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>. All
///  methods are synchronous and are meant to be called off the UI thread,
///  mirroring the other Avalonia services (e.g. <see cref="StashOpsService"/>).
/// </summary>
public sealed class SubmoduleService
{
    /// <summary>
    ///  Lists the repository's submodules, RECURSIVELY: a submodule of a submodule is
    ///  listed too, with its whole path from the top-level repository, exactly like the
    ///  WinForms left panel (<c>SubmoduleTree</c>, which walks
    ///  <c>SubmoduleInfoResult.AllSubmodules</c>). The definitive set of paths comes
    ///  from the core <see cref="GitModule.GetSubmodulesLocalPaths(bool)"/> (which
    ///  parses each <c>.gitmodules</c> down the chain); each entry is then enriched
    ///  with the short SHA and coarse state parsed from <c>git submodule status
    ///  --recursive</c>, with the branch read off the submodule's own HEAD.
    ///  Returns an empty list when the repository has no submodules.
    /// </summary>
    public IReadOnlyList<SubmoduleRow> ListSubmodules(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        IReadOnlyList<string> paths = module.GetSubmodulesLocalPaths(recursive: true);
        if (paths.Count == 0)
        {
            return [];
        }

        Dictionary<string, (string Sha, SubmoduleState State)> status = ReadStatus(module);

        // Ordinal, not OrdinalIgnoreCase, and sorted so that a super-project always
        // precedes its own submodules: the tree builder relies on the parent row
        // existing before the child asks for its host node.
        List<string> ordered = [.. paths.OrderBy(p => p, StringComparer.Ordinal)];
        HashSet<string> known = new(ordered, StringComparer.Ordinal);

        List<SubmoduleRow> rows = [];
        foreach (string path in ordered)
        {
            status.TryGetValue(path, out (string Sha, SubmoduleState State) info);
            SubmoduleState state = status.ContainsKey(path) ? info.State : SubmoduleState.NotInitialized;

            // The declaring repository is the longest listed submodule path that is a
            // proper prefix of this one; nothing means the top-level repository.
            string parent = string.Empty;
            foreach (string candidate in known)
            {
                if (path.StartsWith(candidate + "/", StringComparison.Ordinal) && candidate.Length > parent.Length)
                {
                    parent = candidate;
                }
            }

            rows.Add(new SubmoduleRow(path, info.Sha ?? string.Empty, state)
            {
                ParentPath = parent,
                PathInParent = parent.Length == 0 ? path : path[(parent.Length + 1)..],
                Branch = state == SubmoduleState.NotInitialized
                    ? string.Empty
                    : ReadBranch(System.IO.Path.Combine(repoPath, path.Replace('/', System.IO.Path.DirectorySeparatorChar))),
            });
        }

        return rows;
    }

    // Branch of a submodule's own HEAD, without spawning a process per submodule (a
    // deep chain would cost one `git` launch each, on top of the two this service
    // already runs). A submodule's `.git` is normally a FILE holding
    // "gitdir: ../../.git/modules/<name>", so the real HEAD lives there; an old-style
    // submodule has a real `.git` directory. Empty means detached or unreadable, which
    // the tree shows as "no branch" the way upstream's SubmoduleNode.BranchText does.
    private static string ReadBranch(string submoduleFullPath)
    {
        try
        {
            string dotGit = System.IO.Path.Combine(submoduleFullPath, ".git");
            string gitDir;
            if (Directory.Exists(dotGit))
            {
                gitDir = dotGit;
            }
            else if (File.Exists(dotGit))
            {
                string content = File.ReadAllText(dotGit).Trim();
                const string Marker = "gitdir:";
                if (!content.StartsWith(Marker, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                string target = content[Marker.Length..].Trim();
                gitDir = System.IO.Path.IsPathRooted(target)
                    ? target
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(submoduleFullPath, target));
            }
            else
            {
                return string.Empty;
            }

            string head = System.IO.Path.Combine(gitDir, "HEAD");
            if (!File.Exists(head))
            {
                return string.Empty;
            }

            string text = File.ReadAllText(head).Trim();
            const string RefPrefix = "ref: refs/heads/";
            return text.StartsWith(RefPrefix, StringComparison.Ordinal) ? text[RefPrefix.Length..] : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///  Initializes and updates a single submodule: <c>git submodule update --init -- &lt;path&gt;</c>.
    ///  Run from the repository that declares <paramref name="row"/>, which for a nested
    ///  submodule is not the top-level one — <c>git submodule update</c> only accepts a
    ///  path that is a submodule of the repository it runs in.
    /// </summary>
    public SubmoduleOpResult Update(string repoPath, SubmoduleRow row)
        => Update(ParentRepo(repoPath, row), row.PathInParent);

    /// <inheritdoc cref="Update(string, SubmoduleRow)"/>
    public SubmoduleOpResult Update(string repoPath, string path)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("submodule")
        {
            "update",
            "--init",
            "--",
            path.Quote(),
        };
        return Run(module, args);
    }

    /// <summary>
    ///  Updates a single submodule to the latest commit on its configured remote
    ///  branch, merging that into the current submodule checkout instead of doing a
    ///  hard reset: <c>git submodule update --remote --merge -- &lt;path&gt;</c>.
    ///  This is the reliable "merge" variant run from the super-project working dir
    ///  (a plain <c>git merge</c> inside the submodule needs a well-defined upstream
    ///  and is fragile across detached-HEAD submodule checkouts).
    /// </summary>
    /// <inheritdoc cref="UpdateMerge(string, string)"/>
    public SubmoduleOpResult UpdateMerge(string repoPath, SubmoduleRow row)
        => UpdateMerge(ParentRepo(repoPath, row), row.PathInParent);

    /// <inheritdoc cref="UpdateMerge(string, SubmoduleRow)"/>
    public SubmoduleOpResult UpdateMerge(string repoPath, string path)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("submodule")
        {
            "update",
            "--remote",
            "--merge",
            "--",
            path.Quote(),
        };
        return Run(module, args);
    }

    /// <summary>
    ///  Initializes and updates all submodules recursively:
    ///  <c>git submodule update --init --recursive</c>.
    /// </summary>
    public SubmoduleOpResult UpdateAll(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("submodule")
        {
            "update",
            "--init",
            "--recursive",
        };
        return Run(module, args);
    }

    /// <summary>
    ///  Registers every submodule recorded in <c>.gitmodules</c> into the local
    ///  <c>.git/config</c>: <c>git submodule init</c>. This is a genuinely distinct
    ///  operation from <see cref="UpdateAll(string)"/> — it only copies the
    ///  name/url/branch settings into the local config (so they can then be edited
    ///  before cloning), it does NOT fetch or check out anything.
    ///
    ///  <para>It used to just delegate to <see cref="UpdateAll(string)"/>, which
    ///  made the dialog's "Init all" button a mislabelled duplicate of "Update
    ///  all" — an init that silently cloned and checked out every submodule.</para>
    /// </summary>
    public SubmoduleOpResult InitAll(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("submodule")
        {
            "init",
        };
        return Run(module, args);
    }

    /// <summary>
    ///  Synchronizes every submodule's remote URL with the value in
    ///  <c>.gitmodules</c>, recursively: <c>git submodule sync --recursive</c>.
    /// </summary>
    public SubmoduleOpResult SynchronizeAll(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("submodule")
        {
            "sync",
            "--recursive",
        };
        return Run(module, args);
    }

    // Parses `git submodule status` lines. Each line is:
    //   <prefix><sha1> <path> (<describe>)
    // where <prefix> is ' ' (in sync), '-' (not initialized), '+' (checked out
    // commit differs from index), or 'U' (merge conflicts).
    private static Dictionary<string, (string Sha, SubmoduleState State)> ReadStatus(GitModule module)
    {
        Dictionary<string, (string, SubmoduleState)> map = new(StringComparer.Ordinal);

        // --recursive so a submodule of a submodule reports too, with its path relative
        // to THIS repository (git prints the full chain), matching the keys used by the
        // recursive path list. Uninitialized submodules are simply not descended into.
        GitArgumentBuilder args = new("submodule") { "status", "--recursive" };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return map;
        }

        foreach (string raw in result.StandardOutput.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2)
            {
                continue;
            }

            char prefix = line[0];
            SubmoduleState state = prefix switch
            {
                '-' => SubmoduleState.NotInitialized,
                '+' => SubmoduleState.OutOfDate,
                'U' => SubmoduleState.OutOfDate,
                _ => SubmoduleState.Initialized,
            };

            string rest = line[1..];
            int firstSpace = rest.IndexOf(' ');
            if (firstSpace <= 0)
            {
                continue;
            }

            string sha = rest[..firstSpace];
            string shortSha = sha.Length > 8 ? sha[..8] : sha;

            string tail = rest[(firstSpace + 1)..];
            int parenthesis = tail.IndexOf(" (", StringComparison.Ordinal);
            string path = (parenthesis > 0 ? tail[..parenthesis] : tail).Trim();
            if (path.Length == 0)
            {
                continue;
            }

            map[path] = (shortSha, state);
        }

        return map;
    }

    // Working directory of the repository that declares the row's submodule.
    private static string ParentRepo(string repoPath, SubmoduleRow row)
        => row.ParentPath.Length == 0
            ? repoPath
            : System.IO.Path.Combine(repoPath, row.ParentPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static SubmoduleOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new SubmoduleOpResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
