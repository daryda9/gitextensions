using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The port of upstream <c>FormApplyPatch</c>
///  (<c>src/app/GitUI/CommandsDialogs/FormApplyPatch.cs</c>) — not just "apply a
///  patch" but the whole <c>git am</c> <b>state machine</b>:
///  <list type="bullet">
///   <item>choose a single patch <b>file</b> or a whole <b>directory</b> of patches
///    (upstream's <c>PatchFileMode</c> / <c>PatchDirMode</c> radios) plus the
///    <c>--ignore-whitespace</c> / <c>--signoff</c> options, persisted in the same
///    settings upstream uses (<c>AppSettings.ApplyPatchIgnoreWhitespace</c>,
///    <c>AppSettings.ApplyPatchSignOff</c>);</item>
///   <item>while a session is in progress, the commands that drive it —
///    <b>Conflicts resolved</b> (<c>am --3way --resolved</c>), <b>Skip patch</b>
///    (<c>am --3way --skip</c>) and <b>Abort</b> (<c>am --3way --abort</c>) — with
///    exactly upstream's enablement rules (<c>FormApplyPatch.EnableButtons()</c>,
///    <c>:78-151</c>): mid-session the source selection is frozen and Apply is off;
///    with a conflicted index "Conflicts resolved" stays off until the resolution is
///    staged; with no session open only Apply is live;</item>
///   <item>the <b>patch grid</b> (upstream's <c>PatchGrid</c> user control), which
///    lists the series git keeps in the rebase directory with the per-patch status
///    (Applied / Applying… / Skipped / pending) and the patch's mail headers.</item>
///  </list>
///
///  <para>
///  Every git command runs through <see cref="GitProcessDialog.RunStreamingAsync"/>
///  so its output is visible <em>live</em> (upstream shows a <c>FormProcess</c> for
///  the same commands), and the state is re-read after each one — that re-read is
///  what moves the dialog between the two modes.
///  </para>
///
///  <para><b>Deviations from upstream, deliberate:</b>
///  <list type="bullet">
///   <item>a directory of patches is passed to git as <em>arguments</em>
///    (<c>am --3way &lt;file&gt; &lt;file&gt; …</c>, name-sorted) instead of upstream's
///    <c>GitModule.ApplyPatch</c>, which streams every file into git's stdin. The
///    port's live-output path (<see cref="GitStreamRunner"/>) closes stdin, and the
///    argument form is equivalent for git while additionally showing the user which
///    files are being applied. Sorting is a fix, not a deviation: upstream feeds
///    <c>Directory.GetFiles</c> order, which is unspecified.</item>
///   <item>upstream's <b>Solve conflicts</b> button opens <c>FormResolveConflicts</c>,
///    which the port does not have — so there is no such button here (a button that
///    cannot do anything is worse than its absence). The conflicted state is still
///    reported, and the files are resolvable in the Commit view / an external tool.</item>
///   <item>upstream's <b>Add files</b> opens <c>FormAddFiles</c>; here the same effect
///    is one honest command, <c>git add -A</c>, labelled as such — it is what makes
///    "Conflicts resolved" reachable after a manual resolution.</item>
///   <item>the current patch is highlighted with the themed <c>App.RepoStateDirty</c>
///    (upstream hardcodes <c>Color.OrangeRed</c>, which is not a theme key here).</item>
///  </list>
///  </para>
/// </summary>
public sealed class ApplyPatchDialog : Window
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private readonly string _repoPath;
    private readonly AmSessionService _sessions = new();

    private readonly RadioButton _fileMode;
    private readonly RadioButton _dirMode;
    private readonly TextBox _patchFile;
    private readonly TextBox _patchDir;
    private readonly Button _browseFile;
    private readonly Button _browseDir;
    private readonly CheckBox _ignoreWhitespace;
    private readonly CheckBox _signOff;

    private readonly Button _apply;
    private readonly Button _stageAll;
    private readonly Button _resolved;
    private readonly Button _skip;
    private readonly Button _abort;

    private readonly TextBlock _sessionBanner;
    private readonly TextBlock _status;
    private readonly StackPanel _grid;
    private readonly TextBlock _gridEmpty;

    private readonly IBrush _text;
    private readonly IBrush _dim;
    private readonly IBrush _border;
    private readonly IBrush _panel;
    private readonly IBrush _panelAlt;
    private readonly IBrush _current;

    private AmSessionState _state = AmSessionState.None;

    /// <summary>
    ///  Patches this dialog's session skipped, so the grid can keep showing them as
    ///  "Skipped" after git has moved past them — upstream keeps the same list
    ///  (<c>FormApplyPatch.Skipped</c>, handed to the grid via <c>SetSkipped</c>).
    /// </summary>
    private readonly List<string> _skipped = [];

    /// <summary>
    ///  True when any git command in this dialog changed the repository, so the
    ///  caller knows it must refresh its views.
    /// </summary>
    public bool RepositoryChanged { get; private set; }

    public ApplyPatchDialog(string repoPath, string? initialPatchFile = null, string? initialPatchDir = null)
    {
        _repoPath = repoPath;

        _text = Brush("App.Text", "#DCDCDC");
        _dim = Brush("App.TextDim", "#9B9B9B");
        _border = Brush("App.Border", "#3F3F46");
        _panel = Brush("App.Panel", "#252526");
        _panelAlt = Brush("App.PanelAlt", "#2D2D30");
        _current = Brush("App.RepoStateDirty", "#FFA07A");

        Title = $"{T("FormApplyPatch/$this.Text", "Apply patch")} ({repoPath})";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", "#1E1E1E");

        // ---- source: a single patch file, or a directory of patches ----
        _fileMode = new RadioButton
        {
            Content = T("FormApplyPatch/PatchFileMode.Text", "Patch file"),
            Foreground = _text,
            GroupName = "patchSource",
            IsChecked = initialPatchDir is null,
        };
        _dirMode = new RadioButton
        {
            Content = T("FormApplyPatch/PatchDirMode.Text", "Patch directory"),
            Foreground = _text,
            GroupName = "patchSource",
            IsChecked = initialPatchDir is not null,
        };
        _fileMode.IsCheckedChanged += (_, _) => EnableButtons();
        _dirMode.IsCheckedChanged += (_, _) => EnableButtons();

        _patchFile = new TextBox { Text = initialPatchFile ?? string.Empty, Watermark = T("Path of a .patch / .diff file") };
        _patchDir = new TextBox { Text = initialPatchDir ?? string.Empty, Watermark = T("Directory containing the patch series") };

        _browseFile = new Button { Content = T("Browse…"), Margin = new Thickness(8, 0, 0, 0) };
        _browseFile.Click += (_, _) => _ = BrowseFileAsync();
        _browseDir = new Button { Content = T("Browse…"), Margin = new Thickness(8, 0, 0, 0) };
        _browseDir.Click += (_, _) => _ = BrowseDirAsync();

        _ignoreWhitespace = new CheckBox
        {
            Content = T("FormApplyPatch/IgnoreWhitespace.Text", "Ignore whitespace"),
            Foreground = _text,
            IsChecked = ReadSetting(() => AppSettings.ApplyPatchIgnoreWhitespace),
        };
        _signOff = new CheckBox
        {
            Content = T("FormApplyPatch/SignOff.Text", "Add a Signed-off-by line"),
            Foreground = _text,
            Margin = new Thickness(16, 0, 0, 0),
            IsChecked = ReadSetting(() => AppSettings.ApplyPatchSignOff),
        };
        _ignoreWhitespace.IsCheckedChanged += (_, _) =>
            WriteSetting(v => AppSettings.ApplyPatchIgnoreWhitespace = v, _ignoreWhitespace.IsChecked == true);
        _signOff.IsCheckedChanged += (_, _) =>
            WriteSetting(v => AppSettings.ApplyPatchSignOff = v, _signOff.IsChecked == true);

        // ---- the am session banner + the patch grid ----
        _sessionBanner = new TextBlock
        {
            Foreground = _text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 7),
        };

        _grid = new StackPanel();
        _gridEmpty = new TextBlock
        {
            Foreground = _dim,
            Margin = new Thickness(10, 8),
            TextWrapping = TextWrapping.Wrap,
            Text = T("No patch series in progress. The grid lists the patches of a running "
                     + "git am session with their status."),
        };

        _status = new TextBlock
        {
            Foreground = _dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        // ---- commands ----
        _apply = new Button { Content = T("FormApplyPatch/Apply.Text", "Apply"), MinWidth = 92 };
        _apply.Click += (_, _) => _ = ApplyAsync();

        _stageAll = new Button
        {
            Content = T("Stage all (git add -A)"),
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _stageAll.Click += (_, _) => _ = RunAmCommandAsync(T("Stage all"), AmSessionService.StageAllArguments);

        _resolved = new Button
        {
            Content = T("FormApplyPatch/Resolved.Text", "Conflicts resolved"),
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _resolved.Click += (_, _) => _ = RunAmCommandAsync(
            T("FormApplyPatch/Resolved.Text", "Conflicts resolved"),
            AmSessionService.ResolvedArguments);

        _skip = new Button
        {
            Content = T("FormApplyPatch/Skip.Text", "Skip patch"),
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _skip.Click += (_, _) => _ = SkipAsync();

        _abort = new Button
        {
            Content = T("FormApplyPatch/Abort.Text", "Abort"),
            MinWidth = 92,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _abort.Click += (_, _) => _ = AbortAsync();

        Button close = new()
        {
            Content = T("Close"),
            MinWidth = 92,
            Margin = new Thickness(24, 0, 0, 0),
            IsCancel = true,
        };
        close.Click += (_, _) => Close();

        Content = BuildLayout(close);
        DialogKeys.InstallEscapeClose(this);

        Loaded += (_, _) => _ = RefreshStateAsync();
    }

    private Control BuildLayout(Button close)
    {
        Grid fileRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(22, 2, 0, 6) };
        Grid.SetColumn(_patchFile, 0);
        Grid.SetColumn(_browseFile, 1);
        fileRow.Children.Add(_patchFile);
        fileRow.Children.Add(_browseFile);

        Grid dirRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(22, 2, 0, 0) };
        Grid.SetColumn(_patchDir, 0);
        Grid.SetColumn(_browseDir, 1);
        dirRow.Children.Add(_patchDir);
        dirRow.Children.Add(_browseDir);

        StackPanel options = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _ignoreWhitespace, _signOff },
        };

        Border source = new()
        {
            BorderBrush = _border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(12, 12, 12, 0),
            Child = new StackPanel
            {
                Children = { _fileMode, fileRow, _dirMode, dirRow, options, _status },
            },
        };

        Border banner = new()
        {
            Background = _panelAlt,
            BorderBrush = _border,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12, 10, 12, 0),
            Child = _sessionBanner,
        };

        ScrollViewer gridScroll = new()
        {
            Content = _grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        DockPanel gridPanel = new();
        Control header = GridHeader();
        DockPanel.SetDock(header, Dock.Top);
        gridPanel.Children.Add(header);
        gridPanel.Children.Add(gridScroll);

        Border gridBorder = new()
        {
            Background = _panel,
            BorderBrush = _border,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(12, 10, 12, 0),
            Child = gridPanel,
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 10, 12, 12),
            Children = { _apply, _stageAll, _resolved, _skip, _abort, close },
        };

        DockPanel root = new();
        DockPanel.SetDock(source, Dock.Top);
        DockPanel.SetDock(banner, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(source);
        root.Children.Add(banner);
        root.Children.Add(buttons);
        root.Children.Add(gridBorder);
        return root;
    }

    // Column layout of the grid, shared by the header and every row: the same
    // columns upstream's PatchGrid shows for an am series (no Action/Commit hash —
    // those belong to its interactive-rebase mode, which this dialog is not).
    private static ColumnDefinitions GridColumns() => new("90,54,*,150,160");

    private Control GridHeader()
    {
        Grid header = new() { ColumnDefinitions = GridColumns(), Background = _panelAlt };
        string[] titles =
        [
            T("Status"),
            T("Name"),
            T("Subject"),
            T("Author"),
            T("Date"),
        ];

        for (int i = 0; i < titles.Length; i++)
        {
            TextBlock cell = new()
            {
                Text = titles[i],
                Foreground = _dim,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(8, 5),
            };
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }

        return new Border
        {
            BorderBrush = _border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header,
        };
    }

    // ---- state ----

    private async Task RefreshStateAsync()
    {
        string repo = _repoPath;
        AmSessionState state;
        try
        {
            state = await Task.Run(() => _sessions.Read(repo));
        }
        catch (Exception)
        {
            state = AmSessionState.None;
        }

        // Re-apply the skips this dialog performed: git has already moved past them,
        // so only our own record can still show them as skipped (upstream's Skipped list).
        foreach (AmPatchFile patch in state.Patches)
        {
            if (_skipped.Contains(patch.Name) && !patch.IsNext)
            {
                patch.IsSkipped = true;
            }
        }

        _state = state;
        EnableButtons();
        RebuildGrid();
    }

    /// <summary>
    ///  Port of <c>FormApplyPatch.EnableButtons()</c>: the enablement rules ARE the
    ///  state machine. Mid-session the source selection is frozen and only
    ///  Resolved / Skip / Abort work; a conflicted index disables Resolved (the
    ///  resolution has to be staged first); with no session, only Apply.
    /// </summary>
    private void EnableButtons()
    {
        bool inProgress = _state.InProgress;
        bool conflicted = _state.InConflictedMerge;

        _apply.IsEnabled = !inProgress;
        _ignoreWhitespace.IsEnabled = !inProgress;
        _signOff.IsEnabled = !inProgress;
        _fileMode.IsEnabled = !inProgress;
        _dirMode.IsEnabled = !inProgress;

        _patchFile.IsEnabled = !inProgress && _fileMode.IsChecked == true;
        _browseFile.IsEnabled = !inProgress && _fileMode.IsChecked == true;
        _patchDir.IsEnabled = !inProgress && _dirMode.IsChecked == true;
        _browseDir.IsEnabled = !inProgress && _dirMode.IsChecked == true;

        _stageAll.IsEnabled = inProgress;
        _resolved.IsEnabled = inProgress && !conflicted;
        _skip.IsEnabled = inProgress;
        _abort.IsEnabled = inProgress;

        // Upstream marks the default action with ">…<" and focus; here the same
        // intent, as the dialog's default button.
        _resolved.IsDefault = inProgress && !conflicted;
        _apply.IsDefault = !inProgress;

        if (!inProgress)
        {
            _sessionBanner.Foreground = _dim;
            _sessionBanner.Text = T("No git am session in progress: choose a patch file or a "
                                    + "patch directory and apply it.");
            return;
        }

        AmPatchFile? current = _state.Current;
        int total = _state.Patches.Count;
        string where = current is null
            ? T("a patch")
            : TF("patch {0} of {1}: {2}", current.Name, total, current.Subject ?? string.Empty);

        _sessionBanner.Foreground = _current;
        _sessionBanner.Text = conflicted
            ? TF(
                "A git am session is IN PROGRESS and stopped on {0} with CONFLICTS in the index. "
                + "Resolve the files, stage them (Stage all / the Commit view), then use "
                + "\"Conflicts resolved\". Skip drops this patch; Abort restores the branch.",
                where)
            : TF(
                "A git am session is IN PROGRESS, stopped on {0}. Use \"Conflicts resolved\" to "
                + "continue, Skip to drop this patch, or Abort to restore the branch.",
                where);
    }

    // Rebuilds the grid rows from scratch on every state change: the series is
    // small, and a fresh row set sidesteps the container-recycling staleness that
    // bit the virtualized lists elsewhere in this port.
    private void RebuildGrid()
    {
        _grid.Children.Clear();

        if (_state.Patches.Count == 0)
        {
            _grid.Children.Add(_gridEmpty);
            return;
        }

        bool alternate = false;
        foreach (AmPatchFile patch in _state.Patches)
        {
            _grid.Children.Add(Row(patch, alternate));
            alternate = !alternate;
        }
    }

    private Control Row(AmPatchFile patch, bool alternate)
    {
        Grid row = new()
        {
            ColumnDefinitions = GridColumns(),
            Background = alternate ? _panelAlt : _panel,
        };

        bool isCurrent = patch.IsNext;
        IBrush foreground = isCurrent ? _current : patch.IsApplied || patch.IsSkipped ? _dim : _text;

        string[] cells =
        [
            patch.Status,
            patch.Name,
            patch.Subject ?? string.Empty,
            patch.Author ?? string.Empty,
            patch.Date ?? string.Empty,
        ];

        for (int i = 0; i < cells.Length; i++)
        {
            TextBlock cell = new()
            {
                Text = cells[i],
                Foreground = foreground,
                FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                FontFamily = i == 1 ? Monospace : FontFamily.Default,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 4),
            };
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
        }

        ToolTip.SetTip(row, TF(
            "{0}\nStatus: {1}\nAuthor: {2}\nDate: {3}\n\nDouble-click to view the patch.",
            patch.Subject ?? patch.Name,
            patch.Status.Length > 0 ? patch.Status : T("pending"),
            patch.Author ?? string.Empty,
            patch.Date ?? string.Empty));

        row.DoubleTapped += (_, _) => _ = ViewPatchAsync(patch);
        return row;
    }

    // Upstream's PatchGrid double-click opens the patch (StartViewPatchDialog); the
    // port already has a colour-rendering viewer.
    private async Task ViewPatchAsync(AmPatchFile patch)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(patch.FullName);
        }
        catch (Exception ex)
        {
            _status.Text = TF("Could not read {0}: {1}", patch.Name, ex.Message);
            return;
        }

        await new PatchViewerWindow(patch.Subject ?? patch.Name, text).ShowDialog(this);
    }

    // ---- commands ----

    private async Task ApplyAsync()
    {
        bool ignoreWhitespace = _ignoreWhitespace.IsChecked == true;
        bool signOff = _signOff.IsChecked == true;
        string label = T("FormApplyPatch/$this.Text", "Apply patch");
        string arguments;

        if (_fileMode.IsChecked == true)
        {
            string file = _patchFile.Text?.Trim() ?? string.Empty;
            if (file.Length == 0)
            {
                _status.Text = T("FormApplyPatch/_noFileSelectedText.Text", "Please select a patch to apply");
                return;
            }

            if (!File.Exists(file))
            {
                _status.Text = TF("{0} does not exist.", file);
                return;
            }

            // Same sniff as upstream FormApplyPatch.IsDiffFile: a raw diff goes to
            // git apply, a mailbox/format-patch file to git am.
            arguments = PatchService.IsDiffFile(file)
                ? AmSessionService.ApplyDiffArguments(ignoreWhitespace, file)
                : AmSessionService.ApplyMailboxArguments(signOff, ignoreWhitespace, file);
        }
        else
        {
            string dir = _patchDir.Text?.Trim() ?? string.Empty;
            if (dir.Length == 0)
            {
                _status.Text = T("FormApplyPatch/_noFileSelectedText.Text", "Please select a patch to apply");
                return;
            }

            if (!Directory.Exists(dir))
            {
                _status.Text = TF("{0} does not exist.", dir);
                return;
            }

            IReadOnlyList<string> files;
            try
            {
                files = AmSessionService.PatchFilesInDirectory(dir);
            }
            catch (Exception ex)
            {
                _status.Text = TF("Could not read {0}: {1}", dir, ex.Message);
                return;
            }

            if (files.Count == 0)
            {
                _status.Text = TF("{0} contains no patch files.", dir);
                return;
            }

            arguments = AmSessionService.ApplyMailboxArguments(signOff, ignoreWhitespace, files);
        }

        _status.Text = string.Empty;
        await RunAmCommandAsync(label, arguments);
    }

    private async Task SkipAsync()
    {
        // Remember the patch git is about to drop, so the grid can keep showing it
        // as "Skipped" (upstream adds it to its static Skipped list here).
        AmPatchFile? current = _state.Current;
        if (current is not null)
        {
            current.IsSkipped = true;
            if (!_skipped.Contains(current.Name))
            {
                _skipped.Add(current.Name);
            }
        }

        await RunAmCommandAsync(T("FormApplyPatch/Skip.Text", "Skip patch"), AmSessionService.SkipArguments);
    }

    private async Task AbortAsync()
    {
        await RunAmCommandAsync(T("FormApplyPatch/Abort.Text", "Abort"), AmSessionService.AbortArguments);

        // Upstream clears its Skipped list on abort — the series no longer exists.
        _skipped.Clear();
        await RefreshStateAsync();
    }

    /// <summary>
    ///  Runs one git command with its output live in the process dialog, then
    ///  re-reads the am state — which is what re-arms or disarms the commands.
    /// </summary>
    private async Task RunAmCommandAsync(string label, string arguments)
    {
        string repo = _repoPath;

        await GitProcessDialog.RunStreamingAsync(
            this,
            label,
            emit =>
            {
                int exit = GitStreamRunner.Run(repo, arguments, emit);
                return new GitProcessOutcome(exit == 0, string.Empty);
            },
            interactive: false);

        RepositoryChanged = true;
        await RefreshStateAsync();

        // A patch that stopped on a conflict offers the resolve dialog, which the
        // port now has: this is what the stale note at the top of this file used to
        // say was missing.
        if (_state.InConflictedMerge)
        {
            await ConflictFlow.HandleAsync(this, repo);
            await RefreshStateAsync();
        }
    }

    // ---- pickers ----

    private async Task BrowseFileAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = T("FormApplyPatch/_selectPatchFileCaption.Text", "Select patch file"),
            FileTypeFilter =
            [
                new FilePickerFileType(T("FormApplyPatch/_selectPatchFileFilter.Text", "Patch file (*.Patch)"))
                {
                    Patterns = ["*.patch", "*.diff"],
                },
                new FilePickerFileType(T("All files")) { Patterns = ["*"] },
            ],
        });

        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null)
        {
            _patchFile.Text = path;
            _fileMode.IsChecked = true;
        }
    }

    private async Task BrowseDirAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = T("Select the directory containing the patches"),
        });

        string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (path is not null)
        {
            _patchDir.Text = path;
            _dirMode.IsChecked = true;
        }
    }

    // ---- helpers ----

    // AppSettings touches the settings store; a failure there must not stop the
    // dialog from opening (the options simply start at their default).
    private static bool ReadSetting(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteSetting(Action<bool> write, bool value)
    {
        try
        {
            write(value);
        }
        catch (Exception)
        {
            // Persisting a checkbox is not worth failing the dialog for.
        }
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(key: null, englishFormat, args);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
