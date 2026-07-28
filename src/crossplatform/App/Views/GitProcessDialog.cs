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
///  whether it succeeded, the full textual output git produced, and whether the
///  user aborted it (<see cref="Aborted"/> — the git process was killed, so the
///  operation is neither a success nor a git failure).
/// </summary>
public sealed record GitProcessOutcome(bool Success, string Output, bool Aborted = false);

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
///  <c>OK</c> is enabled only once the operation finished. <c>Abort</c> is shown
///  only for the streaming path — the one where a real <see cref="System.Diagnostics.Process"/>
///  exists to kill — and it kills the git process tree, clears the <c>index.lock</c>
///  it may have left, and reports <see cref="GitProcessOutcome.Aborted"/>.
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

    // Set for the streaming path: the scope holding the live git process, so Abort
    // can really kill it. null on the non-streaming path (the core Executable gives
    // us no handle) — there Abort is hidden rather than pretending to work.
    private Services.GitProcessScope? _scope;
    private GitProcessOutcome? _outcome;
    private bool _aborted;
    private bool _finished;

    // The streaming operation currently bound to this dialog. Kept in a field (not
    // just captured in the Opened handler) so <see cref="Retry"/> can run it again —
    // or run a REPLACEMENT operation — inside the same window.
    private Func<Action<string>, GitProcessOutcome>? _operation;

    // Optional "the operation just finished" hook. It may inspect the outcome, ask
    // the user something (owning its modal on this dialog) and call Retry(); when it
    // returns true the dialog does NOT settle, because another run is under way.
    private Func<GitProcessDialog, GitProcessOutcome, Task<bool>>? _onExit;

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

        // OK stays disabled until the operation really finished: closing mid-run
        // must not look like an acknowledged success (mirrors FormStatus, where Ok
        // is only enabled by Done()).
        _ok = MakeButton("OK");
        _ok.IsEnabled = false;
        _ok.Click += (_, _) => Close();

        // Abort is only shown when there is something we can genuinely kill (the
        // streaming path); RunStreamingInternalAsync makes it visible.
        _abort = MakeButton("Abort");
        _abort.IsVisible = false;
        _abort.Click += (_, _) => AbortOperation();

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

        // Escape must not abandon a git process that is still running: it only
        // closes the dialog once the process has finished.
        DialogKeys.InstallEscapeClose(this, () => _finished);
    }

    /// <summary>
    ///  Runs <paramref name="operation"/> on a background thread inside a modal
    ///  process dialog owned by <paramref name="owner"/>, streaming the executed
    ///  git command lines and then the operation's captured output. Completes when
    ///  the dialog closes (auto-close on success unless <c>Keep dialog open</c> is
    ///  checked, or the user pressing OK/Abort).
    /// </summary>
    public static Task<GitProcessOutcome> RunAsync(Window owner, string label, Func<GitProcessOutcome> operation)
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
    /// <param name="onExit">
    ///  Optional hook invoked on the UI thread each time the operation finishes
    ///  (never after an Abort). It receives this dialog — use it as the owner of any
    ///  question it asks — and the outcome. Returning <see langword="true"/> means it
    ///  took over (typically by calling <see cref="Retry"/>), so the dialog keeps
    ///  running instead of reporting the result and closing.
    /// </param>
    public static Task<GitProcessOutcome> RunStreamingAsync(
        Window owner,
        string label,
        Func<Action<string>, GitProcessOutcome> operation,
        bool closeOnAuthFailure = false,
        Func<GitProcessDialog, GitProcessOutcome, Task<bool>>? onExit = null)
    {
        GitProcessDialog dialog = new(label) { _closeOnAuthFailure = closeOnAuthFailure, _onExit = onExit };
        return dialog.RunStreamingInternalAsync(owner, operation);
    }

    /// <summary>
    ///  Runs the operation again <em>inside this same window</em>, the way
    ///  <c>FormStatus.Retry()</c> does: the console keeps the previous attempt's
    ///  output, the OK button goes back to disabled and Abort becomes live again for
    ///  the new git process. This is what lets a recoverable failure (a rejected
    ///  push) be fixed and re-attempted without the user starting from scratch.
    /// </summary>
    /// <param name="operation">
    ///  Replacement operation for this attempt onwards — e.g. "pull, then push
    ///  again", or the same push with a force flag. <see langword="null"/> repeats
    ///  the operation the dialog already had.
    /// </param>
    /// <param name="note">Line written to the console to introduce the new attempt.</param>
    public void Retry(Func<Action<string>, GitProcessOutcome>? operation = null, string? note = null)
    {
        if (operation is not null)
        {
            _operation = operation;
        }

        if (_operation is null)
        {
            return;
        }

        Append(string.Empty);
        Append(note ?? "Retrying…");
        Append(string.Empty);
        StartStreamingRun();
    }

    // When set, a failure whose output looks like an authentication failure
    // auto-closes the dialog (like success does) so the caller can immediately
    // hand off to the in-app credentials prompt instead of the user having to
    // dismiss a "Failed" dialog first.
    private bool _closeOnAuthFailure;

    // Authentication-failure markers (mirrors RemoteService.LooksLikeAuthFailure)
    // so the dialog can decide whether to auto-close and hand off to credentials.
    private static bool LooksLikeAuthFailure(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        string[] markers =
        [
            "Authentication failed", "could not read Username", "could not read Password",
            "Invalid username or password", "remote: Unauthorized", "fatal: Authentication",
            "terminal prompts disabled",
        ];
        foreach (string marker in markers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<GitProcessOutcome> RunStreamingInternalAsync(Window owner, Func<Action<string>, GitProcessOutcome> operation)
    {
        // Streaming ops go through GitStreamRunner, which owns a real Process: Abort
        // can kill it, so the button is offered here (and only here).
        _operation = operation;
        _abort.IsVisible = true;

        Opened += (_, _) => StartStreamingRun();

        await ShowDialog(owner);
        return FinalOutcome();
    }

    /// <summary>
    ///  Starts (or restarts) the bound streaming operation on a background thread.
    ///  Each run gets a FRESH <see cref="Services.GitProcessScope"/>: a scope that
    ///  has already been aborted kills every process registered afterwards, so
    ///  reusing one would make the retry die on arrival.
    /// </summary>
    private void StartStreamingRun()
    {
        Func<Action<string>, GitProcessOutcome>? operation = _operation;
        if (operation is null)
        {
            return;
        }

        Services.GitProcessScope scope = new();
        _scope = scope;

        // Reset the per-run state so a retry is a genuinely fresh attempt: OK locked
        // again until it finishes, Abort live, and no stale success/failure chrome.
        _outcome = null;
        _aborted = false;
        _finished = false;
        _ok.IsEnabled = false;
        _abort.IsEnabled = true;
        _check.IsVisible = false;
        _header.Text = $"Process — {_label}";
        _status.Text = "Running…";
        _status.Foreground = Brush("App.TextDim", Brushes.Gray);

        _ = Task.Run(() =>
        {
            // Bind the scope to this logical flow so every git process the
            // operation starts registers itself and becomes killable.
            Services.GitStreamRunner.EnterScope(scope);

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
    }

    // The outcome handed back to the caller. When the dialog was closed before the
    // operation reported anything, that is NOT a success — say so explicitly.
    private GitProcessOutcome FinalOutcome()
        => _outcome
           ?? new GitProcessOutcome(
               false,
               _aborted ? "Aborted" : "The process dialog was closed before the operation finished.",
               _aborted);

    /// <summary>
    ///  Abort: kills the running git process tree, clears the <c>index.lock</c> it may
    ///  have left behind (as <c>FormStatus.Abort_Click</c> does via
    ///  <c>module.UnlockIndex(includeSubmodules: true)</c>), writes "Aborted" to the
    ///  console and reports an aborted outcome to the caller. Killing and unlocking
    ///  happen off the UI thread; the dialog closes once the process is really gone.
    /// </summary>
    private void AbortOperation()
    {
        if (_aborted || _finished || _scope is null)
        {
            return;
        }

        _aborted = true;
        _abort.IsEnabled = false;
        _status.Text = "Aborting…";
        _status.Foreground = Brushes.OrangeRed;
        Append(string.Empty);
        Append("Aborted");

        Services.GitProcessScope scope = _scope;
        _ = Task.Run(() =>
        {
            bool killed = scope.KillAll();
            string? repo = scope.RepoPath;
            string? unlockError = null;

            // Only touch index.lock when we actually killed the git process that
            // could have owned it: deleting a live git's lock file would corrupt a
            // still-running command.
            if (killed && !string.IsNullOrEmpty(repo))
            {
                try
                {
                    GitContext.CreateModule(repo).UnlockIndex(includeSubmodules: true);
                }
                catch (Exception ex)
                {
                    unlockError = ex.Message;
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!killed)
                {
                    Append("(no git process was live at that moment; any command this "
                        + "operation starts from now on is killed immediately)");
                }

                if (unlockError is not null)
                {
                    Append("Could not remove index.lock: " + unlockError);
                }
            });
        });
    }

    // Appends a single already-produced line to the beige console and scrolls to
    // the end. Used by the streaming path (called on the UI thread).
    private void AppendLine(string line) => Append(line ?? string.Empty);

    private async Task<GitProcessOutcome> RunInternalAsync(Window owner, Func<GitProcessOutcome> operation)
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

        await ShowDialog(owner);
        return FinalOutcome();
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
        _finished = true;
        _ok.IsEnabled = true;
        _abort.IsEnabled = false;

        if (_aborted)
        {
            // The user killed the process: whatever exit code git ended up with, the
            // operation is an abort, never a success.
            if (!streaming && !string.IsNullOrEmpty(outcome.Output))
            {
                Append(outcome.Output);
            }

            _outcome = new GitProcessOutcome(false, outcome.Output ?? string.Empty, Aborted: true);
            _header.Text = $"Process — {_label} (Aborted)";
            _status.Text = "Aborted";
            _status.Foreground = Brushes.OrangeRed;
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _closeTimer.Tick += (_, _) =>
            {
                _closeTimer?.Stop();
                _closeTimer = null;
                Close();
            };
            _closeTimer.Start();
            return;
        }

        _outcome = outcome;

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

        // Give the exit hook the first word: it may recognise a recoverable failure
        // (a rejected push), ask the user and start another attempt in this window —
        // in which case the dialog must NOT report a result or close. Mirrors
        // FormProcess.OnExit, which skips Done() when HandleOnExit returns true.
        if (_onExit is not null)
        {
            _ = HandleExitAsync(outcome);
            return;
        }

        Settle(outcome);
    }

    // Runs the exit hook, then settles unless it took over with a retry. Exceptions
    // from the hook are swallowed and treated as "not handled": a broken hook must
    // never leave the dialog stuck with no way to finish (upstream does the same,
    // forcing isError = true).
    private async Task HandleExitAsync(GitProcessOutcome outcome)
    {
        bool handled;
        try
        {
            handled = await _onExit!(this, outcome);
        }
        catch (Exception)
        {
            handled = false;
        }

        // A retry started by the hook (or an Abort during the question) means this
        // outcome is stale — the run in flight will call Complete again.
        if (!handled && !_aborted && _finished)
        {
            Settle(outcome);
        }
    }

    // Reports the final result of a run: closing hint, success/failure chrome and
    // the auto-close rules. Split out of Complete so the exit hook can suppress it.
    private void Settle(GitProcessOutcome outcome)
    {
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

            // On an authentication failure, when the caller opted in, auto-close so
            // it can immediately show the in-app credentials prompt and retry —
            // otherwise stay open (regardless of the checkbox) to show the error.
            if (_closeOnAuthFailure && LooksLikeAuthFailure(outcome.Output))
            {
                _status.Text = "Authentication required — asking for credentials…";
                _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _closeTimer.Tick += (_, _) =>
                {
                    _closeTimer?.Stop();
                    _closeTimer = null;
                    Close();
                };
                _closeTimer.Start();
            }
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
