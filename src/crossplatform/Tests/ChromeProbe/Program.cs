using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

// This project inherits the WinForms-shim implicit usings, so a bare Screen is
// System.Windows.Forms'.
using Screen = Avalonia.Platform.Screen;

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

// How much of a MAXIMISED extended-client-area window falls outside the usable screen.
// This is what decides whether the content needs padding, and by how much.
{
    Window window = new()
    {
        Width = 400,
        Height = 300,
        ShowInTaskbar = false,
        SystemDecorations = SystemDecorations.Full,
        ExtendClientAreaToDecorationsHint = true,
        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
        ExtendClientAreaTitleBarHeightHint = -1,
    };

    Console.WriteLine();
    window.Show();
    ChromeProbe.Maximized.Report(window, "extended, normal");

    // WHEN each value lands matters as much as what it is: if OffScreenMargin is still
    // zero at the moment WindowState changes, then compensating from a WindowState
    // handler reads the stale value and the padding never appears.
    Console.WriteLine();
    Console.WriteLine("    property changes while maximising, in order:");
    window.PropertyChanged += (_, e) =>
    {
        // OffScreenMargin has no public AvaloniaProperty to compare against, so every
        // change is logged with the margin's value at that instant; the ordering is what
        // is being read here.
        Console.WriteLine(
            $"      {e.Property.Name,-22} (offScreenMargin now {window.OffScreenMargin})");
    };

    window.WindowState = WindowState.Maximized;

    // The margin is published as the window is re-laid out, not synchronously with the
    // state change, so let the layout pass run before reading it.
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    ChromeProbe.Maximized.Report(window, "extended, maximised");

    window.Close();
}

// Where the top-left of the CONTENT actually lands on screen when maximised, with and
// without the OffScreenMargin compensation. This is the question behind "is there any
// padding": the window overhangs the screen, so content that is not inset by the same
// amount has its first few pixels above the top edge.
foreach (bool compensate in new[] { false, true })
{
    Border content = new() { Background = Avalonia.Media.Brushes.Red };
    Panel layered = new() { Children = { content } };

    Window window = new()
    {
        Width = 400,
        Height = 300,
        ShowInTaskbar = false,
        SystemDecorations = SystemDecorations.Full,
        ExtendClientAreaToDecorationsHint = true,
        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome,
        ExtendClientAreaTitleBarHeightHint = -1,
        Content = layered,
    };

    window.Show();
    window.WindowState = WindowState.Maximized;
    if (compensate)
    {
        layered.Margin = window.OffScreenMargin;
    }

    Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    PixelPoint onScreen = window.PointToScreen(
        content.TranslatePoint(default, window) ?? default);
    Screen? screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
    int top = screen is null ? 0 : onScreen.Y - screen.WorkingArea.Y;

    Console.WriteLine();
    Console.WriteLine($"--- maximised, compensation {(compensate ? "ON " : "OFF")}");
    Console.WriteLine($"    content top-left on screen : {onScreen}");
    Console.WriteLine($"    vs top of working area     : {top:+#;-#;0} px  "
        + (top < 0 ? "<- CLIPPED, above the screen" : top == 0 ? "<- flush with the edge" : "<- inset"));

    window.Close();
}

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
