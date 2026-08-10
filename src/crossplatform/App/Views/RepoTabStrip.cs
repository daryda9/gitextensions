using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  One open tab in the strip: the repository it shows, whether the user has committed
///  to keeping it, and the slots <see cref="MainWindow"/> uses to remember where the
///  user was inside it.
/// </summary>
/// <remarks>
///  <para><b>A tab is not a repository.</b> Several tabs may stand for the SAME working
///  directory — VS Code opens the same file twice, a browser the same page twice — so a
///  user can keep two points of view on one repository (two commits, two bottom panes)
///  without losing either. <see cref="Id"/>, not <see cref="Path"/>, is therefore what
///  identifies a tab: every operation of the strip takes an entry or an id, and the path
///  is demoted to an attribute of the tab.</para>
///
///  <para><see cref="Path"/> stays <c>init</c>-only on purpose: a tab that could be
///  repointed at another repository would quietly invalidate <see cref="SelectedCommit"/>
///  and <see cref="BottomTab"/> without anyone noticing. Re-using the preview slot for a
///  different repository therefore replaces the entry rather than mutating it, which
///  drops the stale per-tab state as a side effect of the only operation that should
///  drop it.</para>
/// </remarks>
public sealed class RepoTabEntry
{
    /// <summary>
    ///  The tab's own identity, independent of the repository behind it.
    ///
    ///  <para>A random GUID rather than a counter: the id is written to
    ///  <c>ui-state.json</c> and read back, so it has to survive a restart, and it has to
    ///  stay unique against tabs restored from that file while new ones are being opened.
    ///  A counter would restart at zero every launch and collide with them. It is opaque
    ///  and never shown — the strip only ever compares it.</para>
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Absolute path of the repository this tab stands for.</summary>
    public string Path { get; init; } = "";

    /// <summary>
    ///  Whether the tab is here to stay. <c>false</c> is a <em>preview</em> tab —
    ///  drawn in italics, and the slot the next unpinned repository takes over.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>The commit selected in this repository. Owned by <see cref="MainWindow"/>.</summary>
    public string? SelectedCommit { get; set; }

    /// <summary>The bottom pane this repository was left on. Owned by <see cref="MainWindow"/>.</summary>
    public string? BottomTab { get; set; }
}

/// <summary>
///  The strip of open repositories, docked under the toolbar: VS Code's editor tabs,
///  with a repository behind each tab instead of a file.
/// </summary>
/// <remarks>
///  <para><b>Why preview tabs exist.</b> Browsing repositories is a lot like browsing
///  files in an editor: most of the opens are a look — a submodule, a worktree, an
///  entry from "Open recent" — and only a few are work. If every open appended a tab
///  the strip would be a scroll bar with labels on it after ten minutes, and the two
///  repositories the user actually alternates between would be lost in it. So an
///  ordinary open lands in a single reusable slot (italic = "I am not keeping this"),
///  and the user promotes it by double-clicking, by "Keep open", or by opening it
///  deliberately as pinned. Nothing is ever taken away silently: the only tab a new
///  preview can displace is the one that was itself never claimed.</para>
///
///  <para><b>Why the close button keeps its space.</b> It is only PAINTED on the active
///  tab and under the pointer, but it is never collapsed — it fades with
///  <see cref="Visual.Opacity"/> and stops hit-testing. Collapsing it would re-measure
///  the tab, so every label in the strip would jump sideways as the pointer crossed it,
///  and the tab under the pointer would shrink out from under the click the user was
///  already making. Reserved space costs 16px per tab and makes the strip stand still.</para>
///
///  <para><b>Colours are read as live brush instances</b> from
///  <c>Application.Current.Resources</c> (see <see cref="Icons.Tint"/>): the theme and
///  the Modern/Classic switch recolour those brushes in place, so a tab painted with
///  the instance follows both, and a tab painted with a copy would freeze at the theme
///  in force when it was built.</para>
///
///  <para>The control is state and paint only — it never touches a repository. The host
///  reacts to <see cref="Activated"/>, <see cref="Emptied"/> and <see cref="Changed"/>.</para>
/// </remarks>
public sealed class RepoTabStrip : UserControl
{
    // A wheel notch scrolls about one tab's worth. The strip is one row high, so the
    // vertical wheel is free real estate and is the only way most mice can reach it.
    private const double WheelStep = 120;

    // The close affordance: small enough not to grow the row, big enough to hit.
    private const double CloseSize = 16;

    private readonly List<RepoTabEntry> _tabs = [];
    private readonly Dictionary<RepoTabEntry, TabVisual> _visuals = [];
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll;

    private RepoTabEntry? _active;

    /// <summary>Builds an empty strip.</summary>
    public RepoTabStrip()
    {
        // The close buttons are bar buttons: flat, a fill only under the pointer. The
        // house look, installed on this control so it beats the app-wide Fluent one.
        BarButtonStyles.Apply(Styles);

        _scroll = new ScrollViewer
        {
            Content = _strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // Tunnelling: the tab under the pointer must not eat the wheel first, and the
        // ancestor that would otherwise scroll is a whole pane away from where the user
        // is pointing.
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        Content = new Border
        {
            Background = B("App.PanelAlt"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _scroll,
        };

        // Reordering listens on the strip as a whole, not on each tab: once a drag is
        // under way the pointer is captured here, and the tab it started on may well
        // have moved out from under it.
        PointerMoved += OnPointerMovedOverStrip;
        PointerReleased += (_, _) => EndDrag();
        PointerCaptureLost += (_, _) => EndDrag();

        // Nothing to lay out when no repository is open; the host shows the dashboard.
        IsVisible = false;
    }

    /// <summary>The open repositories, left to right.</summary>
    public IReadOnlyList<RepoTabEntry> Tabs => _tabs;

    /// <summary>The tab whose repository is loaded, or <c>null</c> when the strip is empty.</summary>
    public RepoTabEntry? Active => _active;

    /// <summary>
    ///  Raised when the active tab changes — a click, an <see cref="Open"/>, or a
    ///  <see cref="Close"/> falling back to a neighbour. The host loads the repository.
    /// </summary>
    public event Action<RepoTabEntry>? Activated;

    /// <summary>Raised when the last tab was closed; the host shows the dashboard.</summary>
    public event Action? Emptied;

    /// <summary>
    ///  Raised for every left click on a tab, including a click on the tab that is
    ///  already active — the case <see cref="Activated"/> swallows. Hosts that show
    ///  something else over the work area (the dashboard) use it to come back.
    /// </summary>
    public event Action<RepoTabEntry>? Picked;

    /// <summary>
    ///  Raised after any add / close / pin / activate, for persistence. Always AFTER
    ///  <see cref="Activated"/> / <see cref="Emptied"/>, so what the host writes out is
    ///  the settled state and not the one halfway through the operation.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    ///  Raised on the tab about to be duplicated, BEFORE the copy is taken.
    ///
    ///  <para>The strip only ever sees the per-tab state the host has already written
    ///  into the entry, and the host writes it when a tab is LEFT. For the tab that is
    ///  on screen that state is therefore stale by exactly the work the user did since
    ///  arriving — which is all of it, and precisely what they expect the duplicate to
    ///  inherit. So the copy asks first: the host flushes the live view state into the
    ///  source, and only then is it cloned.</para>
    /// </summary>
    public event Action<RepoTabEntry>? Duplicating;

    /// <summary>
    ///  Opens a repository, or re-activates it when it is already open.
    ///
    ///  <para>Already open: activated, and additionally pinned when <paramref name="pinned"/>
    ///  says so. Not open and pinned: a new tab right after the active one — next to what
    ///  the user was looking at, which is where they were looking. Not open and unpinned:
    ///  the preview slot, re-used if one exists (same position, new entry, per-tab state
    ///  gone with the old one) and appended otherwise.</para>
    ///
    ///  <para><b>Opening stays one tab per repository even though the strip now allows
    ///  several.</b> Every door into a repository comes through here — the picker, a
    ///  clone, a drop, the tree — and none of them means "and again, separately"; a
    ///  second tab is worth having only when the user asks for it in as many words, which
    ///  is what <see cref="Duplicate"/> and its menu entry are for.</para>
    /// </summary>
    /// <returns>The entry now standing for <paramref name="path"/>.</returns>
    public RepoTabEntry Open(string path, bool pinned)
    {
        // The active tab wins the "already open" lookup when it matches, so re-opening the
        // repository you are looking at never jumps to an older duplicate of it.
        RepoTabEntry? existing = _active is not null && SamePath(_active.Path, path) ? _active : Find(path);
        if (existing is not null)
        {
            // Re-opening deliberately is one of the ways to claim a preview tab.
            if (pinned && !existing.Pinned)
            {
                existing.Pinned = true;
                Sync();
            }

            SetActive(existing);
            Changed?.Invoke();
            return existing;
        }

        RepoTabEntry entry = new() { Path = path, Pinned = pinned };
        if (pinned)
        {
            int at = _active is null ? _tabs.Count : _tabs.IndexOf(_active) + 1;
            Insert(at, entry);
        }
        else
        {
            int slot = _tabs.FindIndex(static t => !t.Pinned);
            if (slot >= 0)
            {
                Drop(_tabs[slot]);
                _tabs.RemoveAt(slot);
                Insert(slot, entry);
            }
            else
            {
                Insert(_tabs.Count, entry);
            }
        }

        SetActive(entry);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Activates <paramref name="entry"/>; a no-op when it is not in the strip.</summary>
    public void Activate(RepoTabEntry entry)
    {
        if (!_tabs.Contains(entry))
        {
            return;
        }

        SetActive(entry);
        Changed?.Invoke();
    }

    /// <summary>
    ///  Opens a second tab on <paramref name="source"/>'s repository, right beside it, and
    ///  activates it. The copy inherits the source's selected commit and bottom pane and
    ///  is independent from that moment on: the two entries are separate objects, and the
    ///  host writes each tab's state into the tab it is leaving.
    ///
    ///  <para>The copy is born PINNED whatever the source was. A duplicate is asked for
    ///  by name, and a preview slot is the one tab the next ordinary open silently takes
    ///  over — the strip must not hand the user a second view and then delete it on the
    ///  next single click in the tree.</para>
    ///
    ///  <para>No keyboard shortcut is bound to this on purpose. Ctrl+PageUp/Down are the
    ///  only chords the strip claims, and everything else in this window's reach is
    ///  already an upstream command; a duplicate is rare enough to live in the menu the
    ///  right button opens, where it is also discoverable.</para>
    /// </summary>
    /// <returns>The new entry, or <paramref name="source"/> when it is not in the strip.</returns>
    public RepoTabEntry Duplicate(RepoTabEntry source)
    {
        int at = _tabs.IndexOf(source);
        if (at < 0)
        {
            return source;
        }

        // Before the copy, not after: see Duplicating.
        Duplicating?.Invoke(source);

        RepoTabEntry copy = new()
        {
            Path = source.Path,
            Pinned = true,
            SelectedCommit = source.SelectedCommit,
            BottomTab = source.BottomTab,
        };

        Insert(at + 1, copy);
        SetActive(copy);
        Changed?.Invoke();
        return copy;
    }

    /// <summary>
    ///  Closes <paramref name="entry"/>. Closing the active one hands over to the
    ///  right-hand neighbour, else the left-hand one, else raises <see cref="Emptied"/> —
    ///  the right first, because that is the tab that visually takes the closed one's place.
    /// </summary>
    public void Close(RepoTabEntry entry)
    {
        int index = _tabs.IndexOf(entry);
        if (index < 0)
        {
            return;
        }

        bool wasActive = ReferenceEquals(entry, _active);
        Drop(entry);
        _tabs.RemoveAt(index);

        if (wasActive)
        {
            _active = null;
            if (_tabs.Count == 0)
            {
                Sync();
                Emptied?.Invoke();
                Changed?.Invoke();
                return;
            }

            SetActive(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        else
        {
            Sync();
        }

        Changed?.Invoke();
    }

    /// <summary>Claims <paramref name="entry"/>, so no preview open can take its slot.</summary>
    public void Pin(RepoTabEntry entry)
    {
        if (entry is not { Pinned: false } || !_tabs.Contains(entry))
        {
            return;
        }

        entry.Pinned = true;
        Sync();
        Changed?.Invoke();
    }

    /// <summary>
    ///  Replaces the whole set, for restoring a persisted session. Raises
    ///  <see cref="Changed"/> but NOT <see cref="Activated"/>: the host is the one
    ///  restoring, so it already knows which repository it is about to load, and an
    ///  event here would make it load it twice.
    ///
    ///  <para><paramref name="activeId"/> is an <see cref="RepoTabEntry.Id"/>, not a
    ///  path: with duplicates a path names any number of tabs, so it can no longer say
    ///  which one was in front.</para>
    /// </summary>
    public void Restore(IEnumerable<RepoTabEntry> tabs, string? activeId)
    {
        foreach (RepoTabEntry old in _tabs)
        {
            Drop(old);
        }

        _tabs.Clear();
        _active = null;
        _tabs.AddRange(tabs);
        _active = activeId is null ? null : _tabs.Find(t => string.Equals(t.Id, activeId, StringComparison.Ordinal));
        Sync();
        Changed?.Invoke();
    }

    // ---- model -------------------------------------------------------------

    // Used by Open() alone, which asks "is this repository already on screen anywhere".
    // Nothing else may look a tab up by path: with duplicates the answer is not unique.
    private RepoTabEntry? Find(string path) => _tabs.Find(t => SamePath(t.Path, path));

    private void Insert(int at, RepoTabEntry entry)
    {
        _tabs.Insert(Math.Clamp(at, 0, _tabs.Count), entry);
        Sync();
    }

    private void Drop(RepoTabEntry entry) => _visuals.Remove(entry);

    private void SetActive(RepoTabEntry entry)
    {
        bool changed = !ReferenceEquals(entry, _active);
        _active = entry;
        Sync();
        if (changed)
        {
            Activated?.Invoke(entry);
        }
    }

    // ---- drag to reorder ---------------------------------------------------

    // The tab a press landed on and where it landed. A drag only STARTS once the pointer
    // has travelled DragSlop, so an ordinary click — and the double click that pins —
    // never rearranges the strip by accident.
    private RepoTabEntry? _pressed;
    private Point _pressedAt;
    private bool _dragging;

    private const double DragSlop = 5;

    private void BeginDrag(RepoTabEntry entry, Point at)
    {
        _pressed = entry;
        _pressedAt = at;
        _dragging = false;
    }

    private void OnPointerMovedOverStrip(object? sender, PointerEventArgs e)
    {
        if (_pressed is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(_strip).Properties.IsLeftButtonPressed)
        {
            // The button came up somewhere we never heard about.
            EndDrag();
            return;
        }

        Point at = e.GetPosition(_strip);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _pressedAt.X) < DragSlop)
            {
                return;
            }

            _dragging = true;

            // Dragging a tab claims it, the way an editor pins the preview you start
            // arranging: a tab you are putting in a particular place is one you mean to
            // keep, and leaving it a preview would let the next single click delete the
            // arrangement you just made.
            Pin(_pressed);

            // From here every move and the release must reach THIS control even if the
            // pointer leaves the strip — a drag that wanders down into the tree would
            // otherwise never end, and the next click would resume it.
            e.Pointer.Capture(this);
            Visual(_pressed).SetDragging(true);
        }

        MoveTo(_pressed, IndexAt(at.X));
    }

    private void EndDrag()
    {
        if (_pressed is not null && _dragging)
        {
            Visual(_pressed).SetDragging(false);

            // The order is part of what the host persists, and a reorder changes nothing
            // else — no activation, no load.
            Changed?.Invoke();
        }

        _pressed = null;
        _dragging = false;
    }

    // The slot the pointer is over: the first tab whose MIDPOINT is to its right, which
    // is what makes a tab swap places as soon as it is dragged past half of its
    // neighbour rather than all the way across it.
    private int IndexAt(double x)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            Rect bounds = Visual(_tabs[i]).Root.Bounds;
            if (x < bounds.X + (bounds.Width / 2))
            {
                return i;
            }
        }

        return _tabs.Count - 1;
    }

    private void MoveTo(RepoTabEntry entry, int target)
    {
        int current = _tabs.IndexOf(entry);
        if (current < 0 || target < 0 || current == target)
        {
            return;
        }

        _tabs.RemoveAt(current);
        _tabs.Insert(Math.Clamp(target, 0, _tabs.Count), entry);
        Sync();
    }

    // ---- paint -------------------------------------------------------------

    // Rebuilds the row of children from the model, re-using each tab's visual so the
    // pointer-over state (and the pointer capture of a click in flight) survives a
    // neighbour appearing or disappearing.
    //
    // The children are touched ONLY when the sequence actually differs — a plain
    // activation repaints and nothing else. Clearing and re-adding the same controls
    // looks harmless and is not: re-parenting a control resets the input state Avalonia
    // keeps for it, and the first thing that costs is the DOUBLE CLICK — the press that
    // activates the tab used to re-parent it, so the second press arrived at a control
    // that had never seen the first and DoubleTapped never fired. It also breaks a drag
    // in flight, which is the other gesture that lives across several events.
    private void Sync()
    {
        if (!SameChildren())
        {
            _strip.Children.Clear();
            foreach (RepoTabEntry entry in _tabs)
            {
                _strip.Children.Add(Visual(entry).Root);
            }
        }

        // Labels are decided for the strip as a WHOLE, here, and handed to each visual:
        // the shortest text that still tells a tab apart depends on its neighbours (see
        // BuildLabels), so no tab can compute its own.
        IReadOnlyList<string> labels = BuildLabels(_tabs);
        for (int i = 0; i < _tabs.Count; i++)
        {
            Visual(_tabs[i]).Apply(ReferenceEquals(_tabs[i], _active), labels[i]);
        }

        IsVisible = _tabs.Count > 0;
    }

    private TabVisual Visual(RepoTabEntry entry)
    {
        if (!_visuals.TryGetValue(entry, out TabVisual? visual))
        {
            visual = Build(entry);
            _visuals[entry] = visual;
        }

        return visual;
    }

    // Whether the strip already holds exactly these tabs, in this order.
    private bool SameChildren()
    {
        if (_strip.Children.Count != _tabs.Count)
        {
            return false;
        }

        for (int i = 0; i < _tabs.Count; i++)
        {
            if (!_visuals.TryGetValue(_tabs[i], out TabVisual? visual)
                || !ReferenceEquals(_strip.Children[i], visual.Root))
            {
                return false;
            }
        }

        return true;
    }

    private TabVisual Build(RepoTabEntry entry)
    {
        TextBlock label = new()
        {
            // Placeholder only: the real text is the strip-wide label Sync computes, and
            // Sync always runs before this control can be seen.
            Text = Leaf(entry.Path),
            FontSize = Metrics.Text.Body,
            Foreground = B("App.Text"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        };

        Button close = new()
        {
            Content = "✕",
            FontSize = Metrics.Text.Caption,
            Foreground = B("App.TextDim"),
            Classes = { BarButtonStyles.Class },
            Width = CloseSize,
            Height = CloseSize,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, e) =>
        {
            // Without this the press underneath would activate the tab we just closed.
            e.Handled = true;
            Close(entry);
        };

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, close },
        };

        // Row 0 is the 2px accent rule of the active tab; it owns its own layout row so
        // that showing it never moves the label. Row 1 is the tab proper.
        Border accent = new() { Height = 2 };
        Grid body = new()
        {
            RowDefinitions = new RowDefinitions("2,*"),
            Children = { accent, row },
        };
        Grid.SetRow(accent, 0);
        Grid.SetRow(row, 1);
        // Air, not a height: the vertical inset is what keeps the label off the edges when
        // the "Large" UI size makes it taller than the row's MinHeight.
        row.Margin = new Thickness(Metrics.Space.Md, Metrics.Space.Xs, Metrics.Space.Sm, Metrics.Space.Xs);

        Border root = new()
        {
            // The 1px rule on the right is the separator between two tabs.
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            // MinHeight, never Height: at the "Large" UI size the label is taller than
            // any fixed row would be, and a clipped repository name is unreadable.
            MinHeight = Metrics.Density.ControlMinHeight,
            Child = body,
        };
        ToolTip.SetTip(root, entry.Path);

        TabVisual visual = new(entry, root, accent, label, close);

        root.PointerEntered += (_, _) => visual.SetHover(true);
        root.PointerExited += (_, _) => visual.SetHover(false);
        root.PointerPressed += (_, e) =>
        {
            PointerPointProperties props = e.GetCurrentPoint(root).Properties;
            if (props.IsMiddleButtonPressed)
            {
                // The editor gesture: middle click discards what is under the pointer.
                e.Handled = true;
                Close(entry);
            }
            else if (props.IsLeftButtonPressed)
            {
                // The second press of a double click, read from the press itself rather
                // than waited for as a DoubleTapped: this is the gesture that claims a
                // preview tab, and it has to survive whatever the FIRST press did to the
                // strip (an activation, a load, a rebuild).
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    Pin(entry);
                    return;
                }

                BeginDrag(entry, e.GetPosition(_strip));
                Activate(entry);

                // Raised even when the tab was ALREADY the active one, which
                // Activate deliberately keeps silent. The host needs the click
                // itself in one case: the dashboard is on screen (the user asked
                // for it from the menu) and the repository the strip still calls
                // active is not loaded any more — without this, clicking it would
                // be the one tab in the strip that does nothing.
                Picked?.Invoke(entry);
            }
        };

        root.ContextMenu = BuildMenu(entry);
        return visual;
    }

    private ContextMenu BuildMenu(RepoTabEntry entry)
    {
        MenuItem keep = new();
        keep.Click += (_, _) => Pin(entry);

        MenuItem duplicate = new();
        duplicate.Click += (_, _) => Duplicate(entry);

        MenuItem close = new();
        close.Click += (_, _) => Close(entry);

        MenuItem others = new();
        others.Click += (_, _) => CloseOthers(entry);

        MenuItem all = new();
        all.Click += (_, _) => CloseAll();

        ContextMenu menu = new()
        {
            ItemsSource = new Control[] { keep, duplicate, new Separator(), close, others, all },
        };

        // Headers are written when the popup opens rather than once at build time: it
        // costs nothing, and it makes the menu follow both the tab's own state ("Keep
        // open" is meaningless on a tab that is already kept) and a language switch,
        // without a subscription to unhook when the tab is closed.
        menu.Opening += (_, _) =>
        {
            keep.Header = MenuText.Escape(T("Keep open"));
            keep.IsVisible = !entry.Pinned;
            // No entry in the upstream catalogue says this — the strip is the port's own —
            // so the English literal is both the text and the lookup key, and a catalogue
            // that grows one later starts translating it without a code change.
            duplicate.Header = MenuText.Escape(T("Duplicate tab"));
            close.Header = MenuText.Escape(T("TranslatedStrings/_closeText.Text", "Close"));
            others.Header = MenuText.Escape(T("Close others"));
            all.Header = MenuText.Escape(T("Close all"));
        };

        return menu;
    }

    // Both bulk closes are ONE operation, not a loop over Close(): closing the tabs one
    // at a time would walk the active tab across every survivor on its way out, and the
    // host loads a repository per Activated. The survivor is chosen first, then the rest
    // go in silence.
    private void CloseOthers(RepoTabEntry keep)
    {
        SetActive(keep);
        foreach (RepoTabEntry other in _tabs)
        {
            if (!ReferenceEquals(other, keep))
            {
                Drop(other);
            }
        }

        _tabs.RemoveAll(t => !ReferenceEquals(t, keep));
        Sync();
        Changed?.Invoke();
    }

    private void CloseAll()
    {
        foreach (RepoTabEntry entry in _tabs)
        {
            Drop(entry);
        }

        _tabs.Clear();
        _active = null;
        Sync();
        Emptied?.Invoke();
        Changed?.Invoke();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll || scroll.Extent.Width <= scroll.Viewport.Width)
        {
            return;
        }

        // Either axis drives the one axis this strip has: a plain wheel is what most
        // mice have, a tilt wheel or a touchpad swipe is what the rest have.
        double delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        double target = Math.Clamp(
            scroll.Offset.X - (delta * WheelStep),
            0,
            Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width));
        scroll.Offset = scroll.Offset.WithX(target);
        e.Handled = true;
    }

    // ---- helpers -----------------------------------------------------------

    // The folder name is what tells two repositories apart; the full path is on the
    // tooltip. A path that is nothing but separators has no folder name, and showing
    // the raw string beats showing an empty tab.
    private static string Leaf(string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        string name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : (path.Length > 0 ? path : "?");
    }

    /// <summary>
    ///  The text of every tab, in strip order. Two tabs the eye cannot tell apart are two
    ///  tabs the user will click at random, so the labels are decided TOGETHER and the
    ///  same two collisions VS Code solves are solved the same two ways.
    ///
    ///  <para><b>Different repositories, same folder name</b> (<c>~/work/api</c> and
    ///  <c>~/toys/api</c>) get more of their path, one parent segment at a time, until the
    ///  colliding paths differ — the shortest text that carries the answer, which is
    ///  exactly VS Code's rule for two files called <c>index.ts</c>. Five segments is the
    ///  bail-out: beyond that the label is longer than the tab and the tooltip, which
    ///  always holds the full path, is the better place to look.</para>
    ///
    ///  <para><b>The same repository twice</b> cannot be disambiguated by path at all —
    ///  there is only one — so those tabs are NUMBERED, "<c>api (1)</c>", "<c>api (2)</c>",
    ///  the convention every browser and window manager uses for a second view of one
    ///  thing. The number is the position among the copies from left to right, not a
    ///  birth order: the eye counts left to right, and a birth order would leave a gap the
    ///  moment a middle copy is closed. Dragging one copy past another therefore swaps
    ///  their numbers, which is the honest reading of a number that means "position".</para>
    ///
    ///  <para>Numbering only ever appears while a duplicate is open: a single tab on a
    ///  repository is labelled exactly as before.</para>
    /// </summary>
    private static IReadOnlyList<string> BuildLabels(List<RepoTabEntry> tabs)
    {
        List<string> labels = new(tabs.Count);
        foreach (RepoTabEntry tab in tabs)
        {
            labels.Add(Leaf(tab.Path));
        }

        // Pass 1 — one label per distinct PATH, grown until distinct paths that share a
        // leaf no longer share a label. Duplicates of one path are one member here.
        Dictionary<string, string> byPath = [];
        Dictionary<string, List<string>> groups = [];
        foreach (RepoTabEntry tab in tabs)
        {
            string key = PathKey(tab.Path);
            if (byPath.ContainsKey(key))
            {
                continue;
            }

            byPath[key] = Leaf(tab.Path);
            if (!groups.TryGetValue(byPath[key], out List<string>? peers))
            {
                peers = [];
                groups[byPath[key]] = peers;
            }

            peers.Add(key);
        }

        foreach (List<string> peers in groups.Values)
        {
            if (peers.Count < 2)
            {
                continue;
            }

            for (int depth = 2; depth <= 5; depth++)
            {
                HashSet<string> tails = new(StringComparer.Ordinal);
                bool distinct = true;
                foreach (string peer in peers)
                {
                    distinct &= tails.Add(Tail(peer, depth));
                }

                // The last depth is taken even when it did NOT separate them: it is still
                // the most informative label available, and stopping short would leave the
                // bare folder name, which carries less.
                if (distinct || depth == 5)
                {
                    foreach (string peer in peers)
                    {
                        byPath[peer] = Tail(peer, depth);
                    }

                    break;
                }
            }
        }

        // Pass 2 — the copies of one path are numbered among themselves.
        Dictionary<string, int> total = [];
        foreach (RepoTabEntry tab in tabs)
        {
            string key = PathKey(tab.Path);
            total[key] = total.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        Dictionary<string, int> seen = [];
        for (int i = 0; i < tabs.Count; i++)
        {
            string key = PathKey(tabs[i].Path);
            labels[i] = byPath[key];
            if (total[key] > 1)
            {
                seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
                labels[i] = $"{labels[i]} ({seen[key]})";
            }
        }

        return labels;
    }

    // The last <paramref name="segments"/> parts of a path, as the user reads them. Not a
    // prefix of the path: what distinguishes ~/work/api from ~/toys/api is the segment
    // just above the leaf, and it is also the one the user recognises.
    private static string Tail(string path, int segments)
    {
        string[] parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Leaf(path);
        }

        int take = Math.Min(segments, parts.Length);
        return string.Join('/', parts[^take..]);
    }

    // The identity of a working directory, for grouping only — SamePath's normalisation
    // squeezed into a key so the grouping is a dictionary lookup rather than an O(n²)
    // sweep. A path too broken to normalise stands for itself, which keeps it in its own
    // group instead of merging every broken path into one.
    private static string PathKey(string path)
    {
        try
        {
            string full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // The house rule for "same repository", replicated locally so the strip stays a
    // leaf control: full-path normalisation, no trailing separator, case-sensitive
    // everywhere but Windows. A path git cannot have (bad characters, too long) is
    // simply not equal to anything.
    private static bool SamePath(string path, string? other)
    {
        if (string.IsNullOrWhiteSpace(other) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string left = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            string right = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(other));
            return string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static IBrush B(string key) => Icons.Tint(key) ?? Brushes.Transparent;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    /// <summary>
    ///  One tab's controls plus the two bits of state that decide how it is painted.
    ///  Kept together so the paint is a single function of (active, hovered) and cannot
    ///  drift between the four call sites that trigger it.
    /// </summary>
    private sealed class TabVisual(
        RepoTabEntry entry,
        Border root,
        Border accent,
        TextBlock label,
        Button close)
    {
        private bool _hovered;
        private bool _active;
        private bool _dragging;

        internal Border Root => root;

        // A tab being dragged is half-transparent, which is the whole feedback it needs:
        // the reorder itself happens live under the pointer, so the strip already shows
        // where the tab is going. Nothing is torn out of the layout and no gap is drawn.
        internal void SetDragging(bool dragging)
        {
            _dragging = dragging;
            Paint();
        }

        internal void SetHover(bool hovered)
        {
            _hovered = hovered;
            Paint();
        }

        internal void Apply(bool active, string text)
        {
            _active = active;
            label.Text = text;
            // Italic IS the preview state — the one visual difference the user has to
            // read at a glance before double-clicking makes it permanent.
            label.FontStyle = entry.Pinned ? FontStyle.Normal : FontStyle.Italic;
            label.FontWeight = active ? Metrics.Text.ActiveWeight : Metrics.Text.BodyWeight;
            ToolTip.SetTip(root, entry.Path);
            Paint();
        }

        private void Paint()
        {
            root.Background = _active ? B("App.Panel") : (_hovered ? B("App.Hover") : Brushes.Transparent);
            accent.Background = _active ? B("App.Accent") : Brushes.Transparent;
            label.Foreground = _active ? B("App.Text") : B("App.TextDim");

            // Faded, not collapsed: see the class remarks. Hit-testing follows the paint
            // so an invisible button can never swallow a click meant for the tab.
            bool shown = _active || _hovered;
            close.Opacity = shown ? 1 : 0;
            close.IsHitTestVisible = shown;
            root.Opacity = _dragging ? 0.6 : 1;
        }
    }
}
