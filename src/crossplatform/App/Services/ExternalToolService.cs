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
    ///  Reveals <paramref name="path"/> in the desktop's file manager.
    ///
    ///  <para>Tries the freedesktop <c>org.freedesktop.FileManager1.ShowItems</c>
    ///  D-Bus call first, which opens the containing folder <em>with the file
    ///  selected</em> (Nautilus, Dolphin, Thunar, Nemo all implement it). Falls
    ///  back to <c>xdg-open</c> on the containing directory when there is no
    ///  session bus or no implementor — headless sessions, for instance.</para>
    /// </summary>
    public ExternalToolResult ShowInFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ExternalToolResult(false, "No path to show.");
        }

        string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            return new ExternalToolResult(false, $"No containing folder for {path}.");
        }

        if (File.Exists(path) && OnPath("dbus-send") &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            ExternalToolResult shown = LaunchDetached(
                "dbus-send",
                new[]
                {
                    "--session",
                    "--dest=org.freedesktop.FileManager1",
                    "--type=method_call",
                    "/org/freedesktop/FileManager1",
                    "org.freedesktop.FileManager1.ShowItems",
                    "array:string:" + new Uri(path).AbsoluteUri,
                    "string:",
                },
                workingDir: null,
                friendly: $"Showing {path} in the file manager");

            if (shown.Success)
            {
                return shown;
            }
        }

        return LaunchDetached("xdg-open", new[] { dir }, workingDir: null,
            friendly: $"Opened folder {dir}");
    }

    // Editors that only make sense inside a terminal: launching them detached
    // from a GUI would start a process with no visible window.
    private static readonly string[] TerminalEditors =
    {
        "vi", "vim", "nvim", "nano", "pico", "ed", "joe", "micro", "hx", "helix", "emacsclient -nw", "emacs -nw",
    };

    /// <summary>
    ///  Opens <paramref name="path"/> in an external editor.
    ///
    ///  <para>Resolution order, and why: git's own <c>GIT_EDITOR</c> /
    ///  <c>core.editor</c> / <c>$VISUAL</c> / <c>$EDITOR</c> comes first, because
    ///  that is the editor the user already told <em>git</em> to use and the
    ///  Windows original likewise honours the configured editor. But most Linux
    ///  users configure a console editor there, which cannot be launched
    ///  detached from a GUI — so a configured console editor is wrapped in a
    ///  terminal emulator. When nothing is configured we hand the file to
    ///  <c>xdg-open</c>, i.e. the desktop's registered handler for that file
    ///  type, which is the closest equivalent of Windows' ShellExecute.</para>
    ///
    ///  <para>Runs <c>git config</c>: call it off the UI thread.</para>
    /// </summary>
    public ExternalToolResult OpenInEditor(string path, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ExternalToolResult(false, "No file to open.");
        }

        string? editor = ResolveEditor(repoPath);
        if (string.IsNullOrWhiteSpace(editor))
        {
            return OpenPath(path);
        }

        string command = editor!.Trim();
        string exe = SplitCommand(command)[0];
        string baseName = Path.GetFileName(exe);

        if (TerminalEditors.Any(e => string.Equals(e, baseName, StringComparison.Ordinal)) ||
            TerminalEditors.Any(e => command.StartsWith(e + " ", StringComparison.Ordinal)))
        {
            return OpenInTerminalEditor(command, path);
        }

        List<string> args = SplitCommand(command).Skip(1).ToList();
        args.Add(path);

        ExternalToolResult result = LaunchDetached(exe, args, workingDir: repoPath,
            friendly: $"Opened {path} in {baseName}");

        // A configured-but-unusable editor must not be a dead end.
        return result.Success ? result : OpenPath(path);
    }

    // Runs a console editor inside the first terminal emulator we find.
    private ExternalToolResult OpenInTerminalEditor(string command, string path)
    {
        foreach ((string exe, string? _) in Terminals)
        {
            if (!OnPath(exe))
            {
                continue;
            }

            // "-e" is understood by every terminal in the probe list; the command
            // is passed through a shell so a multi-word core.editor keeps working.
            ExternalToolResult result = LaunchDetached(
                exe,
                new[] { "-e", "sh", "-c", command + " \"$1\"", "sh", path },
                workingDir: Path.GetDirectoryName(path),
                friendly: $"Opened {path} in {command}");

            if (result.Success)
            {
                return result;
            }
        }

        return OpenPath(path);
    }

    /// <summary>
    ///  The editor git itself would use: <c>GIT_EDITOR</c>, then
    ///  <c>core.editor</c>, then <c>$VISUAL</c>, then <c>$EDITOR</c>. Returns
    ///  <c>null</c> when none is set. Blocking (runs <c>git config</c>).
    /// </summary>
    public static string? ResolveEditor(string? repoPath)
    {
        string? fromEnv = Environment.GetEnvironmentVariable("GIT_EDITOR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        string? configured = ReadGitConfig(repoPath, "core.editor");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        foreach (string name in new[] { "VISUAL", "EDITOR" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadGitConfig(string? repoPath, string key)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("--get");
            psi.ArgumentList.Add(key);

            if (!string.IsNullOrEmpty(repoPath) && Directory.Exists(repoPath))
            {
                psi.WorkingDirectory = repoPath;
            }

            using Process process = new() { StartInfo = psi };
            process.Start();
            string value = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? value.Trim() : null;
        }
        catch (Exception)
        {
            // No git on PATH, no repository: treat as "not configured".
            return null;
        }
    }

    // Minimal shell-ish tokenizer: enough for the "code --wait", "gedit" and
    // "'/opt/My Editor/bin/ed' -n" shapes people put in core.editor.
    private static string[] SplitCommand(string command)
    {
        List<string> parts = [];
        System.Text.StringBuilder current = new();
        char quote = '\0';

        foreach (char c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.Count == 0 ? [command] : parts.ToArray();
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
