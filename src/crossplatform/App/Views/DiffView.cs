using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
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

    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
    private static readonly IBrush HunkBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xD6));
    private static readonly IBrush MetaBrush = Brushes.Gray;

    private readonly ListBox _files;
    private readonly SelectableTextBlock _diff;
    private readonly TextBlock _status;

    private string? _repoPath;
    private string? _commitHash;
    private CancellationTokenSource? _diffCts;

    public DiffView()
    {
        _files = new ListBox
        {
            FontFamily = Monospace,
        };
        _files.SelectionChanged += OnFileSelected;

        _diff = new SelectableTextBlock
        {
            FontFamily = Monospace,
            Margin = new Thickness(8),
            TextWrapping = TextWrapping.NoWrap,
        };

        ScrollViewer diffScroll = new()
        {
            Content = _diff,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 6),
            Foreground = Brushes.Gray,
            Text = "No commit selected.",
        };

        Grid split = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
        };

        Grid.SetColumn(_files, 0);
        _files.Width = 320;

        GridSplitter splitter = new()
        {
            Width = 4,
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, 1);

        Grid.SetColumn(diffScroll, 2);

        split.Children.Add(_files);
        split.Children.Add(splitter);
        split.Children.Add(diffScroll);

        DockPanel root = new();
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(split);

        Content = root;
    }

    /// <summary>
    ///  Loads the changed-files list for <paramref name="commitHash"/> in the
    ///  repository at <paramref name="repoPath"/>. Selecting a file loads its diff.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;

        _files.ItemsSource = null;
        _diff.Inlines?.Clear();
        _diff.Text = string.Empty;
        _status.Text = $"Loading changed files for {commitHash}…";

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DiffFileRow> rows = DiffService.GetChangedFiles(repoPath, commitHash);
                Dispatcher.UIThread.Post(() =>
                {
                    _files.ItemsSource = rows;
                    _status.Text = $"{commitHash}  —  {rows.Count} changed file(s)";
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

        _diff.Inlines?.Clear();
        _diff.Text = "Loading diff…";

        _ = Task.Run(async () =>
        {
            try
            {
                string text = await DiffService.GetFileDiffAsync(repoPath, commitHash, row, token);
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
                brush = MetaBrush;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                brush = HunkBrush;
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
