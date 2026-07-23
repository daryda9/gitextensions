using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Small modal "About" dialog for the Avalonia / Linux port: app title and
///  subtitle, a one-paragraph description, and a few runtime facts (.NET
///  version, OS, UI toolkit). Shown with <see cref="ShowAsync"/> and closed by
///  the default/cancel button. Colors come from the shared dark palette in
///  <c>App.cs</c> (<c>App.Window</c>/<c>App.Text</c>/<c>App.TextDim</c>).
/// </summary>
public sealed class AboutDialog : Window
{
    public AboutDialog()
    {
        IBrush text = Resource("App.Text", "#DCDCDC");
        IBrush dim = Resource("App.TextDim", "#9B9B9B");

        Title = "About Git Extensions";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Resource("App.Window", "#1E1E1E");

        StackPanel header = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Optional app icon; degrade gracefully when it is not linked in.
        Image? icon = IconLoader.Image("GitExtensions", 48);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(icon);
        }

        StackPanel titles = new() { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "Git Extensions",
            Foreground = text,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
        });
        titles.Children.Add(new TextBlock
        {
            Text = "Avalonia / Linux port",
            Foreground = dim,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(titles);

        TextBlock description = new()
        {
            Text = "A cross-platform graphical user interface for Git, "
                 + "reusing the Git Extensions core logic on top of a native "
                 + "Avalonia UI so it runs on Linux.",
            Foreground = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        };

        StackPanel info = new() { Margin = new Thickness(0, 16, 0, 0), Spacing = 4 };
        info.Children.Add(InfoLine(".NET", Environment.Version.ToString(), text, dim));
        info.Children.Add(InfoLine("OS", RuntimeInformation.OSDescription, text, dim));
        info.Children.Add(InfoLine("UI toolkit", "Avalonia", text, dim));

        Button close = new()
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 80,
        };
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(close);

        StackPanel root = new() { Margin = new Thickness(20) };
        root.Children.Add(header);
        root.Children.Add(description);
        root.Children.Add(info);
        root.Children.Add(buttons);

        Content = root;
    }

    /// <summary>Shows the About dialog modally over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner)
        => new AboutDialog().ShowDialog(owner);

    private static Control InfoLine(string label, string value, IBrush text, IBrush dim)
    {
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new TextBlock
        {
            Text = label + ":",
            Foreground = dim,
            FontSize = 12,
            Width = 80,
        });
        line.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        return line;
    }

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
