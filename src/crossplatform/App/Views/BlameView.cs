using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A read-only <c>git blame</c> view: one row per source line showing the
///  commit (short hash), author, final line number and the line text, in a
///  monospace multi-column list. Heavy git work runs off the UI thread, matching
///  <see cref="DiffView"/>. Built on a <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) is required.
/// </summary>
public sealed class BlameView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 160;
    private const double LineWidth = 60;

    private static readonly IBrush MetaBrush = Brushes.Gray;

    private readonly BlameService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;

    public BlameView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4),
            Foreground = Brushes.Gray,
            Text = "No file loaded.",
        };

        _list = new ListBox
        {
            FontFamily = Monospace,
            ItemTemplate = new FuncDataTemplate<BlameLineRow>((row, _) => BuildRow(row), supportsRecycling: true),
        };

        ScrollViewer scroll = new()
        {
            Content = _list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Control header = BuildHeader();

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(header);
        root.Children.Add(scroll);

        Content = root;
    }

    /// <summary>
    ///  Loads and displays the blame of <paramref name="filePath"/> in the
    ///  repository at <paramref name="repoPath"/> at <paramref name="commit"/>
    ///  (defaults to <c>HEAD</c> when null). Heavy git work runs off the UI thread.
    /// </summary>
    public void ShowBlame(string repoPath, string filePath, string? commit = null)
    {
        _list.ItemsSource = null;
        _status.Text = $"Blaming {filePath}…";

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<BlameLineRow> rows = _service.GetBlame(repoPath, filePath, commit);
                Dispatcher.UIThread.Post(() =>
                {
                    _list.ItemsSource = rows;
                    _status.Text = $"{filePath}  —  {rows.Count} line(s)  @ {commit ?? "HEAD"}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = "Error: " + ex.Message);
            }
        });
    }

    private static Grid MakeColumns()
        => new()
        {
            ColumnDefinitions = new ColumnDefinitions($"{HashWidth},{AuthorWidth},{LineWidth},*"),
        };

    private static Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 0, 8, 2);

        AddCell(grid, 0, "Hash", bold: true);
        AddCell(grid, 1, "Author", bold: true);
        AddCell(grid, 2, "Line", bold: true);
        AddCell(grid, 3, "Text", bold: true);

        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private static Control BuildRow(BlameLineRow row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 0, 8, 0);

        AddCell(grid, 0, row.ShortHash, foreground: MetaBrush);
        AddCell(grid, 1, row.Author, foreground: MetaBrush);
        AddCell(grid, 2, row.LineNumber.ToString(), foreground: MetaBrush);
        AddCell(grid, 3, row.Text, trim: false);

        return grid;
    }

    private static void AddCell(Grid grid, int column, string text, bool bold = false, bool trim = true, IBrush? foreground = null)
    {
        TextBlock block = new()
        {
            Text = text,
            FontFamily = Monospace,
            TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        };

        if (foreground is not null)
        {
            block.Foreground = foreground;
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
