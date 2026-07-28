namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Controls the LOCALE of the environment handed to the git processes this port
///  starts for its own operations.
///
///  <para>Why this exists: the port recognises a failed authentication (and a few
///  other recoverable conditions) by reading git's diagnostic messages, and git
///  translates them. With an Italian locale a rejected push prints
///  <c>fatal: Autenticazione non riuscita per '…'</c>, which matches none of the
///  English markers, so the in-app <c>CredentialsDialog</c> fallback never
///  opened (measured in round 10, round 11 fixes it). Rather than shipping a
///  translation table per language — unbounded, and wrong for any locale nobody
///  thought of — the port pins its OWN git children to English diagnostics.</para>
///
///  <para>What exactly is pinned, and why not more:</para>
///  <list type="bullet">
///   <item><description><c>LC_MESSAGES=C</c> — messages only. The encoding-relevant
///    category (<c>LC_CTYPE</c>) is left at the user's value, so accented file
///    names, authors and commit messages keep working, and so do the
///    <c>sh</c>/<c>ssh</c>/credential-helper processes git spawns.</description></item>
///   <item><description><c>LC_ALL</c> is REMOVED (its value is carried over into
///    <c>LC_CTYPE</c> when that is not set on its own). This is not optional:
///    <c>LC_ALL</c> overrides <c>LC_MESSAGES</c>, so with <c>LC_ALL=it_IT.UTF-8</c>
///    left in place the messages stay Italian — verified.</description></item>
///   <item><description><c>LANGUAGE</c> is cleared. gettext ignores it while the
///    message locale is <c>C</c>, so this is belt-and-braces against a wrapper
///    that resets the category.</description></item>
///   <item><description><c>LC_ALL=C</c> is deliberately NOT used: it would drag
///    <c>LC_CTYPE</c> to ASCII for every child in the chain.</description></item>
///  </list>
///
///  <para>Deliberately NOT applied to the embedded Console tab
///  (<see cref="PtyProcess.Start"/>): that shell belongs to the user and must run
///  in the user's language. <see cref="RestoreUserLocale"/> puts the pristine
///  values back there, so the console stays localised even if a process-wide
///  <see cref="DiagnosticLocaleScope"/> happens to be open at that moment.</para>
/// </summary>
public static class GitEnvironment
{
    // The locale variables this class ever touches, in the order glibc resolves
    // them (LC_ALL wins over LC_MESSAGES/LC_CTYPE, which win over LANG).
    private static readonly string[] _localeVars =
    [
        "LC_ALL", "LC_MESSAGES", "LC_CTYPE", "LANG", "LANGUAGE",
    ];

    // The user's locale as it was when the app started, captured BEFORE anything
    // here can modify it (this class is the only writer, and a static field
    // initialiser runs before any member of the class can be called). Used to give
    // the Console tab its own language back.
    private static readonly Dictionary<string, string?> _userLocale = CaptureUserLocale();

    // Nesting depth of DiagnosticLocaleScope, so an inner scope disposing does not
    // restore the environment while an outer one is still running.
    private static int _scopeDepth;
    private static readonly object _scopeLock = new();

    private static Dictionary<string, string?> CaptureUserLocale()
    {
        Dictionary<string, string?> snapshot = new(StringComparer.Ordinal);
        foreach (string name in _localeVars)
        {
            snapshot[name] = Environment.GetEnvironmentVariable(name);
        }

        return snapshot;
    }

    /// <summary>
    ///  Pins <paramref name="env"/> to English diagnostics. Works both for a
    ///  <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> dictionary
    ///  (pre-filled with the inherited environment) and for an overlay dictionary
    ///  applied on top of the inherited environment later.
    /// </summary>
    /// <param name="env">Environment (or overlay) to modify in place.</param>
    /// <param name="nullRemoves">
    ///  <see langword="true"/> when the consumer of <paramref name="env"/> treats a
    ///  <see langword="null"/> value as "unset this variable" (the convention of
    ///  <see cref="PtyProcess.StartCommand"/>); <see langword="false"/> when the
    ///  dictionary IS the environment and a variable is dropped by removing the key.
    /// </param>
    public static void ApplyDiagnosticLocale(IDictionary<string, string?> env, bool nullRemoves = false)
    {
        ArgumentNullException.ThrowIfNull(env);

        // Keep the encoding category the user really has. Read from the process
        // environment rather than from env, because an overlay dictionary does not
        // contain the inherited values at all.
        string? ctype = EffectiveCType();

        if (!string.IsNullOrEmpty(ctype))
        {
            env["LC_CTYPE"] = ctype;
        }

        Unset(env, "LC_ALL", nullRemoves);
        env["LC_MESSAGES"] = "C";
        env["LANGUAGE"] = string.Empty;
    }

    /// <summary>
    ///  Puts the user's own locale back into <paramref name="env"/> — for the
    ///  embedded Console tab, whose shell must never inherit the English pinning.
    /// </summary>
    public static void RestoreUserLocale(IDictionary<string, string?> env, bool nullRemoves = false)
    {
        ArgumentNullException.ThrowIfNull(env);

        foreach (KeyValuePair<string, string?> entry in _userLocale)
        {
            if (entry.Value is null)
            {
                Unset(env, entry.Key, nullRemoves);
            }
            else
            {
                env[entry.Key] = entry.Value;
            }
        }
    }

    /// <summary>
    ///  Pins English diagnostics PROCESS-WIDE for as long as the returned handle is
    ///  alive. Needed for the git commands that go through the shared core
    ///  (<c>GitModule.GitExecutable</c>), which starts its own processes with the
    ///  inherited environment and offers no per-command environment hook — the same
    ///  trick <c>RemoteService.RunWithCredentials</c> already uses for the transient
    ///  credential secret.
    ///  <para>Scopes nest; the environment is restored when the outermost one is
    ///  disposed. Concurrent git commands started by other parts of the app during
    ///  the window simply also get English diagnostics, which is harmless.</para>
    /// </summary>
    public static IDisposable DiagnosticLocaleScope() => new ProcessWideScope();

    // The encoding category actually in force for this process, resolved with
    // glibc's precedence: LC_ALL beats LC_CTYPE, which beats LANG.
    private static string? EffectiveCType() => FirstNonEmpty(
        Environment.GetEnvironmentVariable("LC_ALL"),
        Environment.GetEnvironmentVariable("LC_CTYPE"),
        Environment.GetEnvironmentVariable("LANG"));

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Unset(IDictionary<string, string?> env, string name, bool nullRemoves)
    {
        if (nullRemoves)
        {
            env[name] = null;
        }
        else
        {
            env.Remove(name);
        }
    }

    private sealed class ProcessWideScope : IDisposable
    {
        private bool _disposed;

        public ProcessWideScope()
        {
            lock (_scopeLock)
            {
                if (_scopeDepth++ == 0)
                {
                    // The process environment is a plain string→string map here, so
                    // the "remove" convention is a null value.
                    Environment.SetEnvironmentVariable("LC_CTYPE", EffectiveCType());
                    Environment.SetEnvironmentVariable("LC_ALL", null);
                    Environment.SetEnvironmentVariable("LC_MESSAGES", "C");
                    Environment.SetEnvironmentVariable("LANGUAGE", string.Empty);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_scopeLock)
            {
                if (--_scopeDepth > 0)
                {
                    return;
                }

                foreach (KeyValuePair<string, string?> entry in _userLocale)
                {
                    Environment.SetEnvironmentVariable(
                        entry.Key,
                        string.IsNullOrEmpty(entry.Value) ? null : entry.Value);
                }
            }
        }
    }
}
