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
///
///  <para>Captions go through <see cref="TranslationService"/>. Upstream's
///  <c>FormBlame</c> carries a single trans-unit (the window title) and its blame
///  grid headers are hard-coded in code, so only the columns that do have an
///  upstream equivalent are keyed (<c>FormVerify/columnHash</c>,
///  <c>TranslatedStrings/_author</c>); "Line" and "Text" fall back to the
///  one-argument overload and therefore stay English until a catalogue gains them.
///  The header and the status line are rebuilt on
///  <see cref="TranslationService.LanguageChanged"/>.</para>
/// </summary>
public sealed class BlameView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 160;
    private const double LineWidth = 60;

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;
    private static readonly IBrush MetaBrush = B("App.TextDim");

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private readonly BlameService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly Border _headerHost;

    // Last successful load, kept so a language switch can re-word the status line
    // without re-running git.
    private string? _shownFile;
    private string? _shownCommit;
    private int _shownLines;

    public BlameView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4),
            Foreground = B("App.TextDim"),
            Background = B("App.Toolbar"),
            Padding = new Thickness(4, 4, 4, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = T("No file loaded."),
        };

        _list = new ListBox
        {
            FontFamily = Monospace,
            FontSize = 12,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<BlameLineRow>((row, _) => BuildRow(row), supportsRecycling: true),
        };

        ScrollViewer scroll = new()
        {
            Content = _list,
            Background = B("App.Panel"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _headerHost = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = BuildHeader(),
        };

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_headerHost, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(_headerHost);
        root.Children.Add(scroll);

        Content = root;

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // The event fires on whichever thread finished loading the catalogue, so the
    // relabel is marshalled to the UI thread.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        _headerHost.Child = BuildHeader();
        _status.Text = _shownFile is null ? T("No file loaded.") : StatusLine();
    }

    private string StatusLine() => string.Format(
        T("{0}  —  {1} line(s)  @ {2}"), _shownFile, _shownLines, _shownCommit);

    /// <summary>
    ///  Loads and displays the blame of <paramref name="filePath"/> in the
    ///  repository at <paramref name="repoPath"/> at <paramref name="commit"/>
    ///  (defaults to <c>HEAD</c> when null). Heavy git work runs off the UI thread.
    /// </summary>
    public void ShowBlame(string repoPath, string filePath, string? commit = null)
    {
        _list.ItemsSource = null;
        _shownFile = null;
        _status.Text = string.Format(T("Blaming {0}…"), filePath);

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<BlameLineRow> rows = _service.GetBlame(repoPath, filePath, commit);
                Dispatcher.UIThread.Post(() =>
                {
                    _list.ItemsSource = rows;
                    _shownFile = filePath;
                    _shownCommit = commit ?? "HEAD";
                    _shownLines = rows.Count;
                    _status.Text = StatusLine();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = string.Format(T("Error: {0}"), ex.Message));
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

        AddCell(grid, 0, T("FormVerify/columnHash.HeaderText", "Hash"), bold: true);
        AddCell(grid, 1, T("TranslatedStrings/_author.Text", "Author"), bold: true);
        AddCell(grid, 2, T("Line"), bold: true);
        AddCell(grid, 3, T("Text"), bold: true);

        return grid;
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
