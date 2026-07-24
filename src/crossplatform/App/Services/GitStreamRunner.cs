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
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            onLine("<error: " + ex.Message + ">");
            return -1;
        }
    }
}
