using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The CONTENT of one file at one revision, read-only: the port of upstream's
///  "View" tab in <c>FormFileHistory</c> (<c>ViewTab</c>, a <c>FileViewer</c> put
///  in <c>ViewMode.Text</c> and fed the blob of the selected commit rather than a
///  patch). No toolbar, no editing, no patch parsing — the blob is shown as it is,
///  with a line-number gutter beside it.
///
///  <para>The blob is read through <see cref="DiffTextService.GetFileBytesAsync"/>,
///  the same call the file tree, the diff pane and "save as" already use, so this
///  view adds no git invocation of its own. Bytes, not text, because a blob may not
///  be text at all: a NUL in the head of the file means binary, exactly as git
///  decides it, and then a single line says so instead of painting control
///  characters over the panel.</para>
///
///  <para>Decoding follows the diff pane rather than guessing per file: the
///  encoding is <see cref="DiffTextService.Session"/>'s, resolved through
///  <see cref="DiffTextService.ResolveEncoding"/>, which is what
///  <c>DiffViewerOptions</c> and <see cref="DiffView"/> do. A file and its diff
///  shown side by side must not disagree about what its bytes say.</para>
/// </summary>
public sealed class FileContentView : UserControl
{
    // A property, not a field: a static field initialiser can run before the font
    // manager exists, which would cache the fallback for the life of the process.
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

    // The pitch the text and its gutter BOTH lay out at. Stated explicitly for the
    // reason CommitDialog states it: the two blocks compute different default line
    // heights for the same font, and the numbers then drift a whole line away over
    // a screenful.
    private const double LineHeight = 17;

    // Bytes of a blob to sniff for a NUL before calling it binary; git's own
    // heuristic looks at the first 8000 (the same constant FileTreeView carries).
    private const int BinarySniffLength = 8000;

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string T(string english) => TranslationService.T(english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private readonly TextBlock _status;
    private readonly SelectableTextBlock _content;
    private readonly ScrollViewer _contentScroll;
    private readonly TextBlock _gutter;
    private readonly ScrollViewer _gutterScroll;
    private readonly Border _gutterBorder;
    private readonly BusyOverlay _busy = new();

    // Last successful load, kept so a language switch can re-word the status line
    // without going back to git.
    private string? _shownFile;
    private string? _shownCommit;
    private int _shownLines;

    // One source per request, cancelling the one before it. Two quick selections in
    // the history grid must not leave revision A's text under revision B's status
    // line, and the git call of the superseded load is stopped rather than merely
    // ignored. The field itself is the answer to "am I still the load the view is
    // waiting for": it is only ever swapped on the UI thread.
    //
    // Deliberately never disposed, for the reason BlameView spells out: the token
    // can still be observed by the call it just cancelled.
    private CancellationTokenSource? _loadCts;

    public FileContentView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = T("No file loaded."),
        };

        // SelectableTextBlock, not a TextBox: read-only is the point of the tab, and
        // a TextBox would still take the caret, the context menu and the undo stack.
        // Selection is kept because copying a few lines out of the old revision is
        // the whole reason to open it.
        _content = new SelectableTextBlock
        {
            FontFamily = Monospace,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Background = Brush("App.Panel", Brushes.Black),
            Margin = new Thickness(6, 4, 6, 6),
            VerticalAlignment = VerticalAlignment.Top,
            LineHeight = LineHeight,
        };

        _contentScroll = new ScrollViewer
        {
            Content = _content,
            Background = Brush("App.Panel", Brushes.Black),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ClipToBounds = true,
        };

        _gutter = new TextBlock
        {
            FontFamily = Monospace,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Right,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            Margin = new Thickness(6, 4, 6, 6),
            VerticalAlignment = VerticalAlignment.Top,
            LineHeight = LineHeight,
        };

        // Pinned horizontally — it must not scroll away with a wide line — and driven
        // vertically by the text's own offset. Same two-control arrangement the
        // commit dialog's diff gutter uses; nothing new is invented here.
        _gutterScroll = new ScrollViewer
        {
            Content = _gutter,
            Background = Brush("App.Panel", Brushes.Black),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            ClipToBounds = true,
        };
        _contentScroll.ScrollChanged += (_, _) =>
            _gutterScroll.Offset = _gutterScroll.Offset.WithY(_contentScroll.Offset.Y);

        _gutterBorder = new Border
        {
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _gutterScroll,
            IsVisible = false,
        };

        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_gutterBorder, 0);
        Grid.SetColumn(_contentScroll, 1);
        body.Children.Add(_gutterBorder);
        body.Children.Add(_contentScroll);

        // Over the text AND its gutter — the two are one reading surface and the
        // numbers are as stale as the lines they count — but not over the status bar,
        // which is where the name of the file being read is written.
        body.Children.Add(_busy);
        Grid.SetColumnSpan(_busy, 2);

        Border statusBar = new()
        {
            Background = Brush("App.Toolbar", Brushes.DimGray),
            BorderBrush = Brush("App.Border", Brushes.Gray),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _status,
        };

        DockPanel root = new() { Background = Brush("App.Panel", Brushes.Black) };
        DockPanel.SetDock(statusBar, Dock.Top);
        root.Children.Add(statusBar);
        root.Children.Add(body);

        Content = root;

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // The event fires on whichever thread finished loading the catalogue, so the
    // re-wording is marshalled to the UI thread.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(Retranslate);

    private void Retranslate()
        => _status.Text = _shownFile is null ? T("No file loaded.") : StatusLine();

    private string StatusLine()
        => F(T("{0}  —  {1} line(s)  @ {2}"), _shownFile, _shownLines, _shownCommit);

    /// <summary>
    ///  Shows the content of <paramref name="filePath"/> as of
    ///  <paramref name="commitHash"/> in the repository at
    ///  <paramref name="repoPath"/>. Returns at once: the blob is read off the UI
    ///  thread, and a load superseded by a newer call never reaches the screen.
    /// </summary>
    public void ShowFile(string repoPath, string filePath, string commitHash)
    {
        ResetContent();

        // The status line KEEPS its "Loading {0}…": it names the file, which the
        // spinner cannot, and this pane has no other header — with the line replaced
        // by a bare spinner the user could not tell which of two files they
        // double-clicked is on its way. The overlay adds what the sentence never
        // had: the fact that something is still happening after the first second.
        _status.Text = F(T("Loading {0}…"), filePath);
        _busy.Show();

        // Supersede whatever is in flight. ShowFile only ever runs on the UI thread,
        // so swapping the field needs no locking.
        CancellationTokenSource? previous = _loadCts;
        CancellationTokenSource cts = new();
        _loadCts = cts;
        try
        {
            previous?.Cancel();
        }
        catch (Exception)
        {
            // An already-cancelled or faulted source is not a reason to refuse the
            // new request.
        }

        CancellationToken token = cts.Token;

        // Read on the UI thread: the session options are shared state the worker has
        // no business touching, and the encoding must be the one in force when the
        // user asked for this file.
        Encoding encoding = DiffTextService.ResolveEncoding(DiffTextService.Session.EncodingName);

        Async.Run(
            async () =>
            {
                try
                {
                    // Task.Run around it: GetFileBytesAsync starts a process and runs
                    // synchronously up to its first await, which it documents as "must
                    // not be called from the UI thread".
                    byte[] bytes = await Task.Run(
                        () => DiffTextService.GetFileBytesAsync(repoPath, commitHash, filePath, token),
                        token).ConfigureAwait(false);

                    bool binary = IsBinary(bytes);
                    string text = binary ? string.Empty : Decode(bytes, encoding);
                    int lines = binary ? 0 : CountLines(text);
                    string numbers = binary ? string.Empty : BuildGutter(lines);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // Staleness guard: a newer request may have started — and even
                        // finished — between the git call returning and this post being
                        // pumped, so the current source, not the token alone, decides
                        // whether this text may still be painted.
                        if (!ReferenceEquals(_loadCts, cts) || token.IsCancellationRequested)
                        {
                            return;
                        }

                        // Inside the staleness guard, never outside it: when this load
                        // has been superseded the overlay on screen belongs to the
                        // NEWER request, and hiding it here would strand that one
                        // spinnerless for as long as it still runs.
                        _busy.Hide();

                        _content.Text = binary
                            ? F(T("(binary file — {0} byte(s))"), bytes.Length)
                            : text;
                        _gutter.Text = numbers;
                        _gutterBorder.IsVisible = !binary && lines > 0;
                        _contentScroll.ScrollToHome();

                        _shownFile = filePath;
                        _shownCommit = commitHash;
                        _shownLines = lines;
                        _status.Text = StatusLine();
                    });
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer ShowFile: it owns the view now.
                }
                catch (Exception ex)
                {
                    // A missing path at that revision is the ordinary case here (the
                    // file was added later, or renamed), and git's own message says so
                    // better than anything this view could invent — so it goes on the
                    // status line rather than into the log.
                    string message = ex.Message;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(_loadCts, cts) && !token.IsCancellationRequested)
                        {
                            _busy.Hide();
                            ResetContent();
                            _status.Text = F(T("Error: {0}"), message);
                        }
                    });
                }
            },
            "FileContentView.ShowFile");
    }

    /// <summary>
    ///  Empties the view and forgets what it was showing. Any load still in flight
    ///  is cancelled, so it cannot land on the cleared panel afterwards.
    /// </summary>
    public void Clear()
    {
        CancellationTokenSource? previous = _loadCts;
        _loadCts = null;
        try
        {
            previous?.Cancel();
        }
        catch (Exception)
        {
            // See ShowFile: cancelling a spent source is not an error worth raising.
        }

        // The cancelled load ends in its OperationCanceledException arm, which
        // deliberately touches nothing — so the only place left to take the veil down
        // is here, and without it an emptied pane would spin forever.
        _busy.Hide();

        ResetContent();
        _shownFile = null;
        _shownCommit = null;
        _shownLines = 0;
        _status.Text = T("No file loaded.");
    }

    private void ResetContent()
    {
        _content.Text = string.Empty;
        _gutter.Text = string.Empty;

        // The gutter goes away entirely rather than standing as an empty stripe, the
        // same rule the commit dialog's gutter follows.
        _gutterBorder.IsVisible = false;
    }

    // A NUL byte in the head of the blob means binary, as git decides it too.
    private static bool IsBinary(byte[] bytes)
    {
        int end = Math.Min(bytes.Length, BinarySniffLength);
        for (int i = 0; i < end; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    // The chosen encoding is applied as chosen — this view does not sniff, because
    // the diff pane does not either and the two must agree. Two things are still
    // normalised: a byte-order mark is dropped (it is metadata, and left in place it
    // renders as a stray glyph in front of the first line), and the line endings are
    // brought to '\n' so that counting them gives exactly the number of lines the
    // text block will lay out — which is what keeps the gutter in step.
    private static string Decode(byte[] bytes, Encoding encoding)
    {
        string text = encoding.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    // Lines as the text block will lay them out. A trailing newline closes the last
    // line rather than opening an extra one, so it must not be counted — otherwise
    // the gutter is one number longer than the file.
    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return text[^1] == '\n' ? lines - 1 : lines;
    }

    // One right-aligned number per line, each closed by '\n' exactly as the text's
    // own lines are, so both blocks lay out the same number of lines and stay in
    // step under the shared LineHeight.
    private static string BuildGutter(int lines)
    {
        int width = lines.ToString(CultureInfo.InvariantCulture).Length;
        StringBuilder builder = new(lines * (width + 1));
        for (int i = 1; i <= lines; i++)
        {
            builder.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append('\n');
        }

        return builder.ToString();
    }
}
