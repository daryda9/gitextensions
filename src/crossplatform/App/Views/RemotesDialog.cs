using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Remotes manager" for the Avalonia port, mirroring upstream's
///  <c>FormRemotes</c>. Two tabs:
///
///  <list type="bullet">
///   <item><b>Remotes</b> — the configured remotes (name + fetch URL) with
///    Add / Rename / Remove, plus a details pane that edits the selected remote's
///    fetch URL and its SEPARATE PUSH URL (<c>remote.&lt;name&gt;.pushurl</c>).</item>
///   <item><b>Default pull behavior (fetch &amp; merge)</b> — upstream's
///    <c>tabPage2</c>: per local branch, which remote it pulls from
///    (<c>branch.&lt;x&gt;.remote</c>) and which remote branch it merges with
///    (<c>branch.&lt;x&gt;.merge</c>), with a Save-changes button that also
///    re-reads the configuration.</item>
///  </list>
///
///  <para>Every git call goes through <see cref="RemoteService"/> on a
///  <see cref="Task.Run"/> — the service blocks on async work, so calling it from the
///  UI thread deadlocks — and UI mutations come back via
///  <see cref="Dispatcher.UIThread"/>. <see cref="Changed"/> is set when any mutation
///  succeeds so the caller can refresh the repository tree afterwards.</para>
///
///  <para><b>Not ported, deliberately:</b> upstream's PuTTY panel
///  (<c>remote.&lt;name&gt;.puttykeyfile</c>, "Load SSH key", "Test connection"). It is
///  shown only when <c>GitSshHelpers.IsPlink</c> — i.e. when the configured ssh command
///  ends in <c>plink.exe</c> / <c>TortoisePlink.exe</c> (<c>FormRemotes.cs:399</c>,
///  <c>GitSshHelpers.cs:13</c>) — which never holds on Linux, where ssh is
///  key/agent-based. Upstream hides the whole panel in exactly this situation, so
///  omitting it IS the upstream behaviour, not a gap.</para>
/// </summary>
public sealed class RemotesDialog : Theming.ZoomWindow
{
    private readonly RemoteService _service = new();
    private readonly string _repoPath;

    // --- Tab 1: remotes
    private readonly ListBox _list;
    private readonly Button _add;
    private readonly Button _rename;
    private readonly Button _remove;
    private readonly TextBlock _fetchUrlLabel;
    private readonly TextBox _fetchUrl;
    private readonly CheckBox _useSeparatePushUrl;
    private readonly TextBlock _pushUrlLabel;
    private readonly TextBox _pushUrl;
    private readonly Button _pushUrlBrowse;
    private readonly Button _saveRemote;

    // --- Tab 2: default pull behavior
    private readonly ListBox _branches;
    private readonly TextBox _localBranch;
    private readonly ComboBox _trackingRemote;
    private readonly AutoCompleteBox _mergeWith;
    private readonly Button _saveBranch;
    private readonly TextBlock _branchStatus;
    private readonly TextBlock _behaviorHint;
    private readonly TextBlock _branchNameHeader;
    private readonly TextBlock _remoteHeader;
    private readonly TextBlock _mergeWithHeader;
    private readonly TextBlock _localBranchLabel;
    private readonly TextBlock _remoteLabel;
    private readonly TextBlock _mergeWithLabel;

    // --- Shell
    private readonly TabItem _remotesTab;
    private readonly TabItem _behaviorTab;
    private readonly Button _close;

    private bool _busy;
    private bool _suppressRemoteSelection;

    /// <summary>
    ///  True when at least one add/rename/set-url/remove/config write succeeded, so the
    ///  owner can refresh its view once the dialog is dismissed.
    /// </summary>
    public bool Changed { get; private set; }

    public RemotesDialog(string repoPath)
    {
        _repoPath = repoPath;

        IBrush text = Brush("App.Text", Brushes.Gainsboro);
        IBrush dim = Brush("App.TextDim", Brushes.Gray);

        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Panel", Brushes.DimGray);

        // ================= Tab 1: Remotes =================

        _list = new ListBox
        {
            Background = Brush("App.Control", Brushes.Black),
            Foreground = text,
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (!_suppressRemoteSelection)
            {
                LoadRemoteDetails();
            }
        };

        _add = MakeButton();
        _rename = MakeButton();
        _remove = MakeButton();

        _add.Click += (_, _) => _ = DoAddAsync();
        _rename.Click += (_, _) => _ = DoRenameAsync();
        _remove.Click += (_, _) => _ = DoRemoveAsync();

        _fetchUrl = new TextBox { Foreground = text };

        // Upstream's "Sep&arate Push Url" check box: ticked iff remote.<n>.pushurl
        // exists (FormRemotes.cs:768-769), and it HIDES the URL row when unticked
        // (ShowSeparatePushUrl, FormRemotes.cs:787-806).
        _useSeparatePushUrl = new CheckBox { Foreground = text };
        _useSeparatePushUrl.IsCheckedChanged += (_, _) => ApplySeparatePushUrlVisibility();

        _pushUrlLabel = new TextBlock
        {
            Foreground = dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _pushUrl = new TextBox { Foreground = text };
        _pushUrlBrowse = MakeButton();
        _pushUrlBrowse.Width = double.NaN;
        _pushUrlBrowse.Click += (_, _) => _ = BrowseForPushUrlAsync();

        _saveRemote = MakeButton();
        _saveRemote.Width = double.NaN;
        _saveRemote.HorizontalAlignment = HorizontalAlignment.Right;
        _saveRemote.Click += (_, _) => _ = DoSaveRemoteAsync();

        StackPanel remoteButtons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,

            // MinWidth, not Width: the Italian captions ("Rinomina…", "Aggiungi…") are
            // longer than the English ones, and a hard width would clip them instead of
            // letting this Auto-sized column grow.
            MinWidth = 120,
            Margin = new Thickness(10, 0, 0, 0),
        };
        remoteButtons.Children.Add(_add);
        remoteButtons.Children.Add(_rename);
        remoteButtons.Children.Add(_remove);

        Grid details = new()
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
        };

        _fetchUrlLabel = new TextBlock
        {
            Foreground = dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 6),
        };
        Grid.SetRow(_fetchUrlLabel, 0);
        Grid.SetColumn(_fetchUrlLabel, 0);
        _fetchUrl.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(_fetchUrl, 0);
        Grid.SetColumn(_fetchUrl, 1);
        Grid.SetColumnSpan(_fetchUrl, 2);

        Grid.SetRow(_useSeparatePushUrl, 1);
        Grid.SetColumn(_useSeparatePushUrl, 1);
        Grid.SetColumnSpan(_useSeparatePushUrl, 2);

        Grid.SetRow(_pushUrlLabel, 2);
        Grid.SetColumn(_pushUrlLabel, 0);
        _pushUrl.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_pushUrl, 2);
        Grid.SetColumn(_pushUrl, 1);
        _pushUrlBrowse.Margin = new Thickness(8, 6, 0, 0);
        Grid.SetRow(_pushUrlBrowse, 2);
        Grid.SetColumn(_pushUrlBrowse, 2);

        _saveRemote.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(_saveRemote, 3);
        Grid.SetColumn(_saveRemote, 1);
        Grid.SetColumnSpan(_saveRemote, 2);

        details.Children.Add(_fetchUrlLabel);
        details.Children.Add(_fetchUrl);
        details.Children.Add(_useSeparatePushUrl);
        details.Children.Add(_pushUrlLabel);
        details.Children.Add(_pushUrl);
        details.Children.Add(_pushUrlBrowse);
        details.Children.Add(_saveRemote);

        Grid remotesBody = new()
        {
            Margin = new Thickness(12),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        Grid.SetRow(_list, 0);
        Grid.SetColumn(_list, 0);
        Grid.SetRow(remoteButtons, 0);
        Grid.SetColumn(remoteButtons, 1);
        Grid.SetRow(details, 1);
        Grid.SetColumn(details, 0);
        Grid.SetColumnSpan(details, 2);
        remotesBody.Children.Add(_list);
        remotesBody.Children.Add(remoteButtons);
        remotesBody.Children.Add(details);

        // ================= Tab 2: Default pull behavior =================

        _branches = new ListBox
        {
            Background = Brush("App.Control", Brushes.Black),
            Foreground = text,
            ItemTemplate = BranchRowTemplate(text, dim),
        };
        _branches.SelectionChanged += (_, _) => LoadBranchDetails();

        _localBranch = new TextBox
        {
            IsReadOnly = true,
            Foreground = text,
        };

        // A closed set: "" (no remote) plus every configured remote, exactly like
        // upstream's RemoteRepositoryCombo, which prepends an empty ConfigFileRemote
        // to the remote list (FormRemotes.cs:344-346).
        _trackingRemote = new ComboBox { Foreground = text, MinWidth = 160 };
        _trackingRemote.SelectionChanged += (_, _) => _ = RefreshMergeWithCandidatesAsync();

        // Upstream's DefaultMergeWithCombo is an EDITABLE combo (pick a fetched remote
        // branch or type a name), so AutoCompleteBox is the faithful control here —
        // the same choice CheckoutBranchForm makes for upstream's Branches combo.
        _mergeWith = new AutoCompleteBox
        {
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,
            MaxDropDownHeight = 220,
            MinWidth = 200,
        };

        _saveBranch = MakeButton();
        _saveBranch.Width = double.NaN;
        _saveBranch.Click += (_, _) => _ = DoSaveBranchAsync();

        _branchStatus = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        Grid branchHeader = new()
        {
            ColumnDefinitions = BranchColumns(),
            Margin = new Thickness(6, 0, 6, 4),
        };
        _branchNameHeader = AddHeaderCell(branchHeader, 0, dim);
        _remoteHeader = AddHeaderCell(branchHeader, 1, dim);
        _mergeWithHeader = AddHeaderCell(branchHeader, 2, dim);

        Grid branchEditor = new()
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
        };
        _localBranchLabel = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _remoteLabel = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        _mergeWithLabel = new TextBlock { Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
        Grid.SetColumn(_localBranchLabel, 0);
        Grid.SetColumn(_localBranch, 1);
        Grid.SetColumn(_remoteLabel, 2);
        Grid.SetColumn(_trackingRemote, 3);
        Grid.SetColumn(_mergeWithLabel, 4);
        Grid.SetColumn(_mergeWith, 5);
        branchEditor.Children.Add(_localBranchLabel);
        branchEditor.Children.Add(_localBranch);
        branchEditor.Children.Add(_remoteLabel);
        branchEditor.Children.Add(_trackingRemote);
        branchEditor.Children.Add(_mergeWithLabel);
        branchEditor.Children.Add(_mergeWith);

        StackPanel branchActions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        branchActions.Children.Add(_saveBranch);

        // Rows: hint, column header, the list (takes the slack), the editor, a status line.
        Grid behaviorBody = new()
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
        };
        _behaviorHint = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(_behaviorHint, 0);
        Grid.SetRow(branchHeader, 1);
        Grid.SetRow(_branches, 2);
        Grid.SetRow(branchEditor, 3);
        Grid.SetRow(_branchStatus, 4);
        behaviorBody.Children.Add(_behaviorHint);
        behaviorBody.Children.Add(branchHeader);
        behaviorBody.Children.Add(_branches);
        behaviorBody.Children.Add(branchEditor);
        behaviorBody.Children.Add(_branchStatus);

        Grid behaviorRoot = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(behaviorBody, 0);
        Grid.SetRow(branchActions, 1);
        branchActions.Margin = new Thickness(12, 0, 12, 12);
        behaviorRoot.Children.Add(behaviorBody);
        behaviorRoot.Children.Add(branchActions);

        // ================= Shell =================

        _remotesTab = new TabItem { Content = remotesBody };
        _behaviorTab = new TabItem { Content = behaviorRoot };
        TabControl tabs = new();
        tabs.Items.Add(_remotesTab);
        tabs.Items.Add(_behaviorTab);

        _close = MakeButton();
        _close.Width = double.NaN;
        _close.MinWidth = 90;
        _close.HorizontalAlignment = HorizontalAlignment.Right;
        _close.Margin = new Thickness(12, 0, 12, 12);
        _close.Click += (_, _) => Close();

        Grid root = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(tabs, 0);
        Grid.SetRow(_close, 1);
        root.Children.Add(tabs);
        root.Children.Add(_close);

        // Escape = Close, like every WinForms dialog upstream (their CancelButton).
        // Bubbling phase, so an open context menu or the inline prompts get the key
        // first. Close() alone leaves <see cref="Changed"/> as it stands, so a caller
        // that refreshes on Changed still refreshes after an Escape.
        KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        };

        Content = root;
        DialogKeys.EnsureFocusRoute(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        Opened += (_, _) => ReloadAll();
        UpdateButtons();
        ApplySeparatePushUrlVisibility();
    }

    // --- Translations -----------------------------------------------------

    // A language switch re-labels the chrome AND re-reads both lists: the branch rows
    // carry translated prose of their own ("(none)") inside a data template, which only
    // a fresh item collection re-renders. Re-reading git config is cheap and a language
    // switch is rare, so this is preferred over hand-walking the containers.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        ApplyTranslations();
        ReloadAll();
    });

    private void ApplyTranslations()
    {
        // Upstream's window is "Remote repositories"; the port's shorter "Remotes" is
        // only the English literal, the id decides what a translation says.
        Title = T("FormRemotes/$this.Text", "Remotes");
        _remotesTab.Header = T("FormRemotes/tabPage1.Text", "Remotes");
        _behaviorTab.Header = T("FormRemotes/tabPage2.Text", "Default pull behavior (fetch & merge)");

        // No upstream counterpart for the three list buttons: FormRemotes drives its
        // remote list from a toolbar whose captions are tooltips on icons
        // (_btnNewTooltip / _btnDeleteTooltip), which are sentences, not button labels.
        // The single-argument overload finds the plain words by source text instead.
        _add.Content = T("Add…");
        _rename.Content = T("FormQuickGitRefSelector/_actionRename.Text", "Rename…");
        _remove.Content = T("Remove");
        _close.Content = T("TranslatedStrings/_closeText.Text", "Close");

        _fetchUrlLabel.Text = T("FormRemotes/_labelUrlAsFetch.Text", "Fetch URL");
        _useSeparatePushUrl.Content = T("FormRemotes/checkBoxSepPushUrl.Text", "Use separate push URL");
        _pushUrlLabel.Text = T("FormRemotes/labelPushUrl.Text", "Push URL");
        _pushUrlBrowse.Content = T("FormRemotes/folderBrowserButtonPushUrl.Text", "Browse…");
        _saveRemote.Content = T("FormRemotes/Save.Text", "Save changes");

        _behaviorHint.Text = T(
            "For each local branch, the remote it pulls from (branch.<name>.remote) "
            + "and the remote branch it merges with (branch.<name>.merge). "
            + "Clearing a field removes the corresponding git config key.");

        // The three column captions are upstream's DataGridView headers verbatim; the
        // three editor captions are its labels, which carry the accelerator form.
        _branchNameHeader.Text = T("FormRemotes/BranchName.HeaderText", "Local branch name");
        _remoteHeader.Text = T("FormRemotes/RemoteCombo.HeaderText", "Remote repository");
        _mergeWithHeader.Text = T("FormRemotes/MergeWith.HeaderText", "Default merge with");
        _localBranchLabel.Text = T("FormRemotes/label4.Text", "Local branch");
        _remoteLabel.Text = T("TranslatedStrings/_remote.Text", "Remote");
        _mergeWithLabel.Text = T("FormRemotes/label6.Text", "Merge with");
        _mergeWith.Watermark = T("Type or pick a remote branch");
        _saveBranch.Content = T("FormRemotes/SaveDefaultPushPull.Text", "Save changes");
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // Shared column geometry for the branch header and each branch row, so the two
    // line up. Kept in one place because a mismatch is invisible in code review and
    // obvious on screen.
    private static ColumnDefinitions BranchColumns() => new("2*,1.4*,2*");

    // Returns the cell so the caller can keep it and re-caption it on a language
    // change; the caption itself is set by ApplyTranslations, never here.
    private static TextBlock AddHeaderCell(Grid grid, int column, IBrush brush)
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

    // NOTE: Avalonia re-invokes an item template with a NULL item when it empties a
    // recycled container, so every accessor here is null-tolerant (see the M51 note in
    // HANDOFF: a non-tolerant row factory crashed BlameView).
    private static FuncDataTemplate<BranchTrackingRow> BranchRowTemplate(IBrush text, IBrush dim)
        => new((row, _) =>
        {
            Grid grid = new() { ColumnDefinitions = BranchColumns() };

            TextBlock name = new()
            {
                Text = row?.Name ?? string.Empty,
                Foreground = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // An unconfigured branch shows a dim placeholder rather than a blank cell,
            // so "no default pull" reads as a state instead of a rendering glitch.
            bool hasRemote = !string.IsNullOrEmpty(row?.TrackingRemote);
            TextBlock remote = new()
            {
                Text = hasRemote ? row!.TrackingRemote : T("(none)"),
                Foreground = hasRemote ? text : dim,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            bool hasMerge = !string.IsNullOrEmpty(row?.MergeWith);
            TextBlock merge = new()
            {
                Text = hasMerge ? row!.MergeWith : T("(none)"),
                Foreground = hasMerge ? text : dim,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Grid.SetColumn(name, 0);
            Grid.SetColumn(remote, 1);
            Grid.SetColumn(merge, 2);
            grid.Children.Add(name);
            grid.Children.Add(remote);
            grid.Children.Add(merge);
            return grid;
        });

    // --- Loading ----------------------------------------------------------

    private void ReloadAll()
    {
        ReloadList();
        ReloadBranches();
    }

    private void ReloadList()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            IReadOnlyList<RemoteRow> remotes;
            try
            {
                remotes = _service.ListRemotes(_repoPath);
            }
            catch
            {
                remotes = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                string? keep = Selected?.Name;

                // Re-assigning the ItemsSource fires SelectionChanged with a null
                // selection first; suppress the detail reload so the user's in-progress
                // edits are not wiped by an intermediate empty selection.
                _suppressRemoteSelection = true;
                _list.ItemsSource = remotes;
                RemoteRow? restored = keep is null
                    ? null
                    : remotes.FirstOrDefault(r => r.Name == keep);
                _list.SelectedItem = restored ?? remotes.FirstOrDefault();
                _suppressRemoteSelection = false;

                LoadRemoteDetails();
                PopulateRemoteCombo(remotes);
            });
        });
    }

    private void ReloadBranches()
    {
        _ = Task.Run(() =>
        {
            IReadOnlyList<BranchTrackingRow> rows;
            try
            {
                rows = _service.ListBranchTracking(_repoPath);
            }
            catch
            {
                rows = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                string? keep = SelectedBranch?.Name;
                _branches.ItemsSource = rows;
                _branches.SelectedItem = keep is null
                    ? rows.FirstOrDefault()
                    : rows.FirstOrDefault(r => r.Name == keep) ?? rows.FirstOrDefault();
                LoadBranchDetails();
            });
        });
    }

    private void PopulateRemoteCombo(IReadOnlyList<RemoteRow> remotes)
    {
        string? current = _trackingRemote.SelectedItem as string;

        // The leading empty entry IS the "no remote" choice; selecting it unsets
        // branch.<x>.remote, matching upstream's blank ConfigFileRemote at index 0.
        List<string> items = [string.Empty, .. remotes.Select(r => r.Name)];
        _trackingRemote.ItemsSource = items;
        _trackingRemote.SelectedItem = current is not null && items.Contains(current)
            ? current
            : items[0];
    }

    private RemoteRow? Selected => _list.SelectedItem as RemoteRow;

    private BranchTrackingRow? SelectedBranch => _branches.SelectedItem as BranchTrackingRow;

    private void LoadRemoteDetails()
    {
        RemoteRow? row = Selected;
        if (row is null)
        {
            _fetchUrl.Text = string.Empty;
            _pushUrl.Text = string.Empty;
            _useSeparatePushUrl.IsChecked = false;
        }
        else
        {
            _fetchUrl.Text = row.FetchUrl;
            _pushUrl.Text = row.ConfiguredPushUrl;

            // Ticked iff the key exists — FormRemotes.cs:768-769.
            _useSeparatePushUrl.IsChecked = !string.IsNullOrEmpty(row.ConfiguredPushUrl);
        }

        ApplySeparatePushUrlVisibility();
        UpdateButtons();
    }

    private void LoadBranchDetails()
    {
        BranchTrackingRow? row = SelectedBranch;
        _localBranch.Text = row?.Name ?? string.Empty;

        if (_trackingRemote.ItemsSource is IEnumerable<string> items)
        {
            string want = row?.TrackingRemote ?? string.Empty;
            List<string> list = [.. items];
            _trackingRemote.SelectedItem = list.FirstOrDefault(
                i => string.Equals(i, want, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        _mergeWith.Text = row?.MergeWith ?? string.Empty;
        _saveBranch.IsEnabled = row is not null;
        _ = RefreshMergeWithCandidatesAsync();
    }

    // Re-reads the selected remote's remote-tracking branches into the merge-with
    // suggestions. Upstream does this lazily in DefaultMergeWithComboDropDown; doing it
    // on remote change is the same data, available before the user opens the drop-down.
    private async Task RefreshMergeWithCandidatesAsync()
    {
        string remote = _trackingRemote.SelectedItem as string ?? string.Empty;
        if (remote.Length == 0)
        {
            _mergeWith.ItemsSource = Array.Empty<string>();
            return;
        }

        IReadOnlyList<string> candidates = await Task.Run(
            () => _service.ListMergeWithCandidates(_repoPath, remote));
        _mergeWith.ItemsSource = candidates;
    }

    private void ApplySeparatePushUrlVisibility()
    {
        bool on = _useSeparatePushUrl.IsChecked == true;
        _pushUrlLabel.IsVisible = on;
        _pushUrl.IsVisible = on;
        _pushUrlBrowse.IsVisible = on;
    }

    private void UpdateButtons()
    {
        bool has = Selected is not null;
        _rename.IsEnabled = has;
        _remove.IsEnabled = has;
        _saveRemote.IsEnabled = has;
        _fetchUrl.IsEnabled = has;
        _useSeparatePushUrl.IsEnabled = has;
    }

    // --- Operations -------------------------------------------------------

    private async Task DoAddAsync()
    {
        string? name = await PromptAsync(T("New remote name:"), string.Empty);
        if (name is not { Length: > 0 })
        {
            return;
        }

        if (RemoteExists(name))
        {
            // Upstream's own wording for the clash, with its {0} placeholder kept so a
            // translator can move the name: an active remote is the only kind the port
            // has (it does not implement upstream's "inactive remote" concept).
            await ShowErrorAsync(
                T("Add remote"),
                TranslationService.TFormat(
                    "FormRemotes/_enabledRemoteAlreadyExists.Text",
                    "A remote named '{0}' already exists.",
                    name));
            return;
        }

        string? url = await PromptAsync(
            TranslationService.TFormat(null, "URL for remote '{0}':", name), string.Empty);
        if (url is not { Length: > 0 })
        {
            return;
        }

        RunMutation(
            TranslationService.TFormat(null, "Add remote '{0}'", name),
            () => _service.AddRemote(_repoPath, name, url));
    }

    private async Task DoRenameAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        string? name = await PromptAsync(
            TranslationService.TFormat(null, "Rename remote '{0}' to:", row.Name), row.Name);
        if (name is not { Length: > 0 } target || string.Equals(target, row.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (RemoteExists(target))
        {
            await ShowErrorAsync(
                T("Rename remote"),
                TranslationService.TFormat(
                    "FormRemotes/_enabledRemoteAlreadyExists.Text",
                    "A remote named '{0}' already exists.",
                    target));
            return;
        }

        RunMutation(
            TranslationService.TFormat(null, "Rename '{0}' to '{1}'", row.Name, target),
            () => _service.RenameRemote(_repoPath, row.Name, target));
    }

    private async Task DoRemoveAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (await ConfirmAsync(TranslationService.TFormat(null, "Remove remote '{0}'?", row.Name)))
        {
            RunMutation(
                TranslationService.TFormat(null, "Remove remote '{0}'", row.Name),
                () => _service.RemoveRemote(_repoPath, row.Name));
        }
    }

    /// <summary>
    ///  Saves the selected remote's fetch URL and its separate push URL, the way
    ///  upstream's single Save button does: the fetch URL first (<c>git remote
    ///  set-url</c>), then <c>remote.&lt;name&gt;.pushurl</c> — whose normalisation
    ///  (empty ⇒ drop the key, equal to the fetch URL ⇒ drop the key) lives in
    ///  <see cref="RemoteService.SetSeparatePushUrl"/>. The check box then follows the
    ///  decision the service made, exactly as upstream's <c>SaveClick</c> unticks its
    ///  own box.
    /// </summary>
    private async Task DoSaveRemoteAsync()
    {
        if (Selected is not { } row || _busy)
        {
            return;
        }

        string wantedUrl = _fetchUrl.Text?.Trim() ?? string.Empty;
        if (wantedUrl.Length == 0)
        {
            await ShowErrorAsync(T("Save remote"), T("The fetch URL cannot be empty."));
            return;
        }

        bool separate = _useSeparatePushUrl.IsChecked == true;
        string wantedPushUrl = _pushUrl.Text?.Trim() ?? string.Empty;
        string name = row.Name;
        bool urlChanged = !string.Equals(wantedUrl, row.FetchUrl, StringComparison.Ordinal);

        _busy = true;
        (RemoteOpResult result, bool keptSeparate) = await Task.Run(() =>
        {
            if (urlChanged)
            {
                RemoteOpResult setUrl = _service.SetRemoteUrl(_repoPath, name, wantedUrl);
                if (!setUrl.Success)
                {
                    return (setUrl, separate);
                }
            }

            PushUrlSaveResult push = _service.SetSeparatePushUrl(_repoPath, name, separate, wantedPushUrl, wantedUrl);
            return (push.Result, push.SeparatePushUrlKept);
        });
        _busy = false;

        if (result.Success)
        {
            Changed = true;
            _useSeparatePushUrl.IsChecked = keptSeparate;
            if (!keptSeparate)
            {
                _pushUrl.Text = string.Empty;
            }

            ApplySeparatePushUrlVisibility();
            ReloadList();
        }
        else
        {
            await ShowErrorAsync(
                TranslationService.TFormat(null, "Save remote '{0}'", name),
                string.IsNullOrWhiteSpace(result.Output) ? T("git reported no output.") : result.Output.Trim());
        }
    }

    /// <summary>
    ///  Writes the selected branch's default pull configuration and re-reads the tab.
    ///  Upstream splits this in two: the combos write on <c>Validated</c> (focus loss)
    ///  and its "Save changes" button only calls <c>Initialize()</c> to re-read
    ///  (<c>FormRemotes.cs:724-727</c>). An explicit write-then-refresh button is the
    ///  same net effect with a visible commit point, which suits a dialog that has no
    ///  WinForms validation cycle to hang the write on.
    /// </summary>
    private async Task DoSaveBranchAsync()
    {
        if (SelectedBranch is not { } row)
        {
            return;
        }

        string remote = _trackingRemote.SelectedItem as string ?? string.Empty;
        string merge = _mergeWith.Text?.Trim() ?? string.Empty;
        string branch = row.Name;

        _branchStatus.Text = TranslationService.TFormat(null, "Saving '{0}'…", branch);
        RemoteOpResult result = await Task.Run(
            () => _service.SetBranchPullConfiguration(_repoPath, branch, remote, merge));

        if (result.Success)
        {
            Changed = true;

            // Do NOT claim which keys were removed: only the fields the user changed are
            // written (see RemoteService.SetBranchPullConfiguration), so clearing just
            // the remote leaves branch.<x>.merge in place — exactly as upstream leaves
            // it, since its merge-with handler never fires. The refreshed grid below is
            // the authoritative report.
            _branchStatus.Text = remote.Length == 0
                ? TranslationService.TFormat(null, "'{0}': saved — no default pull remote.", branch)
                : TranslationService.TFormat(null, "'{0}': pulls from '{1}'.", branch, remote);
            ReloadBranches();
        }
        else
        {
            _branchStatus.Text = string.Empty;
            await ShowErrorAsync(
                TranslationService.TFormat(null, "Save default pull behavior for '{0}'", branch),
                string.IsNullOrWhiteSpace(result.Output) ? T("git reported no output.") : result.Output.Trim());
        }
    }

    // Upstream pairs the push URL box with a folder browser
    // (folderBrowserButtonPushUrl), because a push URL is often a local path.
    private async Task BrowseForPushUrlAsync()
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = T("Select the push target repository"),
                    AllowMultiple = false,
                });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { Length: > 0 } path)
            {
                _pushUrl.Text = path;
            }
        }
        catch
        {
            // A missing/failing portal must not take the dialog down; the box stays
            // typeable.
        }
    }

    /// <summary>
    ///  Runs a remote mutation off the UI thread and — crucially — SHOWS git's own
    ///  message when it fails. The previous version kept only
    ///  <see cref="RemoteOpResult.Success"/> and threw the output away, so a
    ///  rejected add/rename/remove looked exactly like a successful one: the list
    ///  simply reloaded unchanged. Upstream surfaces the same text
    ///  (<c>FormRemotes</c> shows <c>result.UserMessage</c> / the <c>RemoveRemote</c>
    ///  output in a message box).
    /// </summary>
    /// <param name="label">What was attempted, used as the error caption.</param>
    private void RunMutation(string label, Func<RemoteOpResult> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            RemoteOpResult result;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                result = new RemoteOpResult(false, ex.GetBaseException().Message, AuthFailed: false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (result.Success)
                {
                    Changed = true;
                }

                ReloadAll();

                if (!result.Success)
                {
                    // Git sometimes fails with no output at all (killed process,
                    // missing git); still tell the user something concrete.
                    string message = string.IsNullOrWhiteSpace(result.Output)
                        ? T("git reported no output.")
                        : result.Output.Trim();
                    _ = ShowErrorAsync(label, message);
                }
            });
        });
    }

    /// <summary>Shows git's failure text in a modal, dismissable box.</summary>
    private async Task ShowErrorAsync(string label, string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button ok = new()
        {
            Content = T("TranslatedStrings/_okText.Text", "OK"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Theming.ZoomWindow dialog = new()
        {
            Title = label,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        ok.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(true);

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = TranslationService.TFormat(null, "{0} failed:", label),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            MaxHeight = 220,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"),
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        });
        content.Children.Add(ok);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        await tcs.Task;
    }

    /// <summary>
    ///  True when <paramref name="name"/> is already a configured remote. Adding or
    ///  renaming onto an existing name is refused up front, with the reason — git
    ///  would fail anyway, and upstream validates the same thing in
    ///  <c>FormRemotes.ValidateRemoteDoesNotExist</c>.
    /// </summary>
    private bool RemoteExists(string name)
        => _list.ItemsSource is IEnumerable<RemoteRow> rows
            && rows.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    // --- Inline prompt / confirm (mirrors RepoObjectsTree helpers) --------

    private async Task<bool> ConfirmAsync(string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Confirm"), Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("TranslatedStrings/_cancelText.Text", "Cancel") };
        Theming.ZoomWindow dialog = new()
        {
            Title = T("Confirm"),
            Width = 340,
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
            Title = T("TranslatedStrings/_remote.Text", "Remote"),
            Width = 420,
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

    // No caption here: every button this dialog keeps is captioned by
    // ApplyTranslations, which is the single place that knows the active language.
    private static Button MakeButton()
        => new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;
}
