using System.Runtime.InteropServices;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Interns the X11 atoms Avalonia needs, before Avalonia looks for them.
///
///  <para><b>The defect.</b> The decoration's "X" did not close the app, and — far
///  worse — the app therefore never went through <c>Window.Closing</c>, so
///  <c>PersistLayout()</c> never ran and the whole UI state (geometry, splitters,
///  active tab, left-panel collapse, grid view toggles, pull action) was lost on
///  every exit that was not <c>Start → Exit</c>.</para>
///
///  <para><b>The root cause is not in this port, and it is not "Avalonia has no
///  close protocol".</b> <c>Avalonia.X11.X11Window</c> implements
///  <c>WM_DELETE_WINDOW</c> correctly: it publishes it via <c>XSetWMProtocols</c>
///  and, on the matching <c>ClientMessage</c>, calls the managed close path that
///  raises <c>Closing</c>. What breaks is one argument, in
///  <c>Avalonia.X11.X11Atoms.PopulateAtoms</c> (11.3.14):</para>
///  <code>
///  XLib.XInternAtoms(display, names, names.Length, only_if_exists: true, atoms);
///  …
///  private void InitAtom(ref nint field, string name, nint value)
///  {
///      if (value != IntPtr.Zero) { field = value; SetName(name, value); }
///  }
///  </code>
///  <para><c>only_if_exists: true</c> asks the server "give me these atoms only if
///  somebody already interned them". An X server starts with just the 68 predefined
///  atoms; <c>WM_PROTOCOLS</c>, <c>WM_DELETE_WINDOW</c> and every <c>_NET_*</c> name
///  are not among them. So on a server where no earlier client has interned them,
///  all 78 lookups return <c>None</c> (0), <c>InitAtom</c> skips every assignment,
///  and Avalonia's whole atom table stays zero. From there:</para>
///  <list type="bullet">
///   <item><description><c>XSetWMProtocols</c> publishes the single atom <c>0</c>,
///     so the window advertises no close protocol and a window manager falls back to
///     <c>XKillClient</c> — the connection dies, no <c>Closing</c>, no saved
///     state.</description></item>
///   <item><description>The receive side is dead too: the handler's first test is
///     <c>message_type != _x11.Atoms.WM_PROTOCOLS</c>, which with a zeroed table
///     rejects every real <c>WM_PROTOCOLS</c> message. Advertising the atom from
///     outside is therefore not enough — measured, it is still
///     ignored.</description></item>
///   <item><description>Everything else keyed off that table goes with it:
///     <c>_NET_WM_STATE</c> (maximize/fullscreen), <c>_NET_WM_WINDOW_TYPE</c>,
///     <c>_MOTIF_WM_HINTS</c>, <c>_NET_WM_SYNC_REQUEST</c>, <c>_NET_WM_PING</c>, and
///     the <c>CLIPBOARD</c> selection atoms.</description></item>
///  </list>
///
///  <para><b>Who is affected.</b> A full desktop (GNOME, KDE, Xfce) interns these
///  names long before the app starts, so the table fills and everything works —
///  which is why this hid for so long. It bites wherever the app is among the first
///  clients on a fresh server: a bare <c>startx</c>, a minimal window manager that
///  never interns the EWMH names, and every <c>Xvfb</c> session — including this
///  port's own headless GUI verification, where the crippled atom table silently
///  changes what a screenshot run is actually testing.</para>
///
///  <para><b>The fix.</b> Intern the same names with <c>only_if_exists: false</c>
///  before Avalonia asks. Atoms are server-global and permanent for the life of the
///  server, so by the time <c>X11Atoms</c> runs its <c>only_if_exists: true</c> query
///  every name exists and the table populates normally. Avalonia then does its own
///  job: the "X" produces a real <c>WM_DELETE_WINDOW</c>, the managed close path
///  runs, <c>Closing</c> fires and <c>PersistLayout()</c> saves the state.</para>
///
///  <para>This deliberately replaces the native close-protocol receiver that the
///  <see cref="X11DropTarget"/> precedent would suggest. A receiver would need a
///  second connection, its own event thread, and — because a <c>ClientMessage</c>
///  sent with an empty event mask reaches only the client that created the window —
///  reparenting Avalonia's toplevel under a window of ours, which would then have to
///  forward every move, resize and maximize by hand. One <c>XInternAtoms</c> call
///  removes the cause instead of working around it, adds no thread, owns no window,
///  and repairs the close path for <b>every</b> window of the app — the main window,
///  the modal dialogs and their Esc/"X" handling included — because they all share
///  the one atom table.</para>
///
///  <para><b>Failure is silent by construction.</b> No X server, no
///  <c>libX11</c>, a Wayland-only session, an exotic build: every path ends in a
///  no-op and the app behaves exactly as it did before. Nothing here is required for
///  the app to run.</para>
/// </summary>
public static class X11AtomPrimer
{
    private const string LibX11 = "libX11.so.6";

    /// <summary>
    ///  The names <c>Avalonia.X11.X11Atoms.PopulateAtoms</c> asks the server for
    ///  (Avalonia 11.3.14), in its own order. It is copied whole rather than reduced
    ///  to the close protocol on purpose: the zeroed table breaks maximize, window
    ///  type, frame hints and the clipboard just as thoroughly, and interning a name
    ///  that a later Avalonia no longer wants costs one unused atom on the server.
    /// </summary>
    private static readonly string[] AtomNames =
    [
        "EDID", "WM_PROTOCOLS", "WM_DELETE_WINDOW", "WM_TAKE_FOCUS", "_NET_SUPPORTED",
        "_NET_CLIENT_LIST", "_NET_NUMBER_OF_DESKTOPS", "_NET_DESKTOP_GEOMETRY",
        "_NET_DESKTOP_VIEWPORT", "_NET_CURRENT_DESKTOP", "_NET_DESKTOP_NAMES",
        "_NET_ACTIVE_WINDOW", "_NET_WORKAREA", "_NET_SUPPORTING_WM_CHECK",
        "_NET_VIRTUAL_ROOTS", "_NET_DESKTOP_LAYOUT", "_NET_SHOWING_DESKTOP",
        "_NET_CLOSE_WINDOW", "_NET_MOVERESIZE_WINDOW", "_NET_WM_MOVERESIZE",
        "_NET_RESTACK_WINDOW", "_NET_REQUEST_FRAME_EXTENTS", "_NET_WM_NAME",
        "_NET_WM_VISIBLE_NAME", "_NET_WM_ICON_NAME", "_NET_WM_VISIBLE_ICON_NAME",
        "_NET_WM_DESKTOP", "_NET_WM_WINDOW_TYPE", "_NET_WM_STATE",
        "_NET_WM_ALLOWED_ACTIONS", "_NET_WM_STRUT", "_NET_WM_STRUT_PARTIAL",
        "_NET_WM_ICON_GEOMETRY", "_NET_WM_ICON", "_NET_WM_PID", "_NET_WM_HANDLED_ICONS",
        "_NET_WM_USER_TIME", "_NET_FRAME_EXTENTS", "_NET_WM_PING",
        "_NET_WM_SYNC_REQUEST", "_NET_WM_SYNC_REQUEST_COUNTER", "_NET_SYSTEM_TRAY_S",
        "_NET_SYSTEM_TRAY_ORIENTATION", "_NET_SYSTEM_TRAY_OPCODE",
        "_NET_WM_STATE_MAXIMIZED_HORZ", "_NET_WM_STATE_MAXIMIZED_VERT",
        "_NET_WM_STATE_FULLSCREEN", "_XEMBED", "_XEMBED_INFO", "_MOTIF_WM_HINTS",
        "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_ABOVE", "_NET_WM_STATE_MODAL",
        "_NET_WM_STATE_HIDDEN", "_NET_WM_CONTEXT_HELP", "_NET_WM_WINDOW_OPACITY",
        "_NET_WM_WINDOW_TYPE_DESKTOP", "_NET_WM_WINDOW_TYPE_DOCK",
        "_NET_WM_WINDOW_TYPE_TOOLBAR", "_NET_WM_WINDOW_TYPE_MENU",
        "_NET_WM_WINDOW_TYPE_UTILITY", "_NET_WM_WINDOW_TYPE_SPLASH",
        "_NET_WM_WINDOW_TYPE_DIALOG", "_NET_WM_WINDOW_TYPE_NORMAL", "CLIPBOARD",
        "CLIPBOARD_MANAGER", "SAVE_TARGETS", "MULTIPLE", "OEMTEXT", "UNICODETEXT",
        "TARGETS", "UTF8_STRING", "UTF16_STRING", "ATOM_PAIR", "MANAGER",
        "_KDE_NET_WM_BLUR_BEHIND_REGION", "INCR", "_NET_WM_STATE_FOCUSED",
    ];

    /// <summary>
    ///  Makes sure every name in <see cref="AtomNames"/> exists on the X server, so
    ///  that Avalonia's own <c>only_if_exists: true</c> lookup finds it. Call once,
    ///  before the Avalonia app builder starts (see <c>Program.Main</c>); calling it
    ///  again is harmless, as interning an existing atom just returns it.
    /// </summary>
    /// <returns>
    ///  True when the atoms were interned. False means there was nothing to do or
    ///  nothing we could do — not an X11 session, no display to connect to, no
    ///  libX11 — in which case the app carries on exactly as before.
    /// </returns>
    public static bool TryPrime()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        // No DISPLAY means no X server to talk to (a pure Wayland session, or a
        // service with no session at all). Under XWayland DISPLAY is set and this
        // works — and is a no-op there, because the compositor's X clients have
        // long since interned these names.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return false;
        }

        IntPtr display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                return false;
            }

            // One round trip for the whole list. only_if_exists: false is the entire
            // point — it creates the atoms that are missing.
            IntPtr[] atoms = new IntPtr[AtomNames.Length];
            XInternAtoms(display, AtomNames, AtomNames.Length, false, atoms);
            XFlush(display);
            return true;
        }
        catch (Exception)
        {
            // DllNotFoundException without libX11, EntryPointNotFoundException on an
            // exotic build: the close path stays as broken as it was, and nothing
            // else notices.
            return false;
        }
        finally
        {
            try
            {
                if (display != IntPtr.Zero)
                {
                    XCloseDisplay(display);
                }
            }
            catch
            {
                // Nothing left to do.
            }
        }
    }

    // ---- libX11 ---------------------------------------------------------------------

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(string? display);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XInternAtoms(
        IntPtr display, string[] names, int count, bool onlyIfExists, IntPtr[] atomsReturn);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);
}
