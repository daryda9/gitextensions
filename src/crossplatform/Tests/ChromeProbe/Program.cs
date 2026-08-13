using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

// Which ways of getting a client-side title bar keep Windows' drag-to-the-top tiling.
//
// Tiling (Aero Snap, Snap Layouts) is a NON-CLIENT behaviour: the move loop offers it
// only to a window that still has WS_CAPTION and WS_THICKFRAME. BeginMoveDrag has
// nothing to do with it, which is why the symptom looked so odd -- the window dragged
// perfectly well and simply refused to snap.
//
// The merged title bar used to be obtained with SystemDecorations.None, which strips
// both styles. This checks the three arrangements against the actual Win32 styles, so
// the choice in MainWindow.ApplyWindowChrome rests on a measurement rather than on a
// belief about what the backend does.
//
// It configures plain windows the same way ApplyWindowChrome configures the real one;
// it does not construct MainWindow, which is internal and would start loading a
// repository. Exit code 0 = the arrangement the port uses can tile.

const int GWL_STYLE = -16;
const uint WS_CAPTION = 0x00C00000;
const uint WS_THICKFRAME = 0x00040000;

AppBuilder.Configure<GitExtensions.Avalonia.App>()
    .UsePlatformDetect()
    .SetupWithoutStarting();

// The separate title bar: the reference, tiling has always worked here.
bool full = Check("Full", new Window { Width = 300, Height = 200 });

// What the merged bar did before, and still does on X11 — where the backend ignores
// the hint below, so there is no alternative to removing the frame.
bool none = Check("None", new Window { Width = 300, Height = 200, SystemDecorations = SystemDecorations.None });

// What the merged bar now does on Windows.
bool extended = Check(
    "ExtendClientArea",
    new Window
    {
        Width = 300,
        Height = 200,
        SystemDecorations = SystemDecorations.Full,
        ExtendClientAreaToDecorationsHint = true,
        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
        ExtendClientAreaTitleBarHeightHint = -1,
    });

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("not Windows: tiling is the window manager's business, nothing to assert");
    Environment.Exit(0);
}

Console.WriteLine();
Console.WriteLine(full && !none && extended
    ? "RESULT: OK - the merged bar keeps the frame on Windows (and None still would not)"
    : "RESULT: UNEXPECTED - re-read the table above before trusting ApplyWindowChrome");
Console.Out.Flush();
Environment.Exit(full && extended ? 0 : 1);

static bool Check(string label, Window window)
{
    window.ShowInTaskbar = false;
    window.Show();

    bool tiles = false;
    string styles = "(not Windows)";

    if (OperatingSystem.IsWindows()
        && window.TryGetPlatformHandle() is { } handle
        && handle.Handle != IntPtr.Zero)
    {
        uint style = (uint)GetWindowLongPtr(handle.Handle, GWL_STYLE);
        bool caption = (style & WS_CAPTION) != 0;
        bool sizing = (style & WS_THICKFRAME) != 0;
        tiles = caption && sizing;
        styles = $"caption={caption,-5} sizing={sizing,-5}";
    }

    Console.WriteLine(
        $"{label,-17} extended={window.IsExtendedIntoWindowDecorations,-5} {styles} tiling={(tiles ? "YES" : "NO")}");

    window.Close();
    return tiles;
}

[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
