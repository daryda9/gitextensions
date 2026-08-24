using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Port of upstream's <c>FormResolveConflicts</c> (1571 lines,
///  <c>src/app/GitUI/CommandsDialogs/FormResolveConflicts.cs</c>): the window that
///  lists the unresolved merge conflicts and resolves them, either by handing a
///  file to the configured merge tool or by keeping one side outright.
///
///  <para><b>Layout</b>, following <c>03_resolve merge conflict window dialog.png</c>:
///  the label <i>Unresolved merge conflicts</i> over a single-column
///  <i>Filename</i> list, a column of four buttons on the right
///  (<b>Open in &lt;tool&gt;</b> / <b>Start mergetool</b> /
///  <b>Rescan merge conflicts</b> / <b>Reset</b>), an information strip that
///  describes the selected conflict with a <b>Merge</b> button beside it, the three
///  <i>Local/current (ours)</i> · <i>Base</i> · <i>Remote/incoming (theirs)</i>
///  rows, and the <b>Help</b> link at the bottom.</para>
///
///  <para><b>The tool name is data, not a literal.</b> The first button reads
///  "Open in " + <see cref="ConflictService.GetMergeToolName"/>, i.e.
///  <c>merge.guitool</c> falling back to <c>merge.tool</c> — the same order as
///  upstream's <c>InitMergetool</c> (<c>FormResolveConflicts.cs:689-711</c>). When
///  neither is configured the caption degrades to "Open in mergetool" and both
///  merge-tool actions are disabled with an explanatory status line, which is the
///  same end state as upstream (it shows a message box, then leaves the buttons
///  disabled because <c>mergeToolExtrasConfigured</c> is false, <c>:824-830</c>).</para>
///
///  <para><b>Conflict kinds come from the index stages</b>
///  (<see cref="ConflictKind"/>), never from git's console text, which is
///  localised on this machine. All six stage combinations get a description; note
///  that upstream only covers four and silently keeps the previous label for the
///  add-by-one-side cases (<c>:856-862</c>).</para>
///
///  <para><b>Threading</b>: every git call goes through <see cref="Task.Run"/> and
///  the merge tool is launched detached, so the window stays responsive while
///  kdiff3/meld is open; when the tool exits the list rescans itself, because
///  <c>git mergetool</c> stages the file on a successful exit and the list would
///  otherwise be stale.</para>
/// </summary>
public sealed class ResolveConflictsDialog : Theming.ZoomWindow
{
    // Upstream's documentation anchor for this form: gotoUserManualControl1 with
    // ManualSectionSubfolder = "modify_history", ManualSectionAnchorName =
    // "handle-merge-conflicts" (FormResolveConflicts.Designer.cs:554-555).
    private const string HelpUrl =
        "https://git-extensions-documentation.readthedocs.io/en/main/modify_history.html#handle-merge-conflicts";

    private readonly string _repoPath;
    private readonly ConflictService _service = new();
    private readonly ExternalToolService _externalTools = new();
    private readonly RerereService _rerere = new();
    private readonly SubmoduleConflictService _submodules = new();
    private readonly MergeToolService _mergeService = new();

    private readonly ListBox _files;
    private readonly TextBlock _header;
    private readonly TextBlock _description;
    private readonly TextBlock _status;
    private readonly Button _openInTool;
    private readonly Button _startMergetool;
    private readonly Button _rescan;
    private readonly Button _reset;
    private readonly Button _merge;
    private readonly TextBlock _labelOurs;
    private readonly TextBlock _labelTheirs;
    private readonly TextBlock _ourName;
    private readonly TextBlock _baseName;
    private readonly TextBlock _theirName;

    // ---- rerere (reuse recorded resolution) ---------------------------------
    // Everything git already does silently, made visible: whether it is on and WHY,
    // what it has already resolved on the user's behalf, and the way out.
    private readonly Border _rerereBanner;
    private readonly TextBlock _rerereBannerTitle;
    private readonly TextBlock _rerereBannerDetail;
    private readonly CheckBox _rerereEnabled;
    private readonly CheckBox _rerereAutoUpdate;
    private readonly Button _rerereCache;
    private readonly TextBlock _rerereReplayed;
    private readonly Expander _rerereDiff;
    private readonly TextBox _rerereDiffText;
    private readonly RowDefinition _diffRow;

    // ---- guided refusal ------------------------------------------------------
    // Shown INSTEAD of an error when the built-in merge cannot open a file: the same
    // sentence a message box would have carried, plus the ways out that actually
    // apply to this file. A user told only "cannot merge" stops there.
    private readonly Border _guided;
    private readonly TextBlock _guidedWhy;
    private readonly TextBlock _guidedNote;
    private readonly Button _guidedOurs;
    private readonly Button _guidedTheirs;
    private readonly Button _guidedImages;
    private readonly Button _guidedSubmodule;

    private readonly MenuItem _ctxForget = new();
    private readonly MenuItem _ctxMergeHere = new();
    private readonly MenuItem _ctxOpenInTool = new();
    private readonly MenuItem _ctxMarkResolved = new();
    private readonly MenuItem _ctxChooseOurs = new();
    private readonly MenuItem _ctxChooseTheirs = new();
    private readonly MenuItem _ctxChooseBase = new();
    private readonly MenuItem _ctxOpen = new();
    private readonly MenuItem _ctxShowInFolder = new();

    // The "staged but still marked" banner: files git thinks are resolved whose
    // indexed content still carries conflict markers (ConflictService.ListStagedWithMarkers).
    private readonly Border _markerBanner;
    private readonly TextBlock _markerText;
    private readonly Button _markerReopen;
    private IReadOnlyList<string> _markerFiles = [];

    private readonly string? _mergeTool;
    private readonly bool _inRebase;

    /// <summary>
    ///  Which operation is stopped here, used ONLY for the rerere wording. It is kept apart from
    ///  <see cref="_inRebase"/>, which decides the ours/theirs labels: a cherry-pick, a revert and
    ///  an <c>am -3</c> conflict all keep the merge orientation (HEAD is still <i>ours</i>), so
    ///  they must not flip the labels — but they are stepwise like a rebase, so they must not be
    ///  told "next time" either.
    /// </summary>
    private readonly RerereOperation _operation;

    private IReadOnlyList<ConflictEntry> _conflicts;
    private string? _refusedPath;
    private RerereSnapshot _rerereState;
    private IReadOnlyList<string> _rerereReplayedPaths;
    private bool _busy;

    /// <summary>
    ///  True once the repository has no unmerged entries left. The caller uses it
    ///  to offer the commit dialog, which is what upstream does at the end of its
    ///  <c>Initialize()</c> (<c>FormResolveConflicts.cs:283-297</c>) — that
    ///  decision does not belong to this window.
    /// </summary>
    public bool AllConflictsResolved { get; private set; }

    private ResolveConflictsDialog(
        string repoPath,
        IReadOnlyList<ConflictEntry> conflicts,
        string? mergeTool,
        bool inRebase,
        RerereOperation operation,
        RerereSnapshot rerereState,
        IReadOnlyList<string> rerereReplayedPaths)
    {
        _repoPath = repoPath;
        _conflicts = conflicts;
        _mergeTool = mergeTool;
        _inRebase = inRebase;
        _operation = operation;
        _rerereState = rerereState;
        _rerereReplayedPaths = rerereReplayedPaths;

        IBrush text = Brush("App.Text", Brushes.Gainsboro);
        IBrush dim = Brush("App.TextDim", Brushes.Gray);
        // App.BorderStrong: this brush only outlines the conflict list — a selectable
        // ListBox on App.Panel plus the header strip that caps the same box — so the
        // 1px line is the control's only chrome and WCAG 1.4.11 asks 3:1 for it.
        IBrush border = Brush("App.BorderStrong", new SolidColorBrush(Color.Parse("#88898F")));

        Title = T("FormResolveConflicts/$this.Text", "Resolve merge conflicts");
        Width = 720;

        // 480 before rerere; the extra height is what the banner and the replay rows
        // need when they are all present — with the applied diff open as well — and it
        // costs a taller empty window otherwise.
        Height = 620;
        MinWidth = 460;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.Black);

        TextBlock caption = new()
        {
            Text = T("FormResolveConflicts/label1.Text", "Unresolved merge conflicts"),
            Foreground = text,
            Margin = new Thickness(0, 0, 0, 4),
        };

        // The DataGridView of the original is a single visible column; a header
        // strip over a ListBox is the same information without pulling in a grid.
        _header = new TextBlock
        {
            Text = T("FormResolveConflicts/FileName.HeaderText", "Filename"),
            Foreground = text,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(8, 4),
        };
        Border headerBar = new()
        {
            Background = Brush("App.PanelAlt", Brushes.DimGray),
            BorderBrush = border,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Child = _header,
        };

        _files = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = text,
            BorderBrush = border,
            BorderThickness = new Thickness(1),

            // The list is the point of the window: whatever else opens below it (the
            // rerere diff, the replay note) may take the rest, never all of it.
            MinHeight = 96,
        };
        _files.SelectionChanged += (_, _) => OnSelectionChanged();
        _files.DoubleTapped += (_, _) => OpenSelectedInMergeTool();

        Grid listArea = new()
        {
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(1, GridUnitType.Star)),
            },
        };
        Grid.SetRow(headerBar, 0);
        Grid.SetRow(_files, 1);
        listArea.Children.Add(headerBar);
        listArea.Children.Add(_files);

        _openInTool = ColumnButton(OpenInToolCaption(), () => OpenSelectedInMergeTool());
        _startMergetool = ColumnButton(
            T("FormResolveConflicts/startMergetool.Text", "Start mergetool"),
            StartMergetoolForAll);
        _rescan = ColumnButton(
            T("FormResolveConflicts/Rescan.Text", "Rescan merge conflicts"),
            () => _ = ReloadAsync());
        _reset = ColumnButton(T("FormResolveConflicts/Reset.Text", "Reset"), () => _ = ResetAsync());

        StackPanel buttonColumn = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(8, 0, 0, 0),
            Children = { _openInTool, _startMergetool, _rescan, _reset },
        };

        // Information strip: icon + description + Merge, exactly upstream's
        // tableLayoutPanel3 (pictureBox1 / conflictDescription / merge).
        _description = new TextBlock
        {
            Text = T("FormResolveConflicts/conflictDescription.Text", "Select file"),
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        _merge = new Button
        {
            MinWidth = 130,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = IconText.Header("Merge", MergeCaption),
        };

        // The Merge button opens the port's OWN editor (MergeToolWindow), not the
        // external tool: it is the one action that works on a machine with nothing
        // installed. The external tool keeps both of its buttons in the column on
        // the right, so nothing is taken away from a user who prefers kdiff3.
        _merge.Click += (_, _) => _ = MergeSelectedHereAsync();

        Grid infoRow = new()
        {
            Margin = new Thickness(0, 10, 0, 0),
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Control infoIcon = IconLoader.Image("information") as Control
            ?? new TextBlock { Text = "i", Foreground = dim };
        infoIcon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(infoIcon, 0);
        Grid.SetColumn(_description, 1);
        Grid.SetColumn(_merge, 2);
        infoRow.Children.Add(infoIcon);
        infoRow.Children.Add(_description);
        infoRow.Children.Add(_merge);

        // The three side rows. The "(ours)" / "(theirs)" suffix is appended at
        // runtime because a rebase swaps the two (upstream :256-279).
        _labelOurs = SideLabel(SuffixedLabel(
            T("FormResolveConflicts/labelLocalCurrent.Text", "Local/current"),
            _inRebase ? TheirsWord : OursWord));
        TextBlock labelBase = SideLabel(T("FormResolveConflicts/labelBase.Text", "Base"));
        _labelTheirs = SideLabel(SuffixedLabel(
            T("FormResolveConflicts/labelRemoteIncoming.Text", "Remote/incoming"),
            _inRebase ? OursWord : TheirsWord));

        ToolTip.SetTip(_labelOurs, _inRebase
            ? T("FormResolveConflicts/_changesLocalRebaseTooltip.Text", "Changes from the branch you are rebasing onto")
            : T("FormResolveConflicts/_changesLocalMergeTooltip.Text", "Changes from the current branch"));
        ToolTip.SetTip(_labelTheirs, _inRebase
            ? T("FormResolveConflicts/_changesRemoteRebaseTooltip.Text", "Changes from the branch you are rebasing")
            : T("FormResolveConflicts/_changesRemoteMergeTooltip.Text", "Changes from the branch you are merging"));

        _ourName = SideValue();
        _baseName = SideValue();
        _theirName = SideValue();

        Grid sides = new()
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
        };
        AddAt(sides, _labelOurs, 0, 0);
        AddAt(sides, _ourName, 0, 1);
        AddAt(sides, labelBase, 1, 0);
        AddAt(sides, _baseName, 1, 1);
        AddAt(sides, _labelTheirs, 2, 0);
        AddAt(sides, _theirName, 2, 1);

        // ---- guided refusal --------------------------------------------------
        // Built once and kept hidden. Its buttons do not implement anything of their
        // own: they call the very actions the context menu calls, so a side kept from
        // here and a side kept from there cannot drift apart.
        _guidedWhy = new TextBlock
        {
            Foreground = text,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _guidedNote = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Metrics.Text.Caption,
            Margin = new Thickness(0, Metrics.Space.Xs, 0, 0),
        };

        _guidedOurs = new Button { MinWidth = 150, Margin = new Thickness(0, Metrics.Space.Sm, Metrics.Space.Sm, 0) };
        _guidedOurs.Click += (_, _) =>
        {
            HideGuided();
            _ = ChooseSideAsync(ConflictChoice.Ours);
        };

        _guidedTheirs = new Button { MinWidth = 150, Margin = new Thickness(0, Metrics.Space.Sm, Metrics.Space.Sm, 0) };
        _guidedTheirs.Click += (_, _) =>
        {
            HideGuided();
            _ = ChooseSideAsync(ConflictChoice.Theirs);
        };

        _guidedImages = new Button
        {
            IsVisible = false,
            Margin = new Thickness(0, Metrics.Space.Sm, Metrics.Space.Sm, 0),
            Content = new TextBlock { Text = T("Compare them as pictures…") },
        };
        ToolTip.SetTip(_guidedImages, T(
            "Opens the two versions side by side, one over the other, and as a map of the pixels "
            + "that differ — the only way to decide between two images."));
        _guidedImages.Click += (_, _) => _ = CompareImagesAsync();

        _guidedSubmodule = new Button
        {
            IsVisible = false,
            Margin = new Thickness(0, Metrics.Space.Sm, Metrics.Space.Sm, 0),
            Content = new TextBlock { Text = ChooseCommitCaption },
        };
        _guidedSubmodule.Click += (_, _) => _ = ChooseSubmoduleCommitAsync();

        // Wrapping and not a row: the two side buttons carry a size and a date, which
        // is the whole point of them, and on a narrow window a row would push the
        // second one off the edge — measured at 720px, the default width.
        WrapPanel guidedButtons = new()
        {
            Orientation = Orientation.Horizontal,
            Children = { _guidedOurs, _guidedTheirs, _guidedImages, _guidedSubmodule },
        };

        _guided = new Border
        {
            Background = Brush("App.PanelAlt", Brushes.DimGray),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.Radius.MdCorner,
            Padding = new Thickness(Metrics.Space.Md, Metrics.Space.Sm),
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
            IsVisible = false,
            Child = new StackPanel { Children = { _guidedWhy, _guidedNote, guidedButtons } },
        };

        // ---- rerere ---------------------------------------------------------
        // The banner exists ONLY while rerere is active, and it says which of the two
        // ways it got there. The second one is the reason this whole block exists: an
        // <git-dir>/rr-cache directory left behind by a past experiment turns rerere on
        // with NOTHING in the configuration to point at, so git rewrites the user's
        // conflicts and no view in any client explains why.
        _rerereBannerTitle = new TextBlock
        {
            Foreground = text,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _rerereBannerDetail = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Metrics.Text.Caption,
            Margin = new Thickness(0, Metrics.Space.Xs, 0, 0),
        };
        _rerereBanner = new Border
        {
            Background = Brush("App.PanelAlt", Brushes.DimGray),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.Radius.MdCorner,
            Padding = new Thickness(Metrics.Space.Md, Metrics.Space.Sm),
            Margin = new Thickness(0, 0, 0, Metrics.Space.Sm),
            Child = new StackPanel { Children = { _rerereBannerTitle, _rerereBannerDetail } },
        };

        // The switches live OUTSIDE the banner, in a strip that is always there: when
        // rerere is off there is no banner to hang them on, and "how do I turn this on"
        // is exactly the question a user who just lost an afternoon to a rebase has.
        _rerereEnabled = new CheckBox
        {
            Content = new TextBlock { Text = T("Reuse recorded conflict resolutions (rerere)") },
            Foreground = text,
        };
        // Deliberately says "the same conflict" and then spells out what that means.
        // Measured on git 2.43: rebasing three commits that each rewrite the SAME line
        // replays nothing, because after the first step your resolution becomes the new
        // "ours" and every later step is a conflict git has never seen. What does get
        // replayed is the same conflict recurring across commits — the same hunk hit by
        // several commits of the series, the same edit repeated over many files, or the
        // whole rebase run a second time after an abort. Promising "resolve it once" for
        // any long rebase would be a promise git does not keep.
        ToolTip.SetTip(_rerereEnabled, T(
            "git remembers how you resolve a conflict and replays that resolution wherever the "
            + "identical conflict turns up again — later commits of a rebase, other files with the "
            + "same clash, or the same rebase run again after an abort. A conflict whose two sides "
            + "are not exactly the ones already recorded is still presented to you."));
        _rerereEnabled.Click += (_, _) => _ = SetRerereEnabledAsync(_rerereEnabled.IsChecked == true);

        _rerereAutoUpdate = new CheckBox
        {
            Content = new TextBlock { Text = T("Stage replayed resolutions automatically") },
            Foreground = text,
        };
        ToolTip.SetTip(_rerereAutoUpdate, T(
            "rerere.autoupdate. Off, a replayed resolution is written into the file but left "
            + "unmerged, so you still have to look at it before staging — that review is the last "
            + "moment a wrongly remembered resolution can be caught. On, it is staged for you; on a "
            + "rebase that check is skipped once per commit, which is where it adds up."));
        _rerereAutoUpdate.Click += (_, _) => _ = SetRerereAutoUpdateAsync(_rerereAutoUpdate.IsChecked == true);

        _rerereCache = new Button
        {
            Content = new TextBlock { Text = T("Recorded resolutions…") },
        };
        ToolTip.SetTip(_rerereCache, T("Inspect what rerere has stored for this repository."));
        _rerereCache.Click += (_, _) => _ = ShowRerereCacheAsync();

        StackPanel rerereStrip = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Md,
            Margin = new Thickness(0, 0, 0, Metrics.Space.Sm),
            Children = { _rerereEnabled, _rerereAutoUpdate, _rerereCache },
        };

        // The gain, spelled out: these are the files the user does NOT have to resolve.
        // Nothing else in the app would ever mention them — they simply are not in the
        // conflict list any more, which is precisely how a wrong replay slips through.
        _rerereReplayed = new TextBlock
        {
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
            IsVisible = false,
        };

        _rerereDiffText = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = Metrics.Text.Body,

            // A diff that shows two lines is a diff nobody reads: the box asks for a
            // hunk's worth of height and takes more when the window has it to give.
            MinHeight = 120,
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = text,
        };
        _rerereDiff = new Expander
        {
            Header = T("Show what rerere applied"),

            // Without both of these the Expander shrinks to its header and the diff is
            // read through a 200px window while the dialog has 700 to give.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, Metrics.Space.Xs, 0, 0),
            IsVisible = false,
            Content = _rerereDiffText,
        };

        _status = new TextBlock
        {
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        // Help: a real link, opened through xdg-open like the rest of the port.
        StackPanel helpContent = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (IconLoader.Image("GotoManual") is { } helpIcon)
        {
            helpIcon.VerticalAlignment = VerticalAlignment.Center;
            helpContent.Children.Add(helpIcon);
        }

        helpContent.Children.Add(new TextBlock
        {
            Text = T("Help"),
            // App.Link is the text-grade blue; App.Accent is tuned as a fill and only
            // reaches 3.40:1 on App.Panel in classic dark. Fallback is modern-dark
            // App.Link so a missing key cannot silently restore the fill-grade colour.
            Foreground = Brush("App.Link", new SolidColorBrush(Color.Parse("#5B9CFF"))),
            TextDecorations = TextDecorations.Underline,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Button help = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = helpContent,
        };
        ToolTip.SetTip(help, string.Format(
            T("GotoUserManualControl/_gotoUserManualControlTooltip.Text", "Read more about this feature at {0}"),
            HelpUrl));
        help.Click += (_, _) => _ = OpenHelpAsync();

        Grid main = new()
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Grid.SetColumn(listArea, 0);
        Grid.SetColumn(buttonColumn, 1);
        main.Children.Add(listArea);
        main.Children.Add(buttonColumn);

        // A file staged with its markers still in it disappears from the list above —
        // the list is ls-files --unmerged — so the merge looks finished while the next
        // commit would carry "<<<<<<< HEAD" into history (measured: git commits it
        // without a word). Saying it here is the whole point; the button is the way
        // back that git has and the UI did not offer.
        _markerText = new TextBlock
        {
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _markerReopen = new Button
        {
            Content = T("Reopen conflict"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = T("Put the file back into conflict (git checkout --merge) so any merge tool can open it again"),
        };
        _markerReopen.Click += (_, _) => _ = ReopenMarkedAsync();

        Grid markerRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_markerText, 0);
        Grid.SetColumn(_markerReopen, 1);
        markerRow.Children.Add(_markerText);
        markerRow.Children.Add(_markerReopen);

        _markerBanner = new Border
        {
            Background = Brush("App.RepoStateDirty", Brushes.Orange),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false,
            Child = markerRow,
        };

        StackPanel topBanners = new() { Children = { _markerBanner, _rerereBanner } };

        Grid root = new()
        {
            Margin = new Thickness(12),

            // Only the conflict list grows; every rerere row is Auto and collapses to
            // nothing when it has nothing to say, so a repository without rerere sees
            // exactly the window it saw before.
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
        };

        // Expanding the diff must not eat the conflict list. An Auto row would take
        // whatever the diff asks for and squeeze the star row above it to nothing —
        // measured: the list collapsed to a sliver and the information strip landed on
        // top of its header. So the diff row becomes a star row of its own while it is
        // open, and the two share what is left.
        _diffRow = root.RowDefinitions[7];
        _rerereDiff.Expanded += (_, _) => _diffRow.Height = new GridLength(1, GridUnitType.Star);
        _rerereDiff.Collapsed += (_, _) => _diffRow.Height = GridLength.Auto;
        Grid.SetRow(topBanners, 0);
        Grid.SetRow(rerereStrip, 1);
        Grid.SetRow(caption, 2);
        Grid.SetRow(main, 3);
        Grid.SetRow(infoRow, 4);

        // Directly under the information strip and the Merge button that produced it:
        // the answer to "why did nothing open?" has to be where the eye already is.
        Grid.SetRow(_guided, 5);
        Grid.SetRow(_rerereReplayed, 6);
        Grid.SetRow(_rerereDiff, 7);
        Grid.SetRow(sides, 8);
        Grid.SetRow(_status, 9);
        Grid.SetRow(help, 10);
        help.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(topBanners);
        root.Children.Add(rerereStrip);
        root.Children.Add(caption);
        root.Children.Add(main);
        root.Children.Add(infoRow);
        root.Children.Add(_guided);
        root.Children.Add(_rerereReplayed);
        root.Children.Add(_rerereDiff);
        root.Children.Add(sides);
        root.Children.Add(_status);
        root.Children.Add(help);

        Content = root;

        ApplyRerereState();
        BuildContextMenu();
        BindRows();
        if (_conflicts.Count > 0)
        {
            _files.SelectedIndex = 0;
        }

        OnSelectionChanged();
        ReportMergeToolState();

        DialogKeys.InstallEscapeClose(this);

        // BOTH strategies, deliberately. Tunnel alone never fired: with focus on the
        // list there is a route to tunnel down, but on a freshly opened window the
        // focused element is the window itself and only the bubbling pass runs
        // (measured headlessly — Escape worked, B/L/R/M did not). Tunnel is still
        // needed for the opposite case, where the focused ListBox would otherwise
        // swallow bare letters as type-to-search.
        // Bubbling on the window, handledEventsToo so a focused ListBox that claimed
        // the key for type-to-search cannot swallow the shortcut. Registered ONCE:
        // adding a second subscription on the list made the handler run twice per
        // press, which would apply a resolution twice.
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // Give the list keyboard focus, as upstream's grid has it: without this the
        // arrow keys do nothing until the user tabs into the list.
        Opened += (_, _) => _files.Focus();
    }

    /// <summary>
    ///  Shows the dialog modally over <paramref name="owner"/> and returns
    ///  <see langword="true"/> when every conflict was resolved, so the caller can
    ///  chain the commit dialog.
    ///
    ///  <para>The three blocking reads this needs (the unmerged index, the merge
    ///  tool name, and whether a rebase is in progress) are done here, on a
    ///  thread-pool thread, <b>before</b> the window exists: the services block on
    ///  async work and calling them from the UI thread deadlocks (the
    ///  <c>PushDialog</c> lesson).</para>
    /// </summary>
    public static async Task<bool> ShowAsync(Window owner, string repoPath)
    {
        ConflictService service = new();
        RerereService rerere = new();

        // The rerere snapshot rides along in the SAME background hop: it is five git
        // processes and it must be in hand before the window is built, or the banner
        // would appear a beat after the dialog and read as a change of state.
        (IReadOnlyList<ConflictEntry> conflicts,
         string? tool,
         bool inRebase,
         RerereOperation operation,
         RerereSnapshot rerereState,
         IReadOnlyList<string> replayed) = await Task.Run(() =>
        {
            IReadOnlyList<ConflictEntry> entries = service.ListConflicts(repoPath);
            RerereSnapshot snapshot = rerere.GetSnapshot(repoPath);
            return (
                entries,
                service.GetMergeToolName(repoPath),
                service.InTheMiddleOfRebase(repoPath),
                rerere.GetOperation(repoPath),
                snapshot,
                ScanReplayed(repoPath, snapshot, entries));
        });

        ResolveConflictsDialog dialog = new(repoPath, conflicts, tool, inRebase, operation, rerereState, replayed);
        await dialog.ShowDialog(owner);
        return dialog.AllConflictsResolved;
    }

    /// <summary>
    ///  True when the repository currently has unmerged entries — the cheap check a
    ///  caller makes before offering "solve conflicts now?". Blocking: call it from
    ///  <see cref="Task.Run"/>.
    /// </summary>
    public static bool HasConflicts(string repoPath)
        => new ConflictService().InTheMiddleOfConflictedMerge(repoPath);

    // ---- rows and selection --------------------------------------------------

    private void BindRows()
    {
        // A NEW list every time: re-assigning the same instance leaves realised
        // containers showing stale visuals (the M50 virtualisation trap).
        _files.ItemsSource = _conflicts.Select(c => c.Path).ToList();
    }

    private List<ConflictEntry> SelectedEntries()
    {
        List<ConflictEntry> selected = [];
        foreach (object? item in _files.SelectedItems ?? (System.Collections.IList)Array.Empty<object>())
        {
            if (item is string path)
            {
                ConflictEntry? entry = _conflicts.FirstOrDefault(c => c.Path == path);
                if (entry is not null)
                {
                    selected.Add(entry);
                }
            }
        }

        return selected;
    }

    private ConflictEntry? SingleSelection()
    {
        List<ConflictEntry> selected = SelectedEntries();
        return selected.Count == 1 ? selected[0] : null;
    }

    private void OnSelectionChanged()
    {
        List<ConflictEntry> selected = SelectedEntries();
        ConflictEntry? single = selected.Count == 1 ? selected[0] : null;

        // "Open in <tool>" / "Merge" act on one file, exactly as upstream disables
        // them for a multi-selection (SetAvailableCommands, :824-830).
        // A submodule pointer has no text to merge: what conflicts is which COMMIT the
        // superproject records, so the merge tool has nothing to open and the answer is
        // always one of the two sides. Offering the tool would open it on an empty file.
        bool submodule = single is { IsSubmodule: true };
        bool mergeable = single is not null && !submodule;
        bool toolUsable = _mergeTool is not null && !_busy;
        _openInTool.IsEnabled = toolUsable && mergeable;

        // The built-in editor needs no configured tool, so it stays available when
        // merge.tool is unset — which on a fresh Linux box is the normal case, and
        // was until now the case where this window could do nothing but pick sides.
        //
        // For a submodule the same button leads somewhere else, and this is the whole
        // of what changes for gitlinks: the action is still "settle this conflict",
        // but the unit is a commit rather than a line, so it opens the commit chooser
        // instead of the text editor. The caption says so — a button that reads
        // "Merge" and opens a list of commits would be a second surprise.
        //
        // Enabled for ANY single selection, including the cases it will refuse. It used
        // to be greyed out for them, which is how a user ends up staring at a dead
        // button with no idea what to do instead: the refusal now carries the ways out,
        // so pressing it is always worth something.
        _merge.IsEnabled = !_busy && single is not null;
        _merge.Content = IconText.Header("Merge", submodule ? ChooseCommitCaption : MergeCaption);
        ToolTip.SetTip(_merge, submodule ? ChooseCommitTooltip : null);
        _ctxMergeHere.Header = submodule ? ChooseCommitCaption : MergeHereCaption;
        ToolTip.SetTip(_ctxMergeHere, submodule
            ? ChooseCommitTooltip
            : T("Open the built-in three-way merge editor"));

        // The guided panel describes ONE file. Moving the selection makes it stale, and
        // a stale panel offering "keep LOCAL" is a way to resolve the wrong path.
        if (single?.Path != _refusedPath)
        {
            HideGuided();
        }
        _startMergetool.IsEnabled = toolUsable && _conflicts.Any(e => !e.IsSubmodule);
        _rescan.IsEnabled = !_busy;
        _reset.IsEnabled = !_busy;

        _ctxMergeHere.IsEnabled = _merge.IsEnabled;
        _ctxOpenInTool.IsEnabled = _openInTool.IsEnabled;
        _ctxMarkResolved.IsEnabled = !_busy && selected.Count > 0;
        _ctxChooseOurs.IsEnabled = !_busy && selected.Count > 0;
        _ctxChooseTheirs.IsEnabled = !_busy && selected.Count > 0;

        // "Choose base" only when every selected conflict actually has a stage 1;
        // for an add/add there is nothing to revert to.
        _ctxChooseBase.IsEnabled = !_busy && selected.Count > 0 && selected.All(e => e.Base.Exists);

        // Forget is offered only while the index is unmerged — which covers a merge and
        // a stopped rebase alike, since _conflicts is filled from the unmerged index and
        // says nothing about which operation put it there. With nothing conflicted in
        // flight git accepts the command, reports success, and the resolution comes
        // straight back — the work tree still holds the resolved text and rerere
        // re-records it. An action that silently undoes itself must not be reachable.
        _ctxForget.IsEnabled = !_busy
            && single is not null
            && _conflicts.Count > 0
            && _rerereState.Configuration.IsActive;

        _rerereEnabled.IsEnabled = !_busy;
        _rerereAutoUpdate.IsEnabled = !_busy && _rerereState.Configuration.IsActive;
        _rerereCache.IsEnabled = !_busy;

        bool onDisk = single is not null && File.Exists(Path.Combine(_repoPath, single.Path));
        _ctxOpen.IsEnabled = onDisk;
        _ctxShowInFolder.IsEnabled = onDisk;

        if (single is null)
        {
            // Upstream clears the three names on any non-single selection (:798)
            // and leaves the description alone; keeping the description would be
            // misleading here, so it goes back to the prompt.
            _ourName.Text = string.Empty;
            _baseName.Text = string.Empty;
            _theirName.Text = string.Empty;
            _description.Text = selected.Count > 1
                ? T("Several files selected. Choose a side, or mark them resolved, from the right-click menu.")
                : T("FormResolveConflicts/conflictDescription.Text", "Select file");
            return;
        }

        _description.Text = Describe(single);

        string deleted = T("FormResolveConflicts/_deleted.Text", "deleted");

        // For a submodule the three "names" are all the same path, which says nothing.
        // The commit each side points at is the whole conflict, so that is what is shown.
        if (single.IsSubmodule)
        {
            string none = T("FormResolveConflicts/_noBase.Text", "no base");
            _ourName.Text = single.Ours.ShortSha ?? deleted;
            _baseName.Text = single.Base.ShortSha ?? none;
            _theirName.Text = single.Theirs.ShortSha ?? deleted;
            return;
        }

        _ourName.Text = single.Ours.Exists ? single.Ours.Path : deleted;
        _baseName.Text = single.Base.Exists
            ? single.Base.Path
            : T("FormResolveConflicts/_noBase.Text", "no base");
        _theirName.Text = single.Theirs.Exists ? single.Theirs.Path : deleted;
    }

    /// <summary>
    ///  The information-box text for a conflict, keyed on the stage triple.
    ///  The first four cases are upstream's literal strings
    ///  (<c>FormResolveConflicts.cs:44-51</c>, selected at <c>:856-862</c>); the
    ///  last two have no upstream equivalent because upstream's switch falls
    ///  through and keeps whatever the label said before.
    /// </summary>
    private string Describe(ConflictEntry entry)
    {
        // Rebase swaps which git side is the "local" one (upstream :782-784).
        string local = _inRebase ? TheirsWord : OursWord;
        string remote = _inRebase ? OursWord : TheirsWord;

        // A submodule conflict is not "the file changed on both sides": nothing about
        // the superproject's content is in dispute, only which commit of the submodule
        // it should record. Upstream says none of this — its switch falls through to
        // whatever the label said before — and the wording matters here, because the
        // right move is to look at the two commits and pick, not to merge anything.
        if (entry.IsSubmodule)
        {
            return string.Format(
                // Points at the chooser first and at the two sides second, because the
                // sides are the weaker answer: they cannot express a commit that
                // contains both, which is what the right answer usually is.
                T("The submodule \"{0}\" points at different commits locally ({1}: {2}) and remotely ({3}: {4}). "
                  + "Use \"Choose the submodule commit…\" to see what lies between the two and pick one — or "
                  + "keep a side from the right-click menu. The submodule is checked out to match."),
                entry.Path,
                local,
                entry.Ours.ShortSha ?? T("FormResolveConflicts/_deleted.Text", "deleted"),
                remote,
                entry.Theirs.ShortSha ?? T("FormResolveConflicts/_deleted.Text", "deleted"));
        }

        return entry.Kind switch
        {
            ConflictKind.BothModified => string.Format(
                T("FormResolveConflicts/_fileChangeLocallyAndRemotely.Text",
                  "The file has been changed both locally ({0}) and remotely ({1}). Merge the changes."),
                local, remote),

            ConflictKind.BothAdded => string.Format(
                T("FormResolveConflicts/_fileCreatedLocallyAndRemotely.Text",
                  "A file with the same name has been created locally ({0}) and remotely ({1}). "
                  + "Choose the file you want to keep or merge the files."),
                local, remote),

            ConflictKind.DeletedByUs => string.Format(
                T("FormResolveConflicts/_fileDeletedLocallyAndModifiedRemotely.Text",
                  "The file has been deleted locally ({0}) and modified remotely ({1}). "
                  + "Choose to delete the file or keep the modified version."),
                local, remote),

            ConflictKind.DeletedByThem => string.Format(
                T("FormResolveConflicts/_fileModifiedLocallyAndDeletedRemotely.Text",
                  "The file has been modified locally ({0}) and deleted remotely ({1}). "
                  + "Choose to delete the file or keep the modified version."),
                local, remote),

            ConflictKind.AddedByUs => string.Format(
                T("The file exists only locally ({0}): there is no base revision and no remote version. "
                  + "Choose to keep it or delete it."),
                local),

            _ => string.Format(
                T("The file exists only remotely ({0}): there is no base revision and no local version. "
                  + "Choose to keep it or delete it."),
                remote),
        };
    }

    // ---- context menu --------------------------------------------------------

    private void BuildContextMenu()
    {
        _ctxMergeHere.Header = MergeHereCaption;
        ToolTip.SetTip(_ctxMergeHere, T("Open the built-in three-way merge editor"));
        _ctxMergeHere.InputGesture = new KeyGesture(Key.M);
        _ctxMergeHere.Click += (_, _) => _ = MergeSelectedHereAsync();

        _ctxOpenInTool.Header = OpenInToolCaption();
        _ctxOpenInTool.Click += (_, _) => OpenSelectedInMergeTool();

        _ctxMarkResolved.Header = T("FormResolveConflicts/ContextMarkAsSolved.Text", "Mark conflict as solved");
        _ctxMarkResolved.Click += (_, _) => _ = MarkSelectedResolvedAsync();

        _ctxChooseOurs.Header = _inRebase
            ? T("FormResolveConflicts/_contextChooseLocalRebaseText.Text", "Choose local/current (theirs)")
            : T("FormResolveConflicts/_contextChooseLocalMergeText.Text", "Choose local/current (ours)");
        ToolTip.SetTip(_ctxChooseOurs, _inRebase
            ? T("FormResolveConflicts/_changesTakeOnlyLocalRebaseTooltip.Text",
                "Take only the changes from the branch you are rebasing onto")
            : T("FormResolveConflicts/_changesTakeOnlyLocalMergeTooltip.Text",
                "Take only the changes from the current branch"));
        _ctxChooseOurs.InputGesture = new KeyGesture(Key.L);
        _ctxChooseOurs.Click += (_, _) => _ = ChooseSideAsync(ConflictChoice.Ours);

        _ctxChooseTheirs.Header = _inRebase
            ? T("FormResolveConflicts/_contextChooseRemoteRebaseText.Text", "Choose remote/incoming (ours)")
            : T("FormResolveConflicts/_contextChooseRemoteMergeText.Text", "Choose remote/incoming (theirs)");
        ToolTip.SetTip(_ctxChooseTheirs, _inRebase
            ? T("FormResolveConflicts/_changesTakeOnlyRemoteRebaseTooltip.Text",
                "Take only the changes from the branch you are rebasing")
            : T("FormResolveConflicts/_changesTakeOnlyRemoteMergeTooltip.Text",
                "Take only the changes from the branch you are merging"));
        _ctxChooseTheirs.InputGesture = new KeyGesture(Key.R);
        _ctxChooseTheirs.Click += (_, _) => _ = ChooseSideAsync(ConflictChoice.Theirs);

        _ctxChooseBase.Header = T("FormResolveConflicts/ContextChooseBase.Text", "Choose base");
        ToolTip.SetTip(_ctxChooseBase, T("FormResolveConflicts/_contextChooseBaseTooltip.Text",
            "Take no changes and revert to base content!"));
        _ctxChooseBase.InputGesture = new KeyGesture(Key.B);
        _ctxChooseBase.Click += (_, _) => _ = ChooseSideAsync(ConflictChoice.Base);

        _ctxForget.Header = T("Forget the recorded resolution…");
        // "conflict on the table" rather than "merge": this same dialog is what a
        // stopped rebase shows, and forget behaves there exactly as it does in a merge
        // (verified mid-rebase: postimage dropped, the path back in status/remaining,
        // MERGE_RR repopulated). Naming only merges would read as "not for you" to the
        // user who needs it most.
        ToolTip.SetTip(_ctxForget, T(
            "Drops what rerere remembers for this file and puts the original conflict markers "
            + "back into it. Offered only while there is a conflict on the table — a merge or a "
            + "stopped rebase: with none, the file still holds the resolved text and rerere "
            + "records it straight back."));
        _ctxForget.Click += (_, _) => _ = ForgetSelectedAsync();

        _ctxOpen.Header = T("FormResolveConflicts/openToolStripMenuItem.Text", "Open");
        _ctxOpen.Click += (_, _) => OpenWorkTreeFile();

        _ctxShowInFolder.Header = T("FormResolveConflicts/openFolderToolStripMenuItem.Text", "Show in folder");
        _ctxShowInFolder.Click += (_, _) => ShowWorkTreeFileInFolder();

        // Items are all in place before the menu can ever be shown: mutating them
        // inside Opening leaves the popup un-measured (a one-line sliver).
        _files.ContextMenu = new ContextMenu
        {
            ItemsSource = new List<Control>
            {
                _ctxMergeHere,
                _ctxOpenInTool,
                _ctxMarkResolved,
                new Separator(),
                _ctxChooseOurs,
                _ctxChooseTheirs,
                _ctxChooseBase,
                new Separator(),
                _ctxForget,
                new Separator(),
                _ctxOpen,
                _ctxShowInFolder,
            },
        };
    }

    // Upstream's hotkeys for FormMergeConflicts (HotkeySettingsManager.cs:343-348):
    // B/L/R choose a side, M merges, F5 rescans.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // handledEventsToo means already-handled presses arrive here too; a shortcut
        // this dialog has itself acted on must not be acted on a second time.
        if (e.Handled || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F5:
                _ = ReloadAsync();
                break;
            case Key.M:
                // The context menu advertises M on the item that, for a gitlink, is the
                // commit chooser; the key has to reach the same place the menu says it
                // does, or the shortcut quietly means something else on submodules.
                if (SingleSelection() is { IsSubmodule: true })
                {
                    _ = MergeSelectedHereAsync();
                }
                else
                {
                    OpenSelectedInMergeTool();
                }

                break;
            case Key.L:
                _ = ChooseSideAsync(ConflictChoice.Ours);
                break;
            case Key.R:
                _ = ChooseSideAsync(ConflictChoice.Theirs);
                break;
            case Key.B:
                _ = ChooseSideAsync(ConflictChoice.Base);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ---- actions -------------------------------------------------------------

    private void OpenSelectedInMergeTool()
    {
        if (_mergeTool is null)
        {
            ReportMergeToolState();
            return;
        }

        ConflictEntry? entry = SingleSelection();
        if (entry is null || _busy)
        {
            return;
        }

        // A merge tool needs three inputs. Without them git's mergetool would open
        // on a degenerate case, so route the user to the side actions instead of
        // launching something useless (upstream asks a modal question here).
        if (!entry.CanThreeWayMerge)
        {
            _status.Text = Describe(entry) + " "
                + T("Use the right-click menu to choose a side; there is no three-way merge to run.");
            return;
        }

        LaunchTool(entry.Path);
    }

    private void StartMergetoolForAll()
    {
        if (_mergeTool is null)
        {
            ReportMergeToolState();
            return;
        }

        LaunchTool(path: null);
    }

    // Detached launch: the window must stay usable while kdiff3 is up. The exit
    // callback arrives on a thread-pool thread, hence the Dispatcher hop.
    private void LaunchTool(string? path)
    {
        _status.Text = path is null
            ? string.Format(T("Starting {0} for all conflicted files…"), _mergeTool)
            : string.Format(T("Opening {0} in {1}…"), path, _mergeTool);

        _ = Task.Run(() =>
        {
            ConflictActionResult result = _service.LaunchMergetool(
                _repoPath,
                path,
                onExit: () => Dispatcher.UIThread.Post(() => _ = ReloadAsync()));

            Dispatcher.UIThread.Post(() => _status.Text = result.Message);
        });
    }

    private async Task ChooseSideAsync(ConflictChoice choice)
    {
        List<ConflictEntry> selected = SelectedEntries();
        if (selected.Count == 0 || _busy)
        {
            return;
        }

        if (choice == ConflictChoice.Base && selected.Any(e => !e.Base.Exists))
        {
            _status.Text = T("At least one of the selected files has no base revision.");
            return;
        }

        SetBusy(true);
        List<string> failures = [];
        try
        {
            failures = await Task.Run(() =>
            {
                List<string> errors = [];
                foreach (ConflictEntry entry in selected)
                {
                    ConflictActionResult result = _service.ChooseSide(_repoPath, entry, choice);
                    if (!result.Success)
                    {
                        errors.Add($"{entry.Path}: {result.Message.Trim()}");
                    }
                }

                return errors;
            });
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
        if (failures.Count > 0)
        {
            _status.Text = string.Format(FailedChoiceText(choice), string.Join("; ", failures));
        }
    }

    // Upstream's per-side failure captions (FormResolveConflicts.cs:88-90).
    private static string FailedChoiceText(ConflictChoice choice) => choice switch
    {
        ConflictChoice.Base => T("FormResolveConflicts/_chooseBaseFileFailedText.Text", "Choose base file failed.") + " {0}",
        ConflictChoice.Ours => T("FormResolveConflicts/_chooseLocalFileFailedText.Text", "Choose local file failed.") + " {0}",
        _ => T("FormResolveConflicts/_chooseRemoteFileFailedText.Text", "Choose remote file failed.") + " {0}",
    };

    /// <summary>
    ///  Opens the built-in three-way editor on the selected file. The window is
    ///  modal on this dialog: <c>git mergetool</c> is launched detached because an
    ///  external process must never freeze the app, but this editor <i>is</i> the
    ///  app, and letting the list be reset underneath it would be a way to lose an
    ///  edit rather than a convenience.
    ///
    ///  <para>The list rescans afterwards whether or not the file was saved: the
    ///  editor stages on save, so a resolved path has to disappear from the list,
    ///  and a cancelled merge costs one cheap <c>ls-files</c>.</para>
    /// </summary>
    private async Task MergeSelectedHereAsync()
    {
        ConflictEntry? entry = SingleSelection();
        if (entry is null || _busy)
        {
            return;
        }

        // A gitlink never reaches the text editor: its own chooser IS the merge.
        if (entry.IsSubmodule)
        {
            await ChooseSubmoduleCommitAsync();
            return;
        }

        // Ask BEFORE opening anything. The editor would refuse just as correctly, but
        // it would refuse from inside a window that then has to be closed, and the
        // refusal would arrive as a line of status text with no way forward attached.
        SetBusy(true);
        MergeRefusal? refusal;
        try
        {
            refusal = await _mergeService.InspectAsync(_repoPath, entry);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            SetBusy(false);
            return;
        }

        if (refusal is not null)
        {
            SetBusy(false);
            ShowGuided(entry, refusal);
            return;
        }

        HideGuided();
        try
        {
            MergeEditorOutcome outcome = await MergeToolWindow.ShowAsync(this, _repoPath, entry);
            _status.Text = outcome.Error
                ?? (outcome.Resolved
                    ? string.Format(T("{0} merged and staged."), entry.Path)

                    // Saved-but-unresolved is its own answer: the work is on disk and
                    // the file is still in conflict on purpose, so the sentence has to
                    // say both — otherwise it reads as "your edits went nowhere".
                    : outcome.Saved
                        ? string.Format(
                            T("{0} saved, still unresolved ({1} conflict(s) left) — kdiff3 and the other tools can still open it."),
                            entry.Path,
                            outcome.Left)
                        : string.Format(T("{0} was left unresolved."), entry.Path));
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
    }

    // ---- guided refusal ------------------------------------------------------

    /// <summary>
    ///  Turns a typed <see cref="MergeRefusal"/> into the panel: one line saying why a
    ///  line-by-line merge is impossible <i>for this file</i>, then the routes that do
    ///  work on it.
    ///
    ///  <para>Every route is an action that already exists elsewhere in this window —
    ///  the two side choices are the context menu's, the submodule route is the same
    ///  chooser the Merge button opens for a gitlink. Nothing here resolves anything by
    ///  itself: a second implementation of "keep ours" is a second thing that can be
    ///  wrong in a different way.</para>
    ///
    ///  <para>The sizes and dates are the reason the panel exists in this shape. "Keep
    ///  LOCAL or keep REMOTE" with nothing else on screen is a coin toss; "keep the
    ///  180 kB one from today or the 43 kB one from March" is a decision.</para>
    /// </summary>
    private void ShowGuided(ConflictEntry entry, MergeRefusal refusal)
    {
        _refusedPath = entry.Path;

        _guidedWhy.Text = entry.Path + " — " + refusal.Message;

        _guidedOurs.Content = SideButtonContent(_ctxChooseOurs.Header as string, refusal.Ours);
        _guidedTheirs.Content = SideButtonContent(_ctxChooseTheirs.Header as string, refusal.Theirs);

        // Offered on the sniffed format, never on the name: a screenshot committed as
        // "logo.dat" is still a PNG, and a "chart.png" produced by a build step often
        // is not one.
        _guidedImages.IsVisible = refusal.AnySideIsImage;

        // Normally not seen: a gitlink goes straight to the chooser from the Merge
        // button, which is the point of task A. It is here because this panel is what
        // a refusal turns into, and a refusal that says "submodule" without offering
        // the one thing that resolves a submodule would be a dead end.
        _guidedSubmodule.IsVisible = refusal.Reason == MergeRefusalReason.Submodule;

        List<string> notes = [];
        if (refusal.Reason == MergeRefusalReason.Submodule)
        {
            notes.Add(T("You are not limited to the two sides: the chooser also lists commits that "
                + "already contain both, which is usually the answer."));
        }

        if (_mergeTool is not null && refusal.Reason != MergeRefusalReason.Submodule)
        {
            // Said as an alternative and not as the answer: an external tool refuses
            // binary content just as often, and a user who has kdiff3 configured should
            // know it is still there without being sent to it first.
            notes.Add(string.Format(
                T("\"{0}\" is still available from the button on the right — {1} may be able to show "
                  + "this file even though the built-in editor cannot."),
                OpenInToolCaption(),
                _mergeTool));
        }

        _guidedNote.Text = string.Join(" ", notes);
        _guidedNote.IsVisible = notes.Count > 0;
        _guided.IsVisible = true;
    }

    private void HideGuided()
    {
        _guided.IsVisible = false;
        _refusedPath = null;
    }

    /// <summary>
    ///  The action on one line and the facts under it, dimmed. On one line the two
    ///  buttons are 400px wide each and the second falls off a 720px window; stacked,
    ///  the facts also read as what they are — a description, not a second command.
    /// </summary>
    private static Control SideButtonContent(string? caption, MergeSideFacts facts) => new StackPanel
    {
        Children =
        {
            new TextBlock { Text = caption ?? string.Empty },
            new TextBlock
            {
                Text = DescribeSide(facts),
                Foreground = Brush("App.TextDim", Brushes.Gray),
                FontSize = Metrics.Text.Caption,
            },
        },
    };

    /// <summary>Size, kind and age of one side, in a single readable clause.</summary>
    private static string DescribeSide(MergeSideFacts facts)
    {
        if (!facts.Exists)
        {
            return T("this side has no such file");
        }

        string what = $"{HumanSize(facts.Size)}, {facts.ContentType}";
        return facts.Date is { } when
            ? what + ", " + when.ToLocalTime().ToString("d MMM yyyy HH:mm")
            : what;
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => string.Format(T("{0} bytes"), bytes),
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} kB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>
    ///  Shows the two versions as pictures. The bytes come from the index stages, not
    ///  from the work tree: the work-tree file of a conflicted binary is whichever side
    ///  git happened to leave there, so it is one of the two at best.
    /// </summary>
    private async Task CompareImagesAsync()
    {
        ConflictEntry? entry = _conflicts.FirstOrDefault(c => c.Path == _refusedPath);
        if (entry is null || _busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            byte[]? ours = await _mergeService.ReadStageAsync(_repoPath, entry.Ours);
            byte[]? theirs = await _mergeService.ReadStageAsync(_repoPath, entry.Theirs);

            // A message comes back only when NEITHER side could be decoded; otherwise
            // the window was shown and there is nothing to report.
            string? error = await ImageDiffWindow.ShowAsync(
                this, ours, theirs, _labelOurs.Text ?? "LOCAL", _labelTheirs.Text ?? "REMOTE");
            if (error is not null)
            {
                _status.Text = error;
            }
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---- submodule -----------------------------------------------------------

    /// <summary>
    ///  The submodule route: show the two recorded commits with the history between
    ///  them, take the one the user picks, and write it into the index.
    ///
    ///  <para>Why this is the <b>main</b> way for a gitlink and not an extra. Choosing
    ///  a side blind is choosing a commit without having seen what is in it: the two
    ///  pointers usually differ by a handful of commits, one side often already
    ///  contains the other, and when they have genuinely diverged the right answer is
    ///  frequently a third commit that contains both — which "keep ours" and "keep
    ///  theirs" cannot express at all.</para>
    ///
    ///  <para>The list is rescanned afterwards exactly as every other action does:
    ///  <c>update-index --cacheinfo</c> clears the three stages, so the path leaves the
    ///  conflict list, and the window closes itself when it was the last one.</para>
    /// </summary>
    private async Task ChooseSubmoduleCommitAsync()
    {
        ConflictEntry? entry = SingleSelection();
        if (entry is null || _busy || !entry.IsSubmodule)
        {
            return;
        }

        HideGuided();
        SetBusy(true);
        try
        {
            SubmoduleConflictDialog chooser = new(_repoPath, entry);
            await chooser.ShowDialog(this);

            if (chooser.ChosenSha is not string sha)
            {
                // Closed without choosing: nothing was written, and saying so beats
                // leaving the previous action's message on screen.
                _status.Text = string.Format(T("{0} was left unresolved."), entry.Path);
                return;
            }

            ConflictActionResult result =
                await Task.Run(() => _submodules.ChooseCommit(_repoPath, entry.Path, sha));
            _status.Text = result.Message;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }

        // Outside the try: ReloadAsync may close the window, and it must not do so
        // while _busy is still set.
        await ReloadAsync();
    }

    private async Task MarkSelectedResolvedAsync()
    {
        List<ConflictEntry> selected = SelectedEntries();
        if (selected.Count == 0 || _busy)
        {
            return;
        }

        SetBusy(true);
        List<string> failures = [];
        try
        {
            failures = await Task.Run(() =>
            {
                List<string> errors = [];
                foreach (ConflictEntry entry in selected)
                {
                    ConflictActionResult result = _service.MarkResolved(_repoPath, entry.Path);
                    if (!result.Success)
                    {
                        errors.Add($"{entry.Path}: {result.Message.Trim()}");
                    }
                }

                return errors;
            });
        }
        catch (Exception ex)
        {
            failures.Add(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
        if (failures.Count > 0)
        {
            _status.Text = string.Format(
                T("FormResolveConflicts/_stageFilename.Text", "Stage '{0}'"),
                string.Join("; ", failures));
        }
    }

    /// <summary>
    ///  Upstream's <b>Reset</b>: two confirmations, then <c>git reset --hard</c> and
    ///  close (<c>FormResolveConflicts.cs:754-780</c>). Both prompts are kept — the
    ///  action throws away every change since the last commit.
    /// </summary>
    private async Task ResetAsync()
    {
        if (_busy)
        {
            return;
        }

        bool first = await ConfirmAsync(
            T("FormResolveConflicts/_abortCurrentOperation.Text",
              "You can abort the current conflict resolution by resetting hard.\n"
              + "All changes since the last commit will be deleted.\n\n"
              + "Do you want to reset the changes?"),
            T("FormResolveConflicts/_resetCaption.Text", "Reset"));
        if (!first)
        {
            return;
        }

        bool second = await ConfirmAsync(
            T("FormResolveConflicts/_areYouSureYouWantDeleteFiles.Text",
              "Are you sure you want to DELETE all changes?\n\nThis action cannot be made undone."),
            T("FormResolveConflicts/_areYouSureYouWantDeleteFilesCaption.Text", "WARNING!"));
        if (!second)
        {
            return;
        }

        SetBusy(true);
        ConflictActionResult result;
        try
        {
            result = await Task.Run(() => _service.ResetHard(_repoPath));
        }
        catch (Exception ex)
        {
            result = new ConflictActionResult(false, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        if (!result.Success)
        {
            _status.Text = result.Message;
            return;
        }

        AllConflictsResolved = true;
        Close();
    }

    /// <summary>
    ///  Re-reads the unmerged index (upstream's <b>Rescan merge conflicts</b>,
    ///  which is just <c>Initialize()</c>). Also the callback after the merge tool
    ///  exits, because <c>git mergetool</c> stages a successfully merged file
    ///  itself. When nothing is left the window closes and reports it, leaving the
    ///  "commit now?" decision to the caller.
    /// </summary>
    /// <summary>
    ///  Refreshes the "staged but still marked" banner from the index.
    /// </summary>
    private async Task RefreshMarkerBannerAsync()
    {
        IReadOnlyList<string> marked;
        try
        {
            marked = await Task.Run(() => _service.ListStagedWithMarkers(_repoPath));
        }
        catch (Exception)
        {
            // A banner is not worth failing a reload over.
            marked = [];
        }

        _markerFiles = marked;
        _markerBanner.IsVisible = marked.Count > 0;
        if (marked.Count == 0)
        {
            return;
        }

        // Names the files, up to a point: the banner has to fit, and the count carries
        // the rest.
        const int Shown = 3;
        string names = string.Join(", ", marked.Take(Shown));
        if (marked.Count > Shown)
        {
            names += string.Format(T(" and {0} more"), marked.Count - Shown);
        }

        _markerText.Text = string.Format(
            T("Marked resolved, but still contains conflict markers: {0}. Committing would carry them into history."),
            names);
        _markerReopen.Content = marked.Count == 1
            ? T("Reopen conflict")
            : string.Format(T("Reopen {0} conflicts"), marked.Count);
    }

    /// <summary>
    ///  Puts every file the banner names back into conflict, under a confirmation that
    ///  states the cost: <c>git checkout --merge</c> rewrites the work-tree file with
    ///  the markers, so a resolution saved there is lost (measured on git 2.43 — the
    ///  stages come back, the resolved text does not).
    /// </summary>
    private async Task ReopenMarkedAsync()
    {
        if (_markerFiles.Count == 0)
        {
            return;
        }

        bool confirmed = await ConfirmAsync(
            string.Format(
                T("Put {0} back into conflict?\n\nThe file(s) will be rewritten with the conflict markers of the "
                    + "original merge, so anything already resolved in them is lost. Every merge tool — this one, "
                    + "git mergetool, kdiff3 — can open them again afterwards."),
                _markerFiles.Count == 1 ? _markerFiles[0] : string.Format(T("{0} files"), _markerFiles.Count)),
            T("Reopen conflict"));

        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        List<string> failed = [];
        try
        {
            foreach (string path in _markerFiles)
            {
                ConflictActionResult result = await Task.Run(() => _service.ReopenConflict(_repoPath, path));
                if (!result.Success)
                {
                    failed.Add($"{path}: {result.Message.Trim()}");
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        _status.Text = failed.Count == 0
            ? string.Format(T("{0} file(s) put back into conflict."), _markerFiles.Count)
            : string.Join(Environment.NewLine, failed);

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        string? keep = SingleSelection()?.Path;

        IReadOnlyList<ConflictEntry> fresh;
        try
        {
            fresh = await Task.Run(() => _service.ListConflicts(_repoPath));
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }

        _conflicts = fresh;
        BindRows();

        // Asked on every reload, including the one that finds no conflicts left: that
        // is exactly the case this catches — nothing unmerged, and a marker still in
        // the index.
        await RefreshMarkerBannerAsync();

        if (_conflicts.Count == 0)
        {
            AllConflictsResolved = _markerFiles.Count == 0;
            if (_markerFiles.Count > 0)
            {
                // Not closing on a marker: closing here reports "you can commit", and
                // the one thing that must not happen is committing these.
                _status.Text = string.Format(
                    T("No file is unmerged, but {0} staged file(s) still contain conflict markers."),
                    _markerFiles.Count);
                return;
            }

            _status.Text = T("FormResolveConflicts/_allConflictsResolved.Text",
                "All merge conflicts are resolved, you can commit.");
            Close();
            return;
        }

        int index = keep is null ? 0 : _conflicts.ToList().FindIndex(c => c.Path == keep);
        _files.SelectedIndex = index >= 0 ? index : 0;
        OnSelectionChanged();

        // rerere's picture changes with every resolution: a merge tool run can hand a
        // path back to rerere, and a forget puts one back into the conflict. Only when
        // the window is staying open — the branch above closes it.
        await RefreshRerereAsync();
    }

    private void OpenWorkTreeFile()
    {
        ConflictEntry? entry = SingleSelection();
        if (entry is null)
        {
            return;
        }

        _ = RunExternalAsync(() => _externalTools.OpenPath(Path.Combine(_repoPath, entry.Path)));
    }

    private void ShowWorkTreeFileInFolder()
    {
        ConflictEntry? entry = SingleSelection();
        if (entry is null)
        {
            return;
        }

        _ = RunExternalAsync(() => _externalTools.ShowInFolder(Path.Combine(_repoPath, entry.Path)));
    }

    private async Task OpenHelpAsync() => await RunExternalAsync(() => _externalTools.OpenUrl(HelpUrl));

    private async Task RunExternalAsync(Func<ExternalToolResult> action)
    {
        ExternalToolResult result;
        try
        {
            result = await Task.Run(action);
        }
        catch (Exception ex)
        {
            result = new ExternalToolResult(false, ex.Message);
        }

        if (!result.Success)
        {
            _status.Text = result.Message;
        }
    }

    // ---- rerere --------------------------------------------------------------

    /// <summary>
    ///  Paints the whole rerere area from <see cref="_rerereState"/>.
    ///
    ///  <para>The banner is shown only when git will actually record and replay here,
    ///  and it distinguishes the two ways that can happen. <c>rerere.enabled=true</c> is
    ///  a decision somebody took; an <c>rr-cache</c> directory with the key unset is
    ///  <b>not</b> — git treats the directory itself as consent, so a repository where
    ///  someone once tried rerere keeps replaying resolutions with nothing in the
    ///  configuration to explain it. That is the case this banner exists for.</para>
    /// </summary>
    /// <summary>
    ///  The name of the operation in flight when it is <i>stepwise</i> — one that stops between
    ///  commits and can hit the same conflict again at the next <c>--continue</c> — and
    ///  <see langword="null"/> for a plain merge or for anything unrecognised, which is what
    ///  falls back to the "next time" wording.
    ///
    ///  <para><b>Measured, not assumed, for each of them on git 2.43.</b> Cherry-pick: the
    ///  conflict recorded (<c>Salvata preimmagine di 'f.txt'</c>) and, after the resolution was
    ///  committed, the same cherry-pick run again replayed it (<c>Risolto conflitto in 'f.txt'
    ///  usando la risoluzione precedente</c>) — and inside a single
    ///  <c>git cherry-pick master..topic</c>, the resolution taken at commit one was replayed at
    ///  commit two. Revert: identical, recorded then replayed. <c>git am -3</c>: identical,
    ///  recorded then replayed. Plain <c>git am</c> is deliberately absent from that list — it
    ///  fails with <c>.rej</c>-style errors, leaves nothing unmerged and never involves rerere,
    ///  so this window does not open for it in the first place.</para>
    /// </summary>
    private string? StepwiseOperationName() => _operation switch
    {
        RerereOperation.Rebase => T("rebase"),
        RerereOperation.CherryPick => T("cherry-pick"),
        RerereOperation.Revert => T("revert"),
        RerereOperation.ApplyMailbox => T("git am"),
        _ => null,
    };

    private void ApplyRerereState()
    {
        RerereConfiguration configuration = _rerereState.Configuration;
        _rerereBanner.IsVisible = configuration.IsActive;
        _rerereEnabled.IsChecked = configuration.IsActive;
        _rerereAutoUpdate.IsChecked = configuration.AutoUpdateEffective;

        // The cache is worth inspecting whenever there is one — including the case
        // where rerere is switched off and a cache full of old resolutions is sitting
        // there waiting for somebody to switch it back on.
        _rerereCache.IsVisible = configuration.IsActive || configuration.CacheDirectoryExists;

        if (configuration.IsActive)
        {
            _rerereBannerTitle.Text = configuration.Activation == RerereActivation.EnabledByCacheDirectory
                ? T("rerere is on because this repository has an rr-cache directory — nothing in your configuration turns it on.")
                : _operation == RerereOperation.Rebase
                    // During a rebase the sentence has to be about the commit, not about
                    // "next time": the replay is evaluated again at every --continue, and
                    // it fires only where the conflict comes back in the same shape.
                    ? T("rerere is on: git records how you resolve this commit's conflicts and replays them at "
                        + "each further step of the rebase where the same conflict comes back.")
                    : StepwiseOperationName() is string stepwise
                        // Same promise, same horizon, different word for the operation — a
                        // cherry-pick, a revert and an `am -3` stop between commits exactly as a
                        // rebase does. Measured: `git cherry-pick master..topic` recorded at the
                        // first commit and replayed at the second within the one run. Kept as a
                        // separate string rather than folded into the rebase one so that
                        // sentence, which was reviewed against its own measurements, is untouched.
                        // Both horizons, because unlike a rebase these are routinely
                        // single-commit: a lone `git revert` has no "further step", and its user
                        // is the one who cares that the resolution survives to the next run.
                        // Both were measured — replay at commit two of one cherry-pick run, and
                        // replay when the very same cherry-pick was run again afterwards.
                        ? string.Format(
                            T("rerere is on: git records how you resolve this commit's conflicts and replays them "
                              + "wherever the same conflict comes back — at a further step of this {0}, or the "
                              + "next time you run it."),
                            stepwise)
                        : T("rerere is on: git is recording how you resolve these conflicts and will replay it next time.");

            string autoUpdate = configuration.AutoUpdateEffective
                ? StepwiseOperationName() is string stepwiseAuto
                    // Measured on git 2.43: with autoupdate on, a replayed step is staged
                    // and the operation STILL stops on that commit — but with nothing unmerged
                    // left, so it never reaches this window at all and the only trace is a
                    // staged change nobody asked to see. Claiming "it never stops" would be
                    // wrong; saying nothing would hide the one step that is never reviewed.
                    // Re-measured for the sequencer: `git cherry-pick master..topic` with
                    // autoupdate on printed "'a.txt' aggiunto all'area di staging usando la
                    // risoluzione precedente", left `git ls-files -u` empty, and still stopped
                    // waiting for --continue. Identical behaviour, so one sentence covers all.
                    ? string.Format(
                        T("Replayed resolutions are staged for you (rerere.autoupdate). On a {0} that skips the "
                          + "review once per commit: a step resolved entirely by rerere leaves nothing unmerged, so "
                          + "it never opens this window and goes on with its resolution staged unseen."),
                        stepwiseAuto)
                    : T("Replayed resolutions are staged for you (rerere.autoupdate), so a replayed conflict never comes back for review.")
                : T("Replayed resolutions are written into the file but left unstaged, so you still get to check them before committing.");

            _rerereBannerDetail.Text = configuration.Activation == RerereActivation.EnabledByCacheDirectory
                ? string.Format(
                    T("git treats {0} as consent. Unticking the box below writes rerere.enabled=false, which is "
                      + "what it takes to stop it: simply removing the setting would leave the directory in charge. {1}"),
                    configuration.CacheDirectory ?? "rr-cache",
                    autoUpdate)
                : autoUpdate;
        }

        _rerereReplayed.IsVisible = _rerereReplayedPaths.Count > 0;
        if (_rerereReplayedPaths.Count > 0)
        {
            // Says "in this step" during any stepwise operation because that is the whole
            // of the promise: the replay covers the commit git is stopped on, and the next
            // --continue can stop again on the very same file.
            _rerereReplayed.Text = StepwiseOperationName() is string stepwise
                ? string.Format(
                    T("rerere has already done these for you in this step of the {0} — {1} — and no "
                      + "conflict markers are left in them. You do not have to redo that work, but review it "
                      + "before staging: the replay is silent, and a resolution remembered wrongly looks "
                      + "exactly like a clean merge."),
                    stepwise,
                    string.Join(", ", _rerereReplayedPaths))
                : string.Format(
                    T("rerere has already done these for you — {0} — and no conflict markers are left in them. "
                      + "You do not have to redo that work, but review it before staging: the replay is silent, "
                      + "and a resolution remembered wrongly looks exactly like a clean merge."),
                    string.Join(", ", _rerereReplayedPaths));
        }

        // Empty is normal and is NOT evidence that rerere did nothing (a completed
        // replay reports an empty diff), so the row simply disappears instead of
        // claiming anything.
        bool hasDiff = _rerereState.ReplayedDiff.Trim().Length > 0;
        _rerereDiff.IsVisible = hasDiff;
        if (hasDiff)
        {
            _rerereDiffText.Text = _rerereState.ReplayedDiff;
        }
        else
        {
            // An invisible star row still claims its share of the window, so the row
            // goes back to Auto whenever the diff disappears while it was open.
            _rerereDiff.IsExpanded = false;
            _diffRow.Height = GridLength.Auto;
        }
    }

    /// <summary>
    ///  The paths rerere has already resolved in the operation in progress — merge or
    ///  rebase, the answer is the same — which is harder to give than it looks.
    ///
    ///  <para><b>git stops saying so the moment it is done.</b> The documented answer is
    ///  "<c>rerere status</c> minus <c>rerere remaining</c>", and it works only while a
    ///  replay is partial. Measured on git 2.43 after a complete replay: <c>status</c>,
    ///  <c>remaining</c> and <c>diff</c> all empty <i>and</i> <c>MERGE_RR</c> truncated to
    ///  zero bytes, while the index was still unmerged and the work tree already held the
    ///  remembered resolution. Every git-side signal is gone precisely in the case the
    ///  user most needs to be told about — a file that will be committed without ever
    ///  having been looked at.</para>
    ///
    ///  <para><b>So the work tree is asked instead.</b> A path that is unmerged in the
    ///  index but carries no conflict markers has had a resolution written into it, and
    ///  with rerere active that is who wrote it. The wording in the banner states the
    ///  checked fact — no markers left — rather than claiming authorship, because a user
    ///  who hand-edited the file without staging it lands in the same state and the
    ///  advice ("review it before staging") is right either way. Only asked when rerere
    ///  is active, so a repository without rerere never sees this line.</para>
    ///
    ///  <para><b>And the eligibility filter is what stops it lying.</b> "unmerged and no
    ///  markers" is also true of conflicts rerere never touches, and those are not rare
    ///  in a rebase of real work. Measured on git 2.43, mid-rebase: a <b>binary</b>
    ///  content conflict sits at <c>UU</c> with git's own side in the work tree, not one
    ///  marker in it, and it is absent from <c>rerere remaining</c> — rerere ignores
    ///  binaries entirely. A <b>mode-only</b> add/add (same blob <c>100755</c> against
    ///  <c>100644</c>) is <c>AA</c> with perfectly clean text and is likewise absent from
    ///  both <c>status</c> and <c>remaining</c>. Without this filter both were announced
    ///  as "rerere has already done these for you", which is the worst kind of wrong: it
    ///  tells the user to stop worrying about a conflict nobody has resolved. So a path
    ///  only qualifies when rerere <i>could</i> have produced it — two existing text
    ///  sides whose content actually differs, no gitlink — and anything else is left in
    ///  the list where the user will meet it. (A modify/delete needs no rule: git does
    ///  report it in <c>remaining</c>, verified.)</para>
    /// </summary>
    private static IReadOnlyList<string> ScanReplayed(
        string repoPath,
        RerereSnapshot snapshot,
        IReadOnlyList<ConflictEntry> conflicts)
    {
        if (!snapshot.Configuration.IsActive)
        {
            return [];
        }

        HashSet<string> remaining = new(snapshot.RemainingPaths, StringComparer.Ordinal);
        List<string> replayed = [];
        foreach (ConflictEntry entry in conflicts)
        {
            // "remaining" is authoritative when it has something to say: git means
            // exactly "the user still has to open this one".
            if (remaining.Contains(entry.Path) || !CouldRerereHaveResolved(entry))
            {
                continue;
            }

            if (!LooksResolved(Path.Combine(repoPath, entry.Path)))
            {
                continue;
            }

            replayed.Add(entry.Path);
        }

        replayed.Sort(StringComparer.Ordinal);
        return replayed;
    }

    /// <summary>
    ///  Whether a conflict is of the kind rerere is even able to resolve, from the index
    ///  alone. rerere works on the text of a three-way content conflict: it has nothing
    ///  to say about a gitlink (the dispute is a commit id), about a side that does not
    ///  exist (modify/delete), or about a conflict where the two blobs are identical and
    ///  only the file mode differs — all three were observed unmerged and marker-free
    ///  mid-rebase, which is exactly the shape of a replayed resolution.
    /// </summary>
    private static bool CouldRerereHaveResolved(ConflictEntry entry)
        => !entry.IsSubmodule
           && entry.Ours.Exists
           && entry.Theirs.Exists
           && entry.Ours.Sha != entry.Theirs.Sha;

    /// <summary>
    ///  True when the file exists, is text, and holds no conflict markers. Read in
    ///  blocks and stopped at the first marker or the first NUL: a conflicted file can
    ///  be arbitrarily large and this runs for every path in the list.
    ///
    ///  <para>The NUL check is not decoration. A binary file trivially contains no
    ///  marker line, so a line-based reading calls every binary conflict "resolved" —
    ///  and git leaves binary conflicts in the work tree without markers by design. An
    ///  unreadable file answers false too, which is the safe direction: it only means
    ///  the banner stays quiet about that path.</para>
    /// </summary>
    private static bool LooksResolved(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            using FileStream stream = File.OpenRead(fullPath);
            using StreamReader reader = new(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is string line)
            {
                if (line.Contains('\0'))
                {
                    return false;
                }

                if (line.StartsWith("<<<<<<<", StringComparison.Ordinal)
                    || line.StartsWith(">>>>>>>", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task RefreshRerereAsync()
    {
        List<ConflictEntry> entries = [.. _conflicts];
        try
        {
            (_rerereState, _rerereReplayedPaths) = await Task.Run(() =>
            {
                RerereSnapshot snapshot = _rerere.GetSnapshot(_repoPath);
                return (snapshot, ScanReplayed(_repoPath, snapshot, entries));
            });
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }

        ApplyRerereState();
        OnSelectionChanged();
    }

    /// <summary>
    ///  Turns rerere on or off for this repository, always by writing an explicit
    ///  boolean and never by removing the key: with an <c>rr-cache</c> directory
    ///  present an unset <c>rerere.enabled</c> still means <i>on</i>, so an "off" that
    ///  unset the key would leave the tick box lying about the state of the repository.
    /// </summary>
    private async Task SetRerereEnabledAsync(bool enabled)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        RerereActionResult result;
        try
        {
            result = await Task.Run(() => _rerere.SetEnabled(_repoPath, enabled));
        }
        catch (Exception ex)
        {
            result = new RerereActionResult(false, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        _status.Text = result.Success
            ? (enabled
                ? T("rerere is on: resolutions recorded from now on will be replayed when the same conflict returns.")
                : T("rerere is off: git will no longer record or replay conflict resolutions here. What is already in the cache stays."))
            : result.Message;

        await RefreshRerereAsync();
    }

    private async Task SetRerereAutoUpdateAsync(bool autoUpdate)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        RerereActionResult result;
        try
        {
            result = await Task.Run(() => _rerere.SetAutoUpdate(_repoPath, autoUpdate));
        }
        catch (Exception ex)
        {
            result = new RerereActionResult(false, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        _status.Text = result.Success
            ? (autoUpdate
                ? T("Replayed resolutions will be staged automatically. That removes the one moment at which a wrong remembered resolution would still have been visible.")
                : T("Replayed resolutions will be left unstaged, so you can review them before committing."))
            : result.Message;

        await RefreshRerereAsync();
    }

    private async Task ShowRerereCacheAsync()
    {
        await RerereCacheWindow.ShowAsync(this, _repoPath);

        // The cache window can expire entries, which changes what will be replayed.
        await RefreshRerereAsync();
    }

    /// <summary>
    ///  The safety valve: drops the remembered resolution for the selected path and
    ///  restores the conflict as the merge — or the rebase step — produced it.
    ///
    ///  <para>Confirmed explicitly, because it throws away the current content of the
    ///  file, and re-checked afterwards against the cache: <c>git rerere forget</c> on a
    ///  path it knows nothing about exits 0 without a word, so a successful exit is not
    ///  evidence that anything was forgotten. Counting the cache before and after is.</para>
    /// </summary>
    private async Task ForgetSelectedAsync()
    {
        ConflictEntry? entry = SingleSelection();
        if (entry is null || _busy || _conflicts.Count == 0 || !_rerereState.Configuration.IsActive)
        {
            return;
        }

        bool confirmed = await ConfirmAsync(
            string.Format(
                T("Forget what rerere remembers about \"{0}\"?\n\n"
                  + "The stored resolution is dropped and the conflict is armed again, so it is presented "
                  + "for resolution instead of being replayed. git may also restore the conflict markers "
                  + "into the file, discarding the text that is in it now.\n\n"
                  + "Do this when the remembered resolution is WRONG: otherwise rerere keeps applying it to "
                  + "every future occurrence of this conflict."),
                entry.Path),
            T("Forget the recorded resolution"));
        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        (RerereActionResult result, int before, int after, bool markersBack) outcome;
        try
        {
            string path = entry.Path;
            outcome = await Task.Run(() =>
            {
                // Counting the stored resolutions before and after is the only way to
                // know whether anything happened: forget on a path rerere never heard of
                // exits 0 without printing a word.
                int before = _rerere.ListCache(_repoPath).Count(e => e.HasPostimage);
                RerereActionResult result = _rerere.Forget(_repoPath, path);
                int after = _rerere.ListCache(_repoPath).Count(e => e.HasPostimage);
                return (result, before, after, !LooksResolved(Path.Combine(_repoPath, path)));
            });
        }
        catch (Exception ex)
        {
            outcome = (new RerereActionResult(false, ex.Message), 0, 0, false);
        }
        finally
        {
            SetBusy(false);
        }

        if (!outcome.result.Success)
        {
            _status.Text = outcome.result.Message.Length > 0
                ? outcome.result.Message
                : string.Format(T("Could not forget the recorded resolution for {0}."), entry.Path);
        }
        else if (outcome.after < outcome.before)
        {
            // Whether the work tree is rewritten is git's business and it does not
            // always do it — measured on git 2.43: a replayed-but-unstaged path kept its
            // resolved text after forget, while the cache entry did go. Saying "the
            // markers are back" unconditionally would send the user to look for
            // something that is not there, so the file is checked and reported as found.
            _status.Text = string.Format(
                outcome.markersBack
                    ? T("Forgot the recorded resolution for {0}; the conflict markers are back in the file.")
                    : T("Forgot the recorded resolution for {0}. git left the file's current text alone — it "
                        + "still holds the resolution you can see — but the conflict is armed again and will "
                        + "no longer be replayed."),
                entry.Path);
        }
        else
        {
            // Nothing changed: there was no stored resolution for this path. Reporting
            // the success alone would have been a lie by omission — and git's exit code
            // is no help, measured mid-rebase it printed "no remembered resolution for
            // 'f.txt'" on stderr and still exited 0, so that line is carried through
            // rather than replaced by a guess.
            _status.Text = string.Format(
                T("Nothing left the cache: rerere had no recorded resolution for {0}. {1}"),
                entry.Path,
                outcome.result.Message).TrimEnd();
        }

        await ReloadAsync();
    }

    // ---- state ---------------------------------------------------------------

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnSelectionChanged();
    }

    // The caption carries the configured tool's name: "Open in kdiff3".
    private string OpenInToolCaption()
        => _mergeTool is null
            ? T("FormResolveConflicts/openMergeToolBtn.Text", "Open in mergetool")
            : $"{T("FormResolveConflicts/_button1Text.Text", "Open in")} {_mergeTool}";

    private void ReportMergeToolState()
    {
        if (_mergeTool is null)
        {
            _status.Text = T("FormResolveConflicts/_noMergeTool.Text",
                "There is no mergetool configured.\nPlease go to settings and set a mergetool!");
            return;
        }

        if (!_service.IsToolOnPath(_mergeTool))
        {
            // A warning only: git resolves the tool through its own mergetool
            // definitions and mergetool.<tool>.path, so PATH is not the last word.
            _status.Text = string.Format(
                T("The merge tool '{0}' is configured but was not found on PATH; git may still resolve it."),
                _mergeTool);
        }
    }

    private async Task<bool> ConfirmAsync(string message, string caption)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = T("Yes"), MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = T("No"), MinWidth = 80, IsCancel = true };
        Theming.ZoomWindow dialog = new()
        {
            Title = caption,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };

        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    Foreground = Brush("App.Text", Brushes.Gainsboro),
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yes, no },
                },
            },
        };

        DialogKeys.InstallEscapeClose(dialog);
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // ---- construction helpers ------------------------------------------------

    private static Button ColumnButton(string caption, Action onClick)
    {
        Button button = new()
        {
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // A string Content would eat the '_' of an accelerator as an access key.
            Content = new TextBlock { Text = RevisionFilterDialog.StripMnemonic(caption) },
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock SideLabel(string caption) => new()
    {
        Text = caption,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        Margin = new Thickness(0, 2, 16, 2),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock SideValue() => new()
    {
        Foreground = Brush("App.TextDim", Brushes.Gray),
        Margin = new Thickness(0, 2, 0, 2),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Upstream appends the side word after a NO-BREAK SPACE
    // (DisplayWithSuffixUpdater / UpdateSuffixWithinParenthesis).
    private static string SuffixedLabel(string caption, string suffix) => $"{caption} ({suffix})";

    private static string MergeCaption => T("FormResolveConflicts/merge.Text", "Merge");

    private static string MergeHereCaption => T("Merge here…");

    // Says what it does, not what it is: the user is picking WHICH COMMIT of the
    // linked repository this project should record.
    private static string ChooseCommitCaption => T("Choose the submodule commit…");

    private static string ChooseCommitTooltip => T(
        "Shows the commits that lie between the two recorded pointers and lets you pick one — "
        + "including a later commit that already contains both sides, which is often the answer "
        + "and which keeping one side cannot express.");

    private static string OursWord => T("FormResolveConflicts/_ours.Text", "ours");

    private static string TheirsWord => T("FormResolveConflicts/_theirs.Text", "theirs");

    private static void AddAt(Grid grid, Control child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
