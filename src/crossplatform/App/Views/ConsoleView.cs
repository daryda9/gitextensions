using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The browse window's "Console" tab: a real embedded terminal.
///  <para>A <see cref="TerminalControl"/> hosts the user's login shell over a genuine
///  PTY (see <see cref="Services.PtyProcess"/>), started in the repository directory,
///  so interactive git, colours, job control and full-screen programs all work exactly
///  as in an external terminal. The old "Open terminal here" escape hatch is kept in the
///  header and still raises <see cref="OpenTerminalRequested"/>.</para>
/// </summary>
public sealed class ConsoleView : UserControl
{
    private readonly TerminalControl _terminal = new();
    private readonly TextBlock _status;
    private readonly Button _restart;
    private bool _started;
    private Window? _hostWindow;
    private string? _repoPath;

    /// <summary>Raised when the user asks to open an external terminal in the repo.</summary>
    public event Action? OpenTerminalRequested;

    public ConsoleView()
    {
        _terminal.DefaultForeground = Brush("App.Text", Brushes.Gainsboro);
        _terminal.DefaultBackground = Brush("App.Panel", new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)));
        _terminal.CursorBrush = Brush("App.Accent", Brushes.DarkOrange);
        _terminal.ShellExited += OnShellExited;

        _status = new TextBlock
        {
            Text = "starting shell…",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = Brush("App.TextDim", Brushes.Gray),
        };

        _restart = MakeButton("Restart shell", () => StartShell(force: true));
        Button external = MakeButton("Open terminal here", () => OpenTerminalRequested?.Invoke());

        StackPanel header = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(6, 4, 6, 4),
            Children =
            {
                new TextBlock
                {
                    Text = "Terminal",
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush("App.Text", Brushes.Gainsboro),
                },
                _status,
            },
        };

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _restart, external },
        };

        Grid bar = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Brush("App.Toolbar", Brushes.Transparent),
        };
        Grid.SetColumn(header, 0);
        Grid.SetColumn(actions, 1);
        bar.Children.Add(header);
        bar.Children.Add(actions);

        DockPanel root = new();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_terminal);

        Content = root;
        Background = Brush("App.Window", Brushes.DimGray);
        ClipToBounds = true;

        AttachedToVisualTree += OnAttached;
    }

    /// <summary>
    ///  Directory the shell works in. Defaults to the repository the app was opened
    ///  on.
    ///
    ///  <para>Setting it while a shell is running does what upstream's
    ///  <c>FormBrowse.ChangeTerminalActiveFolder</c> does
    ///  (<c>FormBrowse.cs:2777-2785</c>, called from <c>SetGitModule</c>): it types a
    ///  <c>cd</c> into the <i>live</i> shell instead of waiting for a restart, so
    ///  opening another repository (or a submodule) moves the terminal with the app.
    ///  With no shell running the value is simply remembered for the next start.</para>
    /// </summary>
    public string? RepoPath
    {
        get => _repoPath;
        set
        {
            string? path = string.IsNullOrWhiteSpace(value) ? null : value;
            if (string.Equals(path, _repoPath, StringComparison.Ordinal))
            {
                return;
            }

            _repoPath = path;

            // Not started yet (or the shell died): ResolveWorkingDirectory will pick
            // the new value up at the next StartShell.
            if (!_started || !_terminal.IsRunning || path is null || !Directory.Exists(path))
            {
                return;
            }

            ChangeWorkingDirectory(path);
        }
    }

    /// <summary>
    ///  Types a <c>cd</c> into the running shell. Upstream prefixes the command with
    ///  Ctrl+A / Ctrl+K (<c>MinttyShellRunner.cs:29-33</c>) so a half-typed line is
    ///  cleared first rather than being fused with the injected command; the same two
    ///  control characters are readline/zle standard on Linux.
    /// </summary>
    private void ChangeWorkingDirectory(string path)
    {
        try
        {
            _terminal.Send("\u0001\u000B" + $"cd {Quote(path)}\n");
            _status.Text = path;
            _status.Foreground = Brush("App.TextDim", Brushes.Gray);
        }
        catch (Exception)
        {
            // A dead PTY is not an error worth surfacing: the next Restart shell
            // starts in the new directory anyway.
        }
    }

    // POSIX single-quoting: everything is literal inside '…', and an embedded quote
    // is written by closing, escaping and reopening. Repository paths can contain
    // spaces, '$' and quotes.
    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_hostWindow is null && this.GetVisualRoot() is Window window)
        {
            _hostWindow = window;
            // A shell (and everything it spawned) must never outlive the window.
            window.Closing += (_, _) => _terminal.StopShell();
        }

        // The tab content attaches the first time the Console tab is shown; start the
        // shell then, not at construction, so the PTY only exists if the user asks.
        Dispatcher.UIThread.Post(() => StartShell(force: false), DispatcherPriority.Background);
    }

    private void StartShell(bool force)
    {
        if (_started && !force && _terminal.IsRunning)
        {
            return;
        }

        string cwd = ResolveWorkingDirectory();
        try
        {
            _terminal.StartShell(cwd);
            _started = true;
            _status.Text = cwd;
            _status.Foreground = Brush("App.TextDim", Brushes.Gray);
            Dispatcher.UIThread.Post(() => _terminal.Focus(), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _started = false;
            _status.Text = $"cannot open a pseudo-terminal: {ex.Message} — use “Open terminal here”";
            _status.Foreground = Brushes.IndianRed;
        }
    }

    private void OnShellExited()
    {
        _status.Text = "shell exited — press “Restart shell”";
        _status.Foreground = Brush("App.TextDim", Brushes.Gray);
    }

    private string ResolveWorkingDirectory()
    {
        foreach (string? candidate in new[] { _repoPath, App.InitialRepoPath, Directory.GetCurrentDirectory() })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return candidate!;
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private Button MakeButton(string text, Action onClick)
    {
        Button button = new()
        {
            Content = text,
            Padding = new Thickness(10, 3, 10, 3),
            Background = Brush("App.Control", Brushes.DimGray),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Focusable = false,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
