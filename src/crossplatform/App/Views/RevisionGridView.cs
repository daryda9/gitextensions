using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A commit-list view (revision grid) for the Avalonia/Linux port. Loads the
///  recent history of a repository off the UI thread and renders it as a
///  multi-column list (Hash / Author / Date / Subject, with ref names shown
///  inline). Uses a <see cref="ListBox"/> with a templated multi-column row so
///  no extra NuGet package (e.g. DataGrid) or theme registration is required.
/// </summary>
public sealed class RevisionGridView : UserControl
{
    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 170;
    private const double DateWidth = 130;

    private readonly RevisionService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    public RevisionGridView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4),
            Foreground = Brushes.Gray,
            Text = "No repository loaded.",
        };

        Control header = BuildHeader();

        _list = new ListBox
        {
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
            ItemTemplate = new FuncDataTemplate<RevisionRow>((row, _) => BuildRow(row), supportsRecycling: true),
        };

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is RevisionRow row)
            {
                RevisionSelected?.Invoke(row.Hash);
            }
        };

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(header);
        root.Children.Add(_list);

        Content = root;
    }

    /// <summary>
    ///  Loads and displays the recent revisions of the repository at
    ///  <paramref name="repoPath"/>. Heavy git work runs off the UI thread.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _list.ItemsSource = null;
        _status.Text = "Loading…";

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<RevisionRow> rows = _service.LoadRevisions(repoPath);
                Dispatcher.UIThread.Post(() =>
                {
                    _list.ItemsSource = rows;
                    _status.Text = $"{repoPath}  —  {rows.Count} commits";
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
            ColumnDefinitions = new ColumnDefinitions(
                $"{HashWidth},{AuthorWidth},{DateWidth},*"),
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

    private static Control BuildRow(RevisionRow row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(0, 1, 0, 1);

        AddCell(grid, 0, row.ShortHash);
        AddCell(grid, 1, row.Author);
        AddCell(grid, 2, row.Date);

        // Subject cell: optional ref badges followed by the subject text.
        StackPanel subject = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };

        foreach (string refName in row.RefNames)
        {
            subject.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = refName,
                    Foreground = Brushes.White,
                    FontSize = 11,
                },
            });
        }

        subject.Children.Add(new TextBlock
        {
            Text = row.Subject,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(subject, 3);
        grid.Children.Add(subject);

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
