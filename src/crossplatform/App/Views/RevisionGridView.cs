using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
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

    // Row metrics — kept tight for a dense, GitExtensions-like log.
    private const double RowFontSize = 12;

    private readonly RevisionService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ContentControl _headerHost;

    // The rows currently displayed, kept so BuildRow can compute a row's index
    // (for the subtle alternating-row background).
    private IReadOnlyList<RevisionRow> _rows = [];

    // Palette pulled from the shared app resources (see App.cs).
    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Width of the graph column; updated to fit the loaded graph's lane count.
    private double _graphWidth = LaneWidth;

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    // Host-registered commit-targeted actions (checkout, cherry-pick, reset, …),
    // appended to each row's context menu. Each handler receives the full hash.
    private readonly List<(string Header, Action<string> Handler)> _commitCommands = [];

    /// <summary>
    ///  Registers an extra context-menu command shown on each commit row; the
    ///  handler is invoked with the row's full commit hash.
    /// </summary>
    public void AddCommitCommand(string header, Action<string> handler)
        => _commitCommands.Add((header, handler));

    public RevisionGridView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(10, 6, 10, 6),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            Background = B("App.Toolbar"),
            Padding = new Thickness(0, 2, 0, 2),
            Text = "No repository loaded.",
        };

        _headerHost = new ContentControl { Content = BuildHeader() };

        _list = new ListBox
        {
            Background = B("App.Window"),
            Foreground = B("App.Text"),
            FontSize = RowFontSize,
            BorderThickness = new Thickness(0),
            ClipToBounds = true,
            ItemTemplate = new FuncDataTemplate<RevisionRow>((row, _) => BuildRow(row), supportsRecycling: true),
        };

        // Dense rows, transparent containers, and an App.Selection highlight for
        // the selected/hovered row (styling the Fluent ListBoxItem template).
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            },
        });
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":pointerover")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.PanelAlt")) },
        });
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.Selection")) },
        });

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

        DockPanel root = new() { Background = B("App.Window") };
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
                    _rows = rows;
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
        grid.Margin = new Thickness(10, 0, 10, 0);

        AddCell(grid, 0, string.Empty, B("App.TextDim"), bold: true);
        AddCell(grid, 1, "Hash", B("App.TextDim"), bold: true);
        AddCell(grid, 2, "Author", B("App.TextDim"), bold: true);
        AddCell(grid, 3, "Date", B("App.TextDim"), bold: true);
        AddCell(grid, 4, "Subject", B("App.TextDim"), bold: true);

        return new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 3, 0, 3),
            Child = grid,
        };
    }

    private Control BuildRow(RevisionRow row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);
        grid.MinHeight = 20;

        // Subtle alternating-row background (App.Panel / App.PanelAlt).
        int index = _rows is List<RevisionRow> list ? list.IndexOf(row) : IndexOf(_rows, row);
        grid.Background = (index & 1) == 0 ? B("App.Panel") : B("App.PanelAlt");

        // Graph cell (column 0): the DAG lanes for this row.
        RevisionGraphControl graph = new(row.GraphSegments, row.NodeLane, LaneWidth);
        Grid.SetColumn(graph, 0);
        grid.Children.Add(graph);

        // Hash: monospace + accent so it reads as a code identifier.
        AddCell(grid, 1, row.ShortHash, B("App.Accent"), monospace: true);
        AddCell(grid, 2, row.Author, B("App.TextDim"));
        AddCell(grid, 3, row.Date, B("App.TextDim"));

        // Subject cell: optional ref badges followed by the subject text.
        StackPanel subject = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (string refName in row.RefNames)
        {
            subject.Children.Add(BuildRefBadge(refName));
        }

        subject.Children.Add(new TextBlock
        {
            Text = row.Subject,
            Foreground = B("App.Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(subject, 4);
        grid.Children.Add(subject);

        grid.ContextMenu = BuildRowContextMenu(row);
        return grid;
    }

    private static int IndexOf(IReadOnlyList<RevisionRow> rows, RevisionRow row)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return 0;
    }

    // A rounded, muted "pill" for a ref name, coloured by kind: local branch,
    // remote-tracking branch, or tag — echoing the original GitExtensions look.
    private static Border BuildRefBadge(string refName)
    {
        (Color bg, Color fg) = RefColors(refName);

        return new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = refName,
                Foreground = new SolidColorBrush(fg),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    // Remote-tracking refs contain a "/" (e.g. origin/main); simple version-like
    // names (v1.2, 2.0) are treated as tags; everything else is a local branch.
    private static (Color Bg, Color Fg) RefColors(string refName)
    {
        if (refName.Contains('/'))
        {
            return (Color.FromRgb(0x3A, 0x4A, 0x5C), Color.FromRgb(0xAF, 0xCB, 0xE3)); // remote: muted blue
        }

        if (Regex.IsMatch(refName, @"^v?\d"))
        {
            return (Color.FromRgb(0x5A, 0x4B, 0x2E), Color.FromRgb(0xE3, 0xCB, 0x95)); // tag: muted amber
        }

        return (Color.FromRgb(0x37, 0x50, 0x3A), Color.FromRgb(0xB6, 0xE0, 0xB9)); // local branch: muted green
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

        ContextMenu menu = new()
        {
            Items =
            {
                copyHash,
                copySubject,
                copyAuthor,
            },
        };

        if (_commitCommands.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach ((string header, Action<string> handler) in _commitCommands)
            {
                MenuItem item = new() { Header = header };
                item.Click += (_, _) => handler(row.Hash);
                menu.Items.Add(item);
            }
        }

        return menu;
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private static void AddCell(Grid grid, int column, string text, IBrush? foreground = null, bool bold = false, bool monospace = false)
    {
        TextBlock block = new()
        {
            Text = text,
            Foreground = foreground ?? B("App.Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        };

        if (monospace)
        {
            block.FontFamily = new FontFamily("monospace,Consolas,Menlo");
        }

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

            // Custom-drawn Controls do NOT clip by default: lane lines/edges can
            // paint outside the row's bounds and smear into neighbours / the
            // panel below. Clip strictly to our own bounds.
            ClipToBounds = true;
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
