using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The bisect control panel: the one place where a bisect session is started,
///  driven and ended.
///
///  <para><b>What it ports.</b> Upstream <c>FormBisect</c>
///  (<c>GitUI/CommandsDialogs/BrowseDialog/FormBisect.cs</c>) — a small fixed modal
///  with five stacked buttons, reached from the Commands menu's "B&amp;isect..."
///  (<c>FormBrowse.cs:1805-1813</c>) and from the "More" button of the bisect
///  notification bar (<c>InteractiveGitActionControl.cs:242-254</c>). Its gating is
///  copied exactly from <c>FormBisect.UpdateButtonsState:27-35</c>: <c>Start</c> is
///  enabled only when no session is open, and good / bad / skip / stop only when one
///  is.</para>
///
///  <para><b>Why the port needs it.</b> Before this dialog the port had no start
///  affordance at all: the grid's "Bisect: mark good/bad/skip" entries silently ran
///  <c>git bisect start</c> for you if no session was open
///  (<c>MainWindow.RunBisect(… ensureStarted: true)</c>), so a misclick in a submenu
///  detached HEAD and moved the work tree with nothing said. Starting a bisect is now
///  an explicit act performed here.</para>
///
///  <para><b>What it adds over upstream, and why that is not a fake button.</b>
///  Upstream shows no progress whatsoever — five buttons and no text — and git's own
///  "Bisecting: N revisions left to test after this (roughly M steps)" is only ever
///  visible in the transient process window. This dialog states the session state
///  permanently, from <see cref="BisectService.GetSession"/>, i.e. from
///  <c>git rev-list --bisect-vars</c>: every figure shown is one git computed. The
///  "Show log" button prints <c>git bisect log</c>, which is real output too. Nothing
///  here is displayed unless the data behind it was actually read — before both ends
///  of the range are marked there is no count, and the dialog says so instead of
///  showing a zero.</para>
///
///  <para><b>Range seeding.</b> With two commits selected in the grid, starting a
///  bisect offers to seed the range from them, which is upstream's
///  <c>Start_Click</c> → <c>BisectRange</c> path. Upstream marks the <i>first</i>
///  selected row good and the <i>last</i> bad; since the grid is newest-first that
///  labels the newer commit good and the older one bad, i.e. backwards for the
///  ordinary "it worked back then, it is broken now" case — the file carries a
///  "TODO: Improve me" over exactly this code. The port seeds the older commit good
///  and the newer bad instead; the deviation is recorded in NOTES.md.</para>
///
///  <para>All git work happens in <see cref="Task.Run"/> — the service blocks — and
///  the buttons are disabled for the duration. Nothing here throws: a failed command
///  is reported in the output box, and the state is re-read afterwards so the buttons
///  always end up agreeing with the repository.</para>
/// </summary>
public sealed class BisectDialog : Theming.ZoomWindow
{
    private readonly BisectService _bisect = new();
    private readonly string _repoPath;

    // The commits the grid had selected when the dialog was opened, oldest first.
    // Used only to offer upstream's range seeding on start.
    private readonly IReadOnlyList<string> _selection;

    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly TextBox _output;

    private readonly Button _start;
    private readonly Button _good;
    private readonly Button _bad;
    private readonly Button _skip;
    private readonly Button _stop;
    private readonly Button _log;

    private BisectSession _session = BisectSession.None;

    /// <param name="repoPath">The repository to bisect.</param>
    /// <param name="selectedHashes">
    ///  The commits selected in the revision grid, <b>oldest first</b>. Two or more
    ///  enable the "seed the range from the selection" offer on start; fewer simply
    ///  start an empty session, as upstream does.
    /// </param>
    public BisectDialog(string repoPath, IReadOnlyList<string>? selectedHashes = null)
    {
        _repoPath = repoPath;
        _selection = selectedHashes ?? [];

        Title = T("FormBisect/$this.Text", "Bisect");
        Width = 560;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        _status = new TextBlock
        {
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        _detail = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gray),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        // Upstream's captions, verbatim (FormBisect.Designer.cs:45-90). "Start
        // bisect" gains an ellipsis only when it will ask about the range.
        _start = MakeButton(T("FormBisect/Start.Text", "Start bisect"), "Bisect", OnStart);
        _good = MakeButton(T("FormBisect/Good.Text", "Mark current revision &good"), "BisectGood", OnGood);
        _bad = MakeButton(T("FormBisect/Bad.Text", "Mark current revision &bad"), "BisectBad", OnBad);
        _skip = MakeButton(T("FormBisect/btnSkip.Text", "&Skip current revision"), "BisectSkip", OnSkip);
        _stop = MakeButton(T("FormBisect/Stop.Text", "Stop bisect"), "BisectStop", OnStop);

        // Not an upstream button: git bisect log is real output and the only way to
        // see the marks already made, which upstream simply never shows.
        _log = MakeButton(T("Show log"), null, OnLog);

        _output = TextBoxSurface.Apply(
            new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = Theming.AppFonts.Monospace,
                FontSize = 12,
                MinHeight = 120,
                Text = string.Empty,
            },
            Brush("App.Control", Brushes.Black),
            Brush("App.Text", Brushes.Gainsboro));

        ScrollViewer outputScroll = new()
        {
            Content = _output,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        StackPanel actions = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _start, _good, _bad, _skip, _stop, _log },
        };

        Button close = new()
        {
            Content = T("FormCommit/Cancel.Text", "Close"),
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            IsCancel = true,
        };
        close.Click += (_, _) => Close();

        StackPanel top = new()
        {
            Orientation = Orientation.Vertical,
            Children = { _status, _detail, actions },
        };

        DockPanel root = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(close);
        root.Children.Add(outputScroll);

        Content = root;
        DialogKeys.InstallEscapeClose(this);

        // Nothing is enabled until the real state has been read off the disk, so the
        // dialog can never offer an action the repository would refuse.
        SetEnabled(false);
        Opened += (_, _) => Reload();
    }

    /// <summary>
    ///  <see langword="true"/> once any command in here changed the repository, so
    ///  the caller knows to refresh the shell. Mirrors upstream's
    ///  <c>RepoChangedNotifier.Notify()</c> after the dialog closes
    ///  (<c>FormBrowse.cs:1811</c>).
    /// </summary>
    public bool RepositoryChanged { get; private set; }

    // ---- state ------------------------------------------------------------------

    // Re-reads the session off the UI thread and repaints. Never throws.
    private void Reload()
    {
        _ = Task.Run(() =>
        {
            BisectSession session;
            try
            {
                session = _bisect.GetSession(_repoPath);
            }
            catch
            {
                session = BisectSession.None;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _session = session;
                Apply();
            });
        });
    }

    // The gating, straight from FormBisect.UpdateButtonsState:27-35, with two honest
    // refinements: once the search has converged there is nothing left to mark, so
    // good/bad/skip go quiet and only the reset remains; and "Show log" needs a
    // session, because `git bisect log` fails without one.
    private void Apply()
    {
        bool open = _session.InProgress;
        bool markable = open && !_session.Finished;

        _start.IsEnabled = !open;
        _good.IsEnabled = markable;
        _bad.IsEnabled = markable;
        _skip.IsEnabled = markable;
        _stop.IsEnabled = open;
        _log.IsEnabled = open;

        _start.Content = !open && _selection.Count > 1
            ? T("FormBisect/Start.Text", "Start bisect") + "…"
            : T("FormBisect/Start.Text", "Start bisect");

        _status.Text = Describe(_session);
        _detail.Text = Detail(_session);
        _detail.IsVisible = _detail.Text.Length > 0;
    }

    /// <summary>
    ///  The headline: which of git's four bisect states the repository is in. Upstream's
    ///  bar says only "Bisect is currently in progress."
    ///  (<c>InteractiveGitActionControl.cs:13,18</c>); the extra states below are the
    ///  ones git itself distinguishes in <c>git bisect log</c>'s "# status:" lines, and
    ///  they are the difference between "click good or bad" and "why is nothing
    ///  happening".
    /// </summary>
    private static string Describe(BisectSession session)
    {
        if (!session.InProgress)
        {
            return T("No bisect session is in progress.");
        }

        if (session.Finished)
        {
            return T("Bisect finished — the first bad commit has been found.");
        }

        if (!session.BadKnown && !session.GoodKnown)
        {
            return T("Bisect started — waiting for a good and a bad commit.");
        }

        if (!session.BadKnown)
        {
            return T("Bisect in progress — waiting for a bad commit.");
        }

        if (!session.GoodKnown)
        {
            return T("Bisect in progress — waiting for a good commit.");
        }

        return T("Bisect in progress.");
    }

    /// <summary>
    ///  The second line: the remaining work, or the answer. Every number here was
    ///  computed by git (<c>rev-list --bisect-vars</c>); when git could not bound the
    ///  range yet, this says what is missing instead of inventing a count.
    /// </summary>
    private static string Detail(BisectSession session)
    {
        if (!session.InProgress)
        {
            return T("Start one to search the history for the commit that introduced a change.");
        }

        if (session.Finished)
        {
            return session.CulpritHash is { Length: > 0 } culprit
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("First bad commit: {0}. Stop the bisect to restore the branch you started from."),
                    Shorten(culprit))
                : T("Stop the bisect to restore the branch you started from.");
        }

        if (!session.Ready)
        {
            return T("Mark the checked-out commit good or bad — the range cannot be narrowed until both ends are known.");
        }

        if (session.HasProgress)
        {
            // Four wordings, not one with "(s)": the count of revisions and the count
            // of steps run down independently, so "1 revision left … 1 more step" is a
            // real state a bisect passes through and "1 revisions … 1 steps" is what
            // the single format used to print. The step half is nested inside each
            // revision half because a translator has to be able to move the two
            // clauses relative to each other.
            string steps = TranslationService.TPlural(
                null, "roughly {0} more step", "roughly {0} more steps", session.StepsLeft);

            return TranslationService.TPlural(
                null,
                "{0} revision left to test, {1}.",
                "{0} revisions left to test, {1}.",
                session.RevisionsLeft,
                steps);
        }

        return T("This is the last commit to test.");
    }

    private static string Shorten(string hash) => hash.Length > 8 ? hash[..8] : hash;

    // ---- actions ----------------------------------------------------------------

    // Upstream Start_Click (FormBisect.cs:37-67): start the session, then — only
    // with more than one revision selected — offer to seed the range from it.
    private void OnStart()
    {
        _ = StartAsync();

        async Task StartAsync()
        {
            if (!await RunAsync(T("Start bisect"), () => _bisect.Start(_repoPath)))
            {
                return;
            }

            if (_selection.Count < 2)
            {
                return;
            }

            string older = _selection[0];
            string newer = _selection[^1];

            // Upstream's own wording (FormBisect.cs:13-14), with the two commits
            // named so the answer is not a guess.
            string question = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("FormBisect/_bisectStart.Text", "Mark selected revisions as start bisect range?")
                    + "\n\n" + T("Good: {0}\nBad: {1}"),
                Shorten(older),
                Shorten(newer));

            if (!await ConfirmAsync(question))
            {
                return;
            }

            if (await RunAsync(T("Bisect good"), () => _bisect.MarkGood(_repoPath, older)))
            {
                await RunAsync(T("Bisect bad"), () => _bisect.MarkBad(_repoPath, newer));
            }
        }
    }

    private void OnGood() => _ = RunAsync(T("Bisect good"), () => _bisect.MarkGood(_repoPath, "HEAD"));

    private void OnBad() => _ = RunAsync(T("Bisect bad"), () => _bisect.MarkBad(_repoPath, "HEAD"));

    private void OnSkip() => _ = RunAsync(T("Bisect skip"), () => _bisect.Skip(_repoPath, "HEAD"));

    private void OnStop() => _ = RunAsync(T("Stop bisect"), () => _bisect.Reset(_repoPath));

    // `git bisect log` changes nothing, so it neither refreshes the shell nor
    // re-reads the session — it only fills the output box.
    private void OnLog()
    {
        SetEnabled(false);

        _ = Task.Run(() =>
        {
            BisectResult result;
            try
            {
                result = _bisect.Log(_repoPath);
            }
            catch (Exception ex)
            {
                result = new BisectResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _output.Text = result.Output;
                Apply();
            });
        });
    }

    /// <summary>
    ///  Runs one bisect command off the UI thread with the buttons disabled, shows
    ///  git's own output, then re-reads the session so the buttons match reality.
    ///  Returns whether the command succeeded; never throws.
    /// </summary>
    private async Task<bool> RunAsync(string label, Func<BisectResult> action)
    {
        SetEnabled(false);
        _output.Text = label + "…";

        BisectResult result;
        try
        {
            result = await Task.Run(action);
        }
        catch (Exception ex)
        {
            result = new BisectResult(false, ex.Message);
        }

        // Even a failed command may have moved the work tree half-way, so the shell
        // is told to refresh either way.
        RepositoryChanged = true;

        _output.Text = result.Output.Length > 0
            ? result.Output
            : (result.Success ? label + ": " + T("done.") : label + ": " + T("failed."));

        BisectSession session;
        try
        {
            session = await Task.Run(() => _bisect.GetSession(_repoPath));
        }
        catch
        {
            session = BisectSession.None;
        }

        _session = session;
        Apply();
        return result.Success;
    }

    // Disables every action while one is running. Apply() then restores exactly the
    // set the repository state allows, so this never leaves a wrong button live.
    private void SetEnabled(bool enabled)
    {
        _start.IsEnabled = enabled;
        _good.IsEnabled = enabled;
        _bad.IsEnabled = enabled;
        _skip.IsEnabled = enabled;
        _stop.IsEnabled = enabled;
        _log.IsEnabled = enabled;
    }

    // A yes/no modal, the same hand-built shape the other dialogs of this port use
    // (e.g. RemotesDialog.ConfirmAsync) — Avalonia ships no message box.
    private async Task<bool> ConfirmAsync(string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Confirm"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("FormCommit/Cancel.Text", "Cancel"), IsCancel = true };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("FormBisect/$this.Text", "Bisect"),
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { yes, no },
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    Foreground = Brush("App.Text", Brushes.Gainsboro),
                    TextWrapping = TextWrapping.Wrap,
                },
                buttons,
            },
        };

        DialogKeys.InstallEscapeClose(dialog);
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // ---- construction helpers ----------------------------------------------------

    private static Button MakeButton(string caption, string? icon, Action onClick)
    {
        Button button = new()
        {
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4),
        };

        string text = RevisionFilterDialog.StripMnemonic(caption);

        if (icon is { Length: > 0 } && IconLoader.Image(icon) is { } image)
        {
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { image, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            };
        }
        else
        {
            button.Content = text;
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
