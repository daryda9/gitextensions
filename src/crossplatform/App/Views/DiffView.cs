using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A two-pane view of a single commit's diff: the changed-files list on the
///  left, the unified diff of the selected file on the right. Heavy git work is
///  performed off the UI thread, matching <c>MainWindow</c>.
/// </summary>
public sealed class DiffView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Diff line colours tuned for the dark palette; hunk/meta pull from app resources.
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0xC7, 0x76));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));

    // File-status glyph colours: modified=accent, added=green, deleted=red.
    private static readonly IBrush ModifiedGlyph = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xD6));
    private static readonly IBrush AddedGlyph = new SolidColorBrush(Color.FromRgb(0x6A, 0xC7, 0x76));
    private static readonly IBrush DeletedGlyph = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));

    // Which comparison the currently loaded file list represents, so file
    // selection loads the matching per-file diff.
    private enum CompareMode
    {
        Commit,       // a single commit vs its first parent
        Range,        // BASE..other (two commits)
        WorkingTree,  // a commit vs the current working tree
    }

    private readonly ListBox _files;
    private readonly SelectableTextBlock _diff;
    private readonly TextBlock _status;

    private string? _repoPath;
    private string? _commitHash;   // the (right/"new") commit; also the "other" side in Range mode
    private string? _baseHash;     // the ("old"/left) commit in Range mode
    private CompareMode _mode = CompareMode.Commit;
    private CancellationTokenSource? _diffCts;

    // The raw unified-diff text currently displayed (the SelectableTextBlock's
    // Text is cleared while inlines are rendered, so keep our own copy to copy).
    private string _currentDiffText = string.Empty;

    public DiffView()
    {
        _files = new ListBox
        {
            FontFamily = Monospace,
            FontSize = 12,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderThickness = new Thickness(0),
            ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<DiffFileRow>(
                (row, _) => BuildFileRow(row),
                supportsRecycling: true),
        };
        _files.SelectionChanged += OnFileSelected;

        // Tight rows + an App.Selection highlight, matching the revision grid.
        _files.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 1, 8, 1)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            },
        });
        _files.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":pointerover")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.PanelAlt")) },
        });
        _files.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.Selection")) },
        });

        MenuItem copyPathItem = new() { Header = "Copy file path" };
        copyPathItem.Click += (_, _) => CopySelectedFilePath();
        MenuItem blameItem = new() { Header = "Blame" };
        blameItem.Click += (_, _) => RaiseFileAction(BlameRequested);
        MenuItem historyItem = new() { Header = "File history" };
        historyItem.Click += (_, _) => RaiseFileAction(FileHistoryRequested);
        MenuItem difftoolItem = new() { Header = "Open in external difftool" };
        difftoolItem.Click += (_, _) => OpenSelectedInExternalDiffTool();
        MenuItem compareWorkingDirItem = new() { Header = "Compare file to working directory" };
        compareWorkingDirItem.Click += (_, _) => CompareSelectedToWorkingDirectory();
        _files.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                copyPathItem,
                new Separator(),
                blameItem,
                historyItem,
                new Separator(),
                difftoolItem,
                compareWorkingDirItem,
            },
        };

        _diff = new SelectableTextBlock
        {
            FontFamily = Monospace,
            FontSize = 12,
            Foreground = B("App.Text"),
            Margin = new Thickness(12, 10, 12, 12),
            TextWrapping = TextWrapping.NoWrap,
        };

        MenuItem copyDiffItem = new() { Header = "Copy diff" };
        copyDiffItem.Click += (_, _) => CopyDiffText();
        MenuItem selectAllCopyItem = new() { Header = "Select all + copy" };
        selectAllCopyItem.Click += (_, _) => SelectAllAndCopy();
        _diff.ContextMenu = new ContextMenu { ItemsSource = new[] { copyDiffItem, selectAllCopyItem } };

        ScrollViewer diffScroll = new()
        {
            Content = _diff,
            Background = B("App.Window"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _status = new TextBlock
        {
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("App.Toolbar"),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "No commit selected.",
        };

        Grid split = new()
        {
            Background = B("App.Panel"),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
        };

        Grid.SetColumn(_files, 0);
        _files.Width = 320;

        GridSplitter splitter = new()
        {
            Width = 4,
            Background = B("App.Border"),
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, 1);

        Grid.SetColumn(diffScroll, 2);

        split.Children.Add(_files);
        split.Children.Add(splitter);
        split.Children.Add(diffScroll);

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(split);

        Content = root;

        // Ctrl+C: copy the file path when the file list is focused, otherwise the diff.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    // A changed-file row: a coloured status glyph (M/A/D/R/C) followed by the path.
    private static Control BuildFileRow(DiffFileRow? row)
    {
        if (row is null)
        {
            return new TextBlock();
        }

        (char glyph, IBrush glyphBrush) = row.Kind switch
        {
            DiffChangeKind.Added => ('A', AddedGlyph),
            DiffChangeKind.Deleted => ('D', DeletedGlyph),
            DiffChangeKind.Renamed => ('R', ModifiedGlyph),
            DiffChangeKind.Copied => ('C', ModifiedGlyph),
            _ => ('M', ModifiedGlyph),
        };

        string path = row.OldName is null || row.OldName == row.Name
            ? row.Name
            : $"{row.OldName} -> {row.Name}";

        StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = glyph.ToString(),
            Foreground = glyphBrush,
            FontFamily = Monospace,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = path,
            Foreground = B("App.Text"),
            FontFamily = Monospace,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_files.IsKeyboardFocusWithin)
            {
                CopySelectedFilePath();
            }
            else
            {
                CopyDiffText();
            }

            e.Handled = true;
        }
    }

    /// <summary>Raised (with the repo-relative file path) to blame the selected file.</summary>
    public event Action<string>? BlameRequested;

    /// <summary>Raised (with the repo-relative file path) to show the selected file's history.</summary>
    public event Action<string>? FileHistoryRequested;

    private void RaiseFileAction(Action<string>? handler)
    {
        if (_files.SelectedItem is DiffFileRow row)
        {
            handler?.Invoke(row.Name);
        }
    }

    // Fire-and-forget: launch the configured external difftool for the selected
    // file. The launch itself runs off the UI thread and the core runs the tool
    // detached, so neither call blocks; only a config error is surfaced (status).
    private void OpenSelectedInExternalDiffTool()
    {
        if (_files.SelectedItem is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        string repoPath = _repoPath;
        string commitHash = _commitHash;

        _ = Task.Run(() =>
        {
            try
            {
                string? message = DiffService.LaunchExternalDiffTool(repoPath, commitHash, row);
                if (!string.IsNullOrEmpty(message))
                {
                    Dispatcher.UIThread.Post(() => _status.Text = message);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = "Difftool error: " + ex.Message);
            }
        });
    }

    // Loads the diff of the selected file's committed version against the current
    // working-tree version and renders it in the shared coloured diff pane.
    private void CompareSelectedToWorkingDirectory()
    {
        if (_files.SelectedItem is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        // Supersede any in-flight per-file diff load, matching OnFileSelected.
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        string repoPath = _repoPath;
        string commitHash = _commitHash;

        _diff.Inlines?.Clear();
        _diff.Text = "Loading diff against working directory…";

        _ = Task.Run(async () =>
        {
            try
            {
                string text = await DiffService.GetFileDiffAgainstWorkingTreeAsync(
                    repoPath, commitHash, row, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        RenderDiff(text);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another selection/compare; ignore.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _diff.Inlines?.Clear();
                        _diff.Text = "Error: " + ex.Message;
                    }
                });
            }
        });
    }

    private void CopySelectedFilePath()
    {
        if (_files.SelectedItem is DiffFileRow row)
        {
            CopyToClipboard(row.Name);
        }
    }

    private void CopyDiffText() => CopyToClipboard(_currentDiffText);

    private void SelectAllAndCopy()
    {
        _diff.SelectAll();
        CopyToClipboard(_currentDiffText);
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>
    ///  Loads the changed-files list for <paramref name="commitHash"/> in the
    ///  repository at <paramref name="repoPath"/>. Selecting a file loads its diff.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _baseHash = null;
        _mode = CompareMode.Commit;

        LoadFileList(
            () => DiffService.GetChangedFiles(repoPath, commitHash),
            count => $"{commitHash}  —  {count} changed file(s)",
            $"Loading changed files for {commitHash}…");
    }

    /// <summary>
    ///  Loads the changed-files list and per-file diffs for the range
    ///  <paramref name="baseHash"/>..<paramref name="otherHash"/>
    ///  (i.e. <c>git diff &lt;base&gt; &lt;other&gt;</c>).
    /// </summary>
    public void ShowRange(string repoPath, string baseHash, string otherHash)
    {
        _repoPath = repoPath;
        _commitHash = otherHash;
        _baseHash = baseHash;
        _mode = CompareMode.Range;

        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = otherHash.Length > 8 ? otherHash[..8] : otherHash;

        LoadFileList(
            () => DiffService.GetDiffFilesBetween(repoPath, baseHash, otherHash),
            count => $"{shortBase} .. {shortOther}  —  {count} changed file(s)",
            $"Loading changed files for {shortBase}..{shortOther}…");
    }

    /// <summary>
    ///  Loads the changed-files list and per-file diffs comparing
    ///  <paramref name="commitHash"/> against the current working tree
    ///  (i.e. <c>git diff &lt;commit&gt;</c>).
    /// </summary>
    public void ShowAgainstWorkingDirectory(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _baseHash = null;
        _mode = CompareMode.WorkingTree;

        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;

        LoadFileList(
            () => DiffService.GetChangedFilesAgainstWorkingTree(repoPath, commitHash),
            count => $"{shortHash} .. working tree  —  {count} changed file(s)",
            $"Loading changes since {shortHash}…");
    }

    // Shared changed-file-list loader: clears the panes, loads the file rows off
    // the UI thread, then populates the list and auto-selects the first row so
    // its per-file diff loads via OnFileSelected (which dispatches on _mode).
    private void LoadFileList(
        Func<IReadOnlyList<DiffFileRow>> load,
        Func<int, string> statusFor,
        string loadingText)
    {
        _files.ItemsSource = null;
        _diff.Inlines?.Clear();
        _diff.Text = string.Empty;
        _currentDiffText = string.Empty;
        _status.Text = loadingText;

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DiffFileRow> rows = load();
                Dispatcher.UIThread.Post(() =>
                {
                    _files.ItemsSource = rows;
                    _status.Text = statusFor(rows.Count);
                    if (rows.Count > 0)
                    {
                        _files.SelectedIndex = 0;
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = "Error: " + ex.Message);
            }
        });
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_files.SelectedItem is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        // Cancel any in-flight diff load for a previously selected file.
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        string repoPath = _repoPath;
        string commitHash = _commitHash;
        string? baseHash = _baseHash;
        CompareMode mode = _mode;

        _diff.Inlines?.Clear();
        _diff.Text = "Loading diff…";

        _ = Task.Run(async () =>
        {
            try
            {
                string text = mode switch
                {
                    CompareMode.Range =>
                        await DiffService.GetFileDiffBetweenAsync(repoPath, baseHash!, commitHash, row, token),
                    CompareMode.WorkingTree =>
                        await DiffService.GetFileDiffAgainstWorkingTreeAsync(repoPath, commitHash, row, token),
                    _ =>
                        await DiffService.GetFileDiffAsync(repoPath, commitHash, row, token),
                };
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        RenderDiff(text);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another selection; ignore.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _diff.Inlines?.Clear();
                        _diff.Text = "Error: " + ex.Message;
                    }
                });
            }
        });
    }

    // Colour each diff line: added green, removed red, hunk headers blue,
    // file/meta headers gray.
    private void RenderDiff(string diffText)
    {
        _currentDiffText = diffText;
        _diff.Text = string.Empty;
        InlineCollection inlines = _diff.Inlines ??= [];
        inlines.Clear();

        foreach (string line in diffText.Split('\n'))
        {
            IBrush? brush = null;

            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("new file", StringComparison.Ordinal) ||
                line.StartsWith("deleted file", StringComparison.Ordinal) ||
                line.StartsWith("rename ", StringComparison.Ordinal) ||
                line.StartsWith("copy ", StringComparison.Ordinal) ||
                line.StartsWith("similarity ", StringComparison.Ordinal))
            {
                brush = B("App.TextDim");
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                brush = B("App.Accent");
            }
            else if (line.StartsWith('+'))
            {
                brush = AddedBrush;
            }
            else if (line.StartsWith('-'))
            {
                brush = RemovedBrush;
            }

            Run run = new(line + "\n");
            if (brush is not null)
            {
                run.Foreground = brush;
            }

            inlines.Add(run);
        }
    }
}
