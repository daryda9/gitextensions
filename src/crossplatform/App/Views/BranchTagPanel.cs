using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Branch/tag operations panel: lists local + remote branches and tags (marking
///  the current branch) and offers checkout, create branch, create tag, merge,
///  rebase and delete. Names are entered inline. All git work runs off the UI
///  thread via <see cref="Task.Run"/> and posts results back with
///  <see cref="Dispatcher.UIThread"/>; buttons are disabled while busy and
///  operation output/errors are shown in a status line.
/// </summary>
public sealed class BranchTagPanel : UserControl
{
    private readonly BranchTagService _service = new();

    private readonly ListBox _branchList;
    private readonly ListBox _tagList;
    private readonly TextBox _refBox;
    private readonly CheckBox _forceDelete;
    private readonly Button _checkoutButton;
    private readonly Button _newBranchButton;
    private readonly Button _newTagButton;
    private readonly Button _mergeButton;
    private readonly Button _rebaseButton;
    private readonly Button _deleteButton;
    private readonly Button _refreshButton;
    private readonly TextBlock _status;
    private readonly TextBlock _branchesHeader;
    private readonly TextBlock _tagsHeader;

    private string? _repoPath;
    private bool _busy;

    // True while the status line still shows the "no repository" placeholder, the only
    // status ApplyTranslations may overwrite: every other one reports a git run that
    // already happened and must survive a language switch.
    private bool _statusIsPlaceholder = true;

    /// <summary>
    ///  Raised on the UI thread after any successful mutating operation
    ///  (create/delete branch or tag, checkout, merge, rebase), so the host can
    ///  refresh history and other views.
    /// </summary>
    public event Action? OperationCompleted;

    public BranchTagPanel()
    {
        _branchList = new ListBox { FontFamily = new FontFamily("monospace,Consolas,Menlo") };
        _tagList = new ListBox { FontFamily = new FontFamily("monospace,Consolas,Menlo") };
        _branchList.SelectionChanged += (_, _) => { if (_branchList.SelectedItem is not null) { _tagList.SelectedItem = null; } };
        _tagList.SelectionChanged += (_, _) => { if (_tagList.SelectedItem is not null) { _branchList.SelectedItem = null; } };
        _branchList.DoubleTapped += (_, _) => DoCheckout();

        Grid lists = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(8, 4, 8, 4),
        };
        (Control branchPanel, _branchesHeader) = MakeListPanel(_branchList);
        (Control tagPanel, _tagsHeader) = MakeListPanel(_tagList);
        Grid.SetColumn(branchPanel, 0);
        Grid.SetColumn(tagPanel, 1);
        lists.Children.Add(branchPanel);
        lists.Children.Add(tagPanel);

        // Name / message / checkout-after-create now live in the create dialogs
        // (CreateBranchDialog / CreateTagDialog); only the start point stays here.
        _refBox = new TextBox();
        _forceDelete = new CheckBox { Margin = new Thickness(0, 2, 0, 0) };

        StackPanel inputs = new() { Spacing = 4, Margin = new Thickness(8, 4, 8, 4) };
        inputs.Children.Add(_refBox);
        StackPanel checks = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
        checks.Children.Add(_forceDelete);
        inputs.Children.Add(checks);

        _checkoutButton = MakeButton(() => DoCheckout());
        _newBranchButton = MakeButton(() => DoCreateBranch());
        _newTagButton = MakeButton(() => DoCreateTag());
        _mergeButton = MakeButton(() => DoMerge());
        _rebaseButton = MakeButton(() => DoRebase());
        _deleteButton = MakeButton(() => _ = DoDeleteAsync());
        _refreshButton = MakeButton(() => RefreshRefs());

        WrapPanel buttons = new() { Margin = new Thickness(8, 0, 8, 4) };
        foreach (Button b in new[] { _checkoutButton, _newBranchButton, _newTagButton, _mergeButton, _rebaseButton, _deleteButton, _refreshButton })
        {
            buttons.Children.Add(b);
        }

        _status = new TextBlock
        {
            Margin = new Thickness(10, 2, 10, 6),
            Foreground = (IBrush)Application.Current!.Resources["App.TextDim"]!,
            TextWrapping = TextWrapping.Wrap,
        };

        StackPanel bottom = new();
        bottom.Children.Add(inputs);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        DockPanel root = new();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(lists);

        Content = root;

        ApplyTranslations();
    }

    // --- Translations -----------------------------------------------------

    // A panel has no Closed event, so the subscription is tied to the visual tree
    // instead: a panel torn out of it (a closed tab, a rebuilt layout) would otherwise
    // stay reachable from the static LanguageChanged event forever.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TranslationService.LanguageChanged += OnLanguageChanged;

        // The language may have changed while this panel was detached, in which case
        // no event reached it; re-stating the captions here is what makes the
        // unsubscribe above safe.
        ApplyTranslations();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TranslationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        // "(* = current)" is the legend of the marker the rows carry, so it travels
        // with the caption rather than being pasted on afterwards.
        _branchesHeader.Text = TF("{0} (* = current)", T("TranslatedStrings/_branchesText.Text", "Branches"));
        _tagsHeader.Text = T("TranslatedStrings/_tagsText.Text", "Tags");

        _refBox.Watermark = T("Start point / commit (default HEAD)");
        _forceDelete.Content = T("Force delete branch");

        _checkoutButton.Content = T("FormCheckoutBranch/Ok.Text", "Checkout");

        // The English here stays "New …", the port's own wording, but the ids are
        // upstream's "Create branch"/"Create tag": the two say the same thing, and
        // borrowing the id is what gives every catalogue a translation for free —
        // inventing an id, or leaving these on the source-text lookup, would leave
        // both buttons in English in every language.
        _newBranchButton.Content = T("TranslatedStrings/_buttonCreateBranch.Text", "New branch") + "…";
        _newTagButton.Content = T("FormCreateTag/Ok.Text", "New tag") + "…";
        _mergeButton.Content = T("FormMergeBranch/Ok.Text", "Merge");
        _rebaseButton.Content = T("FormRebase/btnRebase.Text", "Rebase");
        _deleteButton.Content = T("FormDeleteTag/Ok.Text", "Delete");
        _refreshButton.Content = T("FormBrowse/RefreshButton.ToolTipText", "Refresh");

        if (_statusIsPlaceholder)
        {
            _status.Text = T("No repository loaded.");
        }
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(key: null, englishFormat, args);

    // Every status but the "no repository" placeholder describes something that has
    // already happened, so it is written once and never re-stated.
    private void Status(string message)
    {
        _statusIsPlaceholder = false;
        _status.Text = message;
    }

    /// <summary>
    ///  Points the panel at <paramref name="repoPath"/> and loads its refs.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        RefreshRefs();
    }

    // Returns the header block as well: it carries a caption, so the panel has to keep
    // hold of it to re-label it.
    private static (Control Panel, TextBlock Header) MakeListPanel(ListBox list)
    {
        Grid grid = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(4, 4, 4, 4),
        };
        TextBlock title = new()
        {
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        };
        Grid.SetRow(title, 0);
        Grid.SetRow(list, 1);
        grid.Children.Add(title);
        grid.Children.Add(list);
        return (grid, title);
    }

    private static Button MakeButton(Action onClick)
    {
        Button b = new() { Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private BranchTagRow? SelectedRow()
        => _branchList.SelectedItem as BranchTagRow ?? _tagList.SelectedItem as BranchTagRow;

    private void RefreshRefs()
    {
        if (_repoPath is not { Length: > 0 } repo)
        {
            _status.Text = T("No repository loaded.");
            _statusIsPlaceholder = true;
            return;
        }

        Status(T("RevisionGridControl/_strLoading.Text", "Loading…"));
        RunGit(
            () => _service.LoadRefs(repo),
            listing =>
            {
                _branchList.ItemsSource = listing.Branches.ToList();
                _tagList.ItemsSource = listing.Tags.ToList();
                Status(TF("{0} branches, {1} tags.", listing.Branches.Count, listing.Tags.Count));
            });
    }

    private void DoCheckout() => _ = DoCheckoutAsync();

    // A clean working tree checks out straight away; a dirty one goes through
    // CheckoutBranchDialog first (don't change / merge / reset / stash).
    private async Task DoCheckoutAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (SelectedRow() is not { } row)
            {
                Status(T("Select a branch or tag to checkout."));
                return;
            }

            LocalChangesAction? action = await CheckoutBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, row.Name);

            if (action is not { } changesAction)
            {
                return;
            }

            Status(TF("Checking out {0}…", row.Name));

            // The checkout runs inside the process dialog (upstream's FormCheckoutBranch
            // goes through FormProcess), so RunMutation must NOT wrap it — that would
            // check out a second time. Busy state and refresh stay here.
            bool ok;
            SetBusy(true);
            try
            {
                ok = await RefProcessRunner.CheckoutAsync(
                    TopLevel.GetTopLevel(this) as Window, repo, row.Name, changesAction, service: _service);
            }
            finally
            {
                SetBusy(false);
            }

            // Reloaded on failure and on Abort too: an interrupted checkout can already
            // have moved HEAD, so the list has to show what the repository is now.
            Status(ok
                ? TF("Checked out {0}.", row.Name)
                : TF("Checkout of {0} did not complete.", row.Name));
            RefreshRefs();
            OperationCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            Status(TF("Failed: {0}", ex.Message));
        }
    }

    private void DoCreateBranch() => _ = DoCreateBranchAsync();

    // Routed through CreateBranchDialog (name + checkout-after-create) instead
    // of the inline name box, so the panel offers the same options as the tree's
    // "Create branch…". The start point still comes from the ref box.
    private async Task DoCreateBranchAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            string start = (_refBox.Text ?? string.Empty).Trim();
            CreateBranchRequest? request = await CreateBranchDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, start.Length > 0 ? start : "HEAD");

            if (request is not { } r)
            {
                return;
            }

            Status(TF("Creating branch {0}…", r.Name));

            // Created inside the process dialog, as upstream does through FormProcess
            // (FormCreateBranch.cs:163). No RunMutation wrapper: it would run git twice.
            bool ok;
            SetBusy(true);
            try
            {
                ok = await RefProcessRunner.CreateBranchAsync(
                    TopLevel.GetTopLevel(this) as Window,
                    repo,
                    r.Name,
                    start,
                    r.Checkout,
                    service: _service);
            }
            finally
            {
                SetBusy(false);
            }

            Status(ok
                ? TF("Created branch {0}.", r.Name)
                : TF("Creation of {0} did not complete.", r.Name));
            RefreshRefs();
            OperationCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            Status(TF("Failed: {0}", ex.Message));
        }
    }

    private void DoCreateTag() => _ = DoCreateTagAsync();

    // Routed through CreateTagDialog: kind (lightweight / annotated / signed),
    // force, and optional push to a remote.
    private async Task DoCreateTagAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            string commit = (_refBox.Text ?? string.Empty).Trim();
            CreateTagRequest? request = await CreateTagDialog.AskAsync(
                TopLevel.GetTopLevel(this) as Window, repo, commit.Length > 0 ? commit : "HEAD");

            if (request is not { } r)
            {
                return;
            }

            Status(TF("Creating tag {0}…", r.Name));
            RunMutation(() => _service.CreateTag(repo, r.Name, commit, r.Message, r.Operation, r.SignKeyId, r.Force, r.PushToRemote));
        }
        catch (Exception ex)
        {
            Status(TF("Failed: {0}", ex.Message));
        }
    }

    private void DoMerge() => _ = DoMergeAsync();

    // Opens the merge configuration dialog (port of FormMergeBranch) rather than
    // merging with hard-wired options. The dialog runs git itself through the process
    // dialog, so RunMutation must not wrap it or the merge would run twice.
    private async Task DoMergeAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (SelectedRow() is not { IsTag: false } row)
            {
                Status(T("Select a branch to merge."));
                return;
            }

            Window owner = (TopLevel.GetTopLevel(this) as Window)!;
            MergeDialogResult? result = await MergeDialog.ShowAsync(owner, repo, row.Name);

            if (result is null)
            {
                return;
            }

            Status(result.Success
                ? TF("Merged {0}.", result.Branch)
                : TF("Merge of {0} did not complete.", result.Branch));
            RefreshRefs();

            // Conflicts left by the merge: ask, as upstream does.
            await ConflictFlow.HandleAsync(owner, repo);
            RefreshRefs();
        }
        catch
        {
            // Never throw from an interaction handler.
        }
    }

    private void DoRebase() => _ = DoRebaseAsync();

    // Same shape as DoMergeAsync: the rebase runs off the UI thread and, once git
    // has stopped, the conflict question gets its chance — the banner can now
    // continue, skip or abort the session.
    private async Task DoRebaseAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo
                || TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            if (SelectedRow() is not { IsTag: false } row)
            {
                Status(T("Select a branch to rebase onto."));
                return;
            }

            Status(TF("Rebasing onto {0}…", row.Name));
            BranchTagResult result;
            try
            {
                result = await Task.Run(() => _service.RebaseOnto(repo, row.Name));
            }
            catch (Exception ex)
            {
                result = new BranchTagResult(false, ex.Message);
            }

            Status(result.Success
                ? TF("Rebased onto {0}.", row.Name)
                : TF("Rebase onto {0} did not complete.", row.Name));
            RefreshRefs();

            if (await ConflictFlow.HandleAsync(owner, repo) is { HadConflicts: true })
            {
                RefreshRefs();
            }
        }
        catch (Exception ex)
        {
            Status(TF("Failed: {0}", ex.Message));
        }
    }

    private async Task DoDeleteAsync()
    {
        try
        {
            if (_repoPath is not { Length: > 0 } repo)
            {
                return;
            }

            if (SelectedRow() is not { } row)
            {
                Status(T("Select a branch or tag to delete."));
                return;
            }

            if (row.IsCurrent)
            {
                Status(T("Cannot delete the current branch."));
                return;
            }

            // The wired branch path no longer passes through RunGit, which is where the
            // busy check used to live, so it is made here explicitly.
            if (_busy)
            {
                Status(T("Another Git operation is still running."));
                return;
            }

            // One whole sentence per kind: the noun is inflected differently in the
            // rest of the sentence in most languages, so a "{kind}" hole would give a
            // translator nothing to work with.
            bool confirmed = await ConfirmAsync(row.IsTag
                ? TF("Delete tag '{0}'?", row.Name)
                : TF("Delete branch '{0}'?", row.Name));
            if (!confirmed)
            {
                return;
            }

            Status(row.IsTag
                ? TF("Deleting tag {0}…", row.Name)
                : TF("Deleting branch {0}…", row.Name));

            if (row.IsTag)
            {
                RunMutation(() => _service.DeleteTag(repo, row.Name));
                return;
            }

            // A branch delete goes through the process dialog, like create branch and
            // checkout, so "the branch is not fully merged" is spelled out instead of
            // being squeezed into the status line. The checkbox stays the only source of
            // --force here: unlike the tree's menu, this panel offers it explicitly.
            // No RunMutation wrapper — it would delete twice.
            bool force = _forceDelete.IsChecked == true;
            bool ok;
            SetBusy(true);
            try
            {
                ok = await RefProcessRunner.DeleteBranchAsync(
                    TopLevel.GetTopLevel(this) as Window, repo, row.Name, force, _service);
            }
            finally
            {
                SetBusy(false);
            }

            Status(ok
                ? TF("Deleted branch {0}.", row.Name)
                : TF("Deletion of {0} did not complete.", row.Name));
            RefreshRefs();
            OperationCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            Status(TF("Failed: {0}", ex.Message));
        }
    }

    // Runs a mutating op off the UI thread; on success refreshes the ref lists
    // and raises OperationCompleted, otherwise shows the git output.
    private void RunMutation(Func<BranchTagResult> work)
    {
        RunGit(
            work,
            result =>
            {
                if (result.Success)
                {
                    // git's own output is program output, not a caption: appended raw.
                    Status(T("Done.") + " " + result.Output.Trim());
                    RefreshRefs();
                    OperationCompleted?.Invoke();
                }
                else
                {
                    Status(TF("Failed: {0}", result.Output.Trim()));
                }
            });
    }

    // Runs a git operation off the UI thread and marshals the result (or error)
    // back onto it, disabling the action buttons while busy.
    // The type parameter is TResult, not T: T is now the translation helper of this
    // class, and a generic parameter of that name would hide it inside this method.
    private void RunGit<TResult>(Func<TResult> work, Action<TResult> onResult)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _ = Task.Run(() =>
        {
            try
            {
                TResult result = work();
                Dispatcher.UIThread.Post(() =>
                {
                    SetBusy(false);
                    onResult(result);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    SetBusy(false);
                    Status(T("TranslatedStrings/_error.Text", "Error") + ": " + ex.Message);
                });
            }
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _checkoutButton.IsEnabled = !busy;
        _newBranchButton.IsEnabled = !busy;
        _newTagButton.IsEnabled = !busy;
        _mergeButton.IsEnabled = !busy;
        _rebaseButton.IsEnabled = !busy;
        _deleteButton.IsEnabled = !busy;
        _refreshButton.IsEnabled = !busy;
    }

    // Minimal modal yes/no confirmation. Falls back to allowing the action when
    // no owner window is available (e.g. headless).
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return true;
        }

        TaskCompletionSource<bool> tcs = new();

        // Built in the language in force when it opens and thrown away afterwards, so
        // there is nothing here to re-label on a language change.
        Button yes = new() { Content = T("FormDeleteTag/Ok.Text", "Delete"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Confirm"),
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
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

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
