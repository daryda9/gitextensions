using System.Diagnostics;
using System.Text;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A local tag, projected for the push dialog's "Push tags" tab.
/// </summary>
public sealed record PushTagRow(string Name, string ObjectId);

/// <summary>
///  A local branch with its tracking state, projected for the push dialog's
///  "Push multiple branches" tab. <see cref="Ahead"/>/<see cref="Behind"/> come
///  from the branch's configured upstream (<c>%(upstream:track)</c>) and are -1
///  when the branch has no upstream (or the upstream is gone).
/// </summary>
public sealed record PushBranchRow(string Local, string Upstream, int Ahead, int Behind)
{
    /// <summary>Human readable ahead/behind cell, e.g. <c>2↑ 1↓</c>.</summary>
    public string Track => Ahead < 0 && Behind < 0
        ? (string.IsNullOrEmpty(Upstream) ? "new" : "gone")
        : Ahead == 0 && Behind == 0
            ? "up to date"
            : $"{Math.Max(Ahead, 0)}↑ {Math.Max(Behind, 0)}↓";
}

/// <summary>
///  Snapshot of everything the push dialog's tag / multi-branch tabs display.
/// </summary>
public sealed record PushRefsListing(
    IReadOnlyList<PushTagRow> Tags,
    IReadOnlyList<PushBranchRow> Branches);

/// <summary>
///  Push operations that go beyond the single-branch case handled by
///  <see cref="RemoteService.PushStreaming"/>: pushing an arbitrary set of
///  refspecs (several branches at once, individual tags, <c>--tags</c>) and
///  pushing to a bare URL instead of a configured remote.
///
///  Everything is pushed with ONE <c>git push</c> invocation, streamed through
///  <see cref="GitStreamRunner"/> exactly like the other remote operations, so
///  the shared process dialog shows the live git output and the caller can reuse
///  the same credential-prompt-and-retry flow (<see cref="RemoteOpResult.AuthFailed"/>).
///
///  All methods are synchronous and MUST be called off the UI thread.
/// </summary>
public sealed class PushRefsService
{
    // Same env-var names / inline helper mechanism as RemoteService: the secret is
    // passed through the environment for the duration of the single command and is
    // never part of the (logged) argument string.
    private const string UserEnvVar = "GE_AVALONIA_CRED_USER";
    private const string PassEnvVar = "GE_AVALONIA_CRED_PASS";

    /// <summary>
    ///  Lists local tags and local branches (with tracking state) in a single pair
    ///  of <c>git for-each-ref</c> calls.
    /// </summary>
    public PushRefsListing Load(string repoPath)
    {
        List<PushTagRow> tags = [];
        foreach (string line in Capture(repoPath, "for-each-ref --sort=-creatordate --format=%(refname:short)%09%(objectname:short) refs/tags"))
        {
            string[] parts = line.Split('\t');
            if (parts.Length >= 1 && parts[0].Length > 0)
            {
                tags.Add(new PushTagRow(parts[0], parts.Length > 1 ? parts[1] : string.Empty));
            }
        }

        List<PushBranchRow> branches = [];
        foreach (string line in Capture(repoPath, "for-each-ref --sort=refname --format=%(refname:short)%09%(upstream:short)%09%(upstream:track) refs/heads"))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 0 || parts[0].Length == 0)
            {
                continue;
            }

            string upstream = parts.Length > 1 ? parts[1] : string.Empty;
            string track = parts.Length > 2 ? parts[2] : string.Empty;

            // No upstream at all → the branch has never been pushed: report it as
            // unknown (-1/-1) so the UI says "new" rather than "up to date", which
            // is what an empty %(upstream:track) would otherwise be parsed as.
            (int ahead, int behind) = upstream.Length == 0 ? (-1, -1) : ParseTrack(track);
            branches.Add(new PushBranchRow(parts[0], upstream, ahead, behind));
        }

        return new PushRefsListing(tags, branches);
    }

    // "[ahead 2, behind 1]" / "[ahead 3]" / "[behind 4]" / "[gone]" / "" (in sync).
    private static (int Ahead, int Behind) ParseTrack(string track)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            return (0, 0);
        }

        if (track.Contains("gone", StringComparison.OrdinalIgnoreCase))
        {
            return (-1, -1);
        }

        return (ReadNumberAfter(track, "ahead "), ReadNumberAfter(track, "behind "));
    }

    private static int ReadNumberAfter(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return 0;
        }

        int i = at + marker.Length;
        int value = 0;
        bool any = false;
        while (i < text.Length && char.IsDigit(text[i]))
        {
            value = (value * 10) + (text[i] - '0');
            any = true;
            i++;
        }

        return any ? value : 0;
    }

    /// <summary>
    ///  Runs a single <c>git push</c> against <paramref name="target"/> (a remote
    ///  name OR a bare URL) with the given <paramref name="refspecs"/>, streaming
    ///  every output line through <paramref name="onOutput"/>.
    /// </summary>
    /// <param name="repoPath">Repository working directory.</param>
    /// <param name="target">Remote name or URL to push to.</param>
    /// <param name="refspecs">Refspecs (e.g. <c>main:main</c>, <c>refs/tags/v1</c>); may be empty when <paramref name="allTags"/> is set.</param>
    /// <param name="force">Use <c>--force-with-lease</c> (safe force).</param>
    /// <param name="allTags">Add <c>--tags</c> (push every local tag).</param>
    /// <param name="setUpstream">Add <c>-u</c> so pushed branches start tracking.</param>
    /// <param name="recurseSubmodules">Add <c>--recurse-submodules=on-demand</c>.</param>
    /// <param name="onOutput">Called per output line; may run on a background thread.</param>
    /// <param name="credentials">Optional credentials for an http/https target.</param>
    public RemoteOpResult PushRefsStreaming(
        string repoPath,
        string target,
        IReadOnlyList<string> refspecs,
        bool force,
        bool allTags,
        bool setUpstream,
        bool recurseSubmodules,
        Action<string> onOutput,
        GitCredentials? credentials = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return Refuse("No push target selected.", onOutput);
        }

        if (refspecs.Count == 0 && !allTags)
        {
            return Refuse("Nothing selected to push.", onOutput);
        }

        StringBuilder args = new("push --progress");
        if (force)
        {
            args.Append(" --force-with-lease");
        }

        if (allTags)
        {
            args.Append(" --tags");
        }

        if (setUpstream)
        {
            args.Append(" -u");
        }

        if (recurseSubmodules)
        {
            args.Append(" --recurse-submodules=on-demand");
        }

        args.Append(' ').Append(Quote(target));
        foreach (string spec in refspecs)
        {
            if (!string.IsNullOrWhiteSpace(spec))
            {
                args.Append(' ').Append(Quote(spec));
            }
        }

        string argString = args.ToString();
        IReadOnlyDictionary<string, string?>? env = null;

        if (credentials is not null && IsHttpTarget(repoPath, target))
        {
            string helper = $"!f() {{ test $1 = get && echo username=${UserEnvVar} && echo password=${PassEnvVar}; }}; f";
            argString = $"-c credential.helper= -c \"credential.helper={helper}\" {argString}";
            env = new Dictionary<string, string?>
            {
                [UserEnvVar] = credentials.Username,
                [PassEnvVar] = credentials.Password,
            };
        }

        StringBuilder sb = new();
        int exit;
        try
        {
            exit = GitStreamRunner.Run(repoPath, argString, line =>
            {
                sb.AppendLine(line);
                onOutput(line);
            }, env);
        }
        catch (Exception ex)
        {
            // The streaming process dialog only renders the lines it is handed
            // (not the returned Output), so an exception must be surfaced through
            // onOutput or the user would see an empty "Failed" console.
            string message = $"<error: {ex.GetBaseException().Message}>";
            sb.AppendLine(message);
            onOutput(message);
            exit = -1;
        }

        string output = sb.ToString();
        return new RemoteOpResult(exit == 0, output, LooksLikeAuthFailure(output));
    }

    // Refuses to run and ALSO emits the reason: the streaming process dialog shows
    // only the emitted lines, so a silent refusal would look like an empty failure.
    private static RemoteOpResult Refuse(string reason, Action<string> onOutput)
    {
        onOutput(reason);
        return new RemoteOpResult(false, reason, AuthFailed: false);
    }

    // A target is "http" when it is itself an http/https URL, or when it is a
    // configured remote whose push URL is http/https. Only then does a
    // username/password credential helper make sense (ssh stays key-based).
    private static bool IsHttpTarget(string repoPath, string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? direct)
            && direct.Scheme is "http" or "https")
        {
            return true;
        }

        foreach (string line in Capture(repoPath, $"remote get-url --push {Quote(target)}"))
        {
            if (Uri.TryCreate(line.Trim(), UriKind.Absolute, out Uri? uri)
                && uri.Scheme is "http" or "https")
            {
                return true;
            }
        }

        return false;
    }

    // Mirrors RemoteService.LooksLikeAuthFailure so the dialog's credential retry
    // triggers on the same conditions for every push path.
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

    // Wraps a token in double quotes when it contains anything that a shell-style
    // argument split would break on. Ref names and URLs rarely need it, but a
    // branch like "feature/a b" or a path-with-spaces URL would.
    private static string Quote(string value)
        => value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0
            ? value
            : "\"" + value.Replace("\"", "\\\"") + "\"";

    // Runs a short, non-interactive git command and returns its stdout lines.
    // Read-only plumbing only (for-each-ref / remote get-url) — never a network op.
    private static IReadOnlyList<string> Capture(string repoPath, string arguments)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using Process proc = new() { StartInfo = psi };
            proc.Start();
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);

            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r'))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
