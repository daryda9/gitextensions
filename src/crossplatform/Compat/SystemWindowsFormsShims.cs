// Minimal cross-platform stand-ins for the System.Windows.Forms surface used by
// the reusable Git Extensions core. These make the git-logic assemblies compile
// on Linux; the Avalonia front-end supplies real UI behavior separately and does
// not invoke these WinForms code paths.

using System.ComponentModel;
using System.Reflection;

namespace System.Windows.Forms;

public interface IWin32Window
{
    IntPtr Handle { get; }
}

public enum DialogResult
{
    None = 0,
    OK = 1,
    Cancel = 2,
    Abort = 3,
    Retry = 4,
    Ignore = 5,
    Yes = 6,
    No = 7,
}

public enum MessageBoxButtons
{
    OK = 0,
    OKCancel = 1,
    AbortRetryIgnore = 2,
    YesNoCancel = 3,
    YesNo = 4,
    RetryCancel = 5,
}

public enum MessageBoxIcon
{
    None = 0,
    Error = 16,
    Question = 32,
    Warning = 48,
    Information = 64,
    Hand = 16,
    Stop = 16,
    Exclamation = 48,
    Asterisk = 64,
}

public enum MessageBoxDefaultButton
{
    Button1 = 0,
    Button2 = 256,
    Button3 = 512,
}

public enum AutoScaleMode
{
    None = 0,
    Font = 1,
    Dpi = 2,
    Inherit = 3,
}

// A no-op base control. The reusable core only uses it as a parameter/field type
// and as a generic constraint; it never renders anything on Linux.
public enum CheckState { Unchecked = 0, Checked = 1, Indeterminate = 2 }

public enum BorderStyle { None = 0, FixedSingle = 1, Fixed3D = 2 }

public class Control : IWin32Window, IComponent
{
    public IntPtr Handle => IntPtr.Zero;
    public bool IsDisposed { get; private set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Visible { get; set; } = true;
    public object? Tag { get; set; }
    public ISite? Site { get; set; }
    public event EventHandler? Disposed;
    public virtual void Dispose()
    {
        IsDisposed = true;
        Disposed?.Invoke(this, EventArgs.Empty);
        GC.SuppressFinalize(this);
    }
}

public enum SystemColorMode { Classic = 0, System = 1, Dark = 2 }

public class ToolStripItem : IComponent
{
    public bool IsDisposed { get; private set; }
    public object? Tag { get; set; }
    public ISite? Site { get; set; }
    public event EventHandler? Disposed;
    public void Dispose()
    {
        IsDisposed = true;
        Disposed?.Invoke(this, EventArgs.Empty);
        GC.SuppressFinalize(this);
    }
}

public class ToolStripMenuItem : ToolStripItem
{
    public string Text { get; set; } = string.Empty;
    public bool Checked { get; set; }
}

public class ToolTip : Control
{
    public string ToolTipTitle { get; set; } = string.Empty;
    public string GetToolTip(Control control) => string.Empty;
    public void SetToolTip(Control control, string caption) { }
}

public class ListBox : Control
{
}

// No native folder picker in the shim; the Avalonia UI provides one. Returns
// Cancel so back-end callers behave as if the user dismissed the dialog.
public sealed class FolderBrowserDialog : IDisposable
{
    public string SelectedPath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool ShowNewFolderButton { get; set; } = true;
    public DialogResult ShowDialog() => DialogResult.Cancel;
    public DialogResult ShowDialog(IWin32Window? owner) => DialogResult.Cancel;
    public void Dispose() { }
}

public class Form : Control
{
    public static Form? ActiveForm => null;
}

// Additional no-op controls used only as parameter/field/generic-constraint types
// by the reusable settings-binding infrastructure.
public class TextBox : Control
{
    public string Text { get; set; } = string.Empty;
    public bool Multiline { get; set; }
    public bool ReadOnly { get; set; }
    public BorderStyle BorderStyle { get; set; }
}

public class ComboBox : Control
{
    public string Text { get; set; } = string.Empty;
    public int SelectedIndex { get; set; } = -1;
    public object? SelectedItem { get; set; }
}

public class CheckBox : Control
{
    public bool Checked { get; set; }
    public CheckState CheckState { get; set; }
}

public class ContextMenuStrip : Control
{
}

public class DataGridViewColumn
{
    public string? Name { get; set; }
    public string? HeaderText { get; set; }
    public bool Visible { get; set; } = true;
}

public static class MessageBox
{
    // On Linux there is no WinForms message box; the Avalonia UI shows dialogs.
    // These stand-ins keep non-interactive/back-end call sites working.
    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        => DefaultFor(buttons);

    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        => DefaultFor(buttons);

    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        => DefaultFor(buttons);

    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        => DefaultFor(buttons);

    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
        => DefaultFor(buttons);

    public static DialogResult Show(string text)
        => DialogResult.OK;

    private static DialogResult DefaultFor(MessageBoxButtons buttons) => buttons switch
    {
        MessageBoxButtons.OK => DialogResult.OK,
        MessageBoxButtons.OKCancel => DialogResult.Cancel,
        MessageBoxButtons.YesNo => DialogResult.No,
        MessageBoxButtons.YesNoCancel => DialogResult.Cancel,
        MessageBoxButtons.RetryCancel => DialogResult.Cancel,
        MessageBoxButtons.AbortRetryIgnore => DialogResult.Abort,
        _ => DialogResult.None,
    };
}

public sealed class TextRenderer
{
    public static Drawing.Size MeasureText(string? text, Drawing.Font? font)
    {
        // Rough estimate; no GDI on Linux. Good enough for layout fallbacks.
        int len = text?.Length ?? 0;
        float size = font?.Size ?? 9f;
        return new Drawing.Size((int)(len * size * 0.6f), (int)(size * 1.4f));
    }
}

// Replacement for WinForms Application static helpers used by AppSettings et al.
public static class Application
{
    public static string ExecutablePath { get; } =
        Environment.ProcessPath
        ?? Assembly.GetEntryAssembly()?.Location
        ?? AppContext.BaseDirectory;

    public static string ProductName { get; } =
        Assembly.GetEntryAssembly()?.GetName().Name ?? "GitExtensions";

    public static string ProductVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0";

    // Best-effort dark/light detection is left to the Avalonia host; default light.
    public static SystemColorMode SystemColorMode { get; set; } = SystemColorMode.Classic;

    public static string UserAppDataPath { get; } =
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProductName,
            ProductVersion);

    /// <summary>Unhandled-exception sink. The Avalonia host can subscribe.</summary>
    public static event Action<Exception>? ThreadException;

    public static void OnThreadException(Exception t)
    {
        if (ThreadException is not null)
        {
            ThreadException(t);
        }
        else
        {
            Diagnostics.Trace.TraceError(t.ToString());
        }
    }
}
