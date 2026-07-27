using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitCommands.Logging;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Embeds the core git command log inside the bottom panel's "Output" tab,
///  mirroring upstream's <c>FormGitCommandLog</c> "Command log" tab. The Git
///  Extensions core records every executed process in the process-global
///  <see cref="CommandLog"/>; this view shows one row per entry
///  (<see cref="CommandLogEntry.ColumnLine"/>, oldest-first / newest-last) and,
///  in a splitter-separated pane underneath, the full
///  <see cref="CommandLogEntry.Detail"/> of the selected command — upstream's
///  <c>LogItems</c> / <c>LogOutput</c> split container. Never throws.
///
///  <para>Ported alongside it: the row context menu (<c>mnuSaveToFile</c>,
///  <c>mnuCopyCommandLine</c>, <c>mnuClear</c>) and the <c>chkWordWrap</c>
///  toggle, next to the port's own "Refresh" button.</para>
///
///  <para>Deliberately NOT ported: the "Command cache" tab (the core's command
///  cache is not exposed by the port), <c>chkAlwaysOnTop</c> (this is a tab, not a
///  window) and <c>chkCaptureCallStacks</c>.</para>
///
///  <para>The chrome (button, checkbox, menu, count line) goes through
///  <see cref="TranslationService"/>; the logged command lines themselves are
///  data and are never looked up. A language switch re-runs <c>Reload</c>, which
///  re-renders both.</para>
/// </summary>
public sealed class OutputView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private readonly ListBox _items;
    private readonly TextBox _detail;
    private readonly ScrollViewer _detailScroll;
    private readonly TextBlock _status;
    private readonly Button _refresh;
    private readonly CheckBox _wordWrap;

    // Built once in the constructor: mutating Items while the popup opens leaves
    // it unmeasured (HANDOFF §3). Opening only flips IsEnabled.
    private readonly MenuItem _saveToFileItem = new();
    private readonly MenuItem _copyCommandLineItem = new();
    private readonly MenuItem _clearItem = new();

    // Live updates: CommandLog.CommandsChanged fires from whichever thread ran the
    // process — twice per command at least (start, end) plus once for the PID — so
    // the handler only ever asks for a reload and a throttle timer coalesces the
    // burst into one refresh. Upstream subscribes on Load and unsubscribes on close
    // (FormGitCommandLog.cs:38-58); attach/detach is this port's equivalent.
    private const int ThrottleMs = 300;
    private readonly DispatcherTimer _throttle;
    private bool _subscribed;

    public OutputView()
    {
        // One row per logged command. A ListBox (not a text blob) so a row can be
        // selected and its full output shown underneath, like upstream.
        _items = new ListBox
        {
            FontFamily = Monospace,
            FontSize = 12,
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            BorderThickness = new Thickness(0),

            // Upstream sets LogItems.DisplayMember = nameof(CommandLogEntry.ColumnLine);
            // the Avalonia equivalent is a template over that projection, which
            // keeps the entry itself as the item for the detail pane and the menu.
            ItemTemplate = new FuncDataTemplate<CommandLogEntry>(
                (entry, _) => new TextBlock
                {
                    Text = ColumnLine(entry),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                supportsRecycling: true),
        };
        _items.SelectionChanged += (_, _) => ShowDetail();
        _items.ContextMenu = BuildContextMenu();

        _detail = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Monospace,
            Background = Brush("App.PanelAlt", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };

        _detailScroll = new ScrollViewer
        {
            Content = _detail,
            Background = Brush("App.PanelAlt", Brushes.Black),
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

        _wordWrap = new CheckBox
        {
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _wordWrap.IsCheckedChanged += (_, _) => ApplyWordWrap();

        _status = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        // Auto / * / Auto rather than a fixed-width horizontal StackPanel: the
        // Italian captions are longer than the English ones (HANDOFF §3).
        Grid header = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(8, 6, 8, 6),
        };
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(_wordWrap, 1);
        Grid.SetColumn(_refresh, 2);
        header.Children.Add(_status);
        header.Children.Add(_wordWrap);
        header.Children.Add(_refresh);

        // Upstream's split container: the command list on top, the selected
        // command's full output below, with a draggable splitter between them.
        Grid split = new()
        {
            RowDefinitions = new RowDefinitions("2*,4,*"),
        };

        GridSplitter splitter = new()
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brush("App.Border", Brushes.Gray),
            ResizeDirection = GridResizeDirection.Rows,
        };

        Grid.SetRow(_items, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(_detailScroll, 2);
        split.Children.Add(_items);
        split.Children.Add(splitter);
        split.Children.Add(_detailScroll);

        DockPanel root = new() { Background = Brush("App.Window", Brushes.DimGray) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(split);

        Content = root;
        ClipToBounds = true;

        _throttle = new DispatcherTimer(
            TimeSpan.FromMilliseconds(ThrottleMs),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _throttle!.Stop();
                Reload();
            });
        _throttle.Stop();

        AttachedToVisualTree += (_, _) =>
        {
            Subscribe();
            Reload();
        };
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------- live updates

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        CommandLog.CommandsChanged += OnCommandsChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        CommandLog.CommandsChanged -= OnCommandsChanged;
        _subscribed = false;
        _throttle.Stop();
    }

    // Raised on the thread that ran the process: marshal, then coalesce. Trailing
    // edge only, so a command that starts and ends inside the window costs one
    // refresh, not two.
    private void OnCommandsChanged()
        => Dispatcher.UIThread.Post(
            () =>
            {
                if (_subscribed && !_throttle.IsEnabled)
                {
                    _throttle.Start();
                }
            },
            DispatcherPriority.Background);

    // ------------------------------------------------------------ context menu

    private ContextMenu BuildContextMenu()
    {
        _saveToFileItem.Click += (_, _) => SaveToFile();
        _copyCommandLineItem.Click += (_, _) => CopyCommandLine();
        _clearItem.Click += (_, _) => ClearLog();

        ContextMenu menu = new()
        {
            ItemsSource = new Control[]
            {
                _saveToFileItem,
                _copyCommandLineItem,
                new Separator(),
                _clearItem,
            },
        };

        menu.Opening += (_, _) =>
        {
            bool hasRows = _items.ItemCount > 0;
            _saveToFileItem.IsEnabled = hasRows;
            _clearItem.IsEnabled = hasRows;
            _copyCommandLineItem.IsEnabled = _items.SelectedItem is CommandLogEntry;
        };

        return menu;
    }

    // "&Save to file": every entry as a separator-joined FullLine, exactly like
    // upstream (tab for .txt, the culture list separator for .csv). The picker
    // runs on the UI thread; the write does not.
    private void SaveToFile() => _ = SaveToFileCoreAsync();

    private async Task SaveToFileCoreAsync()
    {
        try
        {
            IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                _status.Text = T("No file picker is available on this display.");
                return;
            }

            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strip(T("FormGitCommandLog/mnuSaveToFile.Text", "_Save to file")),
                SuggestedFileName = "gitcommandlog.txt",
                DefaultExtension = "txt",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType(T("Text files")) { Patterns = ["*.txt"] },
                    new FilePickerFileType(T("CSV files")) { Patterns = ["*.csv"] },
                    new FilePickerFileType(T("All files")) { Patterns = ["*"] },
                ],
            });

            if (target is null)
            {
                return;   // cancelled
            }

            string? destination = target.TryGetLocalPath();
            if (destination is null)
            {
                _status.Text = T("The chosen location is not a local file.");
                return;
            }

            string separator = destination.EndsWith("csv", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.CurrentCulture.TextInfo.ListSeparator
                : "\t";

            string[] lines = CommandLog.Commands.Select(entry => entry.FullLine(separator)).ToArray();
            await Task.Run(() => File.WriteAllLinesAsync(destination, lines));

            _status.Text = F(T("Saved {0}"), destination);
        }
        catch (Exception ex)
        {
            _status.Text = F(T("Could not save the command log: {0}"), ex.Message);
        }
    }

    private void CopyCommandLine()
    {
        if (_items.SelectedItem is not CommandLogEntry entry)
        {
            return;
        }

        try
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(entry.CommandLine);
        }
        catch (Exception ex)
        {
            _status.Text = F(T("Could not copy the command line: {0}"), ex.Message);
        }
    }

    private void ClearLog()
    {
        try
        {
            CommandLog.Clear();
        }
        catch (Exception ex)
        {
            _status.Text = F(T("Could not clear the command log: {0}"), ex.Message);
            return;
        }

        Reload();
    }

    // ---------------------------------------------------------------- behaviour

    private void ApplyWordWrap()
    {
        bool wrap = _wordWrap.IsChecked == true;
        _detail.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _detailScroll.HorizontalScrollBarVisibility =
            wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void ShowDetail()
    {
        try
        {
            _detail.Text = _items.SelectedItem is CommandLogEntry entry
                ? entry.Detail
                : string.Empty;
        }
        catch (Exception ex)
        {
            _detail.Text = F(T("Could not read the command log: {0}"), ex.Message);
        }
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    // The XLIFF strings carry WinForms "&" accelerators; drop them where a plain
    // caption is wanted (a picker title, a checkbox).
    private static string Strip(string caption) => caption.Replace("_", string.Empty);

    // Reload re-labels the chrome and re-renders the rows, so a language switch
    // needs nothing else.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Reload);

    // Snapshots the live core command log, newest-last. The queue is enumerated
    // oldest-first by the core, so no reordering is needed. Keeps the selected
    // command selected across a refresh, as upstream's RefreshListBox does.
    private void Reload()
    {
        ApplyTranslations();

        List<CommandLogEntry> entries;
        try
        {
            entries = CommandLog.Commands.ToList();
        }
        catch (Exception ex)
        {
            _detail.Text = F(T("Could not read the command log: {0}"), ex.Message);
            _status.Text = T("Error reading command log.");
            return;
        }

        // Upstream keeps the last row selected unless the user picked another one.
        bool wasAtEnd = _items.ItemCount == 0 || _items.SelectedIndex == _items.ItemCount - 1;
        int previous = _items.SelectedIndex;

        // A brand-new list: re-assigning the same instance would leave the
        // realised containers untouched (HANDOFF §3).
        _items.ItemsSource = entries;

        if (entries.Count > 0)
        {
            _items.SelectedIndex = wasAtEnd || previous >= entries.Count
                ? entries.Count - 1
                : previous;
        }
        else
        {
            _detail.Text = T("(no git commands have been executed yet in this session)");
        }

        _status.Text = F(T("{0} command(s) logged."), entries.Count);

        Dispatcher.UIThread.Post(() => _items.ScrollIntoView(_items.SelectedIndex), DispatcherPriority.Background);
    }

    private void ApplyTranslations()
    {
        _refresh.Content = T("FormBrowse/RefreshButton.ToolTipText", "Refresh");
        _wordWrap.Content = Strip(T("FormGitCommandLog/chkWordWrap.Text", "Word wrap"));
        _saveToFileItem.Header = T("FormGitCommandLog/mnuSaveToFile.Text", "_Save to file");
        _copyCommandLineItem.Header = T("FormGitCommandLog/mnuCopyCommandLine.Text", "_Copy full command line");
        _clearItem.Header = T("FormGitCommandLog/mnuClear.Text", "C_lear");
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;

    // ColumnLine formats a live entry (duration, exit code); an entry still
    // running must never take the view down.
    private static string ColumnLine(CommandLogEntry entry)
    {
        try
        {
            return entry.ColumnLine;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
