using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
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

    // The extra class the ✕ carries on top of the bar-button one, so its hover fill can
    // be raised without touching every other bar button in the app.
    private const string CloseClass = "tabclose";

    private readonly List<RepoTabEntry> _tabs = [];
    private readonly Dictionary<RepoTabEntry, TabVisual> _visuals = [];
    private readonly TabsPanel _strip = new();
    private readonly ScrollViewer _scroll;

    private RepoTabEntry? _active;

    /// <summary>Builds an empty strip.</summary>
    public RepoTabStrip()
    {
        // The close buttons are bar buttons: flat, a fill only under the pointer. The
        // house look, installed on this control so it beats the app-wide Fluent one.
        BarButtonStyles.Apply(Styles);

        // ...and then the ✕ is given a STRONGER fill than a bar button's, because on this
        // one strip the bar button's own hover fill says nothing. A bar button hovers to
        // App.Hover, and App.Hover is exactly what the tab UNDER the button is already
        // painted with while the pointer is inside it — measured, both #41424A in the
        // modern dark theme — so crossing from the tab onto the ✕ changed not one pixel
        // of fill and the user could not tell whether the next click would close the tab
        // or merely select it. App.Pressed is the next step of the same neutral ramp
        // (#53545B dark, #BABAC0 light), which is a visible lift over App.Hover in both
        // themes and needs no colour of its own. Declared AFTER BarButtonStyles.Apply so
        // that, at equal specificity, this setter is the one that wins.
        // The glyph turns App.IconRed at the same moment, which is the half of the feedback
        // that says WHAT the button does — the one destructive control in the strip — and is
        // legible where a single step of a neutral fill on its own is subtle. It has to be a
        // style and not an assignment on the Button: Fluent's template repaints the CONTENT
        // PRESENTER's Foreground in the pointerover state, and a setter on the presenter beats
        // the Foreground the presenter inherits from the button's own local value — which is
        // why the ✕ used to brighten a little under the pointer and never did anything else.
        Styles.Add(CloseHover(B("App.Pressed"), B("App.IconRed")));

        _scroll = new ScrollViewer
        {
            Content = _strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,

            // The bar must take LAYOUT space, not float over the tabs. Fluent's default
            // (AllowAutoHide) draws a thin line while idle and then swells to its full
            // ~12px thumb, with its two arrow buttons, the moment the pointer enters the
            // scroller — and it draws that on TOP of the content. Everywhere else in the
            // app the content is tall enough for that to be a stripe along the bottom;
            // here the whole viewport is one 24px row, so the swollen bar covers the
            // repository names it is meant to help reach, in the one situation where the
            // strip has more tabs than fit and the user most needs to read them. Measured
            // in the light theme at 560px with 14 tabs: every label went grey behind an
            // opaque slab and stayed there. Reserving the row costs a few pixels of height
            // only while the strip actually overflows (the visibility is still Auto).
            AllowAutoHide = false,
        };

        // Tunnelling: the tab under the pointer must not eat the wheel first, and the
        // ancestor that would otherwise scroll is a whole pane away from where the user
        // is pointing.
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        Content = new Border
        {
            // Same flattening as the pane tabs below (MainWindow's _bottom): the strip
            // takes the surface of what it sits over instead of App.PanelAlt, so the
            // repository tabs are a row of labels on the window rather than a raised
            // band across it. The active tab keeps its accent bar, which is what marks
            // it now that its App.Panel fill matches the strip.
            Background = B("App.Panel"),
            BorderBrush = B("App.Rule"),
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

    /// <summary>
    ///  Hands the row of tabs the width it has to share before it is measured.
    ///
    ///  <para>The tabs live in a ScrollViewer, which measures its content with an
    ///  INFINITE width — that is how a scroller works, and it is why the row could never
    ///  tell "there is plenty of room" from "the window is half its size". The strip
    ///  itself is the only control in the chain that is given the real width, so it is
    ///  the one that has to pass it on. Written before <c>base</c> measures the child, so
    ///  the new budget is used by the very same pass rather than by the next one.</para>
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _strip.Budget = availableSize.Width;
        return base.MeasureOverride(availableSize);
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
        IReadOnlyList<(string Path, string Number)> labels = BuildLabels(_tabs);
        IReadOnlyList<(IBrush? Colour, string? Root)> checkouts = BuildCheckouts(_tabs);
        for (int i = 0; i < _tabs.Count; i++)
        {
            Visual(_tabs[i]).Apply(
                ReferenceEquals(_tabs[i], _active),
                labels[i].Path,
                labels[i].Number,
                checkouts[i].Colour,
                checkouts[i].Root);
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
        PathLabel label = new()
        {
            // Placeholder only: the real text is the strip-wide label Sync computes, and
            // Sync always runs before this control can be seen.
            Text = Leaf(entry.Path),
            FontSize = Metrics.Text.Body,
            Foreground = B("App.Text"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
        };

        Button close = new()
        {
            Content = "✕",
            FontSize = Metrics.Text.Caption,
            Foreground = B("App.TextDim"),
            Classes = { BarButtonStyles.Class, CloseClass },
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

        // The checkout chip. Zero width until Sync decides there is more than one
        // checkout open: a colour that is always on says nothing, and the strip's
        // whole labelling rule is "disambiguate only what is ambiguous".
        Border checkout = new()
        {
            Width = 0,
            Height = 12,
            CornerRadius = Metrics.Radius.SmCorner,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };

        // A Grid, not the StackPanel it used to be: a horizontal StackPanel measures its
        // children with an infinite width, so the label would never learn that the tab
        // around it had been squeezed and could not decide how to elide. The star column
        // hands it exactly the room the chip and the close button leave over. The gaps the
        // StackPanel's Spacing used to draw are margins now, and the chip's is set with
        // the chip itself so a tab without one keeps the label where it always was.
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { checkout, label, close },
        };
        Grid.SetColumn(checkout, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(close, 2);
        label.Margin = new Thickness(0, 0, Metrics.Space.Xs, 0);

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
            BorderBrush = B("App.Rule"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            // MinHeight, never Height: at the "Large" UI size the label is taller than
            // any fixed row would be, and a clipped repository name is unreadable.
            MinHeight = Metrics.Density.ControlMinHeight,
            // A squeezed tab is given less width than its content asked for, and without
            // this the close button would simply paint over its neighbour.
            ClipToBounds = true,
            Child = body,
        };
        // Placeholder only, like the label above: Apply writes the real tip, and it writes
        // it through PathDisplay.CollapseHome for the reason given there.
        ToolTip.SetTip(root, PathDisplay.CollapseHome(entry.Path));

        TabVisual visual = new(entry, root, accent, label, close, checkout);

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
    ///  Which checkout each tab belongs to, as a colour and the checkout's path.
    ///
    ///  <para><b>The case this answers</b>: two clones of one project open side by
    ///  side, each with the same submodules. Their submodule tabs carry the same
    ///  folder name, and although <see cref="BuildLabels"/> lengthens the label
    ///  until the two paths differ, the difference lands in the MIDDLE of two
    ///  otherwise identical strings — which is read, not seen. A colour shared by
    ///  every tab of one checkout is seen.</para>
    ///
    ///  <para><b>Off unless it says something.</b> With a single checkout open every
    ///  tab would get the same colour, which is decoration, so nothing is painted
    ///  until a second checkout appears. Same rule the labels follow: disambiguate
    ///  what is ambiguous and leave the rest alone.</para>
    ///
    ///  <para>The colour follows the order a checkout first appears in the strip,
    ///  from the palette's own icon hues, so it moves with the theme and does not
    ///  depend on a hash of a path that would change when the folder is renamed.</para>
    /// </summary>
    private static IReadOnlyList<(IBrush? Colour, string? Root)> BuildCheckouts(List<RepoTabEntry> tabs)
    {
        string[] roots = new string[tabs.Count];
        List<string> order = [];
        for (int i = 0; i < tabs.Count; i++)
        {
            roots[i] = Services.WorkspaceRoot.Of(tabs[i].Path);
            if (!order.Contains(roots[i], StringComparer.Ordinal))
            {
                order.Add(roots[i]);
            }
        }

        List<(IBrush?, string?)> result = new(tabs.Count);
        for (int i = 0; i < tabs.Count; i++)
        {
            if (order.Count < 2)
            {
                result.Add((null, null));
                continue;
            }

            int index = order.IndexOf(roots[i]);
            string? shown = string.Equals(roots[i], tabs[i].Path.TrimEnd('/', '\\'), StringComparison.Ordinal)
                ? null
                : roots[i];
            result.Add((B(CheckoutKeys[index % CheckoutKeys.Length]), shown));
        }

        return result;
    }

    // Icon hues rather than literals: they are already chosen to be distinguishable
    // from each other and readable in both themes, and ThemeManager recolours the
    // brush instances in place when the theme changes.
    private static readonly string[] CheckoutKeys =
    [
        "App.IconBlue",
        "App.IconAmber",
        "App.IconPurple",
        "App.IconGreen",
        "App.IconCyan",
        "App.IconRed",
    ];

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
    ///
    ///  <para><b>The number is returned SEPARATELY from the path, and that is a
    ///  correction.</b> While it was glued onto the end of one string, the elision ate it:
    ///  a label like <c>a-very-long-repository-name (1)</c> has no <c>/</c> in it, so
    ///  <see cref="PathLabel.Choose"/> went straight to trimming the leaf at its END and
    ///  the <c>(1)</c> was the first thing to go — leaving two tabs on the same repository
    ///  looking IDENTICAL, which is the exact confusion the numbering exists to prevent.
    ///  Measured with two tabs on <c>a-very-long-repository-name-that-will-not-fit</c>: at
    ///  the Large UI size, and at ANY window width (the label's own 220px cap is enough on
    ///  its own), both tabs read <c>a-very-long-repository-…</c> with no number at all.
    ///  Handed over on its own the number becomes a suffix the label protects from the
    ///  elision, so the NAME degrades and the answer survives.</para>
    /// </summary>
    /// <returns>Per tab, the path text to elide and the <c>" (n)"</c> suffix to keep
    ///  whole, which is empty for every tab that has no duplicate.</returns>
    private static IReadOnlyList<(string Path, string Number)> BuildLabels(List<RepoTabEntry> tabs)
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
        List<(string, string)> result = new(tabs.Count);
        for (int i = 0; i < tabs.Count; i++)
        {
            string key = PathKey(tabs[i].Path);
            labels[i] = byPath[key];
            string number = "";
            if (total[key] > 1)
            {
                seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1;
                number = $" ({seen[key]})";
            }

            result.Add((labels[i], number));
        }

        return result;
    }

    // The last <paramref name="segments"/> parts of a path, as the user reads them. Not a
    // prefix of the path: what distinguishes ~/work/api from ~/toys/api is the segment
    // just above the leaf, and it is also the one the user recognises.
    private static string Tail(string path, int segments)
    {
        // Collapsed FIRST, so a label that reaches all the way up to the home directory
        // spells it "~/work/api" and not "home/dario/work/api" — the same spelling the
        // tooltip and the toolbar use, and two segments shorter into the bargain. It
        // cannot merge two different repositories into one label: the substitution is a
        // prefix rewrite of one fixed directory, so distinct paths stay distinct.
        path = PathDisplay.CollapseHome(path);
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

    // The hover fill of the ✕, as a style: Fluent paints a button's chrome through the
    // ContentPresenter inside its template, so the Button's own Background property never
    // reaches the screen and this cannot be a plain assignment. The brush is the LIVE
    // palette instance (see the class remarks): ThemeManager recolours it in place, so the
    // fill follows a theme switch that happens after this style was built.
    private static Style CloseHover(IBrush fill, IBrush glyph)
    {
        Style style = new(x => x.OfType<Button>()
            .Class(BarButtonStyles.Class)
            .Class(CloseClass)
            .Class(":pointerover")
            .Template()
            .OfType<ContentPresenter>()
            .Name("PART_ContentPresenter"));
        style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, fill));
        style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, glyph));
        return style;
    }

    private static IBrush B(string key) => Icons.Tint(key) ?? Brushes.Transparent;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    /// <summary>
    ///  The row of tabs: horizontal, natural width while the tabs fit, and squeezed
    ///  proportionally down to <see cref="MinTabWidth"/> once they do not.
    ///
    ///  <para><b>Why not a StackPanel.</b> A StackPanel gives every tab the width it asks
    ///  for and lets the total run off the end, which the surrounding ScrollViewer then
    ///  hides behind a scrollbar. That is the right last resort and the wrong first
    ///  answer: with three tabs open in a narrow window there is room for all three, just
    ///  not for all three at full length, and a scrollbar hides a tab that could simply
    ///  have been shortened. So the width is shared first and scrolled only when even the
    ///  floor does not fit — the floor being the point below which a tab stops being a
    ///  label and becomes a stub.</para>
    ///
    ///  <para><b>The widest tab pays first.</b> The room is shared by capping, not by
    ///  scaling: one ceiling is found that every tab has to fit under, and a tab already
    ///  narrower than it is left completely alone. So <c>api</c>, which needs 60px and
    ///  costs nobody anything, keeps its 60px, and the pressure lands on the labels that
    ///  have something to give. It is the standard max-min share, and it replaces a
    ///  proportional scale that got the priority exactly backwards: the scale stopped as
    ///  soon as the accumulated floors ate the budget and RETURNED, leaving every tab that
    ///  had not yet been pinned at its FULL natural width — measured with 14 tabs at
    ///  1000px, thirteen of them sat on the 96px floor while the one 240px tab kept all
    ///  240 and the strip scrolled. The tab with the most to give was the only one giving
    ///  nothing.</para>
    ///
    ///  <para>The 96px floor is unchanged, and so is the last resort: when even the floors
    ///  do not fit, every tab is put ON its floor and the ScrollViewer takes over. That is
    ///  also a correction — the row scrolled either way, but before it scrolled with the
    ///  widest tab still at full length, so the tabs the user could reach without
    ///  scrolling were the stubs.</para>
    /// </summary>
    private sealed class TabsPanel : Panel
    {
        // Enough for the chrome (insets, chip, close button) plus a few characters of
        // name. Below this a tab carries no information and only costs the tabs that
        // still could carry some.
        private const double MinTabWidth = 96;

        private double _budget = double.PositiveInfinity;
        private double[] _widths = [];

        /// <summary>
        ///  The width the whole row has to fit into — the strip's own width, which the
        ///  ScrollViewer in between cannot pass down (it measures with infinity).
        /// </summary>
        internal double Budget
        {
            set
            {
                // Guarded, because this is written from a measure pass: assigning the
                // same number back must not invalidate anything, or the layout would
                // never settle.
                if (value.Equals(_budget))
                {
                    return;
                }

                _budget = value;
                InvalidateMeasure();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _widths = new double[Children.Count];
            double natural = 0;
            double height = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
                _widths[i] = Children[i].DesiredSize.Width;
                natural += _widths[i];
                height = Math.Max(height, Children[i].DesiredSize.Height);
            }

            double budget = Math.Min(_budget, availableSize.Width);
            if (double.IsInfinity(budget) || natural <= budget || Children.Count == 0)
            {
                return new Size(natural, height);
            }

            Squeeze(_widths, budget);

            // Measured a second time at the width each tab has actually been given: that
            // is the only way the label inside hears about the squeeze.
            double total = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Measure(new Size(_widths[i], availableSize.Height));
                total += _widths[i];
                height = Math.Max(height, Children[i].DesiredSize.Height);
            }

            return new Size(total, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                double width = i < _widths.Length ? _widths[i] : Children[i].DesiredSize.Width;
                Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return new Size(Math.Max(x, finalSize.Width), finalSize.Height);
        }

        // Find the one ceiling that makes the row fit — sum of min(natural, cap) == budget
        // — and put every tab under it. Tabs below the cap are untouched, so the whole
        // reduction comes off the widest, which is where the slack is.
        private static void Squeeze(double[] widths, double budget)
        {
            // A tab already narrower than the floor is left at its width rather than grown
            // to it: the floor is a limit on shrinking, not a size.
            double[] floors = new double[widths.Length];
            double floorTotal = 0;
            for (int i = 0; i < widths.Length; i++)
            {
                floors[i] = Math.Min(MinTabWidth, widths[i]);
                floorTotal += floors[i];
            }

            if (floorTotal >= budget)
            {
                // Not even the floors fit. Everyone goes to the floor and the ScrollViewer
                // takes it from here — the alternative is to stop halfway and leave whoever
                // happened not to be pinned yet at full size, which is the bug this is.
                Array.Copy(floors, widths, widths.Length);
                return;
            }

            // The cap is solved for exactly rather than searched for. Sorted ascending, the
            // k widest tabs are the ones the cap bites; the rest keep their natural width
            // and their total is what is left of the budget for those k. Walking k upwards
            // from 1, the answer is the first k whose cap still clears the widest tab NOT
            // in the set — below that the k+1'th is over the cap too and belongs in it.
            double[] sorted = (double[])widths.Clone();
            Array.Sort(sorted);

            double natural = 0;
            foreach (double width in sorted)
            {
                natural += width;
            }

            double cap = budget;
            double keptNatural = natural;
            for (int k = 1; k <= sorted.Length; k++)
            {
                keptNatural -= sorted[^k];
                cap = (budget - keptNatural) / k;
                if (k == sorted.Length || cap >= sorted[^(k + 1)])
                {
                    break;
                }
            }

            // floorTotal < budget guarantees the cap lands above the floor, but the clamp
            // is written out anyway: this runs on every measure pass of every strip and a
            // rounding artefact must not be able to produce a tab of two pixels.
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = Math.Max(floors[i], Math.Min(widths[i], cap));
            }
        }
    }

    /// <summary>
    ///  A one-line label for a path-shaped tab title, which shortens from the MIDDLE.
    ///
    ///  <para><b>Why not <see cref="TextTrimming.CharacterEllipsis"/>.</b> The strip's
    ///  labels are paths — <c>pluma_orchestrator/ai-server/core/api</c> — and an ordinary
    ///  end ellipsis cuts exactly the segment that identifies the tab, leaving
    ///  <c>pluma_orchestrator/ai-server/co…</c>: the part every sibling tab shares is kept
    ///  and the part that tells them apart is thrown away. So the LAST segment is never
    ///  touched, and the prefix is what pays.</para>
    ///
    ///  <para><b>The order of degradation</b>, each step used only when the one before it
    ///  does not fit the width the tab was actually given:</para>
    ///  <list type="number">
    ///   <item><description><c>pluma_orchestrator/ai-server/core/api</c> — all of it;</description></item>
    ///   <item><description><c>pluma_orchestrator/…/core/api</c>, then
    ///     <c>pluma_orchestrator/…/api</c> — the head is kept and the middle collapses one
    ///     segment at a time, nearest the head first, because the segments next to the
    ///     leaf are the ones that place it;</description></item>
    ///   <item><description><c>…/core/api</c>, then <c>…/api</c> — the head goes too;</description></item>
    ///   <item><description><c>plum…</c> — the leaf itself trimmed at its end, and only
    ///     here, where half a name still beats no name.</description></item>
    ///  </list>
    ///
    ///  <para>Segments are dropped whole rather than abbreviated to their initial
    ///  (<c>p…/a…/core/api</c>): an initial reads as a name so the eye tries to expand it,
    ///  and a row of them is noise at exactly the moment the tab is short of room. One
    ///  <c>…</c> says "there is more here" once and is the form editors, shells and file
    ///  choosers have already taught everyone.</para>
    ///
    ///  <para><b>A right-to-left name is trimmed at its own end, not at the left of the
    ///  tab.</b> The paragraph direction stays <see cref="FlowDirection.LeftToRight"/>,
    ///  because a path is not a sentence: it is a sequence of names read from the root
    ///  towards the leaf, and that sequence runs left to right here whatever alphabet the
    ///  names are written in. Handing the label the direction of its text instead would
    ///  push the whole label against the right edge of its tab while its neighbours sat
    ///  against the left, and would reverse <c>مشروع/src</c> into <c>src</c>-first, which
    ///  is a path that does not exist. What DOES have to change is where the <c>…</c>
    ///  goes: it is a neutral character, so at the end of an LTR line it is drawn at the
    ///  visual RIGHT — and for Arabic or Hebrew the visual right is where the name BEGINS.
    ///  Measured on <c>مشروع-التطوير-الكبير-جدا</c> and
    ///  <c>פרויקט-פיתוח-גדול-מאוד</c>: the ellipsis sat against the close button, marking
    ///  the first letter of a name whose missing tail ran off the other edge with nothing
    ///  to mark it. Written BEFORE the kept text instead, it lands at the visual left, at
    ///  the end of the reading direction, which is the side the text actually ran out on —
    ///  and it lands there both in this renderer and in a strict bidi implementation, where
    ///  a leading neutral in an LTR paragraph resolves to the paragraph direction.</para>
    ///
    ///  <para>Every candidate is MEASURED with the same typeface, size, weight and style
    ///  the text is drawn with — <c>pluma_orchestrator</c> and <c>iiiiiiiiiiiiiiiii</c> are
    ///  the same seventeen characters and nothing like the same width — and it is measured
    ///  against <see cref="Visual.Bounds"/>, so the answer follows the tab through a
    ///  window resize, a neighbour opening or closing, and the weight change that comes
    ///  with becoming the active tab. The full path stays on the tooltip: the elision is
    ///  for the glance, never for the information.</para>
    /// </summary>
    private sealed class PathLabel : Control
    {
        private const string Ellipsis = "…";

        private string _text = "";
        private string _suffix = "";
        private double _fontSize = Metrics.Text.Body;
        private FontWeight _fontWeight = FontWeight.Normal;
        private FontStyle _fontStyle = FontStyle.Normal;
        private IBrush? _foreground;

        private string? _shown;
        private double _shownFor = -1;

        internal PathLabel() =>
            // The elided string is computed for a width and drawn at it; a stale frame
            // would spill over its neighbour rather than simply look wrong.
            ClipToBounds = true;

        internal string Text
        {
            get => _text;
            set => Set(ref _text, value ?? "");
        }

        /// <summary>
        ///  A tail that is NEVER elided — the <c>" (2)"</c> that tells two tabs on one
        ///  repository apart. It is reserved out of the width first and every candidate is
        ///  measured with it attached, so the path shortens around it instead of it being
        ///  the first thing trimmed away (which is what happened while it was simply part
        ///  of <see cref="Text"/>; see <see cref="BuildLabels"/>).
        /// </summary>
        internal string Suffix
        {
            get => _suffix;
            set => Set(ref _suffix, value ?? "");
        }

        internal double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value);
        }

        internal FontWeight FontWeight
        {
            get => _fontWeight;
            set => Set(ref _fontWeight, value);
        }

        internal FontStyle FontStyle
        {
            get => _fontStyle;
            set => Set(ref _fontStyle, value);
        }

        internal IBrush? Foreground
        {
            get => _foreground;
            set
            {
                // Colour alone never changes what fits, so it repaints and nothing more.
                if (!ReferenceEquals(_foreground, value))
                {
                    _foreground = value;
                    InvalidateVisual();
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            if (_text.Length == 0 || Bounds.Width <= 0)
            {
                return;
            }

            // One FormattedText over body+suffix rather than two draws side by side: the
            // shaper then sees the whole line, so nothing has to be positioned by hand and
            // the result is byte-for-byte what an unelided label would have drawn.
            FormattedText text = Format(Fit(Bounds.Width) + _suffix);

            // Centred on the row rather than sat on the baseline: the tab's height is the
            // chrome's, not the text's.
            context.DrawText(text, new Point(0, Math.Max(0, (Bounds.Height - text.Height) / 2)));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            FormattedText full = Format(_text + _suffix);

            // Rounded UP, so a label asked to render at exactly its own desired width is
            // never elided by a fraction of a pixel it cannot see.
            double width = Math.Ceiling(full.Width);
            return new Size(Math.Min(width, availableSize.Width), Math.Ceiling(full.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Avalonia does not repaint a control merely because it was arranged smaller,
            // and the whole text of this one is a function of that width.
            if (!finalSize.Width.Equals(_shownFor))
            {
                InvalidateVisual();
            }

            return base.ArrangeOverride(finalSize);
        }

        private void Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            _shown = null;
            _shownFor = -1;
            InvalidateMeasure();
            InvalidateVisual();
        }

        // The longest form that fits, cached for the width it was chosen for: Render runs
        // on every repaint (hover, activation, a neighbour's animation) and the answer
        // only ever changes with the width or the text.
        private string Fit(double width)
        {
            if (_shown is not null && width.Equals(_shownFor))
            {
                return _shown;
            }

            _shown = Choose(width);
            _shownFor = width;
            return _shown;
        }

        private string Choose(double width)
        {
            if (Fits(_text, width))
            {
                return _text;
            }

            string[] parts = _text.Split('/');
            if (parts.Length > 1)
            {
                // Head kept, middle collapsing from the head end down to nothing.
                for (int keep = parts.Length - 2; keep >= 1; keep--)
                {
                    string candidate = parts[0] + "/" + Ellipsis + "/" + string.Join('/', parts[^keep..]);
                    if (Fits(candidate, width))
                    {
                        return candidate;
                    }
                }

                // Head gone as well; the tail keeps shedding its outermost segment.
                for (int keep = parts.Length - 1; keep >= 1; keep--)
                {
                    string candidate = Ellipsis + "/" + string.Join('/', parts[^keep..]);
                    if (Fits(candidate, width))
                    {
                        return candidate;
                    }
                }
            }

            // Last resort: the leaf itself, cut at its end. Half a repository name is the
            // only thing left that still says which repository this is.
            //
            // Cut on GRAPHEME boundaries, never on a raw string index. A directory name is
            // free to hold an emoji, and an emoji is two UTF-16 units: `leaf[..length]`
            // with an odd length hands the renderer half a surrogate pair, which draws as a
            // replacement box or vanishes depending on the font. The same index also falls
            // between a letter and its combining mark (a decomposed "é"), and the orphaned
            // mark then reattaches itself to the ellipsis. Choose() takes the LONGEST
            // candidate that fits and a half-cut candidate is narrower than the whole one,
            // so those cuts are not merely possible, they are what the search prefers.
            // This is the same bug this project already fixed once, in the inline diff.
            string leaf = parts[^1];

            // The ellipsis goes on the side the text RUNS OUT, which for a right-to-left
            // name is the visual left, so on those the "…" is written BEFORE the kept text
            // instead of after it. See the remarks above.
            bool rtl = StartsRtl(leaf);
            IReadOnlyList<int> starts = GraphemeStarts(leaf);
            for (int count = starts.Count - 1; count >= 1; count--)
            {
                string kept = rtl ? TrimTrailingNeutrals(leaf[..starts[count]]) : leaf[..starts[count]];
                if (kept.Length == 0)
                {
                    continue;
                }

                string candidate = rtl ? Ellipsis + kept : kept + Ellipsis;
                if (Fits(candidate, width))
                {
                    return candidate;
                }
            }

            return Ellipsis;
        }

        // Drops the punctuation a cut can leave hanging off the end of a right-to-left
        // name — the "-" of "مشروع-التطوير" cut after "مشروع-".
        //
        // A trailing "-" is a NEUTRAL character, and a neutral at the end of the line takes
        // the paragraph's direction, which is left-to-right: it is drawn at the far RIGHT
        // of the label, on the other side of the name from the letters it was written
        // between, so the tab reads "…مشروع" with a stray dash out by the close button.
        // Cutting one character earlier costs a hyphen that the ellipsis already stands
        // for and removes the artefact entirely. Only for RTL names: on an LTR one the
        // trailing neutral is already in its place.
        private static string TrimTrailingNeutrals(string text)
        {
            int end = text.Length;
            while (end > 0)
            {
                int start = end - 1;
                if (start > 0 && char.IsLowSurrogate(text[start]))
                {
                    start--;
                }

                if (char.IsLetterOrDigit(text, start))
                {
                    break;
                }

                end = start;
            }

            return text[..end];
        }

        // The index at which each user-perceived character of <paramref name="text"/>
        // begins, plus its length as a final entry, so slicing at any of them is safe.
        // TextElementEnumerator, not ParseCombiningCharacters: the former follows the
        // current Unicode text-segmentation rules, so a ZWJ emoji sequence or a flag is one
        // element rather than the two or four codepoints it is built from.
        private static IReadOnlyList<int> GraphemeStarts(string text)
        {
            List<int> starts = [];
            System.Globalization.TextElementEnumerator elements =
                System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                starts.Add(elements.ElementIndex);
            }

            starts.Add(text.Length);
            return starts;
        }

        private bool Fits(string text, double width) =>

            // Measured WITH the suffix, so the room the number needs is taken out of the
            // path's budget rather than borrowed from the tab's neighbour.
            //
            // Half a pixel of slack: the width offered comes from a layout pass that has
            // already rounded, and losing a whole segment to that rounding is visible.
            Format(text + _suffix).Width <= width + 0.5;

        /// <summary>
        ///  The line as it is handed to the shaper. The paragraph is LEFT-TO-RIGHT
        ///  whatever alphabet the path is written in, and that is deliberate — see
        ///  <see cref="Choose"/> for the whole argument and for the one thing that does
        ///  have to change when a segment is right-to-left.
        /// </summary>
        private FormattedText Format(string text) => new(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(this.GetValue(TextBlock.FontFamilyProperty), _fontStyle, _fontWeight),
            _fontSize,
            _foreground ?? Brushes.Transparent);

        // Whether the first STRONG character of the text belongs to a right-to-left script,
        // which is the direction the text will be laid out in. Neutrals (digits, brackets,
        // "-", "…") are skipped, because they take their direction from what surrounds
        // them and cannot decide it.
        //
        // A range test rather than a bidi-class lookup: .NET exposes no public bidi
        // category. The ranges below are the RTL side of Unicode — Hebrew through the
        // Arabic supplements (0590–08FF), the Arabic presentation forms (FB1D–FEFF), and
        // the RTL scripts of plane 1 (Phoenician through Adlam) — and the strong LTR side
        // is approximated by "a letter that is not one of those", which is all this needs
        // to decide.
        private static bool StartsRtl(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length)
                {
                    int cp = char.ConvertToUtf32(c, text[i + 1]);
                    if (cp is >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF)
                    {
                        return true;
                    }

                    if (char.IsLetter(text, i))
                    {
                        return false;
                    }

                    i++;
                    continue;
                }

                if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFF')
                {
                    return true;
                }

                if (char.IsLetter(c))
                {
                    return false;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///  One tab's controls plus the two bits of state that decide how it is painted.
    ///  Kept together so the paint is a single function of (active, hovered) and cannot
    ///  drift between the four call sites that trigger it.
    /// </summary>
    private sealed class TabVisual(
        RepoTabEntry entry,
        Border root,
        Border accent,
        PathLabel label,
        Button close,
        Border checkout)
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

        internal void Apply(
            bool active, string text, string number, IBrush? checkoutColour, string? checkoutPath)
        {
            _active = active;
            label.Text = text;

            // Handed over separately so the elision cannot eat it: see BuildLabels.
            label.Suffix = number;

            // A chip only when the strip holds more than one checkout, and the
            // checkout named on the tooltip only when it is not the tab itself —
            // on a plain repository tab the path is already the whole answer.
            checkout.IsVisible = checkoutColour is not null;
            checkout.Width = checkoutColour is null ? 0 : 3;
            checkout.Background = checkoutColour ?? Brushes.Transparent;
            // The gap belongs to the chip, so a tab without one starts its label flush
            // against the tab's own inset exactly as it did before the chip existed.
            checkout.Margin = checkoutColour is null
                ? default
                : new Thickness(0, 0, Metrics.Space.Xs, 0);
            // Italic IS the preview state — the one visual difference the user has to
            // read at a glance before double-clicking makes it permanent.
            label.FontStyle = entry.Pinned ? FontStyle.Normal : FontStyle.Italic;
            label.FontWeight = active ? Metrics.Text.ActiveWeight : Metrics.Text.BodyWeight;
            // Both paths go through PathDisplay.CollapseHome, because both are shown to a
            // user who is already reading "~/…" everywhere else: the toolbar's repository
            // caption, its recent list and the grid's status line all collapse the home
            // prefix, and the tab tooltip was the one place in the shell that answered the
            // same question with "/home/<user>/…". Two spellings of one path read as two
            // paths — and the absolute form is also the one that is too long to take in,
            // which is the opposite of what a tooltip on a squeezed label is for.
            ToolTip.SetTip(root, checkoutPath is null
                ? PathDisplay.CollapseHome(entry.Path)
                : PathDisplay.CollapseHome(entry.Path) + "\n" + TranslationService.T("in checkout:")
                    + " " + PathDisplay.CollapseHome(checkoutPath));
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
