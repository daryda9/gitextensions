using GitExtensions.Avalonia.Services;

string repo = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "navigation-snapshot-repo"));
int hierarchyCalls = 0;
int worktreeCalls = 0;
TaskCompletionSource firstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

RepositoryNavigationSnapshotService service = CreateService(asyncGate: firstGate.Task);
Task<RepositoryNavigationSnapshot>[] concurrent = Enumerable.Range(0, 10)
    .Select(_ => service.GetAsync(repo))
    .ToArray();
Check(concurrent.All(task => ReferenceEquals(task, concurrent[0])), "concurrent callers share the same Task");
firstGate.SetResult();
await Task.WhenAll(concurrent);
Equal(1, hierarchyCalls, "one hierarchy factory call");
Equal(1, worktreeCalls, "one worktree factory call");

if (OperatingSystem.IsWindows())
{
    Task<RepositoryNavigationSnapshot> differentlyCased = service.GetAsync(repo.ToUpperInvariant());
    Check(ReferenceEquals(concurrent[0], differentlyCased), "Windows path casing shares cache entry");
}

service.Invalidate(repo);
Task<RepositoryNavigationSnapshot> afterInvalidation = service.GetAsync(repo);
Check(!ReferenceEquals(concurrent[0], afterInvalidation), "invalidation creates a new Task");
await afterInvalidation;
Equal(2, hierarchyCalls, "invalidation reruns hierarchy factory");
Equal(2, worktreeCalls, "invalidation reruns worktree factory");

int failures = 0;
RepositoryNavigationSnapshotService retrying = new(
    path => Interlocked.Increment(ref failures) == 1
        ? throw new InvalidOperationException("expected")
        : Hierarchy(path, "retry"),
    _ => Array.Empty<WorktreeRow>());
await ThrowsAsync(() => retrying.GetAsync(repo), "first factory error propagates");
RepositoryNavigationSnapshot retry = await retrying.GetAsync(repo);
Equal(2, failures, "failed task does not poison cache");
EqualText("retry", retry.Submodules.Nodes[0].ConfiguredName, "retry result returned");

int staleCall = 0;
TaskCompletionSource staleGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
RepositoryNavigationSnapshotService generations = new(
    path =>
    {
        int call = Interlocked.Increment(ref staleCall);
        if (call == 1) staleGate.Task.GetAwaiter().GetResult();
        return Hierarchy(path, call == 1 ? "old" : "new");
    },
    _ => Array.Empty<WorktreeRow>());
Task<RepositoryNavigationSnapshot> oldTask = generations.GetAsync(repo);
generations.Invalidate(repo);
RepositoryNavigationSnapshot newer = await generations.GetAsync(repo);
staleGate.SetResult();
RepositoryNavigationSnapshot older = await oldTask;
EqualText("new", newer.Submodules.Nodes[0].ConfiguredName, "new generation completes first");
EqualText("old", older.Submodules.Nodes[0].ConfiguredName, "old caller still receives its result");
Check(ReferenceEquals(generations.GetAsync(repo), generations.GetAsync(repo)), "new generation remains cached");
RepositoryNavigationSnapshot cached = await generations.GetAsync(repo);
EqualText("new", cached.Submodules.Nodes[0].ConfiguredName, "old completion does not replace new generation");

Console.WriteLine("PASS: navigation snapshot single-flight, invalidation, retry and stale-generation isolation");

RepositoryNavigationSnapshotService CreateService(Task asyncGate) => new(
    path =>
    {
        Interlocked.Increment(ref hierarchyCalls);
        asyncGate.GetAwaiter().GetResult();
        return Hierarchy(path, "root");
    },
    _ =>
    {
        Interlocked.Increment(ref worktreeCalls);
        asyncGate.GetAwaiter().GetResult();
        return Array.Empty<WorktreeRow>();
    });

static SubmoduleHierarchy Hierarchy(string path, string name) => new(
    path, path, null,
    new[] { new SubmoduleRow(string.Empty, string.Empty, SubmoduleState.Initialized) { AbsolutePath = path, ConfiguredName = name } });

static async Task ThrowsAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("FAIL: " + message);
}

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException("FAIL: " + message);
}

static void Equal(int expected, int actual, string message) => Check(expected == actual, $"{message}: expected {expected}, actual {actual}");
static void EqualText(string expected, string actual, string message) => Check(expected == actual, $"{message}: expected '{expected}', actual '{actual}'");
