using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A single file's commit history, shown in the REAL revision grid: a second
///  <see cref="RevisionGridView"/> instance loaded through
///  <see cref="RevisionGridView.LoadFileHistory"/>, i.e. the same walk narrowed to
///  the file and following it across renames. The tab therefore gets the DAG graph,
///  the branch/tag decorations, the columns and their View menus, multi-selection,
///  the full row context menu, quick-search and the navigation hotkeys — all of
///  which the bare four-column list it replaced did not have.
///
///  <para>WHY A SECOND INSTANCE, not the shell's main grid. The main instance carries
///  repository-wide state that the file history would corrupt and cannot share: the
///  user's place in the repository history (selection, scroll, paged-in depth), the
///  quick-filter box and the advanced <c>RevisionFilter</c> he set, the branch scope
///  and filtered-ref set, the artificial working-directory/index rows fed by
///  <c>MainWindow.SetWorkingState</c>, the persisted view options it publishes through
///  <c>ViewOptionsChanged</c>, and its event wiring into the shell (selection drives
///  the bottom tabs). Narrowing that grid to one path would hijack the top pane, lie
///  in the toolbar (a path filter the user never typed) and be persisted as his
///  preference. It also lives in a different pane: the history has to be visible in
///  the bottom tab while the repository history stays on screen above it.</para>
///
///  <para>WHAT THIS VIEW STILL OWNS. The four <c>git log</c> switches upstream's
///  <c>FormFileHistory</c> exposes (its "Show Full History" drop-down and the two
///  "Detect and follow…" toggles), the Reload button, the message/identity line, and
///  the file-specific commands it plants in the grid's row menu through
///  <see cref="RevisionGridView.AddCommitCommand"/>: "Save as", "Copy path" (both of
///  which need the name the file had IN THAT REVISION — see
///  <see cref="FileHistoryService.GetFilePathByRevision"/>) and the revert /
///  cherry-pick pair, which keep their host-handler-else-do-it-here contract.</para>
///
///  <para>Deliberately NOT ported here (upstream's four inner tabs Commit diff /
///  Diff / View / Blame, the eleven "Blame options" toggles and open-with-difftool
///  "selected &lt;-&gt; local"). Upstream's "Show author date" toggle is gone as a
///  local switch: the grid's own Date menu does author/commit date (and relative
///  dates) for every column set.</para>
/// </summary>
public sealed class FileHistoryView : UserControl
{
    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private readonly FileHistoryService _service = new();
    private readonly RevisionGridView _grid = new();
    private readonly TextBlock _status;
    private readonly Button _fullHistoryButton;
    private readonly Button _followButton;
    private readonly Button _reloadButton;

    // The git log switches (upstream: AppSettings.*InFileHistory, which upstream also
    // persists). Restored from view-prefs.json at construction and written back by
    // SetOptions, so the four switches survive a restart.
    //
    // Loaded per instance rather than through a shared singleton: this view is
    // instantiated twice (MainWindow's History tab and the standalone window
    // CommitDialog opens), and each one reading the file at construction gives the
    // newer instance the current state without any cross-instance plumbing.
    private static readonly ViewPrefsService PrefsService = new();

    private FileHistoryOptions _options = LoadPersistedOptions();

    // Last successful load, so a language switch can re-word the status line
    // without re-running git, and so a toggle can reload the same file.
    private string? _repoPath;
    private string? _filePath;
    private string? _shownFile;

    // Commit hash -> the name the file had in that revision. The grid's row model
    // (RevisionRow) carries no file name, so the mapping is kept here and loaded
    // alongside the grid's walk; without it "Save as" on a commit older than a
    // rename would read (or fail to read) the wrong path.
    private IReadOnlyDictionary<string, string> _pathByHash =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Guards the path map against a stale load: a second file asked for while the
    // first map was still being built must not overwrite it.
    private int _mapToken;

    // Upstream's _fileNotFound marker, appended to the status line for the selected
    // revision; guarded by a token so a stale background check cannot overwrite it.
    private string _notFoundMarker = string.Empty;
    private int _notFoundToken;

    /// <summary>
    ///  Forwards the host's bisect-session probe to the inner grid, so this tab's row
    ///  menu gates its bisect entries on the same answer the repository grid uses (see
    ///  <see cref="RevisionGridView.IsBisectInProgress"/>). Without it the entries here
    ///  would be permanently disabled while the identical menu in the other tab worked.
    /// </summary>
    public Func<bool>? IsBisectInProgress
    {
        get => _grid.IsBisectInProgress;
        set => _grid.IsBisectInProgress = value;
    }

    /// <summary>
    ///  Raised when the user selects a commit; the argument is the full commit hash.
    /// </summary>
    public event Action<string>? RevisionSelected;

    /// <summary>
    ///  Raised by the row menu's "Revert this commit…". When nothing is subscribed
    ///  the view performs the revert itself through <see cref="RevertArchiveService"/>
    ///  and reports the outcome in its own status line; a host that wants the
    ///  shared refresh / watcher-suspend treatment (<c>MainWindow.RunOp</c>) can
    ///  subscribe and take over.
    /// </summary>
    public event Action<string>? RevertCommitRequested;

    /// <summary>
    ///  Raised by the row menu's "Cherry-pick". Same fallback contract as
    ///  <see cref="RevertCommitRequested"/> (<see cref="StashOpsService.CherryPick"/>).
    /// </summary>
    public event Action<string>? CherryPickCommitRequested;

    /// <summary>
    ///  Raised on double click / Enter on a row — upstream's
    ///  <c>FileChangesDoubleClick</c> → <c>RevisionGrid.ViewSelectedRevisions()</c>,
    ///  i.e. "open that revision". The argument is the full commit hash. The grid
    ///  selects the row first, so even with nothing subscribed the double click still
    ///  brings the commit forward through <see cref="RevisionSelected"/>.
    /// </summary>
    public event Action<string>? RevisionActivated;

    public FileHistoryView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4),
            Foreground = B("App.TextDim"),
            Background = B("App.Toolbar"),
            Padding = new Thickness(4, 4, 4, 4),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = T("No file loaded."),
        };

        // The grid raises RevisionSelected only for real commits (never for the
        // artificial rows, which this mode does not show at all), so the host
        // contract is unchanged from the list this replaced.
        _grid.RevisionSelected += hash =>
        {
            CheckFilePresence(hash);
            RevisionSelected?.Invoke(hash);
        };
        _grid.RevisionActivated += hash => RevisionActivated?.Invoke(hash);

        // File-specific commands in the grid's own row menu. The two headers below
        // that the grid routes ("Revert this commit…", "Cherry-pick") land in their
        // structured slots; the file-name-dependent pair lands under "Other actions".
        _grid.AddCommitCommand(
            T("FormFileHistory/saveAsToolStripMenuItem.Text", "Save as") + "…",
            SaveAs);
        _grid.AddCommitCommand(
            T("FileStatusList/tsmiCopyPaths.Text", "Copy path"),
            hash => Copy(PathFor(hash)));
        _grid.AddCommitCommand(
            T("FormFileHistory/revertCommitToolStripMenuItem.Text", "Revert this commit…"),
            RevertCommit);
        _grid.AddCommitCommand(
            T("FormFileHistory/cherryPickThisCommitToolStripMenuItem.Text", "Cherry-pick"),
            CherryPickCommit);

        _fullHistoryButton = new Button
        {
            Background = B("App.Control"),
            Foreground = B("App.Text"),
            Padding = new Thickness(8, 3, 8, 3),
        };
        _fullHistoryButton.Click += (_, _) => ShowFullHistoryMenu();

        // The two "Detect and follow…" switches. They used to sit in the row context
        // menu, which is now the grid's; a drop-down of their own keeps them visible
        // (and keeps them next to the other git log switch, which is what they are).
        _followButton = new Button
        {
            Background = B("App.Control"),
            Foreground = B("App.Text"),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
        };
        _followButton.Click += (_, _) => ShowFollowMenu();

        // Upstream's toolStripSplitLoad ("Reload"): re-runs the log for the same file.
        _reloadButton = new Button
        {
            Background = B("App.Control"),
            Foreground = B("App.Text"),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
        };
        _reloadButton.Click += (_, _) => Reload();

        // WrapPanel, not a fixed-width horizontal StackPanel: the Italian captions
        // are longer than the English ones (HANDOFF §3).
        WrapPanel toolbar = new()
        {
            Background = B("App.Toolbar"),
            Margin = new Thickness(8, 2, 8, 2),
            Children = { _reloadButton, _followButton, _fullHistoryButton },
        };

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(toolbar);
        root.Children.Add(_grid);

        Content = root;

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------- per-revision file name

    // The path the file had in a revision — the ONLY one that resolves against that
    // commit's tree once --follow has walked past a rename. Upstream:
    // FormFileHistory.GetFileNameForRevision, with the same fall back to the current
    // name when git could not name the file.
    private string PathFor(string hash)
        => _pathByHash.TryGetValue(hash, out string? historic) && historic.Length > 0
            ? historic
            : _filePath ?? string.Empty;

    /// <summary>
    ///  Registers a commit-targeted command on the embedded grid's row menu, so the
    ///  shell can give this tab the same operations it gives the repository grid
    ///  (checkout, resets, compare, bisect, …). Without them the menu's Reset /
    ///  Advanced / Compare / Bisect submenus — which are built from exactly these
    ///  registrations — would open empty.
    ///
    ///  <para>This view registers its own file-aware entries in the constructor, i.e.
    ///  BEFORE the shell gets a chance to, and the grid keeps the first registration
    ///  of a given header: "Revert this commit…" / "Cherry-pick" therefore keep going
    ///  through <see cref="RevertCommitRequested"/> /
    ///  <see cref="CherryPickCommitRequested"/> (which the shell handles anyway).</para>
    /// </summary>
    public void AddCommitCommand(string header, Action<string> handler)
        => _grid.AddCommitCommand(header, handler);

    /// <summary>
    ///  The name the file had in <paramref name="hash"/>, or the current name when
    ///  git could not name it there. Upstream's
    ///  <c>FormFileHistory.GetFileNameForRevision</c>; kept public because it is what
    ///  a host needs to read the right blob for an older revision.
    /// </summary>
    public string GetFileNameForRevision(string hash) => PathFor(hash);

    private void Copy(string text)
    {
        try
        {
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            _status.Text = F(T("Error: {0}"), ex.Message);
        }
    }

    // ---------------------------------------------------- the git log switch menus

    // Items are added before ShowAt: a MenuFlyout populated inside Opening is not
    // re-measured and shows up as an empty sliver (HANDOFF §3).
    private void ShowFullHistoryMenu()
    {
        MenuFlyout flyout = new();

        MenuItem full = new()
        {
            Header = T("FormFileHistory/showFullHistoryToolStripMenuItem.Text", "Show full history"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.FullHistory,
        };
        full.Click += (_, _) => SetOptions(_options with { FullHistory = !_options.FullHistory });

        MenuItem simplify = new()
        {
            Header = T("FormFileHistory/simplifyMergesToolStripMenuItem.Text", "Simplify merges"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.SimplifyMerges,
            IsEnabled = _options.FullHistory,   // upstream enables it only then
        };
        simplify.Click += (_, _) => SetOptions(_options with { SimplifyMerges = !_options.SimplifyMerges });

        flyout.Items.Add(full);
        flyout.Items.Add(simplify);
        flyout.ShowAt(_fullHistoryButton);
    }

    private void ShowFollowMenu()
    {
        MenuFlyout flyout = new();

        MenuItem follow = new()
        {
            Header = T("FormFileHistory/followFileHistoryToolStripMenuItem.Text", "Detect and follow renames"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.FollowRenames,
        };
        follow.Click += (_, _) => SetOptions(_options with { FollowRenames = !_options.FollowRenames });

        MenuItem exact = new()
        {
            Header = T(
                "FormFileHistory/followFileHistoryRenamesToolStripMenuItem.Text",
                "Detect and follow - exact renames and copies only"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.ExactRenamesAndCopiesOnly,
            IsEnabled = _options.FollowRenames,
        };
        exact.Click += (_, _) => SetOptions(
            _options with { ExactRenamesAndCopiesOnly = !_options.ExactRenamesAndCopiesOnly });

        flyout.Items.Add(follow);
        flyout.Items.Add(exact);
        flyout.ShowAt(_followButton);
    }

    // The single funnel for all four switches, hence the only place that has to write.
    private void SetOptions(FileHistoryOptions options)
    {
        _options = options;
        PersistOptions(options);

        if (_repoPath is not null && _filePath is not null)
        {
            ShowHistory(_repoPath, _filePath);
        }
    }

    // The persisted switches, or the record's own defaults when there is no file.
    // Only the four the menus expose are read back: FileHistoryOptions carries no
    // other member, and the walk-shaping flags of RevisionFilter are derived from
    // these by ToRevisionFilter().
    private static FileHistoryOptions LoadPersistedOptions()
    {
        try
        {
            FileHistoryPrefs prefs = PrefsService.Load().FileHistory;
            return new FileHistoryOptions(
                FollowRenames: prefs.FollowRenames,
                ExactRenamesAndCopiesOnly: prefs.ExactRenamesAndCopiesOnly,
                FullHistory: prefs.FullHistory,
                SimplifyMerges: prefs.SimplifyMerges);
        }
        catch
        {
            // A field initialiser must never throw: it would take the whole view down.
            return new FileHistoryOptions();
        }
    }

    // Update(), not Save(): the same file carries the diff options, the left panel
    // filters and the filter MRU, and a plain save of a stale copy would revert them.
    private static void PersistOptions(FileHistoryOptions options) =>
        PrefsService.Update(prefs => prefs.FileHistory = new FileHistoryPrefs
        {
            FollowRenames = options.FollowRenames,
            ExactRenamesAndCopiesOnly = options.ExactRenamesAndCopiesOnly,
            FullHistory = options.FullHistory,
            SimplifyMerges = options.SimplifyMerges,
        });

    // --------------------------------------------------------------- "Save as"

    // The file's content at the given commit. The picker runs on the UI thread; the
    // git read and the write do not (same shape as DiffView).
    private void SaveAs(string hash)
    {
        if (_repoPath is null || _filePath is null)
        {
            return;
        }

        // PathFor, not _filePath: for a commit older than a rename the blob only
        // exists under the OLD name, so saving the current name wrote the wrong bytes
        // (or failed) without saying so.
        _ = SaveAsCoreAsync(_repoPath, hash, PathFor(hash));
    }

    private async Task SaveAsCoreAsync(string repoPath, string commit, string path)
    {
        try
        {
            IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                _status.Text = T("No file picker is available on this display.");
                return;
            }

            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("FormFileHistory/saveAsToolStripMenuItem.Text", "Save as"),
                SuggestedFileName = Path.GetFileName(path),
                ShowOverwritePrompt = true,
            });

            if (target is null)
            {
                return;   // cancelled
            }

            string? destination = target.TryGetLocalPath();
            if (destination is null)
            {
                _status.Text = T("The chosen location is not a local file.");
                return;
            }

            _status.Text = F(T("Saving {0}…"), destination);

            await Task.Run(async () =>
            {
                byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, commit, path)
                    .ConfigureAwait(false);
                await File.WriteAllBytesAsync(destination, bytes).ConfigureAwait(false);
            });

            _status.Text = F(T("Saved {0}"), destination);
        }
        catch (Exception ex)
        {
            _status.Text = F(T("Error: {0}"), ex.Message);
        }
    }

    // ------------------------------------------------- revert / cherry-pick

    private void RevertCommit(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        if (RevertCommitRequested is { } handler)
        {
            handler(hash);
            return;
        }

        RunLocally(T("FormFileHistory/revertCommitToolStripMenuItem.Text", "Revert commit"), repo =>
        {
            RevertArchiveResult result = new RevertArchiveService().Revert(repo, hash);
            return (result.Success, result.Output);
        });
    }

    private void CherryPickCommit(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        if (CherryPickCommitRequested is { } handler)
        {
            handler(hash);
            return;
        }

        RunLocally(T("FormFileHistory/cherryPickThisCommitToolStripMenuItem.Text", "Cherry pick commit"), repo =>
        {
            StashOpResult result = new StashOpsService().CherryPick(repo, hash);
            return (result.Success, result.Output);
        });
    }

    // Fallback path used when no host handled the request. git runs off the UI
    // thread and nothing is allowed to escape.
    private void RunLocally(string label, Func<string, (bool Success, string Output)> op)
    {
        string repoPath = _repoPath!;
        _status.Text = F(T("{0}…"), label);

        _ = Task.Run(() =>
        {
            (bool success, string output) result;
            try
            {
                result = op(repoPath);
            }
            catch (Exception ex)
            {
                result = (false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (result.success)
                {
                    _status.Text = F(T("{0}: done"), label);
                    if (_filePath is not null)
                    {
                        ShowHistory(repoPath, _filePath);
                    }
                }
                else
                {
                    string firstLine = result.output.Split('\n')[0].Trim();
                    _status.Text = F(T("{0} stopped: {1}"), label, firstLine);
                }
            });
        });
    }

    // -------------------------------------------------------------- translation

    // Fired on the catalogue-loading thread; marshal the relabel to the UI thread.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        ApplyTranslations();
        _status.Text = _shownFile is null ? T("No file loaded.") : StatusLine();
    }

    private void ApplyTranslations()
    {
        _fullHistoryButton.Content = T("FormFileHistory/ShowFullHistory.ToolTipText", "Show Full History") + "  ▾";
        _followButton.Content =
            T("FormFileHistory/followFileHistoryToolStripMenuItem.Text", "Detect and follow renames") + "  ▾";
        // Upstream's split button carries no caption, only the tooltip, so that is
        // the trans-unit the port's visible caption is keyed to.
        _reloadButton.Content = T("FormFileHistory/toolStripSplitLoad.ToolTipText", "Load file history");
    }

    // The tab's own line: WHICH file is shown (the grid's status line below counts
    // the commits) plus, for the selected revision, upstream's "could not identify
    // the file" marker. It is also the surface the Save as / revert messages use.
    private string StatusLine() => (_shownFile ?? string.Empty) + _notFoundMarker;

    // Upstream's _fileNotFound (" - Git could not identify the file {0}"), which it
    // appends to the commit-info tab caption; the port has no tab there, so it goes on
    // the status line of the selected revision. The blob probe is a git call: off the
    // UI thread, and only the newest one is allowed to write.
    private void CheckFilePresence(string hash)
    {
        if (_repoPath is null)
        {
            return;
        }

        _notFoundMarker = string.Empty;
        if (_shownFile is not null)
        {
            _status.Text = StatusLine();
        }

        string repo = _repoPath;
        string path = PathFor(hash);
        int token = ++_notFoundToken;

        _ = Task.Run(() =>
        {
            bool exists = _service.FileExistsInRevision(repo, hash, path);
            if (exists)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token != _notFoundToken || _shownFile is null)
                {
                    return;
                }

                _notFoundMarker = string.Format(
                    T("FormFileHistory/_fileNotFound.Text", " - Git could not identify the file {0}"),
                    path.Length > 0 ? $"\"{path}\"" : T("(unknown)"));
                _status.Text = StatusLine();
            });
        });
    }

    // ------------------------------------------------------------------ loading

    /// <summary>
    ///  Loads and displays the commit history of <paramref name="filePath"/> in
    ///  the repository at <paramref name="repoPath"/>, in the revision grid. Heavy
    ///  git work runs off the UI thread (the grid's own walk, plus the per-revision
    ///  file-name map this view needs on top of it).
    /// </summary>
    public void ShowHistory(string repoPath, string filePath)
    {
        _repoPath = repoPath;
        _filePath = filePath;
        _shownFile = filePath;
        _notFoundMarker = string.Empty;
        _notFoundToken++;
        _status.Text = StatusLine();

        _grid.LoadFileHistory(repoPath, filePath, _options);

        // The name-per-revision map, in its own git call (one `git log --name-only`,
        // as before): the grid's rows do not carry it, and "Save as" on a pre-rename
        // commit is wrong without it.
        FileHistoryOptions options = _options;
        int token = ++_mapToken;
        _ = Task.Run(() =>
        {
            IReadOnlyDictionary<string, string> map;
            try
            {
                map = _service.GetFilePathByRevision(repoPath, filePath, options);
            }
            catch (Exception)
            {
                // A missing map degrades to "use the current path" — never to a
                // broken history.
                map = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token == _mapToken)
                {
                    _pathByHash = map;
                }
            });
        });
    }

    /// <summary>
    ///  Re-runs the log for the file currently shown, keeping the options. This is
    ///  upstream's <c>toolStripSplitLoad_ButtonClick</c> → <c>LoadFileHistory()</c>;
    ///  it is also what a global refresh should call. A no-op while no file is shown.
    /// </summary>
    public void Reload()
    {
        if (_repoPath is not null && _filePath is not null)
        {
            ShowHistory(_repoPath, _filePath);
        }
    }
}
