using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Escape-closes a dialog <see cref="Window"/>, the way every WinForms dialog
///  upstream does through its <c>CancelButton</c>.
///
///  <para>The port had grown a dozen hand-rolled copies of the same four-line
///  <c>KeyDown</c> block (<c>RemotesDialog</c>, <c>WorktreesDialog</c>,
///  <c>SubmodulesDialog</c>, …) while fifteen other dialog windows had none at all
///  and leaned on <see cref="Button.IsCancel"/> alone. That is not equivalent:
///  <c>IsCancel</c> is driven by a bubbling <c>KeyDown</c>, so it only fires once the
///  key is actually routed, and a key is only routed when something inside the window
///  has focus. A dialog made of nothing but text and one button — <c>AboutDialog</c> —
///  never focuses anything, so its Escape never left the window manager, and a dialog
///  assembled inline with no cancel button at all — the "Open Git repository" picker —
///  had nothing to fire in the first place. Both looked "unresponsive to Escape" while
///  dialogs full of text boxes worked, which is exactly the reported symptom.</para>
///
///  <para>So this helper does the two things a dialog needs, not one: it guarantees a
///  focus route, then handles the key.</para>
/// </summary>
internal static class DialogKeys
{
    /// <summary>
    ///  Makes <paramref name="window"/> close on Escape.
    /// </summary>
    /// <param name="window">The dialog window to wire.</param>
    /// <param name="canClose">
    ///  Optional veto, evaluated when Escape arrives. Return <see langword="false"/> to
    ///  swallow the key and keep the window open — what a dialog that owns a running
    ///  git process wants, so Escape cannot abandon work in flight. When
    ///  <see langword="null"/> the window always closes.
    /// </param>
    public static void InstallEscapeClose(Window window, Func<bool>? canClose = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Bubbling and *not* handledEventsToo: a find bar, a completion popup, a
        // context menu or an inline prompt inside the dialog must get first refusal,
        // and every one of them marks the event handled. Tunnelling here would break
        // DiffView/BlameView's find bars, RepoObjectsTree's search box and the
        // embedded terminal, which all rely on seeing Escape before the window does.
        window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);

        EnsureFocusRoute(window);

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            {
                return;
            }

            // Handled either way: a vetoed Escape must not fall through to a
            // Button.IsCancel that would close the window behind the veto's back.
            e.Handled = true;

            if (canClose is null || canClose())
            {
                window.Close();
            }
        }
    }

    /// <summary>
    ///  Guarantees that key presses reaching <paramref name="window"/> from the window
    ///  manager are actually routed inside it, by giving the window itself focus when
    ///  nothing in it has any.
    ///
    ///  <para>For dialogs that already own a correct Escape handler and only need it to
    ///  start firing. <c>CommitDialog</c> is the case that proves the distinction: its
    ///  Escape-as-Cancel was written correctly and still did nothing, because a
    ///  <c>KeyDown</c> is routed from the focused element and that dialog focuses
    ///  nothing on open — so the key died before reaching the handler. Wiring a second
    ///  Escape handler there would have been wrong; the handler was never the
    ///  problem.</para>
    /// </summary>
    public static void EnsureFocusRoute(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // The visual-root test, not a null check: FocusManager is app-wide in
        // Avalonia 11, so GetFocusedElement() keeps returning the *main* window's
        // focused control while a dialog is up, and a null check would pass and fix
        // nothing. Only adopt focus when it is not already somewhere in this window,
        // so a dialog that deliberately focuses its first field keeps it.
        window.Opened += (_, _) =>
        {
            if (window.FocusManager?.GetFocusedElement() is Visual focused
                && ReferenceEquals(focused.GetVisualRoot(), window))
            {
                return;
            }

            window.Focusable = true;
            window.Focus();
        };
    }
}
