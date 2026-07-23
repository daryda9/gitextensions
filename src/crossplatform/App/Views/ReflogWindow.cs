using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal reflog browser for the Avalonia port: lists the HEAD reflog
///  (selector, short hash, date, action) read via <see cref="ReflogService"/>,
///  and offers per-entry "Copy hash" and "Checkout this" (a detached checkout of
///  the entry's commit through <see cref="BranchTagService"/>). All git work
///  runs off the UI thread via <see cref="Task.Run"/> and marshals back with
///  <see cref="Dispatcher.UIThread"/>.
///
///  <see cref="CheckedOut"/> is set when a checkout succeeds so the caller can
///  refresh the main view after the window closes. Styled from the shared App.*
///  brushes so it matches the active (dark) theme, mirroring
///  <see cref="RemotesDialog"/>.
/// </summary>
public sealed class ReflogWindow : Window
{
    private readonly ReflogService _service = new();
    private readonly string _repoPath;
    private readonly ListBox _list;
    private readonly Button _copyHash;
    private readonly Button _checkout;
    private readonly TextBlock _status;

    private bool _busy;

    /// <summary>
    ///  True when a checkout from the reflog succeeded, so the owner can refresh
    ///  its views once the window is dismissed.
    /// </summary>
    public bool CheckedOut { get; private set; }

    public ReflogWindow(string repoPath)
    {
        _repoPath = repoPath;

        Title = "Reflog";
        Width = 760;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        _list = new ListBox
        {
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            FontFamily = new FontFamily("monospace"),
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, _) => DoCheckout();

        _copyHash = MakeButton("Copy hash");
        _checkout = MakeButton("Checkout this");
        Button refresh = MakeButton("Refresh");
        Button close = MakeButton("Close");

        _copyHash.Click += (_, _) => _ = DoCopyHashAsync();
        _checkout.Click += (_, _) => DoCheckout();
        refresh.Click += (_, _) => ReloadList();
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Width = 130,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(_copyHash);
        buttons.Children.Add(_checkout);
        buttons.Children.Add(refresh);
        buttons.Children.Add(close);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_list, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(_list);
        row.Children.Add(buttons);

        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);
        body.Children.Add(row);
        Content = body;

        Opened += (_, _) => ReloadList();
        UpdateButtons();
    }

    private ReflogEntry? Selected => _list.SelectedItem as ReflogEntry;

    private void UpdateButtons()
    {
        bool has = Selected is not null;
        _copyHash.IsEnabled = has;
        _checkout.IsEnabled = has;
    }

    private void ReloadList()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _status.Text = "Reading reflog…";
        _ = Task.Run(() =>
        {
            IReadOnlyList<ReflogEntry> entries;
            try
            {
                entries = _service.Read(_repoPath);
            }
            catch
            {
                entries = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                _list.ItemsSource = entries;
                _status.Text = entries.Count > 0
                    ? $"{entries.Count} reflog entr{(entries.Count == 1 ? "y" : "ies")}."
                    : "No reflog entries (or not a git repository).";
                UpdateButtons();
            });
        });
    }

    private async Task DoCopyHashAsync()
    {
        if (Selected is not { } entry)
        {
            return;
        }

        if (Clipboard is { } clip)
        {
            await clip.SetTextAsync(entry.ShortHash);
            _status.Text = $"Copied {entry.ShortHash} to the clipboard.";
        }
    }

    // Detached checkout of the selected entry's commit; on success flags
    // CheckedOut so the owner refreshes, and reports git's outcome inline.
    private void DoCheckout()
    {
        if (_busy || Selected is not { } entry)
        {
            return;
        }

        _busy = true;
        _status.Text = $"Checking out {entry.ShortHash}…";
        string hash = entry.ShortHash;
        _ = Task.Run(() =>
        {
            BranchTagResult result;
            try
            {
                result = new BranchTagService().Checkout(_repoPath, hash);
            }
            catch (Exception ex)
            {
                result = new BranchTagResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (result.Success)
                {
                    CheckedOut = true;
                    _status.Text = $"Checked out {hash} (detached).";
                }
                else
                {
                    _status.Text = $"Checkout failed: {result.Output}";
                }
            });
        });
    }

    private static Button MakeButton(string text)
        => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;
}
