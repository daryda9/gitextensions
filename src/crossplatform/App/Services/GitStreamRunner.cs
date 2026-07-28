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
///  Turns the raw byte stream of a PTY into the text of an append-only console,
///  honouring the two control characters git relies on for progress:
///  <list type="bullet">
///   <item><c>\n</c> commits the current line;</item>
///   <item><c>\r</c> rewinds to column 0, so the next write OVERWRITES the line.</item>
///  </list>
///  That is what makes <c>Receiving objects:  37% (…)</c> a single line that keeps
///  updating instead of one line per refresh. ANSI escape sequences (CSI/OSC) are
///  stripped, <c>CSI K</c> / <c>CSI 2K</c> erase the line, backspace and tabs behave.
///  <para>A full terminal grid (<see cref="TerminalEmulator"/>) is deliberately NOT
///  used here: this console is an unbounded scrollback of arbitrarily long lines, not
///  a fixed cols×rows screen, and a grid would wrap/truncate git's output at the
///  emulator width.</para>
///  <para>Thread-safe: <see cref="Feed"/> runs on the PTY reader thread while the UI
///  thread polls <see cref="Snapshot"/>.</para>
/// </summary>
public sealed class PtyTextBuffer
{
    private const int MaxCommitted = 2_000_000;

    private readonly object _sync = new();
    private readonly System.Text.Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _committed = new();
    private readonly StringBuilder _line = new();
    private char[] _chars = new char[8192];
    private int _col;
    private long _version;

    // Escape-sequence state machine: 0 = ground, 1 = saw ESC, 2 = in CSI, 3 = in OSC.
    private int _escState;

    /// <summary>Incremented on every change, so a poller can skip identical frames.</summary>
    public long Version
    {
        get
        {
            lock (_sync)
            {
                return _version;
            }
        }
    }

    /// <summary>Feeds raw terminal bytes. <paramref name="buffer"/> may be reused afterwards.</summary>
    public void Feed(byte[] buffer, int count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_sync)
        {
            int needed = _decoder.GetCharCount(buffer, 0, count, flush: false);
            if (_chars.Length < needed)
            {
                _chars = new char[Math.Max(needed, _chars.Length * 2)];
            }

            int produced = _decoder.GetChars(buffer, 0, count, _chars, 0, flush: false);
            for (int i = 0; i < produced; i++)
            {
                Consume(_chars[i]);
            }

            _version++;
        }
    }

    /// <summary>Appends a line produced outside the PTY (command echo, notes).</summary>
    public void AppendLine(string text)
    {
        lock (_sync)
        {
            if (_line.Length > 0)
            {
                CommitLine();
            }

            _committed.Append(text).Append('\n');
            Trim();
            _version++;
        }
    }

    /// <summary>
    ///  The console text, the live (uncommitted) last line and the percentage found on
    ///  it, all consistent with each other.
    /// </summary>
    public (string Text, string CurrentLine, int? Percent, long Version) Snapshot()
    {
        lock (_sync)
        {
            string current = _line.ToString();
            string text = _committed.Length == 0
                ? current
                : (current.Length == 0 ? _committed.ToString() : _committed + current);
            return (text, current, ParsePercent(current), _version);
        }
    }

    private void Consume(char c)
    {
        switch (_escState)
        {
            case 1:
                _escState = c switch
                {
                    '[' => 2,
                    ']' => 3,
                    // Two-character sequences (ESC =, ESC >, ESC M, …) end here; a
                    // charset selector (ESC ( B) leaves one stray byte, harmless.
                    _ => 0,
                };
                return;

            case 2:
                // CSI ends at the first final byte in 0x40..0x7E.
                if (c is >= (char)0x40 and <= (char)0x7E)
                {
                    if (c == 'K')
                    {
                        // Erase in line: git uses it to clear leftovers of a longer
                        // previous progress line.
                        _line.Length = Math.Min(_line.Length, _col);
                    }

                    _escState = 0;
                }

                return;

            case 3:
                // OSC ends at BEL or at ST (ESC \); treating ESC as a terminator is
                // enough for the title strings git/ssh emit.
                if (c is '\a' or (char)0x1b)
                {
                    _escState = 0;
                }

                return;
        }

        switch (c)
        {
            case (char)0x1b:
                _escState = 1;
                return;

            case '\n':
                CommitLine();
                return;

            case '\r':
                _col = 0;
                return;

            case '\b':
                _col = Math.Max(0, _col - 1);
                return;

            case '\t':
                int target = ((_col / 8) + 1) * 8;
                while (_col < target)
                {
                    Put(' ');
                }

                return;

            case '\a':
                return;

            default:
                if (c >= ' ')
                {
                    Put(c);
                }

                return;
        }
    }

    private void Put(char c)
    {
        while (_line.Length < _col)
        {
            _line.Append(' ');
        }

        if (_col < _line.Length)
        {
            _line[_col] = c;
        }
        else
        {
            _line.Append(c);
        }

        _col++;
    }

    private void CommitLine()
    {
        _committed.Append(_line).Append('\n');
        _line.Clear();
        _col = 0;
        Trim();
    }

    private void Trim()
    {
        if (_committed.Length > MaxCommitted)
        {
            _committed.Remove(0, _committed.Length - (MaxCommitted / 2));
        }
    }

    /// <summary>
    ///  The last <c>NN%</c> on the line, which is where git puts the share of the
    ///  current phase ("Receiving objects:  37% (…)"). <see langword="null"/> when the
    ///  line carries no percentage — the dialog then shows an indeterminate bar rather
    ///  than inventing a value.
    /// </summary>
    internal static int? ParsePercent(string line)
    {
        for (int i = line.Length - 1; i >= 0; i--)
        {
            if (line[i] != '%')
            {
                continue;
            }

            int end = i;
            int start = i;
            while (start > 0 && char.IsAsciiDigit(line[start - 1]))
            {
                start--;
            }

            if (start == end)
            {
                continue;
            }

            if (int.TryParse(line.AsSpan(start, end - start), out int value) && value is >= 0 and <= 100)
            {
                return value;
            }
        }

        return null;
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
