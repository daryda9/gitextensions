using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The "Dashboard" landing view for the Avalonia / Linux port, echoing the
///  original GitExtensions start page. It is shown when no repository is open
///  (or when the user chooses "Close (go to Dashboard)") and lists the
///  favorite and recent repositories as clickable entries.
///
///  <para>The view performs no persistence or git work itself: it renders the
///  two lists it is handed via <see cref="Load"/> and raises
///  <see cref="RepositorySelected"/> (and <see cref="OpenOtherRequested"/>) for
///  the host window to act on — the same separation the toolbar and menu use.
///  Colors come from the shared <c>App.*</c> palette.</para>
/// </summary>
public sealed class DashboardView : UserControl
{
    private readonly StackPanel _favorites;
    private readonly StackPanel _recent;
    private readonly IBrush _text;
    private readonly IBrush _dim;
    private readonly IBrush _accent;

    /// <summary>Raised with a repository path when the user clicks an entry.</summary>
    public event Action<string>? RepositorySelected;

    /// <summary>Raised when the user clicks the "Open repository…" button.</summary>
    public event Action? OpenOtherRequested;

    public DashboardView()
    {
        _text = Resource("App.Text", "#DCDCDC");
        _dim = Resource("App.TextDim", "#9B9B9B");
        _accent = Resource("App.Accent", "#007ACC");

        Background = Resource("App.Window", "#1E1E1E");

        TextBlock title = new()
        {
            Text = "Git Extensions",
            Foreground = _text,
            FontSize = 26,
            FontWeight = FontWeight.SemiBold,
        };
        TextBlock subtitle = new()
        {
            Text = "Open a repository to get started.",
            Foreground = _dim,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 0),
        };

        Button open = new()
        {
            Content = "Open repository…",
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0),
        };
        open.Click += (_, _) => OpenOtherRequested?.Invoke();

        _favorites = new StackPanel { Spacing = 2 };
        _recent = new StackPanel { Spacing = 2 };

        StackPanel content = new() { Margin = new Thickness(28), Spacing = 4 };
        content.Children.Add(title);
        content.Children.Add(subtitle);
        content.Children.Add(open);
        content.Children.Add(SectionHeader("Favorite repositories"));
        content.Children.Add(_favorites);
        content.Children.Add(SectionHeader("Recent repositories"));
        content.Children.Add(_recent);

        Content = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        Load(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    ///  Repopulates the dashboard from the given favorite and recent lists
    ///  (most-relevant first). Empty lists show a dim "(none)" placeholder.
    /// </summary>
    public void Load(IReadOnlyList<string> favorites, IReadOnlyList<string> recent)
    {
        Fill(_favorites, favorites);
        Fill(_recent, recent);
    }

    private void Fill(StackPanel panel, IReadOnlyList<string> repos)
    {
        panel.Children.Clear();

        if (repos is null || repos.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "(none)",
                Foreground = _dim,
                FontSize = 12,
                Margin = new Thickness(4, 2, 0, 2),
            });
            return;
        }

        foreach (string repo in repos)
        {
            string path = repo;
            Button entry = new()
            {
                Content = path,
                Foreground = _accent,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 3, 4, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            entry.Click += (_, _) => RepositorySelected?.Invoke(path);
            panel.Children.Add(entry);
        }
    }

    private Control SectionHeader(string text) => new TextBlock
    {
        Text = text,
        Foreground = _text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 20, 0, 6),
    };

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
