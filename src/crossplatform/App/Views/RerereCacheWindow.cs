using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The contents of <c>&lt;git-dir&gt;/rr-cache</c>, made inspectable.
///
///  <para><b>Why a window for a cache.</b> rerere replays a recorded resolution
///  silently and forever; a resolution recorded wrongly once keeps being applied and
///  the damage looks exactly like a clean merge. The only defence is being able to
///  see what is stored — hence a plain list, newest first, with the age of each entry
///  (the one signal that separates "recorded on purpose last week" from "recorded by
///  accident five minutes ago").</para>
///
///  <para><b>A row is a variant, not a directory.</b> One cache directory can hold
///  <c>preimage</c>, <c>preimage.1</c>, <c>preimage.2</c>… when several paths produce
///  the identical conflict shape, and each of those is an independently forgettable
///  resolution. <see cref="RerereService.ListCache"/> already yields variants, so the
///  list shows them one per row and never collapses them by hash.</para>
///
///  <para><b>The hash becomes a name where it can.</b> <c>MERGE_RR</c> maps the
///  conflict id of the merge <i>currently in flight</i> to its path, so during a
///  conflicted merge the rows involved say which file they belong to. Outside a merge
///  that map is empty and the id is all git itself knows — the cache genuinely does
///  not record which path produced an entry.</para>
///
///  <para>Every git and filesystem call goes through <see cref="Task.Run"/>: the
///  service methods block by contract.</para>
/// </summary>
public sealed class RerereCacheWindow : Theming.ZoomWindow
{
    private readonly RerereService _service = new();
    private readonly string _repoPath;

    private readonly TextBlock _location;
    private readonly ListBox _list;
    private readonly Button _refresh;
    private readonly Button _gc;
    private readonly Button _close;
    private readonly TextBlock _status;

    private IReadOnlyDictionary<string, string> _active = new Dictionary<string, string>();
    private bool _busy;

    public RerereCacheWindow(string repoPath)
    {
        _repoPath = repoPath;

        IBrush text = Brush("App.Text", Brushes.Gainsboro);
        IBrush dim = Brush("App.TextDim", Brushes.Gray);

        Title = T("Recorded conflict resolutions (rerere)");
        Width = 780;
        Height = 460;
        MinWidth = 520;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.Black);

        _location = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Metrics.Text.Caption,
            Margin = new Thickness(0, 0, 0, Metrics.Space.Sm),
        };

        _list = new ListBox
        {
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = text,
            BorderBrush = Brush("App.BorderStrong", new SolidColorBrush(Color.Parse("#88898F"))),
            BorderThickness = new Thickness(1),
            ItemTemplate = new FuncDataTemplate<RerereCacheEntry>((entry, _) => Row(entry)),
        };

        _refresh = MakeButton(() => Reload());
        _gc = MakeButton(() => RunGc());

        // "Expire old entries", never "clean up": gc only drops what git already
        // considers stale (gc.rerereResolved 60 days, gc.rerereUnresolved 15), so on a
        // cache written this week it is a no-op — a button promising a cleanup would
        // simply look broken. See fact 7 in RerereService.Gc's remarks.
        _gc.Content = new TextBlock { Text = T("Expire old entries…") };
        ToolTip.SetTip(_gc, T(
            "Runs 'git rerere gc': drops entries git already considers stale — 60 days for "
            + "resolutions that were used, 15 days for conflicts that were never resolved. "
            + "It cannot remove a recent bad resolution; use Forget for that."));

        _close = MakeButton(Close);
        _close.Content = new TextBlock { Text = T("TranslatedStrings/_closeText.Text", "Close") };
        _refresh.Content = new TextBlock { Text = T("FormBrowse/RefreshButton.ToolTipText", "Refresh") };

        _status = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = Metrics.Space.Sm,
            Margin = new Thickness(0, Metrics.Space.Md, 0, 0),
            Children = { _gc, _refresh, _close },
        };

        Grid root = new()
        {
            Margin = new Thickness(Metrics.Space.Md),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
        };
        Grid.SetRow(_location, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(buttons, 3);
        root.Children.Add(_location);
        root.Children.Add(_list);
        root.Children.Add(_status);
        root.Children.Add(buttons);

        Content = root;
        DialogKeys.InstallEscapeClose(this);
        Opened += (_, _) => Reload();
    }

    /// <summary>Shows the cache modally over <paramref name="owner"/>.</summary>
    public static async Task ShowAsync(Window owner, string repoPath)
        => await new RerereCacheWindow(repoPath).ShowDialog(owner);

    // One row: age first (it is what tells a deliberate entry from an accident), then
    // the id, then whatever name we can put on it, then the "will never replay" mark.
    private Control Row(RerereCacheEntry? entry)
    {
        IBrush text = Brush("App.Text", Brushes.Gainsboro);
        IBrush dim = Brush("App.TextDim", Brushes.Gray);

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Md,
        };

        if (entry is null)
        {
            return row;
        }

        row.Children.Add(new TextBlock
        {
            // Local time: the point of the column is "how long ago", which the user
            // reads against their own clock.
            Text = entry.LastWriteTimeUtc == DateTime.MinValue
                ? "—"
                : entry.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            Foreground = dim,
            FontFamily = new FontFamily("monospace"),
            Width = 116,
        });

        row.Children.Add(new TextBlock
        {
            Text = entry.Variant == 0 ? entry.ShortHash : $"{entry.ShortHash}.{entry.Variant}",
            Foreground = text,
            FontFamily = new FontFamily("monospace"),
            Width = 88,
        });

        // The name is only knowable for the merge in flight; saying "—" is honest,
        // inventing one would not be.
        _active.TryGetValue(entry.ConflictId, out string? path);
        row.Children.Add(new TextBlock
        {
            Text = path ?? "—",
            Foreground = path is null ? dim : text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MinWidth = 160,
        });

        row.Children.Add(new TextBlock
        {
            Text = entry.HasPostimage
                ? string.Format(T("{0} bytes"), entry.PostimageBytes)

                // Fact 6: a variant without a postimage is a conflict git saw but that
                // was never resolved to the end. It occupies the cache and will never be
                // replayed, and that has to be visible without opening anything.
                : T("no resolution recorded — will never be replayed"),
            // App.IconAmber, not a literal: it is the theme's warning hue and it is
            // tuned to stay readable as TEXT in both themes (6.92:1 dark, 5.03:1 light).
            Foreground = entry.HasPostimage ? dim : Brush("App.IconAmber", new SolidColorBrush(Color.Parse("#E0A73C"))),
            FontStyle = entry.HasPostimage ? FontStyle.Normal : FontStyle.Italic,
        });

        if (entry.HasThisimage)
        {
            row.Children.Add(new TextBlock
            {
                Text = T("(being resolved now)"),
                Foreground = dim,
                FontStyle = FontStyle.Italic,
            });
        }

        return row;
    }

    private void Reload()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        _status.Text = T("Reading the rerere cache…");

        _ = Task.Run(() =>
        {
            RerereConfiguration configuration = _service.GetConfiguration(_repoPath);
            IReadOnlyList<RerereCacheEntry> entries = _service.ListCache(_repoPath);
            IReadOnlyDictionary<string, string> active = _service.GetActiveConflicts(_repoPath);

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                _active = active;
                _location.Text = configuration.CacheDirectory ?? T("The rerere cache directory is unknown.");

                // A NEW list instance each time: reassigning the same one leaves
                // realised containers showing stale rows.
                _list.ItemsSource = entries.ToList();
                _status.Text = Describe(entries);
                UpdateButtons();
            });
        });
    }

    private string Describe(IReadOnlyList<RerereCacheEntry> entries)
    {
        if (entries.Count == 0)
        {
            return T("Nothing is recorded yet: no conflict resolution has been stored in this repository.");
        }

        int unusable = entries.Count(e => !e.HasPostimage);
        string counted = entries.Count == 1
            ? T("1 recorded resolution.")
            : string.Format(T("{0} recorded resolutions."), entries.Count);

        return unusable == 0
            ? counted
            : counted + " " + string.Format(
                T("{0} of them have no stored resolution and will never be replayed."),
                unusable);
    }

    private void RunGc()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        _status.Text = T("Expiring old entries…");

        int before = (_list.ItemsSource as IReadOnlyList<RerereCacheEntry>)?.Count ?? 0;

        _ = Task.Run(() =>
        {
            RerereActionResult result = _service.Gc(_repoPath);
            IReadOnlyList<RerereCacheEntry> entries = _service.ListCache(_repoPath);
            IReadOnlyDictionary<string, string> active = _service.GetActiveConflicts(_repoPath);

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                _active = active;
                _list.ItemsSource = entries.ToList();
                UpdateButtons();

                if (!result.Success)
                {
                    _status.Text = result.Message;
                    return;
                }

                // git rerere gc says nothing at all when it expires nothing, so the
                // before/after count is the only honest feedback available.
                int removed = before - entries.Count;
                _status.Text = removed > 0
                    ? string.Format(T("Expired {0} entries. "), removed) + Describe(entries)
                    : T("Nothing was old enough to expire (60 days for used resolutions, 15 for "
                        + "conflicts never resolved). ") + Describe(entries);
            });
        });
    }

    private void UpdateButtons()
    {
        _refresh.IsEnabled = !_busy;
        _gc.IsEnabled = !_busy;
    }

    private static Button MakeButton(Action onClick)
    {
        Button button = new() { MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
