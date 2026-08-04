using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Applies the chosen <see cref="UiSize"/> as a real zoom of a window's whole content,
///  and owns the current level the way <see cref="ThemeManager"/> owns the theme and the
///  style.
///
///  <para><b>The mechanism, and why it is a transform again (M86).</b> M81 zoomed with a
///  per-window <see cref="LayoutTransformControl"/>, M84 removed it for two measured
///  reasons, and M86 brings it back with <em>both</em> of those reasons eliminated rather
///  than tolerated:</para>
///  <list type="number">
///   <item><b>"The transform never reached popups" (M83).</b> True while popups were
///    separate visual roots — native windows on Win32 by default. M86 sets
///    <c>OverlayPopups = true</c> on both backends (<c>Program.BuildAvaloniaApp</c>), so
///    popup content is hosted in the window's own <c>OverlayLayer</c>, inside our host,
///    and therefore scales with it. The cost is real and is stated in the UI: an overlay
///    popup cannot extend beyond the window's bounds.</item>
///   <item><b>"It mutated the content tree from inside a styling callback" (M82/M83).</b>
///    That was a property of the <em>installation route</em>, not of the transform. M81
///    opted windows in with an app-wide <see cref="Style"/> whose setter ran during the
///    window's first measure pass — and Avalonia re-applies styles whenever
///    <see cref="Application.Styles"/> is mutated (which is exactly what opening Settings
///    does), so the setter came round a second time on an already-wrapped window. There is
///    <b>no style and no styling callback here any more</b>: <see cref="Install"/> is
///    called by the window itself, from <see cref="ZoomWindow"/>'s constructor. Nothing
///    re-enters it when <see cref="Application.Styles"/> changes.</item>
///  </list>
///
///  <para><b>Constructor timing is what makes this safe, not just legal.</b> At the point
///  <see cref="ZoomWindow"/> calls <see cref="Install"/> the window has no
///  <see cref="ContentControl.Content"/> yet and no
///  <see cref="ContentControl.Presenter"/> at all, so the host goes in with a null child
///  and there is nothing to prise out of a presenter. The M82 crash — <c>The Control
///  already has a parent</c>, thrown because the presenter was already holding the content
///  when the wrapper tried to take it — <b>cannot arise on this path</b>. The
///  presenter-reconciliation trick is kept anyway in <see cref="TryReparent"/>, because
///  content assigned later (every window that fills itself after <c>Show</c>, and the
///  windows that replace their content on a language switch) reaches it with a live
///  presenter.</para>
///
///  <para><b>What now scales, and it is the whole window.</b> A layout transform scales the
///  measured and rendered result of everything beneath it, so it does not care whether a
///  size came from a resource or from a literal. That is the point: the revision grid, the
///  diff and the file lists — the parts M84's font mechanism provably could not move,
///  because <see cref="Metrics"/> sizes are compile-time constants read once when a view is
///  built — grow at both levels along with the chrome, the spacing, the control heights and
///  the icons.</para>
///
///  <para><b>The font-size knob of M84 is gone; its baseline is not.</b> Scaling font
///  resources on top of a zoom would be two competing size controls, and their product is
///  a size no one chose. <see cref="InstallChromeBaseline"/> therefore writes the three
///  Fluent keys <b>once, at a fixed 12px</b>, and never again — that write is the M81
///  correction that fixed the user's original complaint (Fluent's 14 down to upstream's
///  12), and it is independent of the zoom level.</para>
///
///  <para><b>Live, not on restart.</b> Assigning
///  <see cref="LayoutTransformControl.LayoutTransform"/> invalidates the host's own
///  measure, so every open window re-lays-out at the new factor on the next layout pass
///  and no view is rebuilt. There is nothing half-applied to explain in the UI, and no
///  restart to ask for.</para>
///
///  <para><b>Why the level is not a third argument to
///  <see cref="ThemeManager.Apply(ThemeVariant, AppStyle)"/>.</b> M80's rule was that no
///  call site may pass a literal for a dimension the user did not touch. The zoom shares
///  nothing with the palette, so it keeps its own owner and its own single-argument
///  <see cref="Apply(UiSize)"/>, and the theme/style call sites are left as M80 left
///  them.</para>
/// </summary>
public static class UiScaling
{
    /// <summary>The active level. Changed only through <see cref="Apply(UiSize)"/>.</summary>
    public static UiSize CurrentSize { get; private set; } = UiSize.Standard;

    /// <summary>The active zoom factor.</summary>
    public static double CurrentScale => UiSizes.Scale(CurrentSize);

    // The window's one and only host, remembered on the window itself. This is what makes
    // Install idempotent: a second call on the same window returns without building a
    // second host. M83's blank main window was two hosts fighting over Window.Content
    // until the real content was parented to neither, and although the styling callback
    // that caused the second call is gone (see the class remarks), a window is free to
    // call Install twice and must not be punished for it.
    private static readonly AttachedProperty<LayoutTransformControl?> HostProperty =
        AvaloniaProperty.RegisterAttached<Window, LayoutTransformControl?>("ZoomHost", typeof(UiScaling));

    // The hosts of the windows still alive. Weak, because a closed dialog must be
    // collectable: the list is only ever walked to re-scale, and a window that has gone
    // away needs no re-scaling. Compacted on every walk, so a long session does not
    // accumulate dead slots.
    private static readonly List<WeakReference<LayoutTransformControl>> Hosts = [];

    // Upstream draws its chrome in SystemFonts.MessageBoxFont — Segoe UI 9pt, i.e. 12px
    // at 100% DPI (AppSettings.Font, GitCommands/Settings/AppSettings.cs:1550).
    private const double ChromeFontSize = 12;

    /// <summary>
    ///  Writes the app's chrome font baseline: <b>12px</b>, upstream Git Extensions' own
    ///  chrome size, over Fluent's 14. Call once from <c>App.Initialize</c>.
    ///
    ///  <para><b>Fixed, and no longer a function of <see cref="UiSize"/> (M86).</b> Under
    ///  M84 these three keys <em>were</em> the size option; now the zoom scales text as a
    ///  consequence of scaling everything, so varying them as well would be a second,
    ///  competing knob. What survives from M84 is exactly this: the corrected baseline,
    ///  which is the part that fixed the user's complaint that the port read larger than
    ///  upstream.</para>
    /// </summary>
    public static void InstallChromeBaseline()
    {
        if (Application.Current is not Application app)
        {
            return;
        }

        // Read by every Fluent ControlTheme through a dynamic resource.
        app.Resources["ControlContentThemeFontSize"] = ChromeFontSize;

        // Fluent keeps tooltips on their own key (default 12). Left behind, a tooltip
        // would be the one piece of chrome not at the baseline.
        app.Resources["ToolTipContentThemeFontSize"] = ChromeFontSize;

        // Fluent's own tab header size is 24 — oversized for a dense tool, which is why
        // the port has always overridden it. Since M84 the override IS this key
        // (ModernStyles no longer sets FontSize on TabItem).
        app.Resources["TabItemHeaderFontSize"] = ChromeFontSize;
    }

    /// <summary>
    ///  Wraps <paramref name="window"/>'s content in this window's zoom host, so the
    ///  window follows <see cref="CurrentSize"/> now and on every later change.
    ///
    ///  <para><b>Called by the window itself</b> — see <see cref="ZoomWindow"/>, whose
    ///  constructor is the only caller in the app. Deliberately public and explicit: a
    ///  window that is not a <see cref="ZoomWindow"/> may opt in with one call, and no
    ///  style, callback or global hook can opt it in behind its back.</para>
    ///
    ///  <para>Idempotent, and it never leaves the window's content unparented: if the
    ///  wrapper cannot be installed it declines and the window is simply drawn
    ///  unzoomed. An appearance option is not worth a crash or a blank window.</para>
    /// </summary>
    public static void Install(Window window)
    {
        if (window.GetValue(HostProperty) is LayoutTransformControl already)
        {
            // Already wrapped. Nothing in the app calls Install twice today, but see
            // HostProperty: a second host is the failure mode that blanked the main
            // window in M83, so it is made unreachable rather than merely unlikely.
            // Re-assert the installation in case a Content write slipped past the
            // handler below (it cannot for a change raised after subscription, but a
            // caller invoking Install late on an already-filled window can).
            if (!ReferenceEquals(window.Content, already))
            {
                TryReparent(window, already, window.Content);
            }

            return;
        }

        LayoutTransformControl host = new() { LayoutTransform = Transform(CurrentScale) };

        if (!TryReparent(window, host, window.Content))
        {
            // Declined — see TryReparent. The window keeps the content it had and is
            // drawn unzoomed; it is not registered in Hosts, so a later level change
            // leaves it alone too.
            return;
        }

        window.SetValue(HostProperty, host);
        Hosts.Add(new WeakReference<LayoutTransformControl>(host));

        // A window that assigns Content after construction — which, called from a
        // constructor, is EVERY window: object initialisers and constructor bodies both
        // run after the base constructor — would otherwise throw the host away. Move the
        // new content into the same host instead of building another one, so the host,
        // and its entry in Hosts, stays the same object for the window's whole life.
        bool reparenting = false;
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == ContentControl.ContentProperty
                && !reparenting
                && !ReferenceEquals(window.Content, host))
            {
                // TryReparent writes Content twice at most, and both writes come back
                // here; the flag is what stops the second one starting a new round.
                reparenting = true;
                try
                {
                    TryReparent(window, host, window.Content);
                }
                finally
                {
                    reparenting = false;
                }
            }
        };
    }

    /// <summary>
    ///  Sets the level that new windows are created at and re-zooms the open ones in
    ///  place — live, like the theme and the style, and without rebuilding any view.
    /// </summary>
    public static void Apply(UiSize size)
    {
        CurrentSize = size;
        ITransform? transform = Transform(UiSizes.Scale(size));

        for (int i = Hosts.Count - 1; i >= 0; i--)
        {
            if (Hosts[i].TryGetTarget(out LayoutTransformControl? host))
            {
                // Assigning the transform is enough: LayoutTransformControl invalidates
                // its own measure, so the window re-lays-out at the new factor on the
                // next layout pass. Verified headless on already-shown windows — an
                // explicit InvalidateMeasure() here changes nothing.
                host.LayoutTransform = transform;
            }
            else
            {
                Hosts.RemoveAt(i);
            }
        }
    }

    // null at 1.0, so Standard costs nothing beyond one pass-through element in the tree:
    // a LayoutTransformControl with no transform measures and arranges its child exactly
    // as its parent would have. This is what lets Standard be honestly described as "no
    // transform at all".
    private static ITransform? Transform(double scale)
        => scale == 1.0 ? null : new ScaleTransform(scale, scale);

    /// <summary>
    ///  Makes <paramref name="host"/> the window's content and <paramref name="content"/>
    ///  the host's child, or leaves the window exactly as it was and returns
    ///  <see langword="false"/>.
    ///
    ///  <para><b>The window is not what parents its content (M82).</b> The window's
    ///  <see cref="ContentPresenter"/> is, and it only picks up (or drops) a child on its
    ///  next layout pass. So clearing <c>Window.Content</c> does NOT detach the old content
    ///  there and then: hand it to <see cref="LayoutTransformControl.Child"/> in the same
    ///  breath and Avalonia throws <c>InvalidOperationException: The Control already has a
    ///  parent</c>. <see cref="ContentPresenter.UpdateChild"/> forces the presenter to
    ///  reconcile immediately, which is what actually frees the control.</para>
    ///
    ///  <para>On the constructor path there is no presenter yet and this is all moot — see
    ///  the class remarks. It matters on the <em>later</em> Content writes: a window filled
    ///  after <c>Show</c>, or one replacing its body on a language switch, is already laid
    ///  out and its presenter is holding the old content.</para>
    ///
    ///  <para>The parent check afterwards is not belt-and-braces, it is the contract. A
    ///  window whose content cannot be freed — no presenter yet, a presenter that declined,
    ///  a control held elsewhere — must be left unzoomed rather than bring the process down
    ///  or, worse, be left blank.</para>
    /// </summary>
    private static bool TryReparent(Window window, LayoutTransformControl host, object? content)
    {
        if (ReferenceEquals(content, host))
        {
            return true;
        }

        // Never make the host a descendant of itself. HostProperty stops the one path
        // that ever asked for this, but the cost of getting it wrong is the whole window
        // going blank, silently — the bug this guard is named after. Declining leaves the
        // window as it is.
        if (content is Control candidate && host.GetLogicalAncestors().Contains(candidate))
        {
            return false;
        }

        window.Content = host;
        window.Presenter?.UpdateChild();

        Control? child = AsControl(content);
        if (child is not null && (child.Parent is not null || child.GetVisualParent() is not null))
        {
            window.Content = content;
            return false;
        }

        host.Child = child;

        // RE-ASSERTED, not redundant. LayoutTransformControl rewrites its own
        // LayoutTransform as part of laying out (an assigned ScaleTransform comes back as
        // the equivalent MatrixTransform), and when it lays out with NO child it clears
        // the property outright. A window shown empty and given its content afterwards
        // therefore reached this point with the transform already dropped, and would have
        // been drawn unzoomed until the next level change. Measured headless in M81:
        // without this line such a window reports a null LayoutTransform at Large.
        host.LayoutTransform = Transform(CurrentScale);
        return true;
    }

    // Window.Content is an object: a control goes straight in, anything else (a string, or
    // a value a DataTemplate will render) keeps its templating by going through a
    // ContentControl, because LayoutTransformControl.Child is typed Control.
    private static Control? AsControl(object? content) => content switch
    {
        null => null,
        Control control => control,
        _ => new ContentControl { Content = content },
    };
}
