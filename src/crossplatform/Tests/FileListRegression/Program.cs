using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GitExtensions.Avalonia.Services;

// Regression suite for how the changed-file lists GROUP, HIDE and REMEMBER their rows.
//
// Usage: dotnet run --project Tests/FileListRegression/FileListRegression.Harness.csproj [App directory]
//
// Exit code 0 means every case held; any other value means at least one broke, and each
// broken one is printed.
//
// WHY THIS EXISTS (M226). A user grouped the commit dialog's work-tree list by file path
// and clicked the arrow to fold a folder. The folder's files did not hide — they were
// GONE, and with the only top-level folder folded the whole pane emptied itself. The
// cause was not in the grouping code, which was right: the pane had no record of its rows
// other than the ListBox's own items, and every question about them ("regroup", "how many
// files", "which files does Stage all act on") was answered by reading
// List.Items.OfType<WorkingDirFileRow>(). A folded group keeps its subtree out of those
// items by design, so each of those reads silently shrank to what was on screen, and the
// next rebuild started from the shrunken set. The sibling control never had the defect
// because it keeps its rows in a field (FileStatusListView._files) — the bug was born
// with the second implementation, not with the feature.
//
// Nothing here needs a display or an Avalonia application: the grouping is a pure
// function in a service, the invariant the dialog broke is checked against its SOURCE,
// and the remembered preference is a JSON document.
//
// The five groups of cases:
//
//  A  DiffFileListBuilder — the grouping itself, which no harness covered until now.
//     Above all: FOLDING IS NOT REMOVING. A collapsed key hides rows from the items and
//     changes neither the reported file count nor the caller's input list.
//  B  The commit dialog's source, which must not ask its ListBox what the pane holds.
//     A lint, because the code it guards cannot be constructed without a window.
//  C  The grouping each list opens with: defaults, round-trip, a hand-edited file, and
//     the enum bridge between the dialog's own FileSortMode and the stored shape.
//  D  The folder menu's reach: which rows "this folder" means, worked out BACKWARDS from
//     a header because a folded folder shows none of its files. Two independent
//     computations of one set — the list builder's and the menu's — cross-checked
//     against each other through the count the header prints.
//  E  What a folder-wide stash does, driven through the real service against real
//     repositories: three facts about git, each measured before the menu relied on it.

// The reused git core runs its commands through a JoinableTaskFactory, which the app
// initialises at start-up: group E drives real git through the real service, so it has to
// do the same or the first call throws instead of failing an assertion.
GitUI.CrossPlatformBootstrap.InitializeThreading();

List<string> failures = [];
int cases = 0;

string appDirectory = args.Length > 0 ? args[0] : FindAppDirectory();

// ---------------------------------------------------------------- A: the grouping

// Two folders, a nested one and a file at the root: enough for a tree with a level of
// depth, a folder that is a prefix of nothing, and one row outside every folder.
DiffFileRow[] rows =
[
    Row("App/Views/CommitDialog.cs"),
    Row("App/Views/DiffView.cs"),
    Row("App/Services/DiffService.cs"),
    Row("README.md"),
];

{
    (List<object> items, int count) = Build(rows, DiffFileGroupMode.None, asTree: false, []);
    Expect("no grouping: one node per row and no headers",
        items.Count == 4 && items.All(i => i is FileListFileNode));
    Expect("no grouping: the count is the number of files", count == 4);
    Expect("no grouping: the rows show their full path",
        items.OfType<FileListFileNode>().Any(n => n.Display == "App/Views/CommitDialog.cs"));
}

{
    (List<object> items, int count) = Build(rows, DiffFileGroupMode.Path, asTree: true, []);
    List<FileListGroupNode> headers = [.. items.OfType<FileListGroupNode>()];

    Expect("path tree: a header per folder, at its own level",
        headers.Count == 3
        && headers.Any(h => h.Key == "App" && h.Level == 0)
        && headers.Any(h => h.Key == "App/Views" && h.Level == 1)
        && headers.Any(h => h.Key == "App/Services" && h.Level == 1));

    // The count on a folder is what makes a folded folder legible at all: it is the only
    // thing left on screen saying how much is under it.
    Expect("path tree: a folder counts its descendants, not its direct children",
        headers.Single(h => h.Key == "App").Count == 3);
    Expect("path tree: every file is still there", count == 4);
    Expect("path tree: a file inside a folder shows its name alone",
        items.OfType<FileListFileNode>().Any(n => n.Display == "CommitDialog.cs"));
}

{
    // The reported gesture: fold ONE folder.
    (List<object> items, int count) = Build(rows, DiffFileGroupMode.Path, asTree: true, ["App/Views"]);

    Expect("folding a folder keeps its own header",
        items.OfType<FileListGroupNode>().Any(h => h.Key == "App/Views" && h.IsCollapsed));
    Expect("folding a folder hides the files under it",
        !items.OfType<FileListFileNode>().Any(n => n.Row.Name.StartsWith("App/Views/", StringComparison.Ordinal)));
    Expect("folding a folder leaves its sibling alone",
        items.OfType<FileListFileNode>().Any(n => n.Row.Name == "App/Services/DiffService.cs"));

    // THE case. A count that drops when a folder is folded is the same mistake the commit
    // dialog made, one layer up: it means "hidden" was implemented as "gone".
    Expect("folding a folder does not change the file count", count == 4);
    Expect("folding a folder still counts them in the header",
        items.OfType<FileListGroupNode>().Single(h => h.Key == "App/Views").Count == 2);
}

{
    // Fold the ONLY top-level folder, which is what emptied the user's pane.
    (List<object> items, int count) = Build(
        [Row("App/Views/CommitDialog.cs"), Row("App/Services/DiffService.cs")],
        DiffFileGroupMode.Path,
        asTree: true,
        ["App"]);

    Expect("folding the only root leaves the root visible",
        items.Count == 1 && items[0] is FileListGroupNode { Key: "App", IsCollapsed: true });
    Expect("folding the only root does not empty the list", count == 2);
}

{
    (List<object> items, int count) = Build(rows, DiffFileGroupMode.Path, asTree: false, []);
    List<FileListGroupNode> headers = [.. items.OfType<FileListGroupNode>()];
    Expect("flat path grouping: one header per full directory, all at level 0",
        headers.Count == 3 && headers.All(h => h.Level == 0));
    Expect("flat path grouping: no directory is nested inside another",
        headers.Any(h => h.Key.Contains("Views", StringComparison.Ordinal))
        && headers.Any(h => h.Key.Contains("Services", StringComparison.Ordinal)));
    Expect("flat path grouping: every file is still there", count == 4);
}

{
    (List<object> items, int count) = Build(
        rows,
        DiffFileGroupMode.Extension,
        asTree: false,
        [],
        grouper: r => new DiffFileListBuilder.GroupLabel(
            Path.GetExtension(r.Name), Path.GetExtension(r.Name)));

    Expect("extension grouping: one header per extension",
        items.OfType<FileListGroupNode>().Count() == 2);
    Expect("extension grouping: every file is still there", count == 4);

    (List<object> folded, int foldedCount) = Build(
        rows,
        DiffFileGroupMode.Extension,
        asTree: false,
        [".cs"],
        grouper: r => new DiffFileListBuilder.GroupLabel(
            Path.GetExtension(r.Name), Path.GetExtension(r.Name)));

    Expect("extension grouping: folding hides the files", folded.OfType<FileListFileNode>().Count() == 1);
    Expect("extension grouping: folding does not change the count", foldedCount == 4);
}

{
    // The filter is the one thing that legitimately lowers the count, which is what makes
    // the cases above meaningful rather than tautological.
    (List<object> items, int count) = Build(
        rows, DiffFileGroupMode.None, asTree: false, [], filter: "Views");
    Expect("the filter, unlike folding, does lower the count", count == 2 && items.Count == 2);
}

{
    // The builder is handed the caller's own list; a grouping that reordered or trimmed it
    // in place would corrupt the pane's record of its rows even with the M226 fix in.
    List<DiffFileRow> input = [.. rows];
    Build(input, DiffFileGroupMode.Path, asTree: true, ["App"]);
    Build(input, DiffFileGroupMode.Status, asTree: false, [], grouper: _ => new("k", "k"));
    Expect("the builder does not touch the list it is given",
        input.Count == 4 && input[0].Name == "App/Views/CommitDialog.cs");
}

// ---------------------------------------------------------------- B: the dialog's source

// The defect was a READ: the pane asked its ListBox for its files. It cannot be reached
// by constructing anything (CommitDialog is a Window and needs a display and a
// repository), so it is pinned where it lives — in the text of the file.
//
// The rule, and the reason for its one exception: you may ask the list which row is ON
// SCREEN, because only a visible row can be selected; you may not ask it what the pane
// HOLDS, because a folded group means the list does not know.
{
    string source = Path.Combine(appDirectory, "Views", "CommitDialog.cs");
    Expect($"the dialog's source is where it is expected ({source})", File.Exists(source));

    if (File.Exists(source))
    {
        string text = File.ReadAllText(source);
        string[] receivers = ["_stagedList.Items", "_unstagedList.Items", "pane.List.Items", "list.Items"];
        List<string> offenders = [];

        foreach (string receiver in receivers)
        {
            for (int at = text.IndexOf(receiver, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(receiver, at + 1, StringComparison.Ordinal))
            {
                // ".ItemsSource" is an assignment, not a question: skip anything whose
                // member name merely STARTS with Items.
                int after = at + receiver.Length;
                if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
                {
                    continue;
                }

                // Whitespace collapsed, because these expressions are routinely wrapped
                // over three lines and a line-by-line lint would miss half of them.
                string rest = string.Concat(text[after..Math.Min(text.Length, after + 160)]
                    .Where(c => !char.IsWhiteSpace(c)));

                // Selecting a row that is ON SCREEN: the sanctioned use, and the only one.
                if (rest.StartsWith(".OfType<WorkingDirFileRow>().FirstOrDefault(", StringComparison.Ordinal))
                {
                    continue;
                }

                int line = text.Take(at).Count(c => c == '\n') + 1;
                offenders.Add($"CommitDialog.cs:{line}: {receiver}{rest[..Math.Min(40, rest.Length)]}");
            }
        }

        Expect(
            "the dialog never asks its ListBox what the pane holds"
                + (offenders.Count == 0 ? string.Empty : " — " + string.Join(" | ", offenders)),
            offenders.Count == 0);

        // The other half of the same invariant: there IS a record to read instead, and the
        // rebuild reads it. A lint that only forbids the wrong source passes just as well
        // on a file that has stopped tracking the rows at all.
        Expect("the pane keeps its own row list",
            text.Contains("public IReadOnlyList<WorkingDirFileRow> Rows", StringComparison.Ordinal)
            && text.Contains("BuildItems(pane, pane.Rows)", StringComparison.Ordinal));
    }
}

// ---------------------------------------------------------------- C: the remembered grouping

// Its own config directory: this writes preferences, and it must never be the ones of the
// person running it. ViewPrefsService resolves the path at CONSTRUCTION, so this comes
// before the first service exists.
string sandbox = Path.Combine(Path.GetTempPath(), "gea-filelist-harness-" + Environment.ProcessId);
Directory.CreateDirectory(sandbox);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", sandbox);

{
    FileListPrefs fresh = new();
    Expect("a first run groups nothing, in all three lists",
        fresh.Diff.Group == DiffFileGroupMode.None
        && fresh.CommitUnstaged.Group == DiffFileGroupMode.None
        && fresh.CommitStaged.Group == DiffFileGroupMode.None);
    Expect("a first run has the path grouping nest, in all three lists",
        fresh.Diff.AsTree && fresh.CommitUnstaged.AsTree && fresh.CommitStaged.AsTree);
}

ViewPrefsService prefs = new();

{
    prefs.Update(p => p.FileList.CommitUnstaged = new FileListGrouping
    {
        Group = DiffFileGroupMode.Path,
        AsTree = false,
    });
    Expect("the grouping reached the disk", prefs.Flush(TimeSpan.FromSeconds(20)));

    // The file's raw bytes, not Load(): Load replays this process's queued mutations as a
    // courtesy, which would mask a write that never landed.
    JsonElement stored = JsonDocument.Parse(File.ReadAllText(prefs.FilePath)).RootElement
        .GetProperty("FileList");

    Expect("the work-tree list's grouping is what was chosen",
        stored.GetProperty("CommitUnstaged").GetProperty("Group").GetInt32() == (int)DiffFileGroupMode.Path
        && !stored.GetProperty("CommitUnstaged").GetProperty("AsTree").GetBoolean());
    Expect("the other two lists are untouched by it",
        stored.GetProperty("CommitStaged").GetProperty("Group").GetInt32() == (int)DiffFileGroupMode.None
        && stored.GetProperty("Diff").GetProperty("Group").GetInt32() == (int)DiffFileGroupMode.None);

    FileListPrefs read = new ViewPrefsService().Load().FileList;
    Expect("and it is what the next window would open with",
        read.CommitUnstaged.Group == DiffFileGroupMode.Path && !read.CommitUnstaged.AsTree);
}

{
    // A file from an older build has no FileList at all; one from a later build — or one
    // hand-edited — can carry a grouping this build has no case for. Either must open a
    // list the user can use, not an empty one.
    File.WriteAllText(prefs.FilePath, """
    {
      "LeftPanel": { "SortKey": "CommitDate" },
      "FileList": { "Diff": { "Group": 99, "AsTree": true }, "CommitStaged": null }
    }
    """);

    ViewPrefs recovered = new ViewPrefsService().Load();
    Expect("an unknown grouping collapses to the flat list",
        recovered.FileList.Diff.Group == DiffFileGroupMode.None);
    Expect("a missing list gets the defaults",
        recovered.FileList.CommitStaged is { Group: DiffFileGroupMode.None, AsTree: true });
    Expect("and the rest of the document is still read",
        recovered.LeftPanel.SortKey == "CommitDate");

    File.WriteAllText(prefs.FilePath, """{ "LeftPanel": { "SortKey": "Name" } }""");
    Expect("a document without the group at all gets it whole",
        new ViewPrefsService().Load().FileList is { } whole
        && whole.Diff is not null && whole.CommitUnstaged is not null && whole.CommitStaged is not null);
}

{
    // The commit dialog predates DiffFileGroupMode and carries an enum of its own, whose
    // "no grouping" is a null. The two meet in one pair of switches — and a mode added to
    // the shared enum without a case there would silently save "no grouping" instead.
    Type dialog = typeof(GitExtensions.Avalonia.Views.CommitDialog);
    MethodInfo toSortMode = Method(dialog, "ToSortMode");
    MethodInfo toGroupMode = Method(dialog, "ToGroupMode");

    List<string> lost = [];
    foreach (DiffFileGroupMode mode in Enum.GetValues<DiffFileGroupMode>())
    {
        object? asDialogMode = toSortMode.Invoke(null, [mode]);
        object? back = toGroupMode.Invoke(null, [asDialogMode]);
        if (back is not DiffFileGroupMode returned || returned != mode)
        {
            lost.Add($"{mode} came back as {back}");
        }
    }

    Expect(
        "every grouping survives the trip through the dialog's own enum"
            + (lost.Count == 0 ? string.Empty : " — " + string.Join(", ", lost)),
        lost.Count == 0);

    Expect("and the dialog's \"no grouping\" is the stored None",
        toGroupMode.Invoke(null, [null]) is DiffFileGroupMode.None);
}

// ---------------------------------------------------------------- D: the folder's reach

// The folder menu (right-click on a group header) acts on "this folder", and it has to
// work out which rows those are BACKWARDS from the header — the list cannot tell it,
// because a folded folder shows none of its files and a folded folder is exactly where
// this menu earns its place. So there are two independent computations of one set: the
// one that built the list, and the one the menu reads. If they drift, a gesture aimed at
// a folder acts on a set the user never saw.
//
// The cross-check: every header carries the number of files under it (it is printed on
// screen, "src (3)"), and that number comes from the list builder. The menu's own answer
// must have exactly that many rows, under every grouping, including the tree's nested
// folders whose count is the whole subtree.
{
    Type dialog = typeof(GitExtensions.Avalonia.Views.CommitDialog);
    Type paneType = Nested(dialog, "FileListPane");
    Type headerType = Nested(dialog, "GroupHeader");
    Type sortMode = Nested(dialog, "FileSortMode");
    MethodInfo buildItems = Method(dialog, "BuildItems");
    MethodInfo rowsInGroup = Method(dialog, "RowsInGroup");

    WorkingDirFileRow[] paneRows =
    [
        new("src/views/one.txt", "modified", false),
        new("src/views/two.cs", "new", false),
        new("src/services/three.cs", "modified", false),
        new("docs/readme.txt", "modified", false),
        new("top.txt", "deleted", false),
    ];

    (string Name, object? Group, bool AsTree)[] groupings =
    [
        ("path tree", Enum.Parse(sortMode, "Path"), true),
        ("path flat", Enum.Parse(sortMode, "Path"), false),
        ("extension", Enum.Parse(sortMode, "Extension"), false),
        ("status", Enum.Parse(sortMode, "Status"), false),
    ];

    foreach ((string name, object? group, bool asTree) in groupings)
    {
        object pane = Activator.CreateInstance(paneType, new Avalonia.Controls.ListBox(), false)!;
        paneType.GetField("Group")!.SetValue(pane, group);
        paneType.GetField("AsTree")!.SetValue(pane, asTree);
        paneType.GetField("Rows")!.SetValue(pane, paneRows);

        List<object> items = (List<object>)buildItems.Invoke(null, [pane, paneRows, false])!;
        List<object> headers = [.. items.Where(i => headerType.IsInstanceOfType(i))];
        Expect($"{name}: the list produced headers to test", headers.Count > 0);

        List<string> wrong = [];
        HashSet<string> covered = [];
        foreach (object header in headers)
        {
            string key = (string)headerType.GetProperty("Key")!.GetValue(header)!;
            int said = (int)headerType.GetProperty("Count")!.GetValue(header)!;
            List<WorkingDirFileRow> reached =
                (List<WorkingDirFileRow>)rowsInGroup.Invoke(null, [pane, header])!;

            if (reached.Count != said)
            {
                wrong.Add($"{key}: the header says {said}, the menu reaches {reached.Count}");
            }

            foreach (WorkingDirFileRow row in reached)
            {
                covered.Add(row.Path);
            }
        }

        Expect(
            $"{name}: the menu reaches exactly what each header counts"
                + (wrong.Count == 0 ? string.Empty : " — " + string.Join(" | ", wrong)),
            wrong.Count == 0);
        // In a path TREE a file at the repository root sits under no folder at all, which
        // is not a gap: the other groupings have a bucket for everything, and there the
        // headers must between them reach every row.
        int expected = asTree && group!.ToString() == "Path"
            ? paneRows.Count(r => r.Path.Contains('/', StringComparison.Ordinal))
            : paneRows.Length;
        Expect($"{name}: the headers between them reach every row that has a group",
            covered.Count == expected);
    }

    // And the negative: a header key the grouping never produced reaches nothing, so a
    // stale header (one built before a reload) cannot act on a set by accident.
    {
        object pane = Activator.CreateInstance(paneType, new Avalonia.Controls.ListBox(), false)!;
        paneType.GetField("Group")!.SetValue(pane, Enum.Parse(sortMode, "Path"));
        paneType.GetField("AsTree")!.SetValue(pane, true);
        paneType.GetField("Rows")!.SetValue(pane, paneRows);

        object stale = Activator.CreateInstance(headerType, "nowhere/", "nowhere", 0, 3, false)!;
        List<WorkingDirFileRow> reached =
            (List<WorkingDirFileRow>)rowsInGroup.Invoke(null, [pane, stale])!;
        Expect("a header the grouping never made reaches nothing", reached.Count == 0);
    }
}

// ---------------------------------------------------------------- E: stashing a folder

// Facts about GIT, each measured on a live repository before the folder menu was allowed
// to rely on it (git 2.43). They are not obvious, and two of them decide whether the
// command runs at all rather than how well it runs.
{
    string repos = Path.Combine(sandbox, "stash");
    Directory.CreateDirectory(repos);
    CommitActionsService actions = new();

    // 1. An untracked path without -u fails the WHOLE command and stashes nothing.
    {
        string repo = MakeRepo(Path.Combine(repos, "untracked"));
        File.AppendAllText(Path.Combine(repo, "f", "tracked.txt"), "change\n");
        File.WriteAllText(Path.Combine(repo, "f", "fresh.txt"), "new\n");

        CommitActionResult refused = actions.StashPaths(
            repo, "no -u", ["f/tracked.txt", "f/fresh.txt"], includeUntracked: false);
        Expect("naming an untracked path without -u fails", !refused.Success);
        Expect("and it stashes nothing at all",
            Git(repo, "stash list").Length == 0 && File.Exists(Path.Combine(repo, "f", "fresh.txt")));

        CommitActionResult done = actions.StashPaths(
            repo, "folder", ["f/tracked.txt", "f/fresh.txt"], includeUntracked: true);
        Expect("with -u both the tracked and the untracked path go", done.Success);
        Expect("the untracked file left the working tree",
            !File.Exists(Path.Combine(repo, "f", "fresh.txt")));
        Expect("the tracked change is in the stash",
            Git(repo, "stash show --name-only stash@{0}").Contains("f/tracked.txt", StringComparison.Ordinal));

        // The untracked file rides in the stash's third parent, which is what makes it
        // recoverable rather than deleted.
        Expect("and so is the untracked one, in the third parent",
            Git(repo, "ls-tree -r --name-only stash@{0}^3")
                .Contains("f/fresh.txt", StringComparison.Ordinal));
    }

    // 2. Only the named paths move — the point of the whole gesture.
    {
        string repo = MakeRepo(Path.Combine(repos, "scoped"));
        File.AppendAllText(Path.Combine(repo, "f", "tracked.txt"), "change\n");
        File.AppendAllText(Path.Combine(repo, "outside.txt"), "change\n");

        Expect("a scoped stash succeeds",
            actions.StashPaths(repo, "just f", ["f/tracked.txt"], includeUntracked: false).Success);
        Expect("the folder's file is clean again",
            !Git(repo, "status --porcelain").Contains("f/tracked.txt", StringComparison.Ordinal));
        Expect("and the file outside it was left alone",
            Git(repo, "status --porcelain").Contains("outside.txt", StringComparison.Ordinal));
    }

    // 3. An unresolved merge refuses everything, even for an unrelated pathspec — which
    //    is why the menu keeps the entry out of reach instead of letting it fail on use.
    {
        string repo = MakeRepo(Path.Combine(repos, "conflict"));
        Git(repo, "checkout -q -b other");
        File.WriteAllText(Path.Combine(repo, "f", "tracked.txt"), "theirs\n");
        Git(repo, "commit -qam theirs");
        Git(repo, "checkout -q master");
        File.WriteAllText(Path.Combine(repo, "f", "tracked.txt"), "ours\n");
        Git(repo, "commit -qam ours");
        Git(repo, "merge other");
        File.AppendAllText(Path.Combine(repo, "outside.txt"), "change\n");

        Expect("the merge really is unresolved",
            Git(repo, "ls-files --unmerged").Length > 0);
        CommitActionResult refused =
            actions.StashPaths(repo, "elsewhere", ["outside.txt"], includeUntracked: false);
        Expect("a stash of an UNRELATED path is refused while a merge is unresolved", !refused.Success);
        Expect("and git says why", refused.Output.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    // 4. Nothing to stash is not a crash.
    Expect("an empty path list is reported, not thrown",
        !actions.StashPaths(Path.Combine(repos, "untracked"), "none", [], includeUntracked: false).Success);
}

// ---------------------------------------------------------------- negative cases

// Always on, so a run that has stopped asserting anything cannot pass quietly.
{
    (List<object> items, _) = Build(rows, DiffFileGroupMode.Path, asTree: true, []);
    Expect("a folder that does not exist has no header",
        !items.OfType<FileListGroupNode>().Any(h => h.Key == "Nowhere"));

    (List<object> unfolded, _) = Build(rows, DiffFileGroupMode.Path, asTree: true, ["App/Views/"]);
    Expect("a key with a trailing slash folds nothing (the keys are exact directory paths)",
        unfolded.OfType<FileListFileNode>().Any(n => n.Row.Name == "App/Views/DiffView.cs"));

    Expect("a grouping the enum does not define is not defined",
        !Enum.IsDefined((DiffFileGroupMode)99));
}


// ---------------------------------------------------------------- verdict

try
{
    Directory.Delete(sandbox, recursive: true);
}
catch (IOException)
{
    // The sandbox is evidence on a failure and litter on a success; neither is worth
    // failing a run over.
}

if (failures.Count > 0)
{
    Console.WriteLine();
    foreach (string failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    Console.WriteLine($"\n{failures.Count} of {cases} file-list cases broke");
    return 1;
}

Console.WriteLine(
    $"PASS: {cases} file-list cases — grouping, folding, the dialog's source, the remembered "
    + "choice, the folder menu's reach and what a folder-wide stash does");
return 0;

// ---------------------------------------------------------------- harness

void Expect(string what, bool held)
{
    cases++;
    if (!held)
    {
        failures.Add(what);
    }
}

static DiffFileRow Row(string name) => new(name, null, DiffChangeKind.Modified, IsTracked: true);

static (List<object> Items, int FileCount) Build(
    IReadOnlyList<DiffFileRow> rows,
    DiffFileGroupMode mode,
    bool asTree,
    string[] collapsed,
    Func<DiffFileRow, DiffFileListBuilder.GroupLabel>? grouper = null,
    string? filter = null)
    => DiffFileListBuilder.Build(
        rows,
        DiffFileFilter.Parse(filter),
        mode,
        asTree,
        grouper,
        new HashSet<string>(collapsed, StringComparer.Ordinal),
        static (header, count) => $"{header} ({count})");

static MethodInfo Method(Type type, string name)
    => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"{type.Name}.{name} is gone: the enum bridge this suite checks no longer exists "
            + "under that name, and a renamed private method must not turn into a skipped case.");

// The App directory, so the source lint reads the file that is actually being built. A
// suite that cannot find it FAILS rather than skipping: a lint that silently stops
// looking reads exactly like a lint that found nothing.
static string FindAppDirectory()
{
    for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
    {
        string candidate = Path.Combine(at.FullName, "App", "Views", "CommitDialog.cs");
        if (File.Exists(candidate))
        {
            return Path.Combine(at.FullName, "App");
        }
    }

    return Path.Combine(AppContext.BaseDirectory, "App");
}

static Type Nested(Type type, string name)
    => type.GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            $"{type.Name}.{name} is gone: this suite reaches into the dialog's own types on "
            + "purpose, and a rename must fail loudly instead of skipping the cases.");

// A repository with one folder, one file in it and one outside, all committed.
static string MakeRepo(string path)
{
    Directory.CreateDirectory(Path.Combine(path, "f"));
    Git(path, "init -q -b master .");
    Git(path, "config user.name Harness");
    Git(path, "config user.email harness@example.invalid");
    File.WriteAllText(Path.Combine(path, "f", "tracked.txt"), "base\n");
    File.WriteAllText(Path.Combine(path, "outside.txt"), "base\n");
    Git(path, "add -A");
    Git(path, "commit -qm base");
    return path;
}

static string Git(string repo, string arguments)
{
    ProcessStartInfo info = new("git", arguments)
    {
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    using Process process = Process.Start(info)!;
    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    return output;
}
