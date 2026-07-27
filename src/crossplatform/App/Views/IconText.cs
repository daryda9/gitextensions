using Avalonia.Controls;
using Avalonia.Layout;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Builds the "16px icon + caption" header used by the bottom-panel tabs, mirroring
///  the original <c>FormBrowse</c> tab strip (whose <c>CommitInfoTabControl</c> carries
///  an <c>ImageList</c> and an <c>ImageKey</c> per page). Kept in one place so every
///  caller degrades the same way when an icon name is not in the assets: a missing
///  icon simply yields the caption on its own, never an empty header.
/// </summary>
internal static class IconText
{
    /// <summary>
    ///  Header content for <paramref name="text"/> prefixed by the
    ///  <paramref name="icon"/> asset, or the plain caption when the icon is absent.
    /// </summary>
    internal static object Header(string? icon, string text)
    {
        if (icon is null || IconLoader.Image(icon, 16) is not { } img)
        {
            return text;
        }

        img.VerticalAlignment = VerticalAlignment.Center;

        // Horizontal by nature (an icon beside its caption) but never width-clamped:
        // the caption keeps its own measured width, so longer translations just make
        // the tab wider instead of being cut off.
        StackPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }
}
