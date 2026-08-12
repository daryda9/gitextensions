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
using AvaloniaEdit.Rendering;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The <b>built-in</b> three-way merge editor: the app's own answer to kdiff3 /
///  meld, so a Linux checkout is usable without installing anything.
///
///  <para><b>Original work.</b> Upstream Git Extensions has no such window — it
///  only ever shells out to <c>git mergetool</c>. This is therefore a feature of
///  the port, not a port of a feature, and the external tool stays exactly where
///  it was: <see cref="ResolveConflictsDialog"/> still offers "Open in &lt;tool&gt;"
///  and "Start mergetool" beside it.</para>
///
///  <para><b>Layout</b>: three read-only reference panes across the top — LOCAL,
///  BASE, REMOTE, the whole file each — and the editable merge result underneath,
///  which is the only pane that decides anything. That is the shape a three-way
///  merge actually has, and it is where the established three-way tools (kdiff3
///  among them) converged; the result pane is at the bottom rather than in the
///  middle because it is the one the user types in and the eye should end
///  there.</para>
///
///  <para><b>Conflict markers are the model.</b> The result document holds git's
///  own <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; / ||||||| / ======= / &gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>
///  blocks, and every button simply rewrites the block it is sitting on. Nothing
///  is tracked behind the text: the document IS the state. That is what lets
///  hand-editing and the buttons coexist without a reconciliation step — the user
///  can type freely, and the "N conflicts left" counter is re-derived by scanning
///  for markers after every keystroke. A model kept beside the text would have to
///  guess what an arbitrary edit meant to it; this one cannot be wrong.</para>
///
///  <para>The <c>#n</c> suffix this window writes into the opening marker is the
///  only addition to git's format, and it is what ties a block in the result back
///  to the region it came from in the three reference panes when the user steps
///  through the conflicts. It is removed with the rest of the block the moment the
///  conflict is settled, so it can never reach a commit.</para>
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

    private readonly ConflictHighlighter _resultHighlighter = new();
    private readonly RangeHighlighter _oursHighlighter = new();
    private readonly RangeHighlighter _baseHighlighter = new();
    private readonly RangeHighlighter _theirsHighlighter = new();

    private readonly TextBlock _counter;
    private readonly TextBlock _status;
    private readonly Button _save;

    // Where conflict #id sits in each of the three input files, 1-based inclusive
    // start and exclusive end. Computed once from the parsed chunks; the result
    // document can be edited freely without invalidating it, because these are
    // positions in the INPUTS, which never change.
    private readonly Dictionary<int, (LineRange Ours, LineRange Base, LineRange Theirs)> _sources = [];

    private List<MergeMarkerBlock> _conflicts = [];
    private int _current;
    private int _strays;
    private bool _scanning;

    /// <summary>True once the file has been written and staged.</summary>
    public bool Resolved { get; private set; }

    private MergeToolWindow(string repoPath, MergeDocument document)
    {
        _repoPath = repoPath;
        _doc = document;

        Title = T("Merge") + " — " + document.Path;
        Width = 1180;
        Height = 800;
        MinWidth = 720;
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

        _result = new TextEditor
        {
            FontFamily = AppFonts.Monospace,
            FontSize = AppFonts.MonospaceSize > 0 ? AppFonts.MonospaceSize : 13,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Background = Brush("App.Window", Brushes.Black),
            Padding = new Thickness(10, 8),
            ShowLineNumbers = true,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _result.Options.EnableHyperlinks = false;
        _result.Options.EnableEmailHyperlinks = false;
        _result.Options.AllowScrollBelowDocument = false;
        _result.TextArea.TextView.BackgroundRenderers.Add(_resultHighlighter);
        _result.Text = BuildResultText(document.Chunks);
        _result.TextChanged += (_, _) => Rescan(keepCurrent: true);

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

        Button cancel = new()
        {
            Content = T("Cancel"),
            Padding = Metrics.Density.ButtonPadding,
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        LocateSources(document);

        Content = BuildLayout(cancel);

        // The first conflict is where the work is; opening on line 1 of a 2000-line
        // file with one conflict in the middle would make the window look empty.
        Rescan(keepCurrent: false);
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
        Button previous = ToolButton("◀", T("Previous conflict"), () => GoTo(_current - 1));
        Button next = ToolButton("▶", T("Next conflict"), () => GoTo(_current + 1));

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Xs,
            Children =
            {
                previous,
                next,
                Spacer(),
                ToolButton(T("Take LOCAL"), T("Keep our version of this conflict"), () => Take(Side.Ours)),
                ToolButton(T("Take REMOTE"), T("Keep their version of this conflict"), () => Take(Side.Theirs)),
                ToolButton(T("Take BASE"), T("Go back to the common ancestor for this conflict"),
                    () => Take(Side.Base)),
                Spacer(),
                ToolButton(T("Both: L → R"), T("Keep our version followed by theirs"), () => TakeBoth(oursFirst: true)),
                ToolButton(T("Both: R → L"), T("Keep their version followed by ours"), () => TakeBoth(oursFirst: false)),
            },
        };

        Grid bar = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = Metrics.Space.Hv(Metrics.Space.Sm, Metrics.Space.Xs),
        };
        bar.Children.Add(actions);
        Grid.SetColumn(_counter, 2);
        bar.Children.Add(_counter);

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
        AddAt(pane, Header(T("MERGE RESULT"), "App.Accent"), 0);
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

    // ---------------------------------------------------------------- scanning

    /// <summary>
    ///  Re-derives the conflict list from the text. Called after every change,
    ///  including the user's own typing: the document is the model, so this is the
    ///  only place the counter and the highlighting come from.
    /// </summary>
    private void Rescan(bool keepCurrent)
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        try
        {
            TextDocument document = _result.Document;
            List<MergeMarkerBlock> found = [];

            for (int line = 1; line <= document.LineCount; line++)
            {
                if (!IsMarker(document, line, '<'))
                {
                    continue;
                }

                int mid = FindMarker(document, line + 1, '|');
                int sep = FindMarker(document, mid < 0 ? line + 1 : mid + 1, '=');
                int end = FindMarker(document, sep < 0 ? line + 1 : sep + 1, '>');
                if (sep < 0 || end < 0)
                {
                    continue;
                }

                found.Add(new MergeMarkerBlock(IdOf(Text(document, line)), line, mid, sep, end));
                line = end;
            }

            _conflicts = found;
            _current = keepCurrent ? Math.Clamp(_current, 0, Math.Max(found.Count - 1, 0)) : 0;
            _strays = CountStrayMarkers(document, found);

            _resultHighlighter.Set(found, _current);
            _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
            UpdateCounter();
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>
    ///  Counts marker lines that belong to no complete block.
    ///
    ///  <para>This exists because the block scan is deliberately strict: an edit
    ///  that damages only the opening <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> leaves a
    ///  <c>=======</c> and a <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> behind that are no
    ///  longer a conflict by any definition — and without this count the window
    ///  would cheerfully announce "all conflicts resolved" over a file with merge
    ///  debris in it. That is the one wrong answer this window must not give.</para>
    /// </summary>
    private static int CountStrayMarkers(TextDocument document, IReadOnlyList<MergeMarkerBlock> blocks)
    {
        int strays = 0;
        for (int line = 1; line <= document.LineCount; line++)
        {
            if (!IsMarker(document, line, '<') && !IsMarker(document, line, '|')
                && !IsMarker(document, line, '=') && !IsMarker(document, line, '>'))
            {
                continue;
            }

            if (!blocks.Any(b => line == b.Start || line == b.Mid || line == b.Separator || line == b.End))
            {
                strays++;
            }
        }

        return strays;
    }

    private void UpdateCounter()
    {
        int left = _conflicts.Count;
        _counter.Text = left == 0
            ? T("All conflicts resolved")
            : string.Format(T("Conflict {0} of {1}"), _current + 1, left);
        _counter.Foreground = left == 0 && _strays == 0
            ? Brush("App.DiffAdded", Brushes.Green)
            : Brush("App.Text", Brushes.Gainsboro);

        bool clean = left == 0 && _strays == 0;
        _save.Content = clean
            ? T("Save and mark resolved")
            : left > 0
                ? string.Format(T("Save anyway ({0} left)"), left)
                : T("Save anyway");
        _status.Text = clean
            ? T("Nothing left to decide. Saving stages the file.")
            : left > 0
                ? T("Markers still in the result: saving would commit them.")
                : string.Format(
                    T("{0} leftover marker line(s) belong to no conflict: saving would commit them."), _strays);
        _status.Foreground = clean
            ? Brush("App.TextDim", Brushes.Gray)
            : Brush("App.RepoStateDirty", Brushes.Orange);
    }

    // ------------------------------------------------------------- navigation

    private void GoTo(int index)
    {
        if (_conflicts.Count == 0)
        {
            return;
        }

        _current = Math.Clamp(index, 0, _conflicts.Count - 1);
        MergeMarkerBlock conflict = _conflicts[_current];

        _result.ScrollToLine(conflict.Start);
        _result.TextArea.Caret.Line = conflict.Start;

        if (_sources.TryGetValue(conflict.Id, out (LineRange Ours, LineRange Base, LineRange Theirs) source))
        {
            Reveal(_oursPane, _oursHighlighter, source.Ours);
            Reveal(_basePane, _baseHighlighter, source.Base);
            Reveal(_theirsPane, _theirsHighlighter, source.Theirs);
        }

        _resultHighlighter.Set(_conflicts, _current);
        _result.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        UpdateCounter();
    }

    private static void Reveal(TextEditor editor, RangeHighlighter highlighter, LineRange range)
    {
        highlighter.Range = range;
        if (range.Length > 0)
        {
            editor.ScrollToLine(range.Start);
        }

        editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    // ------------------------------------------------------------ resolutions

    private enum Side
    {
        Ours,
        Base,
        Theirs,
    }

    private void Take(Side side)
    {
        if (Current() is not MergeMarkerBlock conflict)
        {
            return;
        }

        Replace(conflict, Lines(conflict, side));
    }

    private void TakeBoth(bool oursFirst)
    {
        if (Current() is not MergeMarkerBlock conflict)
        {
            return;
        }

        IReadOnlyList<string> ours = Lines(conflict, Side.Ours);
        IReadOnlyList<string> theirs = Lines(conflict, Side.Theirs);
        Replace(conflict, oursFirst ? [.. ours, .. theirs] : [.. theirs, .. ours]);
    }

    private MergeMarkerBlock? Current()
        => _conflicts.Count == 0 ? null : _conflicts[Math.Clamp(_current, 0, _conflicts.Count - 1)];

    private IReadOnlyList<string> Lines(MergeMarkerBlock conflict, Side side)
    {
        // Without --diff3 markers there is no ||||||| line, so "ours" runs all the
        // way to ======= and the base is empty. Both shapes are handled because a
        // file the user has already hand-merged elsewhere can contain either.
        (int from, int to) = side switch
        {
            Side.Ours => (conflict.Start + 1, conflict.Mid < 0 ? conflict.Separator : conflict.Mid),
            Side.Base => conflict.Mid < 0
                ? (conflict.Separator, conflict.Separator)
                : (conflict.Mid + 1, conflict.Separator),
            _ => (conflict.Separator + 1, conflict.End),
        };

        List<string> lines = [];
        for (int line = from; line < to; line++)
        {
            lines.Add(Text(_result.Document, line));
        }

        return lines;
    }

    private void Replace(MergeMarkerBlock conflict, IReadOnlyList<string> lines)
    {
        TextDocument document = _result.Document;
        DocumentLine first = document.GetLineByNumber(conflict.Start);
        DocumentLine last = document.GetLineByNumber(conflict.End);

        // TotalLength includes the line terminator, so the replacement swallows the
        // newline after >>>>>>> too; the text put back therefore has to carry its
        // own trailing newline — unless the block ended the file, where there is
        // none to carry.
        int end = last.Offset + last.TotalLength;
        string replacement = lines.Count == 0 ? string.Empty : string.Join('\n', lines);
        if (replacement.Length > 0 && end > last.EndOffset)
        {
            replacement += '\n';
        }

        document.Replace(first.Offset, end - first.Offset, replacement);

        // Rescan already ran from TextChanged; keep the cursor on the conflict that
        // has taken this one's place so ◀ ▶ do not jump back to the top.
        GoTo(_current);
    }

    // ------------------------------------------------------------------ saving

    private void SaveAndClose()
    {
        _save.IsEnabled = false;
        string text = _result.Text;
        string path = _doc.Path;

        _ = SaveAsync(text, path);
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
        _status.Text = string.Format(T("Could not stage {0}: {1}"), path, result.Message.Trim());
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
    ///  empty side, which is a pure insertion — simply gets an empty range and the
    ///  pane is left where it was rather than jumping somewhere arbitrary.</para>
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
            LineRange ourRange = Locate(document.OursLines, chunk.Ours, ref ours);
            LineRange baseRange = Locate(document.BaseLines, chunk.Base, ref @base);
            LineRange theirRange = Locate(document.TheirsLines, chunk.Theirs, ref theirs);
            _sources[id] = (ourRange, baseRange, theirRange);
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

    private static string BuildResultText(IReadOnlyList<MergeChunk> chunks)
    {
        List<string> lines = [];
        int id = 0;

        foreach (MergeChunk chunk in chunks)
        {
            if (chunk.Kind == MergeChunkKind.Stable)
            {
                lines.AddRange(chunk.Text);
                continue;
            }

            id++;
            lines.Add($"<<<<<<< LOCAL #{id}");
            lines.AddRange(chunk.Ours);
            lines.Add($"||||||| BASE #{id}");
            lines.AddRange(chunk.Base);
            lines.Add("=======");
            lines.AddRange(chunk.Theirs);
            lines.Add($">>>>>>> REMOTE #{id}");
        }

        return string.Join('\n', lines);
    }

    private static int IdOf(string marker)
    {
        int hash = marker.LastIndexOf('#');
        return hash >= 0 && int.TryParse(marker[(hash + 1)..], out int id) ? id : 0;
    }

    private static string Text(TextDocument document, int line)
        => document.GetText(document.GetLineByNumber(line));

    private static int FindMarker(TextDocument document, int from, char marker)
    {
        for (int line = Math.Max(from, 1); line <= document.LineCount; line++)
        {
            if (IsMarker(document, line, marker))
            {
                return line;
            }

            if (marker != '<' && IsMarker(document, line, '<'))
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool IsMarker(TextDocument document, int line, char marker)
    {
        DocumentLine info = document.GetLineByNumber(line);
        if (info.Length < 7)
        {
            return false;
        }

        string text = document.GetText(info.Offset, Math.Min(info.Length, 8));
        for (int i = 0; i < 7; i++)
        {
            if (text[i] != marker)
            {
                return false;
            }
        }

        return text.Length == 7 || text[7] == ' ';
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
///  One conflict marker block as it currently stands in the merge result, by line
///  number. <c>Mid</c> is -1 when the block carries no <c>|||||||</c> line, which
///  is what a file merged elsewhere with the default conflict style looks like.
/// </summary>
internal sealed record MergeMarkerBlock(int Id, int Start, int Mid, int Separator, int End);

/// <summary>A run of lines, 1-based and possibly empty.</summary>
public readonly record struct LineRange(int Start, int Length)
{
    /// <summary>The line after the last one in the range.</summary>
    public int End => Start + Length;
}

/// <summary>
///  Paints the conflict blocks of the merge result: our side, the base and their
///  side each get their own wash, the marker lines a stronger one, and the block
///  the navigation is currently on an accent edge down the left.
///
///  <para>A background <i>renderer</i> rather than a line transformer, because a
///  conflict block contains empty lines and a transformer can only colour
///  characters — an empty line would punch a hole through the middle of the
///  block.</para>
/// </summary>
internal sealed class ConflictHighlighter : IBackgroundRenderer
{
    private static readonly IBrush OursWash = new SolidColorBrush(Color.FromArgb(0x24, 0x6A, 0xC7, 0x76));
    private static readonly IBrush BaseWash = new SolidColorBrush(Color.FromArgb(0x1C, 0x9B, 0x9B, 0x9B));
    private static readonly IBrush TheirsWash = new SolidColorBrush(Color.FromArgb(0x24, 0x5B, 0x9C, 0xFF));
    private static readonly IBrush MarkerWash = new SolidColorBrush(Color.FromArgb(0x38, 0xE0, 0xA7, 0x3C));
    private static readonly IBrush CurrentEdge = new SolidColorBrush(Color.FromRgb(0x5B, 0x9C, 0xFF));

    private IReadOnlyList<MergeMarkerBlock> _conflicts = [];
    private int _current;

    /// <summary>Takes the conflict list and which of them the navigation is on.</summary>
    public void Set(IReadOnlyList<MergeMarkerBlock> conflicts, int current)
    {
        _conflicts = conflicts;
        _current = current;
    }

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Background;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_conflicts.Count == 0 || textView.VisualLines.Count == 0)
        {
            return;
        }

        textView.EnsureVisualLines();

        for (int index = 0; index < _conflicts.Count; index++)
        {
            (_, int start, int mid, int separator, int end) = _conflicts[index];
            bool current = index == _current;
            int oursEnd = mid < 0 ? separator : mid;
            Fill(textView, drawingContext, start + 1, oursEnd, OursWash);
            if (mid >= 0)
            {
                Fill(textView, drawingContext, mid + 1, separator, BaseWash);
            }

            Fill(textView, drawingContext, separator + 1, end, TheirsWash);

            Fill(textView, drawingContext, start, start + 1, MarkerWash);
            if (mid >= 0)
            {
                Fill(textView, drawingContext, mid, mid + 1, MarkerWash);
            }

            Fill(textView, drawingContext, separator, separator + 1, MarkerWash);
            Fill(textView, drawingContext, end, end + 1, MarkerWash);

            if (current)
            {
                Edge(textView, drawingContext, start, end + 1);
            }
        }
    }

    private static void Fill(TextView view, DrawingContext context, int from, int to, IBrush brush)
    {
        foreach (Rect rect in Rects(view, from, to))
        {
            context.FillRectangle(brush, rect);
        }
    }

    private static void Edge(TextView view, DrawingContext context, int from, int to)
    {
        foreach (Rect rect in Rects(view, from, to))
        {
            context.FillRectangle(CurrentEdge, new Rect(rect.X, rect.Y, 3, rect.Height));
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

            double top = visual.VisualTop - view.VerticalOffset;
            yield return new Rect(0, top, Math.Max(view.Bounds.Width, 0), visual.Height);
        }
    }
}

/// <summary>
///  Paints one range of lines in a reference pane — the version of the current
///  conflict that pane is showing. Same reason as
///  <see cref="ConflictHighlighter"/> for being a renderer and not a transformer.
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
