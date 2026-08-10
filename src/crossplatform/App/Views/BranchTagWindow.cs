using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The branch and tag workbench: a window around <see cref="BranchTagPanel"/>.
/// </summary>
/// <remarks>
///  <para><b>Why a window at all, when the port already has these commands.</b> Every
///  action this panel offers exists elsewhere — the left tree lists branches and tags,
///  Commands → New branch / New tag create them, and checkout, merge, rebase and delete
///  each have their own dialog. What does NOT exist anywhere is a place where they are
///  all in reach of the same selection. Renaming a release line today means: find the
///  branch in the tree, check it out from its context menu, open Commands to make the
///  tag, go back to the tree to delete the old branch — three surfaces and a lost
///  selection at every step. This window is the one surface that keeps the selected ref
///  under the cursor while a sequence of ref operations is carried out on it, which is
///  the only thing it adds and the only reason it earns a slot.</para>
///
///  <para><b>Why it is not the left tree instead.</b> The tree's job is navigation: it
///  answers "what refs are there and what is on this one". Hanging a row of action
///  buttons and a start-point box off it would make the permanently-visible panel of the
///  shell carry a mode it is in for a few seconds a week. A window the user opens for a
///  task and closes when it is done is the honest shape for a task-shaped tool, and it
///  is the shape upstream gives every other ref operation (FormCheckoutBranch,
///  FormMergeBranch, FormCreateTag are all dialogs).</para>
///
///  <para><b>Why it does not duplicate those dialogs.</b> It does not re-implement any of
///  them: <see cref="BranchTagPanel"/> OPENS them (CheckoutBranchDialog,
///  CreateBranchDialog, CreateTagDialog, MergeDialog) and keeps the ref lists and the
///  conflict follow-up around them. So the same code runs whichever route the user took,
///  and there is no second implementation to drift.</para>
///
///  <para><b>Modal</b>, like <see cref="StashWindow"/> and <see cref="ReflogWindow"/>,
///  the two other panel-in-a-window tools of the port. That is also what settles the
///  repository question: while this window is up the shell cannot be driven, so the open
///  repository cannot change under the panel. The host closes it anyway if a repository
///  change ever does slip through (see <c>MainWindow.LoadRepository</c>) — the panel
///  holds a path it was handed once, and a panel acting on a repository the user has
///  left is worse than a window that vanished.</para>
/// </remarks>
public sealed class BranchTagWindow : Theming.ZoomWindow
{
    private readonly BranchTagPanel _panel = new();

    /// <summary>
    ///  True once any ref operation in this window succeeded, so the owner knows it has
    ///  to refresh when the window closes.
    /// </summary>
    public bool Changed { get; private set; }

    /// <param name="repoPath">Repository whose branches and tags are listed.</param>
    public BranchTagWindow(string repoPath)
    {
        // No single upstream form covers "branches and tags together", so there is no
        // upstream id to borrow: the source text is the key, which the catalogues can
        // still pick up.
        Title = TranslationService.T("Branches and tags");
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        _panel.OperationCompleted += () => Changed = true;
        Content = _panel;

        DialogKeys.InstallEscapeClose(this);

        // After Content, as in StashWindow: the panel's first load posts back to the UI
        // thread and the lists it fills have to exist by then.
        _panel.LoadRepository(repoPath);
    }
}
