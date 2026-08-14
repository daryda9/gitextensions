using GitExtensions.Avalonia.Services;

// This harness controls the *order* in which the service's loads finish, and its
// only lever is to park the factory delegate — which the service runs on a
// thread-pool worker (Task.Run), deliberately, because the real delegates shell
// out to git. Parking is safe; parking until code that runs *later on the pool*
// releases it is not, and that cost a full CI timeout with an empty log while
// every developer machine passed.
//
// The mechanism, measured rather than guessed: after the first `await` this file's
// own flow runs on a pool worker, so its Task.Run calls enqueue into that worker's
// LOCAL queue — and a local enqueue does not ask the pool for another thread,
// because the enqueueing worker is expected to get to it. When that worker instead
// parks inside an earlier item, the rest of its queue is stranded: the run that
// deadlocked showed the second generation's factory already finished, two work
// items still pending, and the pool down to the one parked thread. More threads do
// not fix it (ThreadPool.SetMinThreads was tried, and does not) because nothing
// wakes anyone to steal a local queue.
//
// So the one section that parks a load runs on a thread of this harness's own: its
// loads are started from outside the pool, which puts them in the global queue where
// a missing worker is asked for, and the gate is opened by a thread no work item can
// strand. Every park is bounded too, so that a future cycle of this shape fails with
// the name of the gate instead of hanging until something kills it.

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

// On a thread of this harness's own, and with blocking waits, for two reasons that
// only this section has. It is the only one that holds one load parked while another
// runs, so it is the only one that can strand work in a worker's local queue — and
// starting the loads from a thread that is not a pool worker puts them in the global
// queue instead, which the pool answers by producing a worker. It is also the only
// one whose assertions depend on which load reached the factory first, and waiting
// for that between the two calls is a blocking wait, not an await.
OnOwnThread("stale-generation isolation", () =>
{
    int staleCall = 0;
    TaskCompletionSource staleGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    RepositoryNavigationSnapshotService generations = new(
        path =>
        {
            int call = Interlocked.Increment(ref staleCall);
            if (call == 1) Park(staleGate.Task, "stale-generation gate");
            return Hierarchy(path, call == 1 ? "old" : "new");
        },
        _ => Array.Empty<WorktreeRow>());

    // Generation 1 must be the load that parks, so wait until its factory is in
    // before invalidating. Nothing here orders the two Task.Run calls otherwise:
    // pinned to one core the worker's local queue runs LIFO, generation 2's factory
    // went in first, took the "old" identity, and the assertion below failed with
    // 'old' where it wanted 'new'.
    Task<RepositoryNavigationSnapshot> oldTask = generations.GetAsync(repo);
    Check(
        SpinWait.SpinUntil(() => Volatile.Read(ref staleCall) >= 1, TimeSpan.FromSeconds(25)),
        "the old generation's factory started");

    generations.Invalidate(repo);
    Task<RepositoryNavigationSnapshot> newTask = generations.GetAsync(repo);
    Check(
        SpinWait.SpinUntil(() => Volatile.Read(ref staleCall) >= 2, TimeSpan.FromSeconds(25)),
        "the new generation's factory ran while the old one was still parked");

    staleGate.SetResult();
    RepositoryNavigationSnapshot newer = newTask.GetAwaiter().GetResult();
    RepositoryNavigationSnapshot older = oldTask.GetAwaiter().GetResult();
    EqualText("new", newer.Submodules.Nodes[0].ConfiguredName, "the new generation returns the new result");
    EqualText("old", older.Submodules.Nodes[0].ConfiguredName, "old caller still receives its result");
    Check(ReferenceEquals(generations.GetAsync(repo), generations.GetAsync(repo)), "new generation remains cached");
    RepositoryNavigationSnapshot cached = generations.GetAsync(repo).GetAwaiter().GetResult();
    EqualText("new", cached.Submodules.Nodes[0].ConfiguredName, "old completion does not replace new generation");
});

Console.WriteLine("PASS: navigation snapshot single-flight, invalidation, retry and stale-generation isolation");

RepositoryNavigationSnapshotService CreateService(Task asyncGate) => new(
    path =>
    {
        Interlocked.Increment(ref hierarchyCalls);
        Park(asyncGate, "single-flight gate (hierarchy)");
        return Hierarchy(path, "root");
    },
    _ =>
    {
        Interlocked.Increment(ref worktreeCalls);
        Park(asyncGate, "single-flight gate (worktrees)");
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

// Runs a section on a dedicated thread and rethrows whatever it threw, so a failure
// still reads as this harness's own failure rather than a background crash.
static void OnOwnThread(string name, Action body)
{
    Exception? failure = null;
    Thread thread = new(() =>
    {
        try { body(); }
        catch (Exception exception) { failure = exception; }
    })
    {
        IsBackground = true,
        Name = name,
    };

    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(60)))
    {
        throw new InvalidOperationException($"FAIL: {name} did not finish within 60s");
    }

    if (failure is not null)
    {
        throw new InvalidOperationException($"FAIL: {name}: {failure.Message}", failure);
    }
}

// Parks the calling worker on a gate the harness opens later. Bounded, because an
// unbounded park turns a wait cycle into silence: what reaches the log then is an
// empty file and whatever timeout killed the process.
static void Park(Task gate, string what)
{
    if (!gate.Wait(TimeSpan.FromSeconds(30)))
    {
        throw new InvalidOperationException(
            $"FAIL: {what} was never opened within 30s — {ThreadPool.ThreadCount} pool threads, "
            + $"{Environment.ProcessorCount} processors. A parked worker is waiting on code that "
            + "needs a worker of its own.");
    }
}

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException("FAIL: " + message);
}

static void Equal(int expected, int actual, string message) => Check(expected == actual, $"{message}: expected {expected}, actual {actual}");
static void EqualText(string expected, string actual, string message) => Check(expected == actual, $"{message}: expected '{expected}', actual '{actual}'");
