using System.Runtime.InteropServices;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Receives file/folder drops on X11, because Avalonia cannot.
///
///  <para><b>Why this exists.</b> Avalonia 11.3's X11 backend implements no part of
///  the XDND protocol: <c>Avalonia.X11</c> never interns an <c>Xdnd*</c> atom and
///  never sets <c>XdndAware</c> on its toplevels, so an external drag is not even
///  offered to the window and <c>DragDrop.DropEvent</c> can never fire here. (The
///  upstream implementation was merged for Avalonia 12.1 and explicitly marked
///  wont-backport.) The managed <c>DragDrop</c> handlers in <c>MainWindow</c> are
///  kept for the day that lands and for the other backends; this class is what
///  actually makes a drop work on Linux today. It is the same class of defect the
///  HANDOFF warns about — a dependency that quietly assumes Windows/macOS.</para>
///
///  <para><b>How.</b> A drag source addresses whichever window advertises
///  <c>XdndAware</c>, but XDND also defines <c>XdndProxy</c>: a window may name
///  another window to receive the protocol messages on its behalf. Since messages
///  sent to Avalonia's window would be delivered to Avalonia's X connection (which
///  drops them), this class opens its own connection, creates a 1×1 unmapped proxy
///  window, and publishes <c>XdndAware</c> + <c>XdndProxy</c> on the real toplevel.
///  The source then talks to the proxy, on our connection, where we can answer.</para>
///
///  <para><b>Threading.</b> Everything runs on one dedicated background thread with
///  its own <c>Display</c>; the callback is invoked from that thread, so the caller
///  must marshal to the UI thread itself. Every failure mode (no X11, no libX11, a
///  malformed message) ends in a silent no-op: drops simply stop working, exactly
///  as they did before, and nothing else in the app notices.</para>
/// </summary>
public sealed class X11DropTarget : IDisposable
{
    private const string LibX11 = "libX11.so.6";

    // X protocol event types we care about.
    private const int SelectionNotify = 31;
    private const int ClientMessage = 33;

    // Size of the largest XEvent; the union is read field by field at fixed offsets
    // rather than mirrored as a struct, which keeps the marshalling honest.
    private const int EventSize = 192;

    private readonly Action<IReadOnlyList<string>> _onDrop;
    private readonly IntPtr _toplevel;
    private readonly Thread _thread;
    private volatile bool _stop;

    private IntPtr _display;
    private IntPtr _proxy;

    private IntPtr _aXdndAware;
    private IntPtr _aXdndProxy;
    private IntPtr _aXdndEnter;
    private IntPtr _aXdndPosition;
    private IntPtr _aXdndStatus;
    private IntPtr _aXdndLeave;
    private IntPtr _aXdndDrop;
    private IntPtr _aXdndFinished;
    private IntPtr _aXdndSelection;
    private IntPtr _aXdndActionCopy;
    private IntPtr _aXdndTypeList;
    private IntPtr _aUriList;
    private IntPtr _aCardinal;
    private IntPtr _aWindow;
    private IntPtr _aAtom;
    private IntPtr _aProperty;

    private IntPtr _source;
    private bool _offersUris;

    private X11DropTarget(IntPtr toplevel, Action<IReadOnlyList<string>> onDrop)
    {
        _toplevel = toplevel;
        _onDrop = onDrop;
        _thread = new Thread(Run) { IsBackground = true, Name = "xdnd-drop-target" };
    }

    /// <summary>
    ///  Starts advertising drop support for the given native window handle.
    ///  Returns null when this is not an X11 session, when libX11 is unavailable,
    ///  or on any set-up error — the caller carries on without drag and drop.
    /// </summary>
    public static X11DropTarget? TryCreate(IntPtr toplevel, Action<IReadOnlyList<string>> onDrop)
    {
        if (toplevel == IntPtr.Zero || !OperatingSystem.IsLinux())
        {
            return null;
        }

        X11DropTarget target = new(toplevel, onDrop);
        try
        {
            if (!target.Initialize())
            {
                target.Dispose();
                return null;
            }
        }
        catch (Exception)
        {
            // DllNotFoundException on a machine without libX11, EntryPointNotFound
            // on an exotic build — either way, no drops, no noise.
            target.Dispose();
            return null;
        }

        target._thread.Start();
        return target;
    }

    public void Dispose()
    {
        _stop = true;

        try
        {
            if (_thread.IsAlive)
            {
                _thread.Join(500);
            }
        }
        catch
        {
            // Shutting down anyway.
        }
    }

    private bool Initialize()
    {
        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
        {
            return false;
        }

        _aXdndAware = Atom("XdndAware");
        _aXdndProxy = Atom("XdndProxy");
        _aXdndEnter = Atom("XdndEnter");
        _aXdndPosition = Atom("XdndPosition");
        _aXdndStatus = Atom("XdndStatus");
        _aXdndLeave = Atom("XdndLeave");
        _aXdndDrop = Atom("XdndDrop");
        _aXdndFinished = Atom("XdndFinished");
        _aXdndSelection = Atom("XdndSelection");
        _aXdndActionCopy = Atom("XdndActionCopy");
        _aXdndTypeList = Atom("XdndTypeList");
        _aUriList = Atom("text/uri-list");
        _aCardinal = Atom("CARDINAL");
        _aWindow = Atom("WINDOW");
        _aAtom = Atom("ATOM");
        _aProperty = Atom("GitExtensionsAvaloniaDrop");

        IntPtr root = XDefaultRootWindow(_display);
        _proxy = XCreateSimpleWindow(_display, root, -100, -100, 1, 1, 0, UIntPtr.Zero, UIntPtr.Zero);
        if (_proxy == IntPtr.Zero)
        {
            return false;
        }

        // XDND version 5 on both windows, and the proxy pointing at itself: the
        // spec asks for the property on the proxy too, so a source can detect a
        // stale proxy left behind by a dead client.
        SetLongProperty(_proxy, _aXdndAware, _aCardinal, 5);
        SetLongProperty(_proxy, _aXdndProxy, _aWindow, _proxy.ToInt64());

        // …and on the window the user actually drags onto.
        SetLongProperty(_toplevel, _aXdndAware, _aCardinal, 5);
        SetLongProperty(_toplevel, _aXdndProxy, _aWindow, _proxy.ToInt64());

        XFlush(_display);
        return true;
    }

    private IntPtr Atom(string name) => XInternAtom(_display, name, false);

    private void SetLongProperty(IntPtr window, IntPtr property, IntPtr type, long value)
    {
        long[] data = [value];
        XChangeProperty(_display, window, property, type, 32, PropModeReplace, data, 1);
    }

    private const int PropModeReplace = 0;

    // ---- event loop --------------------------------------------------------------

    private void Run()
    {
        byte[] buffer = new byte[EventSize];

        try
        {
            while (!_stop)
            {
                if (XPending(_display) == 0)
                {
                    Thread.Sleep(30);
                    continue;
                }

                XNextEvent(_display, buffer);
                Handle(buffer);
            }
        }
        catch
        {
            // A broken connection (the display went away) just ends the loop.
        }
        finally
        {
            try
            {
                if (_display != IntPtr.Zero)
                {
                    XCloseDisplay(_display);
                    _display = IntPtr.Zero;
                }
            }
            catch
            {
                // Nothing left to do.
            }
        }
    }

    private void Handle(byte[] e)
    {
        int type = BitConverter.ToInt32(e, 0);
        if (type != ClientMessage)
        {
            return;
        }

        IntPtr messageType = ReadPtr(e, 40);
        long d0 = ReadLong(e, 56);
        long d1 = ReadLong(e, 64);
        long d2 = ReadLong(e, 72);

        if (messageType == _aXdndEnter)
        {
            _source = new IntPtr(d0);
            _offersUris = OffersUriList(e);
            return;
        }

        if (messageType == _aXdndPosition)
        {
            // Answer every position with our verdict; the source uses it to draw
            // the "can drop here" cursor. Rectangle 0 = ask again on every move.
            SendClientMessage(_source, _aXdndStatus,
                _toplevel.ToInt64(),
                _offersUris ? 1 : 0,
                0,
                0,
                _offersUris ? _aXdndActionCopy.ToInt64() : 0);
            return;
        }

        if (messageType == _aXdndLeave)
        {
            _source = IntPtr.Zero;
            _offersUris = false;
            return;
        }

        if (messageType == _aXdndDrop)
        {
            IntPtr time = new(d2);
            bool handled = false;
            try
            {
                if (_offersUris && _source != IntPtr.Zero)
                {
                    handled = RequestAndDeliver(time);
                }
            }
            catch
            {
                // Report the drop as unhandled rather than propagate.
            }

            SendClientMessage(_source, _aXdndFinished,
                _toplevel.ToInt64(),
                handled ? 1 : 0,
                handled ? _aXdndActionCopy.ToInt64() : 0,
                0,
                0);

            _source = IntPtr.Zero;
            _offersUris = false;
            _ = d1;
        }
    }

    // XdndEnter carries up to three types inline; more than three live in the
    // source's XdndTypeList property (flagged by bit 0 of data.l[1]).
    private bool OffersUriList(byte[] e)
    {
        long flags = ReadLong(e, 64);
        for (int i = 0; i < 3; i++)
        {
            if (ReadLong(e, 72 + (i * 8)) == _aUriList.ToInt64())
            {
                return true;
            }
        }

        if ((flags & 1) == 0)
        {
            return false;
        }

        foreach (long atom in ReadAtomList(_source, _aXdndTypeList))
        {
            if (atom == _aUriList.ToInt64())
            {
                return true;
            }
        }

        return false;
    }

    // Asks for the dragged data, waits briefly for the SelectionNotify that answers
    // it, and hands the parsed paths to the app.
    private bool RequestAndDeliver(IntPtr time)
    {
        XConvertSelection(_display, _aXdndSelection, _aUriList, _aProperty, _proxy, time);
        XFlush(_display);

        byte[] buffer = new byte[EventSize];
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (XPending(_display) == 0)
            {
                Thread.Sleep(20);
                continue;
            }

            XNextEvent(_display, buffer);
            if (BitConverter.ToInt32(buffer, 0) != SelectionNotify)
            {
                // Anything else arriving mid-transfer is still protocol traffic.
                Handle(buffer);
                continue;
            }

            IntPtr property = ReadPtr(buffer, 56);
            if (property == IntPtr.Zero)
            {
                return false;
            }

            string text = ReadTextProperty(_proxy, property);
            IReadOnlyList<string> paths = ParseUriList(text);
            if (paths.Count == 0)
            {
                return false;
            }

            _onDrop(paths);
            return true;
        }

        return false;
    }

    /// <summary>
    ///  Turns a <c>text/uri-list</c> payload into local paths, skipping comments
    ///  and anything that is not a local <c>file:</c> URI.
    /// </summary>
    internal static IReadOnlyList<string> ParseUriList(string text)
    {
        List<string> paths = [];
        if (string.IsNullOrEmpty(text))
        {
            return paths;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                paths.Add(new Uri(line).LocalPath);
            }
            catch (UriFormatException)
            {
                // Not a URI we can use; ignore this entry.
            }
        }

        return paths;
    }

    // ---- property / message helpers ------------------------------------------------

    private string ReadTextProperty(IntPtr window, IntPtr property)
    {
        if (XGetWindowProperty(_display, window, property, IntPtr.Zero, new IntPtr(0x100000), true,
                AnyPropertyType, out _, out int format, out IntPtr items, out _, out IntPtr data) != 0
            || data == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            int count = format == 8 ? (int)items : 0;
            if (count <= 0)
            {
                return string.Empty;
            }

            byte[] bytes = new byte[count];
            Marshal.Copy(data, bytes, 0, count);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            XFree(data);
        }
    }

    private long[] ReadAtomList(IntPtr window, IntPtr property)
    {
        if (window == IntPtr.Zero
            || XGetWindowProperty(_display, window, property, IntPtr.Zero, new IntPtr(1024), false,
                _aAtom, out _, out int format, out IntPtr items, out _, out IntPtr data) != 0
            || data == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            if (format != 32)
            {
                return [];
            }

            int count = (int)items;
            long[] atoms = new long[count];

            // Format 32 means "long" to Xlib, i.e. 64 bit here.
            for (int i = 0; i < count; i++)
            {
                atoms[i] = Marshal.ReadInt64(data, i * 8);
            }

            return atoms;
        }
        finally
        {
            XFree(data);
        }
    }

    private void SendClientMessage(IntPtr window, IntPtr messageType, long d0, long d1, long d2, long d3, long d4)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        byte[] e = new byte[EventSize];
        BitConverter.GetBytes(ClientMessage).CopyTo(e, 0);
        BitConverter.GetBytes(1).CopyTo(e, 16);            // send_event
        BitConverter.GetBytes(_display.ToInt64()).CopyTo(e, 24);
        BitConverter.GetBytes(window.ToInt64()).CopyTo(e, 32);
        BitConverter.GetBytes(messageType.ToInt64()).CopyTo(e, 40);
        BitConverter.GetBytes(32).CopyTo(e, 48);           // format
        BitConverter.GetBytes(d0).CopyTo(e, 56);
        BitConverter.GetBytes(d1).CopyTo(e, 64);
        BitConverter.GetBytes(d2).CopyTo(e, 72);
        BitConverter.GetBytes(d3).CopyTo(e, 80);
        BitConverter.GetBytes(d4).CopyTo(e, 88);

        XSendEvent(_display, window, false, IntPtr.Zero, e);
        XFlush(_display);
    }

    private static long ReadLong(byte[] buffer, int offset) => BitConverter.ToInt64(buffer, offset);

    private static IntPtr ReadPtr(byte[] buffer, int offset) => new(BitConverter.ToInt64(buffer, offset));

    private static readonly IntPtr AnyPropertyType = IntPtr.Zero;

    // ---- libX11 ---------------------------------------------------------------------

    [DllImport(LibX11)]
    private static extern IntPtr XOpenDisplay(string? display);

    [DllImport(LibX11)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);

    [DllImport(LibX11)]
    private static extern IntPtr XCreateSimpleWindow(
        IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, UIntPtr border, UIntPtr background);

    [DllImport(LibX11)]
    private static extern int XChangeProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format,
        int mode, long[] data, int elements);

    [DllImport(LibX11)]
    private static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length,
        bool delete, IntPtr requestedType, out IntPtr actualType, out int actualFormat,
        out IntPtr items, out IntPtr bytesAfter, out IntPtr data);

    [DllImport(LibX11)]
    private static extern int XConvertSelection(
        IntPtr display, IntPtr selection, IntPtr target, IntPtr property,
        IntPtr requestor, IntPtr time);

    [DllImport(LibX11)]
    private static extern int XSendEvent(
        IntPtr display, IntPtr window, bool propagate, IntPtr mask, byte[] send);

    [DllImport(LibX11)]
    private static extern int XPending(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XNextEvent(IntPtr display, byte[] eventReturn);

    [DllImport(LibX11)]
    private static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    private static extern int XFree(IntPtr data);
}
