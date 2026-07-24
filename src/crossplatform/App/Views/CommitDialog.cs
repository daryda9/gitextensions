using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal commit window mirroring the original Git Extensions dedicated commit
///  form. Rather than re-implementing the staged/unstaged/message/amend/commit
///  flow, it hosts a fresh <see cref="WorkingDirectoryView"/> (which already
///  implements all of it) as its content and forwards that view's
///  <see cref="WorkingDirectoryView.Committed"/> notification back to the owner.
///
///  <see cref="Committed"/> fires on both a commit and an undo-last-commit, so the
///  dialog deliberately does NOT auto-close; it simply re-raises the event so the
///  owner can refresh, and the user closes the window when finished.
/// </summary>
public sealed class CommitDialog : Window
{
    private readonly WorkingDirectoryView _workingDir = new();

    /// <summary>Re-raised whenever the hosted view reports a commit / undo.</summary>
    public event Action? Committed;

    public CommitDialog(string repoPath)
    {
        Title = "Commit";
        Width = 900;
        Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        _workingDir.LoadRepository(repoPath);
        _workingDir.Committed += () => Committed?.Invoke();

        Content = _workingDir;
    }

    /// <summary>
    ///  Constructs the dialog, forwards its <see cref="Committed"/> event to
    ///  <paramref name="onCommitted"/>, and shows it modally over
    ///  <paramref name="owner"/>.
    /// </summary>
    public static async Task ShowAsync(Window owner, string repoPath, Action onCommitted)
    {
        CommitDialog dialog = new(repoPath);
        dialog.Committed += onCommitted;
        await dialog.ShowDialog(owner);
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
