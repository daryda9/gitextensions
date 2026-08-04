using System.Diagnostics;
using GitExtensions.Avalonia.Services;

string sandbox = Path.Combine(Path.GetTempPath(), "ge-submodule-hierarchy-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);

try
{
    string leaf = Repo("leaf-source");
    string level3 = Repo("level3-source");
    Add(level3, leaf, "leaf", "leaf-path");
    string level2 = Repo("level2-source");
    Add(level2, level3, "level3", "nested/level-three");
    string level1 = Repo("level1-source");
    Add(level1, level2, "level2", "level-two");
    string sibling = Repo("sibling-source");
    string missingSource = Repo("missing-source");
    string root = Repo("root");
    Add(root, level1, "display-name-differs", "modules/level-one");
    Add(root, sibling, "sibling", "sibling");
    Add(root, missingSource, "missing-name", "missing-path");
    Git(root, "submodule", "deinit", "-f", "--", "missing-path");
    Directory.Delete(Path.Combine(root, "missing-path"), recursive: true);

    string current = Path.Combine(root, "modules", "level-one", "level-two", "nested", "level-three", "leaf-path");
    SubmoduleHierarchy hierarchy = new SubmoduleService().DiscoverHierarchy(current + Path.DirectorySeparatorChar);

    Equal(Normal(root), hierarchy.RootPath, "root discovery");
    Equal(Normal(current), hierarchy.CurrentPath, "current normalization");
    Equal(Normal(Path.GetDirectoryName(current)!), hierarchy.ImmediateSuperprojectPath!, "immediate parent");
    Check(hierarchy.Nodes.Count(n => n.IsCurrent) == 1, "exactly one current node");
    Check(hierarchy.Nodes.Any(n => n.Path == "sibling"), "sibling retained");
    SubmoduleRow missing = hierarchy.Nodes.Single(n => n.Path == "missing-path");
    Check(!missing.Exists && missing.Status == SubmoduleState.NotInitialized, "missing node is graceful");
    SubmoduleRow named = hierarchy.Nodes.Single(n => n.Path == "modules/level-one");
    EqualText("level-one", named.Name, "display name follows configured path basename");
    EqualText("display-name-differs", named.ConfiguredName, "configured name retained as metadata");
    Equal(Normal(root), named.ParentRepositoryPath, "declaring repository");
    SubmoduleRow deepest = hierarchy.Nodes.Single(n => n.IsCurrent);
    Equal("leaf-path", deepest.PathInParent, "configured path in parent");
    Check(deepest.Path.Split('/').Length >= 6, "four-level nesting retained");
    Check(hierarchy.Nodes.Select(n => n.AbsolutePath).Distinct(GetPathComparer()).Count() == hierarchy.Nodes.Count, "no duplicate/loop nodes");
    Check(hierarchy.Nodes.Skip(1).Select(n => n.Path).SequenceEqual(hierarchy.Nodes.Skip(1).Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal)), "stable ordinal order");

    File.AppendAllText(Path.Combine(root, ".gitmodules"), "\n[submodule \"broken\"]\n\tpath = broken\n");
    SubmoduleHierarchy incomplete = new SubmoduleService().DiscoverHierarchy(root);
    Equal(Normal(root), incomplete.RootPath, "incomplete config does not throw");

    File.WriteAllText(Path.Combine(current, ".gitmodules"), "[submodule \"self-alias\"]\n\tpath = self\n\turl = .\n");
    Directory.CreateDirectory(Path.Combine(current, "self"));
    string currentGitFile = File.ReadAllText(Path.Combine(current, ".git")).Trim();
    string currentGitDir = Normal(Path.Combine(current, currentGitFile["gitdir:".Length..].Trim()));
    File.WriteAllText(Path.Combine(current, "self", ".git"), $"gitdir: {currentGitDir}\n");
    Task<SubmoduleHierarchy> cyclicDiscovery = Task.Run(() => new SubmoduleService().DiscoverHierarchy(current));
#pragma warning disable VSTHRD002 // Deliberate bounded wait: this assertion proves malformed recursion terminates.
    Check(cyclicDiscovery.Wait(TimeSpan.FromSeconds(10)), "cyclic alias discovery terminates");
    Check(cyclicDiscovery.Result.Nodes.Count < 130, "cycle/depth guard bounds traversal");
    Check(cyclicDiscovery.Result.Nodes.Count(n => n.Path.EndsWith("/self", StringComparison.Ordinal)) == 1, "cyclic alias fixture is enumerated once");
#pragma warning restore VSTHRD002

    string linked = Path.Combine(sandbox, "linked-worktree");
    Git(sibling, "worktree", "add", "-q", "-b", "linked-test", linked);
    SubmoduleHierarchy worktree = new SubmoduleService().DiscoverHierarchy(linked);
    Equal(Normal(linked), worktree.RootPath, "linked worktree gitdir discovery");

    Console.WriteLine($"PASS: {hierarchy.Nodes.Count} hierarchy nodes; root={hierarchy.RootPath}");
    return 0;
}
finally
{
    try { Directory.Delete(sandbox, recursive: true); } catch { }
}

string Repo(string name)
{
    string path = Path.Combine(sandbox, name);
    Directory.CreateDirectory(path);
    Git(path, "init", "-q");
    Git(path, "config", "user.name", "Harness");
    Git(path, "config", "user.email", "harness@example.invalid");
    File.WriteAllText(Path.Combine(path, "README.md"), name);
    Git(path, "add", "README.md");
    Git(path, "commit", "-q", "-m", "initial");
    return path;
}

void Add(string parent, string source, string name, string path)
{
    Git(parent, "-c", "protocol.file.allow=always", "submodule", "add", "-q", "--name", name, source, path);
    Git(parent, "commit", "-q", "-am", "add submodule");
    Git(parent, "-c", "protocol.file.allow=always", "submodule", "update", "-q", "--init", "--recursive");
}

void Git(string cwd, params string[] args)
{
    ProcessStartInfo start = new(OperatingSystem.IsWindows() ? "git.exe" : "git")
    {
        WorkingDirectory = cwd,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (string arg in args) start.ArgumentList.Add(arg);
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("git.exe did not start");
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stdout}{stderr}");
}

static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
static StringComparer GetPathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("FAIL: " + message); }
static void Equal(string expected, string actual, string message) => Check(GetPathComparer().Equals(expected, actual), $"{message}: expected '{expected}', actual '{actual}'");
static void EqualText(string expected, string actual, string message) => Check(string.Equals(expected, actual, StringComparison.Ordinal), $"{message}: expected '{expected}', actual '{actual}'");
