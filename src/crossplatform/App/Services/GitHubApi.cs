using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A GitHub REST call that came back with something other than success, carrying the
///  status code so a caller can tell "no such user" (404) from "your token is not
///  allowed to do that" (403) without matching on English message text.
/// </summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException()
    {
    }

    public GitHubApiException(string message)
        : base(message)
    {
    }

    public GitHubApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitHubApiException(string message, HttpStatusCode status)
        : base(message)
    {
        Status = status;
    }

    /// <summary>The HTTP status, or 0 when the request never reached the server.</summary>
    public HttpStatusCode Status { get; }
}

#pragma warning disable SA1402 // one type per file: these are the wire shapes of one API

/// <summary>A GitHub account, of which the port only ever needs the login.</summary>
public sealed class GitHubUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;
}

/// <summary>
///  A repository as GitHub returns it. Only the fields the three windows display or
///  act on are declared; System.Text.Json ignores the rest of the (large) payload.
/// </summary>
public sealed class GitHubRepository
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public GitHubUser? Owner { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("fork")]
    public bool Fork { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("forks_count")]
    public int ForksCount { get; set; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("clone_url")]
    public string? CloneUrl { get; set; }

    [JsonPropertyName("ssh_url")]
    public string? SshUrl { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    /// <summary>
    ///  The repository this one was forked from. Present only on a DETAILED repository
    ///  — the objects inside a search result or a listing carry <c>fork: true</c> and
    ///  nothing else — which is why <see cref="GitHubService.GetRepositoryAsync"/>
    ///  exists and why the fork/clone window re-reads a repository before offering to
    ///  add its parent as a remote.
    /// </summary>
    [JsonPropertyName("parent")]
    public GitHubRepository? Parent { get; set; }

    /// <summary>The owner's login, or the first half of <c>full_name</c> if the owner
    /// object was omitted. Never null, so the callers can sort and compare on it.</summary>
    public string OwnerLogin =>
        Owner?.Login is { Length: > 0 } login
            ? login
            : FullName.IndexOf('/') is int slash && slash > 0
                ? FullName[..slash]
                : string.Empty;

    public override string ToString() => FullName.Length > 0 ? FullName : Name;
}

/// <summary>The head of a branch: its name and the commit it points at.</summary>
public sealed class GitHubBranch
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("commit")]
    public GitHubCommitRef? Commit { get; set; }
}

/// <summary>A bare "this is the commit" object, used inside branches and PR ends.</summary>
public sealed class GitHubCommitRef
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;
}

/// <summary>One end of a pull request: which ref, at which sha, in which repository.</summary>
public sealed class GitHubPullRequestEnd
{
    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    /// <summary>
    ///  Null when the fork the request came from has since been deleted. GitHub keeps
    ///  the pull request; the port then has nothing to fetch from, which is why every
    ///  use of this is guarded rather than asserted.
    /// </summary>
    [JsonPropertyName("repo")]
    public GitHubRepository? Repo { get; set; }
}

/// <summary>A pull request, as listed and as created.</summary>
public sealed class GitHubPullRequest
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("user")]
    public GitHubUser? User { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("base")]
    public GitHubPullRequestEnd? Base { get; set; }

    [JsonPropertyName("head")]
    public GitHubPullRequestEnd? Head { get; set; }

    /// <summary>The login of whoever opened it, empty when GitHub omitted the user.</summary>
    public string OwnerLogin => User?.Login ?? string.Empty;

    /// <summary>
    ///  The local branch name upstream fetches a pull request into
    ///  (<c>GitHubPullRequest.FetchBranch</c>): <c>pr/n&lt;number&gt;_&lt;head ref&gt;</c>.
    ///  Kept identical so a repository used from both builds does not end up with two
    ///  branches for one request.
    /// </summary>
    public string FetchBranch =>
        string.Create(CultureInfo.InvariantCulture, $"pr/n{Number}_{Head?.Ref ?? string.Empty}");
}

/// <summary>A comment on the pull request's conversation (an issue comment).</summary>
public sealed class GitHubComment
{
    [JsonPropertyName("user")]
    public GitHubUser? User { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }
}

/// <summary>A commit listed inside a pull request, for the conversation timeline.</summary>
public sealed class GitHubPullRequestCommit
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("commit")]
    public GitHubCommitDetail? Commit { get; set; }
}

/// <summary>The authored part of a commit object.</summary>
public sealed class GitHubCommitDetail
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public GitHubCommitAuthor? Author { get; set; }
}

/// <summary>
///  An issue assigned to the token's owner, for the commit dialog's message helper.
/// </summary>
public sealed class GitHubIssue
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("repository")]
    public GitHubRepository? Repository { get; set; }

    /// <summary>
    ///  Set when this "issue" is really a pull request. The <c>/issues</c> endpoints
    ///  return both, and a pull request has no place in a "fixes #n" suggestion — so
    ///  this is the field that tells them apart.
    /// </summary>
    [JsonPropertyName("pull_request")]
    public object? PullRequest { get; set; }
}

/// <summary>Name and date of a commit's author, as recorded in the object.</summary>
public sealed class GitHubCommitAuthor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }
}

#pragma warning restore SA1402

/// <summary>
///  The GitHub REST v3 calls the port makes, and nothing else.
///
///  <para>Upstream reaches GitHub through the <c>Git.hub</c> library, a synchronous
///  RestSharp wrapper that is not restorable in this tree; this is a direct
///  <see cref="HttpClient"/> against the documented endpoints instead. The shape is
///  deliberately narrower than the library's: every method here is called by one of
///  the three windows, so there is no unreachable surface to keep working.</para>
///
///  <para><b>Everything is asynchronous and cancellable.</b> Upstream's calls block
///  a worker thread and its dialogs mask themselves while they wait; a network round
///  trip that cannot be cancelled is how a dialog ends up unclosable on a flaky
///  connection. Each window passes a token it cancels when it closes.</para>
/// </summary>
public sealed class GitHubClient
{
    /// <summary>
    ///  The media type that asks for the v3 JSON payload, plus the version pin GitHub
    ///  asks integrations to send. Without the pin the API is free to move under us at
    ///  its own schedule.
    /// </summary>
    private const string JsonMediaType = "application/vnd.github+json";

    private const string ApiVersion = "2022-11-28";

    /// <summary>
    ///  One client for the process. <see cref="HttpClient"/> is designed to be shared
    ///  and pools its connections; a per-call instance leaks sockets in TIME_WAIT,
    ///  which is the classic way to make an app stop reaching a host after a while.
    ///  No default headers are set on it — the token differs per instance of this
    ///  class, so authorization goes on the REQUEST.
    /// </summary>
    private static readonly HttpClient Shared = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _endpoint;
    private readonly string? _token;

    /// <param name="apiEndpoint">
    ///  The API root, e.g. <c>https://api.github.com</c>, or
    ///  <c>https://ghe.example.com/api/v3</c> for an Enterprise install.
    /// </param>
    /// <param name="token">The personal access token, or null for anonymous calls.</param>
    public GitHubClient(string apiEndpoint, string? token)
    {
        _endpoint = apiEndpoint.TrimEnd('/');
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    /// <summary>Whether a token was supplied. Anonymous calls work but are rate-limited
    /// to 60/hour and cannot see private repositories.</summary>
    public bool HasToken => _token is not null;

    public Task<GitHubUser> GetCurrentUserAsync(CancellationToken cancellationToken)
        => GetAsync<GitHubUser>("/user", cancellationToken);

    /// <summary>
    ///  The repositories the token's owner can push to, newest activity first.
    ///  <c>affiliation</c> is spelled out rather than left to the default so that
    ///  repositories owned by an organisation the user belongs to are included — the
    ///  default already does, but it is the kind of default that has changed before.
    /// </summary>
    public Task<IReadOnlyList<GitHubRepository>> GetMyRepositoriesAsync(CancellationToken cancellationToken)
        => GetPagedAsync<GitHubRepository>(
            "/user/repos?affiliation=owner,collaborator,organization_member&sort=pushed",
            cancellationToken);

    public Task<IReadOnlyList<GitHubRepository>> GetUserRepositoriesAsync(string user, CancellationToken cancellationToken)
        => GetPagedAsync<GitHubRepository>($"/users/{Escape(user)}/repos?sort=pushed", cancellationToken);

    /// <summary>
    ///  Repository search. One page only: the endpoint is rate-limited far harder than
    ///  the rest of the API (30 requests/minute even authenticated), and nobody scrolls
    ///  to the hundredth match of a search box.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepository>> SearchRepositoriesAsync(string query, CancellationToken cancellationToken)
    {
        GitHubSearchResult<GitHubRepository> result = await GetAsync<GitHubSearchResult<GitHubRepository>>(
            $"/search/repositories?q={Escape(query)}&per_page=100", cancellationToken).ConfigureAwait(false);
        return result.Items ?? [];
    }

    public Task<GitHubRepository> GetRepositoryAsync(string owner, string name, CancellationToken cancellationToken)
        => GetAsync<GitHubRepository>($"/repos/{Escape(owner)}/{Escape(name)}", cancellationToken);

    /// <summary>
    ///  Forks a repository into the token owner's account. GitHub answers 202 with the
    ///  new repository object and creates it in the background, so the caller may have
    ///  to wait a moment before it can be cloned.
    /// </summary>
    public Task<GitHubRepository> ForkRepositoryAsync(string owner, string name, CancellationToken cancellationToken)
        => SendJsonAsync<GitHubRepository>(
            HttpMethod.Post, $"/repos/{Escape(owner)}/{Escape(name)}/forks", body: null, cancellationToken);

    public Task<IReadOnlyList<GitHubBranch>> GetBranchesAsync(string owner, string name, CancellationToken cancellationToken)
        => GetPagedAsync<GitHubBranch>($"/repos/{Escape(owner)}/{Escape(name)}/branches", cancellationToken);

    public Task<IReadOnlyList<GitHubPullRequest>> GetPullRequestsAsync(string owner, string name, CancellationToken cancellationToken)
        => GetPagedAsync<GitHubPullRequest>(
            $"/repos/{Escape(owner)}/{Escape(name)}/pulls?state=open&sort=created&direction=desc",
            cancellationToken);

    /// <param name="head">
    ///  <c>owner:branch</c> when the source is a fork, a bare branch name when it is the
    ///  same repository.
    /// </param>
    public Task<GitHubPullRequest> CreatePullRequestAsync(
        string owner, string name, string head, string baseBranch, string title, string body,
        CancellationToken cancellationToken)
        => SendJsonAsync<GitHubPullRequest>(
            HttpMethod.Post,
            $"/repos/{Escape(owner)}/{Escape(name)}/pulls",
            new Dictionary<string, object?>
            {
                ["title"] = title,
                ["head"] = head,
                ["base"] = baseBranch,
                ["body"] = body,
            },
            cancellationToken);

    public Task<GitHubPullRequest> ClosePullRequestAsync(string owner, string name, int number, CancellationToken cancellationToken)
        => SendJsonAsync<GitHubPullRequest>(
            HttpMethod.Patch,
            string.Create(CultureInfo.InvariantCulture, $"/repos/{Escape(owner)}/{Escape(name)}/pulls/{number}"),
            new Dictionary<string, object?> { ["state"] = "closed" },
            cancellationToken);

    /// <summary>
    ///  The unified diff of a pull request, asked for through the <c>.diff</c> media
    ///  type rather than by downloading <c>diff_url</c>. That URL is unauthenticated
    ///  and cannot see a private repository; this one carries the token.
    /// </summary>
    public Task<string> GetPullRequestDiffAsync(string owner, string name, int number, CancellationToken cancellationToken)
        => GetRawAsync(
            string.Create(CultureInfo.InvariantCulture, $"/repos/{Escape(owner)}/{Escape(name)}/pulls/{number}"),
            "application/vnd.github.diff",
            cancellationToken);

    /// <summary>
    ///  The conversation comments. A pull request IS an issue as far as this endpoint
    ///  is concerned, which is why the path says "issues" — the <c>pulls/…/comments</c>
    ///  endpoint returns the line-by-line review notes instead, which have nowhere
    ///  useful to go in this window.
    /// </summary>
    public Task<IReadOnlyList<GitHubComment>> GetIssueCommentsAsync(string owner, string name, int number, CancellationToken cancellationToken)
        => GetPagedAsync<GitHubComment>(
            string.Create(CultureInfo.InvariantCulture, $"/repos/{Escape(owner)}/{Escape(name)}/issues/{number}/comments"),
            cancellationToken);

    public Task<GitHubComment> PostIssueCommentAsync(string owner, string name, int number, string body, CancellationToken cancellationToken)
        => SendJsonAsync<GitHubComment>(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"/repos/{Escape(owner)}/{Escape(name)}/issues/{number}/comments"),
            new Dictionary<string, object?> { ["body"] = body },
            cancellationToken);

    /// <summary>
    ///  The open issues assigned to the token's owner, most recently updated first —
    ///  upstream's <c>GetAssignedIssues</c>. Pull requests come back from this endpoint
    ///  too and are filtered out here rather than at the call site.
    /// </summary>
    public async Task<IReadOnlyList<GitHubIssue>> GetAssignedIssuesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<GitHubIssue> issues = await GetPagedAsync<GitHubIssue>(
            "/issues?filter=assigned&state=open&sort=updated&direction=desc", cancellationToken).ConfigureAwait(false);
        return [.. issues.Where(i => i.PullRequest is null && i.Number > 0)];
    }

    public Task<IReadOnlyList<GitHubPullRequestCommit>> GetPullRequestCommitsAsync(string owner, string name, int number, CancellationToken cancellationToken)
        => GetPagedAsync<GitHubPullRequestCommit>(
            string.Create(CultureInfo.InvariantCulture, $"/repos/{Escape(owner)}/{Escape(name)}/pulls/{number}/commits"),
            cancellationToken);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get, path, JsonMediaType, content: null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///  Follows the <c>Link: …; rel="next"</c> chain, which is the only supported way
    ///  to page this API — the total is not reported and the last page is recognised
    ///  precisely by that header being absent.
    /// </summary>
    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(string path, CancellationToken cancellationToken)
    {
        // Capped rather than unbounded: an account with thousands of repositories would
        // otherwise spend a minute of round trips filling a list nobody reads to the end.
        const int MaxPages = 10;

        List<T> all = [];
        string? next = path.Contains('?', StringComparison.Ordinal)
            ? $"{path}&per_page=100"
            : $"{path}?per_page=100";

        for (int page = 0; page < MaxPages && next is not null; page++)
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Get, next, JsonMediaType, content: null, cancellationToken).ConfigureAwait(false);
            all.AddRange(await ReadAsync<List<T>>(response, cancellationToken).ConfigureAwait(false));
            next = NextLink(response);
        }

        return all;
    }

    private async Task<string> GetRawAsync(string path, string mediaType, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get, path, mediaType, content: null, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method, string path, IReadOnlyDictionary<string, object?>? body, CancellationToken cancellationToken)
    {
        using StringContent? content = body is null
            ? null
            : new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await SendAsync(
            method, path, JsonMediaType, content, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string pathOrUrl, string accept, HttpContent? content, CancellationToken cancellationToken)
    {
        string url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : _endpoint + pathOrUrl;

        using HttpRequestMessage request = new(method, url) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);

        // GitHub rejects requests without a user agent outright (403), so this is not
        // decoration: it is a required header.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GitExtensions.Avalonia", "5.0"));

        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        try
        {
            return await Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // Reported as one kind of failure whatever the transport said, so the
            // windows have one catch and one message box rather than three.
            throw new GitHubApiException(
                TranslationService.TFormat(null, "Could not reach {0}: {1}", _endpoint, ex.Message), ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancelled token is the caller closing the window; anything else that
            // cancels a request here is the 30 s client timeout.
            throw new GitHubApiException(
                TranslationService.TFormat(null, "{0} did not answer in time.", _endpoint), ex);
        }
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        T? value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new GitHubApiException(
            TranslationService.T("GitHub returned an empty answer."), response.StatusCode);
    }

    /// <summary>
    ///  Turns a failed response into a message worth reading: GitHub's own
    ///  <c>message</c> field when there is one, and the specific diagnosis for the two
    ///  failures a user actually hits — an exhausted rate limit and a token that is
    ///  missing, expired or short of a scope.
    /// </summary>
    private async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = string.Empty;
        try
        {
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("message", out JsonElement message))
            {
                detail = message.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            // A non-JSON error body (a proxy's HTML page, most often) tells the user
            // nothing the status code does not; fall through to the generic message.
        }

        if (response.StatusCode is HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? remaining)
            && remaining.FirstOrDefault() == "0")
        {
            throw new GitHubApiException(
                TranslationService.TFormat(
                    null,
                    "GitHub's rate limit is exhausted{0}. Anonymous access allows 60 requests an hour; a personal access token raises that to 5000.",
                    ResetHint(response)),
                response.StatusCode);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            throw new GitHubApiException(
                TranslationService.TFormat(
                    null,
                    "GitHub rejected the personal access token ({0}). Check it in Settings ▸ GitHub.",
                    detail.Length > 0 ? detail : response.StatusCode.ToString()),
                response.StatusCode);
        }

        throw new GitHubApiException(
            detail.Length > 0
                ? TranslationService.TFormat(null, "GitHub answered {0}: {1}", (int)response.StatusCode, detail)
                : TranslationService.TFormat(null, "GitHub answered {0}.", (int)response.StatusCode),
            response.StatusCode);
    }

    /// <summary>" (it resets at HH:mm)" when the header is present and parseable, else empty.</summary>
    private static string ResetHint(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-reset", out IEnumerable<string>? values)
            || !long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            return string.Empty;
        }

        DateTime reset = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().DateTime;
        return TranslationService.TFormat(null, " (it resets at {0})", reset.ToString("t", CultureInfo.CurrentCulture));
    }

    /// <summary>The URL of the next page, or null when this was the last one.</summary>
    private static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string header in values)
        {
            foreach (string part in header.Split(','))
            {
                int start = part.IndexOf('<');
                int end = part.IndexOf('>');
                if (start >= 0 && end > start && part.Contains("rel=\"next\"", StringComparison.Ordinal))
                {
                    return part[(start + 1)..end];
                }
            }
        }

        return null;
    }
}

/// <summary>The envelope every <c>/search/…</c> endpoint wraps its hits in.</summary>
/// <typeparam name="T">The kind of thing searched for.</typeparam>
internal sealed class GitHubSearchResult<T>
{
    [JsonPropertyName("items")]
    public List<T>? Items { get; set; }
}
