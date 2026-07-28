using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Outcome of a clone / init operation: whether git succeeded, its combined
///  output (for display in the status bar / error panel), and the resulting
///  repository working-directory path when known.
/// </summary>
public sealed record CloneInitResult(bool Success, string Output, string? RepoPath);

/// <summary>
///  Creates repositories from the shell: <see cref="Clone"/> runs
///  <c>git clone &lt;url&gt; &lt;name&gt;</c> inside a chosen parent directory, and
///  <see cref="Init"/> runs <c>git init</c> in a chosen directory.
///
///  Both reuse the Git Extensions core git executable via
///  <see cref="GitContext.CreateModule"/>. A top-level clone/init does not need an
///  existing repository — the module's <see cref="IExecutable"/> simply runs git
///  with its working directory set to the given folder, so we bind a module to the
///  parent (clone) or target (init) directory and drive its executable. Nothing here
///  touches the UI; call these off the UI thread.
/// </summary>
public sealed class CloneInitService
{
    /// <summary>
    ///  Clones <paramref name="url"/> into a subdirectory of
    ///  <paramref name="parentDir"/>. The subdirectory name is derived from the URL
    ///  (its last path segment without a trailing <c>.git</c>) and passed to git
    ///  explicitly, so the resulting repository path is known up-front and returned
    ///  in <see cref="CloneInitResult.RepoPath"/> on success.
    /// </summary>
    public CloneInitResult Clone(string url, string parentDir)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new CloneInitResult(false, "No repository URL was given.", null);
        }

        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
        {
            return new CloneInitResult(false, $"Target directory does not exist: {parentDir}", null);
        }

        string name = RepositoryNameFromUrl(url);
        string repoPath = Path.Combine(parentDir, name);

        // Bind a module to the parent directory (need not be a repo) purely to
        // borrow the core git executable running in that directory.
        GitModule module = GitContext.CreateModule(parentDir);
        ArgumentString args = $"clone \"{url.Trim()}\" \"{name}\"";
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);

        bool ok = result.ExitedSuccessfully && GitModule.IsValidGitWorkingDir(repoPath);
        return new CloneInitResult(ok, result.AllOutput, ok ? repoPath : null);
    }

    /// <summary>
    ///  Builds the <c>git clone</c> argument string for the full option set of
    ///  upstream's <c>FormClone</c> (<c>Commands.Clone</c>): bare, submodule
    ///  initialisation, shallow depth, and the branch to check out.
    ///  <para>
    ///  Returned as a string rather than executed so the caller can stream it
    ///  through <see cref="GitStreamRunner"/>: a clone can take minutes and git
    ///  writes its transfer progress to stderr, which the core executable buffers.
    ///  <c>--progress</c> is always passed, because git only prints progress when it
    ///  believes it is talking to a terminal.
    ///  </para>
    /// </summary>
    /// <param name="url">Repository to clone from.</param>
    /// <param name="targetDir">Full path of the directory to create.</param>
    /// <param name="central">Clone as a bare repository (<c>--bare</c>).</param>
    /// <param name="initSubmodules">Clone submodules too (<c>--recurse-submodules</c>).</param>
    /// <param name="branch">
    ///  Branch to check out; empty means the remote's default HEAD, and
    ///  <see langword="null"/> means "do not check anything out" (<c>--no-checkout</c>),
    ///  mirroring the two synthetic entries of upstream's branch combo.
    /// </param>
    /// <param name="depth">
    ///  Shallow-clone depth, or null for the full history. A depth is paired with
    ///  <c>--no-single-branch</c>: git implies <c>--single-branch</c> when a depth is
    ///  given, and a single-branch clone is painful to widen afterwards, so upstream
    ///  deliberately turns it back off.
    /// </param>
    public static string CloneArguments(
        string url,
        string targetDir,
        bool central = false,
        bool initSubmodules = true,
        string? branch = "",
        int? depth = null)
    {
        GitArgumentBuilder args = new("clone")
        {
            "-v",
            { central, "--bare" },
            { initSubmodules, "--recurse-submodules" },
            { depth is not null, $"--depth {depth}" },
            { depth is not null, "--no-single-branch" },
            "--progress",
            { branch is null, "--no-checkout" },
            { !string.IsNullOrEmpty(branch), $"--branch {branch}" },
            url.Trim().ToPosixPath().Quote(),
            targetDir.Trim().ToPosixPath().Quote(),
        };

        return args.ToString();
    }

    /// <summary>
    ///  Asks the remote which branches it has (<c>git ls-remote --heads</c>), for the
    ///  branch drop-down of the clone dialog. Returns an empty list when the remote
    ///  cannot be reached or refuses — the dialog degrades to "clone the default
    ///  branch" rather than failing. Blocking network call: never on the UI thread.
    /// </summary>
    public static IReadOnlyList<string> ListRemoteBranches(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return [];
        }

        try
        {
            // Any directory works: ls-remote talks to the remote, not to a local repo.
            GitModule module = GitContext.CreateModule(Path.GetTempPath());
            IReadOnlyList<IGitRef> refs = module.GetRemoteServerRefs(
                url.Trim(),
                tags: false,
                branches: true,
                out string? errorOutput,
                cancellationToken);

            return string.IsNullOrEmpty(errorOutput)
                ? refs.Select(r => r.LocalName).Where(n => !string.IsNullOrEmpty(n)).ToList()!
                : [];
        }
        catch (Exception)
        {
            // Unreachable host, bad URL, cancelled: the drop-down just stays empty.
            return [];
        }
    }

    /// <summary>
    ///  Initialises a new git repository in <paramref name="dir"/> (created if it
    ///  does not yet exist), returning the directory as the repository path on
    ///  success.
    ///  <para>
    ///  <paramref name="central"/> selects upstream's "Central repository" type
    ///  (<c>FormInit</c>'s <c>Central</c> radio): <c>git init --bare --shared=all</c>,
    ///  a repository with no working directory, group-writable, meant to be pushed
    ///  to. The default — "Personal" — is a plain <c>git init</c>.
    ///  </para>
    ///  <para>
    ///  A central repository has no work tree, so <c>IsValidGitWorkingDir</c> is the
    ///  wrong success test for it; success is then git's own exit code plus the
    ///  presence of the bare layout.
    ///  </para>
    /// </summary>
    public CloneInitResult Init(string dir, bool central = false)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return new CloneInitResult(false, "No directory was given.", null);
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return new CloneInitResult(false, $"Could not create {dir}: {ex.Message}", null);
        }

        GitModule module = GitContext.CreateModule(dir);
        GitArgumentBuilder args = new("init")
        {
            { central, "--bare" },
            { central, "--shared=all" },
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);

        bool created = central
            ? Directory.Exists(Path.Combine(dir, "refs")) && File.Exists(Path.Combine(dir, "HEAD"))
            : GitModule.IsValidGitWorkingDir(dir);
        bool ok = result.ExitedSuccessfully && created;
        return new CloneInitResult(ok, result.AllOutput, ok ? dir : null);
    }

    // Derives the working-directory name git would use for a clone: the last path
    // segment of the URL with any trailing ".git" (and trailing slashes) removed.
    // Falls back to "repository" if nothing usable can be extracted.
    private static string RepositoryNameFromUrl(string url)
    {
        string trimmed = url.Trim().TrimEnd('/', '\\');

        // Handle both scp-like (git@host:path) and normal URL / path separators.
        int slash = trimmed.LastIndexOfAny(['/', '\\', ':']);
        string segment = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;

        if (segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            segment = segment[..^4];
        }

        return string.IsNullOrWhiteSpace(segment) ? "repository" : segment;
    }
}
