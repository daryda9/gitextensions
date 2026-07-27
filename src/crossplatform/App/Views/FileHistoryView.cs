using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A read-only view of a single file's commit history: a multi-column list
///  (Hash / Author / Date / Subject) of the commits that touched the file,
///  following it across renames. Heavy git work runs off the UI thread, matching
///  <see cref="DiffView"/>. Built on a <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) is required.
///
///  <para>Ported from upstream's <c>FormFileHistory</c>: the row context menu
///  (<c>FileHistoryContextMenu</c>) with the Copy submenu, "Save as", the
///  "Manipulate commit" submenu (revert / cherry pick) and the two
///  "Detect and follow…" toggles, plus the <c>ShowFullHistory</c> drop-down with
///  "Show full history" / "Simplify merges". All four toggles are only
///  <c>git log</c> switches: they go through <see cref="FileHistoryOptions"/> and
///  trigger a reload. Upstream persists them in <c>AppSettings</c>; the port keeps
///  them session-local like the other view toggles.</para>
///
///  <para>Deliberately NOT ported here (upstream's four inner tabs
///  Commit diff / Diff / View / Blame, the eleven "Blame options" toggles, the
///  full <c>FilterToolBar</c> and open-with-difftool "selected &lt;-&gt; local").</para>
///
///  <para>Captions go through <see cref="TranslationService"/>. Upstream's
///  <c>FormFileHistory</c> is a tabbed window whose grid is a
///  <c>RevisionGridControl</c>, so its trans-units are tabs and menu entries rather
///  than column headers; the four headers here are keyed to the equivalent
///  upstream columns (<c>FormVerify</c>) and to the shared
///  <c>TranslatedStrings</c> labels. Header, menu and status line are rebuilt on
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

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private readonly FileHistoryService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly Border _headerHost;
    private readonly Button _fullHistoryButton;

    // Row context menu: every item is built once in the constructor. The Opening
    // handler only flips IsEnabled / IsChecked — mutating Items there leaves the
    // popup unmeasured (HANDOFF §3).
    private readonly MenuItem _copyItem = new();
    private readonly List<(MenuItem Item, string Key, string English, Func<FileHistoryRow, string> Value)> _copyEntries = [];
    private readonly MenuItem _saveAsItem = new();
    private readonly MenuItem _manipulateItem = new();
    private readonly MenuItem _revertItem = new();
    private readonly MenuItem _cherryPickItem = new();
    private readonly MenuItem _followItem = new() { ToggleType = MenuItemToggleType.CheckBox };
    private readonly MenuItem _followExactItem = new() { ToggleType = MenuItemToggleType.CheckBox };

    // The git log switches, session-local (upstream: AppSettings.*InFileHistory).
    private FileHistoryOptions _options = new();

    // Last successful load, so a language switch can re-word the status line
    // without re-running git, and so a toggle can reload the same file.
    private string? _repoPath;
    private string? _filePath;
    private string? _shownFile;
    private int _shownCommits;

    // The row that was right-clicked; a right-click does not move ListBox
    // selection by itself.
    private FileHistoryRow? _menuRow;

    // True for the duration of a right-button press dispatch.
    private bool _rightPressed;

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    /// <summary>
    ///  Raised by "Manipulate commit &gt; Revert commit". When nothing is subscribed
    ///  the view performs the revert itself through <see cref="RevertArchiveService"/>
    ///  and reports the outcome in its own status line; a host that wants the
    ///  shared refresh / watcher-suspend treatment (<c>MainWindow.RunOp</c>) can
    ///  subscribe and take over.
    /// </summary>
    public event Action<string>? RevertCommitRequested;

    /// <summary>
    ///  Raised by "Manipulate commit &gt; Cherry pick commit". Same fallback contract
    ///  as <see cref="RevertCommitRequested"/> (<see cref="StashOpsService.CherryPick"/>).
    /// </summary>
    public event Action<string>? CherryPickCommitRequested;

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
            if (_rightPressed)
            {
                // Selecting by right-click must stay inside this view: the host
                // reacts to RevisionSelected by switching the bottom panel to the
                // commit tab, which would pull the view (and the menu about to
                // open) out from under the pointer. Seen headless.
                return;
            }

            // A left-click supersedes whatever row a previous right-click aimed at.
            _menuRow = null;

            if (_list.SelectedItem is FileHistoryRow row)
            {
                RevisionSelected?.Invoke(row.Hash);
            }
        };

        // A ListBox is not focusable (its containers are) and a bubbling key
        // handler is eaten by the item; tunnelling with handledEventsToo is the
        // shape that works (HANDOFF §3).
        _list.AddHandler(
            KeyDownEvent,
            (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                    && _list.SelectedItem is FileHistoryRow row)
                {
                    Copy(row.Hash);
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Remember which row the pointer is over before the menu opens, and flag
        // the press as a right one for the whole of its dispatch. Tunnelling with
        // handledEventsToo because the ListBoxItem marks the press handled.
        _list.AddHandler(
            PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
            {
                if (!e.GetCurrentPoint(_list).Properties.IsRightButtonPressed)
                {
                    return;
                }

                _menuRow = (e.Source as Visual)?
                    .FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FileHistoryRow;

                // Reset once this input event is fully processed — the ListBox's
                // own selection handling (and the SelectionChanged above) runs
                // inside it.
                _rightPressed = true;
                Dispatcher.UIThread.Post(() => _rightPressed = false, DispatcherPriority.Input);
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _list.ContextMenu = BuildContextMenu();

        _fullHistoryButton = new Button
        {
            Background = B("App.Control"),
            Foreground = B("App.Text"),
            Padding = new Thickness(8, 3, 8, 3),
        };
        _fullHistoryButton.Click += (_, _) => ShowFullHistoryMenu();

        _headerHost = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = BuildHeader(),
        };

        // WrapPanel, not a fixed-width horizontal StackPanel: the Italian captions
        // are longer than the English ones (HANDOFF §3).
        WrapPanel toolbar = new()
        {
            Background = B("App.Toolbar"),
            Margin = new Thickness(8, 2, 8, 2),
            Children = { _fullHistoryButton },
        };

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_headerHost, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(toolbar);
        root.Children.Add(_headerHost);
        root.Children.Add(_list);

        Content = root;

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------- context menu

    private ContextMenu BuildContextMenu()
    {
        // Copy submenu: a fixed set of entries, because Items may not be rebuilt
        // while the popup opens. Only the headers change (they carry a preview of
        // the value, like upstream's CopyContextMenuItem).
        CopyEntry("TranslatedStrings/_commitHashText.Text", "Commit hash", r => r.Hash);
        CopyEntry(null, "Short hash", r => r.ShortHash);
        CopyEntry("TranslatedStrings/_message.Text", "Message", r => r.Message.Length > 0 ? r.Message : r.Subject);
        CopyEntry("TranslatedStrings/_author.Text", "Author",
            r => r.AuthorEmail.Length > 0 ? $"{r.Author} <{r.AuthorEmail}>" : r.Author);
        CopyEntry("TranslatedStrings/_authorDateText.Text", "Author date", r => r.AuthorDate);
        CopyEntry("TranslatedStrings/_commitDateText.Text", "Commit date", r => r.CommitDate);

        _saveAsItem.Click += (_, _) => SaveAs();
        _revertItem.Click += (_, _) => RevertCommit();
        _cherryPickItem.Click += (_, _) => CherryPickCommit();

        _manipulateItem.Items.Add(_revertItem);
        _manipulateItem.Items.Add(_cherryPickItem);

        _followItem.Click += (_, _) => SetOptions(_options with { FollowRenames = !_options.FollowRenames });
        _followExactItem.Click += (_, _) => SetOptions(
            _options with { ExactRenamesAndCopiesOnly = !_options.ExactRenamesAndCopiesOnly });

        ContextMenu menu = new()
        {
            ItemsSource = new Control[]
            {
                _copyItem,
                new Separator(),
                _saveAsItem,
                new Separator(),
                _manipulateItem,
                new Separator(),
                _followItem,
                _followExactItem,
            },
        };

        menu.Opening += (_, _) => UpdateMenuState();
        return menu;
    }

    private void CopyEntry(string? key, string english, Func<FileHistoryRow, string> value)
    {
        MenuItem item = new();
        item.Click += (_, _) =>
        {
            if (Current() is FileHistoryRow row)
            {
                Copy(value(row));
            }
        };

        _copyEntries.Add((item, key ?? string.Empty, english, value));
        _copyItem.Items.Add(item);
    }

    // Opening: enable/disable, check/uncheck, and re-word the Copy previews. No
    // Items are touched.
    private void UpdateMenuState()
    {
        FileHistoryRow? row = Current();
        bool hasRow = row is not null && _repoPath is not null;

        _copyItem.IsEnabled = hasRow;
        _saveAsItem.IsEnabled = hasRow && _filePath is not null;
        _manipulateItem.IsEnabled = hasRow;

        foreach ((MenuItem item, string key, string english, Func<FileHistoryRow, string> value) in _copyEntries)
        {
            string caption = T(key.Length > 0 ? key : null, english);
            item.Header = row is null ? caption : $"{caption}:   {Preview(value(row))}";
            item.IsEnabled = row is not null && value(row).Length > 0;
        }

        _followItem.IsChecked = _options.FollowRenames;
        _followExactItem.IsChecked = _options.ExactRenamesAndCopiesOnly;
        _followExactItem.IsEnabled = _options.FollowRenames;
    }

    // First line, shortened — upstream shows the same kind of inline preview.
    private static string Preview(string text)
    {
        string line = text.Split('\n')[0].Trim();
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    // The right-clicked row wins over the selection: it is what the user aimed at,
    // and it is cleared again as soon as the selection moves.
    private FileHistoryRow? Current()
        => _menuRow ?? _list.SelectedItem as FileHistoryRow;

    private void Copy(string text)
    {
        try
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            _status.Text = F(T("Error: {0}"), ex.Message);
        }
    }

    // ---------------------------------------------------- "Show Full History" ▾

    // Items are added before ShowAt: a MenuFlyout populated inside Opening is not
    // re-measured and shows up as an empty sliver (HANDOFF §3).
    private void ShowFullHistoryMenu()
    {
        MenuFlyout flyout = new();

        MenuItem full = new()
        {
            Header = T("FormFileHistory/showFullHistoryToolStripMenuItem.Text", "Show full history"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.FullHistory,
        };
        full.Click += (_, _) => SetOptions(_options with { FullHistory = !_options.FullHistory });

        MenuItem simplify = new()
        {
            Header = T("FormFileHistory/simplifyMergesToolStripMenuItem.Text", "Simplify merges"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.SimplifyMerges,
            IsEnabled = _options.FullHistory,   // upstream enables it only then
        };
        simplify.Click += (_, _) => SetOptions(_options with { SimplifyMerges = !_options.SimplifyMerges });

        flyout.Items.Add(full);
        flyout.Items.Add(simplify);
        flyout.ShowAt(_fullHistoryButton);
    }

    private void SetOptions(FileHistoryOptions options)
    {
        _options = options;

        if (_repoPath is not null && _filePath is not null)
        {
            ShowHistory(_repoPath, _filePath);
        }
    }

    // --------------------------------------------------------------- "Save as"

    // The file's content at the selected commit. The picker runs on the UI
    // thread; the git read and the write do not (same shape as DiffView).
    private void SaveAs()
    {
        if (Current() is not FileHistoryRow row || _repoPath is null || _filePath is null)
        {
            return;
        }

        _ = SaveAsCoreAsync(_repoPath, row.Hash, _filePath);
    }

    private async Task SaveAsCoreAsync(string repoPath, string commit, string path)
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
                Title = T("FormFileHistory/saveAsToolStripMenuItem.Text", "Save as"),
                SuggestedFileName = Path.GetFileName(path),
                ShowOverwritePrompt = true,
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

            _status.Text = F(T("Saving {0}…"), destination);

            await Task.Run(async () =>
            {
                byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, commit, path)
                    .ConfigureAwait(false);
                await File.WriteAllBytesAsync(destination, bytes).ConfigureAwait(false);
            });

            _status.Text = F(T("Saved {0}"), destination);
        }
        catch (Exception ex)
        {
            _status.Text = F(T("Error: {0}"), ex.Message);
        }
    }

    // ------------------------------------------------- "Manipulate commit" ▸

    private void RevertCommit()
    {
        if (Current() is not FileHistoryRow row || _repoPath is null)
        {
            return;
        }

        if (RevertCommitRequested is { } handler)
        {
            handler(row.Hash);
            return;
        }

        string hash = row.Hash;
        RunLocally(T("FormFileHistory/revertCommitToolStripMenuItem.Text", "Revert commit"), repo =>
        {
            RevertArchiveResult result = new RevertArchiveService().Revert(repo, hash);
            return (result.Success, result.Output);
        });
    }

    private void CherryPickCommit()
    {
        if (Current() is not FileHistoryRow row || _repoPath is null)
        {
            return;
        }

        if (CherryPickCommitRequested is { } handler)
        {
            handler(row.Hash);
            return;
        }

        string hash = row.Hash;
        RunLocally(T("FormFileHistory/cherryPickThisCommitToolStripMenuItem.Text", "Cherry pick commit"), repo =>
        {
            StashOpResult result = new StashOpsService().CherryPick(repo, hash);
            return (result.Success, result.Output);
        });
    }

    // Fallback path used when no host handled the request. git runs off the UI
    // thread and nothing is allowed to escape.
    private void RunLocally(string label, Func<string, (bool Success, string Output)> op)
    {
        string repoPath = _repoPath!;
        _status.Text = F(T("{0}…"), label);

        _ = Task.Run(() =>
        {
            (bool success, string output) result;
            try
            {
                result = op(repoPath);
            }
            catch (Exception ex)
            {
                result = (false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (result.success)
                {
                    _status.Text = F(T("{0}: done"), label);
                    if (_filePath is not null)
                    {
                        ShowHistory(repoPath, _filePath);
                    }
                }
                else
                {
                    string firstLine = result.output.Split('\n')[0].Trim();
                    _status.Text = F(T("{0} stopped: {1}"), label, firstLine);
                }
            });
        });
    }

    // -------------------------------------------------------------- translation

    // Fired on the catalogue-loading thread; marshal the relabel to the UI thread.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        _headerHost.Child = BuildHeader();
        ApplyTranslations();
        _status.Text = _shownFile is null ? T("No file loaded.") : StatusLine();
    }

    private void ApplyTranslations()
    {
        _fullHistoryButton.Content = T("FormFileHistory/ShowFullHistory.ToolTipText", "Show Full History") + "  ▾";
        _copyItem.Header = T("FormFileHistory/copyToClipboardToolStripMenuItem.Text", "_Copy to clipboard");
        _saveAsItem.Header = T("FormFileHistory/saveAsToolStripMenuItem.Text", "Save as");
        _manipulateItem.Header = T("FormFileHistory/manipulateCommitToolStripMenuItem.Text", "Manipulate commit");
        _revertItem.Header = T("FormFileHistory/revertCommitToolStripMenuItem.Text", "Revert commit");
        _cherryPickItem.Header = T("FormFileHistory/cherryPickThisCommitToolStripMenuItem.Text", "Cherry pick commit");
        _followItem.Header = T("FormFileHistory/followFileHistoryToolStripMenuItem.Text", "Detect and follow renames");
        _followExactItem.Header = T(
            "FormFileHistory/followFileHistoryRenamesToolStripMenuItem.Text",
            "Detect and follow - exact renames and copies only");
    }

    private string StatusLine()
        => string.Format(T("{0}  —  {1} commit(s)"), _shownFile, _shownCommits);

    // ------------------------------------------------------------------ loading

    /// <summary>
    ///  Loads and displays the commit history of <paramref name="filePath"/> in
    ///  the repository at <paramref name="repoPath"/>. Heavy git work runs off the
    ///  UI thread.
    /// </summary>
    public void ShowHistory(string repoPath, string filePath)
    {
        _repoPath = repoPath;
        _filePath = filePath;
        _menuRow = null;
        _list.ItemsSource = null;
        _shownFile = null;
        _status.Text = string.Format(T("Loading history of {0}…"), filePath);

        FileHistoryOptions options = _options;

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<FileHistoryRow> rows = _service.GetHistory(repoPath, filePath, options);
                Dispatcher.UIThread.Post(() =>
                {
                    // A brand-new list: re-assigning the same instance would leave
                    // the realised containers untouched (HANDOFF §3).
                    _list.ItemsSource = rows.ToList();
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
