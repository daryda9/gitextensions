using System.Diagnostics;
using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Runs a single <c>git</c> command via a direct <see cref="Process"/> with BOTH
///  stdout and stderr redirected and read asynchronously, invoking
///  <paramref name="onLine"/> for every line as git emits it. This is what makes
///  fetch/push/pull output render <em>truly live</em>: git writes transfer
///  PROGRESS to stderr, which the core <c>Executable</c>/<c>IProcess</c> buffers
///  (no incremental events). Reading the redirected streams line-by-line surfaces
///  that progress incrementally instead of only at end-of-command.
///
///  The runner is intentionally thread-agnostic: <c>OutputDataReceived</c> /
///  <c>ErrorDataReceived</c> fire on threadpool threads, so <c>onLine</c> may be
///  called from a background thread. Marshalling to the UI thread is the caller's
///  responsibility (the process dialog posts each line to the UI thread).
/// </summary>
public static class GitStreamRunner
{
    // The scope the current logical call-flow belongs to. AsyncLocal makes it flow
    // into everything the process dialog's background operation calls (including
    // nested Task.Run), so the runner can register the git process it starts
    // without every caller having to thread a handle through.
    private static readonly AsyncLocal<GitProcessScope?> _currentScope = new();

    /// <summary>
    ///  Binds <paramref name="scope"/> to the current logical call-flow: every
    ///  <see cref="Run"/> executed from here on (on this flow) registers its git
    ///  process with it, so it can be killed. Call it INSIDE the background task
    ///  that runs the operation — the value does not escape that task.
    /// </summary>
    public static void EnterScope(GitProcessScope scope) => _currentScope.Value = scope;

    /// <summary>
    ///  Runs <c>git <paramref name="arguments"/></c> in <paramref name="repoPath"/>,
    ///  emitting each stdout/stderr line through <paramref name="onLine"/> as it is
    ///  produced. Returns the process exit code (non-zero on failure/exception).
    /// </summary>
    /// <param name="repoPath">Working directory for the git process.</param>
    /// <param name="arguments">The git argument string (without the leading "git").</param>
    /// <param name="onLine">Called once per output line; also used to echo the command header.</param>
    /// <param name="env">Optional extra environment variables applied to the child process.</param>
    public static int Run(string repoPath, string arguments, Action<string> onLine, IReadOnlyDictionary<string, string?>? env = null)
    {
        // Echo the command being run: git launched this way does NOT flow through
        // the core CommandLog, so the console would otherwise show no command line.
        onLine("Command to be executed:");
        onLine($"git {arguments}");
        onLine(string.Empty);

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // Make git strictly non-interactive: it must NEVER prompt on the
            // controlling terminal where the app was launched. Setting these
            // BEFORE applying the caller-supplied env means a transient
            // credential helper (env) can still override GCM behaviour.
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GCM_INTERACTIVE"] = "never";
            // Neutralize any inherited askpass helper (desktop sessions often set
            // SSH_ASKPASS=ssh-askpass, which may not exist → "cannot run ssh-askpass").
            // Empty values make git skip askpass entirely and fail fast with a clean
            // auth error that the UI detects to prompt for credentials in-app instead.
            psi.Environment["GIT_ASKPASS"] = "";
            psi.Environment["SSH_ASKPASS"] = "";
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "never";

            if (env is not null)
            {
                foreach (KeyValuePair<string, string?> entry in env)
                {
                    psi.Environment[entry.Key] = entry.Value;
                }
            }

            using Process proc = new() { StartInfo = psi };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onLine(e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onLine(e.Data);
                }
            };

            proc.Start();

            // Close stdin immediately so git sees EOF and never blocks waiting
            // for input on the terminal — it fails fast on any auth prompt.
            try
            {
                proc.StandardInput.Close();
            }
            catch (Exception)
            {
                // Ignore: nothing to do if stdin is already gone.
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Publish the live process so an Abort can actually kill it (and knows
            // which repository's index.lock to clear afterwards). If an abort was
            // already requested before we got here, kill it right away instead of
            // letting a doomed command run to completion.
            GitProcessScope? scope = _currentScope.Value;
            scope?.Register(proc, repoPath);
            try
            {
                proc.WaitForExit();
                return proc.ExitCode;
            }
            finally
            {
                scope?.Unregister(proc);
            }
        }
        catch (Exception ex)
        {
            onLine("<error: " + ex.Message + ">");
            return -1;
        }
    }
}

/// <summary>
///  Tracks the git processes started by <see cref="GitStreamRunner.Run"/> inside one
///  logical operation so a UI "Abort" can really terminate them (mirroring
///  <c>FormStatus.KillCommandProcess</c>), instead of only closing the window and
///  leaving git — and its <c>index.lock</c> — behind.
/// </summary>
public sealed class GitProcessScope
{
    private readonly object _sync = new();
    private readonly List<Process> _live = [];
    private string? _repoPath;
    private bool _abortRequested;

    /// <summary>Whether <see cref="KillAll"/> has been called on this scope.</summary>
    public bool AbortRequested
    {
        get
        {
            lock (_sync)
            {
                return _abortRequested;
            }
        }
    }

    /// <summary>
    ///  Working directory of the last git process started in this scope — the
    ///  repository whose index must be unlocked after an abort. <see langword="null"/>
    ///  when no process was ever started.
    /// </summary>
    public string? RepoPath
    {
        get
        {
            lock (_sync)
            {
                return _repoPath;
            }
        }
    }

    /// <summary>Whether at least one git process is currently running in this scope.</summary>
    public bool HasLiveProcess
    {
        get
        {
            lock (_sync)
            {
                return _live.Count > 0;
            }
        }
    }

    internal void Register(Process process, string repoPath)
    {
        bool killNow;
        lock (_sync)
        {
            _repoPath = repoPath;
            _live.Add(process);
            killNow = _abortRequested;
        }

        if (killNow)
        {
            Kill(process);
        }
    }

    internal void Unregister(Process process)
    {
        lock (_sync)
        {
            _live.Remove(process);
        }
    }

    /// <summary>
    ///  Kills every git process running in this scope, whole process tree included
    ///  (git delegates to helpers such as <c>git-remote-https</c> / <c>ssh</c>, which
    ///  would otherwise survive the parent). Later <see cref="Register"/> calls on
    ///  this scope are killed on arrival too, so an abort cannot be outrun by the
    ///  next command of a multi-step operation.
    /// </summary>
    /// <returns>
    ///  <see langword="true"/> when at least one live process was signalled — i.e.
    ///  the abort had something to terminate.
    /// </returns>
    public bool KillAll()
    {
        Process[] snapshot;
        lock (_sync)
        {
            _abortRequested = true;
            snapshot = _live.ToArray();
        }

        bool killed = false;
        foreach (Process process in snapshot)
        {
            killed |= Kill(process);
        }

        return killed;
    }

    private static bool Kill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception)
        {
            // Race with normal exit, or no permission to signal the tree: nothing
            // more we can do, and an abort must never throw at the UI.
            return false;
        }
    }
}
