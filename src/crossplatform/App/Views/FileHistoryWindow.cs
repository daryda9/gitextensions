using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  One file's history, in a window of its own — the port of upstream's
///  <c>FormFileHistory</c>.
/// </summary>
/// <remarks>
///  <para><b>Why a window and not the tab it replaces.</b> Upstream has never shown file
///  history in the browse window's bottom strip: <c>StartFileHistoryDialog</c> opens a
///  separate top-level window (in fact a separate PROCESS, with a <c>filehistory</c>
///  command line — see <c>GitUICommands.cs:1278</c>), and everything the feature needs
///  is in it: the file's own revision grid AND, under it, the four viewers for the
///  selected revision. The port's bottom tab could only ever be the grid, because the
///  bottom strip's other tabs already belong to the repository's selection; a file's
///  diff and a file's blame had nowhere to go. This window is that missing half.</para>
///
///  <para><b>Structure</b>, one for one with <c>FormFileHistory.Designer.cs</c>: a
///  horizontal split with the file's revision grid on top and a four-tab control below —
///  <b>Commit</b> (<see cref="CommitDetailView"/>), <b>Diff</b> (<see cref="DiffView"/>
///  scoped to this file), <b>View</b> (<see cref="FileContentView"/>, the blob at that
///  revision) and <b>Blame</b> (<see cref="BlameView"/>). The grid itself, with its
///  follow-renames and full-history toggles, is the existing
///  <see cref="FileHistoryView"/> unchanged.</para>
///
///  <para><b>Lazy, like upstream.</b> <c>UpdateSelectedFileViewers</c> loads only the tab
///  that is visible and re-loads on both selection and tab change; so does this. A tab
///  that is already showing the right revision is left alone, which is what keeps
///  walking the grid with the keyboard cheap.</para>
///
///  <para><b>The historic name matters everywhere.</b> A file that was renamed has a
///  different path in older commits, and asking git for the blob under today's name
///  there returns nothing. Every viewer is therefore fed
///  <see cref="FileHistoryView.GetFileNameForRevision"/>, not the name the window was
///  opened with — the same rule as upstream's <c>GetFileNameForRevision</c>, and the
///  reason the title shows the historic name in brackets.</para>
///
///  <para><b>Not modal.</b> Upstream's is a separate process, so the browse window stays
///  usable; here the window is shown non-modally for the same reason, exactly as the
///  commit dialog's own file-history child window already is.</para>
/// </remarks>
public sealed class FileHistoryWindow : ZoomWindow
{
    private static IBrush B(string key, IBrush fallback)
        => Application.Current?.Resources.TryGetResource(key, null, out object? value) == true && value is IBrush brush
            ? brush
            : fallback;

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private readonly FileHistoryService _service = new();

    private readonly string _repoPath;
    private readonly string _filePath;

    private readonly FileHistoryView _history = new();
    private readonly CommitDetailView _detail = new();
    private readonly DiffView _diff = new();
    private readonly FileContentView _content = new();
    private readonly BlameView _blame = new();

    private readonly TabControl _tabs = new();
    private readonly TabItem _commitTab;
    private readonly TabItem _diffTab;
    private readonly TabItem _viewTab;
    private readonly TabItem _blameTab;

    /// <summary>The revision the tabs are showing, empty when nothing is selected.</summary>
    private string _hash = string.Empty;

    /// <summary>The name the file has in <see cref="_hash"/> (renames, see the remarks).</summary>
    private string _nameInRevision = string.Empty;

    /// <summary>
    ///  The older end of a multi-commit selection, empty when a single commit is
    ///  selected. The Diff tab then compares the two ends instead of showing one
    ///  commit; the other three tabs stay on <see cref="_hash"/>, the newer end,
    ///  because a blob and a blame belong to ONE revision.
    /// </summary>
    private string _rangeBase = string.Empty;

    /// <summary>
    ///  What each tab was last loaded with, so re-selecting a tab does not reload it and
    ///  walking the grid only pays for the tab on screen.
    /// </summary>
    private readonly Dictionary<TabItem, string> _loaded = [];

    /// <summary>
    ///  The file's own revision grid, exposed so the host can plant the same commit
    ///  commands and bisect gate it plants in the repository grid — the window itself
    ///  knows nothing about checkout, reset or bisect.
    /// </summary>
    public FileHistoryView History => _history;

    public FileHistoryWindow(string repoPath, string filePath, bool showBlame = false)
    {
        _repoPath = repoPath;
        _filePath = filePath;

        Width = 1100;
        Height = 700;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B("App.Window", Brushes.DimGray);
        SetTitle();

        _commitTab = Tab("CommitSummary", T("FormFileHistory/CommitInfoTabPage.Text", "Commit"), _detail);
        _diffTab = Tab("Diff", T("FormFileHistory/DiffTab.Text", "Diff"), _diff);
        _viewTab = Tab("ViewFile", T("FormFileHistory/ViewTab.Text", "View"), _content);
        _blameTab = Tab("Blame", T("FormFileHistory/BlameTab.Text", "Blame"), _blame);

        _tabs.Items.Add(_commitTab);
        _tabs.Items.Add(_diffTab);
        _tabs.Items.Add(_viewTab);
        _tabs.Items.Add(_blameTab);

        // Upstream opens on Blame when it was asked for one and on Diff otherwise
        // (FormFileHistory.cs:136). The port's Blame entry points go through the same
        // window, which is why the flag exists at all.
        _tabs.SelectedItem = showBlame ? _blameTab : _diffTab;
        _tabs.SelectionChanged += (_, _) => LoadSelectedTab();

        Grid split = new() { RowDefinitions = new RowDefinitions("2*,4,3*") };
        GridSplitter splitter = new()
        {
            Height = 4,
            ResizeDirection = GridResizeDirection.Rows,
            Background = Brushes.Transparent,
        };
        Grid.SetRow(_history, 0);
        Grid.SetRow(splitter, 1);
        Grid.SetRow(_tabs, 2);
        split.Children.Add(_history);
        split.Children.Add(splitter);
        split.Children.Add(_tabs);
        Content = split;

        DialogKeys.InstallEscapeClose(this);

        _history.RevisionSelected += OnRevisionSelected;
        _history.RangeSelected += OnRangeSelected;

        // Last, so the grid is only asked for the history once everything that reacts to
        // its first selection is wired.
        _history.ShowHistory(repoPath, filePath);
    }

    private static TabItem Tab(string icon, string caption, Control content)
        => new() { Header = IconText.Header(icon, caption), Content = content };

    /// <summary>
    ///  <c>File History - &lt;path&gt; [(&lt;name in the selected revision&gt;)] - &lt;repository&gt;</c>,
    ///  the shape upstream's <c>SetTitle</c> composes. The bracketed part appears only
    ///  when the file was known under another name in that revision, which is the
    ///  clearest place to tell the user a rename was crossed.
    /// </summary>
    private void SetTitle(string? nameInRevision = null)
    {
        string title = F(T("FormFileHistory/$this.Text", "File History - {0}"), _filePath);
        if (nameInRevision is { Length: > 0 } historic && !string.Equals(historic, _filePath, StringComparison.Ordinal))
        {
            title += $" ({historic})";
        }

        Title = $"{title} - {PathDisplay.CollapseHome(_repoPath)}";
    }

    /// <summary>
    ///  Two or more commits picked in the file's grid (Ctrl or Shift): the window
    ///  shows what happened to the file BETWEEN the ends of that selection. Upstream
    ///  answers a multi-selection the same way — it diffs against the first of the
    ///  selected revisions — and the port's repository grid already did, through
    ///  <c>MainWindow.OnRangeSelected</c>; only this window was left showing whichever
    ///  single commit happened to be the anchor of the selection.
    /// </summary>
    private void OnRangeSelected(string older, string newer)
    {
        _rangeBase = older ?? string.Empty;
        Select(newer);
    }

    private void OnRevisionSelected(string hash)
    {
        _rangeBase = string.Empty;
        Select(hash);
    }

    private void Select(string hash)
    {
        _hash = hash ?? string.Empty;
        _nameInRevision = _hash.Length > 0 ? _history.GetFileNameForRevision(_hash) : string.Empty;
        SetTitle(_nameInRevision);

        // A new revision invalidates every tab, not only the visible one: the others
        // reload when the user reaches them.
        _loaded.Clear();
        LoadSelectedTab();
    }

    /// <summary>
    ///  Loads the tab on screen for the current revision, and nothing else.
    ///
    ///  <para>The three file-scoped tabs need the file to EXIST in that revision; a
    ///  commit that only touched its neighbours after a rename, or the commit that
    ///  deleted it, has no blob to show. Upstream detaches those tabs and says so in the
    ///  Commit tab's caption; the port disables them instead — a tab that vanishes and
    ///  comes back moves the ones next to it under the pointer — and keeps the same
    ///  caption suffix, so the reason is still on screen.</para>
    /// </summary>
    private void LoadSelectedTab()
    {
        if (_tabs.SelectedItem is not TabItem tab || _hash.Length == 0)
        {
            return;
        }

        // What the tab is showing is identified by the COMPARISON, not by the
        // revision: switching a selection of two commits to just the newer one keeps
        // the same _hash and must still reload the Diff tab.
        string key = _rangeBase.Length > 0 ? $"{_rangeBase}..{_hash}" : _hash;
        if (_loaded.TryGetValue(tab, out string? shown) && string.Equals(shown, key, StringComparison.Ordinal))
        {
            return;
        }

        string hash = _hash;
        string name = _nameInRevision.Length > 0 ? _nameInRevision : _filePath;

        // The commit tab is the one that always works: it describes the commit, not the
        // file, so it is loaded before the existence question is even asked.
        if (ReferenceEquals(tab, _commitTab))
        {
            _detail.ShowCommit(_repoPath, hash);
            _loaded[tab] = key;
            return;
        }

        // git call: off the UI thread, and the answer is applied only if the user is
        // still on the same revision and tab by the time it comes back.
        Async.OffUi(
            () => _service.FileExistsInRevision(_repoPath, hash, name),
            exists =>
            {
                if (!string.Equals(_hash, hash, StringComparison.Ordinal) || !ReferenceEquals(_tabs.SelectedItem, tab))
                {
                    return;
                }

                ApplyFilePresence(exists, name);
                if (!exists)
                {
                    return;
                }

                if (ReferenceEquals(tab, _diffTab) && _rangeBase.Length > 0)
                {
                    // A range: the file's changes between the two ends, taken as a
                    // whole. The list is the range's whole file list, as it is for the
                    // repository grid, with this file selected.
                    _diff.ShowRange(_repoPath, _rangeBase, hash, name);
                }
                else if (ReferenceEquals(tab, _diffTab))
                {
                    // File-scoped, like upstream's Diff tab: the commit's whole file list
                    // is still there — this is the port's DiffView — but the row for THIS
                    // file is the one selected, so the pane opens on the change the
                    // window is about.
                    _diff.ShowCommit(_repoPath, hash, name);
                }
                else if (ReferenceEquals(tab, _viewTab))
                {
                    _content.ShowFile(_repoPath, name, hash);
                }
                else if (ReferenceEquals(tab, _blameTab))
                {
                    _blame.ShowBlame(_repoPath, name, hash);
                }

                _loaded[tab] = key;
            },
            "reading the file history");
    }

    /// <summary>
    ///  Says whether the file is in this revision: the three file tabs are enabled or
    ///  not, the Commit tab carries upstream's "could not identify the file" suffix, and
    ///  a disabled tab hands the selection to Commit so the window is never blank.
    /// </summary>
    private void ApplyFilePresence(bool exists, string name)
    {
        _diffTab.IsEnabled = exists;
        _viewTab.IsEnabled = exists;
        _blameTab.IsEnabled = exists;

        string caption = T("FormFileHistory/CommitInfoTabPage.Text", "Commit");
        if (!exists)
        {
            caption += F(T("FormFileHistory/_fileNotFound.Text", " - Git could not identify the file {0}"), $"\"{name}\"");
        }

        _commitTab.Header = IconText.Header("CommitSummary", caption);

        if (!exists)
        {
            _tabs.SelectedItem = _commitTab;
        }
    }
}
