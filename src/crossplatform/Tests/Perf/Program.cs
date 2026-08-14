using System.Diagnostics;
using GitCommands.Logging;
using GitExtensions.Avalonia;
using GitExtensions.Avalonia.Services;

// Measures what one "open / refresh repository" costs in git processes, so the
// Windows-vs-Linux gap can be attributed rather than guessed at. Prints the wall
// clock, the number of processes started, their summed duration, and the commands
// ranked by how much of the wall clock they account for.
//
// Usage: Perf.Harness <repoPath> [rounds]

string repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
int rounds = args.Length > 1 && int.TryParse(args[1], out int r) ? r : 3;

GitUI.CrossPlatformBootstrap.InitializeThreading();
_ = GitCommands.ExecutableExtensions.GetOutput(new GitCommands.Executable("git"), "--version");

Console.WriteLine($"repo: {repo}");

// Warm-up, exactly as MainWindow.EnsureCoreWarmupAsync does it.
Stopwatch warm = Stopwatch.StartNew();
CommandLog.Clear();
GitCommands.GitModule warmModule = GitContext.CreateModule(repo);
_ = warmModule.GetCurrentCheckout();
_ = new RevisionService().LoadRevisions(repo, 1);
warm.Stop();
Report("warm-up", warm.ElapsedMilliseconds);

for (int i = 1; i <= rounds; i++)
{
    CommandLog.Clear();
    Stopwatch sw = Stopwatch.StartNew();

    // The fan-out MainWindow.LoadRepositoryAfterWarmupAsync performs: revision grid,
    // objects tree, navigation snapshot (submodules + worktrees), status bar and
    // toolbar state, all concurrently.
    Task[] work =
    [
        TimedAsync("revisions", () => new RevisionService().LoadRevisions(repo, 200)),
        TimedAsync("refs", () => new BranchTagService().LoadRefs(repo)),
        TimedAsync("submodules", () => new SubmoduleService().DiscoverHierarchy(repo)),
        TimedAsync("worktrees", () => new WorktreeService().ListWorktrees(repo)),
        TimedAsync("statusbar", () =>
        {
            GitCommands.GitModule m = GitContext.CreateModule(repo);
            string branch = m.GetSelectedBranch();
            _ = m.GetRemoteBranch(branch);
            return m.GetAllChangedFiles();
        }),
    ];
    await Task.WhenAll(work);

    sw.Stop();
    Report($"round {i}", sw.ElapsedMilliseconds);
}

// What a bottom-tab switch costs (commit detail / diff of the selected revision):
// no navigation snapshot is involved, so it is measured on its own.
{
    string head = GitContext.CreateModule(repo).GetCurrentCheckout().ToString();
    CommandLog.Clear();
    Stopwatch sw = Stopwatch.StartNew();
    _ = DiffService.GetChangedFiles(repo, head);
    sw.Stop();
    Report("bottom tab: diff file list", sw.ElapsedMilliseconds);
}

// Submodule status is answered one way on Windows (derived from the index and the
// submodules' HEADs) and another on Linux (git's own `submodule status --recursive`),
// because the latter costs 1.4 s on Windows. Two implementations drift, so check them
// against each other on whatever platform this runs.
{
    Stopwatch sw = Stopwatch.StartNew();
    IReadOnlyList<string> differences = new SubmoduleService().CheckStatusParity(repo);
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("=== submodule rows as the tree receives them");
    foreach (SubmoduleRow row in new SubmoduleService().DiscoverHierarchy(repo).Nodes)
    {
        Console.WriteLine($"   {row.Status,-15} sha={row.ShortSha,-10} exists={row.Exists,-5} branch={row.Branch,-12} name={row.ConfiguredName,-22} {row.Path}");
    }

    Console.WriteLine();
    Console.WriteLine($"=== submodule status parity ({sw.ElapsedMilliseconds} ms for both implementations)");
    if (differences.Count == 0)
    {
        Console.WriteLine("   OK - the derived status matches `git submodule status --recursive` exactly");
    }
    else
    {
        foreach (string difference in differences)
        {
            Console.WriteLine($"   MISMATCH  {difference}");
        }
    }
}

// Reading where the GitHub token lives starts `git credential fill`. On Windows the
// helper is Git Credential Manager, which ACQUIRES credentials rather than just looking
// them up: without credential.interactive=false it opened a sign-in window and waited,
// which froze the app until Windows killed it. This must come back promptly and empty
// for a host nothing is stored under — if it ever hangs here, it hangs the settings
// window too.
{
    Stopwatch sw = Stopwatch.StartNew();
    (string? token, GitHubTokenStore.Storage from) = GitHubTokenStore.Read("api.github.com");
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("=== GitHub token lookup (must never prompt)");
    Console.WriteLine($"   {sw.ElapsedMilliseconds} ms, stored={from}, token={(token is null ? "none" : "present")}");
    Console.WriteLine(sw.ElapsedMilliseconds < 3000
        ? "   OK - returned without waiting for a human"
        : "   SLOW - check that the helper is not prompting");
}

// The watcher's own classification of a Windows path inside .git. This decides
// whether a write the app's own refresh performs is filtered out as noise or
// scheduled as "the repository changed behind our back".
{
    Type watcher = typeof(RepositoryWatcherService);
    Func<string, bool> workTreeNoise = Predicate(watcher, "IsWorkTreeNoise");
    Func<string, bool> gitDirNoise = Predicate(watcher, "IsGitDirNoise");

    Console.WriteLine();
    Console.WriteLine("=== watcher classification (true = ignored as noise)");
    foreach (string path in new[]
    {
        @"C:\repo\.git\objects\ab\cdef0123456789",
        @"C:\repo\.git\index.lock",
        @"C:\repo\.git\FETCH_HEAD",
        @"C:\repo\.git\COMMIT_EDITMSG",
        @"C:\repo\.git\modules\sub\HEAD",
        "/repo/.git/objects/ab/cdef0123456789",
        "/repo/.git/index.lock",
    })
    {
        Console.WriteLine($"   workTreeNoise={workTreeNoise(path),-5} gitDirNoise={gitDirNoise(path),-5} {path}");
    }

    Console.WriteLine($"   IsUnder(C:\\repo\\.git, C:\\repo) = {IsUnder(watcher, @"C:\repo\.git", @"C:\repo")}");
}

static Func<string, bool> Predicate(Type owner, string name)
{
    System.Reflection.MethodInfo method = owner.GetMethod(
        name,
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    return path => (bool)method.Invoke(null, [path])!;
}

static bool IsUnder(Type owner, string path, string root)
{
    System.Reflection.MethodInfo method = owner.GetMethod(
        "IsUnder",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    return (bool)method.Invoke(null, [path, root])!;
}

// Runs one panel loader on the pool and prints how long it took, so a wall clock
// longer than the slowest loader can be told apart from one loader being slow.
static Task TimedAsync<T>(string name, Func<T> load) => Task.Run(() =>
{
    Stopwatch sw = Stopwatch.StartNew();
    _ = load();
    sw.Stop();
    Console.WriteLine($"   [{name,-11}] {sw.ElapsedMilliseconds,5} ms");
});

static void Report(string label, long wallMs)
{
    List<CommandLogEntry> entries = [.. CommandLog.Commands];
    double sum = entries.Sum(e => e.Duration?.TotalMilliseconds ?? 0);

    Console.WriteLine();
    Console.WriteLine($"=== {label}: {wallMs} ms wall, {entries.Count} git processes, {sum:0} ms summed process time");

    IEnumerable<IGrouping<string, CommandLogEntry>> byCommand = entries
        .GroupBy(e => Verb(e.Arguments))
        .OrderByDescending(g => g.Sum(e => e.Duration?.TotalMilliseconds ?? 0));

    foreach (IGrouping<string, CommandLogEntry> group in byCommand.Take(12))
    {
        double total = group.Sum(e => e.Duration?.TotalMilliseconds ?? 0);
        Console.WriteLine($"   {group.Count(),4}x {total,7:0} ms  {group.Key}");
    }
}

// The git subcommand plus its first flag: enough to tell `rev-parse --git-dir` from
// `rev-parse --is-bare-repository` without exploding on per-call arguments.
static string Verb(string arguments)
{
    string[] parts = CommandLogEntry
        .GetGitArgumentsWithoutConfiguration(arguments)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length switch
    {
        0 => "(none)",
        1 => parts[0],
        _ => $"{parts[0]} {parts[1]}",
    };
}
