// Real Avalonia implementation behind the System.Windows.Forms.MessageBox shim.
//
// Reproduces the parts of the WinForms message box the reusable core depends
// on: the button sets, the default button, the icon, the DialogResult mapping,
// Enter/Escape handling and the Ctrl+C "copy message" shortcut.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using WinForms = System.Windows.Forms;

namespace GitExtensions.Compat;

internal sealed class MessageBoxWindow : Window
{
    private readonly WinForms.MessageBoxButtons _buttons;
    private readonly string _text;
    private readonly string _caption;
    private readonly SelectableTextBlock _message;
    private WinForms.DialogResult _result;

    private MessageBoxWindow(string text, string caption, WinForms.MessageBoxButtons buttons, WinForms.MessageBoxIcon icon, WinForms.MessageBoxDefaultButton defaultButton)
    {
        _text = text ?? string.Empty;
        _caption = caption ?? string.Empty;
        _buttons = buttons;
        _result = EscapeResultFor(buttons);

        Title = string.IsNullOrEmpty(_caption) ? "Git Extensions" : _caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 380;
        MaxWidth = 680;
        Background = AvaloniaHost.Brush("App.Window", "#2B2B2B");

        IBrush textBrush = AvaloniaHost.Brush("App.Text", "#DCDCDC");
        IBrush panelBrush = AvaloniaHost.Brush("App.Panel", "#333333");
        IBrush borderBrush = AvaloniaHost.Brush("App.Border", "#454545");

        // --- message row: icon glyph + selectable text.
        Grid body = new()
        {
            Margin = new Thickness(20, 20, 20, 16),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };

        (string glyph, string color) = GlyphFor(icon);
        if (glyph.Length > 0)
        {
            TextBlock iconBlock = new()
            {
                Text = glyph,
                FontSize = 30,
                Foreground = Avalonia.Media.Brush.Parse(color),
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(iconBlock, 0);
            body.Children.Add(iconBlock);
        }

        _message = new SelectableTextBlock
        {
            Text = _text,
            Foreground = textBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_message, 1);
        body.Children.Add(_message);

        // WinForms message boxes copy caption + text on Ctrl+C. Handle it while
        // tunnelling so the shortcut works wherever focus sits — except when the
        // user has actually selected part of the message, where the normal
        // "copy selection" behavior is the more useful one.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // --- button strip.
        StackPanel strip = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(20, 12, 20, 16),
        };

        WinForms.DialogResult[] order = ResultsFor(buttons);
        int defaultIndex = defaultButton switch
        {
            WinForms.MessageBoxDefaultButton.Button2 => 1,
            WinForms.MessageBoxDefaultButton.Button3 => 2,
            _ => 0,
        };
        defaultIndex = Math.Clamp(defaultIndex, 0, order.Length - 1);

        Button? toFocus = null;
        for (int i = 0; i < order.Length; i++)
        {
            WinForms.DialogResult value = order[i];
            Button button = new()
            {
                Content = LabelFor(value),
                MinWidth = 88,
                Padding = new Thickness(14, 6),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = i == defaultIndex,
                IsCancel = value == EscapeResultFor(buttons),
            };
            button.Click += (_, _) =>
            {
                _result = value;
                Close();
            };

            if (i == defaultIndex)
            {
                toFocus = button;
            }

            strip.Children.Add(button);
        }

        Border footer = new()
        {
            Background = panelBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = strip,
        };

        // MinWidth on the Window is ignored under SizeToContent, so the WinForms-ish
        // minimum width has to live on the content root.
        DockPanel root = new() { MinWidth = 380 };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        Opened += (_, _) => toFocus?.Focus();
    }

    /// <summary>
    ///  Shows the dialog modally over <paramref name="owner"/> and resolves to the
    ///  WinForms <see cref="WinForms.DialogResult"/> the caller expects.
    /// </summary>
    internal static async Task<WinForms.DialogResult> ShowAsync(
        Window owner,
        string text,
        string caption,
        WinForms.MessageBoxButtons buttons,
        WinForms.MessageBoxIcon icon,
        WinForms.MessageBoxDefaultButton defaultButton)
    {
        MessageBoxWindow window = new(text, caption, buttons, icon, defaultButton);
        await window.ShowDialog(owner);
        return window._result;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        if (_message.SelectedText is { Length: > 0 })
        {
            // Let SelectableTextBlock copy just the selection.
            return;
        }

        string payload = string.IsNullOrEmpty(_caption)
            ? _text
            : $"{_caption}{Environment.NewLine}{Environment.NewLine}{_text}";
        WinForms.Clipboard.SetText(payload);
        e.Handled = true;
    }

    private static WinForms.DialogResult[] ResultsFor(WinForms.MessageBoxButtons buttons) => buttons switch
    {
        WinForms.MessageBoxButtons.OK => [WinForms.DialogResult.OK],
        WinForms.MessageBoxButtons.OKCancel => [WinForms.DialogResult.OK, WinForms.DialogResult.Cancel],
        WinForms.MessageBoxButtons.AbortRetryIgnore => [WinForms.DialogResult.Abort, WinForms.DialogResult.Retry, WinForms.DialogResult.Ignore],
        WinForms.MessageBoxButtons.YesNoCancel => [WinForms.DialogResult.Yes, WinForms.DialogResult.No, WinForms.DialogResult.Cancel],
        WinForms.MessageBoxButtons.YesNo => [WinForms.DialogResult.Yes, WinForms.DialogResult.No],
        WinForms.MessageBoxButtons.RetryCancel => [WinForms.DialogResult.Retry, WinForms.DialogResult.Cancel],
        _ => [WinForms.DialogResult.OK],
    };

    /// <summary>
    ///  Result produced by Escape / closing the window — matches what the
    ///  previous no-op shim returned, so existing callers keep their behavior
    ///  when the user dismisses the dialog.
    /// </summary>
    private static WinForms.DialogResult EscapeResultFor(WinForms.MessageBoxButtons buttons) => buttons switch
    {
        WinForms.MessageBoxButtons.OK => WinForms.DialogResult.OK,
        WinForms.MessageBoxButtons.OKCancel => WinForms.DialogResult.Cancel,
        WinForms.MessageBoxButtons.YesNo => WinForms.DialogResult.No,
        WinForms.MessageBoxButtons.YesNoCancel => WinForms.DialogResult.Cancel,
        WinForms.MessageBoxButtons.RetryCancel => WinForms.DialogResult.Cancel,
        WinForms.MessageBoxButtons.AbortRetryIgnore => WinForms.DialogResult.Abort,
        _ => WinForms.DialogResult.None,
    };

    private static string LabelFor(WinForms.DialogResult result) => result switch
    {
        WinForms.DialogResult.OK => "OK",
        WinForms.DialogResult.Cancel => "Cancel",
        WinForms.DialogResult.Abort => "Abort",
        WinForms.DialogResult.Retry => "Retry",
        WinForms.DialogResult.Ignore => "Ignore",
        WinForms.DialogResult.Yes => "Yes",
        WinForms.DialogResult.No => "No",
        _ => result.ToString(),
    };

    private static (string Glyph, string Color) GlyphFor(WinForms.MessageBoxIcon icon) => icon switch
    {
        WinForms.MessageBoxIcon.Error => ("⛔", "#E05252"),        // ⛔ (also Hand / Stop)
        WinForms.MessageBoxIcon.Warning => ("⚠", "#E0A030"),      // ⚠ (also Exclamation)
        WinForms.MessageBoxIcon.Question => ("❓", "#4C9AE0"),     // ❓
        WinForms.MessageBoxIcon.Information => ("ℹ", "#4C9AE0"),  // ℹ (also Asterisk)
        _ => (string.Empty, "#000000"),
    };
}
