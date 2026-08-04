using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  "Create new repository" — the port of upstream's <c>FormInit</c>
///  (<c>FormInit.Designer.cs:72-128,180-195</c>), which the Avalonia app never had:
///  the menu entry opened a bare folder picker and ran a plain <c>git init</c>, so
///  the <b>Central</b> repository type (<c>--bare --shared=all</c>) — the one you
///  pick when creating a repository to push to — could not be reached at all.
///
///  <para>
///  The window has the editable directory field with its history and
///  directory-name completion (upstream's combo with
///  <c>AutoCompleteSource.FileSystemDirectories</c>), a Browse button, and the
///  <b>Repository type</b> group: Personal (plain <c>git init</c>) or Central
///  (<c>git init --bare --shared=all</c>).
///  </para>
///
///  <para>
///  git runs in <c>Task.Run</c>; the completion candidates are enumerated off the
///  UI thread too, because listing a slow or huge directory on every keystroke
///  would stutter the field. Escape closes the window (M57 convention). On success
///  the created repository's path is exposed through <see cref="CreatedRepoPath"/> —
///  for a central repository that path has no working directory, so the host should
///  report it rather than try to open it as one (see <see cref="IsCentral"/>).
///  </para>
/// </summary>
public sealed class InitDialog : Theming.ZoomWindow
{
    private readonly AutoCompleteBox _directory;
    private readonly RadioButton _personal;
    private readonly RadioButton _central;
    private readonly TextBlock _status;
    private readonly Button _init;
    private readonly Button _browse;

    private IReadOnlyList<string> _history = [];
    private CancellationTokenSource? _completion;

    /// <summary>Path of the repository that was created, or null if the dialog was cancelled / failed.</summary>
    public string? CreatedRepoPath { get; private set; }

    /// <summary>Whether the created repository is a bare "central" one (no working directory).</summary>
    public bool IsCentral { get; private set; }

    public InitDialog(string? initialDirectory = null)
    {
        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#9B9B9B");

        Title = T("FormInit/$this.Text", "Create new repository");
        Width = 600;

        // An explicit height, NOT SizeToContent.Height: a window manager that ignores
        // the resize request that SizeToContent issues leaves the window at its old
        // size while Avalonia only paints the measured content, and the rest of the
        // surface stays unpainted (a white band under the buttons — visible on every
        // screenshot of this dialog before this change). Every other dialog in the port
        // sizes itself explicitly for the same reason.
        Height = 290;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", "#1E1E1E");

        _directory = new AutoCompleteBox
        {
            Watermark = T("Directory for the new repository"),
            FilterMode = AutoCompleteFilterMode.StartsWithOrdinalCaseSensitive,
            MinimumPrefixLength = 0,

            // Upstream's fallback when no directory is handed in (FormInit.cs:45):
            // the configured default clone destination. Unset by default, in which
            // case the field simply starts empty as before.
            Text = string.IsNullOrEmpty(initialDirectory)
                ? AppSettings.DefaultCloneDestinationPath
                : initialDirectory,
        };
        _directory.TextChanged += (_, _) => RefreshCompletions();

        _browse = new Button { Content = T("Browse…"), Margin = new Thickness(8, 0, 0, 0) };
        _browse.Click += (_, _) => _ = BrowseAsync();

        Grid dirRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetColumn(_directory, 0);
        Grid.SetColumn(_browse, 1);
        dirRow.Children.Add(_directory);
        dirRow.Children.Add(_browse);

        _personal = new RadioButton
        {
            GroupName = "InitRepositoryType",
            Content = T("FormInit/Personal.Text", "Personal repository"),
            Foreground = text,
            IsChecked = true,
        };
        _central = new RadioButton
        {
            GroupName = "InitRepositoryType",
            Content = T(
                "FormInit/Central.Text",
                "Central repository, no working directory  (--bare --shared=all)"),
            Foreground = text,
        };

        _status = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
        };

        _init = new Button { Content = T("FormInit/Init.Text", "Create"), MinWidth = 90, IsDefault = true };
        _init.Click += (_, _) => _ = InitAsync();
        Button cancel = new()
        {
            Content = T("Cancel"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = T("FormInit/label1.Text", "Directory"), Foreground = text },
                dirRow,
                new TextBlock
                {
                    Text = T("FormInit/groupBox1.Text", "Repository type"),
                    Foreground = dim,
                    Margin = new Thickness(0, 16, 0, 6),
                },
                _personal,
                _central,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { _init, cancel },
                },
            },
        };

        // Escape = Close (upstream's CancelButton). Bubbling, so the completion
        // popup keeps first refusal on its own Escape.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Bubble);

        DialogKeys.EnsureFocusRoute(this);

        _ = LoadHistoryAsync();
    }

    // Seeds the dropdown with the recently used repositories, as upstream's combo
    // does (its DataSource is RepositoryHistoryManager.Locals).
    private async Task LoadHistoryAsync()
    {
        IReadOnlyList<string> history;
        try
        {
            history = await new RecentRepositoriesService().LoadAsync();
        }
        catch (Exception)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _history = history;
            if (_directory.ItemsSource is null)
            {
                _directory.ItemsSource = history;
            }
        });
    }

    // Directory-name completion: offer the sibling directories of whatever has been
    // typed so far, plus the history. Enumerating a directory can block (network
    // mounts, huge trees), so it happens off the UI thread and a newer keystroke
    // cancels the previous lookup.
    private void RefreshCompletions()
    {
        string typed = _directory.Text ?? string.Empty;

        _completion?.Cancel();
        _completion = new CancellationTokenSource();
        CancellationToken token = _completion.Token;

        _ = Task.Run(
            () =>
            {
                List<string> candidates = [.. _history];

                try
                {
                    string? parent = typed.EndsWith(Path.DirectorySeparatorChar)
                        ? typed
                        : Path.GetDirectoryName(typed);

                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    {
                        candidates.AddRange(Directory.EnumerateDirectories(parent).Take(200));
                    }
                }
                catch (Exception)
                {
                    // Unreadable or vanished directory: history-only completion.
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _directory.ItemsSource = candidates;
                    }
                });
            },
            token);
    }

    private async Task BrowseAsync()
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = T("Choose a directory for the new repository"),
            });

            if (folders.Count == 0)
            {
                return;
            }

            string? localPath = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
            {
                _directory.Text = localPath;
            }
        }
        catch (Exception ex)
        {
            _status.Text = T("Error: ") + ex.Message;
        }
    }

    private async Task InitAsync()
    {
        string dir = (_directory.Text ?? string.Empty).Trim();

        // Upstream refuses a relative path outright (FormInit.IsRootedDirectoryPath):
        // "git init foo" would silently land wherever the process happens to be.
        if (dir.Length == 0 || !Path.IsPathRooted(dir))
        {
            _status.Text = T("FormInit/_chooseDirectory.Text", "Please choose a directory.");
            return;
        }

        if (File.Exists(dir))
        {
            _status.Text = T(
                "FormInit/_chooseDirectoryNotFile.Text",
                "Cannot initialize a new repository on a file. Please choose a directory.");
            return;
        }

        bool central = _central.IsChecked == true;
        SetBusy(true);
        _status.Text = T("Creating repository…");

        CloneInitResult result;
        try
        {
            result = await Task.Run(() => new CloneInitService().Init(dir, central));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = T("Could not create the repository: ") + ex.Message;
            return;
        }

        if (!result.Success || result.RepoPath is null)
        {
            SetBusy(false);
            _status.Text = T("Could not create the repository:") + Environment.NewLine + result.Output;
            return;
        }

        CreatedRepoPath = result.RepoPath;
        IsCentral = central;

        // Same bookkeeping upstream does on success, so the new repository shows up
        // in the recent list straight away.
        try
        {
            await new RecentRepositoriesService().AddAsync(result.RepoPath);
        }
        catch (Exception)
        {
            // A failed history write must not fail the creation.
        }

        Close();
    }

    private void SetBusy(bool busy)
    {
        _init.IsEnabled = !busy;
        _browse.IsEnabled = !busy;
        _directory.IsEnabled = !busy;
        _personal.IsEnabled = !busy;
        _central.IsEnabled = !busy;
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
