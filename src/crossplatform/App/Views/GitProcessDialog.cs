using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands.Logging;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Outcome of a git operation surfaced through <see cref="GitProcessDialog"/>:
///  whether it succeeded and the full textual output git produced.
/// </summary>
public sealed record GitProcessOutcome(bool Success, string Output);

/// <summary>
///  Shared modal runner for a single git operation, modelled on the original
///  GitExtensions <c>FormProcess</c>. It shows the operation label, streams the
///  git command lines as they are executed (read from the process-global
///  <see cref="CommandLog"/>), then appends the operation's captured output and a
///  success/error result.
///
///  Visually it deliberately mirrors the original Windows "Process" dialog: a
///  fixed beige/tan console with near-black monospace text (NOT theme-driven),
///  a <c>Command to be executed:</c> section, and a footer with a
///  <c>Keep dialog open</c> checkbox plus <c>OK</c> / <c>Abort</c> buttons. Only
///  the surrounding chrome resolves from the shared App.* brushes.
///
///  When the op succeeds it auto-closes unless <c>Keep dialog open</c> is checked;
///  on failure it always stays open.
///
///  Usage: <c>await GitProcessDialog.RunAsync(owner, "Push", () =&gt; …)</c>. The
///  supplied <paramref name="operation"/> runs on a background thread; all UI
///  mutation happens on the UI thread.
/// </summary>
public sealed class GitProcessDialog : Window
{
    // Fixed console look, matching the original Windows dialog (intentionally
    // not theme-driven): warm beige background, near-black text.
    private static readonly IBrush ConsoleBackground = new SolidColorBrush(Color.Parse("#ECE9D8"));
    private static readonly IBrush ConsoleForeground = new SolidColorBrush(Color.Parse("#101010"));

    private readonly string _label;
    private readonly TextBox _output;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status;
    private readonly TextBlock _check;
    private readonly TextBlock _header;
    private readonly CheckBox _keepOpen;
    private readonly Button _ok;
    private readonly Button _abort;

    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _closeTimer;
    private int _consumed;

    public GitProcessDialog(string label)
    {
        _label = label ?? string.Empty;
        Title = $"Process — {_label}";
        Width = 760;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        _check = new TextBlock
        {
            Text = "✔",
            Foreground = Brushes.LimeGreen,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = false,
        };

        _header = new TextBlock
        {
            Text = $"Process — {_label}",
            FontWeight = FontWeight.Bold,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel headerRow = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
            Children = { _check, _header },
        };

        _output = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            Background = ConsoleBackground,
            Foreground = ConsoleForeground,
            CaretBrush = ConsoleForeground,
            BorderThickness = new Thickness(0),
            Text = "Command to be executed:",
        };
        _scroll = new ScrollViewer
        {
            Content = _output,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = ConsoleBackground,
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(1),
        };

        _status = new TextBlock
        {
            Text = "Running…",
            Foreground = Brush("App.TextDim", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _keepOpen = new CheckBox
        {
            Content = "Keep dialog open",
            IsChecked = true,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _ok = MakeButton("OK");
        _ok.Click += (_, _) => Close();

        _abort = MakeButton("Abort");
        // Ops are short/synchronous; best-effort cancel is simply to close.
        _abort.Click += (_, _) => Close();

        StackPanel footRight = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Children = { _keepOpen, _ok, _abort },
        };

        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(_status, 0);
        Grid.SetColumn(footRight, 1);
        footer.Children.Add(_status);
        footer.Children.Add(footRight);

        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(headerRow, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(headerRow);
        body.Children.Add(footer);
        body.Children.Add(_scroll);
        Content = body;
    }

    /// <summary>
    ///  Runs <paramref name="operation"/> on a background thread inside a modal
    ///  process dialog owned by <paramref name="owner"/>, streaming the executed
    ///  git command lines and then the operation's captured output. Completes when
    ///  the dialog closes (auto-close on success unless <c>Keep dialog open</c> is
    ///  checked, or the user pressing OK/Abort).
    /// </summary>
    public static Task RunAsync(Window owner, string label, Func<GitProcessOutcome> operation)
    {
        GitProcessDialog dialog = new(label);
        return dialog.RunInternalAsync(owner, operation);
    }

    /// <summary>
    ///  Runs <paramref name="operation"/> on a background thread, streaming git
    ///  output <em>truly live</em>: the operation is handed an <c>emit</c> callback
    ///  and every line it emits is appended to the console the instant git produces
    ///  it (stdout AND stderr, including fetch/push transfer progress). Unlike
    ///  <see cref="RunAsync"/>, no CommandLog poll timer runs — the operation (via
    ///  <see cref="Services.GitStreamRunner"/>) emits the command header itself.
    /// </summary>
    public static Task RunStreamingAsync(Window owner, string label, Func<Action<string>, GitProcessOutcome> operation)
    {
        GitProcessDialog dialog = new(label);
        return dialog.RunStreamingInternalAsync(owner, operation);
    }

    private Task RunStreamingInternalAsync(Window owner, Func<Action<string>, GitProcessOutcome> operation)
    {
        Opened += (_, _) =>
        {
            _ = Task.Run(() =>
            {
                GitProcessOutcome outcome;
                try
                {
                    // Marshal every emitted line to the UI thread; the runner calls
                    // this from threadpool threads (OutputDataReceived/ErrorDataReceived).
                    outcome = operation(line => Dispatcher.UIThread.Post(() => AppendLine(line)));
                }
                catch (Exception ex)
                {
                    outcome = new GitProcessOutcome(false, ex.GetBaseException().Message ?? "Operation failed.");
                }

                Dispatcher.UIThread.Post(() => Complete(outcome, streaming: true));
            });
        };

        return ShowDialog(owner);
    }

    // Appends a single already-produced line to the beige console and scrolls to
    // the end. Used by the streaming path (called on the UI thread).
    private void AppendLine(string line) => Append(line ?? string.Empty);

    private Task RunInternalAsync(Window owner, Func<GitProcessOutcome> operation)
    {
        // Snapshot the log length so we only stream entries produced by this op.
        _consumed = SafeCommandCount();

        Opened += (_, _) =>
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _pollTimer.Tick += (_, _) => DrainNewCommands();
            _pollTimer.Start();

            _ = Task.Run(operation).ContinueWith(t =>
            {
                GitProcessOutcome outcome = t.IsFaulted
                    ? new GitProcessOutcome(false, t.Exception?.GetBaseException().Message ?? "Operation failed.")
                    : t.Result;
                Dispatcher.UIThread.Post(() => Complete(outcome));
            }, TaskScheduler.Default);
        };

        return ShowDialog(owner);
    }

    // Appends the clean command line of any command-log entries that appeared
    // since the last poll, so the user sees the actual git commands as they run,
    // beneath the "Command to be executed:" header.
    private void DrainNewCommands()
    {
        List<string> lines;
        try
        {
            lines = CommandLog.Commands.Skip(_consumed).Select(c => c.CommandLine).ToList();
        }
        catch (Exception)
        {
            return;
        }

        if (lines.Count == 0)
        {
            return;
        }

        _consumed += lines.Count;
        Append(string.Join(Environment.NewLine, lines));
    }

    private void Complete(GitProcessOutcome outcome) => Complete(outcome, streaming: false);

    private void Complete(GitProcessOutcome outcome, bool streaming)
    {
        _pollTimer?.Stop();
        _pollTimer = null;

        if (!streaming)
        {
            // Non-streaming: flush any final command entries, then the captured
            // operation output. (Streaming already emitted every line live.)
            DrainNewCommands();

            if (!string.IsNullOrEmpty(outcome.Output))
            {
                Append(outcome.Output);
            }
        }

        // Cosmetic closing hint, echoing the original console.
        Append(string.Empty);
        Append("Press Enter or Esc to exit…");

        if (outcome.Success)
        {
            _header.Text = $"Process — {_label} (Done)";
            _check.IsVisible = true;
            _status.Text = "Success";
            _status.Foreground = Brushes.LimeGreen;

            // Keep-open semantics: auto-close only when the box is UNCHECKED.
            if (_keepOpen.IsChecked != true)
            {
                _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _closeTimer.Tick += (_, _) =>
                {
                    _closeTimer?.Stop();
                    _closeTimer = null;
                    Close();
                };
                _closeTimer.Start();
            }
        }
        else
        {
            _header.Text = $"Process — {_label} (Failed)";
            _status.Text = "Failed";
            _status.Foreground = Brushes.OrangeRed;
            // On failure always stay open regardless of the checkbox.
        }
    }

    private void Append(string text)
    {
        _output.Text = string.IsNullOrEmpty(_output.Text)
            ? text
            : _output.Text + Environment.NewLine + text;
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private static int SafeCommandCount()
    {
        try
        {
            return CommandLog.Commands.Count();
        }
        catch (Exception)
        {
            return 0;
        }
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
