using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The revision filter dialog, the Avalonia counterpart of the original
///  <c>FormRevisionFilter</c>: author, committer, message, diff content, date
///  range, path filter and commit limit, plus the case / regex switches and the
///  three cheap history-simplifying toggles.
///
///  <para>Like the original, every value field is gated by its own check box: the
///  box both enables the editor and decides whether the criterion is emitted, so
///  a criterion can be switched off without losing what was typed. "Reset all
///  filters" clears everything in place (it does not close the dialog).</para>
///
///  <para>The result is a <see cref="RevisionFilter"/>, which
///  <see cref="RevisionService"/> turns into <c>git log</c> arguments — the filter
///  is applied by git during the walk, never by sifting loaded rows.</para>
/// </summary>
public sealed class RevisionFilterDialog : Window
{
    private readonly Row _author;
    private readonly Row _committer;
    private readonly Row _message;
    private readonly Row _diffContent;
    private readonly Row _since;
    private readonly Row _until;
    private readonly Row _pathFilter;
    private readonly Row _limit;

    private readonly RadioButton _diffLiteral;
    private readonly RadioButton _diffRegex;

    private readonly CheckBox _ignoreCase;
    private readonly CheckBox _useRegex;
    private readonly CheckBox _hideMerges;
    private readonly CheckBox _firstParent;
    private readonly CheckBox _simplifyByDecoration;

    private static readonly ViewPrefsService PrefsService = new();

    // Snapshot of the persisted MRU, newest first, taken when the dialog opens.
    private readonly List<RevisionFilterMruEntry> _mru;

    /// <summary>True when the user pressed OK (not Cancel / window close).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The criteria as edited; only meaningful once <see cref="Confirmed"/>.</summary>
    public RevisionFilter Result { get; private set; } = RevisionFilter.None;

    public RevisionFilterDialog(RevisionFilter current)
    {
        IBrush window = CheckoutBranchDialog.Brush("App.Window", "#1F1F1F");
        IBrush text = CheckoutBranchDialog.Brush("App.Text", "#DCDCDC");
        IBrush dim = CheckoutBranchDialog.Brush("App.TextDim", "#9A9A9A");
        IBrush border = CheckoutBranchDialog.Brush("App.Border", "#3F3F3F");

        Title = T("FormRevisionFilter/$this.Text", "Filter");
        Width = 620;
        // Translated captions are often much longer than the English ones; the
        // dialog grows downwards instead of clipping them.
        SizeToContent = SizeToContent.Height;
        MinWidth = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        // --- the value rows (check box + editor), all in one two-column grid so
        // the editors line up however long the translated captions turn out to be.
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 8, 0, 0),
        };

        _author = AddRow(grid, T("FormRevisionFilter/_author.Text", "&Author"), current.Author, text);
        _committer = AddRow(grid, T("FormRevisionFilter/_committer.Text", "&Committer"), current.Committer, text);
        _message = AddRow(grid, T("FormRevisionFilter/_message.Text", "&Message"), current.Message, text);
        _diffContent = AddRow(grid, T("FormRevisionFilter/_diffContent.Text", "&Diff contains"), current.DiffContent, text);
        ToolTip.SetTip(_diffContent.Editor, T("FormRevisionFilter/_diffContentToolTip.Text", "SLOW"));

        // How the diff-content search is run: -S (literal occurrences) or -G (regex
        // over the added/removed lines). Upstream only ever offers -G.
        _diffLiteral = new RadioButton
        {
            GroupName = "diffMode",
            Content = T("string (-S)"),
            Foreground = text,
            IsChecked = !current.DiffContentIsRegex,
        };
        _diffRegex = new RadioButton
        {
            GroupName = "diffMode",
            Content = T("regex (-G)"),
            Foreground = text,
            Margin = new Thickness(12, 0, 0, 0),
            IsChecked = current.DiffContentIsRegex,
        };

        // WrapPanel, not a fixed-width horizontal StackPanel: a longer translation
        // moves the second radio to the next line instead of off the dialog.
        WrapPanel diffMode = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0),
            Children = { _diffLiteral, _diffRegex },
        };
        AddSpanningRow(grid, diffMode);

        _since = AddRow(grid, T("FormRevisionFilter/_since.Text", "&Since"), current.DateFrom, text);
        _since.Editor.Watermark = T("yyyy-MM-dd (or \"3 weeks ago\")");
        _until = AddRow(grid, T("FormRevisionFilter/_until.Text", "&Until"), current.DateTo, text);
        _until.Editor.Watermark = T("yyyy-MM-dd (or \"3 weeks ago\")");

        _pathFilter = AddRow(grid, T("FormRevisionFilter/_pathFilter.Text", "&Path filter"), current.PathFilter, text);
        _pathFilter.Editor.Watermark = T("src/  doc/*.md   (space separates several paths)");

        _limit = AddRow(
            grid,
            T("FormRevisionFilter/_limit.Text", "&Limit"),
            current.CommitsLimit > 0 ? current.CommitsLimit.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty,
            text);

        // --- the plain switches -------------------------------------------------
        _ignoreCase = Check(T("FormRevisionFilter/IgnoreCase.Text", "&Ignore case"), !current.CaseSensitive, text);
        _useRegex = Check(T("Use regular expressions"), current.UseRegex, text);
        _hideMerges = Check(T("FormRevisionFilter/HideMergeCommitsCheck.Text", "Hide merge commi&ts"), current.HideMergeCommits, text);
        _firstParent = Check(T("FormRevisionFilter/OnlyFirstParentCheck.Text", "Show only &first parent"), current.FirstParentOnly, text);
        _simplifyByDecoration = Check(
            T("FormRevisionFilter/SimplifyByDecorationCheck.Text", "Simplify b&y decoration"),
            current.SimplifyByDecoration,
            text);

        StackPanel switches = new()
        {
            Spacing = 4,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _ignoreCase, _useRegex, _hideMerges, _firstParent, _simplifyByDecoration },
        };

        TextBlock hint = new()
        {
            Text = T("The criteria are passed to git log, so they apply to the whole history — not only to the commits already loaded."),
            Foreground = dim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };

        // The MRU of previously confirmed filters. Loaded once here (not on every
        // click) so the flyout can be populated before ShowAt — a MenuFlyout filled
        // from its Opening handler is not re-measured and shows as a thin sliver
        // (HANDOFF §3).
        _mru = PrefsService.Load().RevisionFilterMru;

        Button recent = new()
        {
            Content = T("Recent filters") + "  ▾",
            MinWidth = 120,
            // A real control, disabled while there is nothing to offer, rather than a
            // button that opens an empty popup.
            IsEnabled = _mru.Count > 0,
        };
        recent.Click += (_, _) => ShowRecentMenu(recent);

        Button reset = new()
        {
            Content = StripMnemonic(T("FormBrowse/tsmiResetAllFilters.Text", "&Reset revision filters")),
            MinWidth = 120,
            Margin = new Thickness(8, 0, 0, 0),
        };
        reset.Click += (_, _) => ResetFields();

        Button ok = new()
        {
            Content = T("FormRevisionFilter/Ok.Text", "OK"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            Result = Collect();
            Confirmed = true;
            RememberFilter(Result);
            Close();
        };

        Button cancel = new()
        {
            Content = T("TranslatedStrings/_cancelText.Text", "Cancel"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        WrapPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { recent, reset, ok, cancel },
        };

        Border box = new()
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new StackPanel { Children = { grid, switches } },
        };

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children = { box, hint, buttons },
            },
        };

        DialogKeys.InstallEscapeClose(this);
    }

    /// <summary>
    ///  Shows the dialog over <paramref name="owner"/> and returns the edited
    ///  criteria, or <see langword="null"/> when the user cancelled. With no owner
    ///  (headless / self-test) there is nothing to parent a modal to, so it
    ///  returns <see langword="null"/> without showing anything.
    /// </summary>
    public static async Task<RevisionFilter?> AskAsync(Window? owner, RevisionFilter current)
    {
        if (owner is null)
        {
            return null;
        }

        RevisionFilterDialog dialog = new(current);
        await dialog.ShowDialog(owner);
        return dialog.Confirmed ? dialog.Result : null;
    }

    // Reads the controls back into a filter. A criterion counts only when its
    // check box is ticked AND its editor is non-empty, exactly like upstream.
    private RevisionFilter Collect()
    {
        int limit = 0;
        if (_limit.Enabled
            && int.TryParse(_limit.Editor.Text?.Trim(), out int parsed)
            && parsed > 0)
        {
            limit = parsed;
        }

        return new RevisionFilter
        {
            Author = _author.Value,
            Committer = _committer.Value,
            Message = _message.Value,
            DiffContent = _diffContent.Value,
            DiffContentIsRegex = _diffRegex.IsChecked == true,
            DateFrom = _since.Value,
            DateTo = _until.Value,
            PathFilter = _pathFilter.Value,
            CommitsLimit = limit,
            CaseSensitive = _ignoreCase.IsChecked != true,
            UseRegex = _useRegex.IsChecked == true,
            HideMergeCommits = _hideMerges.IsChecked == true,
            FirstParentOnly = _firstParent.IsChecked == true,
            SimplifyByDecoration = _simplifyByDecoration.IsChecked == true,
        };
    }

    // ------------------------------------------------- the recently used filters

    // Items are added before ShowAt (HANDOFF §3). Labels are DATA — user-typed
    // patterns — so they are never fed to the translation lookup, and any "_" in them
    // is doubled so Avalonia's access-key parser does not eat it.
    private void ShowRecentMenu(Control anchor)
    {
        MenuFlyout flyout = new();

        foreach (RevisionFilterMruEntry entry in _mru)
        {
            MenuItem item = new() { Header = Describe(entry).Replace("_", "__") };
            RevisionFilterMruEntry captured = entry;
            item.Click += (_, _) => ApplyFilter(ToFilter(captured));
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    // Pushes the confirmed filter onto the MRU. Update(), not Save(): view-prefs.json
    // also carries the diff options, the file-history switches and the left panel
    // filters, and a plain save of a stale copy would revert them. The neutral filter
    // is dropped by PushMru — "no filter" is not worth a slot, and pressing OK on an
    // empty dialog is how the full history comes back.
    private static void RememberFilter(RevisionFilter filter)
    {
        try
        {
            RevisionFilterMruEntry entry = ToEntry(filter);
            PrefsService.Update(prefs => ViewPrefsService.PushMru(prefs.RevisionFilterMru, entry));
        }
        catch
        {
            // Remembering is a convenience; it must never stop the dialog closing or
            // the filter being applied.
        }
    }

    // Puts a remembered filter back into the controls: the inverse of Collect(), and
    // the reason the MRU is worth having at all — a persisted list nothing reapplies
    // would be dead weight.
    private void ApplyFilter(RevisionFilter f)
    {
        _author.Set(f.Author);
        _committer.Set(f.Committer);
        _message.Set(f.Message);
        _diffContent.Set(f.DiffContent);
        _since.Set(f.DateFrom);
        _until.Set(f.DateTo);
        _pathFilter.Set(f.PathFilter);
        _limit.Set(f.CommitsLimit > 0
            ? f.CommitsLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty);

        _diffRegex.IsChecked = f.DiffContentIsRegex;
        _diffLiteral.IsChecked = !f.DiffContentIsRegex;
        _ignoreCase.IsChecked = !f.CaseSensitive;
        _useRegex.IsChecked = f.UseRegex;
        _hideMerges.IsChecked = f.HideMergeCommits;
        _firstParent.IsChecked = f.FirstParentOnly;
        _simplifyByDecoration.IsChecked = f.SimplifyByDecoration;
    }

    // A one-line summary of a remembered filter, in the shape of the git options it
    // stands for, so the list is readable without opening each entry. Long patterns
    // are elided; the switches are appended as bare flags.
    private static string Describe(RevisionFilterMruEntry e)
    {
        List<string> parts = [];
        Add("--grep", e.Message);
        Add("--author", e.Author);
        Add("--committer", e.Committer);
        Add(e.DiffContentIsRegex ? "-G" : "-S", e.DiffContent);
        Add("--since", e.DateFrom);
        Add("--until", e.DateTo);
        Add("--", e.PathFilter);

        if (e.CommitsLimit > 0)
        {
            parts.Add("-n " + e.CommitsLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (e.HideMergeCommits)
        {
            parts.Add("--no-merges");
        }

        if (e.FirstParentOnly)
        {
            parts.Add("--first-parent");
        }

        if (e.SimplifyByDecoration)
        {
            parts.Add("--simplify-by-decoration");
        }

        return parts.Count == 0 ? T("(no criteria)") : string.Join("  ", parts);

        void Add(string flag, string value)
        {
            if (value.Length > 0)
            {
                parts.Add(flag + " " + Elide(value));
            }
        }
    }

    private static string Elide(string value)
        => value.Length <= 28 ? value : string.Concat(value.AsSpan(0, 27), "…");

    // The two mappings between the walk's filter record and the serialisable entry.
    // Only the criteria the dialog edits cross over: RevisionFilter's rename/history
    // members (FollowRenames, FullHistory, …) belong to file-history mode, are set by
    // that view alone, and must not be resurrected from a remembered dialog filter.
    private static RevisionFilterMruEntry ToEntry(RevisionFilter f) => new()
    {
        Author = f.Author,
        Committer = f.Committer,
        Message = f.Message,
        DiffContent = f.DiffContent,
        DiffContentIsRegex = f.DiffContentIsRegex,
        DateFrom = f.DateFrom,
        DateTo = f.DateTo,
        PathFilter = f.PathFilter,
        CommitsLimit = f.CommitsLimit,
        CaseSensitive = f.CaseSensitive,
        UseRegex = f.UseRegex,
        HideMergeCommits = f.HideMergeCommits,
        FirstParentOnly = f.FirstParentOnly,
        SimplifyByDecoration = f.SimplifyByDecoration,
    };

    private static RevisionFilter ToFilter(RevisionFilterMruEntry e) => new()
    {
        Author = e.Author,
        Committer = e.Committer,
        Message = e.Message,
        DiffContent = e.DiffContent,
        DiffContentIsRegex = e.DiffContentIsRegex,
        DateFrom = e.DateFrom,
        DateTo = e.DateTo,
        PathFilter = e.PathFilter,
        CommitsLimit = e.CommitsLimit,
        CaseSensitive = e.CaseSensitive,
        UseRegex = e.UseRegex,
        HideMergeCommits = e.HideMergeCommits,
        FirstParentOnly = e.FirstParentOnly,
        SimplifyByDecoration = e.SimplifyByDecoration,
    };

    // "Reset all filters": clears every criterion in place, leaving the dialog open
    // so the user can immediately build a new filter (or press OK on an empty one,
    // which is how the full history comes back).
    private void ResetFields()
    {
        foreach (Row row in new[] { _author, _committer, _message, _diffContent, _since, _until, _pathFilter, _limit })
        {
            row.Clear();
        }

        _diffLiteral.IsChecked = true;
        _ignoreCase.IsChecked = true;
        _useRegex.IsChecked = false;
        _hideMerges.IsChecked = false;
        _firstParent.IsChecked = false;
        _simplifyByDecoration.IsChecked = false;
    }

    // One "check box + editor" pair. The check box carries the caption (so the
    // mnemonic-stripped label and its gate are a single target) and the editor sits
    // in the second column, stretching with the dialog.
    private Row AddRow(Grid grid, string caption, string value, IBrush text)
    {
        int r = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        CheckBox gate = new()
        {
            Content = StripMnemonic(caption),
            Foreground = text,
            IsChecked = !string.IsNullOrWhiteSpace(value),
            Margin = new Thickness(0, 3, 12, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        TextBox editor = new()
        {
            Text = value,
            Margin = new Thickness(0, 3, 0, 3),
            IsEnabled = gate.IsChecked == true,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        gate.IsCheckedChanged += (_, _) =>
        {
            editor.IsEnabled = gate.IsChecked == true;
            if (editor.IsEnabled)
            {
                editor.Focus();
            }
        };

        // Typing into an enabled-by-default-empty box is impossible (it is disabled
        // until ticked), but ticking then typing is the normal path; and clearing the
        // text with the box ticked simply yields no criterion.
        Grid.SetRow(gate, r);
        Grid.SetColumn(gate, 0);
        Grid.SetRow(editor, r);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(gate);
        grid.Children.Add(editor);

        return new Row(gate, editor);
    }

    // A control occupying both columns of the grid (used by the -S/-G selector).
    private static void AddSpanningRow(Grid grid, Control content)
    {
        int r = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(content, r);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
    }

    private static CheckBox Check(string caption, bool value, IBrush text)
        => new() { Content = StripMnemonic(caption), Foreground = text, IsChecked = value };

    // WinForms captions carry "&" mnemonics ("&Author"); Avalonia uses "_" and we
    // simply drop them, keeping a literal "&&" as one ampersand.
    internal static string StripMnemonic(string caption)
        => caption.Replace("&&", "").Replace("&", string.Empty).Replace("", "&");

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // A gated value field: the criterion is set only while the gate is ticked.
    private sealed record Row(CheckBox Gate, TextBox Editor)
    {
        public bool Enabled => Gate.IsChecked == true;

        public string Value => Enabled ? Editor.Text ?? string.Empty : string.Empty;

        public void Clear()
        {
            Gate.IsChecked = false;
            Editor.Text = string.Empty;
        }

        /// <summary>
        ///  Puts a value in and ticks the gate iff there is one — the same rule
        ///  <c>AddRow</c> applies to the filter the dialog opens with, so a remembered
        ///  filter lands in exactly the state it would have had if typed.
        /// </summary>
        public void Set(string value)
        {
            Editor.Text = value;
            Gate.IsChecked = !string.IsNullOrWhiteSpace(value);
        }
    }
}
