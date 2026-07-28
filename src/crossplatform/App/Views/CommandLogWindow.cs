using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands.Logging;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal viewer for the core git command log. The Git Extensions core records
///  every executed process in the process-global <see cref="CommandLog"/>
///  (populated from <c>Executable</c> via <c>CommandLog.LogProcessStart</c>); this
///  window surfaces that log verbatim, one entry per line, oldest first / newest
///  last, using each entry's <see cref="CommandLogEntry.ColumnLine"/> projection
///  (timestamp, duration, PID, thread, exit code, command).
///
///  The content is a monospace, read-only text pane. "Refresh" re-reads the live
///  log (which the core keeps appending to as commands run) and scrolls to the
///  newest entry; "Close" dismisses the window. Styled from the shared App.*
///  brushes to match the active theme, mirroring <see cref="ReflogWindow"/>.
/// </summary>
public sealed class CommandLogWindow : Window
{
    private readonly TextBox _log;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status;

    // Live updates, as in OutputView: CommandLog.CommandsChanged fires from the
    // thread that ran the process and fires several times per command, so the
    // handler only arms a throttle timer. Upstream subscribes on Load and
    // unsubscribes on close (FormGitCommandLog.cs:38-58).
    private const int ThrottleMs = 300;
    private readonly DispatcherTimer _throttle;
    private bool _subscribed;

    public CommandLogWindow()
    {
        Title = "Git command log";
        Width = 900;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // TextBoxSurface (M62): the Fluent per-state repaint of the template child
        // beats the local Background, so clicking this read-only log flipped its
        // surface — measured #252526 → #000000 in the dark theme. The last read-only
        // monospace output left unpinned along with SparseDialog's.
        _log = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("monospace"),
            },
            Brush("App.Control", Brushes.Black),
            Brush("App.Text", Brushes.Gainsboro));
        _scroll = new ScrollViewer
        {
            Content = _log,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Button refresh = MakeButton("Refresh");
        Button close = MakeButton("Close");
        refresh.Click += (_, _) => Reload();
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { refresh, close },
        };

        _status = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(_scroll);
        Content = body;
        DialogKeys.InstallEscapeClose(this);

        _throttle = new DispatcherTimer(
            TimeSpan.FromMilliseconds(ThrottleMs),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _throttle!.Stop();
                Reload();
            });
        _throttle.Stop();

        Opened += (_, _) =>
        {
            if (!_subscribed)
            {
                CommandLog.CommandsChanged += OnCommandsChanged;
                _subscribed = true;
            }

            Reload();
        };

        Closed += (_, _) =>
        {
            if (_subscribed)
            {
                CommandLog.CommandsChanged -= OnCommandsChanged;
                _subscribed = false;
            }

            _throttle.Stop();
        };
    }

    private void OnCommandsChanged()
        => Dispatcher.UIThread.Post(
            () =>
            {
                if (_subscribed && !_throttle.IsEnabled)
                {
                    _throttle.Start();
                }
            },
            DispatcherPriority.Background);

    // Snapshots the live core command log and renders it newest-last. The queue
    // is enumerated oldest-first by the core, so no reordering is needed.
    private void Reload()
    {
        List<string> lines;
        try
        {
            lines = CommandLog.Commands.Select(c => c.ColumnLine).ToList();
        }
        catch (Exception ex)
        {
            _log.Text = "Could not read the command log: " + ex.Message;
            _status.Text = "Error reading command log.";
            return;
        }

        // A live refresh must not yank the view away from what the user is reading:
        // follow the tail only while the tail is what is on screen (the state right
        // after opening, and after every refresh that was already at the bottom).
        bool wasAtEnd = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 1;
        Vector offset = _scroll.Offset;
        int caret = _log.CaretIndex;

        _log.Text = lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : "(no git commands have been executed yet in this session)";
        _status.Text = $"{lines.Count} command(s) logged.";

        Dispatcher.UIThread.Post(
            () =>
            {
                if (wasAtEnd)
                {
                    // Scroll to the newest entry (bottom) after the layout settles.
                    _scroll.ScrollToEnd();
                }
                else
                {
                    _log.CaretIndex = Math.Min(caret, _log.Text?.Length ?? 0);
                    _scroll.Offset = offset;
                }
            },
            DispatcherPriority.Background);
    }

    private Button MakeButton(string text) => new()
    {
        Content = text,
        MinWidth = 90,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = Brush("App.Control", Brushes.DimGray),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
