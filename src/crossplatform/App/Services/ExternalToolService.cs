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
///  A shell the user can start from the toolbar's shell split button — the port's
///  equivalent of upstream's <c>IShellDescriptor</c> (GitUI/Shells).
/// </summary>
/// <param name="Name">Display name, used as the menu caption and the button tooltip.</param>
/// <param name="Executable">The command as it is invoked (bare name resolved on PATH).</param>
/// <param name="IconName">
///  Base name of an icon in <c>Assets/Icons</c>. Only a handful of shells have real
///  artwork in the reused Windows icon set; the rest deliberately share the generic
///  <c>Console</c> icon rather than borrowing an unrelated one.
/// </param>
public sealed record ShellDescriptor(string Name, string Executable, string IconName);

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
    // Terminals to probe, in preference order. DirArg is the flag used to set the
    // working directory (null when it inherits the launcher's cwd, which we set via
    // ProcessStartInfo.WorkingDirectory). ExecArg introduces the command to run
    // instead of the login shell: gnome-terminal deprecated "-e" in favour of the
    // "--" separator, everything else in this list understands "-e".
    // ExecArg is null for the terminals that take the program as a bare trailing
    // argument (kitty, foot); everything else introduces it with a flag.
    private static readonly (string Exe, string? DirArg, string? ExecArg)[] Terminals =
    {
        // Debian's "whatever the user chose" alias goes first, but it is NOT trusted:
        // on this machine it resolves to Warp, whose CLI rejects -e, prints its usage
        // and exits 2. That is exactly the reported "the bash button does nothing" —
        // the launch succeeded, the terminal did not. Hence the exit check below.
        ("x-terminal-emulator", null, "-e"),
        ("gnome-terminal", "--working-directory", "--"),
        ("kgx", "--working-directory", "--"),
        ("ptyxis", "--working-directory", "--"),
        ("konsole", "--workdir", "-e"),
        ("xfce4-terminal", "--working-directory", "-e"),
        ("tilix", "--working-directory", "-e"),
        ("terminator", "--working-directory", "-e"),
        ("mate-terminal", "--working-directory", "-e"),
        ("alacritty", "--working-directory", "-e"),
        ("kitty", "--directory", null),
        ("foot", "--working-directory", null),
        ("urxvt", null, "-e"),
        ("xterm", null, "-e"),
    };

    // Every shell the port knows how to offer, in the order the dropdown lists them:
    // the interactive shells people actually pick first, then the POSIX baselines.
    // Presence is decided by probing PATH — nothing here is assumed to exist.
    // Icons: the reused Windows icon set only ships cmd/Console/powershell, so pwsh
    // gets "powershell" and every Unix shell falls back to the generic "Console".
    private static readonly ShellDescriptor[] KnownShells =
    {
        new("Bash", "bash", "Console"),
        new("Zsh", "zsh", "Console"),
        new("Fish", "fish", "Console"),
        new("Nushell", "nu", "Console"),
        new("Elvish", "elvish", "Console"),
        new("Xonsh", "xonsh", "Console"),
        new("Ksh", "ksh", "Console"),
        new("Tcsh", "tcsh", "Console"),
        new("Csh", "csh", "Console"),
        new("Dash", "dash", "Console"),
        new("Sh", "sh", "Console"),
        new("PowerShell", "pwsh", "powershell"),
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
        foreach ((string exe, string? _, string? _) in Terminals)
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
    public ExternalToolResult OpenTerminal(string dir) => OpenTerminal(dir, shellExecutable: null);

    /// <summary>
    ///  Opens a terminal emulator in <paramref name="dir"/> running
    ///  <paramref name="shellExecutable"/>. A null/empty shell keeps the previous
    ///  behaviour: the emulator starts the user's login shell.
    /// </summary>
    public ExternalToolResult OpenTerminal(string dir, string? shellExecutable)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return new ExternalToolResult(false, "No directory for the terminal.");
        }

        // A shell we cannot find would produce a terminal that flashes and dies;
        // fall back to the emulator's own default instead.
        string? shell = string.IsNullOrWhiteSpace(shellExecutable) || !OnPath(shellExecutable!)
            ? null
            : shellExecutable;

        List<string> failures = [];

        // A command the user named wins over the probe list: the list can only ever
        // guess, and it cannot cover every emulator's CLI (Warp is reachable through
        // x-terminal-emulator but rejects the -e the list passes it — M127). It is
        // tried FIRST and, if it will not start, we still fall through to the probe
        // rather than leaving the button dead: a typo in the setting should degrade
        // to the old behaviour, not break the feature.
        string configured = ConfiguredTerminalCommand();
        if (configured.Length > 0)
        {
            string[] parts = SplitCommand(configured);
            if (parts.Length > 0 && parts[0].Length > 0)
            {
                // {dir} / {shell} are substituted wherever they appear; a command that
                // names neither still gets the directory (ProcessStartInfo.WorkingDirectory,
                // set by LaunchTerminal) and simply starts the login shell.
                List<string> args = [.. parts.Skip(1)
                    .Select(a => a.Replace("{dir}", dir, StringComparison.Ordinal)
                                  .Replace("{shell}", shell ?? string.Empty, StringComparison.Ordinal))
                    .Where(a => a.Length > 0)];

                ExternalToolResult configuredResult = LaunchTerminal(
                    parts[0].Replace("{dir}", dir, StringComparison.Ordinal),
                    args,
                    dir,
                    friendly: shell is null
                        ? $"Opened terminal in {dir}"
                        : $"Opened {shell} in {dir}");
                if (configuredResult.Success)
                {
                    return configuredResult;
                }

                failures.Add(parts[0]);
            }
        }

        foreach ((string exe, string? dirArg, string? execArg) in Terminals)
        {
            if (!OnPath(exe))
            {
                continue;
            }

            // When the terminal has an explicit working-directory flag we pass
            // it; otherwise we rely on ProcessStartInfo.WorkingDirectory.
            List<string> args = [];
            if (dirArg is not null)
            {
                args.Add(dirArg);
                args.Add(dir);
            }

            if (shell is not null)
            {
                if (execArg is not null)
                {
                    args.Add(execArg);
                }

                args.Add(shell);
            }

            ExternalToolResult result = LaunchTerminal(exe, args, dir,
                friendly: shell is null
                    ? $"Opened terminal in {dir}"
                    : $"Opened {shell} in {dir}");
            if (result.Success)
            {
                return result;
            }

            failures.Add(exe);
        }

        return new ExternalToolResult(false, failures.Count > 0
            ? $"No terminal emulator would start (tried {string.Join(", ", failures)})."
            : "No terminal emulator found on PATH.");
    }

    // ---- shells (upstream ShellProvider / FillUserShells) ----------------------

    /// <summary>
    ///  Lists the shells that are actually installed, in dropdown order. Mirrors
    ///  upstream's <c>FillUserShells</c>, which skips every descriptor whose
    ///  <c>HasExecutable</c> is false, so the menu never offers a shell that is not
    ///  there. The user's login shell (<c>$SHELL</c>) is appended when it is not one
    ///  of the known names, so an unusual choice is still reachable.
    ///
    ///  <para>Probes PATH: call it off the UI thread.</para>
    /// </summary>
    public static IReadOnlyList<ShellDescriptor> GetShells()
    {
        List<ShellDescriptor> found = KnownShells.Where(s => OnPath(s.Executable)).ToList();

        string? login = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(login))
        {
            string name = Path.GetFileName(login);
            if (name.Length > 0
                && !found.Any(s => string.Equals(s.Executable, name, StringComparison.Ordinal))
                && OnPath(login))
            {
                // Title-cased basename, the same shape as the known entries.
                found.Add(new ShellDescriptor(
                    char.ToUpperInvariant(name[0]) + name[1..], login, "Console"));
            }
        }

        return found;
    }

    /// <summary>
    ///  The <see cref="ShellDescriptor.Executable"/> the user last picked, or null.
    ///  Upstream keeps this in AppSettings; the port has no settings store it owns,
    ///  so — like <see cref="FavoritesService"/> — it uses a small file of its own
    ///  under the user's config directory.
    /// </summary>
    public static string? LoadPreferredShell()
    {
        try
        {
            string path = ShellPreferencePath();
            if (File.Exists(path))
            {
                string value = File.ReadAllText(path).Trim();
                return value.Length == 0 ? null : value;
            }
        }
        catch
        {
            // Unreadable/corrupt → no preference.
        }

        return null;
    }

    /// <summary>Persists the picked shell. Best-effort; never throws.</summary>
    public static void SavePreferredShell(string executable)
    {
        try
        {
            string path = ShellPreferencePath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, executable);
        }
        catch
        {
            // A persistence failure must not break launching the shell.
        }
    }

    private static string ShellPreferencePath()
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(baseDir, "GitExtensions.Avalonia", "shell");
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

    /// <summary>How long a terminal is given to fail before we call the launch good.</summary>
    private static readonly TimeSpan TerminalStartupGrace = TimeSpan.FromMilliseconds(700);

    /// <summary>
    ///  Starts a terminal and gives it a moment to prove it survived.
    ///
    ///  <para><see cref="Process.Start(ProcessStartInfo)"/> succeeding means the binary
    ///  was executed, not that a terminal opened. A candidate that does not understand
    ///  the arguments we pass — <c>x-terminal-emulator</c> pointing at a terminal with
    ///  a different CLI is the case that prompted this — prints its usage and exits
    ///  non-zero in a few milliseconds, and the old code reported that as success and
    ///  stopped trying: the button did nothing and said it had worked.</para>
    ///
    ///  <para>A terminal that is still running after the grace period is a terminal
    ///  that opened; one that exited 0 (the gnome-terminal client hands off to its
    ///  server and returns) is fine too. Only a non-zero exit is a failure, and the
    ///  caller then tries the next candidate. Blocking for up to
    ///  <see cref="TerminalStartupGrace"/>, which is why the callers run this off the
    ///  UI thread.</para>
    /// </summary>
    // Read at launch time rather than cached: the setting is edited in a dialog that
    // does not know this service exists, and a terminal is opened rarely enough that
    // one small JSON read costs nothing. Never throws — an unreadable state file just
    // means "no configured command".
    private static string ConfiguredTerminalCommand()
    {
        try
        {
            return new UiStateService().Load().TerminalCommand?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private ExternalToolResult LaunchTerminal(string exe, IReadOnlyList<string> args, string dir, string friendly)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = exe,
                UseShellExecute = false,
                WorkingDirectory = dir,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return new ExternalToolResult(false, $"Could not start {exe}.");
            }

            if (proc.WaitForExit(TerminalStartupGrace) && proc.ExitCode != 0)
            {
                // Its complaint is the useful part of the message: "unexpected argument
                // '-e'" says precisely why this candidate is not the one.
                string complaint = proc.StandardError.ReadToEnd().Trim();
                int newline = complaint.IndexOf('\n');
                if (newline > 0)
                {
                    complaint = complaint[..newline];
                }

                return new ExternalToolResult(false, complaint.Length > 0
                    ? $"{exe}: {complaint}"
                    : $"{exe} exited with {proc.ExitCode}.");
            }

            return new ExternalToolResult(true, friendly);
        }
        catch (Exception ex)
        {
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
