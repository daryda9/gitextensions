using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GitExtensions.Avalonia.Services;

// Regression suite for the concurrency of App/Services/ViewPrefsService.cs — the file
// EVERY preference surface in the app writes.
//
// Usage: dotnet run --project Tests/ViewPrefsRegression/ViewPrefsRegression.Harness.csproj
//
// Exit code 0 means every case held; any other value means at least one broke, and each
// broken one is printed.
//
// Why this exists at all. The defect it pins is invisible to a build, to a review of any
// single call site, and to the user: two instances of the app (which the modal merge
// editor makes an ordinary situation, not an exotic one) each save a DIFFERENT
// preference, and one of the two silently disappears — no exception, no log line, no
// corrupt file to notice. The old Update() was load → mutate → save with nothing between
// the load and the save, so the loser's change was overwritten by a copy of the file
// taken before it existed. Nothing short of driving genuine concurrency can catch a
// regression of that shape, so this suite spends real threads and real processes.
//
// Every assertion reads the file's RAW BYTES and parses them itself, never the service's
// own Load(): Load replays this process's not-yet-written mutations as a courtesy, which
// would happily mask exactly the write that never reached the disk.
//
// The three properties under test:
//
//  P1  No lost update. Concurrent writers of distinct keys — threads within one process,
//      and separate processes — all find their key in the file afterwards.
//  P2  No torn file. A reader hammering the file during all of that never sees anything
//      but a complete, parseable document; and neither does it after a writer is killed
//      with SIGKILL in the middle of its writes.
//  P3  The reported interleaving specifically: a writer that is slow between its load and
//      its save must not revert what another writer completed in that window.

// ---------------------------------------------------------------- child roles

// Spawned by the parent to get REAL processes, which is the only way to exercise the
// cross-process interlock; a thread would be covered by any in-process lock at all.
if (args.Length > 0 && args[0] == "child")
{
    return Child(args);
}

// ---------------------------------------------------------------- sandbox

// ViewPrefsService resolves its file from XDG_CONFIG_HOME at CONSTRUCTION, so this has to
// happen before the first service exists. It is what stops this suite — which deliberately
// writes garbage, kills writers mid-write and hammers the result — from ever touching the
// preferences of the person running it.
string sandbox = Path.Combine(Path.GetTempPath(), "gea-viewprefs-harness-" + Environment.ProcessId);
Directory.CreateDirectory(sandbox);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", sandbox);

List<string> failures = [];
int cases = 0;

ViewPrefsService service = new();
string file = service.FilePath;
Stopwatch total = Stopwatch.StartNew();

// A starting file, and a witness in a group nobody else in this suite touches: every
// phase re-checks it, so a write that clobbers the whole document instead of merging its
// own group is caught even when the phase's own keys all happen to survive.
service.Update(p =>
{
    p.LeftPanel.SortKey = "CommitDate";
    p.Diff.ContextLines = 7;
});
Expect("the seed reached the disk", Read().RootElement.GetProperty("LeftPanel").GetProperty("SortKey").GetString() == "CommitDate");

// ---------------------------------------------------------------- P1: threads

// Distinct keys in HelpPanels, one namespace per thread, all through the same Update()
// the app uses. Enough writers and enough rounds that the load-mutate-save windows are
// certain to overlap: the old code loses keys here on the first run, every run.
{
    const int Threads = 8;
    const int Rounds = 40;

    Stopwatch phase = Stopwatch.StartNew();
    Barrier start = new(Threads);
    List<Thread> writers = [];

    for (int t = 0; t < Threads; t++)
    {
        int id = t;
        Thread thread = new(() =>
        {
            ViewPrefsService own = new();
            start.SignalAndWait();
            for (int r = 0; r < Rounds; r++)
            {
                // Bound to a fresh local, not to the loop variable: a mutation may be
                // applied later than the call that queued it, and a closure over `r`
                // would then write whatever round the loop had reached by then. This
                // suite found that hazard the hard way; it is the contract stated on
                // ViewPrefsService.Update, and every call site in the app already meets it.
                string key = $"thread-{id}-{r}";
                own.Update(p => p.HelpPanels[key] = true);
            }
        });

        thread.IsBackground = true;
        writers.Add(thread);
        thread.Start();
    }

    foreach (Thread thread in writers)
    {
        thread.Join();
    }

    Expect("the deferred writes flushed within the budget", service.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    HashSet<string> keys = HelpPanelKeys();
    List<string> missing = [];
    for (int t = 0; t < Threads; t++)
    {
        for (int r = 0; r < Rounds; r++)
        {
            if (!keys.Contains($"thread-{t}-{r}"))
            {
                missing.Add($"thread-{t}-{r}");
            }
        }
    }

    cases++;
    if (missing.Count > 0)
    {
        Fail("threads: every concurrently written key survives",
            $"{missing.Count} of {Threads * Rounds} keys were lost, e.g. {string.Join(", ", missing.Take(5))}");
    }

    Expect("threads: the untouched group is intact", Witness());
    Console.WriteLine($"  threads: {Threads} writers x {Rounds} updates, {phase.ElapsedMilliseconds} ms, {missing.Count} lost");
}

// ---------------------------------------------------------------- P1: processes

// The same, across process boundaries, where an in-process lock would prove nothing. Each
// child writes its own namespace of keys and flushes before exiting; the parent writes at
// the same time, so the contention is real rather than staged between children only.
{
    const int Children = 4;
    const int Rounds = 30;

    Stopwatch phase = Stopwatch.StartNew();
    List<Process> children = [];
    for (int c = 0; c < Children; c++)
    {
        children.Add(Spawn("keys", sandbox, $"proc-{c}", Rounds.ToString()));
    }

    for (int r = 0; r < Rounds; r++)
    {
        string key = $"parent-{r}";
        service.Update(p => p.HelpPanels[key] = true);
    }

    List<string> exits = [];
    foreach (Process child in children)
    {
        if (!child.WaitForExit(60_000))
        {
            child.Kill(entireProcessTree: true);
            exits.Add("timed out");
        }
        else if (child.ExitCode != 0)
        {
            exits.Add($"exit {child.ExitCode}");
        }

        child.Dispose();
    }

    Expect("processes: every child exited cleanly", exits.Count == 0);
    Expect("processes: the parent's deferred writes flushed", service.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    HashSet<string> keys = HelpPanelKeys();
    List<string> missing = [];
    for (int c = 0; c < Children; c++)
    {
        for (int r = 0; r < Rounds; r++)
        {
            if (!keys.Contains($"proc-{c}-{r}"))
            {
                missing.Add($"proc-{c}-{r}");
            }
        }
    }

    for (int r = 0; r < Rounds; r++)
    {
        if (!keys.Contains($"parent-{r}"))
        {
            missing.Add($"parent-{r}");
        }
    }

    cases++;
    if (missing.Count > 0)
    {
        Fail("processes: every concurrently written key survives",
            $"{missing.Count} of {(Children + 1) * Rounds} keys were lost, e.g. {string.Join(", ", missing.Take(5))}");
    }

    // The thread phase's keys are still there too: a cross-process writer that overwrote
    // the document instead of merging into it would have taken them with it.
    Expect("processes: the earlier phase's keys are still there", keys.Contains("thread-0-0") && keys.Contains("thread-7-39"));
    Expect("processes: the untouched group is intact", Witness());
    Console.WriteLine($"  processes: {Children} children + parent x {Rounds} updates, {phase.ElapsedMilliseconds} ms, {missing.Count} lost");
}

// ---------------------------------------------------------------- P1: distinct groups

// The defect as it was actually found: not many keys in one map, but two SURFACES saving
// two unrelated preferences at the same moment. Different groups of the document, so a
// merge that works per-key but reserialises a stale copy of the rest still fails here.
{
    Stopwatch phase = Stopwatch.StartNew();
    Process other = Spawn("groups", sandbox, "unused", "0");

    ViewPrefsService mine = new();
    for (int r = 0; r < 200; r++)
    {
        mine.Update(p => p.FileHistory.FullHistory = true);
    }

    Expect("groups: the other instance exited cleanly", other.WaitForExit(60_000) && other.ExitCode == 0);
    other.Dispose();
    Expect("groups: the local writes flushed", mine.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    JsonElement root = Read().RootElement;
    Expect("groups: this instance's preference survived", root.GetProperty("FileHistory").GetProperty("FullHistory").GetBoolean());
    Expect("groups: the other instance's preference survived", root.GetProperty("CloseProcessDialog").GetBoolean());
    Expect("groups: the other instance's second preference survived", root.GetProperty("Merge").GetProperty("InlineMode").GetString() == "Base");
    Expect("groups: the untouched group is intact", Witness());
    Console.WriteLine($"  groups: two instances, unrelated preferences, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- P3: the reported interleaving

// The exact shape of the bug report, staged so that it cannot come out right by luck: one
// writer is slow between its load and its save, and a second writer completes in that
// window. Pre-fix, the slow writer saves a copy of the file taken before the second writer
// existed and the second preference is gone. The slow writer waits for the file itself to
// show the other change, so the test states the interleaving in terms of what is on disk
// rather than in terms of timing.
{
    Stopwatch phase = Stopwatch.StartNew();
    ViewPrefsService slow = new();
    ViewPrefsService quick = new();

    ManualResetEventSlim loaded = new(false);
    Thread fast = new(() =>
    {
        loaded.Wait(5000);
        quick.Update(p => p.FindInFiles.WholeWord = true);
        quick.Flush(TimeSpan.FromSeconds(20));
    });

    fast.IsBackground = true;
    fast.Start();

    slow.Update(p =>
    {
        loaded.Set();

        // Give the other writer every chance to land first. It cannot, once the interlock
        // is in place — which is the point: the wait expiring is a PASS, and the whole
        // cost of it is this one second.
        WaitUntil(() => Read().RootElement.GetProperty("FindInFiles").GetProperty("WholeWord").GetBoolean(), 1000);
        p.GridColumns.Author = 321;
    });

    fast.Join();
    Expect("interleaving: both writers flushed", slow.Flush(TimeSpan.FromSeconds(30)) && quick.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    JsonElement root = Read().RootElement;
    Expect("interleaving: the slow writer's preference survived", root.GetProperty("GridColumns").GetProperty("Author").GetDouble() == 321);
    Expect("interleaving: the quick writer's preference survived", root.GetProperty("FindInFiles").GetProperty("WholeWord").GetBoolean());
    Expect("interleaving: the untouched group is intact", Witness());
    Console.WriteLine($"  interleaving: slow load-mutate-save vs. a completed write, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- P2: never torn

// A reader doing what any second instance does at start-up, while four processes and this
// one rewrite the file as fast as they can. Every read must be a WHOLE document: the old
// WriteAllText truncates the target before it refills it, so a reader landing in that
// window gets an empty or half-written file — which Load() answers with "no preferences",
// i.e. the user's settings silently reset.
{
    Stopwatch phase = Stopwatch.StartNew();
    int reads = 0;
    int torn = 0;
    string firstTorn = string.Empty;
    using CancellationTokenSource stop = new();

    Thread reader = new(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                // A read that could not start at all is not a torn document; the rename
                // never leaves the path without a file, so this should not happen either,
                // and it is counted below as a read that yielded nothing.
                continue;
            }

            reads++;
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);

                // Parseable is not enough: "{}" parses. The witness group has been in
                // every version of this file since the seed, so its absence means the
                // reader saw a document that was never written whole.
                if (!document.RootElement.TryGetProperty("LeftPanel", out JsonElement panel)
                    || panel.GetProperty("SortKey").GetString() != "CommitDate")
                {
                    Interlocked.Increment(ref torn);
                    firstTorn = Excerpt(text);
                }
            }
            catch (JsonException)
            {
                Interlocked.Increment(ref torn);
                firstTorn = Excerpt(text);
            }
        }
    });

    reader.IsBackground = true;
    reader.Start();

    List<Process> hammers = [];
    for (int c = 0; c < 4; c++)
    {
        hammers.Add(Spawn("hammer", sandbox, $"hammer-{c}", "150"));
    }

    for (int r = 0; r < 150; r++)
    {
        string key = $"tear-{r}";
        service.Update(p => p.HelpPanels[key] = true);
    }

    foreach (Process hammer in hammers)
    {
        if (!hammer.WaitForExit(60_000))
        {
            hammer.Kill(entireProcessTree: true);
        }

        hammer.Dispose();
    }

    service.Flush(TimeSpan.FromSeconds(30));
    stop.Cancel();
    reader.Join();
    phase.Stop();

    cases++;
    if (torn > 0)
    {
        Fail("torn reads", $"{torn} of {reads} reads saw an incomplete document, first: {firstTorn}");
    }

    Expect("torn reads: the reader actually got to read", reads > 50);
    Console.WriteLine($"  torn: {reads} concurrent reads during {4} hammering processes, {phase.ElapsedMilliseconds} ms, {torn} torn");
}

// ---------------------------------------------------------------- P2: a writer killed mid-write

// SIGKILL, not a graceful stop: the process disappears between one syscall and the next,
// which is the only way to land inside a write. Two things must hold afterwards — the file
// still parses (the rename is what guarantees it), and the lock the dead process was
// holding is gone, so the next instance can still save. The second half is why the
// interlock is a flock on a sidecar and not a lock file with a pid in it: nothing here
// ever has to decide that a lock is stale.
{
    Stopwatch phase = Stopwatch.StartNew();
    int rounds = 6;
    int survived = 0;

    for (int r = 0; r < rounds; r++)
    {
        Process victim = Spawn("hammer", sandbox, $"victim-{r}", "100000");
        Thread.Sleep(120);
        victim.Kill(entireProcessTree: true);
        victim.WaitForExit(10_000);
        victim.Dispose();

        cases++;
        try
        {
            using JsonDocument document = Read();
            if (document.RootElement.GetProperty("LeftPanel").GetProperty("SortKey").GetString() != "CommitDate")
            {
                Fail($"kill round {r}", "the file parsed but had lost the witness written before the kill");
            }
            else
            {
                survived++;
            }
        }
        catch (JsonException ex)
        {
            Fail($"kill round {r}", "the file left behind does not parse: " + ex.Message);
        }

        // And the survivors can still write: a dead process must not have left an
        // interlock that outlives it.
        ViewPrefsService after = new();
        after.Update(p => p.HelpPanels[$"after-kill-{r}"] = true);
        Expect($"kill round {r}: a write after the kill still flushes", after.Flush(TimeSpan.FromSeconds(10)));
        Expect($"kill round {r}: a write after the kill reached the file", HelpPanelKeys().Contains($"after-kill-{r}"));
    }

    phase.Stop();
    Console.WriteLine($"  kills: {survived}/{rounds} SIGKILLs mid-write left a whole file, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- housekeeping

{
    // The temp files the atomic write stages through are named per process and thread, so
    // a clean run must leave none of them behind — a growing litter in the user's config
    // directory would be a defect of its own. Rounds where a process was SIGKILLed can
    // legitimately leave one, so only the parent's own name is checked.
    string[] leftovers = Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp-" + Environment.ProcessId + "-*");
    Expect("no temp file is left behind by a clean write", leftovers.Length == 0);

    // The file still says what the very first write said, after everything above.
    Expect("the witness survived the whole suite", Witness());
}

// ---------------------------------------------------------------- verdict

total.Stop();

try
{
    Directory.Delete(sandbox, recursive: true);
}
catch (IOException)
{
    // A leftover temp directory is not a test result; never fail the run over it.
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"PASS: {cases} view-preference concurrency cases, no lost update and no torn file ({total.ElapsedMilliseconds} ms)");
    return 0;
}

Console.WriteLine($"FAIL: {failures.Count} of {cases} view-preference concurrency cases broke ({total.ElapsedMilliseconds} ms)");
foreach (string failure in failures)
{
    Console.WriteLine("  " + failure);
}

return 1;

// ---------------------------------------------------------------- harness

void Expect(string name, bool condition)
{
    cases++;
    if (!condition)
    {
        Fail(name, "condition did not hold");
    }
}

void Fail(string name, string detail) => failures.Add($"{name}: {detail}");

// Every assertion goes through here rather than through ViewPrefsService.Load(), which
// replays this process's queued mutations and would mask a write that never landed.
JsonDocument Read() => JsonDocument.Parse(File.ReadAllText(file));

HashSet<string> HelpPanelKeys()
{
    using JsonDocument document = Read();
    HashSet<string> keys = [];
    if (document.RootElement.TryGetProperty("HelpPanels", out JsonElement panels))
    {
        foreach (JsonProperty property in panels.EnumerateObject())
        {
            keys.Add(property.Name);
        }
    }

    return keys;
}

// The two seeded values, in groups no phase writes to: they are how a whole-document
// clobber is told apart from a phase that merely got lucky with its own keys.
bool Witness()
{
    using JsonDocument document = Read();
    return document.RootElement.GetProperty("LeftPanel").GetProperty("SortKey").GetString() == "CommitDate"
        && document.RootElement.GetProperty("Diff").GetProperty("ContextLines").GetInt32() == 7;
}

Process Spawn(string role, string config, string tag, string rounds)
{
    // Works whether the suite was started through its apphost (dotnet run) or as
    // `dotnet <dll>`, because the two put a different thing in ProcessPath.
    string host = Environment.ProcessPath ?? "dotnet";
    ProcessStartInfo info = new() { FileName = host, UseShellExecute = false };

    if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        info.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
    }

    info.ArgumentList.Add("child");
    info.ArgumentList.Add(role);
    info.ArgumentList.Add(config);
    info.ArgumentList.Add(tag);
    info.ArgumentList.Add(rounds);

    // Explicit, not merely inherited: a child that fell back to the real XDG_CONFIG_HOME
    // would write the running user's own preferences, which this suite must never do.
    info.Environment["XDG_CONFIG_HOME"] = config;
    return Process.Start(info)!;
}

static void WaitUntil(Func<bool> condition, int budgetMs)
{
    long deadline = Environment.TickCount64 + budgetMs;
    while (Environment.TickCount64 < deadline)
    {
        try
        {
            if (condition())
            {
                return;
            }
        }
        catch (Exception)
        {
            // The file is being rewritten under us; that is what is being tested.
        }

        Thread.Sleep(5);
    }
}

static string Excerpt(string text)
    => text.Length == 0 ? "<empty>" : "\"" + text[..Math.Min(60, text.Length)].ReplaceLineEndings(" ") + "…\" (" + text.Length + " bytes)";

// The other side of every process case. Deliberately uses nothing but the public service,
// so a child is doing exactly what a second running copy of the app does.
static int Child(string[] args)
{
    string role = args[1];
    string config = args[2];
    string tag = args[3];
    int rounds = int.Parse(args[4]);

    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
    ViewPrefsService prefs = new();

    switch (role)
    {
        case "keys":
            for (int r = 0; r < rounds; r++)
            {
                string key = $"{tag}-{r}";
                prefs.Update(p => p.HelpPanels[key] = true);
            }

            break;

        case "groups":
            // Two preferences of two OTHER surfaces, written over and over so that they
            // overlap whatever the parent is doing.
            for (int r = 0; r < 200; r++)
            {
                prefs.Update(p => p.CloseProcessDialog = true);
                prefs.Update(p => p.Merge.InlineMode = "Base");
            }

            break;

        case "hammer":
            // Writes until it is told how many rounds, or until it is killed. No flush on
            // the way out for the kill rounds — there is nothing graceful about SIGKILL.
            for (int r = 0; r < rounds; r++)
            {
                string key = $"{tag}-{r}";
                prefs.Update(p => p.HelpPanels[key] = true);
            }

            break;
    }

    return prefs.Flush(TimeSpan.FromSeconds(30)) ? 0 : 2;
}
