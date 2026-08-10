using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
///  "Clone repository" — the port of upstream's <c>FormClone</c>. The first
///  version was a URL box, a destination box and a blocking <c>git clone</c> that
///  printed a single "Cloning…" line; everything that makes a clone controllable
///  was missing. This version adds, from <c>FormClone.Designer.cs</c>:
///  <list type="bullet">
///   <item>the editable <b>subdirectory to create</b> (<c>:181-200</c>) — the name
///    used to be derived from the URL in silence — with the <b>destination
///    preview</b> and its "(Directory already exists)" / "(New directory)" hint
///    (<c>:250-260</c>);</item>
///   <item><b>Repository type</b>: personal or central, i.e. <c>--bare</c>
///    (<c>:262-298</c>);</item>
///   <item><b>Initialize all submodules</b>, ON by default (<c>:224-235</c>) — the
///    port never initialised submodules at clone time, so a cloned super-project
///    arrived with empty submodule directories;</item>
///   <item><b>Download full history</b> (<c>:237-248</c>); when off, the clone is
///    <c>--depth 1 --no-single-branch</c>;</item>
///   <item>the <b>branch</b> drop-down (<c>:214-222</c>), filled from
///    <c>ls-remote</c> when it is opened, with the two synthetic entries
///    "(default: remote HEAD)" and "(none: don't checkout)";</item>
///   <item><b>live output</b>: the clone streams through
///    <see cref="GitStreamRunner"/> into an output panel, so transfer progress is
///    visible while it runs.</item>
///  </list>
///
///  <para>
///  The destination directory is created when it does not exist — the service used
///  to refuse a destination whose parent was missing, which made cloning into a
///  fresh path fail for no good reason.
///  </para>
///
///  <para>
///  git runs in <c>Task.Run</c> and the output is posted to the UI thread. Escape
///  closes the window (M57 convention) unless a clone is running. On success the
///  dialog closes and the repository path is exposed through
///  <see cref="ClonedRepoPath"/>.
///  </para>
/// </summary>
public sealed class CloneDialog : Theming.ZoomWindow
{
    // A property, not a field: a static field initialiser can run before the font
    // manager exists, which would cache the fallback for the life of the process.
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

    /// <summary>Branch entry meaning "let git check out whatever the remote's HEAD points at".</summary>
    private static readonly string DefaultBranchItem = TranslationService.T(
        "FormClone/_branchDefaultRemoteHead.Text", "(default: remote HEAD)");

    /// <summary>Branch entry meaning "clone but check nothing out" (<c>--no-checkout</c>).</summary>
    private static readonly string NoBranchItem = TranslationService.T(
        "FormClone/_branchNone.Text", "(none: don't checkout)");

    private readonly TextBox _url;
    private readonly TextBox _parentDir;
    private readonly TextBox _subdirectory;
    private readonly TextBlock _destinationPreview;

    private readonly ComboBox _branches;
    private readonly CheckBox _initSubmodules;
    private readonly CheckBox _fullHistory;
    private readonly RadioButton _personal;
    private readonly RadioButton _central;

    /// <summary>Normal colour of the destination preview.</summary>
    private readonly IBrush _dim;

    /// <summary>
    ///  Colour of a destination preview that is incomplete or points at a non-empty
    ///  directory — upstream paints that label red. <c>App.DiffRemoved</c> is the
    ///  port's registered themed red; a hard-coded one would not follow the theme.
    /// </summary>
    private readonly IBrush _warning;

    private readonly TextBox _output;
    private readonly TextBlock _status;
    private readonly Button _clone;
    private readonly Button _browse;

    private string _autoSubdirectory = string.Empty;
    private bool _busy;
    private CancellationTokenSource? _branchLoad;

    /// <summary>The working-directory path of the freshly cloned repository, or null if the dialog was cancelled / failed.</summary>
    public string? ClonedRepoPath { get; private set; }

    public CloneDialog()
    {
        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#9B9B9B");
        IBrush border = Brush("App.Border", "#3F3F46");
        _dim = dim;
        _warning = Brush("App.DiffRemoved", "#E06C6C");

        Title = T("FormClone/$this.Text", "Clone repository");
        Width = 680;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", "#1E1E1E");

        _url = new TextBox { Watermark = "https://… or git@…:… or /path/to/repo.git" };
        _url.TextChanged += (_, _) =>
        {
            SyncDerivedSubdirectory();
            ResetBranches();
        };

        // Upstream seeds the destination with the configured default clone destination
        // (FormClone.cs:64). Unset by default, in which case the field starts empty.
        _parentDir = new TextBox
        {
            Watermark = T("Directory to clone into"),
            Text = AppSettings.DefaultCloneDestinationPath,
        };
        _parentDir.TextChanged += (_, _) => UpdateDestinationPreview();

        _subdirectory = new TextBox { Watermark = T("Subdirectory to create") };
        _subdirectory.TextChanged += (_, _) => UpdateDestinationPreview();

        _browse = new Button { Content = T("Browse…"), Margin = new Thickness(8, 0, 0, 0) };
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

        _destinationPreview = new TextBlock
        {
            Foreground = dim,
            FontFamily = Monospace,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        _branches = new ComboBox
        {
            ItemsSource = new[] { DefaultBranchItem, NoBranchItem },
            SelectedIndex = 0,
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // Upstream asks the remote when the drop-down opens (Branches_DropDown), not
        // on every keystroke of the URL: ls-remote is a network round-trip.
        _branches.DropDownOpened += (_, _) => LoadBranches();

        _initSubmodules = new CheckBox
        {
            Content = T("FormClone/cbIntializeAllSubmodules.Text", "Initialize all submodules"),
            IsChecked = true,
            Foreground = text,
        };
        _fullHistory = new CheckBox
        {
            Content = T("FormClone/cbDownloadFullHistory.Text", "Download full history"),
            IsChecked = true,
            Foreground = text,
        };

        _personal = new RadioButton
        {
            GroupName = "CloneRepositoryType",
            Content = T("FormClone/Personal.Text", "Personal repository"),
            IsChecked = true,
            Foreground = text,
        };
        _central = new RadioButton
        {
            GroupName = "CloneRepositoryType",
            Content = T("FormClone/CentralRepository.Text", "Central repository, no working directory  (--bare)"),
            Foreground = text,
        };

        // TextBoxSurface, not plain Background/Foreground: the Fluent theme repaints
        // the template's border element per state, and a style setter beats a local
        // value — so on the light theme focusing this console turned it white while
        // the console foreground stayed light grey, i.e. unreadable.
        _output = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = Monospace,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                MinHeight = 110,
            },
            Brush("App.ConsoleBackground", "#2D2D30"),
            Brush("App.ConsoleForeground", "#DCDCDC"),
            border: Brush("App.ConsoleBackground", "#2D2D30"),
            placeholderForeground: Brush("App.ConsoleForeground", "#DCDCDC"));
        ScrollViewer.SetHorizontalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_output, ScrollBarVisibility.Auto);

        _status = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _clone = new Button { Content = T("FormClone/Ok.Text", "Clone"), MinWidth = 90, IsDefault = true };
        _clone.Click += (_, _) => _ = CloneAsync();
        Button cancel = new()
        {
            Content = T("Cancel"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        StackPanel form = new();
        form.Children.Add(new TextBlock { Text = T("Repository to clone:"), Foreground = text });
        form.Children.Add(_url);
        form.Children.Add(new TextBlock
        {
            Text = T("Destination:"),
            Foreground = text,
            Margin = new Thickness(0, 12, 0, 0),
        });
        form.Children.Add(dirRow);
        form.Children.Add(new TextBlock
        {
            Text = T("Subdirectory to create:"),
            Foreground = text,
            Margin = new Thickness(0, 12, 0, 4),
        });
        form.Children.Add(_subdirectory);
        form.Children.Add(_destinationPreview);
        form.Children.Add(new TextBlock
        {
            Text = T("Branch:"),
            Foreground = text,
            Margin = new Thickness(0, 12, 0, 0),
        });
        form.Children.Add(_branches);
        form.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _initSubmodules, _fullHistory },
        });
        form.Children.Add(new TextBlock
        {
            Text = T("FormClone/groupBox1.Text", "Repository type"),
            Foreground = dim,
            Margin = new Thickness(0, 12, 0, 6),
        });
        form.Children.Add(_personal);
        form.Children.Add(_central);

        DockPanel outputDock = new() { Margin = new Thickness(0, 14, 0, 0) };
        TextBlock outputLabel = new()
        {
            Text = T("Output"),
            Foreground = dim,
            Margin = new Thickness(0, 0, 0, 4),
        };
        DockPanel.SetDock(outputLabel, Dock.Top);
        outputDock.Children.Add(outputLabel);
        outputDock.Children.Add(new Border
        {
            Child = _output,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
        });

        Grid buttonRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonRow.Children.Add(_status);
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _clone, cancel },
        };
        Grid.SetColumn(buttons, 1);
        buttonRow.Children.Add(buttons);

        Grid root = new()
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(form, 0);
        Grid.SetRow(outputDock, 1);
        Grid.SetRow(buttonRow, 2);
        root.Children.Add(form);
        root.Children.Add(outputDock);
        root.Children.Add(buttonRow);

        Content = root;
        DialogKeys.EnsureFocusRoute(this);

        UpdateDestinationPreview();

        // Escape = Close (upstream's CancelButton), but not while a clone is running:
        // the window owns the git process and closing would orphan it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && !_busy)
            {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Bubble);
    }

    /// <summary>The full path the clone will create — parent directory plus subdirectory.</summary>
    private string DestinationPath
    {
        get
        {
            string parent = (_parentDir.Text ?? string.Empty).Trim();
            string sub = (_subdirectory.Text ?? string.Empty).Trim();
            return parent.Length == 0 || sub.Length == 0 ? string.Empty : Path.Combine(parent, sub);
        }
    }

    // Keeps the subdirectory in step with the URL — the name git itself would pick —
    // until the user types one of their own.
    //
    // Whether the field is "the user's" is decided by comparing it with the last
    // value WE wrote, not by a flag flipped from the field's own TextChanged: that
    // event does not necessarily run before this method returns, so the flag could
    // latch on our own assignment and freeze the field empty forever.
    private void SyncDerivedSubdirectory()
    {
        string current = _subdirectory.Text ?? string.Empty;
        if (current.Length > 0 && current != _autoSubdirectory)
        {
            return;
        }

        _autoSubdirectory = RepositoryNameFromUrl(_url.Text ?? string.Empty);
        _subdirectory.Text = _autoSubdirectory;
    }

    // Upstream's destination preview (FormClone.cs:333-361): show the exact path and
    // say whether it already exists, because cloning into an existing directory
    // behaves very differently from creating a new one.
    //
    // Two details taken from upstream that the first port version got wrong:
    //  * a missing field is shown as a PLACEHOLDER inside the path ("[Destination]"),
    //    so the shape of the final path is visible before it is complete, instead of
    //    the preview blanking out into unrelated advice;
    //  * "already exists" is a warning only when the directory is NON-EMPTY. Cloning
    //    into an existing but empty directory is normal and git allows it, so warning
    //    about it trains the user to ignore the line. Both warning cases are coloured,
    //    as upstream colours the label red.
    private void UpdateDestinationPreview()
    {
        string parent = (_parentDir.Text ?? string.Empty).Trim();
        string sub = (_subdirectory.Text ?? string.Empty).Trim();

        bool unfilled = parent.Length == 0 || sub.Length == 0;
        string shown = Path.Combine(
            parent.Length == 0 ? $"[{T("Destination")}]" : parent,
            sub.Length == 0 ? $"[{T("Subdirectory to create")}]" : sub);

        if (unfilled)
        {
            _destinationPreview.Text = shown;
            _destinationPreview.Foreground = _warning;
            return;
        }

        bool existsNonEmpty = Directory.Exists(shown) && EnumerateSafely(shown);

        _destinationPreview.Text = existsNonEmpty
            ? $"{shown}  {T("FormClone/_infoDirectoryExists.Text", "(Directory already exists)")}"
            : $"{shown}  {T("FormClone/_infoNewDirectory.Text", "(New directory)")}";
        _destinationPreview.Foreground = existsNonEmpty ? _warning : _dim;
    }

    // Whether the directory holds anything. An unreadable directory counts as
    // "something is there": it is certainly not a clean target for a clone.
    private static bool EnumerateSafely(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception)
        {
            return true;
        }
    }

    // A new URL invalidates whatever the previous remote answered.
    private void ResetBranches()
    {
        _branchLoad?.Cancel();
        _branches.ItemsSource = new[] { DefaultBranchItem, NoBranchItem };
        _branches.SelectedIndex = 0;
    }

    // Fills the drop-down from `git ls-remote --heads`, keeping the two synthetic
    // entries on top. The remote call is a network round-trip, so it runs off the UI
    // thread and a newer request cancels the previous one.
    private void LoadBranches()
    {
        string url = (_url.Text ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return;
        }

        _branchLoad?.Cancel();
        _branchLoad = new CancellationTokenSource();
        CancellationToken token = _branchLoad.Token;

        object? selected = _branches.SelectedItem;
        _status.Text = T("Asking the remote for its branches…");

        _ = Task.Run(
            () =>
            {
                IReadOnlyList<string> branches = CloneInitService.ListRemoteBranches(url, token);

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

                    List<string> items = [DefaultBranchItem, NoBranchItem, .. branches];
                    _branches.ItemsSource = items;
                    _branches.SelectedItem = selected is string previous && items.Contains(previous)
                        ? previous
                        : DefaultBranchItem;

                    _status.Text = branches.Count > 0
                        ? TF("{0} branches on the remote.", branches.Count)
                        : T("The remote did not answer with a branch list.");
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
                Title = T("Choose a directory to clone into"),
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
            _status.Text = T("Error: ") + ex.Message;
        }
    }

    private async Task CloneAsync()
    {
        string url = (_url.Text ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            _status.Text = T("Enter a repository URL.");
            return;
        }

        string parent = (_parentDir.Text ?? string.Empty).Trim();
        if (parent.Length == 0)
        {
            _status.Text = T("Choose a destination directory.");
            return;
        }

        if (!Path.IsPathRooted(parent))
        {
            _status.Text = T("The destination must be an absolute path.");
            return;
        }

        if ((_subdirectory.Text ?? string.Empty).Trim().Length == 0)
        {
            _status.Text = T("Enter the name of the subdirectory to create.");
            return;
        }

        string destination = DestinationPath;

        // Upstream creates the destination itself; the port used to demand that the
        // parent already exist, so cloning into a brand-new path failed needlessly.
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            _status.Text = TF("Could not create {0}: ", destination) + ex.Message;
            return;
        }

        bool central = _central.IsChecked == true;
        string? branch = BranchArgument();
        int? depth = _fullHistory.IsChecked == true ? null : 1;

        string arguments = CloneInitService.CloneArguments(
            url,
            destination,
            central,
            initSubmodules: _initSubmodules.IsChecked == true,
            branch,
            depth);

        SetBusy(true);
        _status.Text = T("Cloning…");
        Append($"$ git {arguments}");

        int exitCode = await Task.Run(() =>
        {
            // GitStreamRunner echoes a three-line command header of its own; the
            // command line is already in the log, so drop it.
            int skipHeader = 3;
            return GitStreamRunner.Run(parent, arguments, line =>
            {
                if (skipHeader > 0)
                {
                    skipHeader--;
                    return;
                }

                Dispatcher.UIThread.Post(() => Append(line));
            });
        });

        if (exitCode != 0)
        {
            SetBusy(false);
            _status.Text = TF("Clone failed (git exited with {0}) — see the output.", exitCode);
            return;
        }

        _status.Text = TF("Cloned into {0}", destination);

        // A bare clone has no working directory, so there is nothing for the host to
        // open; leave the window up with the result rather than closing into nothing.
        if (central)
        {
            SetBusy(false);
            Append(T("Central (bare) repository created — there is no working directory to open."));
            return;
        }

        ClonedRepoPath = destination;

        // Record the clone in the MRU right here, the way InitDialog does for a fresh
        // `git init`: a successful clone is a repository the user just created and it
        // must be reachable from the dashboard / "Open recent" afterwards, whichever
        // caller opened the dialog and whether or not that caller goes on to open it.
        try
        {
            await new RecentRepositoriesService().AddAsync(destination);
        }
        catch (Exception)
        {
            // A failed history write must never fail the clone.
        }

        Close();
    }

    // The branch drop-down's two synthetic entries are not branch names: "default"
    // means "whatever the remote's HEAD is" (no --branch at all) and "none" means
    // --no-checkout, which CloneArguments encodes as a null branch.
    private string? BranchArgument()
    {
        if (_branches.SelectedItem is not string item || item == DefaultBranchItem)
        {
            return string.Empty;
        }

        return item == NoBranchItem ? null : item;
    }

    private void Append(string line)
    {
        _output.Text = string.IsNullOrEmpty(_output.Text) ? line : _output.Text + Environment.NewLine + line;
        _output.CaretIndex = _output.Text.Length;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _clone.IsEnabled = !busy;
        _browse.IsEnabled = !busy;
        _url.IsEnabled = !busy;
        _parentDir.IsEnabled = !busy;
        _subdirectory.IsEnabled = !busy;
        _branches.IsEnabled = !busy;
        _initSubmodules.IsEnabled = !busy;
        _fullHistory.IsEnabled = !busy;
        _personal.IsEnabled = !busy;
        _central.IsEnabled = !busy;
    }

    // The directory name git would use for a clone: the last path segment of the URL
    // with any trailing ".git" (and trailing slashes) removed.
    private static string RepositoryNameFromUrl(string url)
    {
        string trimmed = url.Trim().TrimEnd('/', '\\');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        int slash = trimmed.LastIndexOfAny(['/', '\\', ':']);
        string segment = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;

        if (segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            segment = segment[..^4];
        }

        return segment;
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TF(string english, object arg) => TranslationService.TFormat(key: null, english, arg);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
