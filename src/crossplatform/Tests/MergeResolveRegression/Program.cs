using System.Diagnostics;
using System.Text;
using GitExtensions.Avalonia.Services;

// Regression suite for the merge-resolution contract: what a save does to the index,
// what puts a conflict back, and which staged files still carry conflict markers.
//
// Usage: dotnet run --project Tests/MergeResolveRegression/MergeResolveRegression.Harness.csproj
//
// Exit code 0 means every case held; any other value means at least one did not, and
// each is printed with expected against actual.
//
// Why this exists. Every fact below is a fact about GIT, not about the port's own
// bookkeeping, and each one was measured on a live repository before it was relied on:
//
//   * `git add` on a conflicted path DESTROYS index stages 1/2/3. That is what ends a
//     conflict — and it ends it for every tool at once: ls-files --unmerged goes empty,
//     `git mergetool` answers "No files need merging", and neither this port's editor
//     nor kdiff3 can be pointed at the file again. The old MergeToolService.Save staged
//     unconditionally, so a half-finished resolution locked the user out of every tool,
//     which is the defect this suite pins.
//
//   * git does NOT object to conflict markers. A staged file full of "<<<<<<< HEAD"
//     commits silently, so nothing downstream catches what a save lets through.
//
//   * `git checkout --merge -- <path>` rebuilds the stages and rewrites the work-tree
//     file with markers, which is the only way back — and it discards whatever was
//     resolved, which is why the UI asks first.
//
// The suite drives the real services against real repositories in a scratch directory,
// because a mock of git would only assert what the port already believes.

// The core's git plumbing reaches ThreadHelper.JoinableTaskFactory, which the real
// app initialises from its message loop and a console harness has to initialise for
// itself — the app does exactly this at startup.
GitUI.CrossPlatformBootstrap.InitializeThreading();

List<string> failures = [];
int cases = 0;

string scratch = Path.Combine(
    Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(),
    "ge-merge-resolve-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);

try
{
    await RunSaveLeavesConflictOpenAsync();
    await RunSaveCleanStagesAsync();
    RunReopenAfterStage();
    RunMarkerScan();
}
finally
{
    try
    {
        Directory.Delete(scratch, recursive: true);
    }
    catch (Exception)
    {
        // The evidence is worth more than the disk; run-all.sh keeps its own scratch.
    }
}

if (failures.Count > 0)
{
    foreach (string failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    Console.WriteLine($"FAILED: {failures.Count} of {cases} merge-resolution cases");
    return 1;
}

Console.WriteLine($"PASS: {cases} merge-resolution cases (index stages, reopen, marker scan) against real repositories");
return 0;

// ---------------------------------------------------------------- the cases

// A save that still has markers in it must NOT stage: the file stays unmerged, so
// every tool can still open it. This is the case that used to lock the user out.
async Task RunSaveLeavesConflictOpenAsync()
{
    string repo = Conflicted("save-leaves-conflict-open");
    ConflictEntry entry = OnlyConflict(repo);

    MergeToolService service = new();
    (MergeDocument? document, string? error) = await service.PrepareAsync(repo, entry);
    Check("the merge is prepared", document is not null, error ?? "no document");
    if (document is null)
    {
        return;
    }

    // Half-resolved on purpose: first conflict answered, second left marked.
    string half = Half(repo, entry.Path);
    Check("the half-resolved text still has markers", half.Contains("<<<<<<<", StringComparison.Ordinal), half);

    ConflictActionResult saved = service.Save(repo, document, half, markResolved: false);
    Check("the save succeeds", saved.Success, saved.Message);
    Check("the text reached the work tree",
        File.ReadAllText(Path.Combine(repo, entry.Path)).Contains("BOTH-IMPORTS", StringComparison.Ordinal),
        "marker text not written");

    // The whole point: still unmerged, so kdiff3/mergetool/this editor all still work.
    Check("the file is still unmerged", Unmerged(repo).Contains(entry.Path), Join(Unmerged(repo)));
    Check("git mergetool still has work",
        !Git(repo, "mergetool --no-prompt --tool=true").Contains("No files need merging", StringComparison.Ordinal),
        "mergetool reported nothing to do");
    Check("the conflict list still names it",
        new ConflictService().ListConflicts(repo).Any(c => c.Path == entry.Path),
        "the dialog would show an empty list");
}

// A save with nothing left marked DOES stage: that is what "resolved" means.
async Task RunSaveCleanStagesAsync()
{
    string repo = Conflicted("save-clean-stages");
    ConflictEntry entry = OnlyConflict(repo);

    MergeToolService service = new();
    (MergeDocument? document, _) = await service.PrepareAsync(repo, entry);
    if (document is null)
    {
        Check("the merge is prepared", false, "no document");
        return;
    }

    ConflictActionResult saved = service.Save(repo, document, Clean(), markResolved: true);
    Check("the clean save succeeds", saved.Success, saved.Message);
    Check("a clean save stages the file", Unmerged(repo).Count == 0, Join(Unmerged(repo)));
    Check("nothing marked is left in the index",
        new ConflictService().ListStagedWithMarkers(repo).Count == 0,
        "the marker scan flagged a clean resolution");
}

// The way back, and its cost: stages return, the resolved text does not.
void RunReopenAfterStage()
{
    string repo = Conflicted("reopen-after-stage");
    ConflictEntry entry = OnlyConflict(repo);
    string full = Path.Combine(repo, entry.Path);

    // What a hand-run `git add` (or the port's old unconditional save) leaves behind:
    // a staged file with markers still in it, and no tool able to open it.
    File.WriteAllText(full, File.ReadAllText(full) + "\nSTAGED-HALF\n");
    Git(repo, $"add -- {entry.Path}");
    Check("staging removes the stages", Unmerged(repo).Count == 0, Join(Unmerged(repo)));
    Check("git mergetool has nothing left",
        Git(repo, "mergetool --no-prompt --tool=true").Contains("No files need merging", StringComparison.Ordinal),
        "mergetool still offered the file");

    ConflictService conflicts = new();
    ConflictActionResult reopened = conflicts.ReopenConflict(repo, entry.Path);
    Check("reopen succeeds", reopened.Success, reopened.Message);
    Check("reopen brings the stages back", Unmerged(repo).Contains(entry.Path), Join(Unmerged(repo)));
    Check("reopen brings the markers back",
        File.ReadAllText(full).Contains("<<<<<<<", StringComparison.Ordinal),
        "no markers in the work-tree file");

    // Stated in the confirm dialog, so it is asserted here: reopening discards work.
    Check("reopen discards what was saved",
        !File.ReadAllText(full).Contains("STAGED-HALF", StringComparison.Ordinal),
        "the discarded text survived, so the dialog's warning is wrong");
}

// The scan behind the banner: it must find a staged file with markers, and must not
// mistake ordinary text for one.
void RunMarkerScan()
{
    string repo = Conflicted("marker-scan");
    ConflictEntry entry = OnlyConflict(repo);
    string full = Path.Combine(repo, entry.Path);

    ConflictService conflicts = new();
    Check("an unmerged file is not reported as staged-with-markers",
        conflicts.ListStagedWithMarkers(repo).Count == 0,
        "the scan looked at the work tree instead of the index");

    // A Markdown rule and a document that QUOTES one marker: neither is a conflict,
    // and both are exactly what a naive grep gets wrong.
    File.WriteAllText(Path.Combine(repo, "notes.md"), "Title\n=======\nA line about <<<<<<< markers.\n");
    Git(repo, "add -- notes.md");

    Git(repo, $"add -- {entry.Path}");
    IReadOnlyList<string> marked = conflicts.ListStagedWithMarkers(repo);
    Check("the staged conflict is found", marked.Contains(entry.Path), Join(marked));
    Check("a Markdown rule is not a conflict", !marked.Contains("notes.md"), Join(marked));

    // And once it is genuinely resolved, the banner has to go away.
    Git(repo, $"checkout --merge -- {entry.Path}");
    File.WriteAllText(full, Clean());
    Git(repo, $"add -- {entry.Path}");
    Check("a resolved file is no longer reported",
        !conflicts.ListStagedWithMarkers(repo).Contains(entry.Path),
        Join(conflicts.ListStagedWithMarkers(repo)));
}

// ---------------------------------------------------------------- fixtures

// A repository stopped in a two-region conflict in one file — the shape of the real
// merge this suite was written for: an import line and a body, each changed on both
// sides.
string Conflicted(string name)
{
    string repo = Path.Combine(scratch, name);
    Directory.CreateDirectory(repo);

    Git(repo, "init -q -b main");
    Git(repo, "config user.name Harness");
    Git(repo, "config user.email harness@example.invalid");
    Git(repo, "config commit.gpgsign false");

    File.WriteAllText(Path.Combine(repo, "service.ts"), Base());
    Git(repo, "add -A");
    Git(repo, "commit -q -m base");

    Git(repo, "checkout -q -b theirs");
    File.WriteAllText(Path.Combine(repo, "service.ts"), Theirs());
    Git(repo, "commit -q -am theirs");

    Git(repo, "checkout -q main");
    File.WriteAllText(Path.Combine(repo, "service.ts"), Ours());
    Git(repo, "commit -q -am ours");

    Git(repo, "merge theirs");   // conflicts on purpose; exit code is the point
    return repo;
}

ConflictEntry OnlyConflict(string repo)
{
    IReadOnlyList<ConflictEntry> found = new ConflictService().ListConflicts(repo);
    Check("the fixture conflicts in exactly one file", found.Count == 1, $"{found.Count} conflicted paths");
    return found.Count > 0
        ? found[0]
        : new ConflictEntry("service.ts", ConflictSide.Missing, ConflictSide.Missing, ConflictSide.Missing);
}

// The filler is load-bearing: git merges two conflicts into ONE block when they are
// closer than its context, and this suite needs two separate regions — the shape of
// the real merge it came from (an import line and a method body).
const string Filler = """

class Service {
    private one = 1
    private two = 2
    private three = 3
    private four = 4

    public untouched(): void {
        // nothing here is edited by either side
    }

""";

static string Base() => "import { a } from './a'" + Filler + "    public remove(): void {\n        drop()\n    }\n}\n";

static string Ours() => "import { a } from './a'\nimport { ours } from './ours'" + Filler
    + "    public remove(): void {\n        cleanup()\n    }\n}\n";

static string Theirs() => "import { a } from './a'\nimport { theirs } from './theirs'" + Filler
    + "    public remove(): void {\n        purge()\n        drop()\n    }\n}\n";

// Both imports kept (the answer a "Both: L → R" gives) with the SECOND region left
// marked: the state a save must not stage. Read from the work-tree file, which is
// where git has already written its own markers.
string Half(string repo, string path)
{
    string text = File.ReadAllText(Path.Combine(repo, path));
    int start = text.IndexOf("<<<<<<<", StringComparison.Ordinal);
    int end = text.IndexOf('\n', text.IndexOf(">>>>>>>", StringComparison.Ordinal));
    if (start < 0 || end < 0)
    {
        Check("the conflicted file carries markers", false, text);
        return text;
    }

    // Replace only the FIRST conflict block, leaving the second one as git wrote it.
    return text[..start] + "import { ours } from './ours' // BOTH-IMPORTS\nimport { theirs } from './theirs'"
        + text[(end)..];
}

static string Clean() =>
    "import { a } from './a'\nimport { ours } from './ours'\nimport { theirs } from './theirs'" + Filler
        + "    public remove(): void {\n        purge()\n        cleanup()\n    }\n}\n";

// ---------------------------------------------------------------- plumbing

IReadOnlyList<string> Unmerged(string repo) =>
    [.. Git(repo, "ls-files -u")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('\t').Last().Trim())
        .Distinct(StringComparer.Ordinal)];

static string Join(IEnumerable<string> values) => string.Join(", ", values);

string Git(string repo, string arguments)
{
    ProcessStartInfo info = new("git", arguments)
    {
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    // The harness runs with GIT_CONFIG_GLOBAL/SYSTEM silenced by run-all.sh; setting
    // the identity per repository above keeps it standalone too.
    using Process process = Process.Start(info)!;
    StringBuilder output = new();
    output.Append(process.StandardOutput.ReadToEnd());
    output.Append(process.StandardError.ReadToEnd());
    process.WaitForExit();
    return output.ToString();
}

void Check(string what, bool held, string detail)
{
    cases++;
    if (!held)
    {
        failures.Add($"{what} — got: {detail}");
    }
}
