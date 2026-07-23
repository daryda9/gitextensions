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
///  A commit-list view (revision grid) for the Avalonia/Linux port. Loads the
///  recent history of a repository off the UI thread and renders it as a
///  multi-column list (DAG graph / Hash / Author / Date / Subject, with ref
///  names shown inline). Uses a <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) or theme
///  registration is required.
///
///  <para>The left-most column draws the commit DAG (colored lane lines + a
///  node dot per row, with branch/merge edges between adjacent rows), using the
///  lane layout computed by <see cref="RevisionService"/>.</para>
/// </summary>
public sealed class RevisionGridView : UserControl
{
    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 170;
    private const double DateWidth = 130;

    // Graph rendering metrics.
    private const double LaneWidth = 14;

    private readonly RevisionService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ContentControl _headerHost;

    // Width of the graph column; updated to fit the loaded graph's lane count.
    private double _graphWidth = LaneWidth;

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

        _headerHost = new ContentControl { Content = BuildHeader() };

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

        // Ctrl+C copies the selected commit's hash. (Up/Down selection is handled
        // by the ListBox and fires RevisionSelected via SelectionChanged above.)
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                && _list.SelectedItem is RevisionRow row)
            {
                Copy(row.Hash);
                e.Handled = true;
            }
        };

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_headerHost, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(_headerHost);
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
                    int laneCount = rows.Count > 0 ? rows[0].LaneCount : 1;
                    _graphWidth = Math.Max(1, laneCount) * LaneWidth;
                    _headerHost.Content = BuildHeader();
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

    private Grid MakeColumns()
        => new()
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{_graphWidth},{HashWidth},{AuthorWidth},{DateWidth},*"),
        };

    private Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 0, 8, 2);

        AddCell(grid, 0, string.Empty, bold: true);
        AddCell(grid, 1, "Hash", bold: true);
        AddCell(grid, 2, "Author", bold: true);
        AddCell(grid, 3, "Date", bold: true);
        AddCell(grid, 4, "Subject", bold: true);

        return new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private Control BuildRow(RevisionRow row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(0, 1, 0, 1);

        // Graph cell (column 0): the DAG lanes for this row.
        RevisionGraphControl graph = new(row.GraphSegments, row.NodeLane, LaneWidth);
        Grid.SetColumn(graph, 0);
        grid.Children.Add(graph);

        AddCell(grid, 1, row.ShortHash);
        AddCell(grid, 2, row.Author);
        AddCell(grid, 3, row.Date);

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

        Grid.SetColumn(subject, 4);
        grid.Children.Add(subject);

        grid.ContextMenu = BuildRowContextMenu(row);
        return grid;
    }

    // Right-click menu: copy details of the row that was clicked.
    private ContextMenu BuildRowContextMenu(RevisionRow row)
    {
        MenuItem copyHash = new() { Header = "Copy commit hash" };
        copyHash.Click += (_, _) => Copy(row.Hash);

        MenuItem copySubject = new() { Header = "Copy subject" };
        copySubject.Click += (_, _) => Copy(row.Subject);

        MenuItem copyAuthor = new() { Header = "Copy author" };
        copyAuthor.Click += (_, _) => Copy(row.Author);

        return new ContextMenu
        {
            Items =
            {
                copyHash,
                copySubject,
                copyAuthor,
            },
        };
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
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

    /// <summary>
    ///  Draws one row's slice of the commit DAG: colored lane lines (verticals
    ///  for pass-through lanes, diagonals for branch/merge edges) plus the node
    ///  dot for this commit. Geometry comes from <see cref="RevisionGraphSegment"/>s
    ///  computed by <see cref="RevisionService"/>.
    /// </summary>
    private sealed class RevisionGraphControl : Control
    {
        private static readonly Color[] LaneColors =
        {
            Color.FromRgb(0x22, 0x8B, 0x22), // green
            Color.FromRgb(0x1E, 0x90, 0xFF), // blue
            Color.FromRgb(0xFF, 0x8C, 0x00), // orange
            Color.FromRgb(0x93, 0x70, 0xDB), // purple
            Color.FromRgb(0xDC, 0x14, 0x3C), // crimson
            Color.FromRgb(0x00, 0x8B, 0x8B), // teal
            Color.FromRgb(0xB8, 0x86, 0x0B), // goldenrod
            Color.FromRgb(0xFF, 0x14, 0x93), // pink
        };

        private static readonly IBrush[] LaneBrushes =
            LaneColors.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();

        private readonly IReadOnlyList<RevisionGraphSegment> _segments;
        private readonly int _nodeLane;
        private readonly double _laneWidth;

        public RevisionGraphControl(IReadOnlyList<RevisionGraphSegment> segments, int nodeLane, double laneWidth)
        {
            _segments = segments;
            _nodeLane = nodeLane;
            _laneWidth = laneWidth;
        }

        private static IBrush Brush(int lane)
            => LaneBrushes[((lane % LaneBrushes.Length) + LaneBrushes.Length) % LaneBrushes.Length];

        public override void Render(DrawingContext context)
        {
            double h = Bounds.Height;
            if (h <= 0)
            {
                return;
            }

            double X(double lane) => (lane * _laneWidth) + (_laneWidth / 2);

            foreach (RevisionGraphSegment s in _segments)
            {
                Pen pen = new(Brush(s.ColorLane), 2);
                context.DrawLine(
                    pen,
                    new Point(X(s.FromLane), s.FromY * h),
                    new Point(X(s.ToLane), s.ToY * h));
            }

            IBrush nodeBrush = Brush(_nodeLane);
            context.DrawEllipse(nodeBrush, null, new Point(X(_nodeLane), h / 2), 4, 4);
        }
    }
}
