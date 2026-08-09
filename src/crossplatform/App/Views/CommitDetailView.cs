using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A read-only view of a single commit's metadata: a big identicon avatar and a
///  header (author, dates, committer, full hash, clickable parent/child links,
///  containing branches/tags and the nearest describe tag) above the full commit
///  message. Heavy git work is performed off the UI thread, matching <see cref="DiffView"/>.
///
///  <para>Captions go through <see cref="TranslationService"/>. Upstream keeps this
///  panel's field labels not in <c>CommitInfo</c> — which only owns the context menu
///  and the "Derives from…" wording — but in the shared <c>TranslatedStrings</c>
///  category, so that is where most keys point. The count-dependent ones
///  (<c>{0:Parent|Parents}</c>) are resolved by <see cref="Plural"/> rather than
///  concatenated, and the date cell is one format with two placeholders instead of
///  a relative string glued to an absolute one.</para>
/// </summary>
public sealed class CommitDetailView : UserControl
{
    private const double AvatarSize = 64;

    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private readonly CommitDetailService _service = new();
    private readonly CommitInfoExtrasService _extrasService = new();
    private readonly CommitInfoSettingsService _settingsService = new();
    private readonly ExternalToolService _externalTools = new();
    // Not readonly: the same toggles are also editable from the Settings dialog, and
    // CommitInfoSettingsService.Changed hands this panel the new state (see the ctor).
    private CommitInfoSettings _settings;

    // True while this panel is the one writing, so its own Save does not come back
    // through Changed and re-render a second time.
    private bool _savingOwnToggle;

    private readonly TextBlock _status;
    private readonly Border _avatarHost;
    private readonly StackPanel _details;
    private readonly SelectableTextBlock _message;

    // The context menu and the entries whose state has to be refreshed before it
    // opens. Built once, in full: adding or removing items from Opening leaves the
    // popup unmeasured (HANDOFF §3), so only Header/IsChecked/IsVisible ever change.
    private readonly ContextMenu _menu;
    private readonly MenuItem _copyLinkItem;
    private readonly MenuItem _copyInfoItem;
    private readonly MenuItem _addNotesItem;
    private readonly MenuItem _showBranchesItem;
    private readonly MenuItem _showBranchesRemoteItem;
    private readonly MenuItem _showBranchesRemoteIfNoLocalItem;
    private readonly MenuItem _showTagsItem;
    private readonly MenuItem _showAnnotatedTagsItem;
    private readonly MenuItem _showDerivesFromItem;

    /// <summary>
    ///  The host's live hotkey map, used ONLY to label "Add notes" with the gesture
    ///  actually in force — upstream does exactly this in
    ///  <c>CommitInfo.cs:113</c> (<c>addNoteToolStripMenuItem.ShortcutKeyDisplayString
    ///  = GetShortcutKeyDisplayString(FormBrowse.Command.AddNotes)</c>). Same contract
    ///  as <see cref="MainMenu.Hotkeys"/>: while it is null the label falls back to
    ///  <see cref="HotkeyService.Defaults"/>, and a command the user cleared shows no
    ///  gesture at all rather than lying. Assigning it re-labels immediately, so a
    ///  host can just re-assign after a <see cref="HotkeyService.Changed"/>.
    /// </summary>
    public HotkeyService? Hotkeys
    {
        get => _hotkeys;
        set
        {
            _hotkeys = value;
            _addNotesItem.InputGesture = AddNotesGesture();
        }
    }

    private HotkeyService? _hotkeys;

    /// <summary>The gesture in force for <see cref="BrowseCommand.AddNotes"/>.</summary>
    private KeyGesture? AddNotesGesture()
    {
        if (_hotkeys is { } service)
        {
            return service.GestureFor(BrowseCommand.AddNotes) is { } bound
                ? new KeyGesture(bound.Key, bound.Modifiers)
                : null;
        }

        return HotkeyService.Defaults.TryGetValue(BrowseCommand.AddNotes, out HotkeyGesture g)
            ? new KeyGesture(g.Key, g.Modifiers)
            : null;
    }

    // Which control carries which link target, so a right-click can name the link
    // under the pointer. Rebuilt by every Render.
    private readonly Dictionary<Control, string> _linkTargets = [];

    // The hash spans inside the message pane, in ascending order. Kept apart from
    // _linkTargets because these are ranges of one control's text, not controls.
    private readonly List<CommitMessageLink> _messageLinks = [];

    // Last rendered commit, kept so a language switch can re-label the panel
    // without another git round-trip.
    private CommitDetailInfo? _rendered;

    // Set while the panel shows the placeholder of an artificial row instead of a
    // commit, so a language switch can re-state it (there is nothing to re-load).
    private ArtificialDiff? _artificial;

    // Extra data only some toggles need (remote branches, annotated tag messages,
    // the commit's note), for the commit in _rendered.
    private CommitInfoExtras _extras = CommitInfoExtras.Empty;

    private string _repoPath = string.Empty;

    // The link target under the pointer when the menu was opened, or null.
    private string? _pointerLink;

    private CancellationTokenSource? _cts;

    /// <summary>
    ///  Raised with a full commit hash when the user clicks a parent or child
    ///  link. The host (MainWindow) may subscribe to navigate the grid; unwired
    ///  is harmless.
    /// </summary>
    public event Action<string>? CommitNavigated;

    public CommitDetailView()
    {
        _settings = _settingsService.Load();

        // The Settings dialog edits the very same toggles. Adopting its write keeps this
        // panel's copy from going stale — and, more importantly, from being written back
        // over the dialog's at the next toggle of this menu.
        CommitInfoSettingsService.Changed += OnCommitInfoSettingsChanged;

        _status = new TextBlock
        {
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("App.Toolbar"),
            Foreground = B("App.Text"),
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = T("No commit selected."),
        };

        _avatarHost = new Border
        {
            Width = AvatarSize,
            Height = AvatarSize,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };

        _details = new StackPanel { Orientation = Orientation.Vertical };

        Grid header = new()
        {
            Margin = new Thickness(14, 10, 14, 8),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        Grid.SetColumn(_avatarHost, 0);
        Grid.SetColumn(_details, 1);
        header.Children.Add(_avatarHost);
        header.Children.Add(_details);

        _message = new SelectableTextBlock
        {
            FontFamily = Monospace,
            Foreground = B("App.Text"),
            Margin = new Thickness(14, 10, 14, 14),
            TextWrapping = TextWrapping.Wrap,
        };

        ScrollViewer messageScroll = new()
        {
            Content = _message,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Border separator = new()
        {
            Height = 1,
            Background = B("App.Border"),
            Margin = new Thickness(14, 0, 14, 0),
        };

        DockPanel root = new() { Background = B("App.Panel") };
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(separator, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(header);
        root.Children.Add(separator);
        root.Children.Add(messageScroll);

        Content = root;

        // --- context menu (upstream: commitInfoContextMenuStrip) ---
        _copyLinkItem = Item(T("CommitInfo/copyLinkToolStripMenuItem.Text", "Copy link"), CopyLink);
        _copyInfoItem = Item(T("CommitInfo/copyCommitInfoToolStripMenuItem.Text", "&Copy commit info"), CopyCommitInfo);
        _addNotesItem = Item(T("CommitInfo/addNoteToolStripMenuItem.Text", "Add &notes"), () => EditNotes());

        // Label it with the shipped default straight away, so the entry advertises
        // Ctrl+Shift+N even before a host assigns Hotkeys (and in the Settings
        // dialog's preview, which builds this panel without one).
        _addNotesItem.InputGesture = AddNotesGesture();

        _showBranchesItem = Toggle(
            T("CommitInfo/showContainedInBranchesToolStripMenuItem.Text", "Show local branches containing this commit"),
            () => _settings.ShowContainedInBranchesLocal = !_settings.ShowContainedInBranchesLocal);
        _showBranchesRemoteItem = Toggle(
            T("CommitInfo/showContainedInBranchesRemoteToolStripMenuItem.Text", "Show remote branches containing this commit"),
            () => _settings.ShowContainedInBranchesRemote = !_settings.ShowContainedInBranchesRemote);
        _showBranchesRemoteIfNoLocalItem = Toggle(
            T("CommitInfo/showContainedInBranchesRemoteIfNoLocalToolStripMenuItem.Text",
                "Show remote branches only when no local branch contains this commit"),
            () => _settings.ShowContainedInBranchesRemoteIfNoLocal = !_settings.ShowContainedInBranchesRemoteIfNoLocal);
        _showTagsItem = Toggle(
            T("CommitInfo/showContainedInTagsToolStripMenuItem.Text", "Show tags containing this commit"),
            () => _settings.ShowContainedInTags = !_settings.ShowContainedInTags);
        _showAnnotatedTagsItem = Toggle(
            T("CommitInfo/showMessagesOfAnnotatedTagsToolStripMenuItem.Text", "Show messages of annotated tags"),
            () => _settings.ShowAnnotatedTagsMessages = !_settings.ShowAnnotatedTagsMessages);
        _showDerivesFromItem = Toggle(
            T("CommitInfo/showTagThisCommitDerivesFromMenuItem.Text", "Show the most recent tag this commit derives from"),
            () => _settings.ShowTagThisCommitDerivesFrom = !_settings.ShowTagThisCommitDerivesFrom);

        _menu = new ContextMenu
        {
            Placement = PlacementMode.Pointer,
            ItemsSource = new Control[]
            {
                _copyLinkItem,
                _copyInfoItem,
                new Separator(),
                _showBranchesItem,
                _showBranchesRemoteItem,
                _showBranchesRemoteIfNoLocalItem,
                _showTagsItem,
                _showAnnotatedTagsItem,
                _showDerivesFromItem,
                new Separator(),
                _addNotesItem,
            },
        };

        // Opened by hand from a TUNNELLING press with handledEventsToo: this panel
        // is built out of SelectableTextBlocks, which swallow the secondary button
        // (ContextRequested never fires on them — the same wall CommitDialog's diff
        // menu hit). Getting in ahead of them also leaves any highlight alone.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Hash links inside the message are runs, not controls, so their click and
        // their hand cursor are resolved against the text layout. Tunnelling for the
        // press: the SelectableTextBlock would otherwise start a selection drag and
        // mark the event handled before it ever bubbles here.
        _message.AddHandler(PointerPressedEvent, OnMessagePointerPressed, RoutingStrategies.Tunnel);
        _message.PointerMoved += (_, e) =>
            _message.Cursor = MessageLinkAt(e.GetPosition(_message)) is null ? null : HandCursor;
        _message.PointerExited += (_, _) => _message.Cursor = null;

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>A plain command entry. Mnemonics are converted, not shown.</summary>
    private static MenuItem Item(string header, Action action)
    {
        MenuItem item = new() { Header = MenuHeader(header) };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    ///  A checked visibility entry: flips its setting, persists it, and re-renders
    ///  the panel from the data at hand — upstream's <c>ReloadCommitInfo</c>.
    /// </summary>
    private MenuItem Toggle(string header, Action flip)
    {
        MenuItem item = new()
        {
            Header = MenuHeader(header),
            ToggleType = MenuItemToggleType.CheckBox,
        };
        item.Click += (_, _) =>
        {
            flip();
            _savingOwnToggle = true;
            try
            {
                _settingsService.Save(_settings);
            }
            finally
            {
                _savingOwnToggle = false;
            }

            ReloadAfterSettingChange();
        };
        return item;
    }

    // WinForms "&" mnemonics are dropped; a literal underscore has to be doubled
    // or Avalonia would eat it as a mnemonic marker of its own.
    private static string MenuHeader(string caption)
        => RevisionFilterDialog.StripMnemonic(caption).Replace("_", "__", StringComparison.Ordinal);

    // One shared instance: a Cursor is a small unmanaged handle, and re-creating it
    // on every pointer move over the message would churn one per frame.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private void OnMessagePointerPressed(object? sender, PointerPressedEventArgs e)
    {

        // The right button belongs to the context menu (handled by the tunnelling
        // handler on the panel), which offers "Copy link" for the same span.
        if (!e.GetCurrentPoint(_message).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (MessageLinkAt(e.GetPosition(_message)) is { } hash)
        {
            e.Handled = true;
            CommitNavigated?.Invoke(hash);
        }
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        e.Handled = true;

        _pointerLink = LinkAt(e.GetPosition(this));

        // Everything is settled BEFORE the popup is shown; only labels and states
        // change, never the item list.
        UpdateMenuState();
        _menu.Open(this);
    }

    /// <summary>
    ///  The link target under <paramref name="position"/>, or <see langword="null"/>.
    ///  The only links this panel draws are the parent/child hashes, so a hit is
    ///  resolved by walking up from the element under the pointer to whichever
    ///  ancestor was registered by <see cref="HashLink"/>.
    /// </summary>
    private string? LinkAt(Point position)
    {
        // A hash inside the message body is a text range, not a control, so it is
        // asked first — the walk below would only ever reach the message block itself.
        if (_message.GetVisualRoot() is not null
            && MessageLinkAt(this.TranslatePoint(position, _message) ?? default) is { } messageLink)
        {
            return messageLink;
        }

        if (_linkTargets.Count == 0)
        {
            return null;
        }

        Visual? hit = this.InputHitTest(position) as Visual;
        while (hit is not null)
        {
            if (hit is Control control && _linkTargets.TryGetValue(control, out string? target))
            {
                return target;
            }

            hit = hit.GetVisualParent();
        }

        return null;
    }

    // Labels/enablement for the entries, computed from the state at open time.
    private void UpdateMenuState()
    {
        // Upstream hides "Copy link" outright when the cursor is not over a link,
        // and formats the target into its caption.
        _copyLinkItem.IsVisible = _pointerLink is not null;
        if (_pointerLink is { } link)
        {
            _copyLinkItem.Header = MenuHeader(string.Format(
                T("CommitInfo/_copyLink.Text", "Copy &link ({0})"),
                Shorten(link)));
        }

        _copyInfoItem.IsEnabled = _rendered is not null;
        _addNotesItem.IsEnabled = _rendered is not null && _repoPath.Length > 0;

        _showBranchesItem.IsChecked = _settings.ShowContainedInBranchesLocal;
        _showBranchesRemoteItem.IsChecked = _settings.ShowContainedInBranchesRemote;
        _showBranchesRemoteIfNoLocalItem.IsChecked = _settings.ShowContainedInBranchesRemoteIfNoLocal;
        _showTagsItem.IsChecked = _settings.ShowContainedInTags;
        _showAnnotatedTagsItem.IsChecked = _settings.ShowAnnotatedTagsMessages;
        _showDerivesFromItem.IsChecked = _settings.ShowTagThisCommitDerivesFrom;
    }

    private static string Shorten(string hash) => hash.Length >= 8 ? hash[..8] : hash;

    private void CopyLink()
    {
        if (_pointerLink is { Length: > 0 } link)
        {
            CopyToClipboard(link);
        }
    }

    /// <summary>
    ///  Upstream's "Copy commit info": the header block as plain text, a blank
    ///  line, then the commit message. The containing-branches/tags sections are
    ///  not part of it there either.
    /// </summary>
    private void CopyCommitInfo()
    {
        if (_rendered is not { } detail)
        {
            return;
        }

        StringBuilder sb = new();
        void Line(string label, string value) => sb.Append(label).Append(' ').AppendLine(value);

        Line(T("TranslatedStrings/_author.Text", "Author"), detail.Author);
        Line(detail.DatesDiffer
                ? Plural(T("TranslatedStrings/_authorDateText.Text", "{0:Author date|Author dates}"), 1)
                : T("TranslatedStrings/_dateText.Text", "Date"),
            DateDisplay(detail.AuthorDate, detail.AuthorDateRelative));
        if (detail.CommitterDiffers)
        {
            Line(T("TranslatedStrings/_committerText.Text", "Committer"), detail.Committer);
        }

        if (detail.DatesDiffer)
        {
            Line(Plural(T("TranslatedStrings/_commitDateText.Text", "{0:Commit date|Commit dates}"), 1),
                DateDisplay(detail.CommitDate, detail.CommitDateRelative));
        }

        Line(T("PatchGrid/CommitHash.HeaderText", "Commit hash"), detail.Hash);
        if (detail.ParentHashes.Count > 0)
        {
            Line(Plural(T("TranslatedStrings/_parentsText.Text", "{0:Parent|Parents}"), detail.ParentHashes.Count),
                string.Join(" ", detail.ParentHashes));
        }

        if (detail.ChildHashes.Count > 0)
        {
            Line(Plural(T("TranslatedStrings/_childrenText.Text", "{0:Child|Children}"), detail.ChildHashes.Count),
                string.Join(" ", detail.ChildHashes));
        }

        sb.AppendLine().Append(detail.Message);
        CopyToClipboard(sb.ToString());
    }

    private void CopyToClipboard(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>
    ///  Edits the current commit's git note. Public because it has TWO callers: this
    ///  panel's own context-menu entry, and <c>MainWindow.InstallHotkeys</c>, which
    ///  binds it to <see cref="BrowseCommand.AddNotes"/> (Ctrl+Shift+N) exactly as
    ///  upstream's <c>FormBrowse.Command.AddNotes</c> does — so the gesture works
    ///  while focus is in the revision grid and acts on the selected commit, which is
    ///  the commit this panel is showing.
    ///
    ///  <para>The note is read and written off the UI thread; the editor itself is
    ///  <see cref="AddNotesDialog"/>, because upstream's <c>git notes edit</c> would
    ///  hand the commit to <c>core.editor</c>.</para>
    /// </summary>
    public void EditNotes()
    {
        if (_rendered is not { } detail || _repoPath.Length == 0)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        string repo = _repoPath;
        string hash = detail.Hash;

        _ = Task.Run(async () =>
        {
            string existing;
            try
            {
                existing = _extrasService.LoadNotes(repo, hash);
            }
            catch
            {
                existing = string.Empty;
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                AddNotesDialog dialog = new(Shorten(hash), existing);
                await dialog.ShowDialog(owner);
                if (!dialog.Accepted)
                {
                    return;
                }

                string text = dialog.NoteText;
                string error = await Task.Run(() => _extrasService.SaveNotes(repo, hash, text));
                if (error.Length > 0)
                {
                    _status.Text = string.Format(T("Error: {0}"), error.Split('\n')[0]);
                    return;
                }

                // The note is part of what this panel shows, so refresh it.
                if (_rendered?.Hash == hash)
                {
                    ReloadAfterSettingChange();
                }
            });
        });
    }

    /// <summary>
    ///  Re-fetches whatever the current toggles need (a toggle just turned on may
    ///  require data never loaded) and re-renders. Never throws.
    /// </summary>
    // Another editor of the toggles (the Settings dialog) saved. Re-read the file, tick
    // the menu accordingly and re-render. Raised on whichever thread wrote, hence the
    // hop to the UI thread; the read itself is a small local file.
    private void OnCommitInfoSettingsChanged()
    {
        if (_savingOwnToggle)
        {
            return;
        }

        CommitInfoSettings loaded = _settingsService.Load();
        Dispatcher.UIThread.Post(() =>
        {
            // The menu ticks need no touching here: UpdateMenuState recomputes them from
            // _settings every time the menu opens.
            _settings = loaded;
            ReloadAfterSettingChange();
        });
    }

    private void ReloadAfterSettingChange()
    {
        if (_rendered is not { } detail)
        {
            return;
        }

        // Render at once from what is already loaded, so the change is immediate
        // even if the extra lookup is slow or yields nothing new.
        Render(detail);

        if (_repoPath.Length == 0)
        {
            return;
        }

        string repo = _repoPath;
        string hash = detail.Hash;
        bool wantRemote = _settings.ShowContainedInBranchesRemote
            || _settings.ShowContainedInBranchesRemoteIfNoLocal;
        bool wantTags = _settings.ShowAnnotatedTagsMessages;

        _ = Task.Run(() =>
        {
            CommitInfoExtras extras;
            try
            {
                extras = _extrasService.Load(repo, hash, wantRemote, wantTags);
            }
            catch
            {
                extras = CommitInfoExtras.Empty;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_rendered is { } current && current.Hash == hash)
                {
                    _extras = extras;
                    Render(current);
                }
            });
        });
    }

    // Re-label in place on a language switch. The event fires on whichever thread
    // completed the catalogue load, so the UI work is marshalled explicitly.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        if (_artificial is { } which)
        {
            RenderArtificial(which);
        }
        else if (_rendered is not null)
        {
            Render(_rendered);
        }
        else
        {
            _status.Text = T("No commit selected.");
        }
    }

    /// <summary>
    ///  Loads and displays the metadata for <paramref name="commitHash"/> in the
    ///  repository at <paramref name="repoPath"/>.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        Clear();
        _repoPath = repoPath;
        _artificial = null;
        _status.Text = string.Format(T("Loading commit {0}…"), commitHash);

        bool wantRemote = _settings.ShowContainedInBranchesRemote
            || _settings.ShowContainedInBranchesRemoteIfNoLocal;
        bool wantTags = _settings.ShowAnnotatedTagsMessages;

        _ = Task.Run(() =>
        {
            try
            {
                CommitDetailInfo? detail = _service.LoadCommit(repoPath, commitHash, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // Best-effort; a failure here must not cost the whole panel.
                CommitInfoExtras extras = CommitInfoExtras.Empty;
                if (detail is not null)
                {
                    extras = _extrasService.Load(repoPath, detail.Hash, wantRemote, wantTags, token);
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (detail is null)
                    {
                        _status.Text = string.Format(T("Commit not found: {0}"), commitHash);
                        return;
                    }

                    _extras = extras;
                    Render(detail);
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another selection; ignore.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _status.Text = string.Format(T("Error: {0}"), ex.Message);
                    }
                });
            }
        });
    }

    /// <summary>
    ///  Shows the placeholder of one of the two <b>artificial</b> revision rows —
    ///  the Commit-details half of the
    ///  <c>RevisionGridView.ArtificialRevisionSelected</c> contract. There is no
    ///  commit object behind those rows, so there is no author, date, hash, message
    ///  or "contained in" data to show: the pane names the row and says where its
    ///  content actually is, which is the honest alternative to leaving the
    ///  previously selected commit's details on screen.
    ///
    ///  <para>Upstream renders the row's Subject ("Working directory" / "Commit
    ///  index") as the message body and clears the lower info pane outright
    ///  (<c>CommitInfo.cs:328-357</c>, <c>CommitDataHeaderRenderer.cs:81-133</c>
    ///  suppress date and hash for artificial revisions); the naming here is that
    ///  Subject, with one added sentence because a fixed tab cannot be removed the
    ///  way upstream's can.</para>
    ///
    ///  <para>Synchronous and cheap: it runs no git command at all, so it is safe
    ///  to call straight from the selection handler on the UI thread.</para>
    /// </summary>
    public void ShowArtificial(string repoPath, ArtificialDiff which)
    {
        // Any in-flight commit load must not land on top of the placeholder.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        Clear();
        _repoPath = repoPath;
        _artificial = which;
        RenderArtificial(which);
    }

    private void RenderArtificial(ArtificialDiff which)
    {
        string name = ArtificialRevisionName.Of(which);

        _rendered = null;
        _status.Text = name;
        _avatarHost.Child = null;
        _details.Children.Clear();
        _linkTargets.Clear();
        _messageLinks.Clear();

        _message.Text = string.Empty;
        InlineCollection inlines = _message.Inlines ??= [];
        inlines.Clear();

        // Both brushes are registered palette keys (ThemeManager.Keys + Dark +
        // Light), so the placeholder stays readable in either theme; an unregistered
        // key would silently fall back to black (M62).
        inlines.Add(new Run(name)
        {
            Foreground = B("App.Text"),
            FontWeight = FontWeight.SemiBold,
        });
        inlines.Add(new LineBreak());
        inlines.Add(new Run(string.Format(
            T("{0} is not a commit, so it has no author, date or message. Its changes are in the Diff and File tree tabs."),
            name))
        {
            Foreground = B("App.TextDim"),
        });
    }

    private void Render(CommitDetailInfo detail)
    {
        _artificial = null;
        _rendered = detail;
        _status.Text = detail.Subject;

        _avatarHost.Child = new AvatarControl(Identicon.Create(
            !string.IsNullOrEmpty(detail.AuthorEmail) ? detail.AuthorEmail : detail.AuthorName))
        {
            Width = AvatarSize,
            Height = AvatarSize,
        };

        _details.Children.Clear();
        _linkTargets.Clear();

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        int row = 0;

        string none = T("UserRepositoriesList/tsmiCategoryNone.Text", "(none)");

        AddRow(grid, ref row, T("TranslatedStrings/_author.Text", "Author"),
            PersonValue(detail.Author, detail.AuthorEmail));

        // Upstream keys the date rows off the timestamps, never off the identity:
        // the plain "Date" label is used only while author and commit date are
        // identical, and the commit date gets its own row as soon as they drift
        // apart — the amend / rebase / cherry-pick case, where author and
        // committer are the same person (CommitDataHeaderRenderer.Render).
        AddRow(grid, ref row,
            detail.DatesDiffer
                ? Plural(T("TranslatedStrings/_authorDateText.Text", "{0:Author date|Author dates}"), 1)
                : T("TranslatedStrings/_dateText.Text", "Date"),
            TextValue(DateDisplay(detail.AuthorDate, detail.AuthorDateRelative), monospace: false));

        if (detail.CommitterDiffers)
        {
            AddRow(grid, ref row, T("TranslatedStrings/_committerText.Text", "Committer"),
                PersonValue(detail.Committer, detail.CommitterEmail));
        }

        if (detail.DatesDiffer)
        {
            AddRow(grid, ref row,
                Plural(T("TranslatedStrings/_commitDateText.Text", "{0:Commit date|Commit dates}"), 1),
                TextValue(DateDisplay(detail.CommitDate, detail.CommitDateRelative), monospace: false));
        }

        AddRow(grid, ref row, T("PatchGrid/CommitHash.HeaderText", "Commit hash"),
            TextValue(detail.Hash, monospace: true));

        AddRow(grid, ref row,
            Plural(T("TranslatedStrings/_parentsText.Text", "{0:Parent|Parents}"), detail.ParentHashes.Count),
            detail.ParentHashes.Count > 0 ? LinkRow(detail.ParentHashes) : TextValue(none, monospace: false));
        AddRow(grid, ref row,
            Plural(T("TranslatedStrings/_childrenText.Text", "{0:Child|Children}"), detail.ChildHashes.Count),
            detail.ChildHashes.Count > 0 ? LinkRow(detail.ChildHashes) : TextValue(none, monospace: false));

        _details.Children.Add(grid);

        // Annotated tag messages, first as upstream renders them ("tag: message").
        if (_settings.ShowAnnotatedTagsMessages)
        {
            foreach (CommitInfoAnnotatedTag tag in _extras.AnnotatedTags)
            {
                _details.Children.Add(SectionLabel($"{tag.Name}:"));
                SelectableTextBlock body = TextValue(tag.Message, monospace: false);
                body.Margin = new Thickness(14, 0, 14, 2);
                _details.Children.Add(body);
            }
        }

        // Contained-in branches: one section listing whichever kinds the three
        // branch toggles allow. With all three off the section is absent, exactly
        // as upstream's empty _branchInfo makes it.
        IReadOnlyList<string> branches = VisibleBranches(detail);
        if (_settings.ShowContainedInBranchesLocal
            || _settings.ShowContainedInBranchesRemote
            || _settings.ShowContainedInBranchesRemoteIfNoLocal)
        {
            _details.Children.Add(SectionLabel(branches.Count > 0
                ? T("TranslatedStrings/_containedInBranchesText.Text", "Contained in branches:")
                : T("TranslatedStrings/_containedInNoBranchText.Text", "Contained in no branch")));
            if (branches.Count > 0)
            {
                _details.Children.Add(TagWrap(branches, B("App.GraphGreen")));
            }
        }

        // Contained-in tags.
        if (_settings.ShowContainedInTags)
        {
            if (detail.Tags.Count > 0)
            {
                _details.Children.Add(SectionLabel(T("TranslatedStrings/_containedInTagsText.Text", "Contained in tags:")));
                _details.Children.Add(TagWrap(detail.Tags, B("App.Accent")));
            }
            else
            {
                _details.Children.Add(SectionLabel(T("TranslatedStrings/_containedInNoTagText.Text", "Contained in no tag")));
            }
        }

        // Derives-from-tag. One format with a placeholder, so a language whose
        // word order differs can move the tag name.
        if (_settings.ShowTagThisCommitDerivesFrom)
        {
            if (!string.IsNullOrEmpty(detail.DescribeTag))
            {
                // Upstream prints "Derives from tag: <tag link> + N commits"
                // (CommitInfo.cs:575-590) — the raw "v1.0-5-gabc1234" git hands back
                // is a describe expression, not something a reader should have to
                // decode. The count is a separate label so its plural word stays
                // translatable on its own.
                StackPanel line = new()
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(14, 8, 14, 2),
                };
                line.Children.Add(new TextBlock
                {
                    Text = T("CommitInfo/_derivesFromTag.Text", "Derives from tag:"),
                    Foreground = B("App.TextDim"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 6, 0),
                });
                line.Children.Add(RefLink(detail.DescribeTag, isTag: true));

                if (!string.IsNullOrEmpty(detail.DescribeCommitCount))
                {
                    line.Children.Add(new TextBlock
                    {
                        Text = string.Format(
                            T("+ {0} {1}"),
                            detail.DescribeCommitCount,
                            T("CommitInfo/_plusCommits.Text", "commits")),
                        Foreground = B("App.TextDim"),
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                _details.Children.Add(line);
            }
            else
            {
                _details.Children.Add(SectionLabel(T("CommitInfo/_derivesFromNoTag.Text", "Derives from no tag")));
            }
        }

        // The commit's git note, so "Add notes" has visible feedback.
        if (_extras.Notes.Length > 0)
        {
            _details.Children.Add(SectionLabel(T("Notes:")));
            SelectableTextBlock notes = TextValue(_extras.Notes, monospace: false);
            notes.Margin = new Thickness(14, 0, 14, 2);
            _details.Children.Add(notes);
        }

        RenderMessage(detail);
    }

    /// <summary>
    ///  Fills the message pane, turning every abbreviated hash the service could
    ///  resolve into a link (upstream <c>CommitDataBodyRenderer.cs:44,50-65</c>).
    ///
    ///  <para>The links are <see cref="Run"/>s, not controls: an inline control would
    ///  break the pane's text selection and its wrapping. A run cannot carry a click
    ///  handler, so the spans are remembered in <see cref="_messageLinks"/> and a hit
    ///  is resolved against the block's own text layout — which is also what lets
    ///  <see cref="LinkAt"/> offer "Copy link" over them.</para>
    /// </summary>
    private void RenderMessage(CommitDetailInfo detail)
    {
        _messageLinks.Clear();
        _message.Inlines?.Clear();

        string text = detail.Message;
        IReadOnlyList<CommitMessageLink> links = detail.Links;
        if (links.Count == 0)
        {
            // No inlines at all in the common case: a plain Text keeps selection and
            // wrapping on the fast path.
            _message.Text = text;
            return;
        }

        _message.Text = null;
        InlineCollection inlines = _message.Inlines ??= [];

        int cursor = 0;
        foreach (CommitMessageLink link in links)
        {
            if (link.Start < cursor || link.Start + link.Length > text.Length)
            {
                continue;
            }

            if (link.Start > cursor)
            {
                inlines.Add(new Run(text[cursor..link.Start]));
            }

            // App.Link, not App.Accent: this run is clickable text, and the accent is
            // tuned as a fill (borders, bars, selection) — it only reaches 3.40:1 on
            // App.Panel in classic dark. App.Link is the text-grade blue, ≥ 4.5:1
            // against every surface in all four families.
            inlines.Add(new Run(text.Substring(link.Start, link.Length))
            {
                Foreground = B("App.Link"),
                TextDecorations = TextDecorations.Underline,
            });

            _messageLinks.Add(link);
            cursor = link.Start + link.Length;
        }

        if (cursor < text.Length)
        {
            inlines.Add(new Run(text[cursor..]));
        }
    }

    /// <summary>
    ///  The commit a point inside the message pane is over, or <see langword="null"/>.
    ///  The point is in <see cref="_message"/>'s own coordinates.
    /// </summary>
    private string? MessageLinkAt(Point inMessage)
    {
        if (_messageLinks.Count == 0)
        {
            return null;
        }

        TextLayout layout = _message.TextLayout;
        int index = layout.HitTestPoint(inMessage).TextPosition;

        CommitMessageLink? found = null;
        foreach (CommitMessageLink link in _messageLinks)
        {
            if (index >= link.Start && index < link.Start + link.Length)
            {
                found = link;
                break;
            }
        }

        if (found is null)
        {
            return null;
        }

        // The hit test snaps to the nearest character, so a point in the empty space
        // past the end of a line comes back as that line's last character — which,
        // when a link ends the line, would make the whole margin clickable. The
        // character's own rectangle is what settles it.
        //
        // (TextHitTestResult.IsInside cannot be used for this: on a wrapping
        // SelectableTextBlock it reads false even for a point squarely on a glyph.)
        Rect glyph = layout.HitTestTextPosition(index);
        bool onGlyph = inMessage.Y >= glyph.Y
            && inMessage.Y <= glyph.Bottom
            && inMessage.X >= glyph.X - 1
            && inMessage.X <= glyph.Right + 1;

        return onGlyph ? found.FullHash : null;
    }

    /// <summary>
    ///  The branches to list, per the three branch toggles: locals when they are
    ///  enabled, remotes when they are — and, for "remote if no local", only while
    ///  no local branch contains the commit (upstream reaches the same result by
    ///  suppressing remotes once a local one appears in the sorted list).
    /// </summary>
    private IReadOnlyList<string> VisibleBranches(CommitDetailInfo detail)
    {
        List<string> result = [];
        if (_settings.ShowContainedInBranchesLocal)
        {
            result.AddRange(detail.Branches);
        }

        bool remotesAllowed = _settings.ShowContainedInBranchesRemote
            || (_settings.ShowContainedInBranchesRemoteIfNoLocal && detail.Branches.Count == 0);
        if (remotesAllowed)
        {
            result.AddRange(_extras.RemoteBranches);
        }

        return result;
    }

    /// <summary>
    ///  Empties the pane back to "No commit selected." — the state it is born in.
    ///  Used when the window changes REPOSITORY: the commit on screen belongs to the
    ///  repository being left, and a new one with no selection yet would never overwrite
    ///  it. (The private <c>Clear</c> below empties the fields; this also cancels the
    ///  load in flight and resets the status line, which a reload would have done.)
    /// </summary>
    public void ClearCommit()
    {
        _cts?.Cancel();
        Clear();
        _repoPath = string.Empty;
        _status.Text = T("No commit selected.");
    }

    private void Clear()
    {
        _rendered = null;
        _artificial = null;
        _extras = CommitInfoExtras.Empty;
        _avatarHost.Child = null;
        _details.Children.Clear();
        _linkTargets.Clear();
        _messageLinks.Clear();
        _message.Inlines?.Clear();
        _message.Text = string.Empty;
    }

    // Relative and absolute date in a single translatable format, never two
    // concatenated fragments.
    private static string DateDisplay(string absolute, string relative)
    {
        if (string.IsNullOrEmpty(absolute))
        {
            return relative;
        }

        return string.IsNullOrEmpty(relative)
            ? absolute
            : string.Format(T("{0}  ({1})"), relative, absolute);
    }

    /// <summary>
    ///  Resolves the pluralised placeholder syntax the upstream catalogues use for
    ///  these labels — <c>"{0:Parent|Parents}"</c> — picking the singular for
    ///  <paramref name="count"/> 0 or 1 and the plural otherwise, then substituting
    ///  any bare <c>{0}</c> with the count itself.
    ///
    ///  <para>Some catalogues (the Italian one among them) separate the two forms
    ///  with a backslash instead of a pipe — <c>"{0:Data commit\Data commit}"</c> —
    ///  so both separators are accepted; otherwise the raw placeholder would leak
    ///  into the UI.</para>
    /// </summary>
    private static string Plural(string format, int count)
    {
        StringBuilder sb = new(format.Length);

        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '{')
            {
                sb.Append(c);
                continue;
            }

            int close = format.IndexOf('}', i + 1);
            if (close < 0)
            {
                sb.Append(format, i, format.Length - i);
                break;
            }

            string body = format[(i + 1)..close];
            int colon = body.IndexOf(':');
            if (colon < 0)
            {
                // A bare "{0}" — the count itself.
                sb.Append(count.ToString(CultureInfo.CurrentCulture));
            }
            else
            {
                string forms = body[(colon + 1)..];
                int sep = forms.IndexOfAny(['|', '\\']);
                sb.Append(sep < 0 ? forms : (count is 0 or 1 ? forms[..sep] : forms[(sep + 1)..]));
            }

            i = close;
        }

        return sb.ToString();
    }

    private static SelectableTextBlock TextValue(string text, bool monospace)
    {
        SelectableTextBlock block = new()
        {
            Text = text,
            Foreground = B("App.Text"),
            Margin = new Thickness(0, 3, 0, 3),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (monospace)
        {
            block.FontFamily = Monospace;
        }

        return block;
    }

    /// <summary>
    ///  Renders an "author"/"committer" cell. Upstream turns the identity into a
    ///  <c>mailto:</c> link (<c>CommitDataHeaderRenderer</c> line 94/104), so the
    ///  name is clickable whenever an address is known and falls back to plain
    ///  text when the commit carries no e-mail. The target is registered in
    ///  <see cref="_linkTargets"/> so right-click still offers "Copy link".
    /// </summary>
    private Control PersonValue(string display, string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return TextValue(display, monospace: false);
        }

        string target = "mailto:" + email;
        TextBlock link = new()
        {
            Text = display,
            // Clickable mailto ink, so App.Link rather than the fill-grade accent.
            Foreground = B("App.Link"),
            TextDecorations = TextDecorations.Underline,
            Margin = new Thickness(0, 3, 0, 3),
            TextWrapping = TextWrapping.Wrap,
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(link).Properties.IsRightButtonPressed)
            {
                return;
            }

            // The handler shells out to xdg-open; keep it off the UI thread. Forget
            // observes the fault: a browser that cannot be launched must report, not
            // resurface later as an unhandled exception.
            Task.Run(() => _externalTools.OpenUrl(target)).Forget($"opening {target}");
        };

        _linkTargets[link] = target;
        return link;
    }

    /// <summary>Builds a wrap-panel of clickable short-hash links, each raising <see cref="CommitNavigated"/>.</summary>
    private WrapPanel LinkRow(IReadOnlyList<string> hashes)
    {
        WrapPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 3),
        };

        foreach (string full in hashes)
        {
            panel.Children.Add(HashLink(full));
        }

        return panel;
    }

    private TextBlock HashLink(string fullHash)
    {
        string shortHash = fullHash.Length >= 8 ? fullHash[..8] : fullHash;
        TextBlock link = new()
        {
            Text = shortHash,
            FontFamily = Monospace,
            // An openable sha is a link, and a short monospace hash is the hardest
            // thing on the pane to read — it gets the text-grade blue.
            Foreground = B("App.Link"),
            TextDecorations = TextDecorations.Underline,
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.PointerPressed += (_, e) =>
        {
            // The right button belongs to the context menu, which the tunnelling
            // handler has already dealt with.
            if (e.GetCurrentPoint(link).Properties.IsRightButtonPressed)
            {
                return;
            }

            CommitNavigated?.Invoke(fullHash);
        };

        // Registered so a right-click over it can offer "Copy link".
        _linkTargets[link] = fullHash;
        return link;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = B("App.TextDim"),
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(14, 8, 14, 2),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>
    ///  Renders ref names (branches/tags) as small tinted pill labels. A pill whose
    ///  ref the repository still has resolves to a commit and becomes clickable —
    ///  upstream renders both kinds as links (<c>RefsFormatter.cs:30,41</c>) — which
    ///  is also what gives "Copy link" something to copy over a branch name.
    /// </summary>
    private WrapPanel TagWrap(IReadOnlyList<string> names, IBrush accent)
    {
        WrapPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 2),
        };

        foreach (string name in names)
        {
            TextBlock caption = new()
            {
                Text = name,
                Foreground = B("App.Text"),
                FontSize = 12,
            };

            Border pill = new()
            {
                Background = B("App.Control"),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(0, 2, 6, 2),
                Child = caption,
            };

            // Only a ref that still resolves is made to look clickable: a dead name
            // dressed as a link would be a button that does nothing.
            if (_rendered?.Refs.TryGetValue(name, out string? hash) is true && hash.Length > 0)
            {
                // Only the caption turns into link ink; the pill's border keeps the
                // accent, which is what an accent is for. App.Control equals App.Panel
                // in all four families, so the measured surface is the panel.
                caption.Foreground = B("App.Link");
                pill.Cursor = HandCursor;
                pill.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(pill).Properties.IsLeftButtonPressed)
                    {
                        e.Handled = true;
                        CommitNavigated?.Invoke(hash);
                    }
                };
                _linkTargets[pill] = hash;
            }

            panel.Children.Add(pill);
        }

        return panel;
    }

    /// <summary>
    ///  A ref name rendered as a bare link (no pill), used for the describe tag.
    ///  Falls back to plain text when the ref does not resolve.
    /// </summary>
    private Control RefLink(string name, bool isTag)
    {
        if (_rendered?.Refs.TryGetValue(name, out string? hash) is not true || hash.Length == 0)
        {
            return new TextBlock
            {
                Text = name,
                Foreground = B("App.Text"),
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        TextBlock link = new()
        {
            Text = name,
            // The tag half is a link, so it moves to App.Link; the branch half stays on
            // App.GraphGreen, which encodes "branch" and is not ours to retune here.
            Foreground = isTag ? B("App.Link") : B("App.GraphGreen"),
            FontWeight = FontWeight.SemiBold,
            TextDecorations = TextDecorations.Underline,
            Cursor = HandCursor,
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(link).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                CommitNavigated?.Invoke(hash);
            }
        };

        _linkTargets[link] = hash;
        return link;
    }

    private static void AddRow(Grid grid, ref int row, string label, Control value)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        TextBlock labelBlock = new()
        {
            Text = label,
            Foreground = B("App.TextDim"),
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 3, 16, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(value);
        row++;
    }

    /// <summary>
    ///  A deterministic, offline 5x5 mirrored identicon derived purely from a
    ///  string key (email/author) — mirrors the grid's per-commit avatar so the
    ///  same author yields the same glyph. No network, no gravatar.
    /// </summary>
    private readonly struct Identicon
    {
        private Identicon(bool[,] cells, Color foreground)
        {
            Cells = cells;
            Foreground = foreground;
        }

        public bool[,] Cells { get; }

        public Color Foreground { get; }

        public static Identicon Create(string? key)
        {
            key ??= string.Empty;
            ulong h = Fnv1a64(key.Trim().ToLowerInvariant());
            double hue = (h >> 40) % 360;
            Color fg = FromHsl(hue, 0.55, 0.60);
            bool[,] cells = new bool[5, 5];
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    bool on = ((h >> ((r * 3) + c)) & 1UL) == 1UL;
                    cells[r, c] = on;
                    cells[r, 4 - c] = on;
                }
            }

            return new Identicon(cells, fg);
        }

        private static ulong Fnv1a64(string s)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char ch in s)
            {
                hash ^= ch;
                hash *= 1099511628211UL;
            }

            return hash;
        }

        private static Color FromHsl(double hDeg, double s, double l)
        {
            double c = (1 - Math.Abs((2 * l) - 1)) * s;
            double hp = hDeg / 60.0;
            double x = c * (1 - Math.Abs((hp % 2) - 1));
            double r1 = 0, g1 = 0, b1 = 0;
            if (hp < 1) { r1 = c; g1 = x; }
            else if (hp < 2) { r1 = x; g1 = c; }
            else if (hp < 3) { g1 = c; b1 = x; }
            else if (hp < 4) { g1 = x; b1 = c; }
            else if (hp < 5) { r1 = x; b1 = c; }
            else { r1 = c; b1 = x; }

            double m = l - (c / 2);
            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }
    }

    private sealed class AvatarControl : Control
    {
        private readonly Identicon _icon;

        public AvatarControl(Identicon icon)
        {
            _icon = icon;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext context)
        {
            double size = Math.Min(Bounds.Width, Bounds.Height);
            if (size <= 0)
            {
                return;
            }

            Color fg = _icon.Foreground;
            var backdrop = new SolidColorBrush(Color.FromArgb(0x33, fg.R, fg.G, fg.B));
            context.DrawRectangle(backdrop, null, new RoundedRect(new Rect(0, 0, size, size), 5));

            var brush = new SolidColorBrush(fg);
            double cell = size / 5.0;
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 5; c++)
                {
                    if (_icon.Cells[r, c])
                    {
                        context.FillRectangle(brush, new Rect(c * cell, r * cell, cell, cell));
                    }
                }
            }
        }
    }
}
