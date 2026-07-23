using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A commit-list view (revision grid) for the Avalonia/Linux port. Loads the
///  recent history of a repository off the UI thread and renders it as a
///  multi-column list (DAG graph / Hash / Author / Date / Subject, with ref
///  names shown inline). Uses a <see cref="ListBox"/> with a templated
///  multi-column row so no extra NuGet package (e.g. DataGrid) or theme
///  registration is required.
///
///  <para>The left-most column draws the commit DAG (colored lane lines + a
///  node dot per row, with branch/merge edges between adjacent rows), using the
///  lane layout computed by <see cref="RevisionService"/>.</para>
/// </summary>
public sealed class RevisionGridView : UserControl
{
    // Shared column widths so the header and every row line up.
    private const double HashWidth = 90;
    private const double AuthorWidth = 170;
    private const double DateWidth = 130;

    // Graph rendering metrics.
    private const double LaneWidth = 14;

    // Row metrics — kept tight for a dense, GitExtensions-like log.
    private const double RowFontSize = 12;

    private readonly RevisionService _service = new();
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly ContentControl _headerHost;
    private readonly TextBox _search;

    // "Go to ▾" bar button (holds the navigation flyout) and its hash entry box,
    // kept as fields so a keyboard shortcut (Ctrl+G) can open + focus them.
    private readonly Button _goToButton;
    private readonly TextBox _goToBox;

    // The full, graph-built revision set as loaded from git; filtering selects a
    // subset from this without re-running git or touching the underlying model.
    private IReadOnlyList<RevisionRow> _allRows = [];

    // The rows currently displayed, kept so BuildRow can compute a row's index
    // (for the subtle alternating-row background).
    private IReadOnlyList<RevisionRow> _rows = [];

    // True while a non-empty filter is applied. The DAG graph is drawn from
    // segments precomputed against ADJACENT rows in the full list, so showing an
    // arbitrary subset would leave lane lines/edges pointing at hidden neighbours
    // (a garbled graph). While filtering we therefore collapse the graph column
    // to zero width and skip drawing it, restoring it in full when the filter is
    // cleared. The underlying model (_allRows) is never mutated.
    private bool _filterActive;

    // Path of the loaded repository, for the status line.
    private string _repoLabel = string.Empty;

    // --- "View" options, matching the original Git Extensions revision grid. ---

    // Which timestamp the Date column shows, and whether it is rendered relative
    // ("3 days ago") or absolute ("yyyy-MM-dd HH:mm"). Applied live via RefreshView.
    private enum DateSource { Commit, Author }

    private DateSource _dateSource = DateSource.Commit;
    private bool _relativeDates;

    // Which refs the log walks (All branches / current branch only / filtered).
    // Session-local; changing it re-runs the log via the existing load path.
    private BranchScope _branchScope = BranchScope.AllBranches;

    // Path of the repository last asked to load, so a scope change can re-run the
    // log without the caller re-supplying it (LoadRepository stores it here).
    private string _repoPath = string.Empty;

    // Column visibility toggles (the graph + Subject columns always stay).
    private bool _showHash = true;
    private bool _showAuthor = true;
    private bool _showDate = true;

    // "View" toggles from the original grid. The first four change WHICH commits
    // the walk includes (or the walk order) and therefore reload via the existing
    // load path; the last two are render-time styles applied by RefreshView().
    private bool _showRemotes = true;   // include refs/remotes in the walk
    private bool _showTags = true;      // include refs/tags in the walk
    private bool _showStashes;          // include stash commits in the walk
    private bool _topoOrder;            // --topo-order vs default date order
    private bool _drawNonRelativesGray; // dim rows not reachable from/to HEAD
    private bool _highlightCurrentBranch; // emphasise the current branch's first-parent line

    // Reachability sets computed from the loaded rows whenever _allRows changes,
    // keyed by full hash. Ancestors ∪ descendants ∪ HEAD are the "relatives" of
    // the current branch; _currentBranchLine is HEAD's first-parent chain.
    private HashSet<string> _headRelatives = [];
    private HashSet<string> _currentBranchLine = [];

    // Palette pulled from the shared app resources (see App.cs).
    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Width of the graph column when NOT filtering; updated to fit the loaded
    // graph's lane count. While a filter is active the effective width is 0.
    private double _graphWidth = LaneWidth;

    // The column width actually used by the header/rows right now (0 while filtering).
    private double EffectiveGraphWidth => _filterActive ? 0 : _graphWidth;

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    // Host-registered commit-targeted actions (checkout, cherry-pick, reset, …),
    // appended to each row's context menu. Each handler receives the full hash.
    private readonly List<(string Header, Action<string> Handler)> _commitCommands = [];

    /// <summary>
    ///  Registers an extra context-menu command shown on each commit row; the
    ///  handler is invoked with the row's full commit hash.
    /// </summary>
    public void AddCommitCommand(string header, Action<string> handler)
        => _commitCommands.Add((header, handler));

    public RevisionGridView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(10, 6, 10, 6),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            Background = B("App.Toolbar"),
            Padding = new Thickness(0, 2, 0, 2),
            Text = "No repository loaded.",
        };

        _headerHost = new ContentControl { Content = BuildHeader() };

        _search = new TextBox
        {
            Watermark = "Filter: author / message / hash",
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // A small inline "clear" affordance, shown only when the box has text.
        Button clearButton = new()
        {
            Content = "✕",
            Foreground = B("App.TextDim"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 4, 0),
            FontSize = 12,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        clearButton.Click += (_, _) =>
        {
            _search.Text = string.Empty;
            _search.Focus();
        };
        _search.InnerRightContent = clearButton;

        // Live, in-memory filtering as the user types (no git re-run per keystroke).
        _search.TextChanged += (_, _) =>
        {
            clearButton.IsVisible = !string.IsNullOrEmpty(_search.Text);
            ApplyFilter(_search.Text);
        };

        // Esc clears the filter (and keeps focus in the box).
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _search.Text = string.Empty;
                e.Handled = true;
            }
        };

        // Compact "View" controls sitting to the right of the filter box: a Date
        // menu (author/commit + relative/absolute) and a Columns menu (show/hide
        // Author, Date, Commit-ID). Both apply live via RefreshView().
        Button dateButton = MakeBarButton("Date ▾");
        dateButton.Flyout = BuildDateFlyout();

        Button columnsButton = MakeBarButton("Columns ▾");
        columnsButton.Flyout = BuildColumnsFlyout();

        // Compact commit-navigation control: first-parent / child jumps plus a
        // "go to commit" hash box. Also reachable via keyboard (Alt+↑ / Alt+↓ / Ctrl+G).
        _goToBox = new TextBox
        {
            Watermark = "hash (full or short)",
            Background = B("App.Window"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            MinWidth = 150,
            Padding = new Thickness(6, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _goToButton = MakeBarButton("Go to ▾");
        _goToButton.Flyout = BuildGoToFlyout();

        // Branch-scope control: All branches / Current branch only / Filtered.
        // Switching re-runs the log through the existing load path (Reload).
        Button branchesButton = MakeBarButton("Branches ▾");
        branchesButton.Flyout = BuildBranchesFlyout();

        // "View" control: remote/tag/stash inclusion, walk order, and the two
        // render-time highlight styles. Walk-affecting toggles reload; render-time
        // ones re-template via RefreshView().
        Button viewButton = MakeBarButton("View ▾");
        viewButton.Flyout = BuildViewFlyout();

        DockPanel bar = new();
        DockPanel.SetDock(dateButton, Dock.Right);
        DockPanel.SetDock(columnsButton, Dock.Right);
        DockPanel.SetDock(viewButton, Dock.Right);
        DockPanel.SetDock(branchesButton, Dock.Right);
        DockPanel.SetDock(_goToButton, Dock.Right);
        bar.Children.Add(columnsButton);
        bar.Children.Add(dateButton);
        bar.Children.Add(viewButton);
        bar.Children.Add(branchesButton);
        bar.Children.Add(_goToButton);
        bar.Children.Add(_search); // fills the remaining space

        Border searchBar = new()
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6, 10, 6),
            Child = bar,
        };

        _list = new ListBox
        {
            Background = B("App.Window"),
            Foreground = B("App.Text"),
            FontSize = RowFontSize,
            BorderThickness = new Thickness(0),
            ClipToBounds = true,
            ItemTemplate = new FuncDataTemplate<RevisionRow>((row, _) => BuildRow(row), supportsRecycling: true),
        };

        // Dense rows, transparent containers, and an App.Selection highlight for
        // the selected/hovered row (styling the Fluent ListBoxItem template).
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            },
        });
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":pointerover")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.PanelAlt")) },
        });
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.Selection")) },
        });

        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is RevisionRow row)
            {
                RevisionSelected?.Invoke(row.Hash);
            }
        };

        // Keyboard: Ctrl+C copies the selected commit's hash; Alt+↑ jumps to the
        // first parent, Alt+↓ to the nearest child, Ctrl+G opens the "Go to" box.
        // (Plain Up/Down selection is handled by the ListBox and fires
        // RevisionSelected via SelectionChanged above.)
        _list.KeyDown += (_, e) =>
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            if (ctrl && e.Key == Key.C && _list.SelectedItem is RevisionRow row)
            {
                Copy(row.Hash);
                e.Handled = true;
            }
            else if (alt && e.Key == Key.Up)
            {
                GoToParent();
                e.Handled = true;
            }
            else if (alt && e.Key == Key.Down)
            {
                GoToChild();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.G)
            {
                OpenGoTo();
                e.Handled = true;
            }
        };

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(searchBar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_headerHost, Dock.Top);
        root.Children.Add(searchBar);
        root.Children.Add(_status);
        root.Children.Add(_headerHost);
        root.Children.Add(_list);

        Content = root;
    }

    /// <summary>
    ///  Loads and displays the recent revisions of the repository at
    ///  <paramref name="repoPath"/>. Heavy git work runs off the UI thread.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        _repoPath = repoPath;
        Reload();
    }

    /// <summary>
    ///  (Re-)runs the git log for the stored repository under the current branch
    ///  scope, off the UI thread. Used both for the initial load and whenever the
    ///  branch-scope toggle changes. All view state (text filter, git-notes, date
    ///  mode, column show/hide) is preserved: the DAG graph is rebuilt by the
    ///  service, and the current filter text is re-applied on completion.
    /// </summary>
    private void Reload()
    {
        if (string.IsNullOrEmpty(_repoPath))
        {
            return;
        }

        string repoPath = _repoPath;
        BranchScope scope = _branchScope;
        bool showRemotes = _showRemotes;
        bool showTags = _showTags;
        bool showStashes = _showStashes;
        bool topoOrder = _topoOrder;

        _list.ItemsSource = null;
        _status.Text = "Loading…";

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<RevisionRow> rows = _service.LoadRevisions(
                    repoPath,
                    scope: scope,
                    showRemotes: showRemotes,
                    showTags: showTags,
                    showStashes: showStashes,
                    topoOrder: topoOrder);
                Dispatcher.UIThread.Post(() =>
                {
                    int laneCount = rows.Count > 0 ? rows[0].LaneCount : 1;
                    _graphWidth = Math.Max(1, laneCount) * LaneWidth;
                    _allRows = rows;
                    _repoLabel = repoPath;
                    // Recompute HEAD reachability for the relatives/highlight styles.
                    ComputeReachability();
                    // Re-apply any current filter text so a reload keeps the view consistent.
                    ApplyFilter(_search.Text);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = "Error: " + ex.Message);
            }
        });
    }

    // Human label for the current branch scope, shown in the status line so the
    // effect of the toggle (and the resulting commit count) is visible.
    private string ScopeLabel => _branchScope switch
    {
        BranchScope.AllBranches => "all branches",
        BranchScope.CurrentBranch => "current branch",
        BranchScope.Filtered => "filtered (current branch)",
        _ => "all branches",
    };

    /// <summary>
    ///  Applies a case-insensitive substring filter over the already-loaded
    ///  revisions (author name, commit subject, and full/abbreviated hash).
    ///  Empty text shows everything. Runs purely in memory — no git per keystroke.
    /// </summary>
    private void ApplyFilter(string? text)
    {
        string query = (text ?? string.Empty).Trim();
        bool wasFiltering = _filterActive;
        _filterActive = query.Length > 0;

        IReadOnlyList<RevisionRow> filtered;
        if (!_filterActive)
        {
            filtered = _allRows;
        }
        else
        {
            List<RevisionRow> matches = [];
            foreach (RevisionRow row in _allRows)
            {
                if (Matches(row, query))
                {
                    matches.Add(row);
                }
            }

            filtered = matches;
        }

        _rows = filtered;

        // The graph column width changes with the filter state; rebuild the
        // header so its columns stay aligned with the (re-templated) rows.
        _headerHost.Content = BuildHeader();

        // Reassign the source so every visible row is rebuilt against the current
        // filter/graph state (and stale selection is dropped).
        _list.ItemsSource = null;
        _list.ItemsSource = filtered;

        if (_filterActive)
        {
            _status.Text = $"{_repoLabel}  —  {filtered.Count} of {_allRows.Count} commits  ({ScopeLabel}; filter: \"{query}\")";
        }
        else
        {
            _status.Text = _allRows.Count == 0
                ? "No repository loaded."
                : $"{_repoLabel}  —  {_allRows.Count} commits  ({ScopeLabel})";
        }

        _ = wasFiltering; // (state kept for clarity; no extra action needed)
    }

    private static bool Matches(RevisionRow row, string query)
        => row.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Hash.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.ShortHash.Contains(query, StringComparison.OrdinalIgnoreCase);

    // Re-applies the current "View" options (date mode / visible columns) without
    // re-running git: it rebuilds the header and re-templates the currently shown
    // rows (respecting any active filter, since _rows is the filtered subset).
    private void RefreshView()
    {
        _headerHost.Content = BuildHeader();
        IReadOnlyList<RevisionRow> current = _rows;
        _list.ItemsSource = null;
        _list.ItemsSource = current;
    }

    // Recomputes, from the loaded rows, HEAD's reachability sets used by the two
    // render-time "View" highlight styles. Best-effort and bounded to the loaded
    // window: if HEAD is not among the loaded rows the sets stay empty and both
    // styles become no-ops. Ancestors (all parents) ∪ descendants (all children)
    // ∪ HEAD are the "relatives"; the current-branch line is HEAD's first-parent
    // chain. Uses only ParentHashes already carried on each row — no git.
    private void ComputeReachability()
    {
        _headRelatives = [];
        _currentBranchLine = [];

        RevisionRow? head = null;
        foreach (RevisionRow row in _allRows)
        {
            if (row.IsHead)
            {
                head = row;
                break;
            }
        }

        if (head is null)
        {
            return;
        }

        // Index by hash for O(1) parent/child lookups, and a parent -> children map.
        Dictionary<string, RevisionRow> byHash = new(_allRows.Count);
        Dictionary<string, List<string>> children = [];
        foreach (RevisionRow row in _allRows)
        {
            byHash[row.Hash] = row;
        }

        foreach (RevisionRow row in _allRows)
        {
            foreach (string parent in row.ParentHashes)
            {
                if (!children.TryGetValue(parent, out List<string>? kids))
                {
                    kids = [];
                    children[parent] = kids;
                }

                kids.Add(row.Hash);
            }
        }

        // Ancestors: walk parents from HEAD. Descendants: walk children from HEAD.
        HashSet<string> relatives = [head.Hash];
        Walk(head.Hash, relatives, h => byHash.TryGetValue(h, out RevisionRow? r) ? r.ParentHashes : []);
        Walk(head.Hash, relatives, h => children.TryGetValue(h, out List<string>? c) ? c : []);
        _headRelatives = relatives;

        // Current-branch line: HEAD's first-parent chain (approximates the branch).
        HashSet<string> line = [];
        string? cursor = head.Hash;
        while (cursor is not null && line.Add(cursor) && byHash.TryGetValue(cursor, out RevisionRow? cur))
        {
            cursor = cur.ParentHashes.Count > 0 ? cur.ParentHashes[0] : null;
        }

        _currentBranchLine = line;
    }

    // Iterative transitive walk over a neighbour function, accumulating into seen.
    private static void Walk(string start, HashSet<string> seen, Func<string, IReadOnlyList<string>> neighbours)
    {
        Stack<string> stack = new();
        stack.Push(start);
        while (stack.Count > 0)
        {
            string node = stack.Pop();
            foreach (string next in neighbours(node))
            {
                if (seen.Add(next))
                {
                    stack.Push(next);
                }
            }
        }
    }

    // "View" menu: which refs the log walks (remotes / tags / stashes), the walk
    // order (date vs topological), and the two render-time highlight styles. The
    // first four reload via Reload() (preserving filter/notes/date/columns/DAG);
    // the last two only re-template via RefreshView().
    private Flyout BuildViewFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 210 };

        panel.Children.Add(SectionLabel("Show in log"));

        CheckBox remotes = MakeCheck("Remote branches", _showRemotes);
        remotes.IsCheckedChanged += (_, _) =>
        {
            _showRemotes = remotes.IsChecked == true;
            Reload();
        };

        CheckBox tags = MakeCheck("Tags", _showTags);
        tags.IsCheckedChanged += (_, _) =>
        {
            _showTags = tags.IsChecked == true;
            Reload();
        };

        CheckBox stashes = MakeCheck("Stashes", _showStashes);
        stashes.IsCheckedChanged += (_, _) =>
        {
            _showStashes = stashes.IsChecked == true;
            Reload();
        };

        panel.Children.Add(remotes);
        panel.Children.Add(tags);
        panel.Children.Add(stashes);

        panel.Children.Add(SectionLabel("Order"));
        RadioButton dateOrder = MakeRadio("Date order", "revOrder", !_topoOrder);
        RadioButton topoOrder = MakeRadio("Topo-order", "revOrder", _topoOrder);
        dateOrder.IsCheckedChanged += (_, _) =>
        {
            if (dateOrder.IsChecked == true && _topoOrder)
            {
                _topoOrder = false;
                Reload();
            }
        };
        topoOrder.IsCheckedChanged += (_, _) =>
        {
            if (topoOrder.IsChecked == true && !_topoOrder)
            {
                _topoOrder = true;
                Reload();
            }
        };
        panel.Children.Add(dateOrder);
        panel.Children.Add(topoOrder);

        panel.Children.Add(SectionLabel("Highlighting"));

        CheckBox nonRelatives = MakeCheck("Draw non-relatives gray", _drawNonRelativesGray);
        nonRelatives.IsCheckedChanged += (_, _) =>
        {
            _drawNonRelativesGray = nonRelatives.IsChecked == true;
            RefreshView();
        };

        CheckBox highlight = MakeCheck("Highlight current branch", _highlightCurrentBranch);
        highlight.IsCheckedChanged += (_, _) =>
        {
            _highlightCurrentBranch = highlight.IsChecked == true;
            RefreshView();
        };

        panel.Children.Add(nonRelatives);
        panel.Children.Add(highlight);

        return new Flyout
        {
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
        };
    }

    // Formats a row's Date cell from the selected source (commit vs author) and
    // mode (absolute vs relative). Artificial/empty timestamps render as blank.
    private string FormatDate(RevisionRow row)
    {
        DateTime dt = _dateSource == DateSource.Author ? row.AuthorDate : row.CommitDate;
        if (dt == DateTime.MaxValue || dt == DateTime.MinValue)
        {
            return string.Empty;
        }

        return _relativeDates ? Relative(dt) : dt.ToString("yyyy-MM-dd HH:mm");
    }

    // A compact human "… ago" rendering (dates are LocalDateTime, so compare to now).
    private static string Relative(DateTime dt)
    {
        TimeSpan span = DateTime.Now - dt;
        if (span.Ticks < 0)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 60)
        {
            return "just now";
        }

        if (span.TotalMinutes < 60)
        {
            int m = (int)span.TotalMinutes;
            return $"{m} minute{(m == 1 ? "" : "s")} ago";
        }

        if (span.TotalHours < 24)
        {
            int h = (int)span.TotalHours;
            return $"{h} hour{(h == 1 ? "" : "s")} ago";
        }

        if (span.TotalDays < 30)
        {
            int d = (int)span.TotalDays;
            return $"{d} day{(d == 1 ? "" : "s")} ago";
        }

        if (span.TotalDays < 365)
        {
            int mo = (int)(span.TotalDays / 30);
            return $"{mo} month{(mo == 1 ? "" : "s")} ago";
        }

        int y = (int)(span.TotalDays / 365);
        return $"{y} year{(y == 1 ? "" : "s")} ago";
    }

    // A small compact toolbar button (styled from App.* brushes) used for the
    // Date and Columns dropdown menus next to the filter box.
    private static Button MakeBarButton(string text)
        => new()
        {
            Content = text,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

    // Date menu: choose the timestamp source (commit/author) and the display mode
    // (absolute/relative). Selections apply live.
    private Flyout BuildDateFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 150 };

        panel.Children.Add(SectionLabel("Date shown"));
        RadioButton commit = MakeRadio("Commit date", "revDateSrc", _dateSource == DateSource.Commit);
        RadioButton author = MakeRadio("Author date", "revDateSrc", _dateSource == DateSource.Author);
        commit.IsCheckedChanged += (_, _) =>
        {
            if (commit.IsChecked == true)
            {
                _dateSource = DateSource.Commit;
                RefreshView();
            }
        };
        author.IsCheckedChanged += (_, _) =>
        {
            if (author.IsChecked == true)
            {
                _dateSource = DateSource.Author;
                RefreshView();
            }
        };
        panel.Children.Add(commit);
        panel.Children.Add(author);

        panel.Children.Add(SectionLabel("Format"));
        RadioButton absolute = MakeRadio("Absolute", "revDateFmt", !_relativeDates);
        RadioButton relative = MakeRadio("Relative", "revDateFmt", _relativeDates);
        absolute.IsCheckedChanged += (_, _) =>
        {
            if (absolute.IsChecked == true)
            {
                _relativeDates = false;
                RefreshView();
            }
        };
        relative.IsCheckedChanged += (_, _) =>
        {
            if (relative.IsChecked == true)
            {
                _relativeDates = true;
                RefreshView();
            }
        };
        panel.Children.Add(absolute);
        panel.Children.Add(relative);

        return new Flyout
        {
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
        };
    }

    // Columns menu: toggle visibility of the Author, Date and Commit-ID columns.
    private Flyout BuildColumnsFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 140 };
        panel.Children.Add(SectionLabel("Show columns"));

        CheckBox hash = MakeCheck("Commit ID", _showHash);
        hash.IsCheckedChanged += (_, _) =>
        {
            _showHash = hash.IsChecked == true;
            RefreshView();
        };

        CheckBox author = MakeCheck("Author", _showAuthor);
        author.IsCheckedChanged += (_, _) =>
        {
            _showAuthor = author.IsChecked == true;
            RefreshView();
        };

        CheckBox date = MakeCheck("Date", _showDate);
        date.IsCheckedChanged += (_, _) =>
        {
            _showDate = date.IsChecked == true;
            RefreshView();
        };

        panel.Children.Add(hash);
        panel.Children.Add(author);
        panel.Children.Add(date);

        return new Flyout
        {
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
        };
    }

    // Branches menu: choose which refs the log walks. Each selection re-runs the
    // log via Reload(), preserving the text filter, git-notes, date mode, column
    // toggles and the (service-rebuilt) DAG graph.
    private Flyout BuildBranchesFlyout()
    {
        StackPanel panel = new() { Spacing = 3, Margin = new Thickness(6), MinWidth = 190 };

        panel.Children.Add(SectionLabel("Branches shown"));

        RadioButton all = MakeRadio("All branches", "revBranchScope", _branchScope == BranchScope.AllBranches);
        RadioButton current = MakeRadio("Current branch only", "revBranchScope", _branchScope == BranchScope.CurrentBranch);
        RadioButton filtered = MakeRadio("Filtered branches", "revBranchScope", _branchScope == BranchScope.Filtered);

        all.IsCheckedChanged += (_, _) => SelectScope(all, BranchScope.AllBranches);
        current.IsCheckedChanged += (_, _) => SelectScope(current, BranchScope.CurrentBranch);
        filtered.IsCheckedChanged += (_, _) => SelectScope(filtered, BranchScope.Filtered);

        panel.Children.Add(all);
        panel.Children.Add(current);
        panel.Children.Add(filtered);

        // "Filtered" has no selection UI yet, so it walks the current branch.
        panel.Children.Add(new TextBlock
        {
            Text = "Filtered walks the current branch until a ref picker is added.",
            Foreground = B("App.TextDim"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new Flyout
        {
            Content = new Border
            {
                Background = B("App.Panel"),
                Padding = new Thickness(2),
                Child = panel,
            },
        };
    }

    // Applies a newly-checked branch-scope radio: updates the mode and re-runs the
    // log. Guarded so the uncheck half of a radio pair does nothing, and a no-op
    // re-selection of the same mode does not trigger a redundant reload.
    private void SelectScope(RadioButton radio, BranchScope scope)
    {
        if (radio.IsChecked != true || _branchScope == scope)
        {
            return;
        }

        _branchScope = scope;
        Reload();
    }

    // "Go to" menu: buttons to jump to the first parent / nearest child of the
    // current selection, plus a hash box to select an arbitrary commit. All three
    // also work via keyboard (Alt+↑, Alt+↓, Ctrl+G).
    private Flyout BuildGoToFlyout()
    {
        StackPanel panel = new() { Spacing = 4, Margin = new Thickness(6), MinWidth = 190 };

        Flyout flyout = new();

        panel.Children.Add(SectionLabel("Navigate"));

        Button parent = MakeMenuButton("↑  First parent   (Alt+↑)");
        parent.Click += (_, _) =>
        {
            flyout.Hide();
            GoToParent();
        };

        Button child = MakeMenuButton("↓  Nearest child   (Alt+↓)");
        child.Click += (_, _) =>
        {
            flyout.Hide();
            GoToChild();
        };

        panel.Children.Add(parent);
        panel.Children.Add(child);

        panel.Children.Add(SectionLabel("Go to commit"));
        panel.Children.Add(_goToBox);

        Button go = MakeMenuButton("Select commit");
        void RunGoTo()
        {
            string text = _goToBox.Text ?? string.Empty;
            flyout.Hide();
            GoToCommit(text);
        }

        go.Click += (_, _) => RunGoTo();

        // Enter in the hash box triggers the jump.
        _goToBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RunGoTo();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                flyout.Hide();
                e.Handled = true;
            }
        };

        panel.Children.Add(go);

        flyout.Content = new Border
        {
            Background = B("App.Panel"),
            Padding = new Thickness(2),
            Child = panel,
        };
        return flyout;
    }

    // A full-width, left-aligned button used inside the "Go to" flyout.
    private static Button MakeMenuButton(string text)
        => new()
        {
            Content = text,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };

    // Opens the "Go to" flyout and focuses the hash box (Ctrl+G).
    private void OpenGoTo()
    {
        if (_goToButton.Flyout is Flyout f)
        {
            f.ShowAt(_goToButton);
            Dispatcher.UIThread.Post(() =>
            {
                _goToBox.Focus();
                _goToBox.SelectAll();
            });
        }
    }

    // --- Commit navigation ---------------------------------------------------
    //
    // Parent/child use the real DAG relationship carried on each row
    // (RevisionRow.ParentHashes), NOT graph-lane geometry — so a jump lands on the
    // exact commit even across merges. Navigation targets the currently displayed
    // rows (_rows), which equal _allRows when no filter is applied; "Go to commit"
    // additionally clears an active filter if the target is hidden by it.

    // Selects the first parent (ParentHashes[0]) of the current selection.
    private void GoToParent()
    {
        if (_list.SelectedItem is not RevisionRow row)
        {
            return;
        }

        if (row.ParentHashes.Count == 0)
        {
            _status.Text = "No parent commit (root).";
            return;
        }

        if (!SelectByHash(row.ParentHashes[0]))
        {
            _status.Text = "Parent commit is not in the loaded history.";
        }
    }

    // Selects the child commit nearest to the current selection: any loaded row
    // that lists the current commit among its parents, closest by list position.
    private void GoToChild()
    {
        if (_list.SelectedItem is not RevisionRow row)
        {
            return;
        }

        int current = _list.SelectedIndex;
        RevisionRow? best = null;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < _rows.Count; i++)
        {
            foreach (string parent in _rows[i].ParentHashes)
            {
                if (parent == row.Hash)
                {
                    int distance = Math.Abs(i - current);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = _rows[i];
                    }

                    break;
                }
            }
        }

        if (best is null)
        {
            _status.Text = "No child commit in the loaded history.";
            return;
        }

        SelectRow(best);
    }

    // Selects the commit matching an entered hash (full or abbreviated). Searches
    // the displayed rows first; if a filter hides the target, it is cleared and the
    // full set is retried so the jump still lands.
    private void GoToCommit(string? text)
    {
        string query = (text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return;
        }

        int index = FindIndex(_rows, query);
        if (index < 0 && _filterActive)
        {
            // Drop the filter (ApplyFilter resets _rows to _allRows) and retry.
            _search.Text = string.Empty;
            index = FindIndex(_rows, query);
        }

        if (index < 0)
        {
            _status.Text = $"No commit matching \"{query}\".";
            return;
        }

        SelectIndex(index);
    }

    // Locates a commit by hash: exact full/short match first, then a hash prefix.
    private static int FindIndex(IReadOnlyList<RevisionRow> rows, string query)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Hash.Equals(query, StringComparison.OrdinalIgnoreCase)
                || rows[i].ShortHash.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Hash.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Selects a specific row (by full hash) in the displayed set; returns false if
    // it is not currently shown. Scrolls the target into view and keeps focus on
    // the list so successive keyboard jumps chain naturally.
    private bool SelectByHash(string hash)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Hash == hash)
            {
                SelectIndex(i);
                return true;
            }
        }

        return false;
    }

    private void SelectRow(RevisionRow row)
    {
        int index = FindIndex(_rows, row.Hash);
        if (index >= 0)
        {
            SelectIndex(index);
        }
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        _list.SelectedIndex = index;
        _list.ScrollIntoView(_rows[index]);
        _list.Focus();
    }

    private static TextBlock SectionLabel(string text)
        => new()
        {
            Text = text,
            Foreground = B("App.TextDim"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 2, 0, 1),
        };

    private static RadioButton MakeRadio(string text, string group, bool isChecked)
        => new()
        {
            Content = text,
            GroupName = group,
            IsChecked = isChecked,
            Foreground = B("App.Text"),
            FontSize = 12,
        };

    private static CheckBox MakeCheck(string text, bool isChecked)
        => new()
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = B("App.Text"),
            FontSize = 12,
        };

    // A small amber "note" pill indicating the commit carries a git note.
    private static Border BuildNotesBadge()
        => new()
        {
            Background = new SolidColorBrush(Color.FromRgb(0x5A, 0x4B, 0x2E)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = "This commit has a git note",
            Child = new TextBlock
            {
                Text = "note",
                Foreground = new SolidColorBrush(Color.FromRgb(0xE3, 0xCB, 0x95)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    private Grid MakeColumns()
    {
        // Hidden columns collapse to zero width; their content is simply not added
        // (see BuildHeader/BuildRow) so nothing overflows into the neighbouring cell.
        double hash = _showHash ? HashWidth : 0;
        double author = _showAuthor ? AuthorWidth : 0;
        double date = _showDate ? DateWidth : 0;

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{EffectiveGraphWidth},{hash},{author},{date},*"),
        };
    }

    private Control BuildHeader()
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);

        AddCell(grid, 0, string.Empty, B("App.TextDim"), bold: true);
        if (_showHash)
        {
            AddCell(grid, 1, "Commit ID", B("App.TextDim"), bold: true);
        }

        if (_showAuthor)
        {
            AddCell(grid, 2, "Author", B("App.TextDim"), bold: true);
        }

        if (_showDate)
        {
            AddCell(grid, 3, _relativeDates ? "Date (rel.)" : "Date", B("App.TextDim"), bold: true);
        }

        AddCell(grid, 4, "Subject", B("App.TextDim"), bold: true);

        return new Border
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 3, 0, 3),
            Child = grid,
        };
    }

    private Control BuildRow(RevisionRow row)
    {
        Grid grid = MakeColumns();
        grid.Margin = new Thickness(10, 0, 10, 0);
        grid.MinHeight = 20;

        // Subtle alternating-row background (App.Panel / App.PanelAlt).
        int index = _rows is List<RevisionRow> list ? list.IndexOf(row) : IndexOf(_rows, row);
        grid.Background = (index & 1) == 0 ? B("App.Panel") : B("App.PanelAlt");

        // Graph cell (column 0): the DAG lanes for this row. While a filter is
        // active the rows shown are a non-contiguous subset, so the precomputed
        // segments (which reference adjacent rows in the full list) no longer make
        // sense — the column is collapsed to zero width and the graph is skipped
        // to avoid rendering a garbled DAG. It returns in full once the filter clears.
        if (!_filterActive)
        {
            RevisionGraphControl graph = new(row.GraphSegments, row.NodeLane, LaneWidth);
            Grid.SetColumn(graph, 0);
            grid.Children.Add(graph);
        }

        // Render-time "View" highlight styles (no reload):
        //  - highlight current branch: HEAD's first-parent line is emphasised
        //    (accent + bold), taking precedence over graying.
        //  - draw non-relatives gray: rows not reachable from/to HEAD are dimmed.
        //    Guarded on a non-empty relatives set so it is a no-op when HEAD is
        //    outside the loaded window.
        bool onBranch = _highlightCurrentBranch && _currentBranchLine.Contains(row.Hash);
        bool nonRelative = !onBranch && _drawNonRelativesGray
            && _headRelatives.Count > 0 && !_headRelatives.Contains(row.Hash);

        IBrush hashBrush = nonRelative ? B("App.TextDim") : B("App.Accent");
        IBrush subjectBrush = onBranch ? B("App.Accent") : nonRelative ? B("App.TextDim") : B("App.Text");

        // Hash: monospace + accent so it reads as a code identifier.
        if (_showHash)
        {
            AddCell(grid, 1, row.ShortHash, hashBrush, bold: onBranch, monospace: true);
        }

        if (_showAuthor)
        {
            AddCell(grid, 2, row.Author, B("App.TextDim"));
        }

        if (_showDate)
        {
            AddCell(grid, 3, FormatDate(row), B("App.TextDim"));
        }

        // Subject cell: an optional git-notes indicator, then ref badges, then the
        // subject text.
        StackPanel subject = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (row.HasNotes)
        {
            subject.Children.Add(BuildNotesBadge());
        }

        foreach (string refName in row.RefNames)
        {
            // Respect the remote/tag "View" toggles: hide remote-tracking or tag
            // badges when the corresponding toggle is off, so badge display stays
            // consistent with what the walk includes. (Kind is the same '/'/version
            // heuristic used by RefColors, so it is best-effort.)
            if ((!_showRemotes && IsRemoteRef(refName)) || (!_showTags && IsTagRef(refName)))
            {
                continue;
            }

            subject.Children.Add(BuildRefBadge(refName));
        }

        subject.Children.Add(new TextBlock
        {
            Text = row.Subject,
            Foreground = subjectBrush,
            FontWeight = onBranch ? FontWeight.Bold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Grid.SetColumn(subject, 4);
        grid.Children.Add(subject);

        grid.ContextMenu = BuildRowContextMenu(row);
        return grid;
    }

    private static int IndexOf(IReadOnlyList<RevisionRow> rows, RevisionRow row)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }

        return 0;
    }

    // A rounded, muted "pill" for a ref name, coloured by kind: local branch,
    // remote-tracking branch, or tag — echoing the original GitExtensions look.
    private static Border BuildRefBadge(string refName)
    {
        (Color bg, Color fg) = RefColors(refName);

        return new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = refName,
                Foreground = new SolidColorBrush(fg),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    // Ref-kind heuristics (shared by badge coloring and the remote/tag toggles):
    // a "/" marks a remote-tracking ref (origin/main); a leading version-like
    // token (v1.2, 2.0) marks a tag. Local branches match neither.
    private static bool IsRemoteRef(string refName) => refName.Contains('/');

    private static bool IsTagRef(string refName)
        => !IsRemoteRef(refName) && Regex.IsMatch(refName, @"^v?\d");

    // Remote-tracking refs contain a "/" (e.g. origin/main); simple version-like
    // names (v1.2, 2.0) are treated as tags; everything else is a local branch.
    private static (Color Bg, Color Fg) RefColors(string refName)
    {
        if (refName.Contains('/'))
        {
            return (Color.FromRgb(0x3A, 0x4A, 0x5C), Color.FromRgb(0xAF, 0xCB, 0xE3)); // remote: muted blue
        }

        if (Regex.IsMatch(refName, @"^v?\d"))
        {
            return (Color.FromRgb(0x5A, 0x4B, 0x2E), Color.FromRgb(0xE3, 0xCB, 0x95)); // tag: muted amber
        }

        return (Color.FromRgb(0x37, 0x50, 0x3A), Color.FromRgb(0xB6, 0xE0, 0xB9)); // local branch: muted green
    }

    // Right-click menu: copy details of the row that was clicked.
    private ContextMenu BuildRowContextMenu(RevisionRow row)
    {
        MenuItem copyHash = new() { Header = "Copy commit hash" };
        copyHash.Click += (_, _) => Copy(row.Hash);

        MenuItem copySubject = new() { Header = "Copy subject" };
        copySubject.Click += (_, _) => Copy(row.Subject);

        MenuItem copyAuthor = new() { Header = "Copy author" };
        copyAuthor.Click += (_, _) => Copy(row.Author);

        ContextMenu menu = new()
        {
            Items =
            {
                copyHash,
                copySubject,
                copyAuthor,
            },
        };

        if (_commitCommands.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach ((string header, Action<string> handler) in _commitCommands)
            {
                MenuItem item = new() { Header = header };
                item.Click += (_, _) => handler(row.Hash);
                menu.Items.Add(item);
            }
        }

        return menu;
    }

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private static void AddCell(Grid grid, int column, string text, IBrush? foreground = null, bool bold = false, bool monospace = false)
    {
        TextBlock block = new()
        {
            Text = text,
            Foreground = foreground ?? B("App.Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        };

        if (monospace)
        {
            block.FontFamily = new FontFamily("monospace,Consolas,Menlo");
        }

        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    /// <summary>
    ///  Draws one row's slice of the commit DAG: colored lane lines (verticals
    ///  for pass-through lanes, diagonals for branch/merge edges) plus the node
    ///  dot for this commit. Geometry comes from <see cref="RevisionGraphSegment"/>s
    ///  computed by <see cref="RevisionService"/>.
    /// </summary>
    private sealed class RevisionGraphControl : Control
    {
        private static readonly Color[] LaneColors =
        {
            Color.FromRgb(0x22, 0x8B, 0x22), // green
            Color.FromRgb(0x1E, 0x90, 0xFF), // blue
            Color.FromRgb(0xFF, 0x8C, 0x00), // orange
            Color.FromRgb(0x93, 0x70, 0xDB), // purple
            Color.FromRgb(0xDC, 0x14, 0x3C), // crimson
            Color.FromRgb(0x00, 0x8B, 0x8B), // teal
            Color.FromRgb(0xB8, 0x86, 0x0B), // goldenrod
            Color.FromRgb(0xFF, 0x14, 0x93), // pink
        };

        private static readonly IBrush[] LaneBrushes =
            LaneColors.Select(c => (IBrush)new SolidColorBrush(c)).ToArray();

        private readonly IReadOnlyList<RevisionGraphSegment> _segments;
        private readonly int _nodeLane;
        private readonly double _laneWidth;

        public RevisionGraphControl(IReadOnlyList<RevisionGraphSegment> segments, int nodeLane, double laneWidth)
        {
            _segments = segments;
            _nodeLane = nodeLane;
            _laneWidth = laneWidth;

            // Custom-drawn Controls do NOT clip by default: lane lines/edges can
            // paint outside the row's bounds and smear into neighbours / the
            // panel below. Clip strictly to our own bounds.
            ClipToBounds = true;
        }

        private static IBrush Brush(int lane)
            => LaneBrushes[((lane % LaneBrushes.Length) + LaneBrushes.Length) % LaneBrushes.Length];

        public override void Render(DrawingContext context)
        {
            double h = Bounds.Height;
            if (h <= 0)
            {
                return;
            }

            double X(double lane) => (lane * _laneWidth) + (_laneWidth / 2);

            foreach (RevisionGraphSegment s in _segments)
            {
                Pen pen = new(Brush(s.ColorLane), 2);
                context.DrawLine(
                    pen,
                    new Point(X(s.FromLane), s.FromY * h),
                    new Point(X(s.ToLane), s.ToY * h));
            }

            IBrush nodeBrush = Brush(_nodeLane);
            context.DrawEllipse(nodeBrush, null, new Point(X(_nodeLane), h / 2), 4, 4);
        }
    }
}
