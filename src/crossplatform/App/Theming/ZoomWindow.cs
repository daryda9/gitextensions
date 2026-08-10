using Avalonia.Controls;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  A <see cref="Window"/> that follows the user's <see cref="UiSize"/> zoom.
///
///  <para><b>This class exists to be the one place the zoom host is installed (M86).</b>
///  Every window in the app derives from it, and every inline dialog is constructed as one,
///  so the option reaches the whole UI without any window having to remember to opt in —
///  and without a global hook that could reach a window behind its back.</para>
///
///  <para><b>Why a base class and not an app-wide <c>Style</c>.</b> The style route is what
///  produced three defects in a row (M82: a crash on every window; M83: a blank main
///  window; and a dropped transform on windows filled after <c>Show</c>). All three came
///  from one structural fact: a style setter runs from inside a <em>styling callback</em>,
///  Avalonia re-applies styles whenever <see cref="Avalonia.Application.Styles"/> is
///  mutated — which is exactly what opening the Settings dialog does — and the callback
///  therefore ran a second time on an already-wrapped window while it was mid-layout. A
///  constructor runs <b>once</b>, by definition, and nothing re-enters it when styles
///  change.</para>
///
///  <para><b>The constructor is also the safest possible moment.</b> Here the window has no
///  content and no <c>ContentPresenter</c> yet, so the host is installed with a null child
///  and there is nothing to unparent. The content each window assigns immediately
///  afterwards — in a constructor body, in an object initialiser, or much later after
///  <c>Show</c> — is moved into the existing host by the Content watcher
///  <see cref="UiScaling.Install"/> sets up, never by building a second host.</para>
///
///  <para>Deriving is the whole contract: there is nothing to call and nothing to
///  override. A window that for some reason must derive from <see cref="Window"/> directly
///  can still opt in with a single <see cref="UiScaling.Install"/> call, but then it is
///  that window's own responsibility.</para>
/// </summary>
public class ZoomWindow : Window
{
    /// <summary>
    ///  Installs this window's zoom host. See the class remarks for why this is a
    ///  constructor and not a style, an event handler or a global hook.
    /// </summary>
    public ZoomWindow()
    {
        UiScaling.Install(this);

        // The interface font (AppSettings.Font), applied per window: FontFamily and
        // FontSize inherit down the visual tree, so one assignment here reaches every
        // control in the window without a style per control type. Left alone when
        // unset, so the system font keeps deciding.
        if (AppFonts.Ui is { } family)
        {
            FontFamily = family;
        }

        if (AppFonts.UiSize > 0)
        {
            FontSize = AppFonts.UiSize;
        }
    }
}
