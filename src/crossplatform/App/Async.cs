using System.Diagnostics;
using Avalonia.Threading;

namespace GitExtensions.Avalonia;

/// <summary>
///  The two shapes of asynchrony the views actually need, in one place: start work
///  from a void context without losing its exceptions, and run a piece of work off
///  the UI thread with its result handed back on it.
///
///  <para>The port has no <c>JoinableTaskFactory</c> — it is an Avalonia app, not a
///  Visual Studio extension — so the threading analyzers' standard advice does not
///  apply verbatim. What their warnings were pointing at was real, though, and both
///  problems are structural rather than per-call-site:</para>
///
///  <list type="number">
///   <item>
///    <description>
///     <c>async void</c> handlers and <c>async</c> lambdas passed to <c>EventHandler</c>:
///     an exception that escapes one of those is raised on the synchronization context
///     with nobody to catch it, which on .NET means the PROCESS DIES. A commit dialog
///     must not be able to kill the app because git returned something unexpected.
///    </description>
///   </item>
///   <item>
///    <description>
///     <c>Task.Run(work).ContinueWith(t =&gt; … t.Result …)</c>: reading
///     <see cref="Task{TResult}.Result"/> blocks if the task is not finished, and the
///     continuation swallows faults silently — the two together turn a git failure into
///     a dialog that simply never updates. Awaiting instead cannot block and cannot
///     lose the exception.
///    </description>
///   </item>
///  </list>
/// </summary>
internal static class Async
{
    /// <summary>
    ///  Starts <paramref name="work"/> from a void context (a click handler, a hotkey,
    ///  a menu item) and guarantees its exceptions are observed instead of taking the
    ///  process down.
    /// </summary>
    /// <param name="context">
    ///  What was being done, for the diagnostic line. Keep it short; it is printed
    ///  next to the exception message.
    /// </param>
    internal static void Run(Func<Task> work, string context)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await work().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Report(context, ex);
            }
        }
    }

    /// <summary>
    ///  Observes a task already started elsewhere, so a fault is reported rather than
    ///  raised later on the finalizer thread. The counterpart of <see cref="Run"/> for
    ///  the call sites that legitimately fire work and move on.
    /// </summary>
    /// <remarks>
    ///  A continuation rather than an <c>await</c>, deliberately: awaiting a task
    ///  someone else started is exactly the shape that deadlocks when that task needs
    ///  the thread doing the awaiting. A fault-only continuation observes the
    ///  exception without ever joining the task's completion to this thread.
    /// </remarks>
    internal static void Forget(this Task task, string context)
        => _ = task.ContinueWith(
            faulted => Report(context, faulted.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    ///  Runs <paramref name="work"/> on the thread pool and calls
    ///  <paramref name="onResult"/> with its value on the UI thread. Replaces the
    ///  <c>Task.Run(…).ContinueWith(t =&gt; Dispatcher.UIThread.Post(… t.Result …))</c>
    ///  idiom the port had grown a dozen copies of: same threading, no blocking read
    ///  of <c>Result</c>, and a fault in either half is reported instead of vanishing.
    /// </summary>
    internal static void OffUi<T>(Func<T> work, Action<T> onResult, string context)
        => Run(
            async () =>
            {
                T value = await Task.Run(work).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => onResult(value));
            },
            context);

    private static void Report(string context, Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return;
        }

        string line = $"[Async] {context} failed: {ex.GetType().Name}: {ex.Message}";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }
}
