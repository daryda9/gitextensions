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
///
///  <para>Captions go through <see cref="TranslationService"/>. Upstream's
///  <c>FormFileHistory</c> is a tabbed window whose grid is a
///  <c>RevisionGridControl</c>, so its trans-units are tabs and menu entries rather
///  than column headers; the four headers here are keyed to the equivalent
///  upstream columns (<c>FormVerify</c>) and to the shared
///  <c>TranslatedStrings</c> labels. Header and status line are rebuilt on
///  <see cref="TranslationService.LanguageChanged"/>.</para>
/// </summary>
public sealed class FileHistoryView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 170;
    private const double DateWidth = 130;

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private readonly FileHistoryService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly Border _headerHost;

    // Last successful load, so a language switch can re-word the status line
    // without re-running git.
    private string? _shownFile;
    private int _shownCommits;

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    public FileHistoryView()
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
        root.Children.Add(_list);

        Content = root;

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // Fired on the catalogue-loading thread; marshal the relabel to the UI thread.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        _headerHost.Child = BuildHeader();
        _status.Text = _shownFile is null ? T("No file loaded.") : StatusLine();
    }

    private string StatusLine()
        => string.Format(T("{0}  —  {1} commit(s)"), _shownFile, _shownCommits);

    /// <summary>
    ///  Loads and displays the commit history of <paramref name="filePath"/> in
    ///  the repository at <paramref name="repoPath"/>. Heavy git work runs off the
    ///  UI thread.
    /// </summary>
    public void ShowHistory(string repoPath, string filePath)
    {
        _list.ItemsSource = null;
        _shownFile = null;
        _status.Text = string.Format(T("Loading history of {0}…"), filePath);

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<FileHistoryRow> rows = _service.GetHistory(repoPath, filePath);
                Dispatcher.UIThread.Post(() =>
                {
                    _list.ItemsSource = rows;
                    _shownFile = filePath;
                    _shownCommits = rows.Count;
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
            ColumnDefinitions = new ColumnDefinitions($"{HashWidth},{AuthorWidth},{DateWidth},*"),
        };

    private static Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(8, 0, 8, 2);

        AddCell(grid, 0, T("FormVerify/columnHash.HeaderText", "Hash"), bold: true);
        AddCell(grid, 1, T("TranslatedStrings/_author.Text", "Author"), bold: true);
        AddCell(grid, 2, T("TranslatedStrings/_dateText.Text", "Date"), bold: true);
        AddCell(grid, 3, T("FormVerify/columnSubject.HeaderText", "Subject"), bold: true);

        return grid;
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
