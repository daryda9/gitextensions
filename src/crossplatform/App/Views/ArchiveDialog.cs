using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Minimal modal for archiving a single commit: a format selector (zip /
///  tar.gz), an output-path field with a native "Browse…" save-file picker
///  (<see cref="IStorageProvider"/>), and Archive / Cancel buttons. The actual
///  <c>git archive</c> runs off the UI thread via <see cref="RevertArchiveService"/>;
///  progress and errors are shown inline. On success the dialog closes and the
///  resolved output path is exposed through <see cref="ArchivedPath"/>.
///
///  Styled from the shared App.* brushes so it matches the active (dark) theme.
/// </summary>
public sealed class ArchiveDialog : Window
{
    private readonly string _repoPath;
    private readonly string _commitHash;

    private readonly ComboBox _format;
    private readonly TextBox _outputPath;
    private readonly TextBlock _status;
    private readonly Button _archive;
    private readonly Button _browse;
    private readonly Button _cancel;

    /// <summary>The path of the written archive, or null if the dialog was cancelled / failed.</summary>
    public string? ArchivedPath { get; private set; }

    public ArchiveDialog(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;

        IBrush window = (IBrush)Application.Current!.Resources["App.Window"]!;
        IBrush text = Brush("App.Text", "#DCDCDC");

        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;
        Title = $"Archive commit {shortHash}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        _format = new ComboBox
        {
            ItemsSource = new[] { "zip", "tar.gz" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 140,
        };
        _format.SelectionChanged += (_, _) => SyncExtension();

        _outputPath = new TextBox { Watermark = "Output file path" };

        _browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        _browse.Click += (_, _) => _ = BrowseAsync();

        Grid pathRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetColumn(_outputPath, 0);
        Grid.SetColumn(_browse, 1);
        pathRow.Children.Add(_outputPath);
        pathRow.Children.Add(_browse);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };

        _archive = new Button { Content = "Archive", MinWidth = 80, IsDefault = true };
        _archive.Click += (_, _) => _ = ArchiveAsync();
        _cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        _cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Format:", Foreground = text, Margin = new Thickness(0, 0, 0, 4) },
                _format,
                new TextBlock { Text = "Output file:", Foreground = text, Margin = new Thickness(0, 12, 0, 4) },
                pathRow,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { _archive, _cancel },
                },
            },
        };
    }

    private ArchiveFormat SelectedFormat => _format.SelectedIndex == 1 ? ArchiveFormat.TarGz : ArchiveFormat.Zip;

    private string Extension => SelectedFormat == ArchiveFormat.TarGz ? ".tar.gz" : ".zip";

    // Keeps the output path's extension in sync with the chosen format.
    private void SyncExtension()
    {
        string? current = _outputPath.Text;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        string trimmed = current;
        if (trimmed.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7];
        }
        else if (trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        _outputPath.Text = trimmed + Extension;
    }

    private async Task BrowseAsync()
    {
        try
        {
            string shortHash = _commitHash.Length > 8 ? _commitHash[..8] : _commitHash;
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save archive as",
                SuggestedFileName = $"{shortHash}{Extension}",
                DefaultExtension = Extension.TrimStart('.'),
            });

            if (file is null)
            {
                return;
            }

            string? localPath = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
            {
                _outputPath.Text = localPath;
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
        }
    }

    private async Task ArchiveAsync()
    {
        string path = _outputPath.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = "Choose an output file.";
            return;
        }

        SetBusy(true);
        _status.Text = "Archiving…";

        ArchiveFormat format = SelectedFormat;
        RevertArchiveResult result;
        try
        {
            result = await Task.Run(() => new RevertArchiveService().Archive(_repoPath, _commitHash, format, path));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = "Archive failed: " + ex.Message;
            return;
        }

        if (result.Success)
        {
            ArchivedPath = path;
            await Dispatcher.UIThread.InvokeAsync(Close);
            return;
        }

        SetBusy(false);
        _status.Text = "Archive failed:\n" + result.Output;
    }

    private void SetBusy(bool busy)
    {
        _archive.IsEnabled = !busy;
        _browse.IsEnabled = !busy;
        _format.IsEnabled = !busy;
        _outputPath.IsEnabled = !busy;
    }

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
