using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands.Logging;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Embeds the core git command log inside the bottom panel's "Output" tab,
///  mirroring <see cref="CommandLogWindow"/>. The Git Extensions core records
///  every executed process in the process-global <see cref="CommandLog"/>; this
///  view renders each entry's <see cref="CommandLogEntry.ColumnLine"/> projection
///  verbatim, oldest-first / newest-last, in a monospace read-only pane, with a
///  "Refresh" button that re-reads the live log. Never throws.
///
///  <para>The chrome (the Refresh button and the count line) goes through
///  <see cref="TranslationService"/>; the logged command lines themselves are
///  data and are never looked up. A language switch simply re-runs
///  <c>Reload</c>, which re-renders both.</para>
/// </summary>
public sealed class OutputView : UserControl
{
    private readonly TextBox _log;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status;
    private readonly Button _refresh;

    public OutputView()
    {
        _log = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        _scroll = new ScrollViewer
        {
            Content = _log,
            Background = Brush("App.Panel", Brushes.Black),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _refresh = new Button
        {
            MinWidth = 90,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Brush("App.Control", Brushes.DimGray),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        _refresh.Click += (_, _) => Reload();

        _status = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8, 6, 8, 6),
        };
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(_refresh, 1);
        header.Children.Add(_status);
        header.Children.Add(_refresh);

        DockPanel root = new() { Background = Brush("App.Window", Brushes.DimGray) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_scroll);

        Content = root;
        ClipToBounds = true;

        AttachedToVisualTree += (_, _) => Reload();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    // Reload re-labels the button and re-renders the count line, so a language
    // switch needs nothing else.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Reload);

    // Snapshots the live core command log and renders it newest-last. The queue is
    // enumerated oldest-first by the core, so no reordering is needed.
    private void Reload()
    {
        _refresh.Content = T("FormBrowse/RefreshButton.ToolTipText", "Refresh");

        List<string> lines;
        try
        {
            lines = CommandLog.Commands.Select(c => c.ColumnLine).ToList();
        }
        catch (Exception ex)
        {
            _log.Text = F(T("Could not read the command log: {0}"), ex.Message);
            _status.Text = T("Error reading command log.");
            return;
        }

        _log.Text = lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : T("(no git commands have been executed yet in this session)");
        _status.Text = F(T("{0} command(s) logged."), lines.Count);

        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
