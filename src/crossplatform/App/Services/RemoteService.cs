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
///  <see cref="GitCredentials"/>; for http/https remotes the credentials are
///  injected into the remote URL for that single command only (nothing is written
///  to git config). ssh remotes cannot be helped this way — see the panel's notes.
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
        string remoteArg = ResolveRemoteArg(module, remote, credentials, forPush: false);
        ArgumentString args = module.FetchCmd(remoteArg, remoteBranch: null, localBranch: null);
        return Run(module, args);
    }

    /// <summary>
    ///  Pulls from <paramref name="remote"/>, optionally rebasing local commits.
    /// </summary>
    public RemoteOpResult Pull(string repoPath, string remote, bool rebase, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string remoteArg = ResolveRemoteArg(module, remote, credentials, forPush: false);
        ArgumentString args = module.PullCmd(remoteArg, remoteBranch: null, rebase: rebase);
        return Run(module, args);
    }

    /// <summary>
    ///  Pushes <paramref name="branch"/> to <paramref name="remote"/>. When
    ///  <paramref name="force"/> is set, a plain force push is used.
    /// </summary>
    public RemoteOpResult Push(string repoPath, string remote, string branch, bool force, GitCredentials? credentials = null)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string remoteArg = ResolveRemoteArg(module, remote, credentials, forPush: true);

        if (string.IsNullOrEmpty(branch))
        {
            branch = module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
        }

        if (string.IsNullOrEmpty(branch))
        {
            return new RemoteOpResult(false, "No branch to push (detached HEAD?).", AuthFailed: false);
        }

        ArgumentString args = Commands.Push(
            remote: remoteArg,
            fromBranch: branch,
            toBranch: branch,
            force: force ? ForcePushOptions.Force : ForcePushOptions.DoNotForce,
            track: true,
            recursiveSubmodules: 0);

        return Run(module, args);
    }

    private static RemoteOpResult Run(GitModule module, ArgumentString args)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        string output = result.AllOutput;
        return new RemoteOpResult(result.ExitedSuccessfully, output, LooksLikeAuthFailure(output));
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

    // Returns the argument to use as the "remote" for a command. Without
    // credentials that is just the remote name. With credentials, and only for an
    // http/https URL, the credentials are injected into the URL so this one command
    // authenticates without touching git config; other schemes fall back to the
    // name (the retry then behaves like the first attempt).
    private string ResolveRemoteArg(GitModule module, string remote, GitCredentials? credentials, bool forPush)
    {
        if (credentials is null)
        {
            return remote;
        }

        RemoteRow? row = ListRemotesFrom(module).FirstOrDefault(r => r.Name == remote);
        string? url = forPush ? row?.PushUrl : row?.FetchUrl;
        if (string.IsNullOrEmpty(url))
        {
            return remote;
        }

        return InjectCredentials(url, credentials) ?? remote;
    }

    private static IReadOnlyList<RemoteRow> ListRemotesFrom(GitModule module)
    {
        IReadOnlyList<Remote> remotes = module.GetRemotesAsync().GetAwaiter().GetResult();
        return [.. remotes.Select(r => new RemoteRow(
            r.Name,
            r.FetchUrl ?? string.Empty,
            r.PushUrls.Count > 0 ? r.PushUrls[0] : r.FetchUrl ?? string.Empty))];
    }

    // Builds an authenticated URL for http/https remotes; returns null for other
    // schemes (e.g. ssh, git) where user:password in the URL is meaningless.
    private static string? InjectCredentials(string url, GitCredentials credentials)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        string user = Uri.EscapeDataString(credentials.Username);
        string pass = Uri.EscapeDataString(credentials.Password);
        string authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{user}:{pass}@{authority}{uri.PathAndQuery}";
    }
}
