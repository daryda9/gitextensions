using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  When a user script runs — upstream's <c>GitUI.ScriptsEngine.ScriptEvent</c>, name for
///  name so a script written against the Windows documentation means the same thing here.
///
///  <para>Two of upstream's values are not events at all but placements, and are kept for
///  the same reason: <see cref="ShowInUserMenuBar"/> puts the script in the Tools menu and
///  <see cref="ShowInFileList"/> in the changed-file list's context menu.</para>
/// </summary>
public enum UserScriptEvent
{
    None,
    BeforeCommit,
    AfterCommit,
    BeforePull,
    AfterPull,
    BeforePush,
    AfterPush,
    ShowInUserMenuBar,
    BeforeCheckout,
    AfterCheckout,
    BeforeMerge,
    AfterMerge,
    BeforeFetch,
    AfterFetch,
    ShowInFileList,
}

/// <summary>
///  One user script: a command line to run, and when. The port of upstream's
///  <c>ScriptInfo</c>.
///
///  <para><b>Not ported, deliberately.</b> <c>IsPowerShell</c> (a Windows shell),
///  <c>Icon</c>/<c>IconFilePath</c> (the port has no icon picker and a script does not need
///  one to work), and <c>HotkeyCommandIdentifier</c> — the port's hotkey store binds named
///  commands, and a per-script hotkey would need a whole dynamic scope. Each of those is an
///  addition to this record, not a rewrite, if it is ever wanted.</para>
/// </summary>
public sealed class UserScript
{
    /// <summary>Whether the script is offered/run at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What the menus call it. Also what a confirmation asks about.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///  The executable. Not run through a shell: a bare name is resolved on PATH by the
    ///  process launcher, and quoting rules that differ per shell cannot bite. A script
    ///  that wants a pipeline should name its shell explicitly (<c>bash</c>, <c>-c</c>, …).
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    ///  The arguments, with <c>{placeholders}</c> (see
    ///  <see cref="UserScriptService.Placeholders"/>). Split on whitespace, honouring
    ///  double quotes, AFTER expansion — so a commit subject with spaces stays one
    ///  argument when it is quoted in the template.
    /// </summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>When it runs, or where it is offered.</summary>
    public UserScriptEvent OnEvent { get; set; } = UserScriptEvent.None;

    /// <summary>Ask before running.</summary>
    public bool AskConfirmation { get; set; }

    /// <summary>
    ///  Run without showing the process window. A background script's output is still
    ///  captured and reported when it FAILS — silence on success, never on failure.
    /// </summary>
    public bool RunInBackground { get; set; }

    /// <summary>Also offer it in the revision grid's context menu.</summary>
    public bool AddToRevisionGridContextMenu { get; set; }
}

/// <summary>
///  Everything the port can substitute into a script's arguments: the selected revision,
///  the current branch, the repository. Filled by the caller that knows them, so a hook in
///  the commit dialog does not have to run git to describe the commit it just made.
/// </summary>
/// <param name="RepoPath">The working directory. Always known.</param>
/// <param name="CurrentBranch">
///  The checked-out branch. Empty means "ask git" rather than "detached": a caller that
///  already knows it saves the lookup, one that does not passes nothing.
/// </param>
/// <param name="SelectedHashes">Selected revisions, newest first; may be empty.</param>
/// <param name="Subject">Subject of the first selected revision.</param>
/// <param name="Message">Full message of the first selected revision.</param>
/// <param name="Author">Author of the first selected revision, "Name &lt;mail&gt;".</param>
/// <param name="Committer">Committer of the first selected revision.</param>
/// <param name="AuthorDate">Author date of the first selected revision, ISO-8601.</param>
/// <param name="CommitDate">Commit date of the first selected revision, ISO-8601.</param>
/// <param name="Remote">The remote the operation concerns, when it has one.</param>
/// <param name="RemoteBranch">The remote branch the operation concerns, when it has one.</param>
public sealed record UserScriptContext(
    string RepoPath,
    string CurrentBranch = "",
    IReadOnlyList<string>? SelectedHashes = null,
    string Subject = "",
    string Message = "",
    string Author = "",
    string Committer = "",
    string AuthorDate = "",
    string CommitDate = "",
    string Remote = "",
    string RemoteBranch = "");

/// <summary>The outcome of one script run.</summary>
/// <param name="Ran">Whether the process was actually started (false = declined or disabled).</param>
/// <param name="Success">Whether it exited with 0.</param>
/// <param name="Output">Everything it printed, both streams, in order of arrival.</param>
public sealed record UserScriptResult(bool Ran, bool Success, string Output);

/// <summary>
///  The user scripts: their store, their placeholder expansion and their execution —
///  upstream's <c>ScriptsManager</c> + <c>ScriptOptionsParser</c> + <c>ScriptRunner</c>,
///  which the port had none of.
///
///  <para>Stored in <c>scripts.json</c> beside the other port settings rather than in the
///  core's XML settings: the core's <c>ScriptInfo</c> is serialised as part of a Windows
///  settings blob this port does not write, and a JSON file is something a user can read
///  and fix by hand — which matters for a feature whose whole point is running the user's
///  own commands.</para>
///
///  <para><b>A script is never run through a shell.</b> <see cref="UserScript.Command"/> is
///  the executable and the arguments are a list, so a repository name with a space, a
///  quote or a semicolon in it cannot turn into extra commands. Upstream builds a command
///  string; here that would be handing the shell whatever a branch happens to be called.</para>
/// </summary>
public sealed class UserScriptService
{
    /// <summary>
    ///  The placeholders this port fills, with what each one means. Upstream's list is
    ///  longer; the entries left out are the ones whose value the port cannot produce
    ///  without inventing it — the tag/remote-branch family of a SELECTED revision, and
    ///  the <c>{UserInput}</c>/<c>{UserFiles}</c> prompts, which need dialogs of their own.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Meaning)> Placeholders =
    [
        ("WorkingDir", "the repository's working directory"),
        ("RepoName", "the repository's folder name"),
        ("cBranch", "the currently checked-out branch"),
        ("cHash", "the commit HEAD points at"),
        ("cDefaultRemote", "the current branch's remote, or origin"),
        ("cDefaultRemoteUrl", "that remote's URL"),
        ("sHash", "the selected revision"),
        ("sHashes", "every selected revision, space separated"),
        ("sSubject", "the selected revision's subject"),
        ("sMessage", "the selected revision's full message"),
        ("sAuthor", "the selected revision's author"),
        ("sCommitter", "the selected revision's committer"),
        ("sAuthorDate", "the selected revision's author date"),
        ("sCommitDate", "the selected revision's commit date"),
        ("sRemote", "the remote the running operation concerns"),
        ("sRemoteBranch", "the remote branch the running operation concerns"),
    ];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    ///  Everything the shared file machinery needs to know about this document. Built
    ///  once and static, because <see cref="JsonSettingsFile{T}.For"/> keeps the first
    ///  model it is given for a path.
    /// </summary>
    private static readonly JsonSettingsModel<List<UserScript>> Model = new(
        static () => [],
        static text => JsonSerializer.Deserialize<List<UserScript>>(text, Options),
        static scripts => JsonSerializer.Serialize(scripts, Options),
        static scripts => scripts,
        "saving user scripts",
        static () => Changed?.Invoke());

    private readonly JsonSettingsFile<List<UserScript>> _file;

    public UserScriptService() => _file = JsonSettingsFile<List<UserScript>>.For(ResolvePath(), Model);

    /// <summary>Raised after <see cref="Save"/>, so open menus can rebuild.</summary>
    public static event Action? Changed;

    /// <summary>The resolved JSON path (for diagnostics and for the Settings page's note).</summary>
    public string FilePath => _file.Path;

    /// <summary>
    ///  Every script, in file order. Missing/corrupt file yields none — a hand-edited file
    ///  with a typo must not stop the app from starting; the Settings page is where the
    ///  user can see and rewrite the list.
    /// </summary>
    public IReadOnlyList<UserScript> Load() => _file.Load();

    /// <summary>
    ///  Writes the list; best-effort, then raises <see cref="Changed"/>.
    ///
    ///  <para>Whole-document, and legitimately so: the only editor is the Settings page's
    ///  script list, whose Save button means "these are the scripts now". What the shared
    ///  file adds is the atomic replace — a kill mid-write used to leave a truncated file,
    ///  which <see cref="Load"/> reads as "no scripts at all".</para>
    /// </summary>
    public void Save(IReadOnlyList<UserScript> scripts) => _file.Save([.. scripts]);

    /// <summary>Waits for deferred writes to reach the disk. Tests and shutdown only; blocks.</summary>
    public bool Flush(TimeSpan timeout) => _file.Flush(timeout);

    /// <summary>The enabled scripts bound to <paramref name="scriptEvent"/>, in file order.</summary>
    public IReadOnlyList<UserScript> For(UserScriptEvent scriptEvent)
        => [.. Load().Where(s => s.Enabled && s.OnEvent == scriptEvent)];

    /// <summary>
    ///  Expands the <c>{placeholders}</c> of <paramref name="template"/> from
    ///  <paramref name="context"/>. An unknown placeholder is left ALONE rather than
    ///  emptied: a script that prints <c>{foo}</c> is debuggable, one whose argument
    ///  silently vanished is not.
    /// </summary>
    public static string Expand(string template, UserScriptContext context)
    {
        if (template.Length == 0)
        {
            return template;
        }

        IReadOnlyList<string> hashes = context.SelectedHashes ?? [];
        (string Key, string Value)[] values =
        [
            ("WorkingDir", context.RepoPath),
            ("RepoName", Path.GetFileName(context.RepoPath.TrimEnd('/'))),
            ("cBranch", context.CurrentBranch.Length > 0
                ? context.CurrentBranch
                : Git(context.RepoPath, "symbolic-ref", "--short", "HEAD")),
            ("cHash", ReadHead(context.RepoPath)),
            ("cDefaultRemote", context.Remote.Length > 0 ? context.Remote : DefaultRemote(context.RepoPath)),
            ("cDefaultRemoteUrl", RemoteUrl(context.RepoPath, context.Remote.Length > 0 ? context.Remote : DefaultRemote(context.RepoPath))),
            ("sHash", hashes.Count > 0 ? hashes[0] : string.Empty),
            ("sHashes", string.Join(' ', hashes)),
            ("sSubject", context.Subject),
            ("sMessage", context.Message),
            ("sAuthor", context.Author),
            ("sCommitter", context.Committer),
            ("sAuthorDate", context.AuthorDate),
            ("sCommitDate", context.CommitDate),
            ("sRemote", context.Remote),
            ("sRemoteBranch", context.RemoteBranch),
        ];

        StringBuilder expanded = new(template);
        foreach ((string key, string value) in values)
        {
            expanded.Replace("{" + key + "}", value);
        }

        return expanded.ToString();
    }

    /// <summary>
    ///  Splits an expanded argument string into arguments, honouring double quotes so a
    ///  quoted placeholder that expanded to several words stays one argument. Not a shell
    ///  parser and not meant to be: no globbing, no variables, no operators — see the
    ///  class remarks for why.
    /// </summary>
    public static List<string> SplitArguments(string arguments)
    {
        List<string> parts = [];
        StringBuilder current = new();
        bool quoted = false;
        bool any = false;

        foreach (char c in arguments)
        {
            if (c == '"')
            {
                quoted = !quoted;

                // An empty pair of quotes is a real, empty argument.
                any = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    any = false;
                }

                continue;
            }

            current.Append(c);
            any = true;
        }

        if (any)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>
    ///  Runs one script and returns what it printed. Never throws: a command that does not
    ///  exist is a failed run with the launcher's message as its output, which is what the
    ///  caller shows. Must not be called on the UI thread.
    /// </summary>
    public static UserScriptResult Run(UserScript script, UserScriptContext context)
    {
        string command = Expand(script.Command, context).Trim();
        if (command.Length == 0)
        {
            return new UserScriptResult(Ran: false, Success: false, Output: string.Empty);
        }

        ProcessStartInfo info = new()
        {
            FileName = command,
            WorkingDirectory = context.RepoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in SplitArguments(Expand(script.Arguments, context)))
        {
            info.ArgumentList.Add(argument);
        }

        StringBuilder output = new();
        try
        {
            using Process process = new() { StartInfo = info };
            process.OutputDataReceived += (_, e) => Append(output, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            return new UserScriptResult(Ran: true, process.ExitCode == 0, output.ToString());
        }
        catch (Exception ex)
        {
            return new UserScriptResult(Ran: true, Success: false, Output: ex.Message);
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is not null)
        {
            lock (output)
            {
                output.AppendLine(line);
            }
        }
    }

    // The three git facts a script may ask for that the caller usually does not hold.
    // Read with plumbing and never allowed to throw: a script's arguments are not worth
    // failing an operation over.
    private static string ReadHead(string repoPath) => Git(repoPath, "rev-parse", "HEAD");

    private static string DefaultRemote(string repoPath)
    {
        string remote = Git(repoPath, "config", "--get", "branch." + Git(repoPath, "symbolic-ref", "--short", "HEAD") + ".remote");
        return remote.Length > 0 ? remote : "origin";
    }

    private static string RemoteUrl(string repoPath, string remote)
        => remote.Length == 0 ? string.Empty : Git(repoPath, "remote", "get-url", remote);

    private static string Git(string repoPath, params string[] arguments)
    {
        try
        {
            ProcessStartInfo info = new()
            {
                FileName = "git",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(info);
            if (process is null)
            {
                return string.Empty;
            }

            string result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolvePath() => SettingsPaths.Resolve("scripts.json");
}
