using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

    private readonly TextBlock _status;
    private readonly Border _avatarHost;
    private readonly StackPanel _details;
    private readonly SelectableTextBlock _message;

    // Last rendered commit, kept so a language switch can re-label the panel
    // without another git round-trip.
    private CommitDetailInfo? _rendered;

    private CancellationTokenSource? _cts;

    /// <summary>
    ///  Raised with a full commit hash when the user clicks a parent or child
    ///  link. The host (MainWindow) may subscribe to navigate the grid; unwired
    ///  is harmless.
    /// </summary>
    public event Action<string>? CommitNavigated;

    public CommitDetailView()
    {
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

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // Re-label in place on a language switch. The event fires on whichever thread
    // completed the catalogue load, so the UI work is marshalled explicitly.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
    {
        if (_rendered is not null)
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
        _status.Text = string.Format(T("Loading commit {0}…"), commitHash);

        _ = Task.Run(() =>
        {
            try
            {
                CommitDetailInfo? detail = _service.LoadCommit(repoPath, commitHash, token);
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

    private void Render(CommitDetailInfo detail)
    {
        _rendered = detail;
        _status.Text = detail.Subject;

        _avatarHost.Child = new AvatarControl(Identicon.Create(
            !string.IsNullOrEmpty(detail.AuthorEmail) ? detail.AuthorEmail : detail.AuthorName))
        {
            Width = AvatarSize,
            Height = AvatarSize,
        };

        _details.Children.Clear();

        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        int row = 0;

        string none = T("UserRepositoriesList/tsmiCategoryNone.Text", "(none)");

        AddRow(grid, ref row, T("TranslatedStrings/_author.Text", "Author"),
            TextValue(detail.Author, monospace: false));
        AddRow(grid, ref row, T("TranslatedStrings/_dateText.Text", "Date"),
            TextValue(DateDisplay(detail.AuthorDate, detail.AuthorDateRelative), monospace: false));

        if (detail.CommitterDiffers)
        {
            AddRow(grid, ref row, T("TranslatedStrings/_committerText.Text", "Committer"),
                TextValue(detail.Committer, monospace: false));
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

        // Contained-in branches.
        _details.Children.Add(SectionLabel(detail.Branches.Count > 0
            ? T("TranslatedStrings/_containedInBranchesText.Text", "Contained in branches:")
            : T("TranslatedStrings/_containedInNoBranchText.Text", "Contained in no branch")));
        if (detail.Branches.Count > 0)
        {
            _details.Children.Add(TagWrap(detail.Branches, B("App.GraphGreen")));
        }

        // Contained-in tags.
        if (detail.Tags.Count > 0)
        {
            _details.Children.Add(SectionLabel(T("TranslatedStrings/_containedInTagsText.Text", "Contained in tags:")));
            _details.Children.Add(TagWrap(detail.Tags, B("App.Accent")));
        }
        else
        {
            _details.Children.Add(SectionLabel(T("TranslatedStrings/_containedInNoTagText.Text", "Contained in no tag")));
        }

        // Derives-from-tag. One format with a placeholder, so a language whose
        // word order differs can move the tag name.
        if (!string.IsNullOrEmpty(detail.DescribeTag))
        {
            _details.Children.Add(SectionLabel(string.Format(
                "{0} {1}",
                T("CommitInfo/_derivesFromTag.Text", "Derives from tag:"),
                detail.DescribeTag)));
        }
        else
        {
            _details.Children.Add(SectionLabel(T("CommitInfo/_derivesFromNoTag.Text", "Derives from no tag")));
        }

        _message.Text = detail.Message;
    }

    private void Clear()
    {
        _rendered = null;
        _avatarHost.Child = null;
        _details.Children.Clear();
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
            Foreground = B("App.Accent"),
            TextDecorations = TextDecorations.Underline,
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.PointerPressed += (_, _) => CommitNavigated?.Invoke(fullHash);
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

    /// <summary>Renders ref names (branches/tags) as small tinted pill labels.</summary>
    private static WrapPanel TagWrap(IReadOnlyList<string> names, IBrush accent)
    {
        WrapPanel panel = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 2),
        };

        foreach (string name in names)
        {
            Border pill = new()
            {
                Background = B("App.Control"),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(0, 2, 6, 2),
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = B("App.Text"),
                    FontSize = 12,
                },
            };
            panel.Children.Add(pill);
        }

        return panel;
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
