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
    private readonly ComboBox _inlineMode;
    private readonly TextBlock _counter;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Button _restore;
    private readonly Button _trivial;

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

        _trivial = new Button
        {
            Padding = Metrics.Density.ButtonPadding,
        };
        _trivial.Click += (_, _) => ResolveTrivial();

        _inlineMode = new ComboBox
        {
            ItemsSource = new[]
            {
                T("Inline: LOCAL ↔ REMOTE"),
                T("Inline: each side ↔ BASE"),
                T("Inline: off"),
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = Metrics.Density.ButtonPadding,
            [ToolTip.TipProperty] = T(
                "Mark, inside the lines of the current conflict, the characters that actually differ. "
                    + "LOCAL ↔ REMOTE shows what the two sides disagree about; each side ↔ BASE shows what "
                    + "each of them changed."),
        };

        // The mode is read back out of the combo when the marks are rebuilt, so
        // there is nothing to keep in step here beyond asking for a rebuild.
        _inlineMode.SelectionChanged += (_, _) =>
        {
            if (Current() is Region region)
            {
                Reveal(region);
            }
        };

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
                _trivial,
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

        // Beside the counter and not among the action buttons above: it changes
        // what the panes SHOW, it never changes what the file will contain, and
        // mixing the two kinds of control is how a merge tool gets clicked wrong.
        bottom.Children.Add(_inlineMode);

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

    /// <summary>
    ///  Settles every conflict <see cref="TrivialConflict"/> could classify and
    ///  nobody has answered yet.
    ///
    ///  <para><b>Only on the button.</b> Nothing is classified away when the file
    ///  opens: the window would then present, as the merge, a document the user
    ///  never agreed to, and the one thing a merge tool sells is that what is on
    ///  screen is what will be committed. Pressing the button is the consent; the
    ///  count in its caption is what is being consented to.</para>
    ///
    ///  <para>It goes through <see cref="Replace"/> — the same call a click on
    ///  "Take LOCAL" makes — precisely so that nothing special happened: each
    ///  region is left holding a side, its choice is re-derived from the text like
    ///  everyone else's, and it can be sent back to the marker block one at a time.
    ///  A shortcut that wrote the document behind the regions' backs would buy
    ///  nothing and cost the undo.</para>
    /// </summary>
    private void ResolveTrivial()
    {
        foreach (Region region in _regions.Where(r => r.Choice == MergeChoice.Conflict).ToList())
        {
            if (region.Proposal is MergeChoice choice)
            {
                Replace(region, TextFor(region, choice));
            }
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
        BuildInlineMarks(region);

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

    // ------------------------------------------------------------ intra-line marks

    /// <summary>
    ///  Rebuilds the intra-line marks for the conflict now being shown.
    ///
    ///  <para><b>What is expensive here is not this.</b> Only the pairing is built:
    ///  a line of one side is told which line of the other it is to be compared
    ///  with, and nothing is diffed. The character diff itself is run by the
    ///  renderer, for the lines actually on screen, and cached until the next
    ///  rebuild — a conflict of ten thousand lines therefore costs ten thousand
    ///  dictionary entries and a handful of diffs, not ten thousand diffs.</para>
    ///
    ///  <para><b>BASE is never marked itself.</b> In the "each side ↔ BASE" mode
    ///  the base pane is the thing both sides are measured against, so marking it
    ///  would have to show two answers at once in one pane — the LOCAL story and
    ///  the REMOTE one — which is exactly the rainbow this feature is meant to
    ///  avoid. The two changed pictures are drawn where they belong, each in its
    ///  own pane and in that pane's own colour.</para>
    /// </summary>
    private void BuildInlineMarks(Region region)
    {
        _oursHighlighter.Inline.Clear();
        _baseHighlighter.Inline.Clear();
        _theirsHighlighter.Inline.Clear();

        if (_inlineMode.SelectedIndex is not (SidesMode or BaseMode)
            || !_sources.TryGetValue(region.Id, out (LineRange Ours, LineRange Base, LineRange Theirs) source))
        {
            return;
        }

        MergeChunk chunk = region.Chunk;

        if (_inlineMode.SelectedIndex == SidesMode)
        {
            // One ink for both panes: the statement is "these two lines disagree
            // HERE", and it is a single statement about a pair, not two.
            _oursHighlighter.Inline.SetInk(SidesInk);
            _theirsHighlighter.Inline.SetInk(SidesInk);
            Pair(chunk.Ours, source.Ours, _oursHighlighter.Inline, chunk.Theirs, source.Theirs, _theirsHighlighter.Inline);
            return;
        }

        // Against the base the two answers are independent, so each pane speaks in
        // its own header colour — which is also what tells this mode apart from the
        // other one at a glance, without reading the combo box.
        _oursHighlighter.Inline.SetInk(OursInk);
        _theirsHighlighter.Inline.SetInk(TheirsInk);
        Pair(chunk.Base, source.Base, null, chunk.Ours, source.Ours, _oursHighlighter.Inline);
        Pair(chunk.Base, source.Base, null, chunk.Theirs, source.Theirs, _theirsHighlighter.Inline);
    }

    private const int SidesMode = 0;
    private const int BaseMode = 1;

    // Same three colours the window already speaks in: the amber of a conflict for
    // the LOCAL↔REMOTE reading, and each side's header colour for the ↔BASE one.
    private static readonly Color SidesInk = Color.FromRgb(0xE0, 0xA7, 0x3C);
    private static readonly Color OursInk = Color.FromRgb(0x6A, 0xC7, 0x76);
    private static readonly Color TheirsInk = Color.FromRgb(0x5B, 0x9C, 0xFF);

    /// <summary>
    ///  Tells each line of one side which line of the other it will be compared
    ///  with, and hands the pairs to the panes' overlays.
    ///
    ///  <para>A version whose lines could not be located in its file (an empty
    ///  side, or a repeated block the forward scan gave up on) is skipped whole:
    ///  the line numbers would be a guess, and a mark drawn on the wrong line is
    ///  worse than no mark.</para>
    /// </summary>
    private static void Pair(
        IReadOnlyList<string> left, LineRange leftRange, InlineOverlay? leftOverlay,
        IReadOnlyList<string> right, LineRange rightRange, InlineOverlay? rightOverlay)
    {
        if (leftRange.Length != left.Count || rightRange.Length != right.Count)
        {
            return;
        }

        foreach ((int l, int r) in Align(left, right))
        {
            leftOverlay?.Add(leftRange.Start + l, left[l], right[r], selfIsLeft: true);
            rightOverlay?.Add(rightRange.Start + r, left[l], right[r], selfIsLeft: false);
        }
    }

    // Budget for the line alignment table. A conflict is a handful of lines in
    // practice; past this the pairing would cost more than the marks are worth,
    // and the fallback below is still correct, only less generous.
    private const int MaxAlignCells = 64 * 64;

    /// <summary>
    ///  Which line of <paramref name="left"/> is to be read against which line of
    ///  <paramref name="right"/>. Identical lines are left out: they have nothing
    ///  to mark.
    ///
    ///  <para><b>The pairing rule</b>, which is the whole difficulty of this
    ///  feature. Equal line counts are not enough to pair by position — a side that
    ///  inserted a line at the top would then have every line compared with the
    ///  wrong one, and the panes would fill with marks that mean nothing. So the two
    ///  line lists are first <i>aligned</i> on the lines they have in common (a
    ///  longest-common-subsequence walk, the same idea a line diff uses). Between
    ///  two such anchors sit a run of <c>a</c> unmatched left lines and a run of
    ///  <c>b</c> unmatched right lines: when <c>a == b</c> they are paired by
    ///  position, which is the only pairing the evidence supports, and when
    ///  <c>a != b</c> <b>nothing in that run is paired at all</b>. Lines appear and
    ///  disappear there, and any guess about which line replaced which would be a
    ///  guess the user cannot check.</para>
    ///
    ///  <para>Past the size budget the table is not built and the two sides are
    ///  paired by position only if they have the same number of lines — the same
    ///  rule, restricted to the one case where the alignment could not have found
    ///  anything better.</para>
    /// </summary>
    private static List<(int Left, int Right)> Align(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        List<(int Left, int Right)> pairs = [];
        int n = left.Count;
        int m = right.Count;
        if (n == 0 || m == 0)
        {
            return pairs;
        }

        if ((long)n * m > MaxAlignCells)
        {
            if (n == m)
            {
                for (int i = 0; i < n; i++)
                {
                    pairs.Add((i, i));
                }
            }

            return pairs;
        }

        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0;
        int y = 0;
        int leftRun = 0;
        int rightRun = 0;

        while (x < n && y < m)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                Flush(leftRun, x, rightRun, y);
                x++;
                y++;
                leftRun = x;
                rightRun = y;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        Flush(leftRun, n, rightRun, m);
        return pairs;

        void Flush(int leftFrom, int leftTo, int rightFrom, int rightTo)
        {
            if (leftTo - leftFrom != rightTo - rightFrom)
            {
                return;
            }

            for (int i = 0; i < leftTo - leftFrom; i++)
            {
                pairs.Add((leftFrom + i, rightFrom + i));
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
                Describe(Current()?.Choice ?? MergeChoice.Conflict))
                + (Current() is { Chunk.Trivial: not TrivialKind.None } shown
                    // Said here and not only in the margin because this is where the
                    // user looks before pressing a side: knowing WHY a conflict is
                    // harmless is what makes the automatic answer checkable rather
                    // than something to take on faith.
                    ? " — " + TrivialText.Sentence(shown.Chunk.Trivial)
                    : string.Empty);

        UpdateTrivialButton();

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

    /// <summary>
    ///  Keeps the trivial-resolution button honest: it offers only what it can
    ///  still do — conflicts that are classifiable AND unanswered — and it says
    ///  how many that is, because a bulk action whose extent is invisible until
    ///  after the click is a bulk action nobody presses twice.
    /// </summary>
    private void UpdateTrivialButton()
    {
        List<Region> pending =
            [.. _regions.Where(r => r.Choice == MergeChoice.Conflict && r.Proposal is not null)];

        _trivial.IsEnabled = pending.Count > 0;
        _trivial.Content = pending.Count == 0
            ? T("Resolve trivial")
            : string.Format(CultureInfo.CurrentCulture, T("Resolve trivial ({0})"), pending.Count);

        ToolTip.SetTip(_trivial, pending.Count == 0
            ? T("No conflict left where the two sides say the same thing in a different spelling")
            : string.Format(
                CultureInfo.CurrentCulture,
                T("{0} conflict(s) differ only in {1}. They are settled the way a manual choice would "
                    + "settle them, so each one can still be reopened afterwards."),
                pending.Count,
                string.Join(
                    T(", "),
                    pending.Select(r => TrivialText.Short(r.Chunk.Trivial)).Distinct())));
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

    /// <summary>
    ///  The choice this conflict's triviality proposes, or <c>null</c> when the
    ///  two sides genuinely disagree and only the user may answer.
    /// </summary>
    public MergeChoice? Proposal => Chunk.Proposed switch
    {
        TrivialResolution.Ours => MergeChoice.Ours,
        TrivialResolution.Theirs => MergeChoice.Theirs,
        _ => null,
    };

    /// <summary>
    ///  Whether the region is currently holding the side its triviality proposed.
    ///
    ///  <para>Derived like everything else here, which has a consequence worth
    ///  stating: a trivial conflict the user answered <i>by hand</i> with the same
    ///  side is shown the same way. That is right — the mark says "this region
    ///  holds a side that provably lost nothing", not "a button put it there" —
    ///  and it is what keeps the flag from needing a memory that a later hand edit
    ///  could invalidate.</para>
    /// </summary>
    public bool SettledAsTrivial => Proposal is MergeChoice proposal && Choice == proposal;
}

/// <summary>
///  The words the window uses for <see cref="TrivialKind"/>. Kept here and not in
///  the service because a classification is a fact and a wording is an interface:
///  the service must stay sayable in any language the UI later grows.
/// </summary>
internal static class TrivialText
{
    /// <summary>What fits in the margin beside a region.</summary>
    public static string Short(TrivialKind kind) => kind switch
    {
        TrivialKind.LineEnding => T("line endings"),
        TrivialKind.TrailingWhitespace => T("trailing spaces"),
        TrivialKind.Whitespace => T("spacing"),
        TrivialKind.BlankLines => T("blank lines"),
        TrivialKind.OneSideUnchanged => T("one side unchanged"),
        _ => string.Empty,
    };

    /// <summary>What the counter line says, in full.</summary>
    public static string Sentence(TrivialKind kind) => kind switch
    {
        TrivialKind.LineEnding => T("both sides are the same text, only the line endings differ"),
        TrivialKind.TrailingWhitespace => T("both sides are the same text, only spaces at the end of lines differ"),
        TrivialKind.Whitespace => T("both sides are the same text, only the spacing differs"),
        TrivialKind.BlankLines => T("both sides are the same text, only blank lines differ"),
        TrivialKind.OneSideUnchanged => T("one side did not change anything here"),
        _ => string.Empty,
    };

    private static string T(string english) => TranslationService.T(english);
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

    // The widest note the margin can be asked to draw, used to reserve room. A
    // literal rather than a scan of the regions: the width must not change while
    // the user works, or the text would shift sideways as conflicts are answered.
    private const string WidestNote = " ✓one side unchanged";

    // Deliberately not one of the choice colours: the note answers a different
    // question ("why was this safe?") from the bar beside it ("what does it
    // hold?"), and painting them alike would read as one statement.
    private static readonly IBrush TrivialInk = new SolidColorBrush(Color.FromRgb(0x37, 0xB6, 0xC9));

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
        return new Size(BarWidth + Gap + Format("CONFLICT").Width + Format(WidestNote).Width + Gap, 0);
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

            if (region.Chunk.Trivial == TrivialKind.None)
            {
                continue;
            }

            // The tick is what tells a region settled by triviality from one the
            // user weighed: both may end up holding LOCAL, and only one of the two
            // was decided by an argument the machine can make. Before the button is
            // pressed the same note appears without the tick, as a promise of what
            // "Resolve trivial" would do here.
            FormattedText note = Format(
                (region.SettledAsTrivial ? " ✓" : " ·") + TrivialText.Short(region.Chunk.Trivial));
            note.SetForegroundBrush(TrivialInk);
            context.DrawText(note, new Point(BarWidth + Gap + label.Width, start));
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
///  The intra-line marks of one reference pane: which line is read against which,
///  and the characters that differ between them.
///
///  <para><b>The pairing is pushed in, the diff is pulled out.</b> The window knows
///  which lines belong together and says so once per conflict; the character diff
///  is only run when a line is asked about, which the renderer does for the lines
///  on screen, and the answer is kept until the next <see cref="Clear"/>. So
///  scrolling a conflict pays for what is visible and repainting pays for
///  nothing.</para>
///
///  <para>The cache needs no document listener: the three reference panes are
///  read-only and their text never changes for the life of the window. What does
///  change is which conflict is shown and which comparison was asked for, and both
///  of those go through <see cref="Clear"/>.</para>
/// </summary>
internal sealed class InlineOverlay
{
    private readonly Dictionary<int, (string Left, string Right, bool SelfIsLeft)> _pairs = [];
    private readonly Dictionary<int, IReadOnlyList<InlineSpan>> _cache = [];

    /// <summary>Fill of a marked stretch, or <c>null</c> when marking is off.</summary>
    public IBrush? Fill { get; private set; }

    /// <summary>Outline of a marked stretch: what makes a one-character mark visible.</summary>
    public IPen? Edge { get; private set; }

    /// <summary>Forgets every pairing, every cached diff and the ink.</summary>
    public void Clear()
    {
        _pairs.Clear();
        _cache.Clear();
        Fill = null;
        Edge = null;
    }

    /// <summary>Sets the colour this pane marks in.</summary>
    public void SetInk(Color color)
    {
        // Translucent enough that the wash of the conflict and the text under it
        // both survive: the mark says "look closer here", it does not replace the
        // line's own colour. The outline carries the mark when the fill alone would
        // be too faint to see — a single changed character.
        Fill = new SolidColorBrush(color, 0.34);
        Edge = new Pen(new SolidColorBrush(color, 0.85), 1);
    }

    /// <summary>Records that <paramref name="line"/> of this pane is read against its counterpart.</summary>
    public void Add(int line, string left, string right, bool selfIsLeft)
        => _pairs[line] = (left, right, selfIsLeft);

    /// <summary>
    ///  The stretches to mark on <paramref name="line"/>. Empty when the line has
    ///  no counterpart, when the two are identical, or when the engine judged the
    ///  two lines too far apart for marking to help — a rewritten line is read as a
    ///  rewritten line, not as a chain of boxes.
    /// </summary>
    public IReadOnlyList<InlineSpan> Spans(int line)
    {
        if (_cache.TryGetValue(line, out IReadOnlyList<InlineSpan>? cached))
        {
            return cached;
        }

        if (!_pairs.TryGetValue(line, out (string Left, string Right, bool SelfIsLeft) pair))
        {
            return [];
        }

        InlineDiffResult result = InlineDiff.Compare(pair.Left, pair.Right);
        IReadOnlyList<InlineSpan> spans = !result.Highlight
            ? []
            : pair.SelfIsLeft ? result.Left : result.Right;

        _cache[line] = spans;
        return spans;
    }
}

/// <summary>
///  Paints one range of lines in a reference pane — the version of the current
///  conflict that pane is showing — and, inside those lines, the characters that
///  differ from the version it is being compared with. Same reason as
///  <see cref="RegionHighlighter"/> for being a renderer and not a transformer,
///  and here it is not a nicety: a conflict routinely contains blank lines, and a
///  line transformer cannot colour a line that has no characters.
/// </summary>
internal sealed class RangeHighlighter : IBackgroundRenderer
{
    private static readonly IBrush Wash = new SolidColorBrush(Color.FromArgb(0x3C, 0xE0, 0xA7, 0x3C));

    /// <summary>The lines to wash; a zero-length range paints nothing.</summary>
    public LineRange Range { get; set; }

    /// <summary>The intra-line marks drawn on top of the wash.</summary>
    public InlineOverlay Inline { get; } = new();

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

            // Drawn after the wash of the same line and never instead of it: the
            // line stays part of its conflict, the mark only says where to look.
            DrawInline(textView, drawingContext, visual.FirstDocumentLine, number);
        }
    }

    private void DrawInline(TextView textView, DrawingContext context, DocumentLine line, int number)
    {
        if (Inline.Fill is not IBrush fill)
        {
            return;
        }

        IReadOnlyList<InlineSpan> spans = Inline.Spans(number);
        if (spans.Count == 0)
        {
            return;
        }

        BackgroundGeometryBuilder builder = new() { AlignToWholePixels = true, CornerRadius = 2 };
        foreach (InlineSpan span in spans)
        {
            // Clamped rather than trusted: the spans are offsets into the string the
            // window paired this line with, and a pane whose file moved under us
            // would otherwise index past the end of the line.
            int start = Math.Clamp(span.Start, 0, line.Length);
            int end = Math.Clamp(span.End, start, line.Length);
            if (end == start)
            {
                continue;
            }

            builder.AddSegment(textView, new TextSegment
            {
                StartOffset = line.Offset + start,
                EndOffset = line.Offset + end,
            });
        }

        if (builder.CreateGeometry() is Geometry geometry)
        {
            context.DrawGeometry(fill, Inline.Edge, geometry);
        }
    }
}
