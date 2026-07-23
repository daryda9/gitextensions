using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A read-only view of a single commit's metadata: a compact header
///  (hash, author, dates, committer, parents) above the full commit message.
///  Heavy git work is performed off the UI thread, matching <see cref="DiffView"/>.
/// </summary>
public sealed class CommitDetailView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private readonly CommitDetailService _service = new();

    private readonly SelectableTextBlock _hash;
    private readonly SelectableTextBlock _author;
    private readonly SelectableTextBlock _authorDate;
    private readonly SelectableTextBlock _committer;
    private readonly SelectableTextBlock _commitDate;
    private readonly SelectableTextBlock _parents;
    private readonly SelectableTextBlock _message;
    private readonly TextBlock _status;

    private CancellationTokenSource? _cts;

    public CommitDetailView()
    {
        _status = new TextBlock
        {
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("App.Toolbar"),
            Foreground = B("App.Text"),
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = "No commit selected.",
        };

        _hash = CreateValue(monospace: true);
        _author = CreateValue(monospace: false);
        _authorDate = CreateValue(monospace: false);
        _committer = CreateValue(monospace: false);
        _commitDate = CreateValue(monospace: false);
        _parents = CreateValue(monospace: true);

        Grid header = new()
        {
            Margin = new Thickness(14, 10, 14, 8),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
        };

        AddRow(header, 0, "Hash", _hash);
        AddRow(header, 1, "Author", _author);
        AddRow(header, 2, "Author Date", _authorDate);
        AddRow(header, 3, "Committer", _committer);
        AddRow(header, 4, "Commit Date", _commitDate);
        AddRow(header, 5, "Parents", _parents);

        _message = new SelectableTextBlock
        {
            FontFamily = Monospace,
            Foreground = B("App.Text"),
            Margin = new Thickness(14, 10, 14, 14),
            TextWrapping = TextWrapping.Wrap,
        };

        ScrollViewer messageScroll = new()
        {
            Content = _message,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Border separator = new()
        {
            Height = 1,
            Background = B("App.Border"),
            Margin = new Thickness(14, 0, 14, 0),
        };

        DockPanel root = new() { Background = B("App.Panel") };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(separator, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(header);
        root.Children.Add(separator);
        root.Children.Add(messageScroll);

        Content = root;
    }

    /// <summary>
    ///  Loads and displays the metadata for <paramref name="commitHash"/> in the
    ///  repository at <paramref name="repoPath"/>.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        Clear();
        _status.Text = $"Loading commit {commitHash}…";

        _ = Task.Run(() =>
        {
            try
            {
                CommitDetailInfo? detail = _service.LoadCommit(repoPath, commitHash, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (detail is null)
                    {
                        _status.Text = $"Commit not found: {commitHash}";
                        return;
                    }

                    Render(detail);
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
                        _status.Text = "Error: " + ex.Message;
                    }
                });
            }
        });
    }

    private void Render(CommitDetailInfo detail)
    {
        _status.Text = detail.Subject;
        _hash.Text = $"{detail.ShortHash}  ({detail.Hash})";
        _author.Text = detail.Author;
        _authorDate.Text = detail.AuthorDate;
        _committer.Text = detail.Committer;
        _commitDate.Text = detail.CommitDate;
        _parents.Text = string.IsNullOrEmpty(detail.ParentsDisplay) ? "(none)" : detail.ParentsDisplay;
        _message.Text = detail.Message;
    }

    private void Clear()
    {
        _hash.Text = string.Empty;
        _author.Text = string.Empty;
        _authorDate.Text = string.Empty;
        _committer.Text = string.Empty;
        _commitDate.Text = string.Empty;
        _parents.Text = string.Empty;
        _message.Text = string.Empty;
    }

    private static SelectableTextBlock CreateValue(bool monospace)
    {
        SelectableTextBlock block = new()
        {
            Foreground = B("App.Text"),
            Margin = new Thickness(0, 3, 0, 3),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (monospace)
        {
            block.FontFamily = Monospace;
        }

        return block;
    }

    private static void AddRow(Grid grid, int row, string label, Control value)
    {
        TextBlock labelBlock = new()
        {
            Text = label,
            Foreground = B("App.TextDim"),
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 3, 16, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(value);
    }
}
