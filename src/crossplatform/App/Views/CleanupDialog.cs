using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  "Clean working directory" — the port of upstream's
///  <c>FormCleanupRepository</c> (<c>FormCleanupRepository.Designer.cs:110-155,
///  192-259,271-279</c>), replacing the inline yes/no confirm the main window used
///  to show. That confirm hard-wired <c>git clean -f -d</c> (optionally <c>-x</c>),
///  which made the <em>ignored-only</em> mode — <c>git clean -X</c>, the one you
///  want to wipe build output while keeping new source files — unreachable.
///
///  <para>The dialog exposes the whole upstream option set:</para>
///  <list type="bullet">
///   <item>the three exclusive modes — Remove all (<c>-x</c>) / Remove non-ignored
///    (git's default) / Remove ignored only (<c>-X</c>);</item>
///   <item>"Remove directories" (<c>-d</c>), on by default;</item>
///   <item>"Clean submodules", repeating the clean via
///    <c>submodule foreach --recursive</c>;</item>
///   <item>a multi-line <b>include</b> pathspec filter and a multi-line
///    <b>exclude</b> filter (one <c>--exclude=</c> per line);</item>
///   <item>a repeatable <b>Preview</b> (a real <c>--dry-run</c>) and a persistent
///    log panel that keeps every run's output.</item>
///  </list>
///
///  <para>
///  Clean is destructive and irreversible, so it is never issued blind: pressing
///  <b>Clean</b> ALWAYS runs the dry-run first, prints it into the log, and asks for
///  confirmation naming the number of entries — only then does the real
///  <c>-f</c> clean run. Both runs stream through <see cref="GitStreamRunner"/> so
///  the "Removing &lt;path&gt;" lines appear as git emits them.
///  </para>
///
///  <para>
///  Every git call happens in <c>Task.Run</c>; the log is appended from the UI
///  thread via <see cref="Dispatcher"/>. Escape closes the window (M57 convention).
///  <see cref="Cleaned"/> tells the host whether a real clean succeeded, i.e.
///  whether the working directory needs a refresh.
///  </para>
/// </summary>
public sealed class CleanupDialog : Window
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private readonly string _repoPath;

    private readonly RadioButton _removeAll;
    private readonly RadioButton _removeNonIgnored;
    private readonly RadioButton _removeIgnored;
    private readonly CheckBox _removeDirectories;
    private readonly CheckBox _cleanSubmodules;
    private readonly CheckBox _useIncludeFilter;
    private readonly TextBox _includePaths;
    private readonly CheckBox _useExcludeFilter;
    private readonly TextBox _excludePaths;

    private readonly TextBox _log;
    private readonly Border _logFrame;
    private readonly TextBlock _status;

    private readonly Button _preview;
    private readonly Button _clean;
    private readonly Button _close;

    private readonly Border _confirmBar;
    private readonly TextBlock _confirmText;
    private TaskCompletionSource<bool>? _confirm;

    private bool _busy;

    /// <summary>
    ///  <see langword="true"/> once a real (non dry-run) clean has completed
    ///  successfully in this dialog — the host should refresh the working directory.
    ///  Dry runs never set it.
    /// </summary>
    public bool Cleaned { get; private set; }

    public CleanupDialog(string repoPath)
    {
        _repoPath = repoPath;

        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#9B9B9B");
        IBrush border = Brush("App.Border", "#3F3F46");

        Title = T("FormCleanupRepository/$this.Text", "Clean working directory");
        Width = 660;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", "#1E1E1E");

        _removeAll = Radio(
            T("FormCleanupRepository/RemoveAll.Text", "Remove all untracked files"),
            "git clean -x",
            text,
            dim);
        _removeNonIgnored = Radio(
            T("FormCleanupRepository/RemoveNonIgnored.Text", "Remove non-ignored files only"),
            "git clean",
            text,
            dim);
        _removeIgnored = Radio(
            T("FormCleanupRepository/RemoveIgnored.Text", "Remove ignored files only"),
            "git clean -X",
            text,
            dim);
        _removeNonIgnored.IsChecked = true;

        _removeDirectories = new CheckBox
        {
            Content = T("FormCleanupRepository/RemoveDirectories.Text", "Remove directories"),
            IsChecked = true,
            Foreground = text,
        };
        _cleanSubmodules = new CheckBox
        {
            Content = T("FormCleanupRepository/CleanSubmodules.Text", "Clean submodules"),
            Foreground = text,
        };

        _useIncludeFilter = new CheckBox
        {
            Content = T("FormCleanupRepository/checkBoxIncludePathFilter.Text", "Only clean these paths"),
            Foreground = text,
        };
        _includePaths = FilterBox(border);
        _useExcludeFilter = new CheckBox
        {
            Content = T("FormCleanupRepository/checkBoxExcludePathFilter.Text", "Do not clean these paths"),
            Foreground = text,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _excludePaths = FilterBox(border);

        _useIncludeFilter.IsCheckedChanged += (_, _) => SyncFilterBoxes();
        _useExcludeFilter.IsCheckedChanged += (_, _) => SyncFilterBoxes();
        SyncFilterBoxes();

        // TextBoxSurface, not plain Background/Foreground: the Fluent theme repaints
        // the template's border element per state, and a style setter beats a local
        // value — so on the light theme focusing this console turned it white while
        // the console foreground stayed light grey, i.e. unreadable.
        _log = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = Monospace,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                MinHeight = 140,
            },
            Brush("App.ConsoleBackground", "#2D2D30"),
            Brush("App.ConsoleForeground", "#DCDCDC"),
            border: Brush("App.ConsoleBackground", "#2D2D30"),
            placeholderForeground: Brush("App.ConsoleForeground", "#DCDCDC"));

        // The TextBox scrolls itself; wrapping it in a ScrollViewer would give the
        // log two nested scrollers, and scrolling the outer one to the end left the
        // caller staring at blank space below the text.
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        _logFrame = new Border
        {
            Child = _log,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
        };

        _status = new TextBlock
        {
            Text = T("Nothing has been run yet — start with Preview."),
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _preview = new Button { Content = T("FormCleanupRepository/Preview.Text", "Preview"), MinWidth = 90 };
        _preview.Click += (_, _) => _ = PreviewAsync();
        _clean = new Button
        {
            Content = T("FormCleanupRepository/Cleanup.Text", "Clean"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _clean.Click += (_, _) => _ = CleanAsync();
        _close = new Button
        {
            Content = T("Close"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        _close.Click += (_, _) => Close();

        _confirmText = new TextBlock
        {
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Button confirmYes = new() { Content = T("Delete"), MinWidth = 80 };
        confirmYes.Click += (_, _) => ResolveConfirm(true);
        Button confirmNo = new() { Content = T("Cancel"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        confirmNo.Click += (_, _) => ResolveConfirm(false);

        _confirmBar = new Border
        {
            IsVisible = false,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Background = Brush("App.PanelBackground", "#2A2A2E"),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    _confirmText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { confirmYes, confirmNo },
                        [Grid.ColumnProperty] = 1,
                    },
                },
            },
        };

        Grid root = new()
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
        };

        StackPanel options = new();
        options.Children.Add(new TextBlock
        {
            Text = T("FormCleanupRepository/groupBoxRepositoryType.Text", "What to remove"),
            Foreground = dim,
            Margin = new Thickness(0, 0, 0, 6),
        });
        options.Children.Add(_removeAll);
        options.Children.Add(_removeNonIgnored);
        options.Children.Add(_removeIgnored);
        options.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _removeDirectories, _cleanSubmodules },
        });
        Grid.SetRow(options, 0);
        root.Children.Add(options);

        StackPanel filters = new() { Margin = new Thickness(0, 14, 0, 0) };
        filters.Children.Add(_useIncludeFilter);
        filters.Children.Add(_includePaths);
        filters.Children.Add(_useExcludeFilter);
        filters.Children.Add(_excludePaths);
        filters.Children.Add(new TextBlock
        {
            Text = T("One path per line, relative to the repository root."),
            Foreground = dim,
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetRow(filters, 1);
        root.Children.Add(filters);

        DockPanel logDock = new();
        TextBlock logLabel = new()
        {
            Text = T("FormCleanupRepository/labelPreview.Text", "Output"),
            Foreground = dim,
            Margin = new Thickness(0, 0, 0, 4),
        };
        DockPanel.SetDock(logLabel, Dock.Top);
        logDock.Children.Add(logLabel);
        logDock.Children.Add(_logFrame);
        logDock.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(logDock, 2);
        root.Children.Add(logDock);

        Grid.SetRow(_confirmBar, 3);
        root.Children.Add(_confirmBar);

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
            Children = { _preview, _clean, _close },
        };
        Grid.SetColumn(buttons, 1);
        buttonRow.Children.Add(buttons);
        Grid.SetRow(buttonRow, 4);
        root.Children.Add(buttonRow);

        Content = root;
        DialogKeys.EnsureFocusRoute(this);

        // Escape = Close (upstream's CancelButton). While a confirmation is pending
        // Escape answers "no" instead of leaving the dialog behind a live prompt.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Bubble);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (_confirm is not null)
        {
            ResolveConfirm(false);
            e.Handled = true;
            return;
        }

        if (!_busy)
        {
            e.Handled = true;
            Close();
        }
    }

    // The option set as the service understands it. Reads controls, so UI thread only.
    private CleanOptions CurrentOptions()
    {
        CleanMode mode = _removeAll.IsChecked == true
            ? CleanMode.All
            : _removeIgnored.IsChecked == true
                ? CleanMode.OnlyIgnored
                : CleanMode.OnlyNonIgnored;

        return new CleanOptions(
            mode,
            Directories: _removeDirectories.IsChecked == true,
            CleanSubmodules: _cleanSubmodules.IsChecked == true,
            IncludePaths: _useIncludeFilter.IsChecked == true ? _includePaths.Text : null,
            ExcludePaths: _useExcludeFilter.IsChecked == true ? _excludePaths.Text : null);
    }

    private async Task PreviewAsync()
    {
        CleanOptions options = CurrentOptions();
        SetBusy(true);
        _status.Text = T("Previewing…");

        int wouldRemove = await RunAsync(options, dryRun: true);

        _status.Text = wouldRemove < 0
            ? T("Preview failed — see the output.")
            : TF("Preview: {0} entries would be removed.", wouldRemove);
        SetBusy(false);
    }

    private async Task CleanAsync()
    {
        CleanOptions options = CurrentOptions();
        SetBusy(true);

        // A clean cannot be undone, so the dry-run is not optional: run it, show it,
        // and only ask once the user can see exactly what is about to disappear.
        _status.Text = T("Previewing…");
        int wouldRemove = await RunAsync(options, dryRun: true);

        if (wouldRemove < 0)
        {
            _status.Text = T("Preview failed — nothing was deleted.");
            SetBusy(false);
            return;
        }

        if (wouldRemove == 0)
        {
            _status.Text = T("Nothing to clean.");
            SetBusy(false);
            return;
        }

        _confirmText.Text = TF(
            "{0} entries listed above will be deleted permanently. Continue?",
            wouldRemove);
        _confirmBar.IsVisible = true;
        _confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool go = await _confirm.Task;
        _confirm = null;
        _confirmBar.IsVisible = false;

        if (!go)
        {
            _status.Text = T("Cancelled — nothing was deleted.");
            SetBusy(false);
            return;
        }

        _status.Text = T("Cleaning…");
        int removed = await RunAsync(options, dryRun: false);
        if (removed < 0)
        {
            _status.Text = T("Clean failed — see the output.");
        }
        else
        {
            Cleaned = true;
            _status.Text = TF("Removed {0} entries.", removed);
        }

        SetBusy(false);
    }

    /// <summary>
    ///  Streams one clean (and, when asked for, the submodule pass) into the log.
    ///  Returns the number of entries git reported, or -1 when a command failed.
    /// </summary>
    private async Task<int> RunAsync(CleanOptions options, bool dryRun)
    {
        string mainArgs = WorkingDirectoryService.CleanArguments(options, dryRun);
        string? submoduleArgs = options.CleanSubmodules
            ? WorkingDirectoryService.CleanSubmodulesArguments(options, dryRun)
            : null;

        Append(string.Empty);
        Append($"$ git {mainArgs}");

        string repo = _repoPath;
        int entries = 0;
        bool failed = false;

        await Task.Run(() =>
        {
            void Emit(string line)
            {
                if (CountsAsEntry(line))
                {
                    Interlocked.Increment(ref entries);
                }

                Dispatcher.UIThread.Post(() => Append(line));
            }

            // GitStreamRunner echoes its own "Command to be executed:" header; the
            // dialog already printed the command line, so swallow that preamble and
            // keep the log to what git actually said.
            int skipHeader = 3;
            void EmitBody(string line)
            {
                if (skipHeader > 0)
                {
                    skipHeader--;
                    return;
                }

                Emit(line);
            }

            if (GitStreamRunner.Run(repo, mainArgs, EmitBody) != 0)
            {
                failed = true;
                return;
            }

            if (submoduleArgs is not null)
            {
                Dispatcher.UIThread.Post(() => Append($"$ git {submoduleArgs}"));
                skipHeader = 3;
                if (GitStreamRunner.Run(repo, submoduleArgs, EmitBody) != 0)
                {
                    failed = true;
                }
            }
        });

        return failed ? -1 : entries;
    }

    // git clean prints exactly one line per entry ("Would remove <path>" for a dry
    // run, "Removing <path>" for the real thing) and nothing else. Those prefixes are
    // TRANSLATED by git — an Italian git says "Eliminerei <path>" — so matching them
    // would report zero on most non-English machines. Count lines instead, skipping
    // only the diagnostics git tags with an untranslated prefix.
    private static bool CountsAsEntry(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 0
            && !trimmed.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("Entering '", StringComparison.Ordinal);
    }

    private void Append(string line)
    {
        _log.Text = string.IsNullOrEmpty(_log.Text) ? line : _log.Text + Environment.NewLine + line;

        // Keep the newest line in view: moving the caret is what actually scrolls a
        // TextBox, and the log is read-only so the caret has no other job.
        _log.CaretIndex = _log.Text.Length;
    }

    private void SyncFilterBoxes()
    {
        _includePaths.IsEnabled = _useIncludeFilter.IsChecked == true;
        _excludePaths.IsEnabled = _useExcludeFilter.IsChecked == true;
    }

    private void ResolveConfirm(bool answer) => _confirm?.TrySetResult(answer);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _preview.IsEnabled = !busy;
        _clean.IsEnabled = !busy;
    }

    private static TextBox FilterBox(IBrush border) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = Monospace,
        FontSize = 12,
        Height = 56,
        Margin = new Thickness(0, 4, 0, 0),
        BorderBrush = border,
    };

    private static RadioButton Radio(string caption, string command, IBrush text, IBrush dim)
        => new()
        {
            GroupName = "CleanupMode",
            Foreground = text,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = caption, Foreground = text },
                    new TextBlock { Text = command, Foreground = dim, FontFamily = Monospace, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TF(string english, object arg) => TranslationService.TFormat(key: null, english, arg);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
