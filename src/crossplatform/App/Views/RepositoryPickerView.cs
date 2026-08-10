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

    // Promoted from a local so ApplyTranslations can re-caption it.
    private readonly Button _browseButton;

    // True while the status line still shows the idle invitation. Every other status
    // it can hold names a path or quotes an exception, so a language change must
    // leave those alone rather than overwrite a real message with the greeting.
    private bool _statusIsGreeting = true;

    /// <summary>
    ///  Raised with the resolved repository root when the user picks a valid
    ///  repository (either via Browse… or the recent list). The MRU entry has
    ///  already been recorded when this fires.
    /// </summary>
    public event Action<string>? RepositorySelected;

    public RepositoryPickerView()
    {
        _browseButton = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        _browseButton.Click += OnBrowseClick;

        _status = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = (IBrush)Application.Current!.Resources["App.TextDim"]!,
            TextWrapping = TextWrapping.Wrap,
        };

        _recentHeader = new TextBlock
        {
            Margin = new Thickness(0, 16, 0, 4),
            FontWeight = FontWeight.Bold,
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
                _browseButton,
                _status,
                _recentHeader,
                _recentList,
            },
        };

        Content = root;

        ApplyTranslations();

        // A panel, not a window: the subscription is taken when the view enters the
        // visual tree and dropped when it leaves, so a picker that is replaced by the
        // browse view cannot keep the static event alive.
        AttachedToVisualTree += (_, _) => TranslationService.LanguageChanged += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        // Populate the recent list without blocking construction.
        Refresh();
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
            await Dispatcher.UIThread.InvokeAsync(() => SetStatus(
                TranslationService.TFormat(null, "Could not load recent repositories: {0}", ex.Message)));
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
                Title = T("FormOpenDirectory/$this.Text", "Open Git repository"),
            });

            if (folders.Count == 0)
            {
                return;
            }

            string? localPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(localPath))
            {
                SetStatus(T("The selected folder has no local path."));
                return;
            }

            await SelectRepositoryAsync(localPath);
        }
        catch (Exception ex)
        {
            SetStatus(ErrorText(ex));
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
            SetStatus(T("Validating…"));

            // git working-dir validation touches the filesystem — keep it off the UI thread.
            string? repoRoot = await Task.Run(() => FindRepositoryRoot(candidatePath));

            if (repoRoot is null)
            {
                // The path is data: it is interpolated into the translated sentence,
                // never translated itself.
                SetStatus(TranslationService.TFormat(
                    null, "Not a git repository: {0}", candidatePath));
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
                SetStatus(TranslationService.TFormat(null, "Opening {0}", repoRoot));
                RepositorySelected?.Invoke(repoRoot);
            });

            // Reflect the new most-recent entry.
            Refresh();
        }
        catch (Exception ex)
        {
            SetStatus(ErrorText(ex));
        }
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        // FormFilePrompt's Browse button is the only id whose source carries the
        // trailing ellipsis this button has; Restyle keeps the port's "…" glyph.
        _browseButton.Content = T("FormFilePrompt/btnBrowse.Text", "Browse…");
        _recentHeader.Text = T("UserRepositoriesList/_groupRecentRepositories.Text", "Recent repositories");

        if (_statusIsGreeting)
        {
            _status.Text = T("Open a git repository, or pick one from the recent list.");
        }
    }

    // Every status other than the initial invitation reports something that already
    // happened, so writing one turns the greeting flag off for good.
    private void SetStatus(string text)
    {
        _statusIsGreeting = false;
        _status.Text = text;
    }

    private static string ErrorText(Exception ex)
        => TranslationService.TFormat(
            null, "{0}: {1}", TranslationService.T("TranslatedStrings/_error.Text", "Error"), ex.Message);

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

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
