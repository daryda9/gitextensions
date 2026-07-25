// Real clipboard for the compat layer, backed by Avalonia's IClipboard.
//
// Avalonia only exposes the clipboard through a TopLevel, so a caller with no
// control in hand (all of the reusable core) previously had no way to reach it.
// This shim resolves a TopLevel from the running desktop lifetime instead: the
// active window, else any visible window, else the main window. With no UI at
// all (headless runs) every method is a silent no-op and never throws.

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using GitExtensions.Compat;

namespace System.Windows.Forms;

public static class Clipboard
{
    /// <summary>
    ///  Places <paramref name="text"/> on the system clipboard. Blocks until the
    ///  clipboard has taken ownership so that callers which immediately read it
    ///  back (or exit) observe the new value.
    /// </summary>
    public static void SetText(string? text)
        => _ = AvaloniaHost.Run(
            async owner =>
            {
                IClipboard? clipboard = ClipboardOf(owner);
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(text ?? string.Empty);
                }

                return true;
            },
            fallback: false);

    /// <summary>Returns the clipboard text, or an empty string when unavailable.</summary>
    public static string GetText()
        => AvaloniaHost.Run(
            async owner =>
            {
                IClipboard? clipboard = ClipboardOf(owner);
                return clipboard is null ? string.Empty : await clipboard.GetTextAsync() ?? string.Empty;
            },
            fallback: string.Empty);

    public static bool ContainsText() => GetText().Length > 0;

    public static void Clear()
        => _ = AvaloniaHost.Run(
            async owner =>
            {
                IClipboard? clipboard = ClipboardOf(owner);
                if (clipboard is not null)
                {
                    await clipboard.ClearAsync();
                }

                return true;
            },
            fallback: false);

    /// <summary>
    ///  WinForms compatibility overload. Only text payloads are meaningful on
    ///  Linux; anything else is stringified.
    /// </summary>
    public static void SetDataObject(object? data, bool copy = true, int retryTimes = 0, int retryDelay = 0)
        => SetText(data?.ToString());

    /// <summary>
    ///  Best-effort text set that reports success instead of throwing — the
    ///  contract the original <c>ClipboardUtil.TrySetText</c> offered.
    /// </summary>
    public static bool TrySetText(string? text)
    {
        try
        {
            return AvaloniaHost.Run(
                async owner =>
                {
                    IClipboard? clipboard = ClipboardOf(owner);
                    if (clipboard is null)
                    {
                        return false;
                    }

                    await clipboard.SetTextAsync(text ?? string.Empty);
                    return true;
                },
                fallback: false);
        }
        catch (Exception ex)
        {
            Diagnostics.Trace.TraceError($"[compat] clipboard set failed: {ex}");
            return false;
        }
    }

    private static IClipboard? ClipboardOf(Window owner)
    {
        Debug.Assert(Dispatcher.UIThread.CheckAccess(), "clipboard must be touched on the UI thread");
        return TopLevel.GetTopLevel(owner)?.Clipboard ?? owner.Clipboard;
    }
}
