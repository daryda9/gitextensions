using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Minimal modal for cloning a repository: a URL text box, a target parent
///  directory field with a native "Browse…" folder picker
///  (<see cref="IStorageProvider"/>), and Clone / Cancel buttons. The actual
///  <c>git clone</c> runs off the UI thread via <see cref="CloneInitService"/>;
///  progress and errors are shown inline. On success the dialog closes and the
///  resolved repository path is exposed through <see cref="ClonedRepoPath"/>.
///
///  Styled from the shared App.* brushes so it matches the active (dark) theme.
/// </summary>
public sealed class CloneDialog : Window
{
    private readonly TextBox _url;
    private readonly TextBox _parentDir;
    private readonly TextBlock _status;
    private readonly Button _clone;
    private readonly Button _browse;
    private readonly Button _cancel;

    /// <summary>The working-directory path of the freshly cloned repository, or null if the dialog was cancelled / failed.</summary>
    public string? ClonedRepoPath { get; private set; }

    public CloneDialog()
    {
        IBrush window = (IBrush)Application.Current!.Resources["App.Window"]!;
        IBrush text = Brush("App.Text", "#DCDCDC");

        Title = "Clone repository";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        _url = new TextBox { Watermark = "https://… or git@…:… or /path/to/repo.git" };
        _parentDir = new TextBox { Watermark = "Directory to clone into" };

        _browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        _browse.Click += (_, _) => _ = BrowseAsync();

        Grid dirRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetColumn(_parentDir, 0);
        Grid.SetColumn(_browse, 1);
        dirRow.Children.Add(_parentDir);
        dirRow.Children.Add(_browse);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };

        _clone = new Button { Content = "Clone", MinWidth = 80, IsDefault = true };
        _clone.Click += (_, _) => _ = CloneAsync();
        _cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        _cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Repository to clone:", Foreground = text, Margin = new Thickness(0, 0, 0, 4) },
                _url,
                new TextBlock { Text = "Destination:", Foreground = text, Margin = new Thickness(0, 12, 0, 4) },
                dirRow,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { _clone, _cancel },
                },
            },
        };
    }

    private async Task BrowseAsync()
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Choose a directory to clone into",
            });

            if (folders.Count == 0)
            {
                return;
            }

            string? localPath = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
            {
                _parentDir.Text = localPath;
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
        }
    }

    private async Task CloneAsync()
    {
        string url = _url.Text ?? string.Empty;
        string parent = _parentDir.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            _status.Text = "Enter a repository URL.";
            return;
        }

        if (string.IsNullOrWhiteSpace(parent))
        {
            _status.Text = "Choose a destination directory.";
            return;
        }

        SetBusy(true);
        _status.Text = "Cloning…";

        CloneInitResult result;
        try
        {
            result = await Task.Run(() => new CloneInitService().Clone(url, parent));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = "Clone failed: " + ex.Message;
            return;
        }

        if (result.Success && result.RepoPath is not null)
        {
            ClonedRepoPath = result.RepoPath;
            await Dispatcher.UIThread.InvokeAsync(Close);
            return;
        }

        SetBusy(false);
        _status.Text = "Clone failed:\n" + result.Output;
    }

    private void SetBusy(bool busy)
    {
        _clone.IsEnabled = !busy;
        _browse.IsEnabled = !busy;
        _url.IsEnabled = !busy;
        _parentDir.IsEnabled = !busy;
    }

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
