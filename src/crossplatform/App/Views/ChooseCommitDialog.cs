using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Views;

/// <summary>The commit the user picked: the full hash, and the short form a field shows.</summary>
public sealed record ChosenCommit(string Hash, string ShortHash);

/// <summary>
///  What a caller wants the picker to show. Everything is optional except the
///  repository: a caller that needs nothing but "any commit of this repository" passes
///  the defaults.
/// </summary>
/// <param name="Title">Window title — say what the commit is being picked FOR.</param>
/// <param name="Intro">
///  One line above the grid, in the caller's words. This is where a field's own
///  semantics belong: the rebase's "From" is exclusive, and a picker that does not say
///  so invites off-by-one commits.
/// </param>
/// <param name="Preselect">
///  Commit-ish to select when the list arrives — usually whatever the field already
///  holds, so reopening the picker starts where the user left it. Anything git can
///  resolve; a value it cannot is ignored rather than reported, since the field is free
///  text and the picker's job is to replace it.
/// </param>
/// <param name="CurrentBranchOnly">
///  Walk HEAD only instead of every ref (upstream's <c>showCurrentBranchOnly</c>).
/// </param>
/// <param name="ExcludeAncestorsOf">
///  Commit-ish whose common ancestor with HEAD ends the list: the walk becomes
///  <c>&lt;merge-base&gt;..HEAD</c>, so only the commits that are ON this branch and not
///  on the other one are offered — upstream's <c>lastRevisionToDisplayHash</c>, computed
///  the same way (<c>FormRebase.cs:415-427</c>). A value git cannot resolve, or two
///  histories with no common ancestor, simply leave the list unbounded.
/// </param>
public sealed record ChooseCommitRequest(
    string Title,
    string Intro,
    string? Preselect = null,
    bool CurrentBranchOnly = false,
    string? ExcludeAncestorsOf = null);

/// <summary>
///  Picks one commit from the REAL revision grid — the port of upstream's
///  <c>FormChooseCommit</c> (<c>src/app/GitUI/HelperDialogs/FormChooseCommit.cs</c>),
///  whose absence was the reason the rebase dialog's "From" field was a bare text box
///  (stated in <see cref="RebaseDialog"/> since M69).
///
///  <para><b>A third grid instance, for the same reason the file history has a second
///  one</b> (see <see cref="FileHistoryView"/>): the shell's grid carries the user's
///  place in the repository — selection, scroll, paged-in depth, quick filter, branch
///  scope, the artificial rows — and narrowing it to a branch for the duration of a
///  modal would hijack the window behind the modal and be persisted as a preference.
///  Upstream has to save and restore four global settings around this dialog
///  (<c>FormRebase.cs:404-441</c>) precisely because its picker shares that state; a
///  fresh instance needs none of that.</para>
///
///  <para>The picker therefore gets the graph, the ref decorations, the columns, the row
///  context menu, quick search and the navigation hotkeys for free. What it deliberately
///  does NOT reproduce from upstream's form are the two parent links under the grid: the
///  grid's own <c>Ctrl+Shift+P</c> / Navigate → parent already walks there, and a second
///  way to do it would have to be kept in step with the selection by hand.</para>
///
///  <para>Threading: the two git questions this dialog needs answered — resolving the
///  preselected commit-ish and the merge base that bounds the walk — are asked in
///  <see cref="ShowAsync"/> on a background thread, because the git services block and
///  deadlock when called from the UI thread. The grid does its own loading
///  asynchronously from there.</para>
/// </summary>
public sealed class ChooseCommitDialog : Theming.ZoomWindow
{
    private readonly RevisionGridView _grid = new();
    private readonly TextBlock _selection;
    private readonly Button _ok;

    /// <summary>The commit the user accepted, or <see langword="null"/> if they cancelled.</summary>
    public ChosenCommit? Chosen { get; private set; }

    /// <summary>
    ///  Opens the picker over <paramref name="owner"/> and returns the commit the user
    ///  accepted, or <see langword="null"/> for a cancel.
    /// </summary>
    public static async Task<ChosenCommit?> ShowAsync(Window owner, string repoPath, ChooseCommitRequest request)
    {
        // Both answers come from git and are wanted before the window exists: the
        // preselection has to be handed to the grid with its first load, and the bound
        // decides which walk that load runs.
        (string? preselect, string? stopAt) = await Task.Run(() => Resolve(repoPath, request));

        ChooseCommitDialog dialog = new(repoPath, request, preselect, stopAt);
        await dialog.ShowDialog(owner);
        return dialog.Chosen;
    }

    private static (string? Preselect, string? StopAt) Resolve(string repoPath, ChooseCommitRequest request)
    {
        string? preselect = null;
        string? stopAt = null;

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);

            if (!string.IsNullOrWhiteSpace(request.Preselect))
            {
                ObjectId id = module.RevParse(request.Preselect.Trim());
                if (!id.IsZero)
                {
                    preselect = id.ToString();
                }
            }

            if (!string.IsNullOrWhiteSpace(request.ExcludeAncestorsOf))
            {
                ObjectId other = module.RevParse(request.ExcludeAncestorsOf.Trim());
                ObjectId head = module.RevParse("HEAD");
                if (!other.IsZero && !head.IsZero)
                {
                    ObjectId mergeBase = module.GetMergeBase(other, head);
                    if (!mergeBase.IsZero)
                    {
                        stopAt = mergeBase.ToString();
                    }
                }
            }
        }
        catch (Exception)
        {
            // Both are conveniences: an unresolvable preselection means the list opens at
            // the top, and a merge base git will not compute (unrelated histories, a
            // branch name the user is still typing) means the list is simply not bounded.
            // Neither is worth refusing to open the picker for.
        }

        return (preselect, stopAt);
    }

    private ChooseCommitDialog(string repoPath, ChooseCommitRequest request, string? preselect, string? stopAt)
    {
        Title = request.Title;

        // Sized like the window the commits normally live in, because that is what the
        // list needs to be usable: the subject column is the one being read, and the
        // graph on its left only means anything with several rows of context visible.
        Width = 980;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        TextBlock intro = new()
        {
            Text = request.Intro,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _selection = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gray),
            FontFamily = Theming.AppFonts.Monospace,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _ok = new Button
        {
            Content = T("FormRevisionFilter/Ok.Text", "OK"),
            MinWidth = 90,
            IsDefault = true,
            IsEnabled = false,
        };
        Button cancel = new()
        {
            Content = T("FormCommit/Cancel.Text", "Cancel"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        _ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _ok, cancel },
        };

        Grid footer = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 10, 0, 0),
        };
        Grid.SetColumn(_selection, 0);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(_selection);
        footer.Children.Add(buttons);

        // A double click (and Enter on the grid) is how a list like this is used; upstream
        // wires the same gesture to OK (FormChooseCommit.revisionGrid_DoubleClickRevision).
        _grid.RevisionActivated += hash =>
        {
            Show(hash);
            Accept();
        };
        _grid.RevisionSelected += Show;
        _grid.SelectionCleared += () => Show(null);

        // A range of rows, or one of the artificial rows, is not an answer to "which
        // commit": OK stays off rather than silently taking one end of the selection.
        _grid.RangeSelected += _ => Show(null);
        _grid.WorkingDirectorySelected += () => Show(null);
        _grid.CommitIndexSelected += () => Show(null);

        if (request.CurrentBranchOnly)
        {
            _grid.SetBranchScope(BranchScope.CurrentBranch);
        }

        // Ends the walk at the merge base: `git log HEAD ^<base>`. A bound of the grid's
        // own (see RevisionGridView.SetWalkBound) rather than a ref in the filtered set,
        // which is what it looks like from the outside — a fake `^<hash>` ref survives the
        // first walk and is then dropped when the ref catalogue arrives, so the second
        // walk quietly shows the whole branch again. Measured, on this dialog.
        _grid.SetWalkBound(stopAt);

        DockPanel root = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(intro);
        root.Children.Add(footer);
        root.Children.Add(_grid);

        Content = root;
        DialogKeys.InstallEscapeClose(this);

        // The footer says its piece before the first click too: an OK the user cannot
        // press needs the line that explains why.
        Show(null);

        Opened += (_, _) =>
        {
            _grid.SelectCommitWhenLoaded(preselect);
            _grid.LoadRepository(repoPath);
            Dispatcher.UIThread.Post(() => _grid.Focus());
        };
    }

    // The footer says which commit OK would return, in the short form the field will
    // carry — so the answer is visible before it is given, and a selection lost to a
    // filter or a reload is visible as such.
    private void Show(string? hash)
    {
        // The two artificial rows carry hashes of the right shape (all 1s, all 2s) and
        // are not commits: a field that received one would send git a revision it cannot
        // resolve. They are normally not even in this grid — nothing calls
        // SetWorkingState on it — which is exactly why the guard has to be here rather
        // than in the caller.
        if (string.IsNullOrEmpty(hash)
            || hash.Length < 40
            || hash == RevisionGridView.WorkTreeHash
            || hash == RevisionGridView.IndexHash)
        {
            Chosen = null;
            _ok.IsEnabled = false;
            _selection.Text = T("No commit selected.");
            return;
        }

        Chosen = new ChosenCommit(hash, hash[..8]);
        _ok.IsEnabled = true;
        _selection.Text = string.Format(T("Selected: {0}"), Chosen.ShortHash);
    }

    private void Accept()
    {
        if (Chosen is not null)
        {
            Close();
        }
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
