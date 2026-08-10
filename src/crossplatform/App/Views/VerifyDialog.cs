using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A lost object plus its "recover this one" tick, so the check state can live in the
///  bound row instead of in a parallel structure that the list's container recycling
///  would desynchronise.
/// </summary>
public sealed class LostObjectEntry(LostObject item)
{
    public LostObject Item { get; } = item;

    /// <summary>Ticked by the user; consumed by "Recover selected objects".</summary>
    public bool IsSelected { get; set; }
}

/// <summary>
///  "Recover lost objects" — the Avalonia port of upstream's <c>FormVerify</c>
///  (<c>src/app/GitUI/CommandsDialogs/FormVerify.cs</c>). It replaces the previous
///  stand-in, which was a single "Verify database" button dumping raw <c>git fsck</c>
///  text with no way to act on it.
///
///  <para>What it offers, all backed by <see cref="VerifyService"/>:</para>
///  <list type="bullet">
///   <item>upstream's three fsck switches (<c>--unreachable</c>, <c>--full</c>,
///    <c>--no-reflogs</c>) and its two client-side view filters (commits/tags,
///    blobs/trees);</item>
///   <item>the list of lost objects with Date / Type / Subject / Author / Hash /
///    Parent;</item>
///   <item>recovery: tick objects and press "Recover selected objects" to create
///    <c>LOST_FOUND_*</c> tags, or create a named tag / branch on the selected
///    object;</item>
///   <item>"Save objects to .git/lost-found", "Delete all LOST_AND_FOUND tags" and
///    "Remove all dangling objects" (<c>git prune</c>, confirmed);</item>
///   <item>a preview pane showing <c>git show</c> for the selected object, plus copy
///    hash / copy parent hash / save blob as….</item>
///  </list>
///
///  <para><b>Deliberately absent</b> (no fake buttons): upstream's blob-content
///  language sniffing, which only decorates the Type column with a guessed file type,
///  and its tri-state select-all header cell — the "Select all / none" button below is
///  the same capability without a custom grid header. Both are recorded in NOTES.md.</para>
///
///  <para>Every git call runs through <see cref="Task.Run"/>: the service blocks on git
///  and must never be touched from the UI thread.</para>
/// </summary>
public sealed class VerifyDialog : Theming.ZoomWindow
{
    private readonly VerifyService _service = new();
    private readonly string _repoPath;

    private readonly CheckBox _unreachable;
    private readonly CheckBox _fullCheck;
    private readonly CheckBox _noReflogs;
    private readonly CheckBox _showCommitsAndTags;
    private readonly CheckBox _showOtherObjects;

    private readonly ListBox _list;
    private readonly TextBox _preview;
    private readonly TextBlock _status;

    private readonly Button _recover;
    private readonly Button _createTag;
    private readonly Button _createBranch;
    private readonly Button _saveToLostFound;
    private readonly Button _deleteTags;
    private readonly Button _prune;
    private readonly Button _selectAll;
    private readonly Button _rescan;
    private readonly Button _close;

    private readonly TextBlock _heading;
    private readonly TextBlock _hint;
    private readonly TextBlock[] _headerCells;

    private readonly MenuItem _viewItem;
    private readonly MenuItem _copyHashItem;
    private readonly MenuItem _copyParentItem;
    private readonly MenuItem _saveAsItem;

    private IReadOnlyList<LostObjectEntry> _all = [];
    private bool _busy;

    // True until something real (a git show, a command's output) has been written to
    // the preview pane. Only while it holds may ApplyTranslations replace the pane's
    // text: re-labelling it later would throw away a report the user is reading.
    private bool _previewIsPlaceholder = true;

    /// <summary>
    ///  True when something was written to the repository (tags, branches, a prune), so
    ///  the owner can refresh its views after the dialog closes.
    /// </summary>
    public bool Changed { get; private set; }

    public VerifyDialog(string repoPath)
    {
        _repoPath = repoPath;

        IBrush text = Brush("App.Text", Brushes.Gainsboro);
        IBrush dim = Brush("App.TextDim", Brushes.Gray);

        Width = 1040;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // --- options (upstream's five check boxes, same defaults) ---------
        // Captions and tooltips are all set by ApplyTranslations; only the initial
        // check state, which is behaviour rather than text, is fixed here.
        _unreachable = OptionBox(false, text);
        _fullCheck = OptionBox(false, text);
        _noReflogs = OptionBox(true, text);

        _showCommitsAndTags = OptionBox(true, text);
        _showOtherObjects = OptionBox(false, text);

        // The three fsck switches change the COMMAND, so they re-run git.
        foreach (CheckBox box in new[] { _unreachable, _fullCheck, _noReflogs })
        {
            box.IsCheckedChanged += (_, _) => _ = RescanAsync();
        }

        // The two filters are client-side only — upstream never re-runs git for them.
        // Upstream also refuses to leave both unchecked, auto-ticking the other one
        // (FormVerify.cs:230-252); the same guard lives in ApplyFilter.
        _showCommitsAndTags.IsCheckedChanged += (_, _) => ApplyFilter(cameFromCommitsBox: true);
        _showOtherObjects.IsCheckedChanged += (_, _) => ApplyFilter(cameFromCommitsBox: false);

        // --- the list ----------------------------------------------------
        _list = new ListBox
        {
            Background = Brush("App.Control", Brushes.Black),
            Foreground = text,
            ItemTemplate = RowTemplate(text, dim),
        };
        _list.SelectionChanged += (_, _) => OnSelectionChanged();

        _preview = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("monospace"),
                [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
                [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
            },
            Brush("App.Panel", Brushes.Black),
            text);

        _status = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
        };

        // --- actions -----------------------------------------------------
        _rescan = new Button();
        _selectAll = new Button();
        _recover = new Button();
        _createTag = new Button();
        _createBranch = new Button();
        _saveToLostFound = new Button();

        // The caption MUST NOT be a bare string: a Button treats "_" in string content as
        // an access-key marker and swallows it, so "LOST_AND_FOUND" rendered as
        // "LOSTAND_FOUND" on screen. A TextBlock child is not access-key processed.
        // ApplyTranslations therefore writes into this TextBlock, never into Content.
        _deleteTags = new Button { Content = new TextBlock() };
        _prune = new Button();

        _rescan.Click += (_, _) => _ = RescanAsync();
        _selectAll.Click += (_, _) => ToggleSelectAll();
        _recover.Click += (_, _) => _ = RecoverSelectedAsync();
        _createTag.Click += (_, _) => _ = CreateRefAsync(asBranch: false);
        _createBranch.Click += (_, _) => _ = CreateRefAsync(asBranch: true);
        _saveToLostFound.Click += (_, _) => _ = SaveToLostFoundAsync();
        _deleteTags.Click += (_, _) => _ = DeleteTagsAsync();
        _prune.Click += (_, _) => _ = PruneAsync();

        _close = new Button { MinWidth = 90 };
        _close.Click += (_, _) => Close();

        // --- context menu (upstream's mnuLostObjects) --------------------
        // The menu is built once and only re-labelled, so its items are fields.
        _viewItem = new MenuItem();
        _copyHashItem = new MenuItem();
        _copyParentItem = new MenuItem();
        _saveAsItem = new MenuItem();
        _viewItem.Click += (_, _) => _ = PreviewSelectedAsync();
        _copyHashItem.Click += (_, _) => _ = CopyAsync(Selected?.Item.Hash);
        _copyParentItem.Click += (_, _) => _ = CopyAsync(Selected?.Item.Parent);
        _saveAsItem.Click += (_, _) => _ = SaveBlobAsAsync();

        ContextMenu menu = new();
        menu.Items.Add(_viewItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_copyHashItem);
        menu.Items.Add(_copyParentItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_saveAsItem);

        // Enablement mirrors upstream's Opening handler: "save as" is blob-only.
        menu.Opening += (_, _) =>
        {
            LostObjectEntry? row = Selected;
            _viewItem.IsEnabled = row is not null;
            _copyHashItem.IsEnabled = row is not null;
            _copyParentItem.IsEnabled = row?.Item.Parent.Length > 0;
            _saveAsItem.IsEnabled = row?.Item.Kind == LostObjectKind.Blob;
        };
        _list.ContextMenu = menu;

        // --- layout ------------------------------------------------------
        _heading = new TextBlock
        {
            Foreground = text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        _hint = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        WrapPanel options = new() { Margin = new Thickness(0, 12, 0, 0) };
        foreach (CheckBox box in new[] { _unreachable, _fullCheck, _noReflogs, _showCommitsAndTags, _showOtherObjects })
        {
            box.Margin = new Thickness(0, 0, 16, 6);
            options.Children.Add(box);
        }

        Grid header = new()
        {
            ColumnDefinitions = RowColumns(),
            Margin = new Thickness(6, 6, 6, 4),
        };
        // Column 0 holds the tick boxes and has no caption; it is still built through
        // the same helper so the header grid keeps one cell per column definition.
        _headerCells = [.. Enumerable.Range(0, 7).Select(c => AddHeader(header, c, dim))];

        WrapPanel actions = new() { Margin = new Thickness(0, 10, 0, 0) };
        foreach (Button b in new[] { _rescan, _selectAll, _recover, _createTag, _createBranch, _saveToLostFound, _deleteTags, _prune })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(b);
        }

        GridSplitter splitter = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };

        Grid middle = new()
        {
            RowDefinitions = new RowDefinitions("Auto,3*,Auto,2*"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(splitter, 2);
        Grid.SetRow(_preview, 3);
        middle.Children.Add(header);
        middle.Children.Add(_list);
        middle.Children.Add(splitter);
        middle.Children.Add(_preview);

        _close.HorizontalAlignment = HorizontalAlignment.Right;
        _close.Margin = new Thickness(0, 8, 0, 0);

        Grid root = new()
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto"),
        };
        Grid.SetRow(_heading, 0);
        Grid.SetRow(_hint, 1);
        Grid.SetRow(options, 2);
        Grid.SetRow(middle, 3);
        Grid.SetRow(actions, 4);
        Grid.SetRow(_status, 5);
        Grid.SetRow(_close, 6);
        root.Children.Add(_heading);
        root.Children.Add(_hint);
        root.Children.Add(options);
        root.Children.Add(middle);
        root.Children.Add(actions);
        root.Children.Add(_status);
        root.Children.Add(_close);

        Content = root;
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        Opened += (_, _) => _ = RescanAsync();
        UpdateButtons();
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        // Upstream's window is titled "Verify database"; the port renamed it after what
        // the dialog is FOR, so the id is given explicitly and the English literal is
        // the port's own wording.
        Title = T("FormVerify/$this.Text", "Recover lost objects");

        _heading.Text = T("Lost and unreachable objects");

        // The tag prefix is a git ref name and never translated, so it travels as an
        // argument rather than being concatenated into a translated fragment.
        _hint.Text = TranslationService.TFormat(
            key: null,
            "Tick the objects you want to recover, then press \"Recover selected objects\" to create "
            + "{0}* tags for them. A recovered object becomes reachable "
            + "again, so it survives the next garbage collection.",
            VerifyService.RecoveredTagPrefix);

        Caption(_unreachable, T("Show unreachable objects (--unreachable)"));
        ToolTip.SetTip(_unreachable, T(
            "FormVerify/Unreachable.Text",
            "Print out objects that exist but that aren't reachable from any of the reference nodes."));

        Caption(_fullCheck, T("Full check (--full)"));
        ToolTip.SetTip(_fullCheck, T(
            "FormVerify/FullCheck.Text",
            "Check not just objects in GIT_OBJECT_DIRECTORY, but also the ones found in alternate object pools."));

        Caption(_noReflogs, T("Ignore reflogs (--no-reflogs)"));
        ToolTip.SetTip(_noReflogs, T(
            "FormVerify/NoReflogs.Text",
            "Do not consider commits that are referenced only by an entry in a reflog to be reachable."));

        // Upstream says "annotated tags"; the port's filter also keeps lightweight ones,
        // so the caption is the port's and only the tooltip comes from upstream's id.
        Caption(_showCommitsAndTags, T("Show commits and tags"));
        ToolTip.SetTip(_showCommitsAndTags, T(
            "FormVerify/ShowCommitsAndTags.toolTip", "To recover unreachable commits or annotated tags"));
        Caption(_showOtherObjects, T("FormVerify/ShowOtherObjects.Text", "Show blobs and trees"));
        ToolTip.SetTip(_showOtherObjects, T(
            "FormVerify/ShowOtherObjects.toolTip",
            "To recover contents of files once staged but mistakenly deleted"));

        _headerCells[0].Text = string.Empty;
        _headerCells[1].Text = T("FormVerify/columnDate.HeaderText", "Date");
        _headerCells[2].Text = T("FormVerify/columnType.HeaderText", "Type");
        _headerCells[3].Text = T("FormVerify/columnSubject.HeaderText", "Subject");
        _headerCells[4].Text = T("FormVerify/columnAuthor.HeaderText", "Author");
        _headerCells[5].Text = T("FormVerify/columnHash.HeaderText", "Hash");
        _headerCells[6].Text = T("FormVerify/columnParent.HeaderText", "Parent");

        // Not the shell's "Refresh" id: this button re-runs git fsck rather than
        // reloading a view, and the two read differently once translated.
        _rescan.Content = T("Rescan");
        _selectAll.Content = T("Select all / none");
        _recover.Content = T("FormVerify/btnRestoreSelectedObjects.Text", "Recover selected objects");
        _createTag.Content = T("FormVerify/mnuLostObjectsCreateTag.Text", "Create tag") + "…";
        _createBranch.Content = T("FormVerify/mnuLostObjectsCreateBranch.Text", "Create branch") + "…";
        _saveToLostFound.Content = T("FormVerify/SaveObjects.Text", "Save objects to .git/lost-found");
        Caption(_deleteTags, T("FormVerify/DeleteAllLostAndFoundTags.Text", "Delete all LOST_AND_FOUND tags"));
        _prune.Content = T("FormVerify/Remove.Text", "Remove all dangling objects");
        _close.Content = T("TranslatedStrings/_closeText.Text", "Close");

        _viewItem.Header = T("FormVerify/mnuLostObjectView.Text", "View");
        _copyHashItem.Header = T("FormVerify/copyHashToolStripMenuItem.Text", "Copy object hash");
        _copyParentItem.Header = T("FormVerify/copyParentHashToolStripMenuItem.Text", "Copy parent hash");
        _saveAsItem.Header = T("FormVerify/saveAsToolStripMenuItem.Text", "Save as…");

        if (_previewIsPlaceholder)
        {
            _preview.Text = T("Select an object to preview it.");
        }
    }

    // The Delete-tags button carries a TextBlock rather than a string (see its
    // construction), so its caption cannot be written through Content.
    private static void Caption(ContentControl control, string caption)
    {
        if (control.Content is TextBlock block)
        {
            block.Text = caption;
            return;
        }

        control.Content = caption;
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    /// <summary>Shows the dialog modally over <paramref name="owner"/>.</summary>
    public static async Task<bool> ShowAsync(Window owner, string repoPath)
    {
        VerifyDialog dialog = new(repoPath);
        await dialog.ShowDialog(owner);
        return dialog.Changed;
    }

    // Shared geometry for the header and every row, so the columns line up.
    private static ColumnDefinitions RowColumns() => new("28,150,150,3*,140,110,110");

    private static TextBlock AddHeader(Grid grid, int column, IBrush brush)
    {
        TextBlock cell = new()
        {
            Foreground = brush,
            FontWeight = FontWeight.SemiBold,
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
        return cell;
    }

    private static CheckBox OptionBox(bool isChecked, IBrush foreground) => new()
    {
        IsChecked = isChecked,
        Foreground = foreground,
    };

    // NOTE: null-tolerant on purpose — Avalonia re-invokes the template with a null item
    // when it empties a recycled container (the M51 crash in BlameView).
    private static FuncDataTemplate<LostObjectEntry> RowTemplate(IBrush text, IBrush dim)
        => new((entry, _) =>
        {
            Grid grid = new() { ColumnDefinitions = RowColumns() };

            CheckBox tick = new()
            {
                IsChecked = entry?.IsSelected == true,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // Write straight back into the bound row: a recycled container gets a new
            // DataContext but keeps this visual, so storing the tick anywhere else would
            // make it drift on scroll.
            if (entry is not null)
            {
                tick.IsCheckedChanged += (_, _) => entry.IsSelected = tick.IsChecked == true;
            }

            LostObject? item = entry?.Item;
            Grid.SetColumn(tick, 0);
            grid.Children.Add(tick);

            AddCell(grid, 1, item?.Date?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty, item?.Date is null ? dim : text);
            AddCell(grid, 2, item?.RawType ?? string.Empty, text);
            AddCell(grid, 3, item?.Subject ?? string.Empty, text);
            AddCell(grid, 4, item?.Author ?? string.Empty, text);
            AddCell(grid, 5, item?.ShortHash ?? string.Empty, dim);
            AddCell(grid, 6, item?.Parent is { Length: > 10 } p ? p[..10] : item?.Parent ?? string.Empty, dim);
            return grid;
        });

    private static void AddCell(Grid grid, int column, string value, IBrush brush)
    {
        TextBlock cell = new()
        {
            Text = value,
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private LostObjectEntry? Selected => _list.SelectedItem as LostObjectEntry;

    private VerifyOptions CurrentOptions => new(
        Unreachable: _unreachable.IsChecked == true,
        FullCheck: _fullCheck.IsChecked == true,
        NoReflogs: _noReflogs.IsChecked == true);

    // --- scanning ---------------------------------------------------------

    private async Task RescanAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _status.Text = T("Running git fsck…");

        VerifyOptions options = CurrentOptions;
        VerifyScanResult result = await Task.Run(() => _service.Scan(_repoPath, options));
        int tags = await Task.Run(() => _service.CountRecoveredTags(_repoPath));

        _all = [.. result.Objects.Select(o => new LostObjectEntry(o))];
        ApplyFilter(cameFromCommitsBox: true, silent: true);

        // git's own stderr is passed through untranslated — it is program output, not
        // a caption of ours.
        _status.Text = result.Success
            ? TF(
                "{0} object(s) reported by git fsck; {1} shown. {2} {3}* tag(s) in the repository.",
                result.Objects.Count,
                _list.ItemCount,
                tags,
                VerifyService.RecoveredTagPrefix)
            : TF("git fsck failed: {0}", result.Output);

        _deleteTags.IsEnabled = tags > 0;
        SetBusy(false);
    }

    // Client-side filtering, exactly upstream's IsMatchToFilter. The guard keeps at
    // least one of the two boxes ticked, re-ticking the OTHER one when the user clears
    // the one they just touched.
    private void ApplyFilter(bool cameFromCommitsBox, bool silent = false)
    {
        bool commits = _showCommitsAndTags.IsChecked == true;
        bool others = _showOtherObjects.IsChecked == true;

        if (!commits && !others)
        {
            if (cameFromCommitsBox)
            {
                _showOtherObjects.IsChecked = true;
                others = true;
            }
            else
            {
                _showCommitsAndTags.IsChecked = true;
                commits = true;
            }
        }

        List<LostObjectEntry> shown =
        [
            .. _all.Where(e => e.Item.Kind is LostObjectKind.Commit or LostObjectKind.Tag ? commits : others),
        ];

        // A NEW list instance: re-assigning the same one leaves realised containers
        // showing stale visuals (the M50 virtualisation trap).
        _list.ItemsSource = shown;
        OnSelectionChanged();

        if (!silent)
        {
            _status.Text = TF("{0} of {1} object(s) shown.", shown.Count, _all.Count);
        }
    }

    private void OnSelectionChanged()
    {
        UpdateButtons();
        _ = PreviewSelectedAsync();
    }

    private async Task PreviewSelectedAsync()
    {
        if (Selected is not { } row)
        {
            // Deliberately LEAVE the pane alone. Every action ends with a rescan, which
            // drops the selection and used to land here — wiping the recovery / prune /
            // lost-found report the user had just triggered and replacing it with the
            // placeholder. The initial placeholder is set in the constructor instead.
            return;
        }

        string hash = row.Item.Hash;
        string content = await Task.Run(() => _service.ShowObject(_repoPath, hash));
        if (Selected?.Item.Hash == hash)
        {
            _previewIsPlaceholder = false;
            _preview.Text = content.Length == 0 ? T("(git show produced no output)") : content;
        }
    }

    private void ToggleSelectAll()
    {
        if (_list.ItemsSource is not IEnumerable<LostObjectEntry> rows)
        {
            return;
        }

        List<LostObjectEntry> list = [.. rows];
        bool target = list.Any(r => !r.IsSelected);
        foreach (LostObjectEntry row in list)
        {
            row.IsSelected = target;
        }

        // Rebind so the row check boxes redraw (see the M50 note in ApplyFilter).
        _list.ItemsSource = new List<LostObjectEntry>(list);
        _status.Text = target ? TF("{0} object(s) ticked.", list.Count) : T("Selection cleared.");
    }

    // --- actions ----------------------------------------------------------

    private async Task RecoverSelectedAsync()
    {
        List<LostObject> picked = _list.ItemsSource is IEnumerable<LostObjectEntry> rows
            ? [.. rows.Where(r => r.IsSelected).Select(r => r.Item)]
            : [];

        if (picked.Count == 0)
        {
            _status.Text = T("FormVerify/_selectLostObjectsToRestoreMessage.Text", "Select objects to restore.");
            return;
        }

        SetBusy(true);
        MaintenanceResult result = await Task.Run(() => _service.RecoverAsTags(_repoPath, picked));
        SetBusy(false);

        ShowOutput(result.Output);
        if (result.Success)
        {
            Changed = true;
        }

        _status.Text = result.Success
            ? TF("Recovered {0} object(s) as {1}* tags.", picked.Count, VerifyService.RecoveredTagPrefix)
            : T("Recovery failed — see the pane above.");

        await RescanAsync();
    }

    private async Task CreateRefAsync(bool asBranch)
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (asBranch && !row.Item.CanBecomeBranch)
        {
            _status.Text = T("Only a commit can become a branch.");
            return;
        }

        // Two whole sentences per kind rather than one with a "branch"/"tag" hole:
        // languages inflect the rest of the sentence around that noun, and a hole
        // leaves the translator no way to follow.
        string? name = await PromptAsync(
            asBranch
                ? TF("Name of the recovery branch for {0}:", row.Item.ShortHash)
                : TF("Name of the recovery tag for {0}:", row.Item.ShortHash),
            $"{VerifyService.RecoveredTagPrefix}{row.Item.ShortHash}");

        if (name is not { Length: > 0 } chosen)
        {
            return;
        }

        string hash = row.Item.Hash;
        SetBusy(true);
        MaintenanceResult result = await Task.Run(() => asBranch
            ? _service.CreateBranchAt(_repoPath, chosen, hash)
            : _service.CreateTagAt(_repoPath, chosen, hash));
        SetBusy(false);

        ShowOutput(result.Output);
        if (result.Success)
        {
            Changed = true;
            _status.Text = asBranch
                ? TF("Created branch '{0}' at {1}.", chosen, row.Item.ShortHash)
                : TF("Created tag '{0}' at {1}.", chosen, row.Item.ShortHash);
            await RescanAsync();
        }
        else
        {
            _status.Text = asBranch ? T("Could not create the branch.") : T("Could not create the tag.");
        }
    }

    private async Task SaveToLostFoundAsync()
    {
        SetBusy(true);
        VerifyOptions options = CurrentOptions;
        MaintenanceResult result = await Task.Run(() => _service.SaveObjectsToLostFound(_repoPath, options));
        SetBusy(false);

        ShowOutput(result.Output);
        _status.Text = result.Success
            ? T("Objects written to .git/lost-found.")
            : T("Saving to .git/lost-found failed.");
        await RescanAsync();
    }

    private async Task DeleteTagsAsync()
    {
        SetBusy(true);
        MaintenanceResult result = await Task.Run(() => _service.DeleteRecoveredTags(_repoPath));
        SetBusy(false);

        ShowOutput(result.Output);
        Changed = true;
        _status.Text = TF("{0}* tags removed.", VerifyService.RecoveredTagPrefix);
        await RescanAsync();
    }

    private async Task PruneAsync()
    {
        // Upstream confirms before `git prune` — it is irreversible.
        if (!await ConfirmAsync(
            T("FormVerify/_removeDanglingObjectsQuestion.Text", "Are you sure you want to delete all dangling objects?")
            + "\n\n"
            + T("TranslatedStrings/_cannotBeUndone.Text", "This action cannot be undone.")))
        {
            return;
        }

        SetBusy(true);
        MaintenanceResult result = await Task.Run(() => _service.PruneDanglingObjects(_repoPath));
        SetBusy(false);

        ShowOutput(result.Output);
        Changed = true;
        _status.Text = result.Success ? T("Dangling objects pruned.") : T("git prune failed.");
        await RescanAsync();
    }

    private async Task SaveBlobAsAsync()
    {
        if (Selected is not { Item.Kind: LostObjectKind.Blob } row)
        {
            return;
        }

        try
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Save the lost blob as"),
                SuggestedFileName = $"{row.Item.Hash}_LOST_FOUND.txt",
            });

            if (file?.TryGetLocalPath() is { Length: > 0 } path)
            {
                // The service's own report, already a sentence; left as it comes.
                MaintenanceResult result = await Task.Run(() => _service.SaveBlobAs(_repoPath, row.Item.Hash, path));
                _status.Text = result.Output;
            }
        }
        catch (Exception ex)
        {
            _status.Text = TF("Could not save the blob: {0}", ex.Message);
        }
    }

    private async Task CopyAsync(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return;
        }

        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(value);
            _status.Text = TF("Copied {0}.", value);
        }
    }

    // --- plumbing ---------------------------------------------------------

    // git's output, verbatim. Also marks the preview pane as no longer holding the
    // placeholder, so a later language switch cannot overwrite this report.
    private void ShowOutput(string output)
    {
        _previewIsPlaceholder = false;
        _preview.Text = output;
    }

    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(key: null, englishFormat, args);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool idle = !_busy;
        LostObjectEntry? row = Selected;

        _rescan.IsEnabled = idle;
        _selectAll.IsEnabled = idle && _list.ItemCount > 0;
        _recover.IsEnabled = idle && _list.ItemCount > 0;
        _saveToLostFound.IsEnabled = idle;
        _prune.IsEnabled = idle;
        _createTag.IsEnabled = idle && row is not null;

        // Greyed rather than absent for a non-commit: the button stays discoverable and
        // its precondition is visible, instead of failing after the fact.
        _createBranch.IsEnabled = idle && row?.Item.CanBecomeBranch == true;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Confirm"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Confirm"),
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task<string?> PromptAsync(string message, string initial)
    {
        TaskCompletionSource<string?> tcs = new();

        TextBox input = new() { Text = initial };
        Button ok = new() { Content = T("TranslatedStrings/_okText.Text", "OK"), Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            // These two throw-away windows live for one interaction, so they are simply
            // built in the language in force at the time; there is nothing to re-label.
            Title = T("Recover lost object"),
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        ok.Click += (_, _) => { tcs.TrySetResult(input.Text?.Trim()); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                tcs.TrySetResult(input.Text?.Trim());
                dialog.Close();
                e.Handled = true;
            }
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;
}
