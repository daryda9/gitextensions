// Bridge between the WinForms-shaped compat shims and the live Avalonia
// application.
//
// The shims expose synchronous, WinForms-style APIs (MessageBox.Show returns a
// DialogResult, FolderBrowserDialog.ShowDialog blocks, Clipboard.SetText is
// void). Avalonia is asynchronous throughout, so every real implementation has
// to bridge async -> sync WITHOUT freezing the UI thread:
//
//   * called ON the UI thread  -> start the async operation and pump a nested
//     dispatcher frame (Dispatcher.PushFrame) until it completes. This is what
//     WinForms' own modal loop does, so the UI keeps painting and the modal
//     dialog stays interactive.
//   * called OFF the UI thread -> post the operation to the UI thread and block
//     the *calling* (background) thread on the resulting task. No deadlock: the
//     UI thread is never blocked.
//
// When there is no Avalonia application at all (headless --selftest runs, unit
// tests, code executed before the main window exists) every entry point falls
// back to the previous no-op result instead of throwing.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;

namespace GitExtensions.Compat;

internal static class AvaloniaHost
{
    /// <summary>
    ///  True when a desktop Avalonia application is up and has at least one
    ///  window that can own a modal dialog / hold the clipboard selection.
    /// </summary>
    internal static bool IsUiAvailable => Application.Current is not null && FindOwnerWindow() is not null;

    /// <summary>
    ///  Best owner for a modal dialog: the currently active window, otherwise the
    ///  last shown visible window, otherwise the main window.
    /// </summary>
    internal static Window? FindOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        IReadOnlyList<Window> windows = desktop.Windows;

        for (int i = windows.Count - 1; i >= 0; i--)
        {
            if (windows[i].IsActive && windows[i].IsVisible)
            {
                return windows[i];
            }
        }

        for (int i = windows.Count - 1; i >= 0; i--)
        {
            if (windows[i].IsVisible)
            {
                return windows[i];
            }
        }

        return desktop.MainWindow;
    }

    /// <summary>
    ///  Runs <paramref name="operation"/> on the UI thread and blocks the caller
    ///  until it completes, returning <paramref name="fallback"/> when there is
    ///  no usable UI or the operation faults.
    /// </summary>
    internal static T Run<T>(Func<Window, Task<T>> operation, T fallback)
    {
        if (Application.Current is null)
        {
            return fallback;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Window? owner = FindOwnerWindow();
            if (owner is null)
            {
                return fallback;
            }

            Task<T> task;
            try
            {
                task = operation(owner);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[compat] UI operation failed: {ex}");
                return fallback;
            }

            if (!task.IsCompleted)
            {
                // Nested message loop: keeps the UI responsive while we wait.
                DispatcherFrame frame = new();
                _ = task.ContinueWith(
                    _ => frame.Continue = false,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.UIThread.PushFrame(frame);
            }

            return Unwrap(task, fallback);
        }

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            Window? owner = FindOwnerWindow();
            if (owner is null)
            {
                completion.TrySetResult(fallback);
                return;
            }

            try
            {
                _ = operation(owner).ContinueWith(
                    t => completion.TrySetResult(Unwrap(t, fallback)),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[compat] UI operation failed: {ex}");
                completion.TrySetResult(fallback);
            }
        });

        // Blocks this background thread only; the UI thread keeps running.
        return completion.Task.GetAwaiter().GetResult();
    }

    private static T Unwrap<T>(Task<T> task, T fallback)
    {
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        if (task.Exception is not null)
        {
            Trace.TraceError($"[compat] UI operation faulted: {task.Exception.GetBaseException()}");
        }

        return fallback;
    }

    /// <summary>
    ///  Fire-and-forget variant for void APIs that must never block or throw.
    /// </summary>
    internal static void Post(Func<Window, Task> operation)
    {
        if (Application.Current is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Window? owner = FindOwnerWindow();
            if (owner is null)
            {
                return;
            }

            try
            {
                _ = operation(owner).ContinueWith(
                    t => Trace.TraceError($"[compat] UI operation faulted: {t.Exception?.GetBaseException()}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"[compat] UI operation failed: {ex}");
            }
        });
    }

    /// <summary>
    ///  Theme brush from the running application's resources, matching the
    ///  App.* keys used by the Avalonia front-end, with a literal fallback so
    ///  the shims still look sane when hosted outside the app.
    /// </summary>
    internal static IBrush Brush(string key, string fallback)
    {
        if (Application.Current is { } app
            && app.TryFindResource(key, app.ActualThemeVariant, out object? value)
            && value is IBrush brush)
        {
            return brush;
        }

        return Avalonia.Media.Brush.Parse(fallback);
    }
}
