namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Detects "this git command failed because of authentication" WITHOUT reading a
///  single word of git's output — the second, language-independent line of defence
///  behind <see cref="GitEnvironment"/>.
///
///  <para>How: git talks to its credential helpers through a tiny protocol with
///  three operations — <c>get</c> (asking for a username/password),
///  <c>store</c> (they worked) and <c>erase</c> (the server REJECTED them, git
///  invalidates them). Those verbs are protocol tokens, not messages: they are
///  identical in every locale. The probe adds one extra credential helper to the
///  single git invocation — a shell one-liner that appends the verb it is called
///  with to a private file and prints nothing, so it never answers for, or
///  interferes with, the user's real helper. Afterwards:</para>
///  <list type="bullet">
///   <item><description><see cref="SawRejection"/> (<c>erase</c>) — credentials were
///    supplied and refused. This is exactly the case that printed
///    <c>Autenticazione non riuscita</c> and matched no English marker.</description></item>
///   <item><description><see cref="SawRequest"/> (<c>get</c>) with a non-zero exit —
///    git needed credentials for this command and the command failed, e.g. it could
///    not read a username because prompts are disabled.</description></item>
///  </list>
///
///  <para>Measured behaviour that makes this work: git hands <c>erase</c> to EVERY
///  configured helper, so the probe sees the rejection even though a <c>-c</c>
///  helper is consulted LAST for <c>get</c> (verified on git 2.43 against a local
///  server that always answers <c>401</c>).</para>
///
///  <para>Only ever attached to network commands the port itself runs
///  (fetch/pull/push). Never to the embedded Console tab: that is the user's own
///  shell and the port does not rewrite its command lines.</para>
/// </summary>
public sealed class GitAuthProbe : IDisposable
{
    /// <summary>Environment variable through which the helper learns where to write.</summary>
    private const string PathEnvVar = "GE_AVALONIA_AUTH_PROBE";

    private readonly string? _markerPath;
    private bool _disposed;

    private GitAuthProbe(string? markerPath) => _markerPath = markerPath;

    /// <summary>
    ///  Creates a probe for ONE git invocation. Never throws: when a usable marker
    ///  path cannot be created the probe is inert (<see cref="IsEnabled"/> is
    ///  <see langword="false"/>, the argument string is returned untouched) and
    ///  detection falls back to <see cref="GitEnvironment"/> + the English markers.
    /// </summary>
    public static GitAuthProbe Create()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "gitext-avalonia");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(
                dir,
                $"auth-{Environment.ProcessId}-{Guid.NewGuid():N}.probe");

            // The helper body is a shell one-liner passed through a git argument
            // string, so a path containing a space, a quote or a dollar could not be
            // quoted safely in every layer it crosses. Those are not paths /tmp
            // normally produces; when they happen the probe simply stays off.
            if (path.AsSpan().IndexOfAny(" \t\"'$`\\") >= 0)
            {
                return new GitAuthProbe(markerPath: null);
            }

            return new GitAuthProbe(path);
        }
        catch (Exception)
        {
            return new GitAuthProbe(markerPath: null);
        }
    }

    /// <summary>False when no marker file could be prepared; the probe then does nothing.</summary>
    public bool IsEnabled => _markerPath is not null;

    /// <summary>
    ///  Prepends the probe's credential helper to <paramref name="arguments"/>.
    ///  Configuring a helper on the command line APPENDS it to the configured list,
    ///  so the user's real helper keeps priority for <c>get</c>.
    /// </summary>
    public string Decorate(string arguments)
    {
        if (_markerPath is null)
        {
            return arguments;
        }

        // No double quotes inside the body: the whole helper travels as one
        // double-quoted token through ProcessStartInfo.Arguments (and, on the PTY
        // path, through a single-quoted sh argument). $1 is the operation git asks
        // for; the env var is expanded by the helper's own shell, not by us.
        string helper = $"!f() {{ echo $1 >> ${PathEnvVar}; }}; f";
        return $"-c \"credential.helper={helper}\" {arguments}";
    }

    /// <summary>
    ///  Returns an environment overlay for the child process containing
    ///  <paramref name="baseEnv"/> plus the probe's marker path.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? WithMarker(IReadOnlyDictionary<string, string?>? baseEnv)
    {
        if (_markerPath is null)
        {
            return baseEnv;
        }

        Dictionary<string, string?> env = baseEnv is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(baseEnv, StringComparer.Ordinal);
        env[PathEnvVar] = _markerPath;
        return env;
    }

    /// <summary>
    ///  Publishes the marker path in THIS process's environment, for git commands
    ///  started through the shared core (which offers no per-command environment).
    ///  Restore by disposing the returned handle.
    /// </summary>
    public IDisposable EnterProcessEnvironment()
    {
        if (_markerPath is null)
        {
            return new NoopScope();
        }

        return new ProcessEnvScope(_markerPath);
    }

    /// <summary>
    ///  True when git asked a credential helper for credentials during the command:
    ///  the command needed authentication at all.
    /// </summary>
    public bool SawRequest => Verbs().Contains("get");

    /// <summary>
    ///  True when git INVALIDATED the credentials it had used, which it does only
    ///  after the server refused them: an authentication failure, in any language.
    /// </summary>
    public bool SawRejection => Verbs().Contains("erase");

    /// <summary>
    ///  The locale-independent verdict for a finished command:
    ///  a rejection, or credentials needed by a command that then failed.
    /// </summary>
    /// <param name="exitCode">The git process exit code.</param>
    public bool LooksLikeAuthFailure(int exitCode)
    {
        IReadOnlyCollection<string> verbs = Verbs();
        return verbs.Contains("erase") || (exitCode != 0 && verbs.Contains("get"));
    }

    private IReadOnlyCollection<string> Verbs()
    {
        if (_markerPath is null)
        {
            return [];
        }

        try
        {
            if (!File.Exists(_markerPath))
            {
                return [];
            }

            HashSet<string> verbs = new(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(_markerPath))
            {
                string verb = line.Trim();
                if (verb.Length > 0)
                {
                    verbs.Add(verb);
                }
            }

            return verbs;
        }
        catch (Exception)
        {
            // Unreadable marker: report nothing rather than failing the operation.
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_markerPath is null)
        {
            return;
        }

        try
        {
            File.Delete(_markerPath);
        }
        catch (Exception)
        {
            // Best effort: a leftover empty file in the temp directory is harmless.
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ProcessEnvScope : IDisposable
    {
        private readonly string? _previous;

        public ProcessEnvScope(string markerPath)
        {
            _previous = Environment.GetEnvironmentVariable(PathEnvVar);
            Environment.SetEnvironmentVariable(PathEnvVar, markerPath);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(PathEnvVar, _previous);
    }
}
