using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A read-only modal that renders a patch / diff file with the SAME colouring
///  used by <see cref="DiffView"/>: added lines green, removed lines red, hunk
///  headers in the accent colour, and file/meta headers dimmed. Styled from the
///  shared App.* brushes so it matches the active (dark) theme.
/// </summary>
public sealed class PatchViewerWindow : Window
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    // Same line colours as DiffView so the patch reads identically — now from the
    // shared theme keys rather than duplicated dark-palette literals, which measured
    // 1.88:1 and 2.90:1 against the light theme's background.
    private static IBrush? _addedBrush;
    private static IBrush? _removedBrush;

    private static IBrush AddedBrush =>
        _addedBrush ??= (IBrush)Application.Current!.Resources["App.DiffAdded"]!;

    private static IBrush RemovedBrush =>
        _removedBrush ??= (IBrush)Application.Current!.Resources["App.DiffRemoved"]!;

    public PatchViewerWindow(string title, string patchText)
    {
        IBrush window = (IBrush)Application.Current!.Resources["App.Window"]!;
        IBrush text = B("App.Text", "#DCDCDC");

        Title = title;
        Width = 900;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        SelectableTextBlock body = new()
        {
            FontFamily = Monospace,
            FontSize = 12,
            Foreground = text,
            Margin = new Thickness(12, 10, 12, 12),
            TextWrapping = TextWrapping.NoWrap,
        };
        RenderPatch(body, patchText);

        ScrollViewer scroll = new()
        {
            Content = body,
            Background = window,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Button close = new() { Content = "Close", MinWidth = 80, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        DockPanel root = new() { Background = window };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 12),
            Children = { close },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(scroll);

        Content = root;
        DialogKeys.InstallEscapeClose(this);
    }

    // Colour each patch line, mirroring DiffView.RenderDiff exactly.
    private static void RenderPatch(SelectableTextBlock target, string patchText)
    {
        target.Text = string.Empty;
        InlineCollection inlines = target.Inlines ??= [];
        inlines.Clear();

        foreach (string line in patchText.Replace("\r\n", "\n").Split('\n'))
        {
            IBrush? brush = null;

            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("new file", StringComparison.Ordinal) ||
                line.StartsWith("deleted file", StringComparison.Ordinal) ||
                line.StartsWith("rename ", StringComparison.Ordinal) ||
                line.StartsWith("copy ", StringComparison.Ordinal) ||
                line.StartsWith("similarity ", StringComparison.Ordinal) ||
                line.StartsWith("From ", StringComparison.Ordinal) ||
                line.StartsWith("Subject:", StringComparison.Ordinal) ||
                line.StartsWith("Date:", StringComparison.Ordinal) ||
                line.StartsWith("Author:", StringComparison.Ordinal))
            {
                brush = B("App.TextDim", "#9A9A9A");
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                brush = B("App.Accent", "#4A9ED6");
            }
            else if (line.StartsWith('+'))
            {
                brush = AddedBrush;
            }
            else if (line.StartsWith('-'))
            {
                brush = RemovedBrush;
            }

            Run run = new(line + "\n");
            if (brush is not null)
            {
                run.Foreground = brush;
            }

            inlines.Add(run);
        }
    }

    private static IBrush B(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}

/// <summary>
///  A read-only modal for plain git output (e.g. the result of <c>git am</c> /
///  <c>git apply</c>), shown in a monospace scrollable pane. Used to surface the
///  full message when a patch apply fails.
/// </summary>
public sealed class PatchOutputWindow : Window
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    public PatchOutputWindow(string title, string outputText)
    {
        IBrush window = (IBrush)Application.Current!.Resources["App.Window"]!;
        IBrush text = B("App.Text", "#DCDCDC");

        Title = title;
        Width = 720;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        SelectableTextBlock body = new()
        {
            FontFamily = Monospace,
            FontSize = 12,
            Foreground = text,
            Margin = new Thickness(12, 10, 12, 12),
            TextWrapping = TextWrapping.Wrap,
            Text = outputText,
        };

        ScrollViewer scroll = new()
        {
            Content = body,
            Background = window,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Button close = new() { Content = "Close", MinWidth = 80, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();

        DockPanel root = new() { Background = window };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 12),
            Children = { close },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(scroll);

        Content = root;
        DialogKeys.InstallEscapeClose(this);
    }

    private static IBrush B(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
