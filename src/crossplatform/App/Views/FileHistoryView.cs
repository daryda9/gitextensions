using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A read-only view of a single file's commit history: a multi-column list
///  (Hash / Author / Date / Subject) of the commits that touched the file,
///  following it across renames. Heavy git work runs off the UI thread, matching
///  <see cref="DiffView"/>. Built on a <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) is required.
/// </summary>
public sealed class FileHistoryView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 170;
    private const double DateWidth = 130;

    private readonly FileHistoryService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    public FileHistoryView()
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
            ItemTemplate = new FuncDataTemplate<FileHistoryRow>((row, _) => BuildRow(row), supportsRecycling: true),
        };

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is FileHistoryRow row)
            {
                RevisionSelected?.Invoke(row.Hash);
            }
        };

        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && _list.SelectedItem is FileHistoryRow row)
            {
                _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(row.Hash);
                e.Handled = true;
            }
        };

        Control header = BuildHeader();

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(header);
        root.Children.Add(_list);

        Content = root;
    }

    /// <summary>
    ///  Loads and displays the commit history of <paramref name="filePath"/> in
    ///  the repository at <paramref name="repoPath"/>. Heavy git work runs off the
    ///  UI thread.
    /// </summary>
    public void ShowHistory(string repoPath, string filePath)
    {
        _list.ItemsSource = null;
        _status.Text = $"Loading history of {filePath}…";

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<FileHistoryRow> rows = _service.GetHistory(repoPath, filePath);
                Dispatcher.UIThread.Post(() =>
                {
                    _list.ItemsSource = rows;
                    _status.Text = $"{filePath}  —  {rows.Count} commit(s)";
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
            ColumnDefinitions = new ColumnDefinitions($"{HashWidth},{AuthorWidth},{DateWidth},*"),
        };

    private static Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 0, 8, 2);

        AddCell(grid, 0, "Hash", bold: true);
        AddCell(grid, 1, "Author", bold: true);
        AddCell(grid, 2, "Date", bold: true);
        AddCell(grid, 3, "Subject", bold: true);

        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private static Control BuildRow(FileHistoryRow row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 1, 8, 1);

        AddCell(grid, 0, row.ShortHash);
        AddCell(grid, 1, row.Author);
        AddCell(grid, 2, row.Date);
        AddCell(grid, 3, row.Subject);

        return grid;
    }

    private static void AddCell(Grid grid, int column, string text, bool bold = false)
    {
        TextBlock block = new()
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        };

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
