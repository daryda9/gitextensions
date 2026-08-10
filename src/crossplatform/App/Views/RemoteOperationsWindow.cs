using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Remote operations: a window around <see cref="RemotePanel"/>.
/// </summary>
/// <remarks>
///  <para><b>Its slot is next to <see cref="RemotesDialog"/>, and the pair is the
///  point.</b> RemotesDialog is CONFIGURATION — which remotes exist, their fetch and
///  push URLs, which branch pulls from where — and it runs no transfer at all. This
///  window is the other half: it never edits configuration, it only ACTS on a remote and
///  shows what git said while doing it. Putting them in the same block of the Repository
///  menu is what makes each one's job legible; either one alone reads as "the remotes
///  thing" and gets blamed for not doing the other half.</para>
///
///  <para><b>Why it does not duplicate PullDialog / PushDialog.</b> Those are the
///  upstream forms: they compose ONE command out of many options (which remote branch,
///  merge or rebase, tags, recursive submodules, upstream tracking) and then hand it to
///  the process dialog, which closes when the process ends. This panel is the opposite
///  trade — no options to speak of, one remote selected, and a transcript pane that
///  STAYS on screen across several operations. That is what you want when the answer is
///  not "run my configured pull" but "is this remote reachable and what does it say":
///  fetch, read, push, read, retry after entering credentials — all without the dialog
///  closing under you between steps. The Commands menu keeps Fetch / Pull… / Push… as
///  they are; nothing is moved or shadowed.</para>
///
///  <para><b>Modal</b>, like <see cref="StashWindow"/> and <see cref="ReflogWindow"/>.
///  It also settles the repository question: the shell cannot be driven while this is
///  up, so the open repository cannot change under the panel; and the host closes the
///  window if a change ever does slip through (see <c>MainWindow.LoadRepository</c>),
///  because the panel keeps the path it was handed once and must not go on fetching into
///  a repository the user has left.</para>
/// </remarks>
public sealed class RemoteOperationsWindow : Theming.ZoomWindow
{
    private readonly RemotePanel _panel = new();

    /// <summary>
    ///  True once any remote operation in this window succeeded, so the owner knows it
    ///  has to refresh when the window closes.
    /// </summary>
    public bool Changed { get; private set; }

    /// <param name="repoPath">Repository whose remotes are listed.</param>
    public RemoteOperationsWindow(string repoPath)
    {
        // "Remote operations" is this window's own name — upstream has no form that does
        // this, so there is no id to borrow and the source text is the key.
        Title = TranslationService.T("Remote operations");
        Width = 780;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        _panel.OperationCompleted += () => Changed = true;
        Content = _panel;

        DialogKeys.InstallEscapeClose(this);

        // After Content: the panel's first load posts back to the UI thread and the list
        // it fills has to exist by then.
        _panel.LoadRepository(repoPath);
    }
}
