using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
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

/// <summary>What a conflict region currently holds.</summary>
internal enum MergeChoice
{
    /// <summary>Still the marker block: nobody has decided.</summary>
    Conflict,

    /// <summary>Our version.</summary>
    Ours,

    /// <summary>Their version.</summary>
    Theirs,

    /// <summary>The common ancestor: both changes dropped.</summary>
    Base,

    /// <summary>Ours followed by theirs.</summary>
    OursThenTheirs,

    /// <summary>Theirs followed by ours.</summary>
    TheirsThenOurs,

    /// <summary>Something the user typed that is none of the above.</summary>
    Custom,
}

/// <summary>
///  The <b>built-in</b> three-way merge editor: the app's own answer to kdiff3 /
///  meld, so a Linux checkout is usable without installing anything.
///
///  <para><b>Original work.</b> Upstream Git Extensions has no such window — it
///  only ever shells out to <c>git mergetool</c>. The external tool stays exactly
///  where it was: <see cref="ResolveConflictsDialog"/> still offers
///  "Open in &lt;tool&gt;" and "Start mergetool" beside it.</para>
///
///  <para><b>A choice is a state, not an edit</b> — this is the design decision
///  the whole window turns on, and the first version got it wrong. There, taking a
///  side <i>replaced the marker block with that side's text</i>, which destroyed
///  the region: the other two versions were gone from the document and there was
///  no way back to them. Reported from use, and correctly: a merge tool where the
///  first click is final is not a merge tool. Every established three-way editor
///  treats the decision as revisitable — kdiff3's A/B/C buttons select sources for
///  the conflict (and more than one may be selected), and the block-level
///  accept actions of the editor-based tools can be re-run to change the
///  answer.</para>
///
///  <para>Each conflict is therefore a <see cref="Region"/> that <b>stays alive</b>
///  for the life of the window, delimited by two <see cref="TextAnchor"/>s the
///  document moves for us. Choosing a side rewrites the text between the anchors
///  and the region is still there, still carrying all three versions, ready to be
///  told something else. Choosing the side already showing puts the conflict back
///  the way it was.</para>
///
///  <para><b>The document is still the truth.</b> Nothing records what was chosen:
///  the choice is <i>derived</i> by comparing the text between the anchors against
///  the versions the region carries, after every keystroke. So typing into a
///  resolved region marks it "edited" without any bookkeeping, and a hand edit
///  that happens to reproduce our side is simply shown as our side. A model kept
///  beside the text would have to guess what an arbitrary edit meant to it.</para>
///
///  <para><b>Layout</b>: three read-only reference panes across the top — LOCAL,
///  BASE, REMOTE, the whole file each — and the editable merge result underneath,
///  which is the only pane that decides anything. The result pane carries a margin
///  showing, per conflict, what it currently holds.</para>
/// </summary>
public sealed class MergeToolWindow : ZoomWindow
{
    private readonly string _repoPath;
    private readonly MergeDocument _doc;
    private readonly MergeToolService _service = new();

    private readonly TextEditor _result;
    private readonly TextEditor _oursPane;
    private readonly TextEditor _basePane;
    private readonly TextEditor _theirsPane;

    private readonly List<Region> _regions = [];

    private readonly RegionHighlighter _resultHighlighter;
    private readonly RangeHighlighter _oursHighlighter = new();
    private readonly RangeHighlighter _baseHighlighter = new();
    private readonly RangeHighlighter _theirsHighlighter = new();

    private readonly Dictionary<MergeChoice, ToggleButton> _choiceButtons = [];
    private readonly TextBlock _counter;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Button _restore;

    private readonly Dictionary<int, (LineRange Ours, LineRange Base, LineRange Theirs)> _sources = [];

    private int _current;
    private int _strays;
    private bool _updating;

    /// <summary>True once the file has been written and staged.</summary>
    public bool Resolved { get; private set; }

    private MergeToolWindow(string repoPath, MergeDocument document)
    {
        _repoPath = repoPath;
        _doc = document;

        Title = T("Merge") + " — " + document.Path;
        Width = 1240;
        Height = 820;
        MinWidth = 760;
        MinHeight = 480;
        Background = Brush("App.Window", Brushes.Black);

        // AvaloniaEdit's control theme lives in its own package and is pulled into
        // THIS window's styles, exactly as the diff pane does it, so the dependency
        // never leaks into the application-wide styles.
        Styles.Add(new StyleInclude(new Uri("avares://GitExtensions.Avalonia/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml"),
        });

        _oursPane = ReferenceEditor(document.OursLines, _oursHighlighter);
        _basePane = ReferenceEditor(document.BaseLines, _baseHighlighter);
        _theirsPane = ReferenceEditor(document.TheirsLines, _theirsHighlighter);

        _resultHighlighter = new RegionHighlighter(_regions, () => _current);

        _result = new TextEditor
        {
            FontFamily = AppFonts.Monospace,
            FontSize = AppFonts.MonospaceSize > 0 ? AppFonts.MonospaceSize : 13,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Background = Brush("App.Window", Brushes.Black),
            Padding = new Thickness(6, 8),
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _result.Options.EnableHyperlinks = false;
        _result.Options.EnableEmailHyperlinks = false;
        _result.Options.AllowScrollBelowDocument = false;
        _result.TextArea.TextView.BackgroundRenderers.Add(_resultHighlighter);
        _result.TextArea.LeftMargins.Insert(0, new ChoiceMargin(_regions));

        _counter = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            FontWeight = Metrics.Text.ActiveWeight,
        };
        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _save = new Button
        {
            Content = T("Save and mark resolved"),
            Padding = Metrics.Density.ButtonPadding,
            IsDefault = true,
        };
        _save.Click += (_, _) => SaveAndClose();

        _restore = new Button
        {
            Content = T("Restore conflict"),
            Padding = Metrics.Density.ButtonPadding,
            [ToolTip.TipProperty] = T("Put the marker block back, undoing the choice made here"),
        };
        _restore.Click += (_, _) => Apply(MergeChoice.Conflict);

        Button cancel = new()
        {
            Content = T("Cancel"),
            Padding = Metrics.Density.ButtonPadding,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        LocateSources(document);
        Content = BuildLayout(cancel);

        // The text and the anchors have to be created together: an anchor is a
        // position in a document, so the document must exist first, and the offsets
        // it is built from are only known while building it.
        Load(document);

        _result.TextChanged += (_, _) => Refresh();
        _result.TextArea.Caret.PositionChanged += (_, _) => FollowCaret();

        Refresh();

        // The first conflict is where the work is; opening on line 1 of a 2000-line
        // file with one conflict in the middle would make the window look empty.
        Dispatcher.UIThread.Post(() => GoTo(0), DispatcherPriority.Loaded);
    }

    /// <summary>
    ///  Opens the editor for <paramref name="entry"/> and returns whether the file
    ///  was resolved and staged. Preparing the document runs a few git commands, so
    ///  it happens off the UI thread; a file that cannot be merged line by line
    ///  (binary, a missing stage, a submodule) reports why instead of opening an
    ///  empty window.
    /// </summary>
    public static async Task<(bool Resolved, string? Error)> ShowAsync(
        Window owner, string repoPath, ConflictEntry entry)
    {
        MergeToolService service = new();
        (MergeDocument? document, string? error) =
            await Task.Run(() => service.PrepareAsync(repoPath, entry));
        if (document is null)
        {
            return (false, error ?? "The merge could not be prepared.");
        }

        MergeToolWindow window = new(repoPath, document);
        await window.ShowDialog(owner);
        return (window.Resolved, null);
    }

    // ------------------------------------------------------------------ layout

    private Control BuildLayout(Button cancel)
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,3*,Auto,2*,Auto"),
        };

        AddAt(root, BuildToolbar(), 0);
        AddAt(root, BuildReferenceRow(), 1);

        GridSplitter horizontal = new()
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
        };
        Grid.SetRow(horizontal, 2);
        PaneSplitter.Add(root, horizontal);

        AddAt(root, BuildResultPane(), 3);
        AddAt(root, BuildFooter(cancel), 4);
        return root;
    }

    private Control BuildToolbar()
    {
        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            Children =
            {
                ToolButton("◀", T("Previous conflict"), () => GoTo(_current - 1)),
                ToolButton("▶", T("Next conflict"), () => GoTo(_current + 1)),
                ToolButton("▶!", T("Next conflict nobody has decided yet"), GoToNextUnresolved),
                Spacer(),
                Choice(MergeChoice.Ours, T("Take LOCAL"), T("Keep our version here. Press again to put the conflict back")),
                Choice(MergeChoice.Theirs, T("Take REMOTE"), T("Keep their version here. Press again to put the conflict back")),
                Choice(MergeChoice.Base, T("Take BASE"), T("Drop both changes and go back to the common ancestor")),
                Choice(MergeChoice.OursThenTheirs, T("Both: L → R"), T("Keep our version followed by theirs")),
                Choice(MergeChoice.TheirsThenOurs, T("Both: R → L"), T("Keep their version followed by ours")),
                Spacer(),
                _restore,
            },
        };

        StackPanel bulk = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            Children =
            {
                ToolButton(T("All LOCAL"), T("Take our version in every conflict nobody has decided yet"),
                    () => ApplyToUndecided(MergeChoice.Ours)),
                ToolButton(T("All REMOTE"), T("Take their version in every conflict nobody has decided yet"),
                    () => ApplyToUndecided(MergeChoice.Theirs)),
            },
        };

        Grid top = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        top.Children.Add(actions);
        Grid.SetColumn(bulk, 2);
        top.Children.Add(bulk);

        Grid bottom = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, Metrics.Space.Xs, 0, 0),
        };
        Grid.SetColumn(_counter, 1);
        bottom.Children.Add(_counter);

        StackPanel bar = new()
        {
            Orientation = Orientation.Vertical,
            Margin = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs),
            Children = { top, bottom },
        };

        return new Border
        {
            Background = Brush("App.Toolbar", Brushes.DimGray),
            BorderBrush = Rule(),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };
    }

    private Control BuildReferenceRow()
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,*,4,*"),
        };

        AddColumn(row, Pane(T("LOCAL — our version"), "App.DiffAdded", _oursPane), 0);
        AddColumn(row, Pane(T("BASE — common ancestor"), "App.TextDim", _basePane), 2);
        AddColumn(row, Pane(T("REMOTE — their version"), "App.IconBlue", _theirsPane), 4);

        AddSplitter(row, 1);
        AddSplitter(row, 3);
        return row;
    }

    private Control BuildResultPane()
    {
        Grid pane = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        AddAt(pane, Header(T("MERGE RESULT — editable"), "App.Accent"), 0);
        AddAt(pane, _result, 1);
        return pane;
    }

    private Control BuildFooter(Button cancel)
    {
        Grid footer = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = Metrics.Space.All(Metrics.Space.Sm),
        };
        footer.Children.Add(_status);

        Grid.SetColumn(cancel, 1);
        cancel.Margin = new Thickness(0, 0, Metrics.Space.Xs, 0);
        footer.Children.Add(cancel);

        Grid.SetColumn(_save, 2);
        footer.Children.Add(_save);

        return new Border
        {
            Background = Brush("App.Panel", Brushes.Black),
            BorderBrush = Rule(),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footer,
        };
    }

    private TextEditor ReferenceEditor(IReadOnlyList<string> lines, RangeHighlighter highlighter)
    {
        TextEditor editor = new()
        {
            FontFamily = AppFonts.Monospace,
            FontSize = AppFonts.MonospaceSize > 0 ? AppFonts.MonospaceSize : 13,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Background = Brush("App.Panel", Brushes.Black),
            Padding = new Thickness(8, 6),
            IsReadOnly = true,
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = string.Join('\n', lines),
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.AllowScrollBelowDocument = false;
        editor.TextArea.TextView.BackgroundRenderers.Add(highlighter);
        return editor;
    }

    private Control Pane(string caption, string accentKey, TextEditor editor)
    {
        Grid pane = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
        };
        AddAt(pane, Header(caption, accentKey), 0);
        AddAt(pane, editor, 1);
        return pane;
    }

    private Control Header(string caption, string accentKey)
        => new Border
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
            },
        };

    // ------------------------------------------------------------------ loading

    /// <summary>
    ///  Fills the result document and pins one region per conflict.
    ///
    ///  <para>The document always ends in a newline so that every region can own
    ///  the newline after its last line; without that the last region in the file
    ///  would be a special case in every comparison. What actually reaches the
    ///  work tree is decided by <see cref="MergeToolService.Save"/>, which restores
    ///  the file's own ending.</para>
    /// </summary>
    private void Load(MergeDocument document)
    {
        System.Text.StringBuilder text = new();
        List<(int Start, int End, MergeChunk Chunk)> spans = [];
        int id = 0;

        foreach (MergeChunk chunk in document.Chunks)
        {
            if (chunk.Kind == MergeChunkKind.Stable)
            {
                foreach (string line in chunk.Text)
                {
                    text.Append(line).Append('\n');
                }

                continue;
            }

            id++;
            int start = text.Length;
            text.Append(MarkerBlock(chunk, id));
            spans.Add((start, text.Length, chunk));
        }

        _result.Text = text.ToString();

        TextDocument doc = _result.Document;
        for (int i = 0; i < spans.Count; i++)
        {
            (int start, int end, MergeChunk chunk) = spans[i];

            // BeforeInsertion on the start and AfterInsertion on the end is what
            // makes a region survive being rewritten: the replacement deletes the
            // span (both anchors collapse onto the same offset) and the new text is
            // then inserted there, with the start staying in front of it and the
            // end being carried to its far side. SurviveDeletion because the
            // deletion is the normal case here, not a mishap.
            TextAnchor from = doc.CreateAnchor(start);
            from.SurviveDeletion = true;
            from.MovementType = AnchorMovementType.BeforeInsertion;

            TextAnchor to = doc.CreateAnchor(end);
            to.SurviveDeletion = true;
            to.MovementType = AnchorMovementType.AfterInsertion;

            _regions.Add(new Region(i + 1, chunk, from, to));
        }
    }

    private static string MarkerBlock(MergeChunk chunk, int id)
    {
        System.Text.StringBuilder block = new();
        block.Append("<<<<<<< LOCAL #").Append(id).Append('\n');
        Append(block, chunk.Ours);
        block.Append("||||||| BASE #").Append(id).Append('\n');
        Append(block, chunk.Base);
        block.Append("=======\n");
        Append(block, chunk.Theirs);
        block.Append(">>>>>>> REMOTE #").Append(id).Append('\n');
        return block.ToString();

        static void Append(System.Text.StringBuilder builder, IReadOnlyList<string> lines)
        {
            foreach (string line in lines)
            {
                builder.Append(line).Append('\n');
            }
        }
    }

    // ----------------------------------------------------------------- choices

    /// <summary>
    ///  The text a region would hold for <paramref name="choice"/>. Every option
    ///  ends in a newline because a region owns the newline after its last line.
    /// </summary>
    private static string TextFor(Region region, MergeChoice choice)
        => choice switch
        {
            MergeChoice.Conflict => MarkerBlock(region.Chunk, region.Id),
            MergeChoice.Ours => Join(region.Chunk.Ours),
            MergeChoice.Theirs => Join(region.Chunk.Theirs),
            MergeChoice.Base => Join(region.Chunk.Base),
            MergeChoice.OursThenTheirs => Join(region.Chunk.Ours) + Join(region.Chunk.Theirs),
            MergeChoice.TheirsThenOurs => Join(region.Chunk.Theirs) + Join(region.Chunk.Ours),
            _ => string.Empty,
        };

    private static string Join(IReadOnlyList<string> lines)
        => lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";

    /// <summary>
    ///  Applies <paramref name="choice"/> to the conflict the cursor is on.
    ///  Choosing what is already there puts the marker block back, which is how a
    ///  decision is taken back without a separate gesture.
    /// </summary>
    private void Apply(MergeChoice choice)
    {
        if (Current() is not Region region)
        {
            return;
        }

        MergeChoice target = choice != MergeChoice.Conflict && region.Choice == choice
            ? MergeChoice.Conflict
            : choice;

        Replace(region, TextFor(region, target));
    }

    private void ApplyToUndecided(MergeChoice choice)
    {
        // Only the ones nobody has decided: a bulk action that overwrote deliberate
        // answers would be a way to lose work, and the button says "unresolved".
        foreach (Region region in _regions.Where(r => r.Choice == MergeChoice.Conflict).ToList())
        {
            Replace(region, TextFor(region, choice));
        }

        GoTo(_current);
    }

    private void Replace(Region region, string text)
    {
        TextDocument doc = _result.Document;
        int start = region.Start.Offset;
        int length = Math.Max(region.End.Offset - start, 0);

        _updating = true;
        try
        {
            doc.Replace(start, length, text);
        }
        finally
        {
            _updating = false;
        }

        Refresh();
        Reveal(region);
    }

    /// <summary>
    ///  Re-derives every region's choice from the text between its anchors, plus
    ///  the count of marker lines that belong to no region at all. Runs after every
    ///  change, the user's typing included: nothing is remembered, so nothing can
    ///  drift out of step with what is on screen.
    /// </summary>
    private void Refresh()
    {
        TextDocument doc = _result.Document;

        foreach (Region region in _regions)
        {
            int start = region.Start.Offset;
            int length = Math.Max(region.End.Offset - start, 0);
            string text = doc.GetText(start, length);

            region.Choice = MergeChoice.Custom;
            foreach (MergeChoice candidate in Candidates)
            {
                if (text == TextFor(region, candidate))
                {
                    region.Choice = candidate;
                    break;
                }
            }
        }

        _strays = CountStrayMarkers(doc);
        UpdateCounter();
        _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        InvalidateMargin();
    }

    private static readonly MergeChoice[] Candidates =
    [
        MergeChoice.Conflict,
        MergeChoice.Ours,
        MergeChoice.Theirs,
        MergeChoice.Base,
        MergeChoice.OursThenTheirs,
        MergeChoice.TheirsThenOurs,
    ];

    /// <summary>
    ///  Counts marker lines outside every region — debris a hand edit left behind,
    ///  which is no longer a conflict by any definition and which the counter would
    ///  otherwise ignore while announcing that everything is resolved.
    ///
    ///  <para>A lone <c>=======</c> does not count on its own: seven equals signs
    ///  under a title is valid Markdown, and warning about a heading would teach
    ///  the user to ignore the warning.</para>
    /// </summary>
    private int CountStrayMarkers(TextDocument doc)
    {
        int hard = 0;
        int equals = 0;

        for (DocumentLine? line = doc.GetLineByNumber(1); line is not null; line = line.NextLine)
        {
            char kind = MarkerKind(doc, line);
            if (kind == '\0' || _regions.Any(r => line.Offset >= r.Start.Offset && line.Offset < r.End.Offset))
            {
                continue;
            }

            if (kind == '=')
            {
                equals++;
            }
            else
            {
                hard++;
            }
        }

        return hard > 0 ? hard + equals : 0;
    }

    /// <summary>
    ///  Which conflict marker <paramref name="line"/> is, or <c>'\0'</c>.
    ///  Characters are read in place: this runs once per line per keystroke. The
    ///  length test matters for correctness too — a line of <c>=====</c> underlining
    ///  a heading in Markdown is not a marker.
    /// </summary>
    private static char MarkerKind(TextDocument doc, DocumentLine line)
    {
        if (line.Length < 7)
        {
            return '\0';
        }

        char first = doc.GetCharAt(line.Offset);
        if (first is not ('<' or '|' or '=' or '>'))
        {
            return '\0';
        }

        for (int i = 1; i < 7; i++)
        {
            if (doc.GetCharAt(line.Offset + i) != first)
            {
                return '\0';
            }
        }

        return line.Length == 7 || doc.GetCharAt(line.Offset + 7) == ' ' ? first : '\0';
    }

    // -------------------------------------------------------------- navigation

    private Region? Current()
        => _regions.Count == 0 ? null : _regions[Math.Clamp(_current, 0, _regions.Count - 1)];

    private void GoTo(int index)
    {
        if (_regions.Count == 0)
        {
            return;
        }

        _current = Math.Clamp(index, 0, _regions.Count - 1);
        Region region = _regions[_current];

        _result.ScrollToLine(_result.Document.GetLineByOffset(region.Start.Offset).LineNumber);
        Reveal(region);
    }

    private void GoToNextUnresolved()
    {
        int found = _regions.FindIndex(_current + 1, r => r.Choice == MergeChoice.Conflict);
        if (found < 0)
        {
            found = _regions.FindIndex(r => r.Choice == MergeChoice.Conflict);
        }

        if (found >= 0)
        {
            GoTo(found);
        }
    }

    /// <summary>
    ///  Makes the conflict under the caret the current one, so clicking in the
    ///  result pane is a way of choosing what the buttons act on — the same
    ///  expectation an editor sets everywhere else.
    /// </summary>
    private void FollowCaret()
    {
        if (_updating)
        {
            return;
        }

        int offset = _result.CaretOffset;
        int found = _regions.FindIndex(r => offset >= r.Start.Offset && offset <= r.End.Offset);
        if (found >= 0 && found != _current)
        {
            _current = found;
            Reveal(_regions[found]);
        }
        else
        {
            UpdateCounter();
        }
    }

    private void Reveal(Region region)
    {
        if (_sources.TryGetValue(region.Id, out (LineRange Ours, LineRange Base, LineRange Theirs) source))
        {
            Show(_oursPane, _oursHighlighter, source.Ours);
            Show(_basePane, _baseHighlighter, source.Base);
            Show(_theirsPane, _theirsHighlighter, source.Theirs);
        }

        UpdateCounter();
        _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        InvalidateMargin();
    }

    private static void Show(TextEditor editor, RangeHighlighter highlighter, LineRange range)
    {
        highlighter.Range = range;
        if (range.Length > 0)
        {
            editor.ScrollToLine(range.Start);
        }

        editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void InvalidateMargin()
    {
        foreach (Control margin in _result.TextArea.LeftMargins)
        {
            if (margin is ChoiceMargin)
            {
                margin.InvalidateVisual();
            }
        }
    }

    // ---------------------------------------------------------------- counters

    private void UpdateCounter()
    {
        int total = _regions.Count;
        int undecided = _regions.Count(r => r.Choice == MergeChoice.Conflict);
        int decided = total - undecided;

        _counter.Text = total == 0
            ? T("Nothing conflicts in this file")
            : string.Format(
                CultureInfo.CurrentCulture,
                T("Conflict {0} of {1} — {2} of {1} decided ({3})"),
                _current + 1,
                total,
                decided,
                Describe(Current()?.Choice ?? MergeChoice.Conflict));

        bool clean = undecided == 0 && _strays == 0;
        _counter.Foreground = clean
            ? Brush("App.DiffAdded", Brushes.Green)
            : Brush("App.Text", Brushes.Gainsboro);

        _save.Content = clean
            ? T("Save and mark resolved")
            : undecided > 0
                ? string.Format(CultureInfo.CurrentCulture, T("Save anyway ({0} left)"), undecided)
                : T("Save anyway");

        _status.Text = clean
            ? T("Nothing left to decide. Saving stages the file.")
            : undecided > 0
                ? T("Markers still in the result: saving would commit them.")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    T("{0} leftover marker line(s) belong to no conflict: saving would commit them."),
                    _strays);
        _status.Foreground = clean
            ? Brush("App.TextDim", Brushes.Gray)
            : Brush("App.RepoStateDirty", Brushes.Orange);

        MergeChoice active = Current()?.Choice ?? MergeChoice.Conflict;
        foreach ((MergeChoice choice, ToggleButton button) in _choiceButtons)
        {
            button.IsChecked = choice == active;
            button.IsEnabled = _regions.Count > 0;
        }

        _restore.IsEnabled = _regions.Count > 0 && active != MergeChoice.Conflict;
    }

    private static string Describe(MergeChoice choice) => choice switch
    {
        MergeChoice.Ours => T("LOCAL"),
        MergeChoice.Theirs => T("REMOTE"),
        MergeChoice.Base => T("BASE"),
        MergeChoice.OursThenTheirs => T("both, L → R"),
        MergeChoice.TheirsThenOurs => T("both, R → L"),
        MergeChoice.Custom => T("edited by hand"),
        _ => T("undecided"),
    };

    // ------------------------------------------------------------------ saving

    private void SaveAndClose()
    {
        _save.IsEnabled = false;
        _ = SaveAsync(_result.Text, _doc.Path);
    }

    private async Task SaveAsync(string text, string path)
    {
        ConflictActionResult result = await Task.Run(() => _service.Save(_repoPath, _doc, text));
        if (result.Success)
        {
            Resolved = true;
            Close();
            return;
        }

        _save.IsEnabled = true;
        _status.Text = string.Format(
            CultureInfo.CurrentCulture, T("Could not stage {0}: {1}"), path, result.Message.Trim());
        _status.Foreground = Brush("App.DiffRemoved", Brushes.Red);
    }

    // ------------------------------------------------------------ source ranges

    /// <summary>
    ///  Finds where each conflict's three versions live in the three input files.
    ///
    ///  <para>The search is a forward scan anchored to the previous match, never a
    ///  global one: conflicts appear in the same order in the result as in the
    ///  inputs, so the anchor is what keeps a repeated line (a lone <c>}</c>, say)
    ///  from matching the wrong occurrence. A version that cannot be located — an
    ///  empty side, which is a pure insertion — gets an empty range and the pane is
    ///  left where it was rather than jumping somewhere arbitrary.</para>
    /// </summary>
    private void LocateSources(MergeDocument document)
    {
        int ours = 0;
        int @base = 0;
        int theirs = 0;
        int id = 0;

        foreach (MergeChunk chunk in document.Chunks)
        {
            if (chunk.Kind != MergeChunkKind.Conflict)
            {
                continue;
            }

            id++;
            _sources[id] = (
                Locate(document.OursLines, chunk.Ours, ref ours),
                Locate(document.BaseLines, chunk.Base, ref @base),
                Locate(document.TheirsLines, chunk.Theirs, ref theirs));
        }
    }

    private static LineRange Locate(IReadOnlyList<string> haystack, IReadOnlyList<string> needle, ref int from)
    {
        if (needle.Count == 0)
        {
            return new LineRange(from + 1, 0);
        }

        for (int start = from; start + needle.Count <= haystack.Count; start++)
        {
            bool match = true;
            for (int i = 0; i < needle.Count && match; i++)
            {
                match = haystack[start + i] == needle[i];
            }

            if (match)
            {
                from = start + needle.Count;
                return new LineRange(start + 1, needle.Count);
            }
        }

        return new LineRange(from + 1, 0);
    }

    // ------------------------------------------------------------------ helpers

    private ToggleButton Choice(MergeChoice choice, string caption, string tip)
    {
        ToggleButton button = new()
        {
            Content = caption,
            Padding = Metrics.Density.ButtonPadding,
            [ToolTip.TipProperty] = tip,
        };

        // Click, not IsCheckedChanged: the checked state is a REPORT of what the
        // document holds, written by UpdateCounter. Reacting to the state changing
        // would make setting it from code look like a user decision.
        button.Click += (_, _) => Apply(choice);
        _choiceButtons[choice] = button;
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

    private static Control Spacer()
        => new Border { Width = Metrics.Space.Sm };

    private static void AddAt(Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static void AddColumn(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static void AddSplitter(Grid grid, int column)
    {
        GridSplitter splitter = new()
        {
            Width = 4,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, column);
        PaneSplitter.Add(grid, splitter);
    }

    private static IBrush Rule()
        => Icons.Tint("App.Rule") ?? Brush("App.Border", Brushes.Gray);

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);
}

/// <summary>
///  One conflict, alive for the life of the window. The anchors are the region:
///  the document moves them for us through every edit, so the conflict keeps its
///  place even after its text has been replaced several times.
/// </summary>
internal sealed class Region(int id, MergeChunk chunk, TextAnchor start, TextAnchor end)
{
    /// <summary>1-based number, as written into the marker labels.</summary>
    public int Id { get; } = id;

    /// <summary>The three versions this conflict offers.</summary>
    public MergeChunk Chunk { get; } = chunk;

    /// <summary>Start of the region.</summary>
    public TextAnchor Start { get; } = start;

    /// <summary>End of the region, newline of the last line included.</summary>
    public TextAnchor End { get; } = end;

    /// <summary>What the region currently holds. Derived, never remembered.</summary>
    public MergeChoice Choice { get; set; } = MergeChoice.Conflict;
}

/// <summary>
///  The colours a choice is shown in, shared by the result pane's wash and its
///  margin so that a conflict reads the same in both.
/// </summary>
internal static class ChoicePalette
{
    /// <summary>Wash behind a region holding <paramref name="choice"/>.</summary>
    public static IBrush Wash(MergeChoice choice) => choice switch
    {
        MergeChoice.Ours => OursWash,
        MergeChoice.Theirs => TheirsWash,
        MergeChoice.Base => BaseWash,
        MergeChoice.OursThenTheirs or MergeChoice.TheirsThenOurs => BothWash,
        MergeChoice.Custom => CustomWash,
        _ => ConflictWash,
    };

    /// <summary>The solid colour of the margin bar.</summary>
    public static IBrush Bar(MergeChoice choice) => choice switch
    {
        MergeChoice.Ours => OursBar,
        MergeChoice.Theirs => TheirsBar,
        MergeChoice.Base => BaseBar,
        MergeChoice.OursThenTheirs or MergeChoice.TheirsThenOurs => BothBar,
        MergeChoice.Custom => CustomBar,
        _ => ConflictBar,
    };

    /// <summary>The word shown in the margin.</summary>
    public static string Label(MergeChoice choice) => choice switch
    {
        MergeChoice.Ours => "LOCAL",
        MergeChoice.Theirs => "REMOTE",
        MergeChoice.Base => "BASE",
        MergeChoice.OursThenTheirs => "L → R",
        MergeChoice.TheirsThenOurs => "R → L",
        MergeChoice.Custom => "EDITED",
        _ => "CONFLICT",
    };

    private static readonly IBrush OursWash = new SolidColorBrush(Color.FromArgb(0x22, 0x6A, 0xC7, 0x76));
    private static readonly IBrush TheirsWash = new SolidColorBrush(Color.FromArgb(0x22, 0x5B, 0x9C, 0xFF));
    private static readonly IBrush BaseWash = new SolidColorBrush(Color.FromArgb(0x1C, 0x9B, 0x9B, 0x9B));
    private static readonly IBrush BothWash = new SolidColorBrush(Color.FromArgb(0x22, 0x37, 0xB6, 0xC9));
    private static readonly IBrush CustomWash = new SolidColorBrush(Color.FromArgb(0x22, 0xB1, 0x97, 0xE1));
    private static readonly IBrush ConflictWash = new SolidColorBrush(Color.FromArgb(0x26, 0xE0, 0xA7, 0x3C));

    private static readonly IBrush OursBar = new SolidColorBrush(Color.FromRgb(0x5B, 0xC4, 0x6B));
    private static readonly IBrush TheirsBar = new SolidColorBrush(Color.FromRgb(0x5B, 0x9C, 0xFF));
    private static readonly IBrush BaseBar = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly IBrush BothBar = new SolidColorBrush(Color.FromRgb(0x37, 0xB6, 0xC9));
    private static readonly IBrush CustomBar = new SolidColorBrush(Color.FromRgb(0xB1, 0x97, 0xE1));
    private static readonly IBrush ConflictBar = new SolidColorBrush(Color.FromRgb(0xE0, 0xA7, 0x3C));
}

/// <summary>
///  Paints the conflict regions of the result. An undecided one keeps the
///  three-colour breakdown of the marker block it holds; a decided one gets the
///  flat wash of its choice, so "still to do" and "done" are told apart at a
///  glance and not by reading.
///
///  <para>A background <i>renderer</i> rather than a line transformer, because a
///  region contains empty lines and a transformer can only colour characters — an
///  empty line would punch a hole through the middle of the block.</para>
/// </summary>
internal sealed class RegionHighlighter(IReadOnlyList<Region> regions, Func<int> current) : IBackgroundRenderer
{
    private static readonly IBrush MarkerWash = new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0xA7, 0x3C));
    private static readonly IBrush OursWash = new SolidColorBrush(Color.FromArgb(0x24, 0x6A, 0xC7, 0x76));
    private static readonly IBrush BaseWash = new SolidColorBrush(Color.FromArgb(0x1C, 0x9B, 0x9B, 0x9B));
    private static readonly IBrush TheirsWash = new SolidColorBrush(Color.FromArgb(0x24, 0x5B, 0x9C, 0xFF));
    private static readonly IBrush CurrentEdge = new SolidColorBrush(Color.FromRgb(0x5B, 0x9C, 0xFF));

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (regions.Count == 0 || textView.VisualLines.Count == 0 || textView.Document is not { } doc)
        {
            return;
        }

        textView.EnsureVisualLines();
        int active = current();

        for (int i = 0; i < regions.Count; i++)
        {
            Region region = regions[i];
            int first = doc.GetLineByOffset(region.Start.Offset).LineNumber;
            int last = LastLine(doc, region);

            if (region.Choice == MergeChoice.Conflict)
            {
                // The block's shape is known from the versions it carries, so the
                // three sections can be washed separately without re-parsing it.
                int ours = first + 1;
                int mid = ours + region.Chunk.Ours.Count;
                int sep = mid + 1 + region.Chunk.Base.Count;
                int end = sep + 1 + region.Chunk.Theirs.Count;

                Fill(textView, drawingContext, ours, mid, OursWash);
                Fill(textView, drawingContext, mid + 1, sep, BaseWash);
                Fill(textView, drawingContext, sep + 1, end, TheirsWash);
                Fill(textView, drawingContext, first, first + 1, MarkerWash);
                Fill(textView, drawingContext, mid, mid + 1, MarkerWash);
                Fill(textView, drawingContext, sep, sep + 1, MarkerWash);
                Fill(textView, drawingContext, end, end + 1, MarkerWash);
            }
            else
            {
                Fill(textView, drawingContext, first, last + 1, ChoicePalette.Wash(region.Choice));
            }

            if (i == active)
            {
                foreach (Rect rect in Rects(textView, first, last + 1))
                {
                    drawingContext.FillRectangle(CurrentEdge, new Rect(rect.X, rect.Y, 3, rect.Height));
                }
            }
        }
    }

    private static int LastLine(TextDocument doc, Region region)
    {
        // The region owns the newline after its last line, so the offset at its end
        // is the START of the following line: step back one character to land on
        // the line the user thinks of as the last one.
        int end = Math.Max(region.End.Offset - 1, region.Start.Offset);
        return doc.GetLineByOffset(end).LineNumber;
    }

    private static void Fill(TextView view, DrawingContext context, int from, int to, IBrush brush)
    {
        foreach (Rect rect in Rects(view, from, to))
        {
            context.FillRectangle(brush, rect);
        }
    }

    private static IEnumerable<Rect> Rects(TextView view, int from, int to)
    {
        foreach (VisualLine visual in view.VisualLines)
        {
            int number = visual.FirstDocumentLine.LineNumber;
            if (number < from || number >= to)
            {
                continue;
            }

            yield return new Rect(
                0, visual.VisualTop - view.VerticalOffset, Math.Max(view.Bounds.Width, 0), visual.Height);
        }
    }
}

/// <summary>
///  The strip down the left of the result pane: one coloured bar per conflict with
///  the name of what it currently holds.
///
///  <para>It exists so that the state of a conflict is readable <b>where the
///  conflict is</b>, not only in the toolbar for whichever one is selected.
///  Scrolling through a file with a dozen conflicts and seeing at a glance which
///  are still open is the difference between a tool and a form.</para>
/// </summary>
internal sealed class ChoiceMargin(IReadOnlyList<Region> regions) : AbstractMargin
{
    private const double BarWidth = 4;
    private const double Gap = 4;

    private Typeface _typeface = Typeface.Default;
    private double _emSize = 10;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (TextView is not { } view)
        {
            return new Size(0, 0);
        }

        _typeface = new Typeface(view.GetValue(TextBlock.FontFamilyProperty));
        _emSize = Math.Max(view.GetValue(TextBlock.FontSizeProperty) - 2, 8);
        return new Size(BarWidth + Gap + Format("CONFLICT").Width + Gap, 0);
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
        if (TextView is not { VisualLinesValid: true } view || view.Document is not { } doc)
        {
            return;
        }

        foreach (Region region in regions)
        {
            int first = doc.GetLineByOffset(region.Start.Offset).LineNumber;
            int last = doc.GetLineByOffset(Math.Max(region.End.Offset - 1, region.Start.Offset)).LineNumber;

            double? top = null;
            double bottom = 0;
            foreach (VisualLine visual in view.VisualLines)
            {
                int number = visual.FirstDocumentLine.LineNumber;
                if (number < first || number > last)
                {
                    continue;
                }

                double y = visual.VisualTop - view.VerticalOffset;
                top ??= y;
                bottom = y + visual.Height;
            }

            if (top is not double start)
            {
                continue;
            }

            IBrush bar = ChoicePalette.Bar(region.Choice);
            context.FillRectangle(bar, new Rect(0, start, BarWidth, bottom - start));

            FormattedText label = Format(ChoicePalette.Label(region.Choice));
            label.SetForegroundBrush(bar);
            context.DrawText(label, new Point(BarWidth + Gap, start));
        }
    }

    private FormattedText Format(string value)
        => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _emSize, Brushes.Gray);
}

/// <summary>A run of lines, 1-based and possibly empty.</summary>
public readonly record struct LineRange(int Start, int Length)
{
    /// <summary>The line after the last one in the range.</summary>
    public int End => Start + Length;
}

/// <summary>
///  Paints one range of lines in a reference pane — the version of the current
///  conflict that pane is showing. Same reason as <see cref="RegionHighlighter"/>
///  for being a renderer and not a transformer.
/// </summary>
internal sealed class RangeHighlighter : IBackgroundRenderer
{
    private static readonly IBrush Wash = new SolidColorBrush(Color.FromArgb(0x3C, 0xE0, 0xA7, 0x3C));

    /// <summary>The lines to wash; a zero-length range paints nothing.</summary>
    public LineRange Range { get; set; }

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Range.Length <= 0 || textView.VisualLines.Count == 0)
        {
            return;
        }

        textView.EnsureVisualLines();
        foreach (VisualLine visual in textView.VisualLines)
        {
            int number = visual.FirstDocumentLine.LineNumber;
            if (number < Range.Start || number >= Range.End)
            {
                continue;
            }

            drawingContext.FillRectangle(
                Wash,
                new Rect(0, visual.VisualTop - textView.VerticalOffset,
                    Math.Max(textView.Bounds.Width, 0), visual.Height));
        }
    }
}
