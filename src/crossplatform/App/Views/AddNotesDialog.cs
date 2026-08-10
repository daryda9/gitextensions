using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A small modal editor for a commit's git note, opened from the commit-info
///  panel's "Add notes" command.
///
///  <para>Upstream runs <c>git notes edit</c>, which hands the commit over to
///  <c>core.editor</c>; the port has no editor to spawn, so the note is typed
///  here and written with <c>git notes add -f -F -</c>
///  (<see cref="CommitInfoExtrasService.SaveNotes"/>). Clearing the box removes
///  the note, which is what leaving an empty buffer in an editor does too.</para>
///
///  <para>The dialog itself does no git work: it only collects
///  <see cref="NoteText"/> and reports <see cref="Accepted"/>, so the caller can
///  keep the write on a background thread.</para>
/// </summary>
public sealed class AddNotesDialog : Theming.ZoomWindow
{
    private readonly TextBox _editor;

    /// <summary><see langword="true"/> when the user confirmed rather than cancelled.</summary>
    public bool Accepted { get; private set; }

    /// <summary>The text the user left in the editor.</summary>
    public string NoteText => _editor.Text ?? string.Empty;

    /// <param name="shortHash">The commit the note belongs to, for the caption.</param>
    /// <param name="existingNote">The note already attached to the commit, if any.</param>
    public AddNotesDialog(string shortHash, string existingNote)
    {
        Title = string.Format(
            T("{0} — {1}"),
            Strip(T("CommitInfo/addNoteToolStripMenuItem.Text", "Add &notes")),
            shortHash);
        Width = 620;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        TextBlock label = new()
        {
            Text = T("Note attached to this commit (git notes):"),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Theming.AppFonts.Monospace,
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Text = existingNote,
        };

        TextBlock hint = new()
        {
            Text = T("Leaving the note empty removes it."),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        Button ok = new()
        {
            Content = T("FormRevisionFilter/Ok.Text", "OK"),
            MinWidth = 90,
            IsDefault = true,
        };
        Button cancel = new()
        {
            Content = T("FormCommit/Cancel.Text", "Cancel"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        ok.Click += (_, _) =>
        {
            Accepted = true;
            Close();
        };
        cancel.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        DockPanel root = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(label, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(label);
        root.Children.Add(buttons);
        root.Children.Add(hint);
        root.Children.Add(_editor);

        Content = root;
        DialogKeys.InstallEscapeClose(this);

        Opened += (_, _) =>
        {
            _editor.Focus();
            _editor.CaretIndex = _editor.Text?.Length ?? 0;
        };
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string Strip(string caption) => RevisionFilterDialog.StripMnemonic(caption);

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
