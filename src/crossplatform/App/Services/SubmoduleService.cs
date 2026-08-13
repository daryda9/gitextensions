using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;
using System.Diagnostics;
using System.Text;

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
    public string AbsolutePath { get; init; } = string.Empty;
    public string ParentRepositoryPath { get; init; } = string.Empty;
    public string ConfiguredName { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public bool IsCurrent { get; init; }
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
/// Complete submodule graph rooted at the highest super-project Git can resolve.
/// Paths are normalized absolute paths. Discovery is synchronous and performs git and
/// filesystem I/O; callers must invoke it from a worker thread (the Avalonia callers use
/// <c>Task.Run</c>).
/// </summary>
public sealed record SubmoduleHierarchy(
    string RootPath,
    string CurrentPath,
    string? ImmediateSuperprojectPath,
    IReadOnlyList<SubmoduleRow> Nodes);

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
    /// <summary>Discovers the top project, parent chain, siblings and descendants.</summary>
    public SubmoduleHierarchy DiscoverHierarchy(string repoPath)
    {
        string current = Normalize(repoPath);
        List<string> chain = [current];
        HashSet<string> visited = new(PathComparer);
        visited.Add(current);

        string cursor = current;
        while (TrySuperproject(cursor) is { Length: > 0 } parent && visited.Add(parent))
        {
            chain.Add(parent);
            cursor = parent;
        }

        string root = chain[^1];
        string? immediateParent = chain.Count > 1 ? chain[1] : null;
        IReadOnlyList<SubmoduleRow> listed = ListSubmodulesCore(root);
        List<SubmoduleRow> nodes = new(listed.Count + 1)
        {
            new(string.Empty, string.Empty, SubmoduleState.Initialized)
            {
                AbsolutePath = root,
                ParentRepositoryPath = string.Empty,
                ConfiguredName = DirectoryName(root),
                Exists = Directory.Exists(root),
                IsCurrent = SamePath(root, current),
            },
        };

        nodes.AddRange(listed.Select(row => row with { IsCurrent = SamePath(row.AbsolutePath, current) }));
        return new(root, current, immediateParent, nodes);
    }

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
        => ListSubmodulesCore(Normalize(repoPath));

    private IReadOnlyList<SubmoduleRow> ListSubmodulesCore(string repoPath)
    {
        // One cache per discovery, never across discoveries: the whole point of
        // re-running this is to notice that the repository moved. Within a single
        // walk, though, the same questions were asked over and over — every
        // submodule was probed twice for "is this a repository?" and the parent's
        // .gitmodules was re-read once per sibling. Each of those is a git process,
        // which costs ~1 ms on Linux and ~40 ms on Windows (measured), so the
        // duplication was invisible on one platform and dominant on the other.
        GitProbeCache cache = new();
        List<DiscoveredSubmodule> ordered = WalkSubmodules(repoPath, cache);
        if (ordered.Count == 0)
        {
            return [];
        }

        Dictionary<string, (string Sha, SubmoduleState State)> status = ReadStatus(repoPath, ordered, cache);

        List<SubmoduleRow> rows = [];
        foreach (DiscoveredSubmodule item in ordered)
        {
            string path = item.Path;
            status.TryGetValue(path, out (string Sha, SubmoduleState State) info);
            SubmoduleState state = status.ContainsKey(path) ? info.State : SubmoduleState.NotInitialized;
            rows.Add(new SubmoduleRow(path, info.Sha ?? string.Empty, state)
            {
                ParentPath = item.ParentPath,
                PathInParent = item.PathInParent,
                AbsolutePath = item.AbsolutePath,
                ParentRepositoryPath = item.ParentRepositoryPath,
                ConfiguredName = cache.ConfiguredName(item.ParentRepositoryPath, item.PathInParent),
                Exists = Directory.Exists(item.AbsolutePath) && cache.IsRepository(item.AbsolutePath),
                Branch = state == SubmoduleState.NotInitialized
                    ? string.Empty
                    : ReadBranch(item.AbsolutePath),
            });
        }

        return rows;
    }

    /// <summary>
    ///  Discovers every submodule reachable from <paramref name="repoPath"/>, depth
    ///  first, sorted by path. Split out of <see cref="ListSubmodulesCore"/> so the
    ///  parity check can reuse exactly the same discovery.
    /// </summary>
    private List<DiscoveredSubmodule> WalkSubmodules(string repoPath, GitProbeCache cache)
    {
        List<DiscoveredSubmodule> discovered = [];
        HashSet<string> visitedRepositories = new(PathComparer);
        object gate = new();
        Walk(repoPath, string.Empty, depth: 0);
        return [.. discovered.OrderBy(p => p.Path, StringComparer.Ordinal)];

        void Walk(string declaringRepo, string parentPath, int depth)
        {
            const int MaxDepth = 128;
            string normalizedRepo = Normalize(declaringRepo);
            if (depth >= MaxDepth)
            {
                return;
            }

            // A directory with no .gitmodules declares no submodules, so there is
            // nothing here to descend into — and answering that from the filesystem
            // costs nothing, where the three git probes below cost ~120 ms on Windows.
            // This is what makes a LEAF submodule free instead of the most expensive
            // node in the walk. GetSubmodulesLocalPaths reads this same working-tree
            // file, so its absence is the same answer, reached earlier.
            if (!File.Exists(System.IO.Path.Combine(normalizedRepo, ".gitmodules")))
            {
                return;
            }

            if (!cache.IsRepository(normalizedRepo))
            {
                return;
            }

            string identity = cache.RepositoryIdentity(normalizedRepo);
            lock (gate)
            {
                if (!visitedRepositories.Add(identity))
                {
                    return;
                }
            }

            IReadOnlyList<string> direct;
            try
            {
                direct = GitContext.CreateModule(normalizedRepo).GetSubmodulesLocalPaths(recursive: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return;
            }

            // Siblings are independent — each one only probes its own directory — so
            // they are walked concurrently. On Windows that turns N serial process
            // launches into one round; the results are re-sorted below, so the
            // ordering does not depend on completion order.
            Parallel.ForEach(direct.OrderBy(p => p, StringComparer.Ordinal), rawPath =>
            {
                string localPath = rawPath.Replace('\\', '/').Trim('/');
                if (localPath.Length == 0)
                {
                    return;
                }

                string path = parentPath.Length == 0 ? localPath : $"{parentPath}/{localPath}";
                string absolute;
                try
                {
                    absolute = Normalize(System.IO.Path.Combine(normalizedRepo, Native(localPath)));
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return;
                }

                lock (gate)
                {
                    discovered.Add(new(path, parentPath, localPath, absolute, normalizedRepo));
                }

                Walk(absolute, path, depth + 1);
            });
        }
    }

    /// <summary>
    ///  Memoises the three read-only git probes this discovery repeats, for the
    ///  lifetime of one discovery only. Thread-safe: the sibling walk is concurrent.
    /// </summary>
    private sealed class GitProbeCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _isRepository = new(PathComparer);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _identity = new(PathComparer);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _gitmodules = new(PathComparer);

        public bool IsRepository(string path)
            => _isRepository.GetOrAdd(path, static p => SubmoduleService.IsRepository(p));

        public string RepositoryIdentity(string path)
            => _identity.GetOrAdd(path, static p => SubmoduleService.RepositoryIdentity(p));

        /// <summary>
        ///  Name under which <paramref name="pathInParent"/> is declared in its parent's
        ///  <c>.gitmodules</c>. The file is read once per parent repository rather than
        ///  once per submodule declared in it.
        /// </summary>
        public string ConfiguredName(string parentRepo, string pathInParent)
        {
            if (parentRepo.Length == 0)
            {
                return string.Empty;
            }

            string listing = _gitmodules.GetOrAdd(parentRepo, static repo =>
            {
                GitOutput result = RunGit(repo, "config --file .gitmodules --get-regexp ^submodule\\..*\\.path$");
                return result.ExitCode == 0 ? result.Output : string.Empty;
            });

            return NameFromGitmodules(listing, pathInParent);
        }
    }

    private sealed record DiscoveredSubmodule(
        string Path,
        string ParentPath,
        string PathInParent,
        string AbsolutePath,
        string ParentRepositoryPath);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string Native(string path) => path.Replace('/', System.IO.Path.DirectorySeparatorChar);

    private static string Normalize(string path)
        => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static bool SamePath(string left, string right) => PathComparer.Equals(Normalize(left), Normalize(right));

    private static string DirectoryName(string path)
        => new DirectoryInfo(System.IO.Path.TrimEndingDirectorySeparator(path)).Name;

    private static bool IsRepository(string path)
        => RunGit(path, "rev-parse --is-inside-work-tree") is { ExitCode: 0, Output: "true" };

    private static string RepositoryIdentity(string path)
    {
        GitOutput result = RunGit(path, "rev-parse --absolute-git-dir");
        if (result.ExitCode == 0 && result.Output.Length > 0)
        {
            try
            {
                return Normalize(result.Output);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return Normalize(path);
    }

    private static string? TrySuperproject(string path)
    {
        GitOutput result = RunGit(path, "rev-parse --show-superproject-working-tree");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return null;
        }

        try
        {
            return Normalize(result.Output);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Pure parser over the output of `config --get-regexp submodule.*.path`, split out
    // of the former ReadConfiguredName so the process behind it can be run once per
    // parent repository (GitProbeCache.ConfiguredName) instead of once per submodule.
    private static string NameFromGitmodules(string listing, string pathInParent)
    {
        foreach (string line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0 || !string.Equals(line[(separator + 1)..].Trim().Replace('\\', '/'), pathInParent, StringComparison.Ordinal))
            {
                continue;
            }

            const string Prefix = "submodule.";
            const string Suffix = ".path";
            string key = line[..separator];
            return key.StartsWith(Prefix, StringComparison.Ordinal) && key.EndsWith(Suffix, StringComparison.Ordinal)
                ? key[Prefix.Length..^Suffix.Length]
                : string.Empty;
        }

        return string.Empty;
    }

    // Argument-list form, for the calls that pass submodule paths as pathspecs: a
    // path containing a space cannot survive being concatenated into a command line.
    private static GitOutput RunGit(string workingDirectory, IReadOnlyList<string> argumentList)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in argumentList)
        {
            start.ArgumentList.Add(argument);
        }

        return RunGit(start);
    }

    private static GitOutput RunGit(string workingDirectory, string arguments)
    {
        ProcessStartInfo start = new("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return RunGit(start);
    }

    private static GitOutput RunGit(ProcessStartInfo start)
    {
        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return new(-1, string.Empty);
            }

            StringBuilder stdout = new();
            StringBuilder stderr = new();
            process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
            process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            return new(process.ExitCode, stdout.ToString().Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(-1, string.Empty);
        }
    }

    private static void Append(StringBuilder target, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (target)
        {
            target.AppendLine(line);
        }
    }

    private readonly record struct GitOutput(int ExitCode, string Output);

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

    /// <summary>
    ///  Submodule SHA and coarse state, keyed by path from the top-level repository.
    ///
    ///  <para><b>Why this forks by platform.</b> The obvious implementation is
    ///  <c>git submodule status --recursive</c>, and on Linux that is exactly what runs:
    ///  it is git's own answer, it costs 45 ms, and there is no reason to second-guess
    ///  it. On Windows the same command costs 1.4 s — measured, and measured again with
    ///  <b>zero</b> submodules, where it still costs 1.1 s. <c>git-submodule</c> is a
    ///  <c>#!/bin/sh</c> script on both platforms (verified: same 10 KB file), so every
    ///  invocation starts a shell and forks a git child per submodule. Creating a
    ///  process costs ~0.7 ms on Linux and ~20 ms on Windows, and an MSYS2 shell ~62 ms,
    ///  so the identical script is ~50x more expensive there. It ran on every repository
    ///  and branch switch, and was 1.4 s of the 2.2 s those took.</para>
    ///
    ///  <para>The Windows path derives the same answer from data git exposes without a
    ///  shell: the index of each declaring repository (one <c>ls-files --stage</c> per
    ///  repository, not per submodule) and each submodule's own HEAD. That is the
    ///  definition of git's own prefixes — <c>-</c> is "not initialized", <c>+</c> is
    ///  "checked-out commit differs from the index", <c>U</c> is "unmerged" — rather
    ///  than an approximation of them.</para>
    ///
    ///  <para><b>Keeping the two honest.</b> Two implementations can drift, and the
    ///  Windows one is the less-travelled. <c>Tests/Perf</c> compares this against
    ///  <c>git submodule status --recursive</c> on a real repository and reports any
    ///  disagreement, so a divergence shows up as a failed check and not as a wrong
    ///  icon in the tree.</para>
    /// </summary>
    private static Dictionary<string, (string Sha, SubmoduleState State)> ReadStatus(
        string repoPath,
        IReadOnlyList<DiscoveredSubmodule> discovered,
        GitProbeCache cache)
        => OperatingSystem.IsWindows()
            ? DeriveStatus(discovered, cache)
            : ReadStatusFromGit(repoPath);

    /// <summary>
    ///  Runs BOTH status implementations against <paramref name="repoPath"/> and reports
    ///  every disagreement, one line each; an empty list means they agree exactly.
    ///
    ///  <para>This exists because <see cref="ReadStatus"/> deliberately answers the same
    ///  question two different ways depending on the platform, and two implementations
    ///  drift. It runs on either platform (the derived one does not depend on being on
    ///  Windows — only on it being worth the trouble there), so a Linux CI run checks the
    ///  Windows code path too. <c>Tests/Perf</c> calls it.</para>
    /// </summary>
    public IReadOnlyList<string> CheckStatusParity(string repoPath)
    {
        GitProbeCache cache = new();
        List<DiscoveredSubmodule> ordered = WalkSubmodules(Normalize(repoPath), cache);
        if (ordered.Count == 0)
        {
            return [];
        }

        Dictionary<string, (string Sha, SubmoduleState State)> derived = DeriveStatus(ordered, cache);
        Dictionary<string, (string Sha, SubmoduleState State)> fromGit = ReadStatusFromGit(Normalize(repoPath));

        List<string> differences = [];
        foreach (string path in derived.Keys.Union(fromGit.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            bool inDerived = derived.TryGetValue(path, out (string Sha, SubmoduleState State) d);
            bool inGit = fromGit.TryGetValue(path, out (string Sha, SubmoduleState State) g);

            if (!inDerived || !inGit)
            {
                differences.Add($"{path}: {(inDerived ? "missing from git" : "missing from derived")}");
                continue;
            }

            if (d.State != g.State || !string.Equals(d.Sha, g.Sha, StringComparison.Ordinal))
            {
                differences.Add($"{path}: derived {d.State}/{d.Sha} vs git {g.State}/{g.Sha}");
            }
        }

        return differences;
    }

    /// <summary>
    ///  Windows replacement for <c>git submodule status --recursive</c>, built from the
    ///  index and the submodules' own HEADs.
    /// </summary>
    private static Dictionary<string, (string Sha, SubmoduleState State)> DeriveStatus(
        IReadOnlyList<DiscoveredSubmodule> discovered,
        GitProbeCache cache)
    {
        Dictionary<string, (string Sha, SubmoduleState State)> map = new(StringComparer.Ordinal);

        // What the containing repository records for each submodule. One process per
        // DECLARING repository, with the submodule paths as pathspecs, rather than one
        // per submodule — the whole point of the exercise.
        Dictionary<string, IndexEntry> indexed = new(StringComparer.Ordinal);
        foreach (IGrouping<string, DiscoveredSubmodule> group in discovered.GroupBy(d => d.ParentRepositoryPath, PathComparer))
        {
            List<string> arguments = ["ls-files", "--stage", "-z", "--"];
            arguments.AddRange(group.Select(d => d.PathInParent));

            GitOutput listing = RunGit(group.Key, arguments);
            if (listing.ExitCode != 0)
            {
                continue;
            }

            Dictionary<string, IndexEntry> entries = ParseIndexEntries(listing.Output);
            foreach (DiscoveredSubmodule item in group)
            {
                if (entries.TryGetValue(item.PathInParent, out IndexEntry entry))
                {
                    indexed[item.Path] = entry;
                }
            }
        }

        // The commit each submodule is actually checked out at. `rev-parse HEAD` inside
        // the submodule rather than resolving refs/ and packed-refs by hand: it is one
        // cheap process, it is git's own answer, and reimplementing ref resolution is
        // exactly the kind of second guess this method is trying to avoid. Run
        // concurrently, so N submodules cost one round rather than N.
        System.Collections.Concurrent.ConcurrentDictionary<string, string> heads = new(StringComparer.Ordinal);
        Parallel.ForEach(discovered, item =>
        {
            if (!IsInitialized(item.AbsolutePath) || !cache.IsRepository(item.AbsolutePath))
            {
                return;
            }

            GitOutput head = RunGit(item.AbsolutePath, "rev-parse HEAD");
            if (head.ExitCode == 0 && head.Output.Length > 0)
            {
                heads[item.Path] = head.Output.Trim();
            }
        });

        foreach (DiscoveredSubmodule item in discovered)
        {
            indexed.TryGetValue(item.Path, out IndexEntry entry);
            bool hasHead = heads.TryGetValue(item.Path, out string? headSha);

            // No checkout at all is git's '-'. The SHA shown then is the one recorded in
            // the index, which is all that is known about it — as `submodule status` does.
            if (!hasHead)
            {
                map[item.Path] = (Abbreviate(entry.Sha ?? string.Empty), SubmoduleState.NotInitialized);
                continue;
            }

            // An unmerged index entry (stage != 0) is git's 'U'; a checkout that does not
            // match the recorded commit is git's '+'. Both map to OutOfDate, as the
            // previous parser did.
            SubmoduleState state = entry.Unmerged || !string.Equals(headSha, entry.Sha, StringComparison.Ordinal)
                ? SubmoduleState.OutOfDate
                : SubmoduleState.Initialized;

            map[item.Path] = (Abbreviate(headSha!), state);
        }

        return map;
    }

    // A submodule that was never `git submodule update`d has no .git at all; that is
    // what makes it "not initialized" rather than merely out of date.
    private static bool IsInitialized(string absolutePath)
    {
        try
        {
            string dotGit = System.IO.Path.Combine(absolutePath, ".git");
            return Directory.Exists(dotGit) || File.Exists(dotGit);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private readonly record struct IndexEntry(string Sha, bool Unmerged);

    // `ls-files --stage -z` prints "<mode> <sha> <stage>\t<path>" per entry, NUL
    // separated so a path with a newline in it cannot be mistaken for two entries.
    // Only gitlinks (mode 160000) are of interest here.
    private static Dictionary<string, IndexEntry> ParseIndexEntries(string output)
    {
        Dictionary<string, IndexEntry> entries = new(StringComparer.Ordinal);

        foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = record.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            string[] fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[0] != "160000")
            {
                continue;
            }

            string path = record[(tab + 1)..].Replace('\\', '/').Trim('/');
            bool unmerged = fields[2] != "0";

            // An unmerged path has three entries (stages 1-3); the first one seen fixes
            // the SHA, and any stage != 0 marks the whole path unmerged.
            if (entries.TryGetValue(path, out IndexEntry existing))
            {
                entries[path] = existing with { Unmerged = existing.Unmerged || unmerged };
                continue;
            }

            entries[path] = new IndexEntry(fields[1], unmerged);
        }

        return entries;
    }

    private static string Abbreviate(string sha) => sha.Length > 8 ? sha[..8] : sha;

    // Parses `git submodule status` lines. Each line is:
    //   <prefix><sha1> <path> (<describe>)
    // where <prefix> is ' ' (in sync), '-' (not initialized), '+' (checked out
    // commit differs from index), or 'U' (merge conflicts).
    private static Dictionary<string, (string Sha, SubmoduleState State)> ReadStatusFromGit(string repoPath)
    {
        Dictionary<string, (string, SubmoduleState)> map = new(StringComparer.Ordinal);

        // --recursive so a submodule of a submodule reports too, with its path relative
        // to THIS repository (git prints the full chain), matching the keys used by the
        // recursive path list. Uninitialized submodules are simply not descended into.
        GitOutput result = RunGit(repoPath, "submodule status --recursive");
        if (result.ExitCode != 0)
        {
            return map;
        }

        foreach (string raw in result.Output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 2)
            {
                continue;
            }

            // An in-sync submodule is prefixed with a SPACE, and RunGit trims the whole
            // output — so the first line arrives without it while the others keep it.
            // Consuming line[0] unconditionally therefore ate the first digit of the
            // first submodule's SHA (caught by CheckStatusParity, which reported
            // "3f486b28" where git means "63f486b2"). Only consume a prefix that really
            // is one; a SHA is hex and can never start with these.
            char prefix = line[0];
            SubmoduleState state = prefix switch
            {
                '-' => SubmoduleState.NotInitialized,
                '+' => SubmoduleState.OutOfDate,
                'U' => SubmoduleState.OutOfDate,
                _ => SubmoduleState.Initialized,
            };

            string rest = (prefix is '-' or '+' or 'U' ? line[1..] : line).TrimStart();
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
        => row.ParentRepositoryPath.Length > 0
            ? row.ParentRepositoryPath
            : row.ParentPath.Length == 0
            ? repoPath
            : System.IO.Path.Combine(repoPath, row.ParentPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static SubmoduleOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new SubmoduleOpResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
