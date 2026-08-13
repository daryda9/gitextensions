using System.Diagnostics;
using System.Globalization;
using System.Text;
using GitCommands.Remotes;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A git remote of the current repository that points at the configured GitHub host
///  — upstream's <c>GitHubHostedRemote</c>.
/// </summary>
/// <param name="Name">The local remote name (<c>origin</c>, <c>upstream</c>, …).</param>
/// <param name="Owner">The account the remote repository belongs to.</param>
/// <param name="Repository">The remote repository's name, without <c>.git</c>.</param>
/// <param name="Url">The configured fetch URL, kept so the protocol can be reused.</param>
public sealed record GitHubHostedRemote(string Name, string Owner, string Repository, string Url)
{
    /// <summary><c>owner/repository</c>, the way both this port and upstream label a remote.</summary>
    public string Data => $"{Owner}/{Repository}";

    /// <summary>
    ///  Whether the remote is configured over SSH. A clone or a fetch the port starts
    ///  on behalf of a pull request follows the protocol the user already uses, rather
    ///  than forcing HTTPS and asking for credentials they had arranged not to need.
    /// </summary>
    public bool UsesSsh => !Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name} — {Data}";
}

/// <summary>
///  Where the GitHub personal access token lives.
///
///  <para><b>Not in the settings file.</b> A token is a password: it can push to every
///  repository the account can, and <c>app-settings.json</c> is a world-readable file
///  that gets copied around with a dotfiles repo. The token is handed to <b>git's own
///  credential helper</b> instead — on this machine that is
///  <c>git-credential-libsecret</c>, i.e. the desktop keyring — through the documented
///  <c>git credential approve/fill/reject</c> protocol, under the host
///  <c>api.github.com</c>.</para>
///
///  <para>The API host, deliberately, and not <c>github.com</c>: overwriting the entry
///  git uses to push would be a nasty surprise for anyone who had a different token
///  stored for that.</para>
///
///  <para>When no helper is configured, git happily accepts <c>approve</c> and forgets
///  it. That silent no-op is why <see cref="Save"/> reads the value back before
///  reporting where it went, and falls back to a file with owner-only permissions —
///  which <see cref="Describe"/> then says out loud, because a user who thinks their
///  token is in the keyring deserves to know it is on disk.</para>
/// </summary>
public static class GitHubTokenStore
{
    /// <summary>Where a token ended up (or would end up), for the settings page to show.</summary>
    public enum Storage
    {
        /// <summary>No token stored anywhere the port can see.</summary>
        None,

        /// <summary>Held by git's configured credential helper — the keyring, normally.</summary>
        CredentialHelper,

        /// <summary>Written to a file readable only by its owner, because no helper kept it.</summary>
        PlainFile,

        /// <summary>Supplied by the environment; read-only as far as the port is concerned.</summary>
        Environment,
    }

    /// <summary>
    ///  Environment variables consulted when nothing is stored, in order. These are the
    ///  names the GitHub CLI and Actions use, so a token already exported for scripting
    ///  works here without being typed a second time.
    /// </summary>
    private static readonly string[] EnvironmentVariables = ["GITEXT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    /// <summary>Reads the token for <paramref name="apiHost"/>, or null when there is none.</summary>
    public static string? Load(string apiHost) => Read(apiHost).Token;

    /// <summary>Reads the token and says where it came from.</summary>
    public static (string? Token, Storage From) Read(string apiHost)
    {
        string? fromHelper = FillFromHelper(apiHost);
        if (fromHelper is { Length: > 0 })
        {
            return (fromHelper, Storage.CredentialHelper);
        }

        string path = FilePath(apiHost);
        if (File.Exists(path))
        {
            try
            {
                string text = File.ReadAllText(path).Trim();
                if (text.Length > 0)
                {
                    return (text, Storage.PlainFile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable file is the same as no file: fall through.
            }
        }

        foreach (string name in EnvironmentVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                return (value.Trim(), Storage.Environment);
            }
        }

        return (null, Storage.None);
    }

    /// <summary>
    ///  Stores <paramref name="token"/> and returns where it actually landed. An empty
    ///  token erases instead, which is how the settings page offers "forget it".
    /// </summary>
    public static Storage Save(string apiHost, string token)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            Erase(apiHost);
            return Storage.None;
        }

        RunCredential("approve", apiHost, token);

        // Read it back rather than trusting the exit code: `git credential approve`
        // succeeds whether or not anything is listening.
        if (FillFromHelper(apiHost) == token)
        {
            DeleteFile(apiHost);
            return Storage.CredentialHelper;
        }

        return WriteFile(apiHost, token) ? Storage.PlainFile : Storage.None;
    }

    /// <summary>Forgets the token, in both places it could be.</summary>
    public static void Erase(string apiHost)
    {
        string? existing = FillFromHelper(apiHost);
        if (existing is { Length: > 0 })
        {
            RunCredential("reject", apiHost, existing);
        }

        DeleteFile(apiHost);
    }

    /// <summary>One sentence for the settings page describing where the token is kept.</summary>
    public static string Describe(string apiHost, Storage storage) => storage switch
    {
        Storage.CredentialHelper => TranslationService.TFormat(
            null, "Stored in git's credential helper, under {0}.", apiHost),
        Storage.PlainFile => TranslationService.TFormat(
            null,
            "No git credential helper kept it, so it is in {0} — a plain file, readable only by you.",
            FilePath(apiHost)),
        Storage.Environment => TranslationService.T(
            "Taken from the environment (GITEXT_GITHUB_TOKEN, GH_TOKEN or GITHUB_TOKEN). Type one here to store it instead."),
        _ => TranslationService.T("No token yet. It will go to git's credential helper if one is configured."),
    };

    private static string FilePath(string apiHost)
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        // The host is part of the name so an Enterprise install and github.com can each
        // have their own token without one overwriting the other.
        string safeHost = string.Concat(apiHost.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
        return Path.Combine(baseDir, "GitExtensions.Avalonia", $"github-token-{safeHost}");
    }

    private static bool WriteFile(string apiHost, string token)
    {
        string path = FilePath(apiHost);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Created empty and restricted BEFORE the secret goes in: writing first and
            // chmod-ing after leaves a window in which the token is world-readable.
            using (File.Create(path))
            {
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(path, token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void DeleteFile(string apiHost)
    {
        try
        {
            File.Delete(FilePath(apiHost));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a token we cannot delete is still one the user can remove.
        }
    }

    /// <summary>The password git's helper has for the host, or null.</summary>
    private static string? FillFromHelper(string apiHost)
    {
        string output = RunCredential("fill", apiHost, password: null);
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith("password=", StringComparison.Ordinal))
            {
                return line["password=".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    ///  Runs one <c>git credential &lt;verb&gt;</c> and returns its stdout.
    ///
    ///  <para><b>This must never be able to ask the user anything.</b> A <c>fill</c> here
    ///  is a LOOKUP — "is a token stored?" — and "no" is a perfectly good answer that the
    ///  callers all handle. Three separate switches are needed to hold that line, because
    ///  git can prompt in three different ways:</para>
    ///
    ///  <list type="bullet">
    ///   <item><description><c>GIT_TERMINAL_PROMPT=0</c> — git's own terminal prompt, which
    ///    a GUI app has no terminal to answer.</description></item>
    ///   <item><description>an empty <c>GIT_ASKPASS</c>/<c>SSH_ASKPASS</c> — the external
    ///    prompt program.</description></item>
    ///   <item><description><c>credential.interactive=false</c> — the CREDENTIAL HELPER's
    ///    own UI, which the two above do not touch at all.</description></item>
    ///  </list>
    ///
    ///  <para>That third one was missing, and it is the one that matters on Windows. The
    ///  helper there is Git Credential Manager, which ACQUIRES credentials rather than
    ///  merely looking them up: asked for a host it has nothing for, it opened its
    ///  "Connect to GitHub" sign-in window and waited. Since this ran synchronously on
    ///  the UI thread, the app stopped responding and Windows killed it — logged as
    ///  "Application Hang", reported as a crash. It happened on merely OPENING settings,
    ///  in a repository whose remote was Bitbucket over SSH and which had no business
    ///  with GitHub at all. Linux never showed it: git-credential-libsecret answers
    ///  "not found" and says nothing.</para>
    /// </summary>
    private static string RunCredential(string verb, string apiHost, string? password)
    {
        // How long a helper may take before it is presumed wedged. Generous: on Windows
        // the helper is a .NET program and costs ~100 ms just to start.
        const int TimeoutMs = 5000;

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = "-c credential.interactive=false credential " + verb,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_ASKPASS"] = string.Empty;
            psi.Environment["SSH_ASKPASS"] = string.Empty;

            // The pre-config way of saying credential.interactive=false, for a helper
            // older than that setting. Ignored by versions that read the config.
            psi.Environment["GCM_INTERACTIVE"] = "never";
            GitEnvironment.ApplyDiagnosticLocale(psi.Environment);

            using Process process = new() { StartInfo = psi };

            // Both pipes are drained by events, started before anything is written. The
            // previous shape — write stdin, then ReadToEnd — made the timeout below
            // unreachable, because ReadToEnd returns only when the child closes stdout:
            // a helper sitting on its own dialog blocked here forever and the 5 s guard
            // never ran. Draining stderr matters for the mirror-image reason: a helper
            // that fills a pipe nobody reads blocks on the write and never exits.
            StringBuilder stdout = new();
            process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
            process.ErrorDataReceived += (_, e) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.StandardInput.NewLine = "\n";
            process.StandardInput.WriteLine("protocol=https");
            process.StandardInput.WriteLine("host=" + apiHost);
            if (password is not null)
            {
                // A username is required for the entry to be addressable again; "token"
                // is what GitHub itself calls the PAT when it is used as a password.
                process.StandardInput.WriteLine("username=token");
                process.StandardInput.WriteLine("password=" + password);
            }

            process.StandardInput.WriteLine();
            process.StandardInput.Close();

            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill(entireProcessTree: true);
                return string.Empty;
            }

            // The overload above returns as soon as the process is gone; this one also
            // waits for the redirected readers to finish, so stdout is complete below.
            process.WaitForExit();

            return process.ExitCode == 0 ? stdout.ToString() : string.Empty;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No git on the PATH, or it could not be started: the caller treats that as
            // "nothing stored", which is exactly right.
            return string.Empty;
        }
    }

    // The callback fires per line, with a null to mark the end of the stream.
    private static void Append(StringBuilder target, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (target)
        {
            target.AppendLine(line);
        }
    }
}

/// <summary>
///  The repository-host integration, the port of upstream's <c>GitHub3Plugin</c> and of
///  the <c>IRepositoryHostPlugin</c> surface it implements.
///
///  <para>Upstream ships this as an MEF plugin because its plugin model can host
///  several repository hosts at once. The port has exactly one, so it is a service:
///  the indirection would buy an extension point nobody can currently extend, at the
///  cost of an interface to keep honest.</para>
///
///  <para>What it does <b>not</b> do is decide anything about the UI. It answers three
///  questions — which of this repository's remotes are on the host, what the API says,
///  and what the web URL of a thing is — and the three windows do the rest.</para>
/// </summary>
public sealed class GitHubService
{
    /// <summary>The remote name upstream adds a fork's parent under (<c>UpstreamConventionName</c>).</summary>
    public const string UpstreamRemoteName = "upstream";

    private static readonly GitHostingRemoteParser Parser = new();

    private readonly AppPreferences _prefs;

    public GitHubService()
        : this(new SettingsService().Load())
    {
    }

    public GitHubService(AppPreferences prefs) => _prefs = prefs;

    /// <summary>The configured host, <c>github.com</c> unless an Enterprise name was set.</summary>
    public string Host => _prefs.GitHubHost is { Length: > 0 } host ? host.Trim() : "github.com";

    /// <summary>
    ///  The API root. GitHub.com puts it on its own subdomain; every Enterprise install
    ///  serves it from <c>/api/v3</c> on the same host.
    /// </summary>
    public string ApiEndpoint =>
        IsDotCom ? "https://api.github.com" : $"https://{Host}/api/v3";

    /// <summary>The host the credential helper stores this install's token under.</summary>
    public string ApiHost => IsDotCom ? "api.github.com" : Host;

    /// <summary>The web root, for the links this service builds.</summary>
    public string WebEndpoint => $"https://{Host}";

    private bool IsDotCom => string.Equals(Host, "github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a token is available at all; the windows refuse to open without one.</summary>
    public bool IsConfigured => GitHubTokenStore.Load(ApiHost) is { Length: > 0 };

    /// <summary>An API client carrying the stored token (or anonymous, if there is none).</summary>
    public GitHubClient CreateClient() => new(ApiEndpoint, GitHubTokenStore.Load(ApiHost));

    /// <summary>Where GitHub's "create a token" form lives, pre-filled with the scopes needed.</summary>
    public string NewTokenUrl =>
        $"{WebEndpoint}/settings/tokens/new?description=Token%20for%20Git%20Extensions&scopes=repo";

    /// <summary>Where existing tokens are managed.</summary>
    public string ManageTokensUrl => $"{WebEndpoint}/settings/tokens";

    /// <summary>
    ///  The remotes of <paramref name="repoPath"/> that live on this host, in
    ///  configuration order and without duplicates.
    ///
    ///  <para>Read straight from <c>git remote -v</c> rather than through
    ///  <see cref="RemoteService"/>: this runs while a window is opening, and the
    ///  question — "which remotes are GitHub?" — needs no more than the URLs.</para>
    /// </summary>
    public IReadOnlyList<GitHubHostedRemote> GetHostedRemotes(string repoPath)
    {
        List<GitHubHostedRemote> remotes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach ((string name, string url) in ReadRemotes(repoPath))
        {
            if (!Parser.TryExtractGitHostingDataFromRemoteUrl(url, out string? hosting, out string? owner, out string? repository)
                || !string.Equals(hosting, Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add($"{name} {owner}/{repository}"))
            {
                remotes.Add(new GitHubHostedRemote(name, owner, repository, url));
            }
        }

        return remotes;
    }

    /// <summary>Whether this repository has anything to do with the host at all.</summary>
    public bool IsRelevantTo(string? repoPath)
        => repoPath is { Length: > 0 } && GetHostedRemotes(repoPath).Count > 0;

    /// <summary>The web page of one commit.</summary>
    public string CommitUrl(GitHubHostedRemote remote, string commitHash)
        => $"{WebEndpoint}/{remote.Data}/commit/{commitHash}";

    /// <summary>The blame page of one line of one file at one commit — upstream's <c>GetBlameUrl</c>.</summary>
    public string BlameUrl(GitHubHostedRemote remote, string commitHash, string fileName, int oneBasedLine)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{WebEndpoint}/{remote.Data}/blame/{commitHash}/{fileName}#L{oneBasedLine}");

    /// <summary>The file's page at one commit.</summary>
    public string FileUrl(GitHubHostedRemote remote, string commitHash, string fileName)
        => $"{WebEndpoint}/{remote.Data}/blob/{commitHash}/{fileName}";

    /// <summary>
    ///  Adds the parent of a forked <c>origin</c> as a remote, and returns the name it
    ///  was added under — upstream's <c>AddUpstreamRemoteAsync</c>. Returns null when
    ///  there is nothing to do: no GitHub remote owned by me, not a fork, or a remote
    ///  for the parent already configured.
    /// </summary>
    public async Task<string?> AddUpstreamRemoteAsync(string repoPath, CancellationToken cancellationToken)
    {
        string? login = await GetLoginAsync(cancellationToken).ConfigureAwait(false);
        if (login is null)
        {
            return null;
        }

        GitHubHostedRemote? mine = GetHostedRemotes(repoPath)
            .FirstOrDefault(r => string.Equals(r.Owner, login, StringComparison.OrdinalIgnoreCase));
        if (mine is null)
        {
            return null;
        }

        GitHubRepository repository = await CreateClient()
            .GetRepositoryAsync(mine.Owner, mine.Repository, cancellationToken).ConfigureAwait(false);
        if (!repository.Fork || repository.Parent is null)
        {
            return null;
        }

        string parentUrl = (mine.UsesSsh ? repository.Parent.SshUrl : repository.Parent.CloneUrl) ?? string.Empty;
        if (parentUrl.Length == 0)
        {
            return null;
        }

        IReadOnlyList<(string Name, string Url)> existing = ReadRemotes(repoPath);
        if (existing.Any(r => r.Name == UpstreamRemoteName
            || string.Equals(r.Url, parentUrl, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        RemoteOpResult result = new RemoteService().AddRemote(repoPath, UpstreamRemoteName, parentUrl);
        return result.Success ? UpstreamRemoteName : null;
    }

    /// <summary>
    ///  The login of the token's owner, or null when there is no token or GitHub will
    ///  not say. Cached for the life of the process against the token it was fetched
    ///  with, because "is this remote mine?" is asked once per remote per window.
    /// </summary>
    public async Task<string?> GetLoginAsync(CancellationToken cancellationToken)
    {
        string? token = GitHubTokenStore.Load(ApiHost);
        if (token is null)
        {
            return null;
        }

        lock (LoginLock)
        {
            if (_cachedTokenHash == token.GetHashCode(StringComparison.Ordinal) && _cachedLogin is not null)
            {
                return _cachedLogin;
            }
        }

        string login;
        try
        {
            login = (await CreateClient().GetCurrentUserAsync(cancellationToken).ConfigureAwait(false)).Login;
        }
        catch (GitHubApiException)
        {
            // A bad token is not this method's problem to report: the caller that
            // actually needs data will fail with a message the user can act on.
            return null;
        }

        lock (LoginLock)
        {
            _cachedLogin = login;
            _cachedTokenHash = token.GetHashCode(StringComparison.Ordinal);
        }

        return login;
    }

    /// <summary>Drops the cached login, so a token changed in Settings takes effect at once.</summary>
    public static void ForgetLogin()
    {
        lock (LoginLock)
        {
            _cachedLogin = null;
            _cachedTokenHash = 0;
        }
    }

    private static readonly Lock LoginLock = new();
    private static string? _cachedLogin;
    private static int _cachedTokenHash;

    /// <summary>
    ///  <c>git remote -v</c>, reduced to the fetch URL of each remote. Parsed here
    ///  rather than reused from <see cref="RemoteService"/> so that opening a window
    ///  costs one git invocation and no repository object.
    /// </summary>
    private static IReadOnlyList<(string Name, string Url)> ReadRemotes(string repoPath)
    {
        List<(string, string)> remotes = [];
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = "remote -v",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            GitEnvironment.ApplyDiagnosticLocale(psi.Environment);

            using Process process = new() { StartInfo = psi };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            foreach (string line in output.Split('\n'))
            {
                // "name\turl (fetch)" — the push line names the same remote and would
                // only add a duplicate.
                if (!line.EndsWith("(fetch)", StringComparison.Ordinal))
                {
                    continue;
                }

                int tab = line.IndexOf('\t', StringComparison.Ordinal);
                int space = line.LastIndexOf(' ');
                if (tab > 0 && space > tab)
                {
                    remotes.Add((line[..tab].Trim(), line[(tab + 1)..space].Trim()));
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No git, or not a repository: no hosted remotes, which is a valid answer.
        }

        return remotes;
    }
}
