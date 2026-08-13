using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The list of steps an interactive rebase still has to replay, opened for editing —
///  upstream's <c>Edit todo</c> (<c>FormRebase.cs:300-315</c>,
///  <c>Commands.EditTodoRebase()</c>), which is the one rebase command this port was
///  missing.
///
///  <para><b>Why a window and not the command.</b> Upstream hands
///  <c>git rebase --edit-todo</c> to <c>FormProcess</c> and lets git open the user's
///  editor. That is not available here: this port runs interactive commands on a PTY, so
///  git would start a full-screen editor inside a text box that is not a terminal — the
///  defect M183 fixed, whose rule is that anything reaching the PTY must be explicitly
///  editor-less. So the editor is this window, and
///  <see cref="RebaseSessionService.ReadTodo"/> /
///  <see cref="RebaseSessionService.WriteTodo"/> are the scripted
///  <c>GIT_SEQUENCE_EDITOR</c> underneath it.</para>
///
///  <para><b>git validates, this window does not.</b> The text typed into git's buffer is
///  parsed and installed by git, and a list it refuses comes back as git's own message,
///  shown verbatim at the bottom of this window with the edits still on screen so they can
///  be fixed. The two checks made <i>before</i> asking git are the two that are either
///  irreversible (an empty list) or certain (a <c>squash</c> with nothing to meld into) —
///  and even those are stated as what git will do, not as rules of this port's own.</para>
///
///  <para><b>A row is a commit, not a hash.</b> git's todo names each step by abbreviated
///  id and subject; both are kept, because deciding to squash the fourth commit into the
///  third is a decision about what they <i>are</i>. Steps this port does not model —
///  <c>exec</c>, <c>break</c>, <c>label</c>, <c>reset</c>, <c>merge</c>,
///  <c>update-ref</c>, a flagged <c>fixup -C</c> — are shown as the raw line git wrote and
///  can be moved or removed but not re-commanded, and travel back byte for byte.</para>
///
///  <para>Every git call blocks and therefore runs through <see cref="Task.Run"/>. The
///  window closes only after git has accepted the list.</para>
/// </summary>
public sealed class RebaseTodoWindow : Theming.ZoomWindow
{
    private readonly RebaseSessionService _service;
    private readonly string _repoPath;

    private readonly ObservableCollection<RebaseTodoStep> _steps = [];
    private readonly ListBox _list;
    private readonly TextBlock _summary;
    private readonly TextBox _error;
    private readonly Border _errorBox;
    private readonly StackPanel _commandButtons;
    private readonly Button _up;
    private readonly Button _down;
    private readonly Button _remove;
    private readonly Button _apply;
    private readonly Button _cancel;

    // How many of the steps git listed when the window opened were COMMIT steps — not how
    // many rows there were. The two differ by every exec/label/reset/merge/update-ref line,
    // and a --rebase-merges todo is mostly those: the 18-step todo measured on the field had
    // 4 commits in it. Counting rows made "take out one exec" report "1 commit will not be
    // in the rebased branch", which is false and is exactly the sentence a user acts on.
    private int _initialCommits;

    // Commands already replayed. Zero means a squash/fixup at the head of the list has
    // nothing to meld into and git will refuse it — see Validate.
    private int _doneSteps;

    // True when the rows came from git's own todo file because git refused to open the
    // list (see RebaseSessionService.ReadTodo). The window is then a repair tool, and says
    // so in the summary.
    private bool _fromStorage;

    private bool _busy;

    public RebaseTodoWindow(string repoPath, RebaseSessionService service)
    {
        _repoPath = repoPath;
        _service = service;

        IBrush text = Brush("App.Text", Brushes.Gainsboro);
        IBrush dim = Brush("App.TextDim", Brushes.Gray);

        Title = T("FormRebase/btnEditTodo.Text", "Edit todo...");
        Width = 760;
        Height = 560;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.Black);

        TextBlock intro = new()
        {
            // The three verbs are the WHOLE vocabulary, and saying so is the only way the
            // window can be told apart from an editor that lost the user's text: a step
            // cannot be added — not here and not in git's own todo editor either, because the
            // list is the plan the rebase already made and nothing outside it is loaded. The
            // second sentence is the other half of the same honesty: a row taken out is only
            // recoverable while the window is open, which makes Cancel the undo.
            Text = T("These steps have not been replayed yet. Change what each one does, put them in "
                + "another order, or take one out — those three are all git's todo allows; no step "
                + "can be added to it. Cancel puts every row back, but a row taken out is gone once "
                + "you Apply, which hands the list to git to check and install. Nothing is replayed "
                + "until you use Continue."),
            Foreground = dim,
            FontSize = Metrics.Text.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, Metrics.Space.Sm),
        };

        _list = new ListBox
        {
            ItemsSource = _steps,
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = text,
            BorderBrush = Brush("App.BorderStrong", new SolidColorBrush(Color.Parse("#88898F"))),
            BorderThickness = new Thickness(1),
            ItemTemplate = new FuncDataTemplate<RebaseTodoStep>((step, _) => Row(step)),
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();

        _commandButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
        };

        foreach (string command in RebaseTodo.CommitCommands)
        {
            _commandButtons.Children.Add(CommandButton(command));
        }

        // Moving and removing are the two edits that are not a command: they change the
        // shape of the list rather than what a step does.
        _up = MakeButton(T("Move up"), T("Replay this step earlier in the series"), () => Move(-1));
        _down = MakeButton(T("Move down"), T("Replay this step later in the series"), () => Move(+1));
        _remove = MakeButton(
            T("Remove"),
            T("Take this step out of the list. Its commit will not be in the rebased branch — "
                + "the same effect as 'drop', which keeps the row visible instead."),
            RemoveSelected);

        StackPanel shapeButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _up, _down, _remove },
        };

        Grid tools = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, Metrics.Space.Sm, 0, Metrics.Space.Sm),
        };
        Grid.SetColumn(_commandButtons, 0);
        Grid.SetColumn(shapeButtons, 2);
        tools.Children.Add(_commandButtons);
        tools.Children.Add(shapeButtons);

        // The legend is permanent, not a tooltip: squash and fixup differ only in what
        // happens to a commit message, which is invisible until it is already lost.
        TextBlock legend = new()
        {
            Text = string.Join("\n", Legend()),
            Foreground = dim,
            FontSize = Metrics.Text.Caption,
            TextWrapping = TextWrapping.Wrap,
        };

        _summary = new TextBlock
        {
            Foreground = dim,
            FontSize = Metrics.Text.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
        };

        // git's own words, in git's own language, in a box the user can select and copy.
        // Read-only rather than a TextBlock because a rejected todo is something to take
        // to a terminal or a bug report.
        _error = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brush("App.IconRed", new SolidColorBrush(Color.Parse("#D16969"))),
            FontFamily = new FontFamily("monospace"),
            FontSize = Metrics.Text.Caption,
        };

        _errorBox = new Border
        {
            IsVisible = false,
            Background = Brush("App.PanelAlt", Brushes.Black),
            BorderBrush = Brush("App.IconRed", new SolidColorBrush(Color.Parse("#D16969"))),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Metrics.Space.Sm),
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
            Child = _error,
        };

        // The accept button is named after what it does to the list, not after the window:
        // the window is "Edit todo", the button installs what was edited.
        _apply = MakeButton(
            T("Apply"),
            T("Hand the list to git, which checks it and installs it (git rebase --edit-todo)"),
            () => _ = ApplyAsync());
        _apply.IsDefault = true;

        _cancel = MakeButton(
            T("FormCommit/Cancel.Text", "Cancel"),
            T("Close without changing anything"),
            Close);
        _cancel.IsCancel = true;

        StackPanel dialogButtons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = Metrics.Space.Sm,
            Margin = new Thickness(0, Metrics.Space.Md, 0, 0),
            Children = { _apply, _cancel },
        };

        Grid root = new()
        {
            Margin = new Thickness(Metrics.Space.Md),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto"),
        };
        Grid.SetRow(intro, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(tools, 2);
        Grid.SetRow(legend, 3);
        Grid.SetRow(_summary, 4);
        Grid.SetRow(_errorBox, 5);
        Grid.SetRow(dialogButtons, 6);
        root.Children.Add(intro);
        root.Children.Add(_list);
        root.Children.Add(tools);
        root.Children.Add(legend);
        root.Children.Add(_summary);
        root.Children.Add(_errorBox);
        root.Children.Add(dialogButtons);

        Content = root;
        DialogKeys.InstallEscapeClose(this);
        Opened += (_, _) => Load();
    }

    /// <summary>
    ///  Shows the window modally over <paramref name="owner"/>. Answers true when git
    ///  accepted a new list, so the caller knows whether to refresh anything.
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, string repoPath, RebaseSessionService service)
    {
        RebaseTodoWindow window = new(repoPath, service);
        await window.ShowDialog(owner);
        return window.Applied;
    }

    /// <summary>True once git has accepted an edited list; false after a plain Cancel.</summary>
    public bool Applied { get; private set; }

    // ---- one row -----------------------------------------------------------------------

    // Command, then abbreviated id, then subject. The command is colour-coded because the
    // whole point of the list is scanning down the left-hand column; the colours are the
    // theme's icon hues, which are tuned to stay readable as text in both themes.
    private Control Row(RebaseTodoStep? step)
    {
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Md,
        };

        if (step is null)
        {
            return row;
        }

        if (!step.IsCommitStep)
        {
            // A step this port does not model: show git's line as it is, so nothing about
            // it looks editable when it is not.
            row.Children.Add(new TextBlock
            {
                Text = step.Raw,
                Foreground = Brush("App.TextDim", Brushes.Gray),
                FontFamily = new FontFamily("monospace"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return row;
        }

        row.Children.Add(new TextBlock
        {
            Text = step.Command,
            Foreground = CommandBrush(step.Command),
            FontFamily = new FontFamily("monospace"),
            FontWeight = FontWeight.SemiBold,
            Width = 72,
        });

        row.Children.Add(new TextBlock
        {
            // git abbreviates for its editor but stores full ids, and the fallback path
            // reads the stored file; shorten for the eye only — what goes back to git is
            // whatever git gave, untouched.
            Text = step.Sha.Length > 10 ? step.Sha[..8] : step.Sha,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            FontFamily = new FontFamily("monospace"),
            Width = 80,
        });

        row.Children.Add(new TextBlock
        {
            Text = step.Subject,
            // A dropped commit is still listed, so it has to look dropped.
            Foreground = step.Command == "drop"
                ? Brush("App.TextDim", Brushes.Gray)
                : Brush("App.Text", Brushes.Gainsboro),
            TextDecorations = step.Command == "drop" ? TextDecorations.Strikethrough : null,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return row;
    }

    private static IBrush CommandBrush(string command) => command switch
    {
        "reword" => Brush("App.IconBlue", Brushes.SteelBlue),
        "edit" => Brush("App.IconAmber", Brushes.Goldenrod),
        "squash" => Brush("App.IconPurple", Brushes.MediumPurple),
        "fixup" => Brush("App.IconCyan", Brushes.CadetBlue),
        "drop" => Brush("App.IconRed", Brushes.IndianRed),
        _ => Brush("App.Text", Brushes.Gainsboro),
    };

    /// <summary>
    ///  One line per command, always on screen. The wording is git's own definition from
    ///  the legend it writes into every todo file, said in a full sentence: git's
    ///  "like squash but keep only the previous commit's log message" is only readable
    ///  next to the squash line it refers to, which is exactly what a tooltip would hide.
    /// </summary>
    private static IEnumerable<string> Legend() => RebaseTodo.CommitCommands.Select(c => c + " — " + Describe(c));

    private static string Describe(string command) => command switch
    {
        "pick" => T("replay this commit as it is."),

        // Measured, not assumed: git asks a message editor for reword and squash, and this
        // port answers that editor for git (GIT_EDITOR=true in RebaseSessionService) because
        // the alternative on a PTY is a full-screen vi inside a text box that hangs the
        // process dialog. So the message is NOT offered. Followed to the end on git 2.43 with
        // no editor in the environment: a reword of "topic A" produced a commit still called
        // "topic A", and a squash produced git's default concatenation, "topic B\n\ntopic C".
        // Saying "you get to write the combined message" was therefore a promise the port
        // breaks silently, on the one thing that is invisible until it is already lost.
        "reword" => T("replay it — but its message comes through UNCHANGED: Continue answers "
            + "git's message editor for you. Use 'Reword commit…' on the result afterwards."),
        "edit" => T("replay it, then stop so you can amend the commit itself."),
        "squash" => T("melt it into the step above. The combined message is git's default — "
            + "both messages, one after the other — and is not offered for editing."),
        "fixup" => T("melt it into the step above and throw ITS message away — the step above keeps its own."),
        "drop" => T("do not replay it. The commit will not be in the branch."),
        _ => string.Empty,
    };

    // ---- editing ------------------------------------------------------------------------

    private Button CommandButton(string command)
    {
        Button button = new()
        {
            Content = command,
            FontFamily = new FontFamily("monospace"),
            Padding = new Thickness(Metrics.Space.Sm, Metrics.Space.Xs),
            MinWidth = 0,
        };
        ToolTip.SetTip(button, command + " — " + Describe(command));
        button.Click += (_, _) => SetCommand(command);
        return button;
    }

    private void SetCommand(string command)
    {
        int index = _list.SelectedIndex;
        if (_busy || index < 0 || index >= _steps.Count || !_steps[index].IsCommitStep)
        {
            return;
        }

        // Replacing the record is what makes the row redraw: the steps are immutable, and
        // an ObservableCollection notices a replacement, not a mutation.
        _steps[index] = _steps[index] with { Command = command };
        _list.SelectedIndex = index;
        UpdateSummary();
        UpdateButtons();
    }

    private void Move(int delta)
    {
        int index = _list.SelectedIndex;
        int target = index + delta;
        if (_busy || index < 0 || target < 0 || target >= _steps.Count)
        {
            return;
        }

        _steps.Move(index, target);
        _list.SelectedIndex = target;
        UpdateButtons();
    }

    private void RemoveSelected()
    {
        int index = _list.SelectedIndex;
        if (_busy || index < 0 || index >= _steps.Count)
        {
            return;
        }

        _steps.RemoveAt(index);

        // Keep a selection where the removed row was, so a run of removals needs no
        // re-aiming; the summary below counts what that is costing.
        _list.SelectedIndex = _steps.Count == 0 ? -1 : Math.Min(index, _steps.Count - 1);
        UpdateSummary();
        UpdateButtons();
    }

    // ---- loading and applying ------------------------------------------------------------

    private void Load()
    {
        _busy = true;
        UpdateButtons();
        _summary.Text = T("Reading the todo list from git…");

        _ = Task.Run(() =>
        {
            RebaseTodoList todo = _service.ReadTodo(_repoPath);

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;

                if (!todo.Success)
                {
                    ShowGitOutput(todo.Output);
                    _summary.Text = T("git would not open the todo list.");
                    UpdateButtons();
                    return;
                }

                _steps.Clear();
                foreach (RebaseTodoStep step in todo.Steps)
                {
                    _steps.Add(step);
                }

                _initialCommits = todo.Steps.Count(s => s.IsCommitStep);
                _doneSteps = todo.DoneSteps;
                _fromStorage = todo.FromStorage;
                _list.SelectedIndex = _steps.Count > 0 ? 0 : -1;
                UpdateSummary();
                UpdateButtons();

                if (todo.FromStorage)
                {
                    // git rejected the list it already has and would not open it. The rows
                    // are that rejected list, straight from git's file, so its complaint has
                    // to stay on screen: this is a repair, not an edit.
                    ShowGitOutput(todo.Output);
                }
            });
        });
    }

    private async Task ApplyAsync()
    {
        if (_busy)
        {
            return;
        }

        if (Validate() is { Length: > 0 } refusal)
        {
            ShowGitOutput(refusal);
            return;
        }

        if (_steps.Count == 0 && !await ConfirmAsync(
            // Measured on git 2.43, not deduced: --edit-todo accepts an empty list, and the
            // next --continue ends the rebase where it stands. git's own todo legend
            // promises the rebase would be "aborted", which is true only for the list it
            // writes at the START of a rebase — half-way through, the steps already
            // replayed stay and the pending ones are simply gone.
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("The list is empty.\n\nThe rebase will end at the commit it is stopped on, and the "
                    + "{0} remaining commits will not be in the branch. This is not an abort: what has "
                    + "already been replayed stays. The dropped commits survive only in the reflog."),
                _initialCommits),
            T("FormRebase/btnEditTodo.Text", "Edit todo...")))
        {
            return;
        }

        _busy = true;
        _errorBox.IsVisible = false;
        UpdateButtons();
        _summary.Text = T("Handing the list to git…");

        List<RebaseTodoStep> steps = [.. _steps];
        RebaseCommandResult result = await Task.Run(() => _service.WriteTodo(_repoPath, steps, _ => { }));

        _busy = false;
        UpdateButtons();

        if (!result.Success)
        {
            // git refused: keep the window, the edits and git's message together, because
            // the fix is one row away and re-reading the list would throw the work out.
            ShowGitOutput(result.Output);
            UpdateSummary();
            return;
        }

        Applied = true;
        Close();
    }

    /// <summary>
    ///  The one check made before asking git, and only because git's refusal would be
    ///  certain: <c>squash</c> and <c>fixup</c> meld into the step above, so the first
    ///  pending step can only carry them when something has already been replayed for it to
    ///  meld into. Verified both ways on git 2.43 — with an empty <c>done</c> git answers
    ///  "cannot 'squash' without a previous commit" and refuses the whole edit; with one
    ///  commit replayed it accepts it, and melding the next commit into the one just
    ///  replayed is a legitimate thing to ask for, so it is <b>not</b> blocked here.
    ///  <para>Everything else is left to git on purpose. Returns "" when there is nothing
    ///  to say.</para>
    /// </summary>
    private string Validate()
    {
        if (_doneSteps > 0 || _steps.Count == 0)
        {
            return string.Empty;
        }

        RebaseTodoStep first = _steps[0];
        return first.Command is "squash" or "fixup"
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("git will refuse this list: '{0}' melts a commit into the one before it, and this is "
                    + "the first step of the rebase — there is nothing before it yet. Move another step "
                    + "above it, or use 'pick'."),
                first.Command)
            : string.Empty;
    }

    private void ShowGitOutput(string text)
    {
        _error.Text = text.Trim();
        _errorBox.IsVisible = _error.Text.Length > 0;
    }

    private void UpdateSummary()
    {
        // What the user loses is measured in COMMITS, so both terms are commit counts: the
        // ones that arrived, minus the ones still on their way into the branch. A commit
        // leaves the branch two ways and they must count once each — its row was taken out,
        // or its row still reads 'drop'. Rows that are not commits (exec, label, reset,
        // merge, update-ref) cancel out of both terms, which is the point.
        int kept = _steps.Count(s => s.IsCommitStep && s.Command != "drop");
        int dropped = _initialCommits - kept;

        string counted = TranslationService.TPlural(null, "{0} step left.", "{0} steps left.", _steps.Count);

        // The count of commits that will not be in the branch is the number the user cannot
        // get back, so it is stated on its own rather than left to be worked out from the
        // list length.
        string summary = dropped <= 0
            ? counted
            : counted + " " + TranslationService.TPlural(
                null,
                "{0} commit will not be in the rebased branch.",
                "{0} commits will not be in the rebased branch.",
                dropped);

        _summary.Text = _fromStorage
            ? summary + " " + T("git has already refused this list — see its message below. Fix it here and Apply.")
            : summary;
    }

    private void UpdateButtons()
    {
        int index = _list.SelectedIndex;
        bool hasSelection = !_busy && index >= 0 && index < _steps.Count;
        bool commitStep = hasSelection && _steps[index].IsCommitStep;

        foreach (Control child in _commandButtons.Children)
        {
            child.IsEnabled = commitStep;
        }

        _up.IsEnabled = hasSelection && index > 0;
        _down.IsEnabled = hasSelection && index < _steps.Count - 1;
        _remove.IsEnabled = hasSelection;
        _apply.IsEnabled = !_busy;
        _list.IsEnabled = !_busy;
    }

    // ---- helpers --------------------------------------------------------------------------

    // A yes/no modal, the same hand-built shape the rest of this port uses (Avalonia ships
    // no message box) — modelled on RepositoryProgressBanner.ConfirmAsync.
    private async Task<bool> ConfirmAsync(string message, string caption)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Confirm"), Margin = new Thickness(0, 0, Metrics.Space.Sm, 0) };
        Button no = new() { Content = T("FormCommit/Cancel.Text", "Cancel"), IsCancel = true };

        Theming.ZoomWindow dialog = new()
        {
            Title = caption,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.Black),
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Metrics.Space.Lg),
            Spacing = Metrics.Space.Md,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    Foreground = Brush("App.Text", Brushes.Gainsboro),
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };

        DialogKeys.InstallEscapeClose(dialog);
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private static Button MakeButton(string caption, string tooltip, Action onClick)
    {
        Button button = new()
        {
            Content = caption,
            Padding = new Thickness(Metrics.Space.Md, Metrics.Space.Xs),
            MinWidth = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        if (tooltip.Length > 0)
        {
            ToolTip.SetTip(button, tooltip);
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
