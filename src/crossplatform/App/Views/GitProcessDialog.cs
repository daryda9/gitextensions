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
public sealed class GitProcessDialog : Theming.ZoomWindow, Services.IGitPtyHost
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
    private readonly ProgressBar _progress;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly TextBlock _inputLabel;
    private readonly Grid _inputRow;

    /// <summary>
    ///  What the header and the status line are currently saying. Both are rewritten
    ///  as the run progresses, so a language change cannot simply re-apply a fixed
    ///  caption: it has to know which sentence is on screen. <see cref="Phase.Prompt"/>
    ///  is the one state whose status text belongs to git (the question it just
    ///  asked) and is therefore never translated.
    /// </summary>
    private enum Phase
    {
        Running,
        Aborting,
        Aborted,
        Success,
        Failed,
        AuthRequired,
        Prompt,
    }

    private Phase _phase = Phase.Running;

    // The console content. Held in a terminal-aware buffer (not in the TextBox) so a
    // \r progress update REWRITES the current line instead of appending another one,
    // and so appending is not an O(n²) string concatenation.
    private readonly Services.PtyTextBuffer _console = new();
    private DispatcherTimer? _renderTimer;
    private long _renderedVersion = -1;

    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _closeTimer;
    private int _consumed;

    // PTY mode: the live terminal the git command runs on, so the input box can
    // answer whatever git asks. null while nothing is running.
    private Services.PtyProcess? _pty;
    private bool _interactive;
    private string? _promptShown;

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
        Width = 760;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // The success/failure inks sit on App.Window (this dialog's background), not
        // on the fixed beige console, so they have to follow the theme. They used to
        // be Brushes.LimeGreen / Brushes.OrangeRed, which measured 1.91:1 and 3.10:1
        // against the light theme's #F3F3F3 — the "Success" label was verified washed
        // out on screen. App.DiffAdded/App.DiffRemoved already carry a green and a red
        // per theme, so no App.Success/App.Error pair is invented here.
        _check = new TextBlock
        {
            Text = "✔",
            Foreground = Brush("App.DiffAdded", Brushes.LimeGreen),
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            IsVisible = false,
        };

        _header = new TextBlock
        {
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

        // TextBoxSurface, not plain Background/Foreground: the Fluent theme repaints
        // the box from theme resources on hover/focus, which turned this console
        // black-on-black the moment it was clicked (see TextBoxSurface docs).
        _output = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("monospace"),
                BorderThickness = new Thickness(0),
            },
            ConsoleBackground,
            ConsoleForeground,
            border: ConsoleBackground,
            placeholderForeground: ConsoleForeground);
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
            Foreground = Brush("App.TextDim", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            // A prompt echoed here can be long; ellipsize it instead of letting it
            // run under the footer buttons.
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Remembered across runs, as upstream does: the checkbox is the inverse of
        // the single global AppSettings.CloseProcessDialog flag
        // (FormStatus.cs:50 reads it, FormStatus.cs:276 writes it back). Unchecking it
        // once must therefore still be unchecked the next time ANY process dialog
        // opens — the port previously hard-coded `true` here, which is why the choice
        // was forgotten.
        _keepOpen = new CheckBox
        {
            IsChecked = !new Services.ViewPrefsService().Load().CloseProcessDialog,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _keepOpen.IsCheckedChanged += (_, _) => KeepOpenChanged();

        // OK stays disabled until the operation really finished: closing mid-run
        // must not look like an acknowledged success (mirrors FormStatus, where Ok
        // is only enabled by Done()).
        _ok = MakeButton();
        _ok.IsEnabled = false;

        // IsDefault (Enter activates it) is granted only together with IsEnabled, so
        // Enter can never close the dialog while git is still running — there Enter
        // belongs to the interactive Reply box, which handles the key itself.
        _ok.IsDefault = false;
        _ok.Click += (_, _) => Close();

        // Abort is only shown when there is something we can genuinely kill (the
        // streaming path); RunStreamingInternalAsync makes it visible.
        _abort = MakeButton();
        _abort.IsVisible = false;
        _abort.Click += (_, _) => AbortOperation();

        // Progress reflects what git itself reports on its \r line: a real percentage
        // when there is one, indeterminate otherwise. It is never advanced on a guess.
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            IsIndeterminate = true,
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // Answers to whatever git asks on the terminal: an SSH key passphrase, the
        // host-key yes/no, an HTTPS username/password. Only shown on the PTY path,
        // where there is a terminal to write to.
        _inputLabel = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        _input = new TextBox { VerticalAlignment = VerticalAlignment.Center };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == global::Avalonia.Input.Key.Enter)
            {
                SendInput();
                e.Handled = true;
            }
        };

        _send = MakeButton();
        _send.MinWidth = 70;
        _send.Margin = new Thickness(8, 0, 0, 0);
        _send.Click += (_, _) => SendInput();

        _inputRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };
        Grid.SetColumn(_inputLabel, 0);
        Grid.SetColumn(_input, 1);
        Grid.SetColumn(_send, 2);
        _inputRow.Children.Add(_inputLabel);
        _inputRow.Children.Add(_input);
        _inputRow.Children.Add(_send);

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
        DockPanel.SetDock(_progress, Dock.Bottom);
        DockPanel.SetDock(_inputRow, Dock.Bottom);
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(headerRow);
        body.Children.Add(footer);
        body.Children.Add(_inputRow);
        body.Children.Add(_progress);
        body.Children.Add(_scroll);
        Content = body;

        ApplyTranslations();
        Services.TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Services.TranslationService.LanguageChanged -= OnLanguageChanged;

        // One renderer for the whole dialog lifetime: it copies the console buffer into
        // the TextBox only when it changed, which keeps a flood of progress updates
        // (hundreds per second on a fast clone) from saturating the UI thread.
        Opened += (_, _) => StartRenderer();
        Closed += (_, _) =>
        {
            _renderTimer?.Stop();
            _renderTimer = null;
        };

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
    ///  <para>By default the command runs on a PSEUDO-TERMINAL
    ///  (<paramref name="interactive"/>), which is what makes git print its own
    ///  transfer progress (the <c>\r</c>-updated "Receiving objects:  37%" line, shown
    ///  here as one self-updating line plus a real progress bar) and what makes its
    ///  questions answerable in the input box at the bottom: SSH key passphrase,
    ///  host-key <c>yes/no</c>, HTTPS username/password. On that path — and only there
    ///  — <c>GIT_TERMINAL_PROMPT</c> is 1; on pipes it stays 0, because a question
    ///  nobody can see is a silent hang.</para>
    /// </summary>
    /// <param name="interactive">
    ///  <see langword="false"/> keeps the old piped, strictly non-interactive
    ///  behaviour for operations that must never wait for a human.
    /// </param>
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
        Func<GitProcessDialog, GitProcessOutcome, Task<bool>>? onExit = null,
        bool interactive = true)
    {
        GitProcessDialog dialog = new(label)
        {
            _closeOnAuthFailure = closeOnAuthFailure,
            _onExit = onExit,
            _interactive = interactive,
        };
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

        // The caller's note is already in the user's language when it supplies one;
        // only the default belongs to this dialog.
        Append(note ?? T("Retrying…"));
        Append(string.Empty);
        StartStreamingRun();
    }

    // When set, a failure whose output looks like an authentication failure
    // auto-closes the dialog (like success does) so the caller can immediately
    // hand off to the in-app credentials prompt instead of the user having to
    // dismiss a "Failed" dialog first.
    private bool _closeOnAuthFailure;

    // The language-independent auth verdict for the run in flight: the services
    // report into it (credential-helper verbs, exit code), so the dialog no longer
    // depends on git's diagnostics being in a language it happens to know. Recreated
    // per run, because one dialog hosts several attempts.
    private Services.GitAuthSignal? _authSignal;

    // Authentication-failure markers, the SECOND opinion now that Services.
    // GitEnvironment pins the port's git children to English diagnostics: without
    // that pinning an Italian git printed "Autenticazione non riuscita", no marker
    // matched, and the CredentialsDialog fallback never opened (round 10 defect).
    // Translated markers are deliberately NOT added here — the locale of the child
    // process is the thing that was made deterministic instead.
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
        _ok.IsDefault = false;
        _abort.IsEnabled = true;
        _check.IsVisible = false;
        SetPhase(Phase.Running);

        Services.GitAuthSignal authSignal = new();
        _authSignal = authSignal;

        _ = Task.Run(() =>
        {
            // Bind the scope to this logical flow so every git process the
            // operation starts registers itself and becomes killable.
            Services.GitStreamRunner.EnterScope(scope);

            // …and the auth signal, so a service that recognises an authentication
            // failure (in any language) can say so directly.
            Services.GitAuthSignal.Enter(authSignal);

            // …and, when interactive, bind this dialog as the terminal host: git then
            // runs on a PTY, so it prints its own transfer progress and can ASK.
            if (_interactive)
            {
                Services.GitStreamRunner.EnterPtyHost(this);
            }

            GitProcessOutcome outcome;
            try
            {
                // On the PTY path append straight into the (thread-safe) console
                // buffer: hopping through the dispatcher would let the terminal bytes,
                // which arrive without a hop, overtake the command header and land
                // above it. On the piped path there is no such race, and the existing
                // dispatcher hop is kept.
                outcome = _interactive
                    ? operation(line => _console.AppendLine(line))
                    : operation(line => Dispatcher.UIThread.Post(() => AppendLine(line)));
            }
            catch (Exception ex)
            {
                outcome = new GitProcessOutcome(false, ex.GetBaseException().Message ?? OperationFailedText);
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
               _aborted ? T("Aborted") : T("The process dialog was closed before the operation finished."),
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
        SetPhase(Phase.Aborting);
        Append(string.Empty);
        Append(T("Aborted"));

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
                    Append(T("(no git process was live at that moment; any command this "
                        + "operation starts from now on is killed immediately)"));
                }

                if (unlockError is not null)
                {
                    // The exception text is the runtime's; only the sentence is ours.
                    Append(Services.TranslationService.TFormat(
                        null, "Could not remove index.lock: {0}", unlockError));
                }
            });
        });
    }

    // Appends a single already-produced line to the beige console and scrolls to
    // the end. Used by the streaming path (called on the UI thread).
    private void AppendLine(string line) => Append(line ?? string.Empty);

    // ---- IGitPtyHost: the git command running on a pseudo-terminal ---------------
    // Called from the operation's thread and from the PTY reader thread. Nothing here
    // touches Avalonia state: the buffer is thread-safe and the renderer (UI thread)
    // picks the changes up on its next tick.

    void Services.IGitPtyHost.Started(Services.PtyProcess pty) => _pty = pty;

    void Services.IGitPtyHost.Output(byte[] buffer, int count) => _console.Feed(buffer, count);

    void Services.IGitPtyHost.Ended(int exitCode) => _pty = null;

    private async Task<GitProcessOutcome> RunInternalAsync(Window owner, Func<GitProcessOutcome> operation)
    {
        // Snapshot the log length so we only stream entries produced by this op.
        _consumed = SafeCommandCount();

        // Header for the non-streaming path only: the streaming path gets it from
        // GitStreamRunner, which echoes the exact command line it runs.
        // No upstream id: FormProcess writes this header from a literal too, so the
        // source-text overload is all there is.
        Append(T("Command to be executed:"));

        Opened += (_, _) =>
        {
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _pollTimer.Tick += (_, _) => DrainNewCommands();
            _pollTimer.Start();

            Services.GitAuthSignal authSignal = new();
            _authSignal = authSignal;

            _ = Task.Run(() =>
            {
                Services.GitAuthSignal.Enter(authSignal);
                return operation();
            }).ContinueWith(t =>
            {
                GitProcessOutcome outcome = t.IsFaulted
                    ? new GitProcessOutcome(false, t.Exception?.GetBaseException().Message ?? OperationFailedText)
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
        _ok.IsDefault = true;
        _abort.IsEnabled = false;
        _pty = null;
        _input.Text = string.Empty;
        Render();

        if (_aborted)
        {
            // The user killed the process: whatever exit code git ended up with, the
            // operation is an abort, never a success.
            if (!streaming && !string.IsNullOrEmpty(outcome.Output))
            {
                Append(outcome.Output);
            }

            _outcome = new GitProcessOutcome(false, outcome.Output ?? string.Empty, Aborted: true);
            SetPhase(Phase.Aborted);
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
        Append(T("Press Enter or Esc to exit…"));

        if (outcome.Success)
        {
            _check.IsVisible = true;
            SetPhase(Phase.Success);

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
            SetPhase(Phase.Failed);

            // On an authentication failure, when the caller opted in, auto-close so
            // it can immediately show the in-app credentials prompt and retry —
            // otherwise stay open (regardless of the checkbox) to show the error.
            // Two independent signals, in order of robustness: the structural one the
            // service reported (credential-helper verbs — identical in every locale),
            // then the English text markers, which are only guaranteed to appear
            // because GitEnvironment pins the child's message locale.
            bool authFailure = _authSignal?.AuthFailureDetected == true
                || LooksLikeAuthFailure(outcome.Output);

            if (_closeOnAuthFailure && authFailure)
            {
                SetPhase(Phase.AuthRequired);
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

        // The dialog stays open (keep-open, failure, or abort-less error): give OK the
        // keyboard focus so the hint above — "Press Enter or Esc to exit…" — is true.
        // Skipped when a close timer is already running, so the auto-close paths are
        // untouched.
        if (_closeTimer is null)
        {
            Dispatcher.UIThread.Post(() => _ok.Focus());
        }
    }

    // Reacts to the user toggling "Keep dialog open", mirroring
    // FormStatus.KeepDialogOpen_CheckedChanged (FormStatus.cs:274-284):
    //
    //  * the choice is persisted at once as the global CloseProcessDialog flag, so the
    //    NEXT dialog opens with the same box state;
    //  * and the invariant is maintained: if the box is turned OFF while the operation
    //    has ALREADY finished successfully, the dialog closes now. Without this the
    //    box was useless in practice — a checkout finishes in a few hundred
    //    milliseconds, so by the time the user reaches the checkbox the auto-close
    //    decision in Settle() has long been taken (with the box still checked), and
    //    unchecking it did nothing at all.
    //
    // An aborted or failed run is left alone, exactly like upstream: only a success
    // closes itself.
    private void KeepOpenChanged()
    {
        bool keep = _keepOpen.IsChecked == true;

        // Best-effort, read-modify-write so another surface's group is not reverted.
        new Services.ViewPrefsService().Update(p => p.CloseProcessDialog = !keep);

        if (!keep && _finished && !_aborted && _outcome?.Success == true)
        {
            _closeTimer?.Stop();
            _closeTimer = null;
            Close();
        }
    }

    // Appends to the console buffer; the renderer copies it into the TextBox on its
    // next tick (or immediately when the dialog is not open yet).
    private void Append(string text)
    {
        _console.AppendLine(text ?? string.Empty);
        if (_renderTimer is null)
        {
            Render();
        }
    }

    private void StartRenderer()
    {
        if (_renderTimer is not null)
        {
            return;
        }

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _renderTimer.Tick += (_, _) => Render();
        _renderTimer.Start();
        Render();
    }

    // Copies the console buffer into the TextBox and derives the progress bar and the
    // input affordances from the live (uncommitted) terminal line.
    private void Render()
    {
        (string text, string currentLine, int? percent, long version) = _console.Snapshot();
        bool running = !_finished && !_aborted;

        if (version != _renderedVersion)
        {
            _renderedVersion = version;
            _output.Text = text;
            _scroll.ScrollToEnd();
        }

        if (running)
        {
            _progress.IsVisible = true;
            if (percent is int p)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = p;
            }
            else
            {
                // No percentage in git's output: an indeterminate bar, never a
                // fabricated one.
                _progress.IsIndeterminate = true;
            }
        }
        else
        {
            _progress.IsVisible = false;
        }

        if (!_interactive)
        {
            return;
        }

        _inputRow.IsVisible = true;
        _input.IsEnabled = running && _pty is not null;
        _send.IsEnabled = _input.IsEnabled;

        // A terminal prompt is a line git left WITHOUT a newline, normally ending in
        // ':' or '?'. Mask the box when what it asks for is a secret: ssh writes the
        // passphrase prompt with echo off, so the answer is never echoed back into the
        // console either.
        bool prompting = running && LooksLikePrompt(currentLine);

        // Put the caret where the answer has to be typed, once per question.
        if (prompting && _promptShown != currentLine)
        {
            _promptShown = currentLine;
            _input.Focus();
        }
        else if (!prompting)
        {
            _promptShown = null;
        }

        bool secret = prompting && IsSecretPrompt(currentLine);
        _input.PasswordChar = secret ? '•' : '\0';
        _inputLabel.Text = prompting ? T("git asks:") : T("Reply:");
        _inputLabel.Foreground = prompting ? Brushes.Goldenrod : Brush("App.TextDim", Brushes.Gray);
        if (prompting && running)
        {
            // The question is git's own line: echoed verbatim, never translated. The
            // phase records that, so a language change leaves it standing.
            _phase = Phase.Prompt;
            _status.Text = currentLine.Trim();
            _status.Foreground = Brushes.Goldenrod;
        }
    }

    private static bool LooksLikePrompt(string line)
    {
        string trimmed = line.TrimEnd();
        return trimmed.Length > 0 && (trimmed.EndsWith(':') || trimmed.EndsWith('?') || trimmed.EndsWith(']'));
    }

    private static bool IsSecretPrompt(string line)
        => line.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
           || line.Contains("password", StringComparison.OrdinalIgnoreCase)
           || line.Contains("PIN", StringComparison.Ordinal);

    // Writes the typed answer to the terminal, followed by the newline the reading
    // program is waiting for, and clears the box (so a passphrase does not linger).
    private void SendInput()
    {
        Services.PtyProcess? pty = _pty;
        string text = _input.Text ?? string.Empty;
        _input.Text = string.Empty;
        if (pty is null || !pty.IsRunning)
        {
            return;
        }

        try
        {
            pty.Write(text + "\n");
        }
        catch (Exception ex)
        {
            Append(Services.TranslationService.TFormat(
                null, "<could not send input: {0}>", ex.Message));
        }
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

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        // The window title never carries the (Done)/(Failed) suffix — only the header
        // inside the dialog does, exactly as before.
        Title = HeaderText(suffix: null);

        // Captions come from FormStatus, the WinForms dialog this one imitates. Its
        // targets carry WinForms accelerators ("&Annulla"); Restyle strips them
        // because the literals passed here have none.
        _keepOpen.Content = T("FormStatus/KeepDialogOpen.Text", "Keep dialog open");
        _ok.Content = T("FormStatus/Ok.Text", "OK");
        _abort.Content = T("FormStatus/Abort.Text", "Abort");

        // The PTY reply row has no WinForms counterpart except upstream's password
        // prompt, whose button is "Send input".
        _send.Content = T("PasswordInput/SendInput.Text", "Send");
        _input.Watermark = T("type here to answer git (Enter sends)");
        _inputLabel.Text = T("Reply:");

        // Restates whatever the header and status line are saying right now. The
        // console is NOT re-rendered: it holds git's output plus lines already
        // written, and re-translating a transcript would rewrite history.
        SetPhase(_phase);
    }

    // Header and status for a phase, and the phase itself: the single place that
    // decides what those two say, so ApplyTranslations can simply replay it.
    private void SetPhase(Phase phase)
    {
        _phase = phase;

        _header.Text = HeaderText(phase switch
        {
            Phase.Aborted => T("Aborted"),
            Phase.Success => T("CreatePullRequestForm/_strDone.Text", "Done"),
            // An authentication failure IS a failure: the header keeps saying so
            // while the status line explains what happens next.
            Phase.Failed or Phase.AuthRequired => T("Failed"),
            _ => null,
        });

        if (phase == Phase.Prompt)
        {
            // git's question is already on the status line; leave it there.
            return;
        }

        _status.Text = phase switch
        {
            Phase.Aborting => T("Aborting…"),
            Phase.Aborted => T("Aborted"),
            Phase.Success => T("Success"),
            Phase.Failed => T("Failed"),
            Phase.AuthRequired => T("Authentication required — asking for credentials…"),
            _ => T("Running…"),
        };

        _status.Foreground = phase switch
        {
            Phase.Success => Brush("App.DiffAdded", Brushes.LimeGreen),
            Phase.Aborting or Phase.Aborted or Phase.Failed or Phase.AuthRequired
                => Brush("App.DiffRemoved", Brushes.OrangeRed),
            _ => Brush("App.TextDim", Brushes.Gray),
        };
    }

    // "Process — <label>", optionally with the run's verdict appended. The label is
    // the caller's and is already in the user's language.
    private string HeaderText(string? suffix)
    {
        string head = Services.TranslationService.TFormat(
            null, "{0} — {1}", T("FormStatus/$this.Text", "Process"), _label);
        return suffix is null
            ? head
            : Services.TranslationService.TFormat(null, "{0} ({1})", head, suffix);
    }

    // Read from the operation's background thread as well as from the UI thread; T is
    // a dictionary hit, so that is safe.
    private static string OperationFailedText => T("Operation failed.");

    private static string T(string english) => Services.TranslationService.T(english);

    private static string T(string? key, string english) => Services.TranslationService.T(key, english);

    private Button MakeButton() => new()
    {
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
