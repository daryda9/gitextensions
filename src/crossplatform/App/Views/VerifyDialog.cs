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
public sealed class VerifyDialog : Window
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

    private IReadOnlyList<LostObjectEntry> _all = [];
    private bool _busy;

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

        Title = "Recover lost objects";
        Width = 1040;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // --- options (upstream's five check boxes, same defaults) ---------
        _unreachable = OptionBox(
            "Show unreachable objects (--unreachable)",
            false,
            text,
            "Print out objects that exist but that aren't reachable from any of the reference nodes.");
        _fullCheck = OptionBox(
            "Full check (--full)",
            false,
            text,
            "Check not just objects in GIT_OBJECT_DIRECTORY, but also the ones found in alternate object pools.");
        _noReflogs = OptionBox(
            "Ignore reflogs (--no-reflogs)",
            true,
            text,
            "Do not consider commits that are referenced only by an entry in a reflog to be reachable.");

        _showCommitsAndTags = OptionBox("Show commits and tags", true, text, null);
        _showOtherObjects = OptionBox("Show blobs and trees", false, text, null);

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
                Text = "Select an object to preview it.",
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
        _rescan = new Button { Content = "Rescan" };
        _selectAll = new Button { Content = "Select all / none" };
        _recover = new Button { Content = "Recover selected objects" };
        _createTag = new Button { Content = "Create tag…" };
        _createBranch = new Button { Content = "Create branch…" };
        _saveToLostFound = new Button { Content = "Save objects to .git/lost-found" };
        _deleteTags = new Button { Content = "Delete all LOST_AND_FOUND tags" };
        _prune = new Button { Content = "Remove all dangling objects" };

        _rescan.Click += (_, _) => _ = RescanAsync();
        _selectAll.Click += (_, _) => ToggleSelectAll();
        _recover.Click += (_, _) => _ = RecoverSelectedAsync();
        _createTag.Click += (_, _) => _ = CreateRefAsync(asBranch: false);
        _createBranch.Click += (_, _) => _ = CreateRefAsync(asBranch: true);
        _saveToLostFound.Click += (_, _) => _ = SaveToLostFoundAsync();
        _deleteTags.Click += (_, _) => _ = DeleteTagsAsync();
        _prune.Click += (_, _) => _ = PruneAsync();

        Button close = new() { Content = "Close", MinWidth = 90 };
        close.Click += (_, _) => Close();

        // --- context menu (upstream's mnuLostObjects) --------------------
        MenuItem view = new() { Header = "View" };
        MenuItem copyHash = new() { Header = "Copy object hash" };
        MenuItem copyParent = new() { Header = "Copy parent hash" };
        MenuItem saveAs = new() { Header = "Save as…" };
        view.Click += (_, _) => _ = PreviewSelectedAsync();
        copyHash.Click += (_, _) => _ = CopyAsync(Selected?.Item.Hash);
        copyParent.Click += (_, _) => _ = CopyAsync(Selected?.Item.Parent);
        saveAs.Click += (_, _) => _ = SaveBlobAsAsync();

        ContextMenu menu = new();
        menu.Items.Add(view);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyHash);
        menu.Items.Add(copyParent);
        menu.Items.Add(new Separator());
        menu.Items.Add(saveAs);

        // Enablement mirrors upstream's Opening handler: "save as" is blob-only.
        menu.Opening += (_, _) =>
        {
            LostObjectEntry? row = Selected;
            view.IsEnabled = row is not null;
            copyHash.IsEnabled = row is not null;
            copyParent.IsEnabled = row?.Item.Parent.Length > 0;
            saveAs.IsEnabled = row?.Item.Kind == LostObjectKind.Blob;
        };
        _list.ContextMenu = menu;

        // --- layout ------------------------------------------------------
        TextBlock heading = new()
        {
            Text = "Lost and unreachable objects",
            Foreground = text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        TextBlock hint = new()
        {
            Text = "Tick the objects you want to recover, then press \"Recover selected objects\" to create "
                 + $"{VerifyService.RecoveredTagPrefix}* tags for them. A recovered object becomes reachable "
                 + "again, so it survives the next garbage collection.",
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
        AddHeader(header, 0, string.Empty, dim);
        AddHeader(header, 1, "Date", dim);
        AddHeader(header, 2, "Type", dim);
        AddHeader(header, 3, "Subject", dim);
        AddHeader(header, 4, "Author", dim);
        AddHeader(header, 5, "Hash", dim);
        AddHeader(header, 6, "Parent", dim);

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

        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 8, 0, 0);

        Grid root = new()
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto"),
        };
        Grid.SetRow(heading, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(options, 2);
        Grid.SetRow(middle, 3);
        Grid.SetRow(actions, 4);
        Grid.SetRow(_status, 5);
        Grid.SetRow(close, 6);
        root.Children.Add(heading);
        root.Children.Add(hint);
        root.Children.Add(options);
        root.Children.Add(middle);
        root.Children.Add(actions);
        root.Children.Add(_status);
        root.Children.Add(close);

        Content = root;
        DialogKeys.InstallEscapeClose(this);

        Opened += (_, _) => _ = RescanAsync();
        UpdateButtons();
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>.</summary>
    public static async Task<bool> ShowAsync(Window owner, string repoPath)
    {
        VerifyDialog dialog = new(repoPath);
        await dialog.ShowDialog(owner);
        return dialog.Changed;
    }

    // Shared geometry for the header and every row, so the columns line up.
    private static ColumnDefinitions RowColumns() => new("28,150,150,3*,140,110,110");

    private static void AddHeader(Grid grid, int column, string caption, IBrush brush)
    {
        TextBlock cell = new()
        {
            Text = caption,
            Foreground = brush,
            FontWeight = FontWeight.SemiBold,
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static CheckBox OptionBox(string caption, bool isChecked, IBrush foreground, string? tooltip)
    {
        CheckBox box = new()
        {
            Content = caption,
            IsChecked = isChecked,
            Foreground = foreground,
        };

        if (tooltip is not null)
        {
            ToolTip.SetTip(box, tooltip);
        }

        return box;
    }

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
        _status.Text = "Running git fsck…";

        VerifyOptions options = CurrentOptions;
        VerifyScanResult result = await Task.Run(() => _service.Scan(_repoPath, options));
        int tags = await Task.Run(() => _service.CountRecoveredTags(_repoPath));

        _all = [.. result.Objects.Select(o => new LostObjectEntry(o))];
        ApplyFilter(cameFromCommitsBox: true, silent: true);

        _status.Text = result.Success
            ? $"{result.Objects.Count} object(s) reported by git fsck; {_list.ItemCount} shown. "
              + $"{tags} {VerifyService.RecoveredTagPrefix}* tag(s) in the repository."
            : $"git fsck failed: {result.Output}";

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
            _status.Text = $"{shown.Count} of {_all.Count} object(s) shown.";
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
            _preview.Text = "Select an object to preview it.";
            return;
        }

        string hash = row.Item.Hash;
        string content = await Task.Run(() => _service.ShowObject(_repoPath, hash));
        if (Selected?.Item.Hash == hash)
        {
            _preview.Text = content.Length == 0 ? "(git show produced no output)" : content;
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
        _status.Text = target ? $"{list.Count} object(s) ticked." : "Selection cleared.";
    }

    // --- actions ----------------------------------------------------------

    private async Task RecoverSelectedAsync()
    {
        List<LostObject> picked = _list.ItemsSource is IEnumerable<LostObjectEntry> rows
            ? [.. rows.Where(r => r.IsSelected).Select(r => r.Item)]
            : [];

        if (picked.Count == 0)
        {
            _status.Text = "Select objects to restore.";
            return;
        }

        SetBusy(true);
        MaintenanceResult result = await Task.Run(() => _service.RecoverAsTags(_repoPath, picked));
        SetBusy(false);

        _preview.Text = result.Output;
        if (result.Success)
        {
            Changed = true;
        }

        _status.Text = result.Success
            ? $"Recovered {picked.Count} object(s) as {VerifyService.RecoveredTagPrefix}* tags."
            : "Recovery failed — see the pane above.";

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
            _status.Text = "Only a commit can become a branch.";
            return;
        }

        string kind = asBranch ? "branch" : "tag";
        string? name = await PromptAsync(
            $"Name of the recovery {kind} for {row.Item.ShortHash}:",
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

        _preview.Text = result.Output;
        if (result.Success)
        {
            Changed = true;
            _status.Text = $"Created {kind} '{chosen}' at {row.Item.ShortHash}.";
            await RescanAsync();
        }
        else
        {
            _status.Text = $"Could not create the {kind}.";
        }
    }

    private async Task SaveToLostFoundAsync()
    {
        SetBusy(true);
        VerifyOptions options = CurrentOptions;
        MaintenanceResult result = await Task.Run(() => _service.SaveObjectsToLostFound(_repoPath, options));
        SetBusy(false);

        _preview.Text = result.Output;
        _status.Text = result.Success ? "Objects written to .git/lost-found." : "Saving to .git/lost-found failed.";
        await RescanAsync();
    }

    private async Task DeleteTagsAsync()
    {
        SetBusy(true);
        MaintenanceResult result = await Task.Run(() => _service.DeleteRecoveredTags(_repoPath));
        SetBusy(false);

        _preview.Text = result.Output;
        Changed = true;
        _status.Text = $"{VerifyService.RecoveredTagPrefix}* tags removed.";
        await RescanAsync();
    }

    private async Task PruneAsync()
    {
        // Upstream confirms before `git prune` — it is irreversible.
        if (!await ConfirmAsync("Are you sure you want to delete all dangling objects?\n\nThis cannot be undone."))
        {
            return;
        }

        SetBusy(true);
        MaintenanceResult result = await Task.Run(() => _service.PruneDanglingObjects(_repoPath));
        SetBusy(false);

        _preview.Text = result.Output;
        Changed = true;
        _status.Text = result.Success ? "Dangling objects pruned." : "git prune failed.";
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
                Title = "Save the lost blob as",
                SuggestedFileName = $"{row.Item.Hash}_LOST_FOUND.txt",
            });

            if (file?.TryGetLocalPath() is { Length: > 0 } path)
            {
                MaintenanceResult result = await Task.Run(() => _service.SaveBlobAs(_repoPath, row.Item.Hash, path));
                _status.Text = result.Output;
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not save the blob: {ex.Message}";
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
            _status.Text = $"Copied {value}.";
        }
    }

    // --- plumbing ---------------------------------------------------------

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

        Button yes = new() { Content = "Confirm", Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Confirm",
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
        Button ok = new() { Content = "OK", Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Recover lost object",
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
