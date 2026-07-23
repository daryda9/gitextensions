using System.Diagnostics;
using System.Runtime.InteropServices;
namespace GitCommands.Git.Extensions;

public static class ProcessExtensions
{
    public static void TerminateTree(this Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            // Send Ctrl+C
            NativeMethods.AttachConsole(process.Id);
            NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, add: true);
            NativeMethods.GenerateConsoleCtrlEvent(0, 0);

            if (!process.HasExited)
            {
                process.WaitForExit(500);
            }
        }
        else
        {
            // Unix (Linux/macOS): the console-control APIs above do not exist.
            // Send SIGINT to the child, which is the graceful "Ctrl+C" equivalent
            // and lets git flush/clean up. If delivery fails we fall through to the
            // hard Process.Kill below.
            try
            {
                if (NativeMethods.kill(process.Id, NativeMethods.SIGINT) == 0 && !process.HasExited)
                {
                    process.WaitForExit(500);
                }
            }
            catch (DllNotFoundException)
            {
                // libc unavailable on this platform; fall back to the hard kill below.
            }
            catch (EntryPointNotFoundException)
            {
                // kill(2) unavailable; fall back to the hard kill below.
            }
        }

        if (!process.HasExited)
        {
            if (OperatingSystem.IsWindows())
            {
                process.Kill();
            }
            else
            {
                // Ensure the whole child tree is reaped when SIGINT was ignored.
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, int dwProcessGroupId);

        // POSIX signal number for interrupt (Ctrl+C).
        public const int SIGINT = 2;

        // POSIX kill(2); sends a signal to a process. Only invoked on non-Windows.
        [DllImport("libc", SetLastError = true)]
        public static extern int kill(int pid, int sig);
    }
}
