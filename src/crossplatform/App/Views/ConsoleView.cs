using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A minimal stand-in for the original browse window's "Console" tab. Rather
///  than embedding a real PTY, it offers a short note and an "Open terminal here"
///  button that raises <see cref="OpenTerminalRequested"/>; the host wires that to
///  the external-tool terminal launcher for the current repository.
/// </summary>
public sealed class ConsoleView : UserControl
{
    /// <summary>Raised when the user asks to open a terminal in the current repo.</summary>
    public event Action? OpenTerminalRequested;

    public ConsoleView()
    {
        TextBlock title = new()
        {
            Text = "Integrated terminal",
            FontWeight = FontWeight.Bold,
            FontSize = 15,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        TextBlock note = new()
        {
            Text = "Open a system terminal in the current repository directory.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
            Foreground = Brush("App.TextDim", Brushes.Gray),
        };

        Button open = new()
        {
            Content = "Open terminal here",
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Brush("App.Control", Brushes.DimGray),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        open.Click += (_, _) => OpenTerminalRequested?.Invoke();

        StackPanel panel = new()
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Top,
            Children = { title, note, open },
        };

        Content = panel;
        Background = Brush("App.Window", Brushes.DimGray);
        ClipToBounds = true;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
