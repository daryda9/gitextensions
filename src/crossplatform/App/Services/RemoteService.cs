using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A configured git remote, projected for display in the Avalonia remote panel.
/// </summary>
public sealed record RemoteRow(string Name, string FetchUrl, string PushUrl)
{
    public string Display => string.IsNullOrEmpty(FetchUrl) ? Name : $"{Name}  ({FetchUrl})";

    public override string ToString() => Display;
}

/// <summary>
///  Username / password pair collected from the credentials dialog and used to
///  retry a single remote operation that failed authentication.
/// </summary>
public sealed record GitCredentials(string Username, string Password);

/// <summary>
///  Outcome of a remote operation (fetch / pull / push). <see cref="AuthFailed"/>
///  is set when the git output looks like an authentication failure, signalling
///  the UI to prompt for credentials and retry once.
/// </summary>
public sealed record RemoteOpResult(bool Success, string Output, bool AuthFailed);

/// <summary>
///  Remote operations (list remotes, fetch, pull, push) implemented by reusing the
///  Git Extensions core (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>.
///  The fetch/pull command builders are methods on the module; push uses the core
///  <see cref="Commands.Push"/> argument builder. All methods are synchronous and
///  are meant to be called off the UI thread.
///
///  Credentials: on Linux git normally resolves credentials through a configured
///  credential helper, so most operations succeed without a prompt. When an
///  operation fails authentication the caller can retry passing
///  <see cref="GitCredentials"/>; for http/https remotes the credentials are fed to
///  git through a transient, in-memory credential helper installed with per-command
///  <c>-c credential.helper=…</c> config (nothing is written to git config, and the
///  secret is never placed in the remote URL). The helper reads the username /
///  password from environment variables that are set only for the duration of the
///  single command and cleared immediately afterwards, so the secret never appears
///  in the (logged) git command line, the reflog, or the visible process arguments.
///  ssh remotes stay key-based and are left untouched — see the panel's notes.
/// </summary>
public sealed class RemoteService
{
    /// <summary>
    ///  Lists the configured remotes (name + fetch/push URLs).
    /// </summary>
    public IReadOnlyList<RemoteRow> ListRemotes(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        IReadOnlyList<Remote> remotes = module.GetRemotesAsync().GetAwaiter().GetResult();
        return [.. remotes.Select(r => new RemoteRow(
            r.Name,
            r.FetchUrl ?? string.Empty,
            r.PushUrls.Count > 0 ? r.PushUrls[0] : r.FetchUrl ?? string.Empty))];
    }

    /// <summary>
    ///  Returns the currently checked-out branch name (empty when detached).
    /// </summary>
    public string GetCurrentBranch(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        return module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
    }

    /// <summary>
    ///  Fetches from <paramref name="remote"/> (all its refs).
    /// </summary>
    public RemoteOpResult Fetch(string repoPath, string remote, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = module.FetchCmd(remote, remoteBranch: null, localBranch: null);
        return Run(module, remote, args, credentials, forPush: false);
    }

    /// <summary>
    ///  Pulls from <paramref name="remote"/>, optionally rebasing local commits.
    /// </summary>
    public RemoteOpResult Pull(string repoPath, string remote, bool rebase, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = module.PullCmd(remote, remoteBranch: null, rebase: rebase);
        return Run(module, remote, args, credentials, forPush: false);
    }

    /// <summary>
    ///  Pushes <paramref name="branch"/> to <paramref name="remote"/>. When
    ///  <paramref name="force"/> is set, a <em>safe</em> force push
    ///  (<c>--force-with-lease</c>) is used: the push is rejected if the remote ref
    ///  advanced since we last fetched it, so it cannot silently clobber someone
    ///  else's work the way a plain <c>--force</c> would.
    /// </summary>
    public RemoteOpResult Push(string repoPath, string remote, string branch, bool force, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        if (string.IsNullOrEmpty(branch))
        {
            branch = module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(branch))
        {
            return new RemoteOpResult(false, "No branch to push (detached HEAD?).", AuthFailed: false);
        }

        ArgumentString args = Commands.Push(
            remote: remote,
            fromBranch: branch,
            toBranch: branch,
            force: force ? ForcePushOptions.ForceWithLease : ForcePushOptions.DoNotForce,
            track: true,
            recursiveSubmodules: 0);

        return Run(module, remote, args, credentials, forPush: true);
    }

    // Runs a remote command. Without credentials (or for non-http/https remotes)
    // the command runs as-is and git resolves auth through its own configured
    // helpers. With credentials on an http/https remote the command is wrapped with
    // a transient, in-memory credential helper (see <see cref="RunWithCredentials"/>).
    private RemoteOpResult Run(GitModule module, string remote, ArgumentString args, GitCredentials? credentials, bool forPush)
    {
        if (credentials is not null && IsHttpRemote(module, remote, forPush))
        {
            return RunWithCredentials(module, args, credentials);
        }

        return Run(module, args);
    }

    private static RemoteOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        string output = result.AllOutput;
        return new RemoteOpResult(result.ExitedSuccessfully, output, LooksLikeAuthFailure(output));
    }

    // Environment variable names the transient credential helper reads. Only these
    // *names* ever appear in the (logged) git command line; the secret values live
    // in the process environment for the duration of the single command and are
    // cleared immediately afterwards.
    private const string UserEnvVar = "GE_AVALONIA_CRED_USER";
    private const string PassEnvVar = "GE_AVALONIA_CRED_PASS";

    // Feeds <paramref name="credentials"/> to a single git command through git's own
    // credential mechanism, without ever putting the secret in the remote URL, the
    // command line, or git config.
    //
    // Mechanism: two per-command config entries are prepended to the git invocation:
    //   -c credential.helper=            → clears any inherited/prompting helper
    //   -c credential.helper=!f() {...}  → a one-shot inline helper that echoes the
    //                                      credentials git asks for
    // The inline helper references environment variables ($GE_AVALONIA_CRED_*) rather
    // than embedding the username/password literally, so the secret is NOT part of the
    // argument string that <see cref="Executable"/> logs / stores on the process. The
    // env vars are set just before the command and removed in a finally block.
    private static RemoteOpResult RunWithCredentials(GitModule module, ArgumentString args, GitCredentials credentials)
    {
        // The inline helper only responds to the "get" operation; it echoes the
        // username/password git expects on stdout. No double quotes are used inside
        // the helper body so the whole value can be wrapped in a single double-quoted
        // token when passed as a process argument.
        string helper = $"!f() {{ test $1 = get && echo username=${UserEnvVar} && echo password=${PassEnvVar}; }}; f";
        ArgumentString wrapped = $"-c credential.helper= -c \"credential.helper={helper}\" {args.Arguments}";

        string? previousUser = Environment.GetEnvironmentVariable(UserEnvVar);
        string? previousPass = Environment.GetEnvironmentVariable(PassEnvVar);
        try
        {
            // Child git processes are started with UseShellExecute=false and inherit
            // this process's environment, so setting the vars here makes them visible
            // to the one git command (and its credential helper subshell) only.
            Environment.SetEnvironmentVariable(UserEnvVar, credentials.Username);
            Environment.SetEnvironmentVariable(PassEnvVar, credentials.Password);
            return Run(module, wrapped);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserEnvVar, previousUser);
            Environment.SetEnvironmentVariable(PassEnvVar, previousPass);
        }
    }

    // Detects the common authentication-failure phrases git emits so the UI can
    // prompt for credentials and retry once.
    private static bool LooksLikeAuthFailure(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        string[] markers =
        [
            "Authentication failed",
            "could not read Username",
            "could not read Password",
            "Invalid username or password",
            "remote: Unauthorized",
            "fatal: Authentication",
            "terminal prompts disabled",
        ];

        foreach (string marker in markers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // True when the given remote's URL is http/https, i.e. a remote for which the
    // username/password credential helper is meaningful. ssh/git remotes are
    // key-based and return false (the retry then behaves like the first attempt).
    private static bool IsHttpRemote(GitModule module, string remote, bool forPush)
    {
        RemoteRow? row = ListRemotesFrom(module).FirstOrDefault(r => r.Name == remote);
        string? url = forPush ? row?.PushUrl : row?.FetchUrl;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https";
    }

    private static IReadOnlyList<RemoteRow> ListRemotesFrom(GitModule module)
    {
        IReadOnlyList<Remote> remotes = module.GetRemotesAsync().GetAwaiter().GetResult();
        return [.. remotes.Select(r => new RemoteRow(
            r.Name,
            r.FetchUrl ?? string.Empty,
            r.PushUrls.Count > 0 ? r.PushUrls[0] : r.FetchUrl ?? string.Empty))];
    }
}
