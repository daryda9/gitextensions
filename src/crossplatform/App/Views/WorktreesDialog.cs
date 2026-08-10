using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Worktrees manager" for the Avalonia port: lists the repository's
///  linked worktrees (path, branch/HEAD, flags) and offers Add… (path + branch),
///  Remove (selected, confirmed), Prune and Close, each delegating to
///  <see cref="WorktreeService"/> (which shells out to <c>git worktree …</c>).
///  Command output/status is shown in a read-only pane, and the list re-reflects
///  the new state after every action. All git work runs off the UI thread via
///  <see cref="Task.Run"/> and marshals back with <see cref="Dispatcher.UIThread"/>.
///  <see cref="Changed"/> is set when any mutation succeeds so the caller can
///  refresh the repository tree after the dialog closes.
/// </summary>
public sealed class WorktreesDialog : Theming.ZoomWindow
{
    private readonly WorktreeService _service = new();
    private readonly string _repoPath;
    private readonly ListBox _list;
    private readonly Button _add;
    private readonly Button _remove;
    private readonly Button _prune;
    private readonly Button _close;
    private readonly TextBox _output;
    private readonly TextBlock _status;

    private bool _busy;

    /// <summary>True when at least one listed worktree is stale (prunable).</summary>
    private bool _anyPrunable;

    /// <summary>
    ///  True when at least one add/remove/prune succeeded, so the owner can
    ///  refresh its view once the dialog is dismissed.
    /// </summary>
    public bool Changed { get; private set; }

    /// <summary>
    ///  Set when the user accepted the offer to switch to a worktree just created here;
    ///  the host opens it after the dialog closes. Null means "stay where we are".
    ///  <para>A property and not a call into the host: this dialog knows nothing about
    ///  MainWindow, exactly as <see cref="Changed"/> only reports that a refresh is due.</para>
    /// </summary>
    public string? RepositoryToOpen { get; private set; }

    public WorktreesDialog(string repoPath)
    {
        _repoPath = repoPath;

        Width = 640;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Panel", Brushes.DimGray);

        _list = new ListBox
        {
            Background = Brush("App.PanelAlt", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();

        // Stale (prunable) worktrees are struck through and dimmed, so it is visible
        // at a glance why they cannot be removed and what Prune would clear out.
        _list.ItemTemplate = new FuncDataTemplate<WorktreeItem>((item, _) =>
        {
            TextBlock text = new()
            {
                Text = item?.ToString() ?? string.Empty,
                Foreground = item?.Row.IsPrunable == true
                    ? Brush("App.TextDim", Brushes.Gray)
                    : Brush("App.Text", Brushes.Gainsboro),
            };

            if (item?.Row.IsPrunable == true)
            {
                text.TextDecorations = TextDecorations.Strikethrough;
            }

            return text;
        });

        _add = MakeButton();
        _remove = MakeButton();
        _prune = MakeButton();
        _close = MakeButton();

        _add.Click += (_, _) => _ = DoAddAsync();
        _remove.Click += (_, _) => _ = DoRemoveAsync();
        _prune.Click += (_, _) => Run(
            T("TranslatedStrings/_pruneWorktrees.Text", "Prune"),
            () => _service.PruneWorktrees(_repoPath));
        _close.Click += (_, _) => Close();

        // Escape = Close (upstream's CancelButton). Bubbling, so inner popups keep
        // their own Escape; Close() does not touch <see cref="Changed"/>.
        KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,

            // MinWidth, not Width: translated captions are routinely longer than the
            // English ones ("Prune deleted worktrees" → "Prune dei worktree eliminati"),
            // and a hard width would clip them instead of growing this Auto column.
            MinWidth = 140,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(_add);
        buttons.Children.Add(_remove);
        buttons.Children.Add(_prune);
        buttons.Children.Add(new Border { Height = 8 });
        buttons.Children.Add(_close);

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_list, 0);
        Grid.SetColumn(buttons, 1);
        top.Children.Add(_list);
        top.Children.Add(buttons);

        _status = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gainsboro),
            Margin = new Thickness(0, 8, 0, 4),
            Text = string.Empty,
        };

        // TextBoxSurface: see OutputView — the Fluent per-state repaint beats the local
        // Background, so clicking this read-only log flipped its surface to pure
        // black (dark) / pure white (light).
        _output = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Height = 120,
                FontFamily = new FontFamily("monospace"),
                VerticalContentAlignment = VerticalAlignment.Top,
            },
            Brush("App.PanelAlt", Brushes.Black),
            Brush("App.Text", Brushes.Gainsboro));

        Grid body = new()
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
        };
        Grid.SetRow(top, 0);
        Grid.SetRow(_status, 1);
        Grid.SetRow(_output, 2);
        body.Children.Add(top);
        body.Children.Add(_status);
        body.Children.Add(_output);

        Content = body;
        DialogKeys.EnsureFocusRoute(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        Opened += (_, _) => ReloadList();
        UpdateButtons();
    }

    // --- Translations -----------------------------------------------------

    // Re-labelling the chrome is not enough: every row's caption is built by
    // WorktreeItem.ToString, which the ListBox only calls when the item collection
    // changes. ReloadList is the rebuild path, exactly as MainToolbar re-runs its own.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        ApplyTranslations();
        ReloadList();
    });

    private void ApplyTranslations()
    {
        Title = T("TranslatedStrings/_worktreesText.Text", "Worktrees");

        // Upstream's captions are longer sentences on wider buttons ("&Create...",
        // "&Delete selected", "&Prune deleted worktrees"); the ids are still the right
        // ones, because they are what a translator has already been asked to word for
        // exactly these four actions.
        _add.Content = T("FormManageWorktree/buttonCreateNewWorktree.Text", "Add…");
        _remove.Content = T("FormManageWorktree/buttonDeleteSelectedWorktree.Text", "Remove");
        _prune.Content = T("FormManageWorktree/buttonPruneWorktrees.Text", "Prune");
        _close.Content = T("TranslatedStrings/_closeText.Text", "Close");
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private WorktreeItem? Selected => _list.SelectedItem as WorktreeItem;

    /// <summary>
    ///  `git worktree remove` refuses several of the entries it lists, so Remove is
    ///  only offered where it can actually work. Mirrors the gating of the Windows
    ///  dialog (<c>FormManageWorktree.CanDeleteSelectedWorkspace</c>):
    ///  <list type="bullet">
    ///   <item>the MAIN worktree owns the repository — it can never be removed;</item>
    ///   <item>a bare worktree has no working tree to remove;</item>
    ///   <item>a stale (prunable) entry has no working directory left; it is cleared
    ///    with Prune, not Remove;</item>
    ///   <item>the worktree the app currently has OPEN cannot remove itself.</item>
    ///  </list>
    ///  Previously only bare entries were excluded, so Remove was offered on the
    ///  main and on the open worktree and git simply failed.
    /// </summary>
    private bool CanRemove(WorktreeItem? item)
        => item is not null
            && !item.Row.IsMain
            && !item.Row.IsBare
            && !item.Row.IsPrunable
            && !item.Row.IsSamePath(_repoPath);

    private void UpdateButtons()
    {
        _remove.IsEnabled = !_busy && CanRemove(Selected);

        // Nothing to prune → nothing for the button to do.
        _prune.IsEnabled = !_busy && _anyPrunable;
    }

    private void ReloadList()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        _ = Task.Run(() =>
        {
            IReadOnlyList<WorktreeRow> rows;
            try
            {
                rows = _service.ListWorktrees(_repoPath);
            }
            catch
            {
                rows = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                string? keep = Selected?.Row.Path;
                List<WorktreeItem> items = rows.Select(r => new WorktreeItem(r)).ToList();
                _anyPrunable = items.Exists(i => i.Row.IsPrunable);
                _list.ItemsSource = items;
                if (keep is not null)
                {
                    _list.SelectedItem = items.FirstOrDefault(i => i.Row.Path == keep);
                }

                UpdateButtons();
            });
        });
    }

    // --- Operations -------------------------------------------------------

    private async Task DoAddAsync()
    {
        string? path = await PromptAsync(
            T("FormCreateWorktree/lblNewWorktreeFolder.Text", "New worktree path:"), string.Empty);
        if (path is not { Length: > 0 } target)
        {
            return;
        }

        // Branch is optional: empty lets git create a branch named after the path
        // (normalised to git's ref rules by the service).
        string? branch = await PromptAsync(
            TranslationService.TFormat(
                null, "Branch/revision for '{0}' (blank = new branch):", target),
            string.Empty);
        Run(
            TranslationService.TFormat(null, "Add '{0}'", target),
            () => _service.AddWorktree(_repoPath, target, branch ?? string.Empty),
            onSuccess: () => _ = OfferToOpenAsync(target));
    }

    private async Task DoRemoveAsync()
    {
        if (Selected is not { } item || !CanRemove(item))
        {
            return;
        }

        // Upstream's own confirmation, placeholder included, so the question reads the
        // way the Windows dialog asks it.
        if (await ConfirmAsync(TranslationService.TFormat(
            "TranslatedStrings/_deleteWorktreeConfirmation.Text",
            "Remove worktree '{0}'?",
            item.Row.Path)))
        {
            Run(
                TranslationService.TFormat(null, "Remove '{0}'", item.Row.Path),
                () => _service.RemoveWorktree(_repoPath, item.Row.Path));
        }
    }

    // Offers to make the freshly created worktree the open repository, mirroring what
    // upstream added in 6c302d839 (and what the clone flow has always done). Accepting
    // closes this dialog: the host cannot switch the repository under an open modal.
    private async Task OfferToOpenAsync(string path)
    {
        if (await ConfirmAsync(TranslationService.TFormat(
            "TranslatedStrings/_switchWorktreeConfirmation.Text",
            "Switch to the new worktree '{0}'?",
            path)))
        {
            RepositoryToOpen = System.IO.Path.GetFullPath(path);
            Close();
        }
    }

    private void Run(string label, Func<WorktreeOpResult> work, Action? onSuccess = null)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _status.Text = TranslationService.TFormat(null, "{0}…", label);
        UpdateButtons();
        _ = Task.Run(() =>
        {
            WorktreeOpResult result;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                result = new WorktreeOpResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (result.Success)
                {
                    Changed = true;
                }

                _status.Text = result.Success
                    ? TranslationService.TFormat(null, "{0}: OK", label)
                    : TranslationService.TFormat(null, "{0}: failed", label);
                _output.Text = result.Output;
                ReloadList();

                if (result.Success)
                {
                    onSuccess?.Invoke();
                }
            });
        });
    }

    // --- Inline prompt / confirm (mirrors RepoObjectsTree helpers) --------

    private async Task<bool> ConfirmAsync(string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Confirm"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Confirm"),
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task<string?> PromptAsync(string message, string initial)
    {
        TaskCompletionSource<string?> tcs = new();

        TextBox input = new() { Text = initial };
        Button ok = new() { Content = T("TranslatedStrings/_okText.Text", "OK"), Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Worktree"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        ok.Click += (_, _) => { tcs.TrySetResult(input.Text?.Trim()); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                tcs.TrySetResult(input.Text?.Trim());
                dialog.Close();
                e.Handled = true;
            }
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // No caption here: ApplyTranslations owns every button label in this dialog.
    private static Button MakeButton()
        => new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    // Display wrapper: the ListBox renders ToString(), so surface path + state
    // while keeping the underlying row for actions.
    private sealed record WorktreeItem(WorktreeRow Row)
    {
        public override string ToString()
        {
            // "bare" and "detached" are left in git's own words: they are the names of
            // the states as git prints them (`git worktree list` writes "(bare)" and
            // "(detached HEAD)"), not prose the port invented, and the row shows them
            // next to a path and a SHA that are equally untranslatable.
            string state = Row.IsBare ? "bare"
                : Row.Branch.Length > 0 ? Row.Branch
                : Row.IsDetached ? $"detached @ {Row.Head}"
                : Row.Head;

            string label = state.Length > 0 ? $"{Row.Path}  [{state}]" : Row.Path;

            // Say why an entry is not actionable rather than leaving a silently
            // disabled Remove button.
            if (Row.IsPrunable)
            {
                label += "  " + TranslationService.T("(deleted — use Prune)");
            }
            else if (Row.IsMain)
            {
                label += "  " + TranslationService.T("(main)");
            }

            return label;
        }
    }
}
