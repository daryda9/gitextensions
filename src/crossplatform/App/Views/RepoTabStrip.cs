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
///  One open repository in the strip: its path, whether the user has committed to
///  keeping it, and the slots <see cref="MainWindow"/> uses to remember where the
///  user was inside it.
/// </summary>
/// <remarks>
///  <para><see cref="Path"/> is <c>init</c>-only on purpose: the strip identifies a
///  tab BY its path, so a tab that could be repointed at another repository would
///  quietly invalidate <see cref="SelectedCommit"/> and <see cref="BottomTab"/>
///  without anyone noticing. Re-using the preview slot for a different repository
///  therefore replaces the entry rather than mutating it, which drops the stale
///  per-tab state as a side effect of the only operation that should drop it.</para>
/// </remarks>
public sealed class RepoTabEntry
{
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
    ///  Raised after any add / close / pin / activate, for persistence. Always AFTER
    ///  <see cref="Activated"/> / <see cref="Emptied"/>, so what the host writes out is
    ///  the settled state and not the one halfway through the operation.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    ///  Opens a repository, or re-activates it when it is already open.
    ///
    ///  <para>Already open: activated, and additionally pinned when <paramref name="pinned"/>
    ///  says so. Not open and pinned: a new tab right after the active one — next to what
    ///  the user was looking at, which is where they were looking. Not open and unpinned:
    ///  the preview slot, re-used if one exists (same position, new entry, per-tab state
    ///  gone with the old one) and appended otherwise.</para>
    /// </summary>
    /// <returns>The entry now standing for <paramref name="path"/>.</returns>
    public RepoTabEntry Open(string path, bool pinned)
    {
        RepoTabEntry? existing = Find(path);
        if (existing is not null)
        {
            // Re-opening deliberately is one of the ways to claim a preview tab.
            if (pinned && !existing.Pinned)
            {
                existing.Pinned = true;
                Refresh(existing);
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

    /// <summary>Activates the tab for <paramref name="path"/>; a no-op when it is not open.</summary>
    public void Activate(string path)
    {
        if (Find(path) is not { } entry)
        {
            return;
        }

        SetActive(entry);
        Changed?.Invoke();
    }

    /// <summary>
    ///  Closes the tab for <paramref name="path"/>. Closing the active one hands over to
    ///  the right-hand neighbour, else the left-hand one, else raises <see cref="Emptied"/> —
    ///  the right first, because that is the tab that visually takes the closed one's place.
    /// </summary>
    public void Close(string path)
    {
        if (Find(path) is not { } entry)
        {
            return;
        }

        int index = _tabs.IndexOf(entry);
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

    /// <summary>Claims the tab for <paramref name="path"/>, so no preview open can take its slot.</summary>
    public void Pin(string path)
    {
        if (Find(path) is not { Pinned: false } entry)
        {
            return;
        }

        entry.Pinned = true;
        Refresh(entry);
        Changed?.Invoke();
    }

    /// <summary>
    ///  Replaces the whole set, for restoring a persisted session. Raises
    ///  <see cref="Changed"/> but NOT <see cref="Activated"/>: the host is the one
    ///  restoring, so it already knows which repository it is about to load, and an
    ///  event here would make it load it twice.
    /// </summary>
    public void Restore(IEnumerable<RepoTabEntry> tabs, string? activePath)
    {
        foreach (RepoTabEntry old in _tabs)
        {
            Drop(old);
        }

        _tabs.Clear();
        _active = null;
        _tabs.AddRange(tabs);
        _active = activePath is null ? null : Find(activePath);
        Sync();
        Changed?.Invoke();
    }

    // ---- model -------------------------------------------------------------

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

    // ---- paint -------------------------------------------------------------

    // Rebuilds the row of children from the model, re-using each tab's visual so the
    // pointer-over state (and the pointer capture of a click in flight) survives a
    // neighbour appearing or disappearing.
    private void Sync()
    {
        _strip.Children.Clear();
        foreach (RepoTabEntry entry in _tabs)
        {
            if (!_visuals.TryGetValue(entry, out TabVisual? visual))
            {
                visual = Build(entry);
                _visuals[entry] = visual;
            }

            visual.Apply(ReferenceEquals(entry, _active));
            _strip.Children.Add(visual.Root);
        }

        IsVisible = _tabs.Count > 0;
    }

    private void Refresh(RepoTabEntry entry)
    {
        if (_visuals.TryGetValue(entry, out TabVisual? visual))
        {
            visual.Apply(ReferenceEquals(entry, _active));
        }
    }

    private TabVisual Build(RepoTabEntry entry)
    {
        TextBlock label = new()
        {
            Text = Label(entry.Path),
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
            Close(entry.Path);
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
                Close(entry.Path);
            }
            else if (props.IsLeftButtonPressed)
            {
                Activate(entry.Path);
            }
        };

        // Double click claims the tab, exactly as it claims a preview tab in an editor:
        // the second click of "I keep coming back to this one".
        root.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            Pin(entry.Path);
        };

        root.ContextMenu = BuildMenu(entry);
        return visual;
    }

    private ContextMenu BuildMenu(RepoTabEntry entry)
    {
        MenuItem keep = new();
        keep.Click += (_, _) => Pin(entry.Path);

        MenuItem close = new();
        close.Click += (_, _) => Close(entry.Path);

        MenuItem others = new();
        others.Click += (_, _) => CloseOthers(entry);

        MenuItem all = new();
        all.Click += (_, _) => CloseAll();

        ContextMenu menu = new()
        {
            ItemsSource = new Control[] { keep, new Separator(), close, others, all },
        };

        // Headers are written when the popup opens rather than once at build time: it
        // costs nothing, and it makes the menu follow both the tab's own state ("Keep
        // open" is meaningless on a tab that is already kept) and a language switch,
        // without a subscription to unhook when the tab is closed.
        menu.Opening += (_, _) =>
        {
            keep.Header = MenuText.Escape(T("Keep open"));
            keep.IsVisible = !entry.Pinned;
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
    private static string Label(string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        string name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : (path.Length > 0 ? path : "?");
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

        internal Border Root => root;

        internal void SetHover(bool hovered)
        {
            _hovered = hovered;
            Paint();
        }

        internal void Apply(bool active)
        {
            _active = active;
            label.Text = Label(entry.Path);
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
        }
    }
}
