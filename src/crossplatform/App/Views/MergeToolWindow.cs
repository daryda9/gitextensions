using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
///  What a region git merged by itself currently holds.
///
///  <para>Deliberately a second enum and not <see cref="MergeChoice"/>: the resting
///  state of an automatic merge is "what git decided", which is not one of the
///  answers a conflict can hold, and the two lists must not be assignable to each
///  other by accident.</para>
/// </summary>
internal enum AutoChoice
{
    /// <summary>Still what <c>git merge-file</c> produced.</summary>
    Git,

    /// <summary>Overridden by hand with our version.</summary>
    Ours,

    /// <summary>Overridden by hand with their version.</summary>
    Theirs,

    /// <summary>Overridden by hand with the common ancestor: git's change undone.</summary>
    Base,

    /// <summary>Typed over: none of the above.</summary>
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

    /// <summary>
    ///  The regions git merged without asking. Not conflicts and never turned into
    ///  them: they are already settled, and the list exists so that "settled" does
    ///  not have to mean "invisible".
    /// </summary>
    private readonly List<AutoSpan> _autos = [];

    private readonly RegionHighlighter _resultHighlighter;
    private readonly RangeHighlighter _oursHighlighter = new();
    private readonly RangeHighlighter _baseHighlighter = new();
    private readonly RangeHighlighter _theirsHighlighter = new();

    private readonly Dictionary<MergeChoice, ToggleButton> _choiceButtons = [];
    private readonly ComboBox _inlineMode;
    private readonly TextBlock _counter;
    private readonly TextBlock _summary;
    private readonly TextBlock _autoNote;
    private readonly TextBlock _status;
    private readonly Button _save;
    private readonly Button _restore;
    private readonly Button _trivial;
    private readonly Button _previousAuto;
    private readonly Button _nextAuto;

    private readonly Dictionary<int, (LineRange Ours, LineRange Base, LineRange Theirs)> _sources = [];

    private int _current;

    /// <summary>Which automatic merge is being inspected, or -1 for none.</summary>
    private int _currentAuto = -1;

    private int _strays;
    private bool _updating;

    /// <summary>
    ///  What the context menu is about to act on, resolved from the pointer when the
    ///  menu opens and left alone afterwards, so that every item in one opening of
    ///  the menu acts on the region its caption named.
    /// </summary>
    private Region? _menuRegion;

    private AutoSpan? _menuAuto;

    /// <summary>Offset of the first character of the line right-clicked, or -1.</summary>
    private int _menuOffset = -1;

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

        _resultHighlighter = new RegionHighlighter(_regions, () => _current, _autos, () => _currentAuto);

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
        _result.TextArea.LeftMargins.Insert(0, new ChoiceMargin(_regions, _autos));

        _counter = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            FontWeight = Metrics.Text.ActiveWeight,
        };

        // The line that answers the question kdiff3 answers with a dialog on open:
        // how much of this merge is already done, and by whom. It is a line and not
        // a modal because the answer is still worth having an hour later, and
        // because an obstacle that must be dismissed before working is not
        // information, it is a toll.
        _summary = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, Metrics.Space.Xs, 0, 0),
        };

        // Which automatic merge is being looked at is a different fact from the
        // file-wide tally, and it belongs beside "Conflict 1 of 2" — the two answer
        // the same question about the two journeys through the file. Keeping it out
        // of the summary is also what stops the summary from being trimmed away.
        _autoNote = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(Metrics.Space.Sm, 0, Metrics.Space.Sm, 0),
        };

        _previousAuto = ToolButton(
            "◀ᴀ",
            T("Previous region git merged by itself. These are already settled: this only shows you what "
                + "was decided for you"),
            () => GoToAuto(_currentAuto - 1));
        _nextAuto = ToolButton(
            "ᴀ▶",
            T("Next region git merged by itself. These are already settled: this only shows you what "
                + "was decided for you"),
            () => GoToAuto(_currentAuto + 1));
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
            SelectedIndex = RestoredInlineMode(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = Metrics.Density.ButtonPadding,
            [ToolTip.TipProperty] = T(
                "Mark, inside the lines of the current conflict, the characters that actually differ. "
                    + "LOCAL ↔ REMOTE shows what the two sides disagree about; each side ↔ BASE shows what "
                    + "each of them changed."),
        };

        // The mode is read back out of the combo when the marks are rebuilt, so
        // there is nothing to keep in step here beyond asking for a rebuild — of the
        // reference panes AND of the result, which reads the same combo.
        //
        // Attached after the initial SelectedIndex so that seeding the control from
        // the file cannot be mistaken for the user choosing, and write the file back.
        _inlineMode.SelectionChanged += (_, _) =>
        {
            PersistInlineMode();
            BuildResultMarks();

            if (Current() is Region region)
            {
                Reveal(region);
            }
            else
            {
                _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
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
        AttachMenus();

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

                // A second pair and not a mode on the first: the two journeys are
                // different questions ("what must I answer?" and "what was answered
                // for me?"), and folding them into one ◀ ▶ would make the answer to
                // the first one arrive at unpredictable moments.
                _previousAuto,
                _nextAuto,
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
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, Metrics.Space.Xs, 0, 0),
        };

        // Beside the counter and not among the action buttons above: it changes
        // what the panes SHOW, it never changes what the file will contain, and
        // mixing the two kinds of control is how a merge tool gets clicked wrong.
        bottom.Children.Add(_inlineMode);

        Grid.SetColumn(_autoNote, 1);
        bottom.Children.Add(_autoNote);

        Grid.SetColumn(_counter, 2);
        bottom.Children.Add(_counter);

        StackPanel bar = new()
        {
            Orientation = Orientation.Vertical,
            Margin = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs),
            Children = { top, bottom, _summary },
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

        // Where each stable chunk begins in the document, so an automatic merge
        // reported as "chunk 4, line 2" can be turned into an offset without
        // searching the text for it.
        Dictionary<int, int> chunkStarts = [];
        int id = 0;

        for (int c = 0; c < document.Chunks.Count; c++)
        {
            MergeChunk chunk = document.Chunks[c];
            if (chunk.Kind == MergeChunkKind.Stable)
            {
                chunkStarts[c] = text.Length;
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

        // The automatic merges are anchored too, for one reason: the conflicts above
        // them change length every time a side is taken, and a mark kept as a line
        // number would slide off the text it is describing on the first click.
        IReadOnlyList<string?> ancestors = RecoverAutoBases(document);

        for (int a = 0; a < document.AutoMerges.Count; a++)
        {
            AutoMerge auto = document.AutoMerges[a];
            if (!chunkStarts.TryGetValue(auto.ChunkIndex, out int at))
            {
                continue;
            }

            IReadOnlyList<string> lines = document.Chunks[auto.ChunkIndex].Text;
            int start = at;
            for (int i = 0; i < auto.LineOffset && i < lines.Count; i++)
            {
                start += lines[i].Length + 1;
            }

            // The merged text is rebuilt from the very lines the offsets are summed
            // from, so "what git put here" and "what lies between the anchors" cannot
            // drift apart — which is what the whole restore path rests on.
            System.Text.StringBuilder produced = new();
            int end = start;
            for (int i = auto.LineOffset; i < auto.LineOffset + auto.LineCount && i < lines.Count; i++)
            {
                produced.Append(lines[i]).Append('\n');
                end += lines[i].Length + 1;
            }

            // Same movement rules as a conflict's anchors, and for the same reason:
            // taking a side deletes the span and inserts in its place, and only
            // BeforeInsertion/AfterInsertion keep the span wrapped around the new
            // text instead of collapsing behind it. The price is that text typed
            // exactly at a deletion mark joins the span — which is then reported as
            // an edit rather than as git's work, so nothing is claimed falsely.
            TextAnchor from = doc.CreateAnchor(Math.Min(start, doc.TextLength));
            from.SurviveDeletion = true;
            from.MovementType = AnchorMovementType.BeforeInsertion;
            TextAnchor to = doc.CreateAnchor(Math.Min(end, doc.TextLength));
            to.SurviveDeletion = true;
            to.MovementType = AnchorMovementType.AfterInsertion;

            _autos.Add(new AutoSpan(
                _autos.Count + 1, auto, from, to, produced.ToString(), a < ancestors.Count ? ancestors[a] : null));
        }
    }

    /// <summary>
    ///  The ancestor lines each automatic merge replaced — the one thing
    ///  <see cref="AutoMerge"/> does not carry and the one thing an override needs.
    ///
    ///  <para><b>Why it is recomputed here.</b> Knowing BASE is enough to know all
    ///  three versions of an automatic merge: git only merged it silently because one
    ///  side left the ancestor alone, so the quiet side's version <i>is</i> the
    ///  ancestor and the loud side's is what is on screen. The service found these
    ///  changes by diffing the ancestor against the merged text with the conflict
    ///  blocks put back to the ancestor; the same comparison is repeated here and each
    ///  automatic merge is matched to the stretch it came from by the position of its
    ///  first produced line. Repeating one diff is cheaper and far safer than
    ///  reconstructing the ancestor by adding up line counts, where a single stretch
    ///  the service dropped would silently shift every ancestor after it — and an
    ///  override built on the wrong ancestor is a merge tool writing text nobody
    ///  wrote.</para>
    ///
    ///  <para>Anything that cannot be matched gets <see langword="null"/> and that
    ///  span simply stays read-only, as it was before this existed.</para>
    /// </summary>
    private static IReadOnlyList<string?> RecoverAutoBases(MergeDocument document)
    {
        if (document.AutoMerges.Count == 0)
        {
            return [];
        }

        List<string> view = [];
        Dictionary<(int Chunk, int Line), int> where = [];

        for (int c = 0; c < document.Chunks.Count; c++)
        {
            MergeChunk chunk = document.Chunks[c];
            bool stable = chunk.Kind == MergeChunkKind.Stable;
            IReadOnlyList<string> lines = stable ? chunk.Text : chunk.Base;

            for (int i = 0; i < lines.Count; i++)
            {
                if (stable)
                {
                    where[(c, i)] = view.Count;
                }

                view.Add(lines[i]);
            }

            // A deletion at the very end of a chunk is reported one line past its
            // last, which is a position and not a line: it still has to be findable.
            if (stable)
            {
                where[(c, lines.Count)] = view.Count;
            }
        }

        IReadOnlyList<LineDiff.Hunk>? hunks = LineDiff.Diff(document.BaseLines, view);
        List<string?> answer = [];

        foreach (AutoMerge auto in document.AutoMerges)
        {
            answer.Add(
                hunks is not null
                && where.TryGetValue((auto.ChunkIndex, auto.LineOffset), out int at)
                && Origin(hunks, at, auto.LineCount) is LineDiff.Hunk hunk
                    ? Ancestor(document.BaseLines, hunk)
                    : null);
        }

        return answer;

        static LineDiff.Hunk? Origin(IReadOnlyList<LineDiff.Hunk> hunks, int at, int produced)
        {
            foreach (LineDiff.Hunk hunk in hunks)
            {
                // A stretch that produced lines is found by containment; a deletion
                // produced none, so it is found by the boundary it sits on.
                bool match = produced > 0
                    ? at >= hunk.RightStart && at < hunk.RightEnd
                    : at == hunk.RightStart && hunk.RightEnd == hunk.RightStart;
                if (match)
                {
                    return hunk;
                }
            }

            return null;
        }

        static string Ancestor(IReadOnlyList<string> lines, LineDiff.Hunk hunk)
        {
            System.Text.StringBuilder text = new();
            for (int i = hunk.LeftStart; i < hunk.LeftEnd && i < lines.Count; i++)
            {
                text.Append(lines[i]).Append('\n');
            }

            return text.ToString();
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
        Rewrite(region.Start, region.End, text);
        Refresh();
        Reveal(region);
    }

    /// <summary>
    ///  Puts <paramref name="choice"/> into an automatic merge, or git's own answer
    ///  back. It is <see cref="Rewrite"/> and nothing else — the same call a conflict
    ///  makes — so an overridden region is still just text between two anchors and
    ///  the way back is another rewrite, not an undo stack.
    /// </summary>
    private void ApplyAuto(AutoSpan span, AutoChoice choice)
    {
        if (span.TextFor(choice) is not string text)
        {
            return;
        }

        Rewrite(span.Start, span.End, text);

        // The region acted on becomes the one the review round and the note describe:
        // a change whose effect is announced somewhere the user is not looking is a
        // change they have to verify by hand.
        _currentAuto = _autos.IndexOf(span);
        Refresh();
        _result.ScrollToLine(_result.Document.GetLineByOffset(span.Start.Offset).LineNumber);
    }

    private void Rewrite(TextAnchor from, TextAnchor to, string text)
    {
        TextDocument doc = _result.Document;
        int start = from.Offset;
        int length = Math.Max(to.Offset - start, 0);

        _updating = true;
        try
        {
            doc.Replace(start, length, text);
        }
        finally
        {
            _updating = false;
        }
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

        // Read back the same way, for the same reason: an automatic merge that has
        // been overridden — or typed over — must say so, and the only witness that
        // cannot go stale is the text itself.
        foreach (AutoSpan span in _autos)
        {
            int start = span.Start.Offset;
            int length = Math.Max(span.End.Offset - start, 0);
            span.State = span.Derive(doc.GetText(start, length));
        }

        _strays = CountStrayMarkers(doc);

        // After the choices have been re-derived and never before: the marks are laid
        // out from what each region is now holding.
        BuildResultMarks();

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
    ///  Walks the regions git merged by itself. It <b>changes nothing</b>: the text
    ///  is not touched, no choice is offered, the caret is not even moved — the only
    ///  effect is that the region is brought into view and named. Reviewing an
    ///  automatic merge is the one thing a merge tool never invites you to do, and a
    ///  wrong automatic merge is worse than a wrong conflict precisely because it is
    ///  the one nobody reads.
    /// </summary>
    private void GoToAuto(int index)
    {
        if (_autos.Count == 0)
        {
            return;
        }

        // Wrapping, unlike the conflict arrows: this is a review round, and a round
        // that stops at the ends makes the user click back through the whole file to
        // see the first one again.
        _currentAuto = ((index % _autos.Count) + _autos.Count) % _autos.Count;

        AutoSpan span = _autos[_currentAuto];
        _result.ScrollToLine(_result.Document.GetLineByOffset(span.Start.Offset).LineNumber);
        UpdateCounter();
        _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        InvalidateMargin();
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

        // Clicking inside an automatic merge names it in the summary line, so the
        // question "what is this line doing here?" is answerable by clicking on it
        // and not only by walking the review round.
        _currentAuto = _autos.FindIndex(a => offset >= a.Start.Offset && offset < Math.Max(a.End.Offset, a.Start.Offset + 1));

        int found = _regions.FindIndex(r => offset >= r.Start.Offset && offset <= r.End.Offset);
        if (found >= 0 && found != _current)
        {
            _current = found;
            Reveal(_regions[found]);
        }
        else
        {
            UpdateCounter();
            InvalidateMargin();
            _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
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

    /// <summary>
    ///  Rebuilds the intra-line marks of the <b>result</b> pane, for every region at
    ///  once.
    ///
    ///  <para><b>Why the pane that decides gets marks too.</b> It was left out with
    ///  the argument that inside a marker block the two sides sit one above the other,
    ///  so the same difference would be drawn twice. That is true and it is not a
    ///  reason: the user reads the block top to bottom precisely to see where the two
    ///  disagree, and drawing the answer on both halves is what makes it findable
    ///  without counting characters. The reference panes above show the two versions
    ///  <i>in their files</i>; this pane shows what the merged file will contain, and
    ///  that is where the eye is when the button is pressed.</para>
    ///
    ///  <para><b>What is marked is where LOCAL's and REMOTE's lines currently are</b>,
    ///  whatever put them there. In an open conflict both blocks are present, so the
    ///  two halves of the block are marked against each other; a region that has been
    ///  answered with one side holds only that side's lines, and they are marked
    ///  against what the combo names. So the combo keeps ONE meaning across the four
    ///  panes — "compare with the other side" or "compare with the ancestor" — instead
    ///  of quietly changing what a colour means depending on which pane it is in.
    ///  Reading a decided region against the side that was dropped is also the check
    ///  the user actually wants there: what did taking LOCAL throw away?</para>
    ///
    ///  <para><b>What is deliberately left unmarked.</b> A region holding BASE, and a
    ///  region typed over by hand. BASE is the thing both readings measure against, so
    ///  marking it would mean showing two answers at once in one place — the same
    ///  argument that keeps the BASE pane clean. Hand-typed text belongs to nobody: any
    ///  pairing of it with a version would be a guess, and a guess redrawn on every
    ///  keystroke is worse than silence. The block of BASE lines inside an open marker
    ///  block is left alone for the first of those reasons.</para>
    ///
    ///  <para>Runs from <see cref="Refresh"/>, i.e. after every change of the text:
    ///  the marks are keyed by document line number, so a rebuild is not an
    ///  optimisation but a correctness requirement — taking a side four lines up moves
    ///  every line below it. Only the pairing is rebuilt; the character diffs stay
    ///  lazy and are run by the renderer for the lines on screen.</para>
    /// </summary>
    private void BuildResultMarks()
    {
        InlineOverlay ours = _resultHighlighter.OursMarks;
        InlineOverlay theirs = _resultHighlighter.TheirsMarks;

        ours.Clear();
        theirs.Clear();

        int mode = _inlineMode.SelectedIndex;
        if (mode is not (SidesMode or BaseMode) || _regions.Count == 0)
        {
            return;
        }

        // Same inks as the panes above, so a mark means the same thing wherever it is
        // read: amber for "the two sides disagree here", each side's own colour for
        // "this is what that side changed".
        ours.SetInk(mode == SidesMode ? SidesInk : OursInk);
        theirs.SetInk(mode == SidesMode ? SidesInk : TheirsInk);

        TextDocument doc = _result.Document;

        foreach (Region region in _regions)
        {
            (int? oursAt, int? baseAt, int? theirsAt) = Blocks(doc, region);
            MergeChunk chunk = region.Chunk;

            if (mode == SidesMode)
            {
                // The counterpart may not be in the document at all — that is the
                // normal case for an answered region — and it does not need to be:
                // the region carries all three versions, and the side that is missing
                // from the text is exactly the one the mark is measuring against.
                Pair(
                    chunk.Ours, Block(oursAt, chunk.Ours.Count), oursAt is null ? null : ours,
                    chunk.Theirs, Block(theirsAt, chunk.Theirs.Count), theirsAt is null ? null : theirs);
                continue;
            }

            Pair(
                chunk.Base, Block(baseAt, chunk.Base.Count), null,
                chunk.Ours, Block(oursAt, chunk.Ours.Count), oursAt is null ? null : ours);
            Pair(
                chunk.Base, Block(baseAt, chunk.Base.Count), null,
                chunk.Theirs, Block(theirsAt, chunk.Theirs.Count), theirsAt is null ? null : theirs);
        }

        // A range whose start is unknown still has to have the right LENGTH: Pair
        // refuses to mark a side whose lines it could not locate, and a version that
        // is merely absent from the result is located perfectly well — in the chunk.
        static LineRange Block(int? start, int count) => new(start ?? 1, count);
    }

    /// <summary>
    ///  Where, in the result document, the three versions of <paramref name="region"/>
    ///  currently are — <see langword="null"/> for a version that is not there at all,
    ///  or that is empty and so occupies no line.
    ///
    ///  <para>Computed from the line counts of the versions and not by searching the
    ///  text, which is sound because the text between the anchors is one of the strings
    ///  <see cref="TextFor"/> builds: <see cref="Refresh"/> has just re-derived the
    ///  choice by comparing them character for character, so the arithmetic below
    ///  cannot describe a layout the document does not have. The one case where it
    ///  could — <see cref="MergeChoice.Custom"/>, where the text is whatever was typed
    ///  — is answered with three nulls.</para>
    /// </summary>
    private static (int? Ours, int? Base, int? Theirs) Blocks(TextDocument doc, Region region)
    {
        int first = doc.GetLineByOffset(region.Start.Offset).LineNumber;
        MergeChunk chunk = region.Chunk;

        return region.Choice switch
        {
            // The marker block, whose shape is the one RegionHighlighter washes: a
            // label line, our lines, a label line, the ancestor's, "=======", theirs,
            // and the closing label.
            MergeChoice.Conflict => (
                At(first + 1, chunk.Ours.Count),
                At(first + 2 + chunk.Ours.Count, chunk.Base.Count),
                At(first + 3 + chunk.Ours.Count + chunk.Base.Count, chunk.Theirs.Count)),
            MergeChoice.Ours => (At(first, chunk.Ours.Count), null, null),
            MergeChoice.Theirs => (null, null, At(first, chunk.Theirs.Count)),
            MergeChoice.Base => (null, At(first, chunk.Base.Count), null),
            MergeChoice.OursThenTheirs => (
                At(first, chunk.Ours.Count), null, At(first + chunk.Ours.Count, chunk.Theirs.Count)),
            MergeChoice.TheirsThenOurs => (
                At(first + chunk.Theirs.Count, chunk.Ours.Count), null, At(first, chunk.Theirs.Count)),
            _ => (null, null, null),
        };

        static int? At(int line, int count) => count > 0 ? line : null;
    }

    private const int SidesMode = 0;
    private const int BaseMode = 1;
    private const int OffMode = 2;

    /// <summary>
    ///  Where the chosen reading is kept between sessions — the same file, and the
    ///  same service, that already carry the diff viewer's own intra-line switch
    ///  (<see cref="DiffViewerOptions.InlineDiff"/>). A window-local copy would have
    ///  been half the work and none of the benefit: this window is opened once per
    ///  conflicting FILE, so "per session" here means "until the next file", which is
    ///  precisely when the user would have to set it again.
    /// </summary>
    private static readonly ViewPrefsService Prefs = new();

    /// <summary>
    ///  The reading the file names, or LOCAL ↔ REMOTE when it names none — the
    ///  default this window has always opened in, unchanged.
    /// </summary>
    private static int RestoredInlineMode() => Prefs.Load().Merge.InlineMode switch
    {
        "Base" => BaseMode,
        "Off" => OffMode,
        _ => SidesMode,
    };

    /// <summary>
    ///  Writes the reading back. Through <see cref="ViewPrefsService.Update"/> and not
    ///  <c>Save</c>, for the reason that method exists: this window is modal over a
    ///  main window whose other surfaces write the same file.
    /// </summary>
    private void PersistInlineMode()
    {
        string mode = _inlineMode.SelectedIndex switch
        {
            BaseMode => "Base",
            OffMode => "Off",
            _ => "Sides",
        };

        Prefs.Update(prefs => prefs.Merge.InlineMode = mode);
    }

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
        UpdateSummary(undecided);

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
    ///  The sentence that answers, on open and for as long as the window is up, the
    ///  question kdiff3 answers with a dialog: how many changes there were, how many
    ///  git settled by itself, how many are left.
    ///
    ///  <para><b>Why it is worth a line of the window.</b> <c>git merge-file</c> does
    ///  most of the work and shows none of it: it fuses everything that does not
    ///  clash and leaves only the wreckage on screen. A user arriving from kdiff3
    ///  reads that silence as "the tool did nothing", and — worse — never learns that
    ///  six decisions were taken in their name.</para>
    ///
    ///  <para>The automatic figures are facts about git's output and never change;
    ///  what is left to decide is recomputed from the regions on every keystroke, so
    ///  the line stays true while the user works instead of describing the file as it
    ///  was when it opened.</para>
    /// </summary>
    private void UpdateSummary(int undecided)
    {
        int conflicts = _regions.Count;

        _previousAuto.IsEnabled = _autos.Count > 0;
        _nextAuto.IsEnabled = _autos.Count > 0;

        if (!_doc.AutoMergeKnown)
        {
            // Saying "0 merged automatically" here would be a lie of the worst kind:
            // a precise one. The versions were too far apart to recover the work git
            // did, and that is what gets said.
            _summary.Text = string.Format(
                CultureInfo.CurrentCulture,
                T("These versions differ too widely to say how much git merged on its own — {0} conflict(s) "
                    + "are left for you."),
                conflicts);
            _autoNote.Text = string.Empty;
            return;
        }

        int autos = _autos.Count;
        int total = autos + conflicts;
        int trivial = _regions.Count(r => r.Choice == MergeChoice.Conflict && r.Proposal is not null);

        string line = string.Format(
            CultureInfo.CurrentCulture,
            T("{0} change(s) here — {1} merged automatically by git, {2} left for you to decide"),
            total,
            autos,
            undecided);

        if (autos > 0)
        {
            line += " (" + string.Join(
                T(", "),
                Breakdown(AutoMergeSide.Local, T("{0} from LOCAL"))
                    .Concat(Breakdown(AutoMergeSide.Remote, T("{0} from REMOTE")))
                    .Concat(Breakdown(AutoMergeSide.Both, T("{0} both sides made the same change")))) + ")";
        }

        // Counted separately from the breakdown above, which describes what git did
        // and must keep describing it: this says what has been done about it since.
        int overridden = _autos.Count(a => a.Overridden);
        if (overridden > 0)
        {
            line += string.Format(
                CultureInfo.CurrentCulture,
                T(" — {0} of them overridden by hand"),
                overridden);
        }

        if (trivial > 0)
        {
            line += string.Format(
                CultureInfo.CurrentCulture,
                T(" — {0} of those need no thought: press \"Resolve trivial\""),
                trivial);
        }

        _summary.Text = line;

        _autoNote.Text = _currentAuto >= 0 && _currentAuto < _autos.Count
            ? string.Format(
                CultureInfo.CurrentCulture,
                T("Merged automatically {0} of {1} — {2}"),
                _currentAuto + 1,
                autos,
                _autos[_currentAuto].Description)
            : string.Empty;

        IEnumerable<string> Breakdown(AutoMergeSide side, string format)
        {
            int count = _autos.Count(a => a.Merge.Side == side);
            return count == 0
                ? []
                : [string.Format(CultureInfo.CurrentCulture, format, count)];
        }
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

    // ------------------------------------------------------------ context menus

    /// <summary>
    ///  Puts the choice where the conflict is.
    ///
    ///  <para><b>Why the toolbar was not enough.</b> Reported from use, and the
    ///  comparison was with kdiff3: there, the side is picked by right-clicking the
    ///  block itself. Here it could only be picked at the top of the window, which
    ///  means looking away from the text to answer a question about the text, and
    ///  then looking back to check that the thing that changed was the thing you were
    ///  reading. With more than one conflict on screen that check is not a formality.
    ///  </para>
    ///
    ///  <para><b>The menu acts on the line under the pointer</b>, never on the
    ///  "current" conflict the toolbar acts on. If the user right-clicks a conflict,
    ///  they mean that conflict — a menu that silently retargeted to the selected one
    ///  would be a way to answer the wrong question with a confident click. The
    ///  region is named in the menu's first line for the same reason: the target is
    ///  stated before it is acted on, not inferred from what moves afterwards. Acting
    ///  then <i>makes</i> that region current, so the toolbar and the margin agree
    ///  with what just happened.</para>
    ///
    ///  <para><b>It reports as much as it offers.</b> Each side is a radio item and
    ///  the one the region is holding is the one marked, so the menu answers "where
    ///  am I?" before "where can I go?" — a list of five identical-looking commands,
    ///  one of which is already in force, is a list that has to be verified against
    ///  the margin before it can be used.</para>
    /// </summary>
    private void AttachMenus()
    {
        AttachResultMenu();
        AttachReferenceMenu(_oursPane, MergeChoice.Ours, T("LOCAL"), s => s.Ours);
        AttachReferenceMenu(_basePane, MergeChoice.Base, T("BASE"), s => s.Base);
        AttachReferenceMenu(_theirsPane, MergeChoice.Theirs, T("REMOTE"), s => s.Theirs);
    }

    private void AttachResultMenu()
    {
        // Two captions and not one sentence that grows: the popup measures itself
        // the first time it opens and keeps that width, so a caption that gets
        // longer when a region is overridden would have its ending clipped —
        // silently, and exactly on the words that say what changed. The first line
        // is fixed for as long as the menu points at the same region; the second
        // appears when there is something to add and is always shorter.
        MenuItem header = Caption(CaptionWidth);
        MenuItem state = Caption(0);
        MenuItem ours = Pick(T("Take LOCAL"), () => ChooseHere(MergeChoice.Ours));
        MenuItem @base = Pick(T("Take BASE"), () => ChooseHere(MergeChoice.Base));
        MenuItem theirs = Pick(T("Take REMOTE"), () => ChooseHere(MergeChoice.Theirs));
        MenuItem oursThenTheirs = Pick(T("Both: LOCAL → REMOTE"), () => ChooseHere(MergeChoice.OursThenTheirs));
        MenuItem theirsThenOurs = Pick(T("Both: REMOTE → LOCAL"), () => ChooseHere(MergeChoice.TheirsThenOurs));

        // Two separate ways back because they undo two different things: a conflict
        // returns to the marker block nobody has answered, while an automatic merge
        // returns to an answer git already gave. One item saying "restore" would have
        // to mean both, and the user would not know which they were getting.
        MenuItem restore = Pick(T("Restore conflict"), () =>
        {
            if (_menuRegion is Region region)
            {
                ApplyTo(region, MergeChoice.Conflict);
            }
        });
        MenuItem restoreAuto = Pick(T("Restore git's merge"), () =>
        {
            if (_menuAuto is AutoSpan span)
            {
                ApplyAuto(span, AutoChoice.Git);
            }
        });

        Separator top = new();
        Separator bottom = new();

        // Cut/copy/paste stay, and stay at the bottom: the result pane is a real
        // editor and the editing commands apply to every line of it, including the
        // lines the merge commands have nothing to say about. The merge items go
        // above them because they are why the menu was opened.
        MenuItem cut = Command(T("Cut"), () => _result.Cut());
        MenuItem copy = Command(T("Copy"), () => _result.Copy());
        MenuItem paste = Command(T("Paste"), () => _result.Paste());

        ContextMenu menu = new()
        {
            ItemsSource = new Control[]
            {
                header, state, top,
                ours, @base, theirs, oursThenTheirs, theirsThenOurs, restore, restoreAuto,
                bottom, cut, copy, paste,
            },
        };

        menu.Opening += (_, _) =>
        {
            Target();

            bool conflict = _menuRegion is not null;
            bool auto = _menuAuto is not null;

            // Hidden and not greyed when there is nothing under the pointer: a
            // command that cannot run is noise, and five of them in a row read as a
            // broken menu rather than as an answer. What is left still works — see
            // the caption, which says why the merge items are gone.
            ours.IsVisible = conflict || Distinct(AutoChoice.Ours);
            @base.IsVisible = conflict || Distinct(AutoChoice.Base);
            theirs.IsVisible = conflict || Distinct(AutoChoice.Theirs);
            bool sides = ours.IsVisible || @base.IsVisible || theirs.IsVisible;
            oursThenTheirs.IsVisible = conflict;
            theirsThenOurs.IsVisible = conflict;
            restore.IsVisible = conflict;
            restoreAuto.IsVisible = auto;
            top.IsVisible = sides || conflict || auto;

            state.IsVisible = false;

            if (_menuRegion is Region region)
            {
                header.Header = string.Format(
                    CultureInfo.CurrentCulture,
                    T("Conflict {0} of {1} — now {2}"),
                    region.Id,
                    _regions.Count,
                    Describe(region.Choice));

                if (region.Chunk.Trivial != TrivialKind.None)
                {
                    // The same argument the margin and the counter already make,
                    // repeated where the decision is about to be taken.
                    state.IsVisible = true;
                    state.Header = TrivialText.Sentence(region.Chunk.Trivial);
                }

                Mark(ours, region.Choice == MergeChoice.Ours);
                Mark(@base, region.Choice == MergeChoice.Base);
                Mark(theirs, region.Choice == MergeChoice.Theirs);
                Mark(oursThenTheirs, region.Choice == MergeChoice.OursThenTheirs);
                Mark(theirsThenOurs, region.Choice == MergeChoice.TheirsThenOurs);
                Mark(restore, region.Choice == MergeChoice.Conflict);
            }
            else if (_menuAuto is AutoSpan span)
            {
                // What git did, and only that: the line counts stay in the toolbar's
                // note, which names the same region as soon as one of these items is
                // used. A caption is a label on a target, not a report.
                header.Header = string.Format(
                    CultureInfo.CurrentCulture,
                    T("Merged by git {0} of {1} — {2}"),
                    span.Number,
                    _autos.Count,
                    span.Merge.Side switch
                    {
                        AutoMergeSide.Local => T("taken from LOCAL"),
                        AutoMergeSide.Remote => T("taken from REMOTE"),
                        _ => T("both sides agreed"),
                    });

                Mark(ours, span.State == AutoChoice.Ours);
                Mark(@base, span.State == AutoChoice.Base);
                Mark(theirs, span.State == AutoChoice.Theirs);
                Mark(restoreAuto, span.State == AutoChoice.Git);

                if (!span.CanOverride)
                {
                    // Said, not hidden in silence: the user can see the region is
                    // marked AUTO and would otherwise read the missing commands as a
                    // bug rather than as the honest limit they are.
                    state.IsVisible = true;
                    state.Header = T("Ancestor unknown: no side can be taken here");
                }
                else if (span.State != AutoChoice.Git)
                {
                    state.IsVisible = true;
                    state.Header = span.State switch
                    {
                        AutoChoice.Ours => T("Overridden by hand: now LOCAL"),
                        AutoChoice.Theirs => T("Overridden by hand: now REMOTE"),
                        AutoChoice.Base => T("Overridden: git's change undone"),
                        _ => T("Edited by hand"),
                    };
                }
            }
            else
            {
                header.Header = T("No conflict and no automatic merge on this line");
            }

            cut.IsEnabled = _result.SelectionLength > 0;
            copy.IsEnabled = _result.SelectionLength > 0;
        };

        _result.TextArea.ContextMenu = menu;
        WatchRightClicks(_result);

        // Only the sides that would really put something else here.
        //
        // An automatic merge has three versions but rarely three answers: git merged
        // it silently because one side left the ancestor alone, so that side's text
        // IS the ancestor, and "take LOCAL" and "take BASE" would be two names for
        // one command. Offering both is worse than useless — the state is read back
        // out of the text, so whichever the user picked, the margin would name the
        // other one half the time and look like a bug. What is dropped is a duplicate
        // label, never a reachable text.
        bool Distinct(AutoChoice choice)
        {
            if (_menuAuto is not AutoSpan span || !span.CanOverride || span.TextFor(choice) is not string text
                || text == span.GitText)
            {
                return false;
            }

            // Ordered, so that of two identical options the first one wins and the
            // decision does not depend on which item is being asked about.
            foreach (AutoChoice other in new[] { AutoChoice.Ours, AutoChoice.Theirs, AutoChoice.Base })
            {
                if (other == choice)
                {
                    return true;
                }

                if (span.TextFor(other) == text)
                {
                    return false;
                }
            }

            return true;
        }

        // Resolving the target here and not in the click handlers: every item then
        // acts on the same region the caption named, whatever the document did in
        // between, and there is one place where "under the pointer" is defined.
        void Target()
        {
            _menuRegion = null;
            _menuAuto = null;
            if (_menuOffset < 0)
            {
                return;
            }

            _menuRegion = _regions.FirstOrDefault(r => Covers(r.Start, r.End, _menuOffset));

            // A conflict wins a tie. The two cannot overlap as the file is loaded —
            // automatic merges live in the stable text between conflicts — but hand
            // editing can bring the anchors together, and what is still to be decided
            // outranks what is already settled.
            if (_menuRegion is null)
            {
                _menuAuto = _autos.FirstOrDefault(a => Covers(a.Start, a.End, _menuOffset));
            }
        }
    }

    /// <summary>
    ///  The menu of one read-only pane: "I can see the version I want, take it".
    ///
    ///  <para>The most direct gesture there is, and the one kdiff3 users reach for
    ///  first. It offers exactly one command, because a reference pane can say only
    ///  one thing about a conflict — this side — and offering the other two here
    ///  would make the pane the user clicked in irrelevant to what happens.</para>
    /// </summary>
    private void AttachReferenceMenu(
        TextEditor editor,
        MergeChoice side,
        string name,
        Func<(LineRange Ours, LineRange Base, LineRange Theirs), LineRange> pick)
    {
        Region? target = null;

        MenuItem header = Caption(CaptionWidth);
        MenuItem take = Pick(string.Empty, () =>
        {
            if (target is Region region)
            {
                ApplyTo(region, side);
            }
        });

        Separator rule = new();
        MenuItem copy = Command(T("Copy"), editor.Copy);

        ContextMenu menu = new()
        {
            ItemsSource = new Control[] { header, new Separator(), take, rule, copy },
        };

        menu.Opening += (_, _) =>
        {
            target = Locate();

            take.IsVisible = target is not null;
            rule.IsVisible = target is not null;

            if (target is Region region)
            {
                header.Header = string.Format(
                    CultureInfo.CurrentCulture,
                    T("{0} — the version of conflict {1}, now {2}"),
                    name,
                    region.Id,
                    Describe(region.Choice));
                take.Header = string.Format(
                    CultureInfo.CurrentCulture, T("Take {0} for conflict {1}"), name, region.Id);
                Mark(take, region.Choice == side);
            }
            else
            {
                header.Header = string.Format(
                    CultureInfo.CurrentCulture,
                    T("{0} — this line belongs to no conflict"),
                    name);
            }

            copy.IsEnabled = editor.SelectionLength > 0;
        };

        editor.TextArea.ContextMenu = menu;
        WatchRightClicks(editor);

        Region? Locate()
        {
            if (_menuOffset < 0 || _menuOffset > editor.Document.TextLength)
            {
                return null;
            }

            int line = editor.Document.GetLineByOffset(_menuOffset).LineNumber;
            foreach (Region region in _regions)
            {
                if (_sources.TryGetValue(region.Id, out (LineRange Ours, LineRange Base, LineRange Theirs) source)
                    && pick(source) is { Length: > 0 } range
                    && line >= range.Start && line < range.End)
                {
                    return region;
                }
            }

            return null;
        }
    }

    /// <summary>
    ///  Remembers which line a right-click landed on.
    ///
    ///  <para>Tunnelling, and on the text area: the position has to be taken on the
    ///  way down, before the editor has decided anything about the click and before
    ///  the menu opens. The caret is not consulted — it is where the user <i>was</i>,
    ///  and the whole point of this menu is that it acts where they are pointing.</para>
    /// </summary>
    private void WatchRightClicks(TextEditor editor)
        => editor.TextArea.AddHandler(
            PointerPressedEvent,
            (_, e) =>
            {
                if (e.GetCurrentPoint(editor.TextArea.TextView).Properties.PointerUpdateKind
                    == PointerUpdateKind.RightButtonPressed)
                {
                    _menuOffset = LineOffsetAt(editor, e.GetPosition(editor.TextArea.TextView));
                }
            },
            RoutingStrategies.Tunnel);

    /// <summary>
    ///  The first offset of the line under <paramref name="point"/>, or -1 below the
    ///  last line — where the nearest line is a guess and the honest answer is that
    ///  the click was on nothing.
    /// </summary>
    private static int LineOffsetAt(TextEditor editor, Point point)
    {
        TextView view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        return view.GetVisualLineFromVisualTop(point.Y + view.VerticalOffset) is VisualLine line
            ? line.FirstDocumentLine.Offset
            : -1;
    }

    /// <summary>
    ///  Whether <paramref name="offset"/> is inside a span. The end anchor sits on
    ///  the first character of the following line, so it is excluded; a span holding
    ///  nothing at all still owns the one position it collapsed onto, which is what
    ///  makes a deleted stretch right-clickable.
    /// </summary>
    private static bool Covers(TextAnchor from, TextAnchor to, int offset)
        => offset >= from.Offset && offset < Math.Max(to.Offset, from.Offset + 1);

    private void ChooseHere(MergeChoice choice)
    {
        if (_menuRegion is Region region)
        {
            ApplyTo(region, choice);
        }
        else if (_menuAuto is AutoSpan span)
        {
            ApplyAuto(span, choice switch
            {
                MergeChoice.Ours => AutoChoice.Ours,
                MergeChoice.Theirs => AutoChoice.Theirs,
                MergeChoice.Base => AutoChoice.Base,
                _ => AutoChoice.Git,
            });
        }
    }

    /// <summary>
    ///  Applies a choice to a named conflict, whichever pane the user asked from.
    ///
    ///  <para>No toggling, unlike the toolbar button: there, pressing the pressed
    ///  button is the way back. Here the state is shown as a radio and a radio that
    ///  undoes itself on a second click is not a radio — "Restore conflict" is one
    ///  item below and says what it does.</para>
    /// </summary>
    private void ApplyTo(Region region, MergeChoice choice)
    {
        int index = _regions.IndexOf(region);
        if (index >= 0)
        {
            _current = index;
        }

        Replace(region, TextFor(region, choice));
    }

    /// <summary>
    ///  Room reserved for the caption, so the popup is measured once at a width
    ///  every caption fits in. Kept under the theme's cap on flyout width — past
    ///  that cap a caption is not wrapped or ellipsised, it is simply cut off, and
    ///  the words that get cut are the last ones, which is where the state is said.
    /// </summary>
    private const double CaptionWidth = 400;

    /// <summary>The menu's first line: what is about to be acted on.</summary>
    private static MenuItem Caption(double width) => new() { IsEnabled = false, MinWidth = width };

    private static MenuItem Pick(string caption, Action action)
    {
        MenuItem item = new() { Header = caption, ToggleType = MenuItemToggleType.Radio };

        // Click and not IsCheckedChanged, exactly as the toolbar buttons do it: the
        // mark is a REPORT of what the document holds, written when the menu opens,
        // and reacting to it changing would make that report look like a decision.
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Command(string caption, Action action)
    {
        MenuItem item = new() { Header = caption };
        item.Click += (_, _) => action();
        return item;
    }

    private static void Mark(MenuItem item, bool active) => item.IsChecked = active;

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
///  One region git merged by itself, pinned to the document the same way a conflict
///  is.
///
///  <para><b>It is not a conflict, but it is not sealed either.</b> Git took the
///  decision before the window opened and the resting state is that decision; what
///  the span adds is the ability to <i>see</i> it — in the margin, and by walking
///  through them — and to <i>disagree</i> with it. kdiff3 lets a side be picked on
///  any region, not only on the ones it could not settle, and it is right to: an
///  automatic merge is only "safe" because the other side did not touch those lines,
///  which is an argument about the text, not about the intent. So the three versions
///  travel with the span and any of them can be put in, exactly the way a conflict
///  works — including the way back, which here is git's own answer.</para>
///
///  <para><b>The document is still the truth</b>, as it is for a conflict: nothing
///  records what was picked. <see cref="Derive"/> re-reads it from the text between
///  the anchors, so a hand edit inside an automatic merge shows up as one rather
///  than being quietly reported as git's work.</para>
/// </summary>
/// <param name="gitText">What <c>git merge-file</c> put here, newline of the last line included.</param>
/// <param name="baseText">
///  The ancestor lines this change replaced, or <see langword="null"/> when they
///  could not be recovered — in which case the span stays read-only rather than
///  offering an override built on a guess.
/// </param>
internal sealed class AutoSpan(
    int number, AutoMerge merge, TextAnchor start, TextAnchor end, string gitText, string? baseText)
{
    /// <summary>1-based position in the review round.</summary>
    public int Number { get; } = number;

    /// <summary>What the service recovered about this change.</summary>
    public AutoMerge Merge { get; } = merge;

    /// <summary>Start of the merged text.</summary>
    public TextAnchor Start { get; } = start;

    /// <summary>End of the merged text; equal to <see cref="Start"/> for a deletion.</summary>
    public TextAnchor End { get; } = end;

    /// <summary>What git produced here, which is what "restore" means for this span.</summary>
    public string GitText { get; } = gitText;

    /// <summary>The common ancestor's version of these lines, when it is known.</summary>
    public string? BaseText { get; } = baseText;

    /// <summary>
    ///  Our version of these lines. The side that did <b>not</b> make the change
    ///  still has the ancestor here — that is precisely why git did not have to ask
    ///  — so the deduction costs nothing and invents nothing.
    /// </summary>
    public string? OursText => Merge.Side == AutoMergeSide.Remote ? BaseText : GitText;

    /// <summary>Their version of these lines, by the same argument.</summary>
    public string? TheirsText => Merge.Side == AutoMergeSide.Local ? BaseText : GitText;

    /// <summary>Whether a side can be taken here at all.</summary>
    public bool CanOverride => BaseText is not null;

    /// <summary>What the span holds now. Derived, never remembered.</summary>
    public AutoChoice State { get; set; } = AutoChoice.Git;

    /// <summary>Whether the user has put something else here than git did.</summary>
    public bool Overridden => State != AutoChoice.Git;

    /// <summary>The text <paramref name="choice"/> would put here, or <c>null</c> if unknown.</summary>
    public string? TextFor(AutoChoice choice) => choice switch
    {
        AutoChoice.Git => GitText,
        AutoChoice.Ours => OursText,
        AutoChoice.Theirs => TheirsText,
        AutoChoice.Base => BaseText,
        _ => null,
    };

    /// <summary>
    ///  Reads the state back out of <paramref name="text"/>. Git's own answer is
    ///  tested first on purpose: where one side changed nothing, "take that side"
    ///  and "keep git's merge" are the same characters, and the honest report is the
    ///  one that says the region is untouched.
    /// </summary>
    public AutoChoice Derive(string text)
    {
        if (text == GitText)
        {
            return AutoChoice.Git;
        }

        foreach (AutoChoice candidate in Overrides)
        {
            if (TextFor(candidate) is string option && text == option)
            {
                return candidate;
            }
        }

        return AutoChoice.Custom;
    }

    private static readonly AutoChoice[] Overrides = [AutoChoice.Ours, AutoChoice.Theirs, AutoChoice.Base];

    /// <summary>
    ///  What the margin says beside it. The arrow points the way the text travelled —
    ///  from a side into the result — because "AUTO LOCAL" would read like the name
    ///  of a button that can be pressed.
    ///
    ///  <para>An overridden span says OVERRIDE and not the side alone: without that
    ///  word the only trace that git had decided something else here would be gone,
    ///  and the region would be indistinguishable from ordinary merged text.</para>
    /// </summary>
    public string Label => State switch
    {
        AutoChoice.Ours => "OVERRIDE ← LOCAL",
        AutoChoice.Theirs => "OVERRIDE ← REMOTE",
        AutoChoice.Base => "OVERRIDE ← BASE",
        AutoChoice.Custom => "OVERRIDE ✎ EDIT",
        _ => Merge.Side switch
        {
            AutoMergeSide.Local => "AUTO ← LOCAL",
            AutoMergeSide.Remote => "AUTO ← REMOTE",
            _ => "AUTO = both",
        },
    };

    /// <summary>What git did here, in the words of the summary line.</summary>
    public string GitDescription
        => Merge.Side switch
        {
            AutoMergeSide.Local => T("taken from LOCAL"),
            AutoMergeSide.Remote => T("taken from REMOTE"),
            _ => T("both sides made this same change"),
        }
        + (Merge.LineCount == 0
            ? string.Format(CultureInfo.CurrentCulture, T(" — {0} line(s) removed here"), Merge.RemovedLines)
            : Merge.RemovedLines == 0
                ? string.Format(CultureInfo.CurrentCulture, T(" — {0} line(s) added"), Merge.LineCount)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    T(" — {0} line(s) replaced {1}"),
                    Merge.LineCount,
                    Merge.RemovedLines));

    /// <summary>The same fact, plus what the user has since done about it.</summary>
    public string Description => GitDescription + (State switch
    {
        AutoChoice.Ours => T(" — overridden by hand: now LOCAL"),
        AutoChoice.Theirs => T(" — overridden by hand: now REMOTE"),
        AutoChoice.Base => T(" — overridden by hand: git's change undone"),
        AutoChoice.Custom => T(" — edited by hand"),
        _ => string.Empty,
    });

    private static string T(string english) => TranslationService.T(english);
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

    /// <summary>
    ///  The wash for an automatic merge somebody has overridden, or <c>null</c> while
    ///  it still holds git's answer — which has an ink of its own, quieter than any
    ///  of these, because nobody has decided anything there.
    /// </summary>
    public static IBrush? Override(AutoChoice choice) => choice switch
    {
        AutoChoice.Ours => OursWash,
        AutoChoice.Theirs => TheirsWash,
        AutoChoice.Base => BaseWash,
        AutoChoice.Custom => CustomWash,
        _ => null,
    };

    /// <summary>The margin bar of an overridden automatic merge, or <c>null</c>.</summary>
    public static IBrush? OverrideBar(AutoChoice choice) => choice switch
    {
        AutoChoice.Ours => OursBar,
        AutoChoice.Theirs => TheirsBar,
        AutoChoice.Base => BaseBar,
        AutoChoice.Custom => CustomBar,
        _ => null,
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
internal sealed class RegionHighlighter(
    IReadOnlyList<Region> regions,
    Func<int> current,
    IReadOnlyList<AutoSpan> autos,
    Func<int> currentAuto) : IBackgroundRenderer
{
    private static readonly IBrush MarkerWash = new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0xA7, 0x3C));

    // One ink for every automatic merge whichever side it came from, and a faint
    // one. It has to be visible enough to say "this line is not the ancestor's" and
    // quiet enough not to look like a decision waiting to be taken; the side is
    // spelled out in the margin, where a word can say it without a colour code
    // nobody was taught.
    private static readonly IBrush AutoWash = new SolidColorBrush(Color.FromArgb(0x16, 0x9B, 0xB4, 0xC8));
    private static readonly IBrush AutoEdge = new SolidColorBrush(Color.FromRgb(0x6F, 0x9C, 0xB4));
    private static readonly IBrush OursWash = new SolidColorBrush(Color.FromArgb(0x24, 0x6A, 0xC7, 0x76));
    private static readonly IBrush BaseWash = new SolidColorBrush(Color.FromArgb(0x1C, 0x9B, 0x9B, 0x9B));
    private static readonly IBrush TheirsWash = new SolidColorBrush(Color.FromArgb(0x24, 0x5B, 0x9C, 0xFF));
    private static readonly IBrush CurrentEdge = new SolidColorBrush(Color.FromRgb(0x5B, 0x9C, 0xFF));

    /// <summary>
    ///  Intra-line marks on the lines the result currently takes from LOCAL, and on
    ///  the ones it takes from REMOTE.
    ///
    ///  <para>Two overlays and not one because the two speak in different inks in the
    ///  "each side ↔ BASE" reading, and one overlay carries one ink. They cannot
    ///  disagree about a line: no line of the result belongs to both sides.</para>
    /// </summary>
    public InlineOverlay OursMarks { get; } = new();

    /// <summary>The same, for the lines taken from REMOTE.</summary>
    public InlineOverlay TheirsMarks { get; } = new();

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.VisualLines.Count == 0 || textView.Document is not { } doc)
        {
            return;
        }

        textView.EnsureVisualLines();

        // Drawn before the conflicts so that a conflict's own wash always wins where
        // the two happen to meet: what is still to be decided outranks what is done.
        int activeAuto = currentAuto();
        for (int i = 0; i < autos.Count; i++)
        {
            AutoSpan span = autos[i];
            int firstLine = doc.GetLineByOffset(span.Start.Offset).LineNumber;
            int lastLine = doc.GetLineByOffset(Math.Max(span.End.Offset - 1, span.Start.Offset)).LineNumber;

            // A deletion produced no lines, so there is nothing to wash: only the
            // margin can say that text went away here, and it does.
            if (span.End.Offset > span.Start.Offset)
            {
                Fill(textView, drawingContext, firstLine, lastLine + 1,
                    ChoicePalette.Override(span.State) ?? AutoWash);
            }

            if (i == activeAuto)
            {
                foreach (Rect rect in Rects(textView, firstLine, lastLine + 1))
                {
                    drawingContext.FillRectangle(AutoEdge, new Rect(rect.X, rect.Y, 3, rect.Height));
                }
            }
        }

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

        // Last, so the marks sit on top of every wash they fall inside: a mark is
        // read against the line it is on, and a translucent wash painted over it
        // would take it back down towards the colour it is meant to stand out from.
        // Asked for every visible line and not only for the regions', because the
        // lookup is a dictionary miss for the stable text between them.
        foreach (VisualLine visual in textView.VisualLines)
        {
            int number = visual.FirstDocumentLine.LineNumber;
            OursMarks.Draw(textView, drawingContext, visual.FirstDocumentLine, number);
            TheirsMarks.Draw(textView, drawingContext, visual.FirstDocumentLine, number);
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
internal sealed class ChoiceMargin(IReadOnlyList<Region> regions, IReadOnlyList<AutoSpan> autos) : AbstractMargin
{
    private const double BarWidth = 4;
    private const double Gap = 4;

    // The widest note the margin can be asked to draw, used to reserve room. A
    // literal rather than a scan of the regions: the width must not change while
    // the user works, or the text would shift sideways as conflicts are answered.
    private const string WidestNote = " ✓one side unchanged";

    // The widest label, likewise. "OVERRIDE ← REMOTE" is longer than "CONFLICT", and
    // a margin measured for a shorter one would clip the very thing this feature
    // exists to show.
    private const string WidestLabel = "OVERRIDE ← REMOTE";

    // Same ink as the wash in the result pane, opaque here because a 4px bar in a
    // 9% grey would not be a bar. Deliberately outside the choice palette: an
    // automatic merge is not one of the answers a user can give.
    private static readonly IBrush AutoInk = new SolidColorBrush(Color.FromRgb(0x6F, 0x9C, 0xB4));

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
        return new Size(BarWidth + Gap + Format(WidestLabel).Width + Format(WidestNote).Width + Gap, 0);
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

        foreach (AutoSpan span in autos)
        {
            int from = doc.GetLineByOffset(span.Start.Offset).LineNumber;
            int to = doc.GetLineByOffset(Math.Max(span.End.Offset - 1, span.Start.Offset)).LineNumber;
            if (Extent(view, from, to) is not (double y, double height))
            {
                continue;
            }

            // A deletion covers no lines, so its bar is a tick on the boundary
            // instead of a stripe down a block: it marks a place, not a stretch.
            bool removal = span.End.Offset <= span.Start.Offset;
            IBrush ink = ChoicePalette.OverrideBar(span.State) ?? AutoInk;
            context.FillRectangle(ink, new Rect(0, y, BarWidth, removal ? 3 : height));

            FormattedText auto = Format(span.Label + (removal ? " −" + span.Merge.RemovedLines : string.Empty));
            auto.SetForegroundBrush(ink);
            context.DrawText(auto, new Point(BarWidth + Gap, y));
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

    /// <summary>
    ///  Where a run of document lines sits on screen, or <c>null</c> when none of it
    ///  is visible — the ordinary case for a file that does not fit in the window.
    /// </summary>
    private static (double Y, double Height)? Extent(TextView view, int first, int last)
    {
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

        return top is double start ? (start, bottom - start) : null;
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

    /// <summary>
    ///  Draws the marks of one document line, if it has any and if marking is on.
    ///
    ///  <para>Here rather than in each renderer because both of them need it and it is
    ///  the same drawing: the reference panes mark the version they are showing, the
    ///  result pane marks the version it is holding, and a mark that looked different
    ///  in the two would read as a different statement.</para>
    /// </summary>
    public void Draw(TextView textView, DrawingContext context, DocumentLine line, int number)
    {
        if (Fill is not IBrush fill)
        {
            return;
        }

        IReadOnlyList<InlineSpan> spans = Spans(number);
        if (spans.Count == 0)
        {
            return;
        }

        BackgroundGeometryBuilder builder = new() { AlignToWholePixels = true, CornerRadius = 2 };
        foreach (InlineSpan span in spans)
        {
            // Clamped rather than trusted: the spans are offsets into the string the
            // window paired this line with, and a line the user has typed into since
            // would otherwise be indexed past its end.
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
            context.DrawGeometry(fill, Edge, geometry);
        }
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
            Inline.Draw(textView, drawingContext, visual.FirstDocumentLine, number);
        }
    }
}
