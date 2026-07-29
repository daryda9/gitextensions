using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>What the user chose in <see cref="ResetChangesDialog"/>.</summary>
public enum ResetChangesAction
{
    /// <summary>Nothing must be touched. First value on purpose: it is what closing the window means.</summary>
    Cancel,

    /// <summary>Discard tracked changes, leave untracked files alone.</summary>
    Reset,

    /// <summary>Discard tracked changes AND delete untracked files/directories.</summary>
    ResetAndDelete,
}

/// <summary>
///  Port of upstream's <c>FormResetChanges</c>: the confirmation that stands between
///  the commit dialog's "Reset all changes" / "Reset unstaged changes" buttons and an
///  unrecoverable <c>git reset --hard</c> / <c>git checkout</c>.
///
///  <para>Its real job is the second question, the one the port used to skip entirely:
///  <b>what happens to untracked files</b>. A reset never removes them, so without
///  this checkbox "Reset all changes" silently leaves new files behind, and upstream's
///  own wording ("Also delete new files and/or directories") is the only place the
///  user is told. The checkbox follows upstream's tri-state enablement: it is forced
///  ON and disabled when there is nothing BUT new files (a reset alone would do
///  nothing), forced OFF and disabled when there are no new files at all, and only
///  offered when the selection mixes the two.</para>
///
///  <para>Deviation from upstream, deliberate: the counts of the files actually
///  involved are shown, because the port's caller knows them and "are you sure" is a
///  poor question when the user cannot see the size of the answer.</para>
///
///  <para>No git work happens here — the dialog only reports
///  <see cref="SelectedAction"/>, so the caller keeps every command on a background
///  thread.</para>
/// </summary>
public sealed class ResetChangesDialog : Window
{
    private readonly CheckBox _deleteNew;

    /// <summary>What the user chose. <see cref="ResetChangesAction.Cancel"/> unless Reset was pressed.</summary>
    public ResetChangesAction SelectedAction { get; private set; }

    /// <param name="trackedCount">Tracked (existing) files that would be reverted.</param>
    /// <param name="untrackedCount">Untracked (new) files that the clean would delete.</param>
    /// <param name="onlyWorkTree">
    ///  The caller is resetting the work tree only ("Reset unstaged changes"), which is
    ///  worth saying out loud: staged changes survive.
    /// </param>
    public ResetChangesDialog(int trackedCount, int untrackedCount, bool onlyWorkTree)
    {
        bool hasExistingFiles = trackedCount > 0;
        bool hasNewFiles = untrackedCount > 0;

        Title = T("FormResetChanges/$this.Text", "Reset changes");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        TextBlock message = new()
        {
            Text = T("FormResetChanges/txtMessage.Text", "Are you sure you want to reset your changes?"),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        };

        TextBlock scope = new()
        {
            Text = Describe(trackedCount, untrackedCount, onlyWorkTree),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _deleteNew = new CheckBox
        {
            Content = new TextBlock
            {
                // A string Content would eat the '&' mnemonic marker's neighbours as an
                // access key (HANDOFF §3), so the label goes in as a child.
                Text = StripMnemonic(T(
                    "FormResetChanges/cbDeleteNewFilesAndDirectories.Text",
                    "Also delete &new files and/or directories")),
                Foreground = Brush("App.Text", Brushes.Gainsboro),
                TextWrapping = TextWrapping.Wrap,
            },
            Margin = new Thickness(0, 14, 0, 0),
        };

        // Upstream's exact tri-state (FormResetChanges.cs:42-58).
        if (!hasExistingFiles)
        {
            _deleteNew.IsChecked = true;
            _deleteNew.IsEnabled = false;
        }
        else if (!hasNewFiles)
        {
            _deleteNew.IsChecked = false;
            _deleteNew.IsEnabled = false;
        }
        else
        {
            _deleteNew.IsEnabled = true;
        }

        TextBlock hint = new()
        {
            Text = T("FormResetChanges/lblDeleteHint.Text", "This will delete any uncommitted work."),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 4, 0, 0),
        };

        Button reset = new()
        {
            Content = new TextBlock { Text = StripMnemonic(T("FormResetChanges/btnReset.Text", "R&eset")) },
            MinWidth = 90,
            IsDefault = true,
        };
        Button cancel = new()
        {
            Content = new TextBlock { Text = StripMnemonic(T("FormResetChanges/btnCancel.Text", "&Cancel")) },
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        reset.Click += (_, _) =>
        {
            SelectedAction = _deleteNew.IsChecked == true
                ? ResetChangesAction.ResetAndDelete
                : ResetChangesAction.Reset;
            Close();
        };
        cancel.Click += (_, _) =>
        {
            SelectedAction = ResetChangesAction.Cancel;
            Close();
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);

        StackPanel root = new() { Margin = new Thickness(16) };
        root.Children.Add(message);
        root.Children.Add(scope);
        root.Children.Add(_deleteNew);
        root.Children.Add(hint);
        root.Children.Add(buttons);

        Content = root;
        DialogKeys.InstallEscapeClose(this);
        Opened += (_, _) => reset.Focus();
    }

    /// <summary>
    ///  Shows the dialog modally and returns the choice. Closing the window with Esc
    ///  or the X leaves <see cref="ResetChangesAction.Cancel"/>, so an accidental
    ///  dismissal can never destroy work.
    /// </summary>
    public static async Task<ResetChangesAction> ShowAsync(
        Window owner, int trackedCount, int untrackedCount, bool onlyWorkTree)
    {
        ResetChangesDialog dialog = new(trackedCount, untrackedCount, onlyWorkTree);
        await dialog.ShowDialog(owner);
        return dialog.SelectedAction;
    }

    // Says exactly what is at stake, so "are you sure" has a measurable answer.
    private static string Describe(int trackedCount, int untrackedCount, bool onlyWorkTree)
    {
        string what = onlyWorkTree
            ? T("Unstaged changes in {0} tracked file(s) will be reverted; staged changes are kept.")
            : T("Staged and unstaged changes in {0} tracked file(s) will be reverted.");

        string text = string.Format(what, trackedCount);
        if (untrackedCount > 0)
        {
            text += "\n" + string.Format(
                T("{0} untracked file(s) are also present."), untrackedCount);
        }

        return text;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string StripMnemonic(string caption) => RevisionFilterDialog.StripMnemonic(caption);

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
