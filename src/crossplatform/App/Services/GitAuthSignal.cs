namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Carries "the git command that just ran failed on AUTHENTICATION" from the
///  service that ran it up to the dialog that hosts it, without the verdict having
///  to survive a round-trip through git's (translated) console text.
///
///  <para>Why a holder and not a return value: <c>GitProcessDialog</c> hands the
///  caller's operation an opaque <c>Func&lt;…, GitProcessOutcome&gt;</c>, and the
///  callers that build that outcome live in files this port composes elsewhere. The
///  same trick <see cref="GitStreamRunner.EnterScope"/> already uses is applied:
///  the dialog creates a holder, publishes it on the logical call-flow of its
///  background task (an <see cref="AsyncLocal{T}"/> value flows DOWN into everything
///  the operation calls), and the services report into that object — a mutation the
///  dialog then reads back on its own thread.</para>
/// </summary>
public sealed class GitAuthSignal
{
    private static readonly AsyncLocal<GitAuthSignal?> _current = new();

    private int _authFailed;

    /// <summary>The holder bound to the current logical call-flow, if any.</summary>
    public static GitAuthSignal? Current => _current.Value;

    /// <summary>
    ///  Binds <paramref name="signal"/> to the current logical call-flow. Call it
    ///  INSIDE the background task that runs the operation; the value does not
    ///  escape that task.
    /// </summary>
    public static void Enter(GitAuthSignal signal) => _current.Value = signal;

    /// <summary>
    ///  Records that a git command in this flow failed because of authentication.
    ///  Safe from any thread; the flag only ever goes from unset to set.
    /// </summary>
    public static void Report()
    {
        if (_current.Value is GitAuthSignal signal)
        {
            Interlocked.Exchange(ref signal._authFailed, 1);
        }
    }

    /// <summary>True once any command in this flow reported an authentication failure.</summary>
    public bool AuthFailureDetected => Volatile.Read(ref _authFailed) != 0;

    /// <summary>
    ///  Clears the flag, so one dialog can host several attempts (the retry a
    ///  rejected push or a credential prompt triggers) without the first attempt's
    ///  verdict leaking into the next one's.
    /// </summary>
    public void Clear() => Interlocked.Exchange(ref _authFailed, 0);
}
