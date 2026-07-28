using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Repository picker: a native "Browse…" folder dialog plus a clickable list of
///  recently-used repositories. Replaces the WinForms open-repo stub.
///
///  Selecting a folder validates that it is inside a git working directory
///  (walking up to the repository root), records it as most-recently-used, then
///  raises <see cref="RepositorySelected"/> with the resolved repository root.
///  All git/IO work runs off the UI thread; UI updates are marshalled back via
///  <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class RepositoryPickerView : UserControl
{
    private readonly RecentRepositoriesService _recentRepositories = new();
    private readonly ListBox _recentList;
    private readonly TextBlock _status;
    private readonly TextBlock _recentHeader;

    // Guards against re-installing when the control is detached and re-attached.
    private bool _escapeInstalled;

    /// <summary>
    ///  Raised with the resolved repository root when the user picks a valid
    ///  repository (either via Browse… or the recent list). The MRU entry has
    ///  already been recorded when this fires.
    /// </summary>
    public event Action<string>? RepositorySelected;

    public RepositoryPickerView()
    {
        Button browseButton = new()
        {
            Content = "Browse…",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        browseButton.Click += OnBrowseClick;

        _status = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Text = "Open a git repository, or pick one from the recent list.",
        };

        _recentHeader = new TextBlock
        {
            Margin = new Thickness(0, 16, 0, 4),
            FontWeight = FontWeight.Bold,
            Text = "Recent repositories",
        };

        _recentList = new ListBox
        {
            MinHeight = 120,
        };
        _recentList.SelectionChanged += OnRecentSelectionChanged;

        StackPanel root = new()
        {
            Margin = new Thickness(12),
            Background = (IBrush)Application.Current!.Resources["App.Window"]!,
            Children =
            {
                browseButton,
                _status,
                _recentHeader,
                _recentList,
            },
        };

        Content = root;

        // Populate the recent list without blocking construction.
        Refresh();
    }

    /// <summary>
    ///  Wires Escape to close the dialog this picker is shown in.
    ///
    ///  <para>The "Open Git repository" dialog is assembled inline by the caller as a
    ///  bare <see cref="Window"/> wrapped around this control, with no cancel button
    ///  and no key handling, so Escape did nothing. The wiring belongs on that window,
    ///  but the picker is its only content and its only consumer, so installing from
    ///  here fixes it without reaching into the caller.</para>
    ///
    ///  <para>The <c>Content == this</c> test is the guard that keeps it honest: it
    ///  only fires when the picker <em>is</em> the whole window, i.e. the Open dialog.
    ///  Were the picker ever embedded in a larger window — the dashboard, say — Escape
    ///  must not close that window, and this check makes sure it cannot.</para>
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (!_escapeInstalled
            && this.GetVisualRoot() is Window window
            && ReferenceEquals(window.Content, this))
        {
            _escapeInstalled = true;
            DialogKeys.InstallEscapeClose(window);
        }
    }

    /// <summary>
    ///  Reloads the recent-repositories list from storage. Safe to call from any
    ///  thread; the reload runs in the background and updates the UI on the UI
    ///  thread.
    /// </summary>
    public void Refresh()
    {
        _ = ReloadRecentAsync();
    }

    private async Task ReloadRecentAsync()
    {
        IReadOnlyList<string> recent;
        try
        {
            recent = await _recentRepositories.LoadAsync();
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "Could not load recent repositories: " + ex.Message);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _recentList.SelectionChanged -= OnRecentSelectionChanged;
            _recentList.ItemsSource = recent;
            _recentList.SelectedItem = null;
            _recentList.SelectionChanged += OnRecentSelectionChanged;

            bool any = recent.Count > 0;
            _recentHeader.IsVisible = any;
            _recentList.IsVisible = any;
        });
    }

    // Event handler: synchronously delegates to the guarded async core so the
    // handler itself is not "async void" (exceptions are contained below).
    private void OnBrowseClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => _ = BrowseAsync();

    private async Task BrowseAsync()
    {
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (top is null)
            {
                return;
            }

            IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Open Git repository",
            });

            if (folders.Count == 0)
            {
                return;
            }

            string? localPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(localPath))
            {
                _status.Text = "The selected folder has no local path.";
                return;
            }

            await SelectRepositoryAsync(localPath);
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
        }
    }

    // Event handler: synchronously delegates to the guarded async core (see above).
    private void OnRecentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_recentList.SelectedItem is not string path || string.IsNullOrEmpty(path))
        {
            return;
        }

        // Reset selection so the same entry can be picked again later.
        _recentList.SelectedItem = null;

        _ = SelectRepositoryAsync(path);
    }

    private async Task SelectRepositoryAsync(string candidatePath)
    {
        try
        {
            _status.Text = "Validating…";

            // git working-dir validation touches the filesystem — keep it off the UI thread.
            string? repoRoot = await Task.Run(() => FindRepositoryRoot(candidatePath));

            if (repoRoot is null)
            {
                _status.Text = $"Not a git repository: {candidatePath}";
                return;
            }

            try
            {
                await _recentRepositories.AddAsync(repoRoot);
            }
            catch
            {
                // Recording the MRU entry is best-effort; still open the repository.
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _status.Text = $"Opening {repoRoot}";
                RepositorySelected?.Invoke(repoRoot);
            });

            // Reflect the new most-recent entry.
            Refresh();
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
        }
    }

    // Accept a subdirectory: walk up until a git working dir (repository root) is found.
    private static string? FindRepositoryRoot(string path)
    {
        try
        {
            DirectoryInfo? dir = new(path);
            while (dir is not null)
            {
                if (GitModule.IsValidGitWorkingDir(dir.FullName))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // fall through to null
        }

        return null;
    }
}
