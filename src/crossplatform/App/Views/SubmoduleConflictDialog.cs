using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  "Which commit should this submodule point at?" — the dialog for a gitlink
///  conflict, where the two side buttons of <see cref="ResolveConflictsDialog"/> are
///  not enough.
///
///  <para><b>What it exists to show.</b> A submodule conflict is two commit ids and
///  nothing else on screen, so "keep mine / keep theirs" is answered blind: nobody can
///  see what is between the two pointers, which is the only fact that decides it. This
///  dialog puts the three pointers at the top with their subjects, says outright when
///  one side is simply an ancestor of the other (a case with no judgement left in it),
///  lists what each side added since the fork, and — the reason the OK button is not
///  just two buttons — offers the commits that already contain <b>both</b> sides.
///  When such a commit exists it is nearly always the right answer, and it is one that
///  <c>checkout --ours/--theirs</c> cannot express.</para>
///
///  <para><b>Degraded mode.</b> An uninitialised submodule has no object database, so
///  there is no history to read. The dialog then shows the reason and the two shas and
///  keeps only the two side buttons — it must not fail, and it must not pretend the
///  empty lists mean "nothing changed".</para>
///
///  <para>Nothing is written here. The dialog only reports the chosen commit through
///  <see cref="ChosenSha"/>; the caller applies it with
///  <see cref="SubmoduleConflictService.ChooseCommit"/>. All git reads happen off the
///  UI thread.</para>
/// </summary>
public sealed class SubmoduleConflictDialog : ZoomWindow
{
    private readonly SubmoduleConflictService _service = new();
    private readonly string _repoPath;
    private readonly string _path;
    private readonly string? _baseSha;
    private readonly string? _oursSha;
    private readonly string? _theirsSha;

    private readonly StackPanel _pointers;
    private readonly TextBlock _relation;
    private readonly TextBlock _notice;
    private readonly Grid _lists;
    private readonly ListBox _ours;
    private readonly ListBox _theirs;
    private readonly ListBox _candidates;
    private readonly TextBlock _candidatesHeader;
    private readonly TextBox _manual;
    private readonly Button _useManual;
    private readonly Button _keepLocal;
    private readonly Button _keepRemote;
    private readonly TextBlock _chosen;
    private readonly Button _ok;

    private SubmoduleConflictReport? _report;

    /// <summary>
    ///  The submodule commit the user settled on, or <see langword="null"/> when the
    ///  dialog was cancelled. It is deliberately a bare sha and not a "side": it may be
    ///  a third commit that is neither of them.
    /// </summary>
    public string? ChosenSha => _accepted ? _picked : null;

    /// <summary>The commit highlighted so far — meaningless until OK is pressed.</summary>
    private string? _picked;

    /// <summary>
    ///  True only when the user pressed OK. Every other way out of this window —
    ///  Cancel, Escape, the title bar's close button, the window manager killing it —
    ///  must read as "no decision", and a flag set in one place is the only spelling
    ///  that covers all of them. Clearing the sha in the Cancel handler did not: Escape
    ///  goes through <see cref="DialogKeys"/> straight to Close(), so a highlighted row
    ///  survived a cancelled dialog and the caller applied it.
    /// </summary>
    private bool _accepted;

    /// <summary>The conflicted submodule path this dialog was opened on.</summary>
    public string SubmodulePath => _path;

    /// <summary>
    ///  Opens the dialog on the three stage shas of a conflicted gitlink. Any of them
    ///  may be null (an add/add conflict has no base).
    /// </summary>
    public SubmoduleConflictDialog(string repoPath, string path, string? baseSha, string? oursSha, string? theirsSha)
    {
        _repoPath = repoPath;
        _path = path;
        _baseSha = baseSha;
        _oursSha = oursSha;
        _theirsSha = theirsSha;

        Width = 900;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Panel", Brushes.DimGray);
        Title = TranslationService.TFormat(null, "Submodule conflict — {0}", path);

        _pointers = new StackPanel { Spacing = Metrics.Space.Xs };

        _relation = new TextBlock
        {
            Foreground = Brush("App.Accent", Brushes.DeepSkyBlue),
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
        };

        _notice = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gainsboro),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
            IsVisible = false,
        };

        _ours = MakeList();
        _theirs = MakeList();
        _candidates = MakeList();

        // One selection at a time across the three lists: the dialog resolves to a
        // single commit, so two highlighted rows would be a lie about the state.
        _ours.SelectionChanged += (_, _) => OnPicked(_ours);
        _theirs.SelectionChanged += (_, _) => OnPicked(_theirs);
        _candidates.SelectionChanged += (_, _) => OnPicked(_candidates);

        _candidatesHeader = Header(TranslationService.T("Commits that contain BOTH sides"));
        _candidatesHeader.Foreground = Brush("App.Accent", Brushes.DeepSkyBlue);

        _lists = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(0, Metrics.Space.Md, 0, 0),
        };
        Place(Header(TranslationService.T("Only in YOURS (local)")), 0, 0);
        Place(Header(TranslationService.T("Only in THEIRS (remote)")), 0, 1);
        Place(_ours, 1, 0);
        Place(_theirs, 1, 1);
        Place(_candidatesHeader, 2, 0, span: 2);
        Place(_candidates, 3, 0, span: 2);
        _candidates.Height = 96;

        _manual = TextBoxSurface.Apply(
            new TextBox { Watermark = TranslationService.T("…or a commit / tag / branch of the submodule") },
            Brush("App.PanelAlt", Brushes.Black),
            Brush("App.Text", Brushes.Gainsboro));
        _manual.KeyDown += (_, e) =>
        {
            if (e.Key == global::Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                UseManual();
            }
        };

        _useManual = new Button { Content = TranslationService.T("Use") };
        _useManual.Click += (_, _) => UseManual();

        _keepLocal = new Button { Content = TranslationService.T("Keep LOCAL") };
        _keepRemote = new Button { Content = TranslationService.T("Keep REMOTE") };
        _keepLocal.Click += (_, _) => Pick(_oursSha, clearLists: true);
        _keepRemote.Click += (_, _) => Pick(_theirsSha, clearLists: true);

        _chosen = new TextBlock
        {
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _ok = new Button
        {
            Content = TranslationService.T("TranslatedStrings/_ok.Text", "OK"),
            IsDefault = true,
            IsEnabled = false,
            MinWidth = 84,
            Margin = new Thickness(Metrics.Space.Sm, 0, 0, 0),
        };
        _ok.Click += (_, _) =>
        {
            _accepted = true;
            Close();
        };

        Button cancel = new()
        {
            Content = TranslationService.T("FormCommit/Cancel.Text", "Cancel"),
            IsCancel = true,
            MinWidth = 84,
            Margin = new Thickness(Metrics.Space.Sm, 0, 0, 0),
        };
        cancel.Click += (_, _) => Close();

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Sm,
            Margin = new Thickness(0, Metrics.Space.Md, 0, Metrics.Space.Sm),
        };
        actions.Children.Add(_keepLocal);
        actions.Children.Add(_keepRemote);
        actions.Children.Add(_manual);
        actions.Children.Add(_useManual);
        _manual.Width = 240;

        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(_chosen, 0);
        Grid.SetColumn(_ok, 1);
        Grid.SetColumn(cancel, 2);
        footer.Children.Add(_chosen);
        footer.Children.Add(_ok);
        footer.Children.Add(cancel);

        Grid body = new()
        {
            Margin = Metrics.Space.All(Metrics.Space.Lg),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"),
        };
        Grid.SetRow(_pointers, 0);
        Grid.SetRow(_relation, 1);
        Grid.SetRow(_notice, 2);
        Grid.SetRow(_lists, 3);
        Grid.SetRow(actions, 4);
        Grid.SetRow(footer, 5);
        body.Children.Add(_pointers);
        body.Children.Add(_relation);
        body.Children.Add(_notice);
        body.Children.Add(_lists);
        body.Children.Add(actions);
        body.Children.Add(footer);

        Content = body;
        DialogKeys.InstallEscapeClose(this);

        ShowPointers(null);
        UpdateChosenText();
        Opened += (_, _) => Load();
    }

    /// <summary>
    ///  Convenience overload for the conflict list, which already holds the three
    ///  stages. Kept separate so this dialog does not force
    ///  <see cref="ConflictService"/>'s types on a caller that only has shas.
    /// </summary>
    public SubmoduleConflictDialog(string repoPath, ConflictEntry entry)
        : this(repoPath, entry.Path, entry.Base.Sha, entry.Ours.Sha, entry.Theirs.Sha)
    {
    }

    // --- Loading ----------------------------------------------------------

    private void Load()
    {
        _relation.Text = TranslationService.T("Reading the submodule's history…");
        string repo = _repoPath;
        string path = _path;
        string? b = _baseSha, o = _oursSha, t = _theirsSha;

        // Off the UI thread, as the service's contract demands: every query below is a
        // blocking git process and a submodule with a long history takes visible time.
        // Async.OffUi rather than a hand-rolled ContinueWith: no blocking read of
        // Result, and a fault is reported instead of leaving the dialog on "Reading…".
        Async.OffUi(() => _service.Describe(repo, path, b, o, t), Apply, "submodule conflict");
    }

    private void Apply(SubmoduleConflictReport report)
    {
        _report = report;
        ShowPointers(report);

        if (!report.HasHistory)
        {
            Degrade(report.Unavailable!);
            return;
        }

        _relation.Text = report.Relation switch
        {
            SubmodulePointerRelation.Same =>
                TranslationService.T("Both sides point at the same commit — either button resolves it."),
            SubmodulePointerRelation.OursBehind =>
                TranslationService.T("YOURS points BACKWARDS: your commit is an ancestor of theirs, so theirs already contains it. Keeping REMOTE loses nothing."),
            SubmodulePointerRelation.OursAhead =>
                TranslationService.T("YOURS points FORWARDS: their commit is an ancestor of yours, so yours already contains it. Keeping LOCAL loses nothing."),
            SubmodulePointerRelation.Diverged => report.Candidates.Count > 0
                ? TranslationService.TFormat(null,
                    "The two sides have DIVERGED at {0}. {1} commit(s) below already contain both — one of those is usually the answer.",
                    Short(report.MergeBase), report.Candidates.Count)
                : TranslationService.TFormat(null,
                    "The two sides have DIVERGED at {0}, and no commit in this clone contains both. Merge them inside the submodule, or keep one side.",
                    Short(report.MergeBase)),
            _ => string.Empty,
        };

        if (report.MergeBase is not null && !report.MergeBaseIsRecordedBase)
        {
            // Worth saying: the "only in" lists are computed from the submodule's own
            // fork point, which is not the commit the superproject recorded as BASE.
            _notice.Text = TranslationService.TFormat(null,
                "Note: the fork point inside the submodule ({0}) is not the commit the superproject recorded as BASE ({1}); the lists below are relative to the fork point.",
                Short(report.MergeBase), Short(report.Base.Sha));
            _notice.IsVisible = true;
        }

        _ours.ItemsSource = report.OnlyInOurs.Select(c => new Row(c)).ToList();
        _theirs.ItemsSource = report.OnlyInTheirs.Select(c => new Row(c)).ToList();
        _candidates.ItemsSource = report.Candidates.Select(c => new Row(c)).ToList();

        bool anyCandidate = report.Candidates.Count > 0;
        _candidates.IsVisible = anyCandidate;
        _candidatesHeader.IsVisible = anyCandidate;
    }

    /// <summary>
    ///  The no-history path: message, side buttons, nothing else. The lists are hidden
    ///  rather than left empty so nobody reads "no commits" into them.
    /// </summary>
    private void Degrade(string message)
    {
        _relation.Text = message;
        _lists.IsVisible = false;
        _manual.IsEnabled = false;
        _useManual.IsEnabled = false;
        _notice.Text = TranslationService.TFormat(null,
            "LOCAL is {0}, REMOTE is {1}. Only these two can be chosen here.",
            Short(_oursSha), Short(_theirsSha));
        _notice.IsVisible = true;
    }

    // --- The three pointers ----------------------------------------------

    private void ShowPointers(SubmoduleConflictReport? report)
    {
        _pointers.Children.Clear();
        _pointers.Children.Add(PointerRow("BASE", report?.Base ?? SubmoduleCommitInfo.Unknown(_baseSha), report));
        _pointers.Children.Add(PointerRow("LOCAL", report?.Ours ?? SubmoduleCommitInfo.Unknown(_oursSha), report));
        _pointers.Children.Add(PointerRow("REMOTE", report?.Theirs ?? SubmoduleCommitInfo.Unknown(_theirsSha), report));
    }

    private Control PointerRow(string label, SubmoduleCommitInfo info, SubmoduleConflictReport? report)
    {
        // The ancestry marker goes on the row itself, not only in the sentence above:
        // this is the one fact that turns a decision into a formality, and it belongs
        // next to the commit it is about.
        string marker = (label, report?.Relation) switch
        {
            ("LOCAL", SubmodulePointerRelation.OursBehind) => TranslationService.T("  ← behind REMOTE (ancestor)"),
            ("REMOTE", SubmodulePointerRelation.OursBehind) => TranslationService.T("  ← contains LOCAL"),
            ("LOCAL", SubmodulePointerRelation.OursAhead) => TranslationService.T("  ← contains REMOTE"),
            ("REMOTE", SubmodulePointerRelation.OursAhead) => TranslationService.T("  ← behind LOCAL (ancestor)"),
            _ => string.Empty,
        };

        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = Metrics.Space.Sm };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 72,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("App.TextDim", Brushes.Gainsboro),
        });
        row.Children.Add(new TextBlock
        {
            Text = info.IsEmpty ? TranslationService.T("(absent)") : info.ShortSha,
            Width = 80,
            FontFamily = new FontFamily("monospace"),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        });
        row.Children.Add(new TextBlock
        {
            Text = Describe(info) + marker,
            Foreground = Brush(info.Exists ? "App.Text" : "App.TextDim", Brushes.Gainsboro),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return row;
    }

    private static string Describe(SubmoduleCommitInfo info)
    {
        if (info.IsEmpty)
        {
            return TranslationService.T("this side records no submodule commit");
        }

        if (!info.Exists)
        {
            return TranslationService.T("not in this clone — fetch the submodule to see it");
        }

        string date = info.Date is { } when ? when.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : string.Empty;
        return $"{info.Subject}   —   {info.Author}   {date}";
    }

    // --- Picking ----------------------------------------------------------

    private void OnPicked(ListBox source)
    {
        if (source.SelectedItem is not Row row)
        {
            return;
        }

        foreach (ListBox other in new[] { _ours, _theirs, _candidates })
        {
            if (!ReferenceEquals(other, source))
            {
                other.SelectedItem = null;
            }
        }

        Pick(row.Commit.Sha, clearLists: false);
    }

    private void Pick(string? sha, bool clearLists)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return;
        }

        if (clearLists)
        {
            _ours.SelectedItem = null;
            _theirs.SelectedItem = null;
            _candidates.SelectedItem = null;
        }

        _picked = sha;
        UpdateChosenText();
    }

    private void UseManual()
    {
        string typed = _manual.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(typed))
        {
            return;
        }

        string repo = _repoPath;
        string path = _path;
        _useManual.IsEnabled = false;
        Async.OffUi(
            () => _service.ResolveRevision(repo, path, typed),
            sha =>
            {
                _useManual.IsEnabled = true;
                if (sha is null)
                {
                    // Not an error dialog: an unknown revision is a typo, and the
                    // chosen-commit line is where the user is already looking.
                    _chosen.Text = TranslationService.TFormat(null, "'{0}' does not name a commit of this submodule.", typed.Trim());
                    return;
                }

                Pick(sha, clearLists: true);
            },
            "submodule revision");
    }

    private void UpdateChosenText()
    {
        _ok.IsEnabled = _picked is not null;
        if (_picked is null)
        {
            _chosen.Text = TranslationService.T("Nothing chosen yet.");
            return;
        }

        string label =
            Same(_picked, _oursSha) ? TranslationService.T("the LOCAL side")
            : Same(_picked, _theirsSha) ? TranslationService.T("the REMOTE side")
            : Same(_picked, _baseSha) ? TranslationService.T("the BASE commit")
            : TranslationService.T("neither side");

        SubmoduleCommitInfo? info = Find(_picked);
        string subject = info is { Exists: true } ? $" — {info.Subject}" : string.Empty;
        _chosen.Text = TranslationService.TFormat(null,
            "{0} will point at {1} ({2}){3}", _path, Short(_picked), label, subject);
    }

    private SubmoduleCommitInfo? Find(string sha)
    {
        if (_report is null)
        {
            return null;
        }

        return new[] { _report.Base, _report.Ours, _report.Theirs }
            .Concat(_report.OnlyInOurs)
            .Concat(_report.OnlyInTheirs)
            .Concat(_report.Candidates)
            .FirstOrDefault(c => Same(c.Sha, sha));
    }

    // --- Small helpers ----------------------------------------------------

    private static bool Same(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Short(string? sha)
        => sha is { Length: >= 8 } ? sha[..8] : sha ?? "—";

    private ListBox MakeList()
    {
        ListBox list = MakeBareList();

        // A commit line is sha + date + subject + author and does not fit in half a
        // dialog; trimming it would hide the author, which is half of "whose change is
        // this?". Scrolling sideways keeps the whole line reachable.
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        return list;
    }

    private ListBox MakeBareList() => new()
    {
        Background = Brush("App.PanelAlt", Brushes.Black),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        FontFamily = new FontFamily("monospace"),
        FontSize = Metrics.Text.Body,
        Margin = new Thickness(0, Metrics.Space.Xs, Metrics.Space.Xs, 0),
    };

    private TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("App.TextDim", Brushes.Gainsboro),
        Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
    };

    private void Place(Control control, int row, int column, int span = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, span);
        _lists.Children.Add(control);
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    /// <summary>One selectable commit line. The ListBox renders <see cref="ToString"/>.</summary>
    private sealed record Row(SubmoduleCommitInfo Commit)
    {
        public override string ToString()
        {
            string date = Commit.Date is { } when ? when.LocalDateTime.ToString("yyyy-MM-dd") : "          ";
            return $"{Commit.ShortSha}  {date}  {Commit.Subject}  ({Commit.Author})";
        }
    }
}
