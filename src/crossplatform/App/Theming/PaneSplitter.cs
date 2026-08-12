using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Where two panes meet: a 4 px grab strip that paints nothing, and a 1 px rule down
///  the middle of it.
/// </summary>
/// <remarks>
///  <para>A <see cref="GridSplitter"/> has to be about 4 px to be catchable with a
///  pointer, so giving it <c>Background = App.Border</c> — which four views did — draws
///  the boundary FOUR TIMES thicker than every other line in the app. Next to the 1 px
///  rules under a toolbar and around a text box it reads as a pale stripe laid across
///  the window rather than as a seam, and the commit dialog had already been fixed by
///  hand for exactly that reason. Splitting the two jobs keeps both: the splitter stays
///  4 px and transparent so the grab target is unchanged, and a sibling 1 px border in
///  the same cell says where the seam is.</para>
///
///  <para>The rule is added FIRST and is not hit-testable, so the splitter sits over it
///  and takes every pointer event; the cell is <c>Auto</c> in all four grids, so its
///  width still comes from the splitter and no layout moves.</para>
/// </remarks>
internal static class PaneSplitter
{
    /// <summary>
    ///  Adds <paramref name="splitter"/> to <paramref name="grid"/> together with its
    ///  rule, in whichever cell the caller has already assigned it to. The splitter's
    ///  own background is overwritten with <see cref="Brushes.Transparent"/> — not left
    ///  null, or the grip would stop taking the pointer.
    /// </summary>
    internal static void Add(Grid grid, GridSplitter splitter)
    {
        // Which way it runs, off the dimension the caller pinned: every splitter in the
        // port sets exactly one of Width/Height and leaves the other free. Reading it
        // back beats trusting ResizeDirection, which is left at Auto in half the call
        // sites and is only resolved once the splitter is in a live tree.
        bool vertical = !double.IsNaN(splitter.Width);

        Border rule = new()
        {
            Background = Icons.Tint("App.Rule") ?? SolidColorBrush.Parse("#3F3F46"),
            Width = vertical ? 1 : double.NaN,
            Height = vertical ? double.NaN : 1,
            HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        Grid.SetColumn(rule, Grid.GetColumn(splitter));
        Grid.SetRow(rule, Grid.GetRow(splitter));
        Grid.SetColumnSpan(rule, Grid.GetColumnSpan(splitter));
        Grid.SetRowSpan(rule, Grid.GetRowSpan(splitter));

        splitter.Background = Brushes.Transparent;

        grid.Children.Add(rule);
        grid.Children.Add(splitter);
    }
}
