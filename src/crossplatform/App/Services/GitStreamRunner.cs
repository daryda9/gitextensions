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

    // The interactive-terminal host bound to the current logical call-flow. When set,
    // Run() executes git on a PTY instead of on redirected pipes, so git behaves the
    // way it does in a terminal: it prints transfer progress by itself with \r
    // updates, and it may ASK (key passphrase, host-key yes/no, HTTPS credentials).
    private static readonly AsyncLocal<IGitPtyHost?> _currentPtyHost = new();

    /// <summary>
    ///  Binds <paramref name="host"/> to the current logical call-flow: every
    ///  <see cref="Run"/> executed from here on runs git on a pseudo-terminal and
    ///  streams RAW terminal bytes to the host, which also gets the
    ///  <see cref="PtyProcess"/> so it can answer prompts. Call it INSIDE the
    ///  background task that runs the operation.
    ///  <para>Passing <see langword="null"/> restores the piped, strictly
    ///  non-interactive path.</para>
    /// </summary>
    public static void EnterPtyHost(IGitPtyHost? host) => _currentPtyHost.Value = host;

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

        IGitPtyHost? ptyHost = _currentPtyHost.Value;
        if (ptyHost is not null)
        {
            int? ptyExit = RunOnPty(repoPath, arguments, onLine, env, ptyHost);
            if (ptyExit is int code)
            {
                return code;
            }

            // The PTY could not be created (no /dev/ptmx, exhausted pty slots): fall
            // through to the piped path rather than failing the operation. Prompts
            // are then unanswerable again, exactly as before this feature existed.
            onLine("<no pseudo-terminal available; falling back to non-interactive git>");
        }

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

    /// <summary>
    ///  Runs git on a pseudo-terminal, streaming raw bytes to <paramref name="host"/>.
    ///  Returns the exit code, or <see langword="null"/> when no PTY could be created
    ///  (the caller then falls back to the piped path).
    /// </summary>
    private static int? RunOnPty(
        string repoPath,
        string arguments,
        Action<string> onLine,
        IReadOnlyDictionary<string, string?>? env,
        IGitPtyHost host)
    {
        Dictionary<string, string?> ptyEnv = new(StringComparer.Ordinal)
        {
            // THE deliberate difference from the piped path: on a PTY a prompt is
            // visible and answerable, so git is allowed to ask. On pipes it must stay
            // disabled, or the app would block on an invisible question.
            ["GIT_TERMINAL_PROMPT"] = "1",
            // Force the terminal, not a graphical askpass helper: ssh prefers
            // SSH_ASKPASS whenever DISPLAY is set, which would move the passphrase
            // prompt into a window we do not control (or fail if it is missing).
            ["SSH_ASKPASS_REQUIRE"] = "never",
            ["GIT_ASKPASS"] = null,
            ["SSH_ASKPASS"] = null,
            ["GCM_INTERACTIVE"] = "auto",
            ["TERM"] = "xterm-256color",
            // Progress is what we are here for; make sure a pager never swallows the
            // output of a command that happens to produce a lot of it.
            ["GIT_PAGER"] = "cat",
        };

        if (env is not null)
        {
            foreach (KeyValuePair<string, string?> entry in env)
            {
                ptyEnv[entry.Key] = entry.Value;
            }
        }

        PtyProcess pty = new();
        GitProcessScope? scope = _currentScope.Value;
        try
        {
            pty.Output += (buffer, count) => host.Output(buffer, count);

            // 200 columns: git sizes its progress line to the terminal width, and a
            // narrow terminal truncates "Receiving objects: ..." mid-way.
            pty.StartCommand(repoPath, "git", QuoteForShell(arguments), ptyEnv, cols: 200, rows: 50);
        }
        catch (Exception ex)
        {
            onLine("<pty: " + ex.Message + ">");
            try
            {
                pty.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }

            return null;
        }

        scope?.RegisterPty(pty, repoPath);
        try
        {
            host.Started(pty);
            pty.WaitForExit(Timeout.Infinite);
            int code = pty.ExitCode ?? -1;
            host.Ended(code);
            return code;
        }
        finally
        {
            scope?.UnregisterPty(pty);
            try
            {
                pty.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }

    /// <summary>
    ///  Re-quotes a <see cref="ProcessStartInfo.Arguments"/>-style string so it can be
    ///  handed to <c>sh -c</c> without the shell re-interpreting <c>$</c>, backticks,
    ///  globs or spaces inside a ref name. The string is first split with (an
    ///  approximation of) the rules .NET itself uses on Unix — whitespace separates,
    ///  double quotes group, a backslash escapes a following quote — then every token
    ///  is wrapped in single quotes.
    /// </summary>
    internal static string QuoteForShell(string arguments)
        => string.Join(' ', SplitArguments(arguments).Select(a => "'" + a.Replace("'", "'\\''") + "'"));

    internal static List<string> SplitArguments(string arguments)
    {
        List<string> result = [];
        StringBuilder token = new();
        bool inQuotes = false;
        bool hasToken = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            char c = arguments[i];

            if (c == '\\' && i + 1 < arguments.Length && arguments[i + 1] == '"')
            {
                token.Append('"');
                hasToken = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t' || c == '\n' || c == '\r'))
            {
                if (hasToken)
                {
                    result.Add(token.ToString());
                    token.Clear();
                    hasToken = false;
                }

                continue;
            }

            token.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            result.Add(token.ToString());
        }

        return result;
    }
}

/// <summary>
///  Consumer of a git command running on a pseudo-terminal: it receives the raw
///  terminal byte stream (which contains <c>\r</c> progress updates and ANSI escapes)
///  and the live <see cref="PtyProcess"/>, so it can write answers to prompts.
///  <para>All members are called from background threads — the PTY reader thread for
///  <see cref="Output"/>, the operation's own thread for the others.</para>
/// </summary>
public interface IGitPtyHost
{
    /// <summary>The git command started on <paramref name="pty"/>; write answers to it.</summary>
    void Started(PtyProcess pty);

    /// <summary>A chunk of raw terminal output. The buffer is REUSED — copy what you keep.</summary>
    void Output(byte[] buffer, int count);

    /// <summary>The git command exited with <paramref name="exitCode"/>.</summary>
    void Ended(int exitCode);
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
    private readonly List<PtyProcess> _livePty = [];
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
                return _live.Count > 0 || _livePty.Count > 0;
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

    internal void RegisterPty(PtyProcess pty, string repoPath)
    {
        bool killNow;
        lock (_sync)
        {
            _repoPath = repoPath;
            _livePty.Add(pty);
            killNow = _abortRequested;
        }

        if (killNow)
        {
            KillPty(pty);
        }
    }

    internal void UnregisterPty(PtyProcess pty)
    {
        lock (_sync)
        {
            _livePty.Remove(pty);
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
        PtyProcess[] ptySnapshot;
        lock (_sync)
        {
            _abortRequested = true;
            snapshot = _live.ToArray();
            ptySnapshot = _livePty.ToArray();
        }

        bool killed = false;
        foreach (Process process in snapshot)
        {
            killed |= Kill(process);
        }

        foreach (PtyProcess pty in ptySnapshot)
        {
            killed |= KillPty(pty);
        }

        return killed;
    }

    /// <summary>
    ///  Aborts a git command running on a PTY the way a terminal user would, escalating
    ///  only as needed: SIGINT to the terminal's FOREGROUND PROCESS GROUP first (that
    ///  hits git and the <c>ssh</c>/<c>git-remote-https</c> helper sharing the group,
    ///  and lets git clean up its own temporary files), then SIGTERM, then the hard
    ///  path — <see cref="PtyProcess.Dispose"/> closes the master, which SIGHUPs the
    ///  session and kills the child tree.
    /// </summary>
    private static bool KillPty(PtyProcess pty)
    {
        try
        {
            if (!pty.IsRunning)
            {
                return false;
            }

            pty.Interrupt();
            if (pty.WaitForExit(700))
            {
                return true;
            }

            pty.Terminate();
            if (!pty.WaitForExit(700))
            {
                pty.Dispose();
            }

            return true;
        }
        catch (Exception)
        {
            // An abort must never throw at the UI.
            return false;
        }
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
