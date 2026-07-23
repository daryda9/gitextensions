using System.Diagnostics;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Outcome of an external-tool launch: whether the process was started
///  (not whether the tool itself later succeeds — these are fire-and-forget),
///  and a human-readable message for the status bar. Nothing here ever throws
///  to the caller; failures come back as <c>Success = false</c> with a message.
/// </summary>
public sealed record ExternalToolResult(bool Success, string Message);

/// <summary>
///  Launches external programs, files, URLs and terminals on Linux without
///  blocking or crashing the UI thread. Every method catches its own errors
///  (missing binary, no display, etc.) and returns an <see cref="ExternalToolResult"/>
///  the host can surface in the status bar.
///
///  Design mirrors the original GitExtensions "Repository / Tools / Help" menu
///  launchers, but targets freedesktop tooling: <c>xdg-open</c> for files and
///  URLs, and a best-effort terminal probe for the "Git bash" equivalent.
/// </summary>
public sealed class ExternalToolService
{
    // Terminals to probe, in preference order. Each entry is the executable and
    // the flag used to set its working directory (null when it inherits the
    // launcher's cwd, which we set via ProcessStartInfo.WorkingDirectory).
    private static readonly (string Exe, string? DirArg)[] Terminals =
    {
        ("x-terminal-emulator", null),
        ("gnome-terminal", "--working-directory"),
        ("konsole", "--workdir"),
        ("xfce4-terminal", "--working-directory"),
        ("xterm", null),
    };

    /// <summary>
    ///  Opens a file or directory with the desktop's default handler via
    ///  <c>xdg-open</c> (a file manager for a directory, the default editor for
    ///  a text file, etc.).
    /// </summary>
    public ExternalToolResult OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ExternalToolResult(false, "No path to open.");
        }

        return LaunchDetached("xdg-open", new[] { path }, workingDir: null,
            friendly: $"Opened {path}");
    }

    /// <summary>Opens a URL in the default browser via <c>xdg-open</c>.</summary>
    public ExternalToolResult OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ExternalToolResult(false, "No URL to open.");
        }

        return LaunchDetached("xdg-open", new[] { url }, workingDir: null,
            friendly: $"Opened {url}");
    }

    /// <summary>
    ///  Ensures the given file exists (creating an empty one if missing) and then
    ///  opens it with the default text editor via <c>xdg-open</c>. Used for the
    ///  "Edit .gitignore/.gitattributes/.mailmap/exclude" entries so the editor
    ///  opens even on a fresh repository.
    /// </summary>
    public ExternalToolResult OpenOrCreateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ExternalToolResult(false, "No file to open.");
        }

        try
        {
            if (!File.Exists(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Create(path).Dispose();
            }
        }
        catch (Exception ex)
        {
            return new ExternalToolResult(false, $"Could not create {path}: {ex.Message}");
        }

        return OpenPath(path);
    }

    /// <summary>
    ///  Opens a terminal emulator in <paramref name="dir"/>, probing common
    ///  terminals in order. If none is found the result reports that so the host
    ///  can surface a message rather than crash.
    /// </summary>
    public ExternalToolResult OpenTerminal(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return new ExternalToolResult(false, "No directory for the terminal.");
        }

        foreach ((string exe, string? dirArg) in Terminals)
        {
            if (!OnPath(exe))
            {
                continue;
            }

            // When the terminal has an explicit working-directory flag we pass
            // it; otherwise we rely on ProcessStartInfo.WorkingDirectory.
            string[] args = dirArg is null
                ? Array.Empty<string>()
                : new[] { dirArg, dir };

            ExternalToolResult result = LaunchDetached(exe, args, workingDir: dir,
                friendly: $"Opened terminal in {dir}");
            if (result.Success)
            {
                return result;
            }
        }

        return new ExternalToolResult(false,
            "No terminal emulator found (tried x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, xterm).");
    }

    /// <summary>
    ///  Starts <paramref name="exe"/> with <paramref name="args"/> detached
    ///  (non-blocking, no shell), with an optional working directory. Returns a
    ///  failed result — never throws — if the binary is missing or the launch
    ///  fails (e.g. no X display). <c>UseShellExecute = false</c> so we invoke the
    ///  executable directly and get its real error.
    /// </summary>
    public ExternalToolResult LaunchDetached(string exe, IReadOnlyList<string> args, string? workingDir, string? friendly = null)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                UseShellExecute = false,
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            if (!string.IsNullOrEmpty(workingDir))
            {
                psi.WorkingDirectory = workingDir;
            }

            Process? proc = Process.Start(psi);
            return proc is null
                ? new ExternalToolResult(false, $"Could not start {exe}.")
                : new ExternalToolResult(true, friendly ?? $"Launched {exe}");
        }
        catch (Exception ex)
        {
            // Missing binary (Win32Exception), no display, permissions, etc.
            return new ExternalToolResult(false, $"Could not launch {exe}: {ex.Message}");
        }
    }

    // True when a bare executable name resolves on PATH. Absolute paths are
    // checked directly. Used to skip absent terminals before attempting a launch.
    private static bool OnPath(string exe)
    {
        if (exe.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(exe);
        }

        string paths = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, exe)))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return false;
    }
}
