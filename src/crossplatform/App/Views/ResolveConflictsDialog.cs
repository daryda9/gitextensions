using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Port of upstream's <c>FormResolveConflicts</c> (1571 lines,
///  <c>src/app/GitUI/CommandsDialogs/FormResolveConflicts.cs</c>): the window that
///  lists the unresolved merge conflicts and resolves them, either by handing a
///  file to the configured merge tool or by keeping one side outright.
///
///  <para><b>Layout</b>, following <c>03_resolve merge conflict window dialog.png</c>:
///  the label <i>Unresolved merge conflicts</i> over a single-column
///  <i>Filename</i> list, a column of four buttons on the right
///  (<b>Open in &lt;tool&gt;</b> / <b>Start mergetool</b> /
///  <b>Rescan merge conflicts</b> / <b>Reset</b>), an information strip that
///  describes the selected conflict with a <b>Merge</b> button beside it, the three
///  <i>Local/current (ours)</i> · <i>Base</i> · <i>Remote/incoming (theirs)</i>
///  rows, and the <b>Help</b> link at the bottom.</para>
///
///  <para><b>The tool name is data, not a literal.</b> The first button reads
///  "Open in " + <see cref="ConflictService.GetMergeToolName"/>, i.e.
///  <c>merge.guitool</c> falling back to <c>merge.tool</c> — the same order as
///  upstream's <c>InitMergetool</c> (<c>FormResolveConflicts.cs:689-711</c>). When
///  neither is configured the caption degrades to "Open in mergetool" and both
///  merge-tool actions are disabled with an explanatory status line, which is the
///  same end state as upstream (it shows a message box, then leaves the buttons
///  disabled because <c>mergeToolExtrasConfigured</c> is false, <c>:824-830</c>).</para>
///
///  <para><b>Conflict kinds come from the index stages</b>
///  (<see cref="ConflictKind"/>), never from git's console text, which is
///  localised on this machine. All six stage combinations get a description; note
///  that upstream only covers four and silently keeps the previous label for the
///  add-by-one-side cases (<c>:856-862</c>).</para>
///
///  <para><b>Threading</b>: every git call goes through <see cref="Task.Run"/> and
///  the merge tool is launched detached, so the window stays responsive while
///  kdiff3/meld is open; when the tool exits the list rescans itself, because
///  <c>git mergetool</c> stages the file on a successful exit and the list would
///  otherwise be stale.</para>
/// </summary>
public sealed class ResolveConflictsDialog : Theming.ZoomWindow
{
    // Upstream's documentation anchor for this form: gotoUserManualControl1 with
    // ManualSectionSubfolder = "modify_history", ManualSectionAnchorName =
    // "handle-merge-conflicts" (FormResolveConflicts.Designer.cs:554-555).
    private const string HelpUrl =
        "https://git-extensions-documentation.readthedocs.io/en/main/modify_history.html#handle-merge-conflicts";

    private readonly string _repoPath;
    private readonly ConflictService _service = new();
    private readonly ExternalToolService _externalTools = new();

    private readonly ListBox _files;
    private readonly TextBlock _header;
    private readonly TextBlock _description;
    private readonly TextBlock _status;
    private readonly Button _openInTool;
    private readonly Button _startMergetool;
    private readonly Button _rescan;
    private readonly Button _reset;
    private readonly Button _merge;
    private readonly TextBlock _labelOurs;
    private readonly TextBlock _labelTheirs;
    private readonly TextBlock _ourName;
    private readonly TextBlock _baseName;
    private readonly TextBlock _theirName;

    private readonly MenuItem _ctxOpenInTool = new();
    private readonly MenuItem _ctxMarkResolved = new();
    private readonly MenuItem _ctxChooseOurs = new();
    private readonly MenuItem _ctxChooseTheirs = new();
    private readonly MenuItem _ctxChooseBase = new();
    private readonly MenuItem _ctxOpen = new();
    private readonly MenuItem _ctxShowInFolder = new();

    private readonly string? _mergeTool;
    private readonly bool _inRebase;

    private IReadOnlyList<ConflictEntry> _conflicts;
    private bool _busy;

    /// <summary>
    ///  True once the repository has no unmerged entries left. The caller uses it
    ///  to offer the commit dialog, which is what upstream does at the end of its
    ///  <c>Initialize()</c> (<c>FormResolveConflicts.cs:283-297</c>) — that
    ///  decision does not belong to this window.
    /// </summary>
    public bool AllConflictsResolved { get; private set; }

    private ResolveConflictsDialog(
        string repoPath,
        IReadOnlyList<ConflictEntry> conflicts,
        string? mergeTool,
        bool inRebase)
    {
        _repoPath = repoPath;
        _conflicts = conflicts;
        _mergeTool = mergeTool;
        _inRebase = inRebase;

        IBrush text = Brush("App.Text", Brushes.Gainsboro);
        IBrush dim = Brush("App.TextDim", Brushes.Gray);
        IBrush border = Brush("App.Border", Brushes.DimGray);

        Title = T("FormResolveConflicts/$this.Text", "Resolve merge conflicts");
        Width = 720;
        Height = 480;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.Black);

        TextBlock caption = new()
        {
            Text = T("FormResolveConflicts/label1.Text", "Unresolved merge conflicts"),
            Foreground = text,
            Margin = new Thickness(0, 0, 0, 4),
        };

        // The DataGridView of the original is a single visible column; a header
        // strip over a ListBox is the same information without pulling in a grid.
        _header = new TextBlock
        {
            Text = T("FormResolveConflicts/FileName.HeaderText", "Filename"),
            Foreground = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 4),
        };
        Border headerBar = new()
        {
            Background = Brush("App.PanelAlt", Brushes.DimGray),
            BorderBrush = border,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Child = _header,
        };

        _files = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = text,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
        };
        _files.SelectionChanged += (_, _) => OnSelectionChanged();
        _files.DoubleTapped += (_, _) => OpenSelectedInMergeTool();

        Grid listArea = new()
        {
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(1, GridUnitType.Star)),
            },
        };
        Grid.SetRow(headerBar, 0);
        Grid.SetRow(_files, 1);
        listArea.Children.Add(headerBar);
        listArea.Children.Add(_files);

        _openInTool = ColumnButton(OpenInToolCaption(), () => OpenSelectedInMergeTool());
        _startMergetool = ColumnButton(
            T("FormResolveConflicts/startMergetool.Text", "Start mergetool"),
            StartMergetoolForAll);
        _rescan = ColumnButton(
            T("FormResolveConflicts/Rescan.Text", "Rescan merge conflicts"),
            () => _ = ReloadAsync());
        _reset = ColumnButton(T("FormResolveConflicts/Reset.Text", "Reset"), () => _ = ResetAsync());

        StackPanel buttonColumn = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(8, 0, 0, 0),
            Children = { _openInTool, _startMergetool, _rescan, _reset },
        };

        // Information strip: icon + description + Merge, exactly upstream's
        // tableLayoutPanel3 (pictureBox1 / conflictDescription / merge).
        _description = new TextBlock
        {
            Text = T("FormResolveConflicts/conflictDescription.Text", "Select file"),
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        _merge = new Button
        {
            MinWidth = 130,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = IconText.Header("Merge", T("FormResolveConflicts/merge.Text", "Merge")),
        };
        _merge.Click += (_, _) => OpenSelectedInMergeTool();

        Grid infoRow = new()
        {
            Margin = new Thickness(0, 10, 0, 0),
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Control infoIcon = IconLoader.Image("information", 16) as Control
            ?? new TextBlock { Text = "i", Foreground = dim };
        infoIcon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(infoIcon, 0);
        Grid.SetColumn(_description, 1);
        Grid.SetColumn(_merge, 2);
        infoRow.Children.Add(infoIcon);
        infoRow.Children.Add(_description);
        infoRow.Children.Add(_merge);

        // The three side rows. The "(ours)" / "(theirs)" suffix is appended at
        // runtime because a rebase swaps the two (upstream :256-279).
        _labelOurs = SideLabel(SuffixedLabel(
            T("FormResolveConflicts/labelLocalCurrent.Text", "Local/current"),
            _inRebase ? TheirsWord : OursWord));
        TextBlock labelBase = SideLabel(T("FormResolveConflicts/labelBase.Text", "Base"));
        _labelTheirs = SideLabel(SuffixedLabel(
            T("FormResolveConflicts/labelRemoteIncoming.Text", "Remote/incoming"),
            _inRebase ? OursWord : TheirsWord));

        ToolTip.SetTip(_labelOurs, _inRebase
            ? T("FormResolveConflicts/_changesLocalRebaseTooltip.Text", "Changes from the branch you are rebasing onto")
            : T("FormResolveConflicts/_changesLocalMergeTooltip.Text", "Changes from the current branch"));
        ToolTip.SetTip(_labelTheirs, _inRebase
            ? T("FormResolveConflicts/_changesRemoteRebaseTooltip.Text", "Changes from the branch you are rebasing")
            : T("FormResolveConflicts/_changesRemoteMergeTooltip.Text", "Changes from the branch you are merging"));

        _ourName = SideValue();
        _baseName = SideValue();
        _theirName = SideValue();

        Grid sides = new()
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
        };
        AddAt(sides, _labelOurs, 0, 0);
        AddAt(sides, _ourName, 0, 1);
        AddAt(sides, labelBase, 1, 0);
        AddAt(sides, _baseName, 1, 1);
        AddAt(sides, _labelTheirs, 2, 0);
        AddAt(sides, _theirName, 2, 1);

        _status = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        // Help: a real link, opened through xdg-open like the rest of the port.
        StackPanel helpContent = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (IconLoader.Image("GotoManual", 16) is { } helpIcon)
        {
            helpIcon.VerticalAlignment = VerticalAlignment.Center;
            helpContent.Children.Add(helpIcon);
        }

        helpContent.Children.Add(new TextBlock
        {
            Text = T("Help"),
            Foreground = Brush("App.Accent", Brushes.DodgerBlue),
            TextDecorations = TextDecorations.Underline,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Button help = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = helpContent,
        };
        ToolTip.SetTip(help, string.Format(
            T("GotoUserManualControl/_gotoUserManualControlTooltip.Text", "Read more about this feature at {0}"),
            HelpUrl));
        help.Click += (_, _) => _ = OpenHelpAsync();

        Grid main = new()
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Grid.SetColumn(listArea, 0);
        Grid.SetColumn(buttonColumn, 1);
        main.Children.Add(listArea);
        main.Children.Add(buttonColumn);

        Grid root = new()
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(1, GridUnitType.Star)),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
        };
        Grid.SetRow(caption, 0);
        Grid.SetRow(main, 1);
        Grid.SetRow(infoRow, 2);
        Grid.SetRow(sides, 3);
        Grid.SetRow(_status, 4);
        Grid.SetRow(help, 5);
        help.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(caption);
        root.Children.Add(main);
        root.Children.Add(infoRow);
        root.Children.Add(sides);
        root.Children.Add(_status);
        root.Children.Add(help);

        Content = root;

        BuildContextMenu();
        BindRows();
        if (_conflicts.Count > 0)
        {
            _files.SelectedIndex = 0;
        }

        OnSelectionChanged();
        ReportMergeToolState();

        DialogKeys.InstallEscapeClose(this);

        // BOTH strategies, deliberately. Tunnel alone never fired: with focus on the
        // list there is a route to tunnel down, but on a freshly opened window the
        // focused element is the window itself and only the bubbling pass runs
        // (measured headlessly — Escape worked, B/L/R/M did not). Tunnel is still
        // needed for the opposite case, where the focused ListBox would otherwise
        // swallow bare letters as type-to-search.
        // Bubbling on the window, handledEventsToo so a focused ListBox that claimed
        // the key for type-to-search cannot swallow the shortcut. Registered ONCE:
        // adding a second subscription on the list made the handler run twice per
        // press, which would apply a resolution twice.
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // Give the list keyboard focus, as upstream's grid has it: without this the
        // arrow keys do nothing until the user tabs into the list.
        Opened += (_, _) => _files.Focus();
    }

    /// <summary>
    ///  Shows the dialog modally over <paramref name="owner"/> and returns
    ///  <see langword="true"/> when every conflict was resolved, so the caller can
    ///  chain the commit dialog.
    ///
    ///  <para>The three blocking reads this needs (the unmerged index, the merge
    ///  tool name, and whether a rebase is in progress) are done here, on a
    ///  thread-pool thread, <b>before</b> the window exists: the services block on
    ///  async work and calling them from the UI thread deadlocks (the
    ///  <c>PushDialog</c> lesson).</para>
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, string repoPath)
    {
        ConflictService service = new();
        (IReadOnlyList<ConflictEntry> conflicts, string? tool, bool inRebase) = await Task.Run(() => (
            service.ListConflicts(repoPath),
            service.GetMergeToolName(repoPath),
            service.InTheMiddleOfRebase(repoPath)));

        ResolveConflictsDialog dialog = new(repoPath, conflicts, tool, inRebase);
        await dialog.ShowDialog(owner);
        return dialog.AllConflictsResolved;
    }

    /// <summary>
    ///  True when the repository currently has unmerged entries — the cheap check a
    ///  caller makes before offering "solve conflicts now?". Blocking: call it from
    ///  <see cref="Task.Run"/>.
    /// </summary>
    public static bool HasConflicts(string repoPath)
        => new ConflictService().InTheMiddleOfConflictedMerge(repoPath);

    // ---- rows and selection --------------------------------------------------

    private void BindRows()
    {
        // A NEW list every time: re-assigning the same instance leaves realised
        // containers showing stale visuals (the M50 virtualisation trap).
        _files.ItemsSource = _conflicts.Select(c => c.Path).ToList();
    }

    private List<ConflictEntry> SelectedEntries()
    {
        List<ConflictEntry> selected = [];
        foreach (object? item in _files.SelectedItems ?? (System.Collections.IList)Array.Empty<object>())
        {
            if (item is string path)
            {
                ConflictEntry? entry = _conflicts.FirstOrDefault(c => c.Path == path);
                if (entry is not null)
                {
                    selected.Add(entry);
                }
            }
        }

        return selected;
    }

    private ConflictEntry? SingleSelection()
    {
        List<ConflictEntry> selected = SelectedEntries();
        return selected.Count == 1 ? selected[0] : null;
    }

    private void OnSelectionChanged()
    {
        List<ConflictEntry> selected = SelectedEntries();
        ConflictEntry? single = selected.Count == 1 ? selected[0] : null;

        // "Open in <tool>" / "Merge" act on one file, exactly as upstream disables
        // them for a multi-selection (SetAvailableCommands, :824-830).
        bool toolUsable = _mergeTool is not null && !_busy;
        _openInTool.IsEnabled = toolUsable && single is not null;
        _merge.IsEnabled = toolUsable && single is not null;
        _startMergetool.IsEnabled = toolUsable && _conflicts.Count > 0;
        _rescan.IsEnabled = !_busy;
        _reset.IsEnabled = !_busy;

        _ctxOpenInTool.IsEnabled = _openInTool.IsEnabled;
        _ctxMarkResolved.IsEnabled = !_busy && selected.Count > 0;
        _ctxChooseOurs.IsEnabled = !_busy && selected.Count > 0;
        _ctxChooseTheirs.IsEnabled = !_busy && selected.Count > 0;

        // "Choose base" only when every selected conflict actually has a stage 1;
        // for an add/add there is nothing to revert to.
        _ctxChooseBase.IsEnabled = !_busy && selected.Count > 0 && selected.All(e => e.Base.Exists);

        bool onDisk = single is not null && File.Exists(Path.Combine(_repoPath, single.Path));
        _ctxOpen.IsEnabled = onDisk;
        _ctxShowInFolder.IsEnabled = onDisk;

        if (single is null)
        {
            // Upstream clears the three names on any non-single selection (:798)
            // and leaves the description alone; keeping the description would be
            // misleading here, so it goes back to the prompt.
            _ourName.Text = string.Empty;
            _baseName.Text = string.Empty;
            _theirName.Text = string.Empty;
            _description.Text = selected.Count > 1
                ? T("Several files selected. Choose a side, or mark them resolved, from the right-click menu.")
                : T("FormResolveConflicts/conflictDescription.Text", "Select file");
            return;
        }

        _description.Text = Describe(single);

        string deleted = T("FormResolveConflicts/_deleted.Text", "deleted");
        _ourName.Text = single.Ours.Exists ? single.Ours.Path : deleted;
        _baseName.Text = single.Base.Exists
            ? single.Base.Path
            : T("FormResolveConflicts/_noBase.Text", "no base");
        _theirName.Text = single.Theirs.Exists ? single.Theirs.Path : deleted;
    }

    /// <summary>
    ///  The information-box text for a conflict, keyed on the stage triple.
    ///  The first four cases are upstream's literal strings
    ///  (<c>FormResolveConflicts.cs:44-51</c>, selected at <c>:856-862</c>); the
    ///  last two have no upstream equivalent because upstream's switch falls
    ///  through and keeps whatever the label said before.
    /// </summary>
    private string Describe(ConflictEntry entry)
    {
        // Rebase swaps which git side is the "local" one (upstream :782-784).
        string local = _inRebase ? TheirsWord : OursWord;
        string remote = _inRebase ? OursWord : TheirsWord;

        return entry.Kind switch
        {
            ConflictKind.BothModified => string.Format(
                T("FormResolveConflicts/_fileChangeLocallyAndRemotely.Text",
                  "The file has been changed both locally ({0}) and remotely ({1}). Merge the changes."),
                local, remote),

            ConflictKind.BothAdded => string.Format(
                T("FormResolveConflicts/_fileCreatedLocallyAndRemotely.Text",
                  "A file with the same name has been created locally ({0}) and remotely ({1}). "
                  + "Choose the file you want to keep or merge the files."),
                local, remote),

            ConflictKind.DeletedByUs => string.Format(
                T("FormResolveConflicts/_fileDeletedLocallyAndModifiedRemotely.Text",
                  "The file has been deleted locally ({0}) and modified remotely ({1}). "
                  + "Choose to delete the file or keep the modified version."),
                local, remote),

            ConflictKind.DeletedByThem => string.Format(
                T("FormResolveConflicts/_fileModifiedLocallyAndDeletedRemotely.Text",
                  "The file has been modified locally ({0}) and deleted remotely ({1}). "
                  + "Choose to delete the file or keep the modified version."),
                local, remote),

            ConflictKind.AddedByUs => string.Format(
                T("The file exists only locally ({0}): there is no base revision and no remote version. "
                  + "Choose to keep it or delete it."),
                local),

            _ => string.Format(
                T("The file exists only remotely ({0}): there is no base revision and no local version. "
                  + "Choose to keep it or delete it."),
                remote),
        };
    }

    // ---- context menu --------------------------------------------------------

    private void BuildContextMenu()
    {
        _ctxOpenInTool.Header = OpenInToolCaption();
        _ctxOpenInTool.Click += (_, _) => OpenSelectedInMergeTool();

        _ctxMarkResolved.Header = T("FormResolveConflicts/ContextMarkAsSolved.Text", "Mark conflict as solved");
        _ctxMarkResolved.Click += (_, _) => _ = MarkSelectedResolvedAsync();

        _ctxChooseOurs.Header = _inRebase
            ? T("FormResolveConflicts/_contextChooseLocalRebaseText.Text", "Choose local/current (theirs)")
            : T("FormResolveConflicts/_contextChooseLocalMergeText.Text", "Choose local/current (ours)");
        ToolTip.SetTip(_ctxChooseOurs, _inRebase
            ? T("FormResolveConflicts/_changesTakeOnlyLocalRebaseTooltip.Text",
                "Take only the changes from the branch you are rebasing onto")
            : T("FormResolveConflicts/_changesTakeOnlyLocalMergeTooltip.Text",
                "Take only the changes from the current branch"));
        _ctxChooseOurs.InputGesture = new KeyGesture(Key.L);
        _ctxChooseOurs.Click += (_, _) => _ = ChooseSideAsync(ConflictChoice.Ours);

        _ctxChooseTheirs.Header = _inRebase
            ? T("FormResolveConflicts/_contextChooseRemoteRebaseText.Text", "Choose remote/incoming (ours)")
            : T("FormResolveConflicts/_contextChooseRemoteMergeText.Text", "Choose remote/incoming (theirs)");
        ToolTip.SetTip(_ctxChooseTheirs, _inRebase
            ? T("FormResolveConflicts/_changesTakeOnlyRemoteRebaseTooltip.Text",
                "Take only the changes from the branch you are rebasing")
            : T("FormResolveConflicts/_changesTakeOnlyRemoteMergeTooltip.Text",
                "Take only the changes from the branch you are merging"));
        _ctxChooseTheirs.InputGesture = new KeyGesture(Key.R);
        _ctxChooseTheirs.Click += (_, _) => _ = ChooseSideAsync(ConflictChoice.Theirs);

        _ctxChooseBase.Header = T("FormResolveConflicts/ContextChooseBase.Text", "Choose base");
        ToolTip.SetTip(_ctxChooseBase, T("FormResolveConflicts/_contextChooseBaseTooltip.Text",
            "Take no changes and revert to base content!"));
        _ctxChooseBase.InputGesture = new KeyGesture(Key.B);
        _ctxChooseBase.Click += (_, _) => _ = ChooseSideAsync(ConflictChoice.Base);

        _ctxOpen.Header = T("FormResolveConflicts/openToolStripMenuItem.Text", "Open");
        _ctxOpen.Click += (_, _) => OpenWorkTreeFile();

        _ctxShowInFolder.Header = T("FormResolveConflicts/openFolderToolStripMenuItem.Text", "Show in folder");
        _ctxShowInFolder.Click += (_, _) => ShowWorkTreeFileInFolder();

        // Items are all in place before the menu can ever be shown: mutating them
        // inside Opening leaves the popup un-measured (a one-line sliver).
        _files.ContextMenu = new ContextMenu
        {
            ItemsSource = new List<Control>
            {
                _ctxOpenInTool,
                _ctxMarkResolved,
                new Separator(),
                _ctxChooseOurs,
                _ctxChooseTheirs,
                _ctxChooseBase,
                new Separator(),
                _ctxOpen,
                _ctxShowInFolder,
            },
        };
    }

    // Upstream's hotkeys for FormMergeConflicts (HotkeySettingsManager.cs:343-348):
    // B/L/R choose a side, M merges, F5 rescans.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // handledEventsToo means already-handled presses arrive here too; a shortcut
        // this dialog has itself acted on must not be acted on a second time.
        if (e.Handled || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F5:
                _ = ReloadAsync();
                break;
            case Key.M:
                OpenSelectedInMergeTool();
                break;
            case Key.L:
                _ = ChooseSideAsync(ConflictChoice.Ours);
                break;
            case Key.R:
                _ = ChooseSideAsync(ConflictChoice.Theirs);
                break;
            case Key.B:
                _ = ChooseSideAsync(ConflictChoice.Base);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- actions -------------------------------------------------------------

    private void OpenSelectedInMergeTool()
    {
        if (_mergeTool is null)
        {
            ReportMergeToolState();
            return;
        }

        ConflictEntry? entry = SingleSelection();
        if (entry is null || _busy)
        {
            return;
        }

        // A merge tool needs three inputs. Without them git's mergetool would open
        // on a degenerate case, so route the user to the side actions instead of
        // launching something useless (upstream asks a modal question here).
        if (!entry.CanThreeWayMerge)
        {
            _status.Text = Describe(entry) + " "
                + T("Use the right-click menu to choose a side; there is no three-way merge to run.");
            return;
        }

        LaunchTool(entry.Path);
    }

    private void StartMergetoolForAll()
    {
        if (_mergeTool is null)
        {
            ReportMergeToolState();
            return;
        }

        LaunchTool(path: null);
    }

    // Detached launch: the window must stay usable while kdiff3 is up. The exit
    // callback arrives on a thread-pool thread, hence the Dispatcher hop.
    private void LaunchTool(string? path)
    {
        _status.Text = path is null
            ? string.Format(T("Starting {0} for all conflicted files…"), _mergeTool)
            : string.Format(T("Opening {0} in {1}…"), path, _mergeTool);

        _ = Task.Run(() =>
        {
            ConflictActionResult result = _service.LaunchMergetool(
                _repoPath,
                path,
                onExit: () => Dispatcher.UIThread.Post(() => _ = ReloadAsync()));

            Dispatcher.UIThread.Post(() => _status.Text = result.Message);
        });
    }

    private async Task ChooseSideAsync(ConflictChoice choice)
    {
        List<ConflictEntry> selected = SelectedEntries();
        if (selected.Count == 0 || _busy)
        {
            return;
        }

        if (choice == ConflictChoice.Base && selected.Any(e => !e.Base.Exists))
        {
            _status.Text = T("At least one of the selected files has no base revision.");
            return;
        }

        SetBusy(true);
        List<string> failures = [];
        try
        {
            failures = await Task.Run(() =>
            {
                List<string> errors = [];
                foreach (ConflictEntry entry in selected)
                {
                    ConflictActionResult result = _service.ChooseSide(_repoPath, entry, choice);
                    if (!result.Success)
                    {
                        errors.Add($"{entry.Path}: {result.Message.Trim()}");
                    }
                }

                return errors;
            });
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
        if (failures.Count > 0)
        {
            _status.Text = string.Format(FailedChoiceText(choice), string.Join("; ", failures));
        }
    }

    // Upstream's per-side failure captions (FormResolveConflicts.cs:88-90).
    private static string FailedChoiceText(ConflictChoice choice) => choice switch
    {
        ConflictChoice.Base => T("FormResolveConflicts/_chooseBaseFileFailedText.Text", "Choose base file failed.") + " {0}",
        ConflictChoice.Ours => T("FormResolveConflicts/_chooseLocalFileFailedText.Text", "Choose local file failed.") + " {0}",
        _ => T("FormResolveConflicts/_chooseRemoteFileFailedText.Text", "Choose remote file failed.") + " {0}",
    };

    private async Task MarkSelectedResolvedAsync()
    {
        List<ConflictEntry> selected = SelectedEntries();
        if (selected.Count == 0 || _busy)
        {
            return;
        }

        SetBusy(true);
        List<string> failures = [];
        try
        {
            failures = await Task.Run(() =>
            {
                List<string> errors = [];
                foreach (ConflictEntry entry in selected)
                {
                    ConflictActionResult result = _service.MarkResolved(_repoPath, entry.Path);
                    if (!result.Success)
                    {
                        errors.Add($"{entry.Path}: {result.Message.Trim()}");
                    }
                }

                return errors;
            });
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
        if (failures.Count > 0)
        {
            _status.Text = string.Format(
                T("FormResolveConflicts/_stageFilename.Text", "Stage '{0}'"),
                string.Join("; ", failures));
        }
    }

    /// <summary>
    ///  Upstream's <b>Reset</b>: two confirmations, then <c>git reset --hard</c> and
    ///  close (<c>FormResolveConflicts.cs:754-780</c>). Both prompts are kept — the
    ///  action throws away every change since the last commit.
    /// </summary>
    private async Task ResetAsync()
    {
        if (_busy)
        {
            return;
        }

        bool first = await ConfirmAsync(
            T("FormResolveConflicts/_abortCurrentOperation.Text",
              "You can abort the current conflict resolution by resetting hard.\n"
              + "All changes since the last commit will be deleted.\n\n"
              + "Do you want to reset the changes?"),
            T("FormResolveConflicts/_resetCaption.Text", "Reset"));
        if (!first)
        {
            return;
        }

        bool second = await ConfirmAsync(
            T("FormResolveConflicts/_areYouSureYouWantDeleteFiles.Text",
              "Are you sure you want to DELETE all changes?\n\nThis action cannot be made undone."),
            T("FormResolveConflicts/_areYouSureYouWantDeleteFilesCaption.Text", "WARNING!"));
        if (!second)
        {
            return;
        }

        SetBusy(true);
        ConflictActionResult result;
        try
        {
            result = await Task.Run(() => _service.ResetHard(_repoPath));
        }
        catch (Exception ex)
        {
            result = new ConflictActionResult(false, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        if (!result.Success)
        {
            _status.Text = result.Message;
            return;
        }

        AllConflictsResolved = true;
        Close();
    }

    /// <summary>
    ///  Re-reads the unmerged index (upstream's <b>Rescan merge conflicts</b>,
    ///  which is just <c>Initialize()</c>). Also the callback after the merge tool
    ///  exits, because <c>git mergetool</c> stages a successfully merged file
    ///  itself. When nothing is left the window closes and reports it, leaving the
    ///  "commit now?" decision to the caller.
    /// </summary>
    private async Task ReloadAsync()
    {
        string? keep = SingleSelection()?.Path;

        IReadOnlyList<ConflictEntry> fresh;
        try
        {
            fresh = await Task.Run(() => _service.ListConflicts(_repoPath));
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }

        _conflicts = fresh;
        BindRows();

        if (_conflicts.Count == 0)
        {
            AllConflictsResolved = true;
            _status.Text = T("FormResolveConflicts/_allConflictsResolved.Text",
                "All merge conflicts are resolved, you can commit.");
            Close();
            return;
        }

        int index = keep is null ? 0 : _conflicts.ToList().FindIndex(c => c.Path == keep);
        _files.SelectedIndex = index >= 0 ? index : 0;
        OnSelectionChanged();
    }

    private void OpenWorkTreeFile()
    {
        ConflictEntry? entry = SingleSelection();
        if (entry is null)
        {
            return;
        }

        _ = RunExternalAsync(() => _externalTools.OpenPath(Path.Combine(_repoPath, entry.Path)));
    }

    private void ShowWorkTreeFileInFolder()
    {
        ConflictEntry? entry = SingleSelection();
        if (entry is null)
        {
            return;
        }

        _ = RunExternalAsync(() => _externalTools.ShowInFolder(Path.Combine(_repoPath, entry.Path)));
    }

    private async Task OpenHelpAsync() => await RunExternalAsync(() => _externalTools.OpenUrl(HelpUrl));

    private async Task RunExternalAsync(Func<ExternalToolResult> action)
    {
        ExternalToolResult result;
        try
        {
            result = await Task.Run(action);
        }
        catch (Exception ex)
        {
            result = new ExternalToolResult(false, ex.Message);
        }

        if (!result.Success)
        {
            _status.Text = result.Message;
        }
    }

    // ---- state ---------------------------------------------------------------

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnSelectionChanged();
    }

    // The caption carries the configured tool's name: "Open in kdiff3".
    private string OpenInToolCaption()
        => _mergeTool is null
            ? T("FormResolveConflicts/openMergeToolBtn.Text", "Open in mergetool")
            : $"{T("FormResolveConflicts/_button1Text.Text", "Open in")} {_mergeTool}";

    private void ReportMergeToolState()
    {
        if (_mergeTool is null)
        {
            _status.Text = T("FormResolveConflicts/_noMergeTool.Text",
                "There is no mergetool configured.\nPlease go to settings and set a mergetool!");
            return;
        }

        if (!_service.IsToolOnPath(_mergeTool))
        {
            // A warning only: git resolves the tool through its own mergetool
            // definitions and mergetool.<tool>.path, so PATH is not the last word.
            _status.Text = string.Format(
                T("The merge tool '{0}' is configured but was not found on PATH; git may still resolve it."),
                _mergeTool);
        }
    }

    private async Task<bool> ConfirmAsync(string message, string caption)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Yes"), MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("No"), MinWidth = 80, IsCancel = true };
        Theming.ZoomWindow dialog = new()
        {
            Title = caption,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    Foreground = Brush("App.Text", Brushes.Gainsboro),
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };

        DialogKeys.InstallEscapeClose(dialog);
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // ---- construction helpers ------------------------------------------------

    private static Button ColumnButton(string caption, Action onClick)
    {
        Button button = new()
        {
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // A string Content would eat the '_' of an accelerator as an access key.
            Content = new TextBlock { Text = RevisionFilterDialog.StripMnemonic(caption) },
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock SideLabel(string caption) => new()
    {
        Text = caption,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        Margin = new Thickness(0, 2, 16, 2),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock SideValue() => new()
    {
        Foreground = Brush("App.TextDim", Brushes.Gray),
        Margin = new Thickness(0, 2, 0, 2),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Upstream appends the side word after a NO-BREAK SPACE
    // (DisplayWithSuffixUpdater / UpdateSuffixWithinParenthesis).
    private static string SuffixedLabel(string caption, string suffix) => $"{caption} ({suffix})";

    private static string OursWord => T("FormResolveConflicts/_ours.Text", "ours");

    private static string TheirsWord => T("FormResolveConflicts/_theirs.Text", "theirs");

    private static void AddAt(Grid grid, Control child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
