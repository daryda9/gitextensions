using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A real pseudo-terminal (PTY) hosting the user's login shell.
///  <para>
///  The master side is obtained straight from libc
///  (<c>posix_openpt</c> / <c>grantpt</c> / <c>unlockpt</c> / <c>ptsname_r</c>) so no
///  native helper library or NuGet package is required. The child is *not* started
///  with <c>fork()</c> — calling fork from a multi-threaded .NET runtime is unsafe —
///  but with <see cref="Process"/> running
///  <c>setsid -w sh -c 'exec 0&lt;pts 1&gt;pts 2&gt;&amp;1; exec $SHELL -i'</c>.
///  <c>setsid</c> makes the shell a session leader and, because the slave device is the
///  first terminal it opens (without <c>O_NOCTTY</c>), the pts becomes its *controlling*
///  terminal. That is what gives us a working line discipline: job control, Ctrl+C →
///  SIGINT, window size, <c>isatty()</c> = true (so <c>ls</c>/<c>git</c> colourise).
///  </para>
///  <para>All I/O happens on a dedicated background thread; consumers receive bytes
///  through <see cref="Output"/> and must marshal to the UI thread themselves.</para>
/// </summary>
public sealed class PtyProcess : IDisposable
{
    private const int O_RDWR = 0x0002;
    private const int O_NOCTTY = 0x0100;
    private const int O_CLOEXEC = 0x80000;
    private const ulong TIOCSWINSZ = 0x5414;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int fd, byte[] buf, int buflen);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_winsize(int fd, ulong request, ref WinSize size);

    [DllImport("libc", SetLastError = true, EntryPoint = "signal")]
    private static extern nint sys_signal(int signum, nint handler);

    private const int SIGINT = 2;
    private const int SIGQUIT = 3;
    private const int SIGPIPE = 13;
    private static readonly nint SIG_DFL = 0;

    private static readonly List<WeakReference<PtyProcess>> s_live = [];

    static PtyProcess()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
    }

    private readonly object _writeLock = new();
    private int _master = -1;
    private Process? _child;
    private Thread? _reader;
    private volatile bool _disposed;

    /// <summary>Raised on a background thread with a chunk of raw shell output.</summary>
    public event Action<byte[], int>? Output;

    /// <summary>Raised on a background thread once the shell has terminated.</summary>
    public event Action? Exited;

    /// <summary>The path of the slave device, for diagnostics.</summary>
    public string SlavePath { get; private set; } = string.Empty;

    /// <summary>True while the shell is running.</summary>
    public bool IsRunning => !_disposed && _master >= 0;

    /// <summary>
    ///  Opens a PTY and starts the user's shell in <paramref name="workingDirectory"/>.
    ///  Throws <see cref="InvalidOperationException"/> when the PTY cannot be created.
    /// </summary>
    public void Start(string workingDirectory, int cols, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (_master < 0)
        {
            throw new InvalidOperationException($"posix_openpt failed (errno {Marshal.GetLastWin32Error()})");
        }

        if (grantpt(_master) != 0 || unlockpt(_master) != 0)
        {
            int err = Marshal.GetLastWin32Error();
            close(_master);
            _master = -1;
            throw new InvalidOperationException($"grantpt/unlockpt failed (errno {err})");
        }

        byte[] nameBuf = new byte[256];
        if (ptsname_r(_master, nameBuf, nameBuf.Length) != 0)
        {
            int err = Marshal.GetLastWin32Error();
            close(_master);
            _master = -1;
            throw new InvalidOperationException($"ptsname_r failed (errno {err})");
        }

        int len = Array.IndexOf(nameBuf, (byte)0);
        SlavePath = Encoding.UTF8.GetString(nameBuf, 0, len < 0 ? nameBuf.Length : len);

        Resize(cols, rows);

        string shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s && File.Exists(s)
            ? s
            : "/bin/bash";

        // The child opens the slave itself, after setsid(), so the pts becomes its
        // controlling terminal. Quoting: SlavePath is always "/dev/pts/<n>".
        string script = $"exec 0<{SlavePath} 1>{SlavePath} 2>&1; exec {Quote(shell)} -i";
        bool haveSetsid = File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid");

        ProcessStartInfo psi = new()
        {
            FileName = haveSetsid ? "setsid" : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.CurrentDirectory,
        };

        if (haveSetsid)
        {
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("/bin/sh");
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);

        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["COLORTERM"] = "truecolor";
        psi.Environment["COLUMNS"] = cols.ToString();
        psi.Environment["LINES"] = rows.ToString();
        // Git Extensions sets GIT_* helpers for its own child processes; a user shell
        // must not inherit a non-interactive askpass or pager configuration.
        psi.Environment.Remove("GIT_ASKPASS");
        psi.Environment.Remove("SSH_ASKPASS");
        psi.Environment.Remove("GIT_TERMINAL_PROMPT");

        // Signal *dispositions* survive execve when they are SIG_IGN, and a GUI process
        // very often has SIGINT/SIGQUIT ignored (that is what a shell does to background
        // jobs) plus SIGPIPE ignored (the .NET runtime does that). A shell started with
        // those inherited would never react to Ctrl+C and pipelines like `git log | head`
        // would report write errors, so restore the defaults across the fork and put our
        // own dispositions back immediately afterwards.
        nint oldInt = sys_signal(SIGINT, SIG_DFL);
        nint oldQuit = sys_signal(SIGQUIT, SIG_DFL);
        nint oldPipe = sys_signal(SIGPIPE, SIG_DFL);
        try
        {
            _child = Process.Start(psi) ?? throw new InvalidOperationException("could not start the shell");
        }
        finally
        {
            sys_signal(SIGINT, oldInt);
            sys_signal(SIGQUIT, oldQuit);
            sys_signal(SIGPIPE, oldPipe);
        }

        lock (s_live)
        {
            s_live.Add(new WeakReference<PtyProcess>(this));
        }

        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "pty-reader",
        };
        _reader.Start();
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private void ReadLoop()
    {
        byte[] buffer = new byte[8192];
        while (!_disposed)
        {
            int fd = _master;
            if (fd < 0)
            {
                break;
            }

            nint n;
            try
            {
                n = read(fd, buffer, buffer.Length);
            }
            catch
            {
                break;
            }

            if (n > 0)
            {
                try
                {
                    Output?.Invoke(buffer, (int)n);
                }
                catch
                {
                    // A rendering failure must never take the reader thread down.
                }
            }
            else if (n == 0)
            {
                break;
            }
            else
            {
                // EIO is the normal "slave side closed" indication on Linux.
                if (Marshal.GetLastWin32Error() == 4 /* EINTR */)
                {
                    continue;
                }

                break;
            }
        }

        try
        {
            Exited?.Invoke();
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>Sends raw bytes (already encoded) to the shell's standard input.</summary>
    public void Write(byte[] data)
    {
        if (_disposed || _master < 0 || data.Length == 0)
        {
            return;
        }

        lock (_writeLock)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                byte[] chunk = offset == 0 ? data : data[offset..];
                nint written = write(_master, chunk, chunk.Length);
                if (written <= 0)
                {
                    return;
                }

                offset += (int)written;
            }
        }
    }

    /// <summary>Sends UTF-8 text to the shell.</summary>
    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    /// <summary>Tells the kernel line discipline about the new window size (TIOCSWINSZ),
    /// which makes the shell and full-screen programs redraw at the right geometry.</summary>
    public void Resize(int cols, int rows)
    {
        if (_master < 0 || cols <= 0 || rows <= 0)
        {
            return;
        }

        WinSize ws = new()
        {
            Rows = (ushort)Math.Clamp(rows, 1, 1000),
            Cols = (ushort)Math.Clamp(cols, 1, 1000),
        };
        ioctl_winsize(_master, TIOCSWINSZ, ref ws);
    }

    /// <summary>Closes the PTY and terminates the shell (and its children).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        int fd = _master;
        _master = -1;
        if (fd >= 0)
        {
            // Closing the master hangs up the terminal: the session leader and its
            // foreground job get SIGHUP, so nothing is left behind as a zombie.
            close(fd);
        }

        Process? child = _child;
        _child = null;
        if (child is not null)
        {
            try
            {
                if (!child.WaitForExit(400))
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The shell may already be gone; nothing to do.
            }
            finally
            {
                child.Dispose();
            }
        }
    }

    private static void KillAll()
    {
        lock (s_live)
        {
            foreach (WeakReference<PtyProcess> reference in s_live)
            {
                if (reference.TryGetTarget(out PtyProcess? pty))
                {
                    try
                    {
                        pty.Dispose();
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }

            s_live.Clear();
        }
    }
}
