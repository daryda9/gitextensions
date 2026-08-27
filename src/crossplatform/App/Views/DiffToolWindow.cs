using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The <b>built-in</b> side-by-side difftool: two versions of one file, aligned
///  line by line, so comparing does not require kdiff3, meld or any other
///  external program either.
///
///  <para><b>Original work</b>, like <see cref="MergeToolWindow"/>: upstream only
///  shells out to <c>git difftool</c>, and that route is still offered beside this
///  one. This is the same window one column narrower — the merge editor without
///  the base and without a result to write — and it deliberately shares its
///  shape, so the two feel like one tool seen twice.</para>
///
///  <para><b>Line numbers come from the alignment, not from the document.</b> The
///  panes are padded with filler rows to stay level with each other, so the
///  editor's own line numbers would count the padding and disagree with the file.
///  <see cref="AlignedLineNumberMargin"/> renders the real numbers instead, and
///  leaves a filler row blank — a row that exists on one side only has no number
///  on the other, and inventing one would be a lie about where the line lives.</para>
/// </summary>
public sealed class DiffToolWindow : ZoomWindow
{
    private readonly DiffDocument _doc;

    private readonly TextEditor _left;
    private readonly TextEditor _right;
    private readonly TextBlock _counter;

    // The word diff of the changed rows, computed on demand for the rows that
    // scroll into view and kept for as long as the window lives — the document is
    // built once and never edited, so nothing can invalidate it.
    private readonly Dictionary<int, InlineDiffResult> _inlineSpans = [];

    private int _current = -1;
    private bool _syncing;

    private DiffToolWindow(DiffDocument document)
    {
        _doc = document;

        Title = T("Compare") + " — " + document.Path;
        Width = 1180;
        Height = 760;
        MinWidth = 640;
        MinHeight = 400;
        Background = Brush("App.Window", Brushes.Black);

        Styles.Add(new StyleInclude(new Uri(Theming.AssetUri.Base))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });

        _left = Pane(document, side: true);
        _right = Pane(document, side: false);

        // Two panes showing the same rows have to move together, or the alignment
        // they were padded for is lost the moment either is scrolled.
        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        _counter = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            FontWeight = Metrics.Text.ActiveWeight,
        };

        Content = BuildLayout();
        UpdateCounter();

        // Open on the first difference for the same reason the merge editor does:
        // line 1 of a long file with one change in the middle looks like no change.
        Dispatcher.UIThread.Post(() => GoTo(0), DispatcherPriority.Loaded);
    }

    /// <summary>
    ///  Opens the comparison. Reading the two versions and running git happen off
    ///  the UI thread; a failure reports why instead of opening an empty window.
    /// </summary>
    public static async Task<string?> ShowAsync(
        Window owner,
        string repoPath,
        string path,
        string leftLabel,
        string rightLabel,
        string left,
        string right,
        bool histogram)
    {
        DiffToolService service = new();
        (DiffDocument? document, string? error) = await Task.Run(
            () => service.PrepareAsync(repoPath, path, leftLabel, rightLabel, left, right, histogram));
        if (document is null)
        {
            return error ?? "The comparison could not be prepared.";
        }

        DiffToolWindow window = new(document);
        await window.ShowDialog(owner);
        return null;
    }

    private Control BuildLayout()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };

        Grid bar = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs),
        };
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            Children =
            {
                ToolButton("▲", T("Previous difference"), () => GoTo(_current - 1)),
                ToolButton("▼", T("Next difference"), () => GoTo(_current + 1)),
                InlineDiffToggle(),
            },
        };
        bar.Children.Add(actions);

        // A read-only window still needs a way out, and it is the only button that
        // is not optional: without it the window can only be dismissed by the
        // window manager, and a session without one (or a user who never looks at
        // the title bar) is stuck. IsCancel also gives it Escape.
        Button close = new()
        {
            Content = T("Close"),
            Padding = Metrics.Density.ButtonPadding,
            IsCancel = true,
            Margin = new Thickness(Metrics.Space.Sm, 0, 0, 0),
        };
        close.Click += (_, _) => Close();

        StackPanel right = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            Children = { _counter, close },
        };
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);

        Border toolbar = new()
        {
            Background = Brush("App.Toolbar", Brushes.DimGray),
            BorderBrush = Rule(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        Grid panes = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,*"),
        };
        AddColumn(panes, Titled(_doc.LeftLabel, "App.DiffRemoved", _left), 0);
        AddColumn(panes, Titled(_doc.RightLabel, "App.DiffAdded", _right), 2);

        GridSplitter splitter = new()
        {
            Width = 4,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, 1);
        PaneSplitter.Add(panes, splitter);

        Grid.SetRow(panes, 1);
        root.Children.Add(panes);
        return root;
    }

    private TextEditor Pane(DiffDocument document, bool side)
    {
        TextEditor editor = new()
        {
            FontFamily = AppFonts.Monospace,
            FontSize = AppFonts.MonospaceSize > 0 ? AppFonts.MonospaceSize : 13,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Background = Brush("App.Panel", Brushes.Black),
            Padding = new Thickness(4, 6),
            IsReadOnly = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join('\n', document.Rows.Select(r => (side ? r.Left : r.Right) ?? string.Empty)),
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.AllowScrollBelowDocument = false;
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffRowHighlighter(document.Rows, side));

        // After the row wash, so the word marks paint ON it rather than under it:
        // renderers of one layer are drawn in the order they were added, and the
        // point of the pair is that the row still reads as changed while the marks
        // say where. The cache is shared by the two panes, because a changed row's
        // word diff is one comparison whose two halves the two panes each use once.
        editor.TextArea.TextView.BackgroundRenderers.Add(
            new InlineDiffRowHighlighter(document.Rows, side, _inlineSpans));
        editor.TextArea.LeftMargins.Insert(0, new AlignedLineNumberMargin(document.Rows, side));
        return editor;
    }

    private Control Titled(string caption, string accentKey, TextEditor editor)
    {
        Grid pane = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        Border header = new()
        {
            Background = Brush("App.PanelAlt", Brushes.DimGray),
            BorderBrush = Rule(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs),
            Child = new TextBlock
            {
                Text = caption,
                Foreground = Brush(accentKey, Brushes.Gray),
                FontWeight = Metrics.Text.ActiveWeight,
                FontSize = Metrics.Text.Caption,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        Grid.SetRow(header, 0);
        Grid.SetRow(editor, 1);
        pane.Children.Add(header);
        pane.Children.Add(editor);
        return pane;
    }

    private void Sync(TextEditor from, TextEditor to)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (Math.Abs(to.VerticalOffset - from.VerticalOffset) > 0.5)
            {
                to.ScrollToVerticalOffset(from.VerticalOffset);
            }

            if (Math.Abs(to.HorizontalOffset - from.HorizontalOffset) > 0.5)
            {
                to.ScrollToHorizontalOffset(from.HorizontalOffset);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void GoTo(int index)
    {
        if (_doc.Hunks.Count == 0)
        {
            UpdateCounter();
            return;
        }

        _current = Math.Clamp(index, 0, _doc.Hunks.Count - 1);
        int line = _doc.Hunks[_current] + 1;
        _left.ScrollToLine(line);
        _right.ScrollToLine(line);
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        _counter.Text = _doc.Hunks.Count == 0
            ? T("The two versions are identical")
            : string.Format(T("Difference {0} of {1}"), Math.Max(_current, 0) + 1, _doc.Hunks.Count);
        _counter.Foreground = _doc.Hunks.Count == 0
            ? Brush("App.TextDim", Brushes.Gray)
            : Brush("App.Text", Brushes.Gainsboro);
    }

    // The switch for the intra-line marks. It lives on this toolbar and on the patch
    // pane's strip, over one process-wide flag, so the two surfaces never disagree
    // about it — and flipping it here writes view-prefs.json just as flipping it there
    // does: the user turned the marks off in "a" diff window, not for this window.
    private ToggleButton InlineDiffToggle()
    {
        ToggleButton button = new()
        {
            Content = "a|b",
            Padding = Metrics.Density.ButtonPadding,
            IsChecked = InlineDiffOptions.Enabled,
            Margin = new Thickness(Metrics.Space.Sm, 0, 0, 0),
            [ToolTip.TipProperty] = T("Highlight the changed words inside a changed line"),
        };
        button.IsCheckedChanged += (_, _) =>
        {
            InlineDiffOptions.Enabled = button.IsChecked == true;
            DiffViewerOptions.Persist();
            _left.TextArea.TextView.InvalidateVisual();
            _right.TextArea.TextView.InvalidateVisual();
        };
        return button;
    }

    private Button ToolButton(string caption, string tip, Action action)
    {
        Button button = new()
        {
            Content = caption,
            Padding = Metrics.Density.ButtonPadding,
            [ToolTip.TipProperty] = tip,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static void AddColumn(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static IBrush Rule()
        => Icons.Tint("App.Rule") ?? Brush("App.Border", Brushes.Gray);

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);
}

/// <summary>
///  Paints one side of the alignment: removed rows, added rows, replaced rows and
///  the filler that stands opposite a row the other side does not have.
///
///  <para>Filler gets its own, flatter wash rather than the colour of the change
///  it faces: it is <b>absence</b>, not content, and painting it like a deletion
///  would make an added line look as though something had also been removed.</para>
/// </summary>
internal sealed class DiffRowHighlighter(IReadOnlyList<DiffRow> rows, bool left) : IBackgroundRenderer
{
    private static readonly IBrush Removed = new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0x6C, 0x6C));
    private static readonly IBrush Added = new SolidColorBrush(Color.FromArgb(0x30, 0x6A, 0xC7, 0x76));
    private static readonly IBrush Changed = new SolidColorBrush(Color.FromArgb(0x2E, 0xE0, 0xA7, 0x3C));
    private static readonly IBrush Filler = new SolidColorBrush(Color.FromArgb(0x1E, 0x80, 0x80, 0x80));

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.VisualLines.Count == 0)
        {
            return;
        }

        textView.EnsureVisualLines();
        foreach (VisualLine visual in textView.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;
            if (index < 0 || index >= rows.Count)
            {
                continue;
            }

            DiffRow row = rows[index];
            IBrush? brush = row.Kind switch
            {
                DiffRowKind.Changed => Changed,
                DiffRowKind.Removed => left ? Removed : Filler,
                DiffRowKind.Added => left ? Filler : Added,
                _ => null,
            };

            if (brush is null)
            {
                continue;
            }

            drawingContext.FillRectangle(
                brush,
                new Rect(0, visual.VisualTop - textView.VerticalOffset,
                    Math.Max(textView.Bounds.Width, 0), visual.Height));
        }
    }
}

/// <summary>
///  Marks the changed words inside a <see cref="DiffRowKind.Changed"/> row: the
///  removed stretches on the left pane, the added ones on the right.
///
///  <para>The alignment has already done the pairing this needs — a changed row IS
///  a pair of lines — so there is no rule to invent here, unlike the unified pane.
///  A filler row has no counterpart at all and is deliberately left alone: there is
///  nothing on the other side to have changed <i>from</i>.</para>
///
///  <para>An <see cref="IBackgroundRenderer"/>, like the row wash it sits on, and
///  for the extra reason that a <c>DocumentColorizingTransformer</c> would skip the
///  empty lines the filler is made of. Only the rows on screen are compared, and
///  each comparison is cached — <paramref name="spans"/> is shared with the other
///  pane, which needs the other half of the same answer.</para>
/// </summary>
internal sealed class InlineDiffRowHighlighter(
    IReadOnlyList<DiffRow> rows, bool left, Dictionary<int, InlineDiffResult> spans) : IBackgroundRenderer
{
    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!InlineDiffOptions.Enabled || textView.VisualLines.Count == 0)
        {
            return;
        }

        textView.EnsureVisualLines();
        foreach (VisualLine visual in textView.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;
            if (index < 0 || index >= rows.Count || rows[index].Kind != DiffRowKind.Changed)
            {
                continue;
            }

            InlineDiffResult result = Spans(index);
            if (!result.Highlight)
            {
                // The two lines share too little for the marks to say anything: the
                // row wash alone is the honest answer.
                continue;
            }

            DocumentLine line = visual.FirstDocumentLine;
            InlineDiffPainter.Paint(
                textView,
                drawingContext,
                left ? DiffPalette.RemovedInline : DiffPalette.AddedInline,
                line.Offset,
                line.EndOffset,
                left ? result.Left : result.Right);
        }
    }

    private InlineDiffResult Spans(int index)
    {
        if (spans.TryGetValue(index, out InlineDiffResult? cached))
        {
            return cached;
        }

        DiffRow row = rows[index];
        InlineDiffResult result = InlineDiff.Compare(row.Left ?? string.Empty, row.Right ?? string.Empty);
        spans[index] = result;
        return result;
    }
}

/// <summary>
///  A line-number margin that reports the line's number <b>in its own file</b>
///  rather than its position in the padded document, and shows nothing at all on a
///  filler row.
/// </summary>
internal sealed class AlignedLineNumberMargin(IReadOnlyList<DiffRow> rows, bool left) : AbstractMargin
{
    private const double Gap = 8;

    private Typeface _typeface = Typeface.Default;
    private double _emSize = 13;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (TextView is not { } view)
        {
            return new Size(0, 0);
        }

        _typeface = new Typeface(view.GetValue(TextBlock.FontFamilyProperty));
        _emSize = view.GetValue(TextBlock.FontSizeProperty);

        int widest = Math.Max(rows.Count, 1);
        FormattedText sample = Format(new string('9', widest.ToString(CultureInfo.InvariantCulture).Length));
        return new Size(sample.Width + Gap, 0);
    }

    /// <inheritdoc/>
    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= Redraw;
            oldTextView.ScrollOffsetChanged -= Redraw;
        }

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += Redraw;
            newTextView.ScrollOffsetChanged += Redraw;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateMeasure();
    }

    private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true } view)
        {
            return;
        }

        IBrush foreground = Application.Current?.Resources["App.TextDim"] as IBrush ?? Brushes.Gray;
        foreach (VisualLine visual in view.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;
            if (index < 0 || index >= rows.Count)
            {
                continue;
            }

            int number = left ? rows[index].LeftLine : rows[index].RightLine;
            if (number <= 0)
            {
                continue;
            }

            FormattedText text = Format(number.ToString(CultureInfo.InvariantCulture));
            text.SetForegroundBrush(foreground);
            context.DrawText(text, new Point(Bounds.Width - Gap - text.Width, visual.VisualTop - view.VerticalOffset));
        }
    }

    private FormattedText Format(string value)
        => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _emSize, Brushes.Gray);
}
