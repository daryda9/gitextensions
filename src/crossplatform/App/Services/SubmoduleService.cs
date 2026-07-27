using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single submodule entry, projected for display in the Avalonia repo-objects
///  tree. Independent of the WinForms core UI types.
/// </summary>
/// <param name="Path">Local path of the submodule relative to the super-project.</param>
/// <param name="ShortSha">Abbreviated commit the submodule is checked out at, or empty when unknown.</param>
/// <param name="Status">
///  Coarse working state derived from <c>git submodule status</c>:
///  <c>Initialized</c>, <c>NotInitialized</c> (leading <c>-</c>), <c>OutOfDate</c>
///  (leading <c>+</c>), or <c>Unknown</c>.
/// </param>
public sealed record SubmoduleRow(string Path, string ShortSha, SubmoduleState Status)
{
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
    ///  Lists the repository's submodules. The definitive set of paths comes from
    ///  the core <see cref="GitModule.GetSubmodulesLocalPaths(bool)"/> (which parses
    ///  <c>.gitmodules</c>); each entry is then enriched with the short SHA and
    ///  coarse state parsed from <c>git submodule status</c>. Returns an empty list
    ///  when the repository has no submodules.
    /// </summary>
    public IReadOnlyList<SubmoduleRow> ListSubmodules(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        IReadOnlyList<string> paths = module.GetSubmodulesLocalPaths(recursive: false);
        if (paths.Count == 0)
        {
            return [];
        }

        Dictionary<string, (string Sha, SubmoduleState State)> status = ReadStatus(module);

        List<SubmoduleRow> rows = [];
        foreach (string path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (status.TryGetValue(path, out (string Sha, SubmoduleState State) info))
            {
                rows.Add(new SubmoduleRow(path, info.Sha, info.State));
            }
            else
            {
                rows.Add(new SubmoduleRow(path, string.Empty, SubmoduleState.NotInitialized));
            }
        }

        return rows;
    }

    /// <summary>
    ///  Initializes and updates a single submodule: <c>git submodule update --init -- &lt;path&gt;</c>.
    /// </summary>
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

        GitArgumentBuilder args = new("submodule") { "status" };
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

    private static SubmoduleOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new SubmoduleOpResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
