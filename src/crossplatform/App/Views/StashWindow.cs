using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The stash dialog: a window around <see cref="StashPanel"/>.
///
///  <para>Upstream has no stash tab — <c>FormBrowse</c>'s tab strip is Commit · Diff ·
///  File tree · GPG (plus the Console / Output / Blame / File history pages it adds at
///  run time), and every stash surface (the toolbar's stash split button, the Commands
///  menu, the left tree's "Open stash" and "Manage stashes…") goes to
///  <c>UICommands.StartStashDialog</c>, i.e. a modal <c>FormStash</c>. The port had put
///  the same panel in a ninth bottom tab, which left the stash list, its file lists and
///  its diff squeezed into the bottom strip and made the panel compete for the space the
///  commit detail wants.</para>
///
///  <para>The two <c>StartStashDialog</c> arguments come through as
///  <see cref="StashPanel.ManageStashes"/> and
///  <see cref="StashPanel.SelectStashOnLoad"/>, so opening a stash node lands on that
///  stash and "Manage stashes…" lands on the newest one, exactly as upstream.</para>
/// </summary>
public sealed class StashWindow : Theming.ZoomWindow
{
    private readonly StashPanel _panel = new();

    /// <summary>
    ///  True once any stash operation in this window succeeded, so the owner knows it
    ///  has to refresh when the window closes.
    /// </summary>
    public bool Changed { get; private set; }

    /// <param name="repoPath">Repository whose stashes are listed.</param>
    /// <param name="manageStashes">
    ///  Start on the newest stash rather than on the working-directory row — upstream's
    ///  <c>manageStashes</c> flag.
    /// </param>
    /// <param name="initialStash">Stash to select on load ("stash@{2}"), if any.</param>
    public StashWindow(string repoPath, bool manageStashes = true, string? initialStash = null)
    {
        Title = TranslationService.T("FormStash/$this.Text", "Stash");
        Width = 1000;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        _panel.ManageStashes = manageStashes;
        _panel.SelectStashOnLoad(initialStash);
        _panel.OperationCompleted += () => Changed = true;
        Content = _panel;

        DialogKeys.InstallEscapeClose(this);

        // After Content: the panel's first fill posts back to the UI thread, and the
        // list it fills has to exist by then.
        _panel.LoadRepository(repoPath);
    }

    /// <summary>Opens the "create a stash" prompt as soon as the window is up.</summary>
    public void BeginCreateStash() => _panel.BeginCreateStash();
}
