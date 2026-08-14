using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GitExtensions.Avalonia.Services;

// Regression suite for the SIX settings files that sit next to view-prefs.json:
// app-settings.json, ui-state.json, commit-info.json, favorites.json, scripts.json and
// hotkeys.json — all of them now written through App/Services/JsonSettingsFile.cs.
//
// Usage: dotnet run --project Tests/SettingsStoresRegression/SettingsStoresRegression.Harness.csproj
//
// Exit code 0 means every case held; any other value means at least one broke, and each
// broken one is printed.
//
// Why this exists. Tests/ViewPrefsRegression pins the machinery through ONE document.
// These six adopted the same machinery afterwards, and adopting it is not one change but
// two: the file has to be written atomically, AND the call site has to stop writing the
// whole document back. Either half can be got wrong on its own, and neither failure is
// visible to a build, to a review of one call site, or to the user — the setting simply
// is not there next time. ui-state.json is the worst case and has its own phase below:
// the main window loads it once at start-up, keeps it for the whole session and used to
// write all of it back on close, so every setting any dialog stored in between was
// reverted by quitting the app.
//
// Every assertion reads the file's RAW BYTES and parses them itself, never a service's
// own Load(): Load replays this process's not-yet-written mutations as a courtesy, which
// would happily mask a write that never reached the disk.
//
// The properties under test, per store:
//
//  P1  No lost update. Writers of DIFFERENT fields — threads here, and a separate
//      process — all find their field in the file afterwards.
//  P2  No torn file. A reader hammering the file throughout, and a writer SIGKILLed
//      mid-write, never leave anything but a complete, parseable document.
//  P3  The list stores merge by element: two instances favoriting two different
//      repositories both keep theirs.

// ---------------------------------------------------------------- child roles

if (args.Length > 0 && args[0] == "child")
{
    return Child(args);
}

// ---------------------------------------------------------------- sandbox

// Every service resolves its file from XDG_CONFIG_HOME at CONSTRUCTION, so this has to
// happen before the first service exists. It is what stops this suite — which deliberately
// kills writers mid-write and hammers the result — from ever touching the settings of the
// person running it.
string sandbox = Path.Combine(Path.GetTempPath(), "gea-settings-harness-" + Environment.ProcessId);
Directory.CreateDirectory(sandbox);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", sandbox);

List<string> failures = [];
int cases = 0;

SettingsService settings = new();
UiStateService uiState = new();
CommitInfoSettingsService commitInfo = new();
FavoritesService favorites = new();
UserScriptService scripts = new();

string settingsFile = settings.FilePath;
string uiStateFile = uiState.FilePath;
string commitInfoFile = commitInfo.FilePath;
string favoritesFile = favorites.FilePath;
string scriptsFile = scripts.FilePath;

Stopwatch total = Stopwatch.StartNew();

// ---------------------------------------------------------------- P1: app-settings, threads

// Four writers, four DIFFERENT fields of the same document, each looping so the
// load-mutate-save windows are certain to overlap. Load-mutate-save loses fields here on
// the first run, every run.
{
    // 40, not more: every one of these fields is clamped by Sanitize (the tightest is
    // 0..50), and a test value the store legitimately rewrites would fail for the wrong
    // reason.
    const int Rounds = 40;
    Stopwatch phase = Stopwatch.StartNew();

    settings.Update(p => p.GitHubHost = "witness.example");

    (string Name, Action<AppPreferences, int> Write)[] writers =
    [
        ("CommitValidationFirstLineMaxChars", static (p, v) => p.CommitValidationFirstLineMaxChars = v),
        ("CommitValidationMaxCharsPerLine", static (p, v) => p.CommitValidationMaxCharsPerLine = v),
        ("CommitDialogNumberOfPreviousMessages", static (p, v) => p.CommitDialogNumberOfPreviousMessages = v),
        ("DiffVerticalRulerPosition", static (p, v) => p.DiffVerticalRulerPosition = v),
    ];

    RunConcurrently(writers.Length, index =>
    {
        SettingsService own = new();
        Action<AppPreferences, int> write = writers[index].Write;
        for (int r = 0; r < Rounds; r++)
        {
            // Bound to a fresh local, not to the loop variable: a mutation may be applied
            // later than the call that queued it, and a closure over `r` would then write
            // whatever round the loop had reached by then. That is the contract stated on
            // JsonSettingsFile.Update, and every call site in the app meets it.
            int value = r + 1;
            own.Update(p => write(p, value));
        }
    });

    Expect("app-settings: the deferred writes flushed", settings.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    JsonElement root = Read(settingsFile).RootElement;
    foreach ((string name, _) in writers)
    {
        Expect($"app-settings: {name} kept its writer's last value", root.GetProperty(name).GetInt32() == Rounds);
    }

    Expect("app-settings: the untouched field is intact", root.GetProperty("GitHubHost").GetString() == "witness.example");
    Console.WriteLine($"  app-settings: {writers.Length} threads x {Rounds} updates of distinct fields, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- P1: app-settings, processes

// The same across a process boundary, where an in-process lock would prove nothing: this
// is the two-instances case the port makes ordinary (the merge editor opens a second one).
{
    Stopwatch phase = Stopwatch.StartNew();
    Process child = Spawn("app-settings", sandbox, "0", "100");

    for (int r = 0; r < 100; r++)
    {
        int value = r + 1;
        settings.Update(p => p.RecentRepositoriesHistorySize = value);
    }

    Expect("app-settings: the other instance exited cleanly", child.WaitForExit(60_000) && child.ExitCode == 0);
    child.Dispose();
    Expect("app-settings: the local writes flushed", settings.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    JsonElement root = Read(settingsFile).RootElement;
    Expect("app-settings: this instance's field survived", root.GetProperty("RecentRepositoriesHistorySize").GetInt32() == 100);
    Expect("app-settings: the other instance's field survived", root.GetProperty("GitHubIssueCommitMessageCount").GetInt32() == 100);
    Expect("app-settings: the thread phase's fields are still there", root.GetProperty("DiffVerticalRulerPosition").GetInt32() == 40);
    Console.WriteLine($"  app-settings: parent + one other instance, distinct fields, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- P1: ui-state, the session-long window

// The reported shape, staged: one writer holds a document for a long time (the main
// window's session-old UiState) while another writes a different field, and only then does
// the first one save. Pre-fix the second field is gone — that is what quitting the app used
// to do to everything the dialogs had stored.
{
    Stopwatch phase = Stopwatch.StartNew();

    // What the main window has when it starts: a whole document, read once.
    UiState sessionOld = uiState.Load();
    sessionOld.WindowWidth = 1444;
    sessionOld.TreeWidth = 321;

    // Meanwhile, three dialogs write three other fields, exactly as they do now.
    new UiStateService().Update(s => s.Language = "Italiano");
    new UiStateService().Update(s => s.DefaultPullAction = "Rebase");
    new UiStateService().Update(s => s.AutoPullOnPushRejected = "Fetch");
    Expect("ui-state: the dialogs' writes flushed", uiState.Flush(TimeSpan.FromSeconds(30)));

    // And now the window closes. It must write ITS fields, not its whole snapshot.
    double width = sessionOld.WindowWidth;
    double tree = sessionOld.TreeWidth;
    uiState.Update(s =>
    {
        s.WindowWidth = width;
        s.TreeWidth = tree;
    });
    Expect("ui-state: the close-time write flushed", uiState.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    JsonElement root = Read(uiStateFile).RootElement;
    Expect("ui-state: the window's own geometry was saved", root.GetProperty("WindowWidth").GetDouble() == 1444);
    Expect("ui-state: and its tree width", root.GetProperty("TreeWidth").GetDouble() == 321);
    Expect("ui-state: the language a dialog wrote survived the close", root.GetProperty("Language").GetString() == "Italiano");
    Expect("ui-state: the pull action survived the close", root.GetProperty("DefaultPullAction").GetString() == "Rebase");
    Expect("ui-state: the push-rejected choice survived the close", root.GetProperty("AutoPullOnPushRejected").GetString() == "Fetch");
    Console.WriteLine($"  ui-state: a session-old snapshot closing over three dialogs' writes, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- P1: commit-info, two editors

// Six bools with two editors — the panel's context menu and the Settings dialog. Each
// toggle is written as a SET of a value computed before the call, never as a flip, which
// is what makes it safe to replay onto whatever the file says at write time.
{
    Stopwatch phase = Stopwatch.StartNew();
    Process child = Spawn("commit-info", sandbox, "0", "120");

    for (int r = 0; r < 120; r++)
    {
        new CommitInfoSettingsService().Update(s => s.ShowContainedInBranchesRemote = true);
    }

    Expect("commit-info: the other editor exited cleanly", child.WaitForExit(60_000) && child.ExitCode == 0);
    child.Dispose();
    Expect("commit-info: the local writes flushed", commitInfo.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    JsonElement root = Read(commitInfoFile).RootElement;
    Expect("commit-info: this editor's toggle survived", root.GetProperty("ShowContainedInBranchesRemote").GetBoolean());
    Expect("commit-info: the other editor's toggle survived", root.GetProperty("ShowContainedInBranchesRemoteIfNoLocal").GetBoolean());
    Expect("commit-info: the toggle neither of them touched is untouched", root.GetProperty("ShowContainedInTags").GetBoolean());
    Console.WriteLine($"  commit-info: two editors, distinct toggles, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- P3: favorites merge by element

// A list, not a record: the merge has to keep BOTH instances' entries, not just the last
// writer's list. Favoriting from the dashboard while a second instance favorites something
// else is the everyday version of this.
{
    const int Rounds = 120;
    Stopwatch phase = Stopwatch.StartNew();
    Process child = Spawn("favorites", sandbox, "other", Rounds.ToString());

    for (int r = 0; r < Rounds; r++)
    {
        favorites.Add($"/repos/mine-{r}");
    }

    Expect("favorites: the other instance exited cleanly", child.WaitForExit(60_000) && child.ExitCode == 0);
    child.Dispose();
    Expect("favorites: the local additions flushed", favorites.Flush(TimeSpan.FromSeconds(30)));
    phase.Stop();

    HashSet<string> stored = FavoritePaths(favoritesFile);
    List<string> missing = [];
    for (int r = 0; r < Rounds; r++)
    {
        if (!stored.Contains($"/repos/mine-{r}"))
        {
            missing.Add($"/repos/mine-{r}");
        }

        if (!stored.Contains($"/repos/other-{r}"))
        {
            missing.Add($"/repos/other-{r}");
        }
    }

    cases++;
    if (missing.Count > 0)
    {
        Fail("favorites: every concurrently added repository survives",
            $"{missing.Count} of {Rounds * 2} are missing, e.g. {string.Join(", ", missing.Take(5))}");
    }

    // The category filed by the other instance is on the entry, not lost to a plain
    // re-add: the two shapes (bare string, object) have to survive each other's writes.
    Expect("favorites: the other instance's category survived", CategoryOf(favoritesFile, "/repos/other-0") == "Work");
    Console.WriteLine($"  favorites: two instances x {Rounds} additions, {phase.ElapsedMilliseconds} ms, {missing.Count} lost");
}

// ---------------------------------------------------------------- P2: never torn

// A reader doing what any second instance does at start-up, while four processes rewrite
// ui-state.json as fast as they can. Every read must be a WHOLE document: WriteAllText
// truncates the target before it refills it, so a reader landing in that window gets an
// empty or half-written file — which Load() answers with defaults, i.e. the user's window
// size, theme and language silently reset.
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
                text = File.ReadAllText(uiStateFile);
            }
            catch (IOException)
            {
                continue;
            }

            reads++;
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);

                // Parseable is not enough: "{}" parses. The language has been in the file
                // since the phase above, so its absence means the reader saw a document
                // that was never written whole.
                if (!document.RootElement.TryGetProperty("Language", out JsonElement language)
                    || language.GetString() != "Italiano")
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
        hammers.Add(Spawn("hammer-ui", sandbox, $"hammer-{c}", "200"));
    }

    for (int r = 0; r < 200; r++)
    {
        int value = 500 + r;
        uiState.Update(s => s.WindowHeight = value);
    }

    foreach (Process hammer in hammers)
    {
        if (!hammer.WaitForExit(60_000))
        {
            hammer.Kill(entireProcessTree: true);
        }

        hammer.Dispose();
    }

    uiState.Flush(TimeSpan.FromSeconds(30));
    stop.Cancel();
    reader.Join();
    phase.Stop();

    cases++;
    if (torn > 0)
    {
        Fail("ui-state torn reads", $"{torn} of {reads} reads saw an incomplete document, first: {firstTorn}");
    }

    Expect("ui-state: the reader actually got to read", reads > 50);
    Console.WriteLine($"  torn: {reads} concurrent reads of ui-state during 4 hammering processes, {phase.ElapsedMilliseconds} ms, {torn} torn");
}

// ---------------------------------------------------------------- P2: killed mid-write

// SIGKILL, not a graceful stop: the process disappears between one syscall and the next,
// which is the only way to land inside a write. Run against the three stores whose writers
// are whole-document — scripts and hotkeys have exactly one editor each, so atomicity, not
// merging, is the whole of what they need.
{
    Stopwatch phase = Stopwatch.StartNew();
    (string Role, string File, string Witness)[] victims =
    [
        ("hammer-ui", uiStateFile, "Language"),
        ("hammer-scripts", scriptsFile, string.Empty),
        ("hammer-hotkeys", new HotkeyService().FilePath, string.Empty),
    ];

    foreach ((string role, string path, string witness) in victims)
    {
        string name = Path.GetFileName(path);
        Process victim = Spawn(role, sandbox, "victim", "100000");
        Thread.Sleep(250);
        victim.Kill(entireProcessTree: true);
        victim.WaitForExit(10_000);
        victim.Dispose();

        cases++;
        try
        {
            using JsonDocument document = Read(path);
            if (witness.Length > 0 && !document.RootElement.TryGetProperty(witness, out _))
            {
                Fail($"kill: {name}", "the file parsed but had lost the value written before the kill");
            }
        }
        catch (FileNotFoundException)
        {
            // The victim may have been killed before it ever wrote; nothing to check, and
            // an absent file is not a corrupt one.
            //
            // SAID OUT LOUD, because this branch drops the two assertions below and the
            // run then reports a smaller case count for no visible reason — measured, 39
            // instead of 41. A suite whose totals move on their own is a suite nobody can
            // read a regression out of.
            Console.WriteLine($"  kills: {name} was killed before it ever wrote — 2 assertions skipped for it");
            continue;
        }
        catch (JsonException ex)
        {
            Fail($"kill: {name}", "the file left behind does not parse: " + ex.Message);
        }

        // And the survivors can still write: a dead process must not have left an
        // interlock that outlives it. This is why the interlock is a flock on a sidecar
        // and not a lock file with a pid in it — nothing ever has to decide it is stale.
        UiStateService after = new();
        after.Update(s => s.SystemThemeSeen = "Light");
        Expect($"kill: {name}: a write after the kill still flushes", after.Flush(TimeSpan.FromSeconds(10)));
        Expect($"kill: {name}: a write after the kill reached the file",
            Read(uiStateFile).RootElement.GetProperty("SystemThemeSeen").GetString() == "Light");
    }

    phase.Stop();
    Console.WriteLine($"  kills: {victims.Length} SIGKILLs mid-write left a whole file, {phase.ElapsedMilliseconds} ms");
}

// ---------------------------------------------------------------- housekeeping

{
    // The temp files the atomic write stages through are named per process and thread, so
    // a clean run must leave none of the PARENT's behind — a growing litter in the user's
    // config directory would be a defect of its own. Rounds where a process was SIGKILLed
    // can legitimately leave one, hence the process-id filter.
    string[] leftovers = Directory.GetFiles(
        Path.GetDirectoryName(uiStateFile)!, "*.tmp-" + Environment.ProcessId + "-*");
    Expect("no temp file is left behind by a clean write", leftovers.Length == 0);

    // Every store still holds what the phases above put in it, after everything since.
    Expect("app-settings still holds its witness",
        Read(settingsFile).RootElement.GetProperty("GitHubHost").GetString() == "witness.example");
    Expect("ui-state still holds the language",
        Read(uiStateFile).RootElement.GetProperty("Language").GetString() == "Italiano");
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
    Console.WriteLine($"PASS: {cases} settings-store concurrency cases, no lost update and no torn file ({total.ElapsedMilliseconds} ms)");
    return 0;
}

Console.WriteLine($"FAIL: {failures.Count} of {cases} settings-store concurrency cases broke ({total.ElapsedMilliseconds} ms)");
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

// Runs `count` threads, one per index, and waits for all of them. The barrier is what
// makes the writes genuinely simultaneous rather than merely interleaved.
static void RunConcurrently(int count, Action<int> body)
{
    Barrier start = new(count);
    List<Thread> threads = [];
    for (int i = 0; i < count; i++)
    {
        int index = i;
        Thread thread = new(() =>
        {
            start.SignalAndWait();
            body(index);
        })
        {
            IsBackground = true,
        };

        threads.Add(thread);
        thread.Start();
    }

    foreach (Thread thread in threads)
    {
        thread.Join();
    }
}

// Every assertion goes through here rather than through a service's Load(), which replays
// this process's queued mutations and would mask a write that never landed.
static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));

// favorites.json is an array of either a bare path or {path, category}, so it is read the
// way the file is written rather than through the service.
static HashSet<string> FavoritePaths(string path)
{
    HashSet<string> paths = [];
    using JsonDocument document = Read(path);
    foreach (JsonElement entry in document.RootElement.EnumerateArray())
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            paths.Add(entry.GetString()!);
        }
        else if (entry.TryGetProperty("path", out JsonElement stored))
        {
            paths.Add(stored.GetString()!);
        }
    }

    return paths;
}

static string? CategoryOf(string path, string repo)
{
    using JsonDocument document = Read(path);
    foreach (JsonElement entry in document.RootElement.EnumerateArray())
    {
        if (entry.ValueKind == JsonValueKind.Object
            && entry.TryGetProperty("path", out JsonElement stored)
            && stored.GetString() == repo)
        {
            return entry.TryGetProperty("category", out JsonElement category) ? category.GetString() : null;
        }
    }

    return null;
}

static string Excerpt(string text)
    => text.Length == 0 ? "<empty>" : "\"" + text[..Math.Min(60, text.Length)].ReplaceLineEndings(" ") + "…\" (" + text.Length + " bytes)";

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
    // would write the running user's own settings, which this suite must never do.
    info.Environment["XDG_CONFIG_HOME"] = config;
    return Process.Start(info)!;
}

// The other side of every process case. Deliberately uses nothing but the public services,
// so a child is doing exactly what a second running copy of the app does.
static int Child(string[] args)
{
    string role = args[1];
    string config = args[2];
    string tag = args[3];
    int rounds = int.Parse(args[4]);

    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);

    switch (role)
    {
        case "app-settings":
        {
            SettingsService own = new();
            for (int r = 0; r < rounds; r++)
            {
                int value = r + 1;
                own.Update(p => p.GitHubIssueCommitMessageCount = value);
            }

            return own.Flush(TimeSpan.FromSeconds(30)) ? 0 : 2;
        }

        case "commit-info":
        {
            CommitInfoSettingsService own = new();
            for (int r = 0; r < rounds; r++)
            {
                own.Update(s => s.ShowContainedInBranchesRemoteIfNoLocal = true);
            }

            return own.Flush(TimeSpan.FromSeconds(30)) ? 0 : 2;
        }

        case "favorites":
        {
            FavoritesService own = new();
            for (int r = 0; r < rounds; r++)
            {
                own.Add($"/repos/{tag}-{r}");
            }

            // One of them filed, so the object shape and the bare-string shape have to
            // survive each other's merges.
            own.AssignCategory($"/repos/{tag}-0", "Work");
            return own.Flush(TimeSpan.FromSeconds(30)) ? 0 : 2;
        }

        case "hammer-ui":
        {
            // Writes until it is told how many rounds, or until it is killed. No flush on
            // the way out for the kill rounds — there is nothing graceful about SIGKILL.
            UiStateService own = new();
            for (int r = 0; r < rounds; r++)
            {
                int value = 400 + (r % 500);
                own.Update(s => s.WindowHeight = value);
            }

            return own.Flush(TimeSpan.FromSeconds(30)) ? 0 : 2;
        }

        case "hammer-scripts":
        {
            UserScriptService own = new();
            for (int r = 0; r < rounds; r++)
            {
                int index = r;
                own.Save([new UserScript { Name = $"{tag}-{index}", Command = "true" }]);
            }

            return own.Flush(TimeSpan.FromSeconds(30)) ? 0 : 2;
        }

        case "hammer-hotkeys":
        {
            HotkeyService own = new();
            for (int r = 0; r < rounds; r++)
            {
                own.Save();
            }

            return own.Flush(TimeSpan.FromSeconds(30)) ? 0 : 2;
        }
    }

    return 3;
}
