using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The command palette: one box, one list, every command the window can perform.
///
///  <para><b>Port-specific.</b> Upstream Git Extensions has no such surface. It exists
///  here because the menu has grown past a hundred entries spread over ten top-level
///  menus, and because half the keyboard commands (the focus moves, the quick pull/push
///  variants) appear in no menu at all and were therefore reachable only by someone who
///  had read <see cref="HotkeyService.Defaults"/>.</para>
///
///  <para><b>It owns no command list.</b> The rows are handed in by the host, which
///  builds them from <see cref="MainMenu.EnumerateCommands"/> — the live menu tree,
///  walked when this window opens — plus <see cref="HotkeyService.BoundCommands"/>. So
///  a command added to the menu is in the palette the same day, with the same caption,
///  the same icon, the same shortcut and the same gating; see
///  <see cref="MainMenu.EnumerateCommands"/> for why that matters more than it sounds.</para>
///
///  <para><b>Escape cannot invoke anything.</b> The chosen entry is published through
///  <see cref="Chosen"/> and is only ever written by <see cref="Accept"/> — closing by
///  any other route (Escape, the window button, the owner going away) leaves it null.
///  This port has been bitten before by a dialog that applied its highlighted row on
///  Escape because it read the selection back after <c>ShowDialog</c>, so the
///  highlighted row is deliberately NOT the answer here; only an explicit accept is.</para>
///
///  <para><b>Disabled commands are shown, greyed, and cannot be run.</b> Filtering them
///  out was the alternative and it is worse: "why is there no Commit here" has no answer
///  the user can see, whereas a greyed row with "unavailable" next to it says the
///  command exists and something about the repository is why it is not offered — the
///  same message the menu gives by greying rather than hiding.</para>
/// </summary>
public sealed class CommandPaletteWindow : Theming.ZoomWindow
{
    // Twelve rows: enough that the whole of a top-level menu is visible at once, few
    // enough that the window still reads as an overlay and not as a second main window.
    private const int VisibleRows = 12;
    private const double RowHeight = 26;

    private readonly CommandPaletteService _service;
    private readonly IReadOnlyList<PaletteEntry> _entries;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _status;

    private readonly IBrush _text;
    private readonly IBrush _dim;
    private readonly IBrush _accent;

    /// <param name="entries">Every command to offer, in the order they should appear
    /// when nothing has been typed and nothing has been used yet.</param>
    /// <param name="service">The matcher and MRU store; a fresh one by default.</param>
    public CommandPaletteWindow(IReadOnlyList<PaletteEntry> entries, CommandPaletteService? service = null)
    {
        _entries = entries;
        _service = service ?? new CommandPaletteService();

        _text = B("App.Text", Brushes.Gainsboro);
        _dim = B("App.TextDim", Brushes.Gray);
        _accent = B("App.Accent", Brushes.DodgerBlue);

        Title = T("Command palette");
        Width = 640;
        Height = (VisibleRows * RowHeight) + 110;
        MinWidth = 420;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B("App.Window", Brushes.Black);

        _search = new TextBox
        {
            Watermark = T("Type a command…"),
            Background = B("App.Panel", Brushes.Black),
            Foreground = _text,
            Padding = Metrics.Density.InputPadding,
            CornerRadius = Metrics.Radius.SmCorner,
            [DockPanel.DockProperty] = Dock.Top,
            Margin = new Thickness(0, 0, 0, Metrics.Space.Sm),
        };

        _status = new TextBlock
        {
            Foreground = _dim,
            FontSize = Metrics.Text.Caption,
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
            [DockPanel.DockProperty] = Dock.Bottom,
        };

        _list = new ListBox
        {
            Background = B("App.Panel", Brushes.Black),
            Foreground = _text,
            BorderBrush = B("App.BorderStrong", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.Radius.SmCorner,
            ItemTemplate = new FuncDataTemplate<PaletteMatch?>((match, _) => Row(match)),
        };

        Content = new DockPanel
        {
            Margin = Metrics.Space.All(Metrics.Space.Md),
            Children = { _search, _status, _list },
        };

        _search.TextChanged += (_, _) => Refill();

        // Tunnelling and handledEventsToo, for the reason the rest of the port keeps
        // rediscovering: the TextBox owns the focus (it must — the palette is a typing
        // surface), so Enter and the arrows never reach a bubbling handler on the
        // window in a usable state. Only the four keys the palette itself defines are
        // taken; everything else, the text editing included, falls through untouched.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // A click IS the choice: this list has no second gesture to reserve for opening
        // (there is no preview, no context menu), so requiring a double click would only
        // make the mouse path slower than the keyboard one for no gain.
        _list.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble);

        Opened += (_, _) =>
        {
            Refill();
            _search.Focus();
        };
    }

    /// <summary>
    ///  The command the user accepted, or <see langword="null"/> when the palette was
    ///  dismissed. Written by <see cref="Accept"/> only — see the class remarks.
    ///
    ///  <para>The caller must run <see cref="PaletteEntry.Invoke"/> AFTER this window is
    ///  gone (the window is closed before the property is read, and the host defers the
    ///  call by one dispatcher turn): most commands open a modal dialog owned by the
    ///  main window, and running one while the palette is still up would either parent
    ///  it wrongly or leave the palette floating over it.</para>
    /// </summary>
    public PaletteEntry? Chosen { get; private set; }

    private void Refill()
    {
        IReadOnlyList<PaletteMatch> rows = _service.Filter(_entries, _search.Text);
        _list.ItemsSource = rows;
        _list.SelectedIndex = rows.Count > 0 ? 0 : -1;

        _status.Text = rows.Count switch
        {
            0 => T("No command matches."),

            // The hint is worth a line: the two things that are not guessable about this
            // window are that a disabled row stays visible and that Escape runs nothing.
            _ => T("Enter runs the selected command · Escape closes · greyed commands are unavailable here"),
        };
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                // No Chosen assignment, by construction: this is the whole point.
                Close();
                e.Handled = true;
                break;

            case Key.Enter or Key.Return:
                Accept(_list.SelectedItem as PaletteMatch);
                e.Handled = true;
                break;

            case Key.Down:
                Move(1);
                e.Handled = true;
                break;

            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;

            case Key.PageDown:
                Move(VisibleRows);
                e.Handled = true;
                break;

            case Key.PageUp:
                Move(-VisibleRows);
                e.Handled = true;
                break;
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only a click that landed ON a row counts; the padding around the rows and the
        // scrollbar are not an accept gesture.
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is not null)
        {
            Accept(_list.SelectedItem as PaletteMatch);
        }
    }

    private void Move(int delta)
    {
        if (_list.ItemCount == 0)
        {
            return;
        }

        // Wrapping, because the list is a ring the user scans rather than a range they
        // navigate: Up from the first row means "the last one", not "nothing happens".
        int index = _list.SelectedIndex + delta;
        index = ((index % _list.ItemCount) + _list.ItemCount) % _list.ItemCount;
        _list.SelectedIndex = index;
        _list.ScrollIntoView(index);
    }

    private void Accept(PaletteMatch? match)
    {
        // A disabled command is not "accepted and then ignored": the window stays open
        // so the user can pick another row, exactly as clicking a greyed menu entry
        // leaves the menu open.
        if (match is not { Entry.IsEnabled: true })
        {
            return;
        }

        Chosen = match.Entry;
        _service.Remember(match.Entry.Id);
        Close();
    }

    private Control Row(PaletteMatch? match)
    {
        // The panel recycles containers, and it clears one by setting its content to
        // null BEFORE dropping it — so the template is genuinely called with no item
        // while the list is being refilled, on every keystroke. RevisionGridView's row
        // template takes the same nullable parameter for the same reason.
        if (match is null)
        {
            return new Grid { Height = RowHeight };
        }

        PaletteEntry entry = match.Entry;

        Grid row = new()
        {
            Height = RowHeight,
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        // A fixed-width cell whether or not there is an icon, so the captions line up
        // down the list instead of stepping in and out with the icon coverage.
        Border icon = new()
        {
            Width = Metrics.Density.IconSize,
            Height = Metrics.Density.IconSize,
            Margin = new Thickness(0, 0, Metrics.Space.Sm, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = entry.IconName is { Length: > 0 } name ? IconLoader.Image(name) : null,
            Opacity = entry.IsEnabled ? 1d : 0.4d,
        };

        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        TextBlock caption = new()
        {
            FontSize = Metrics.Text.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Fill(caption, match);
        Grid.SetColumn(caption, 1);
        row.Children.Add(caption);

        TextBlock trailing = new()
        {
            Text = entry.IsEnabled ? entry.Gesture ?? string.Empty : T("unavailable"),
            Foreground = _dim,
            FontSize = Metrics.Text.Caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Metrics.Space.Md, 0, 0, 0),
        };
        Grid.SetColumn(trailing, 2);
        row.Children.Add(trailing);

        return row;
    }

    // The caption as runs: the parent chain dimmed, the leaf in the normal text colour,
    // and the characters the query landed on in the accent colour and the "active"
    // weight. Runs are cut where EITHER property changes, so a hit inside the path keeps
    // both its highlight and its demotion.
    private void Fill(TextBlock block, PaletteMatch match)
    {
        PaletteEntry entry = match.Entry;
        InlineCollection inlines = block.Inlines ??= [];
        HashSet<int> hits = [.. match.Hits];

        string display = entry.Display;
        int i = 0;
        while (i < display.Length)
        {
            bool hit = hits.Contains(i);
            bool inPath = i < entry.LabelStart;

            int start = i;
            while (i < display.Length && hits.Contains(i) == hit && (i < entry.LabelStart) == inPath)
            {
                i++;
            }

            inlines.Add(new Run(display[start..i])
            {
                Foreground = !entry.IsEnabled ? _dim
                    : hit ? _accent
                    : inPath ? _dim
                    : _text,
                FontWeight = hit ? Metrics.Text.ActiveWeight : Metrics.Text.BodyWeight,
            });
        }
    }

    private static string T(string english) => TranslationService.T(english);

    private static IBrush B(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush brush
            ? brush
            : fallback;
}
