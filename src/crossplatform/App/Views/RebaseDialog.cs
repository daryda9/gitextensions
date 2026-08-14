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
///  What the rebase dialog produced: the options the user settled on and, when the
///  dialog ran the rebase itself, git's verdict.
///  <para><see cref="Success"/> is <c>false</c> for a rebase that stopped on a
///  conflict (git exits non-zero), which is not an error but the state the caller
///  must react to — the rebase banner then owns Continue / Skip / Abort.</para>
/// </summary>
public sealed record RebaseDialogResult(
    RebaseChoice Choice,
    bool Executed,
    bool Success,
    string Output);

/// <summary>
///  Rebase configuration dialog, the port of upstream's <c>FormRebase</c>
///  (<c>src/app/GitUI/CommandsDialogs/FormRebase.cs</c>): the user picks the ref to
///  replay the current branch onto, decides about interactivity, autosquash,
///  auto-stash, the two date rewrites and merge preservation, and may narrow the
///  work to a specific range of commits. The <c>git rebase</c> that results runs
///  through the shared <see cref="GitProcessDialog"/>, so the command line and git's
///  own output are visible live.
///
///  <para><b>Options only — the running rebase is not this window's business.</b>
///  Upstream's form doubles as a command panel while a rebase is stopped (Continue,
///  Skip, Abort, Solve conflicts, Edit todo — <c>FormRebase.cs:149-206</c>). In this
///  port that role already belongs to <see cref="RepositoryProgressBanner"/>, which is
///  always on screen instead of behind a dialog the user has to reopen. Duplicating
///  those buttons here would give the same command two homes and two enable rules.</para>
///
///  <para><b>Options deliberately not offered.</b></para>
///  <list type="bullet">
///   <item><c>--preserve-merges</c> as such: git 2.43 answers
///    <c>fatal: --preserve-merges was replaced by --rebase-merges</c>. Upstream's
///    <c>chkPreserveMerges</c> is therefore reproduced with the modern flag and the
///    modern caption; see <see cref="BranchTagService.BuildRebaseArguments"/>.</item>
///   <item>~~the <b>commit picker</b> next to "From"~~ — <b>it exists since M214</b>
///    (<see cref="ChooseCommitDialog"/>, the port of <c>FormChooseCommit</c>), reached
///    from the <c>…</c> button beside the field and bounded the same way upstream bounds
///    it: the current branch, down to its merge base with the target. The field stays a
///    text box as well, and still accepts any commit-ish — a SHA, <c>HEAD~3</c>, a
///    ref.</item>
///  </list>
///
///  <para><b>Update refs is an override, not an option.</b> The box mirrors the
///  repository's effective <c>rebase.updateRefs</c> and a flag is sent ONLY when the
///  user moves it away from that value (<c>FormRebase.cs:331-335</c>) — leaving it
///  alone keeps the command line free of a flag git would have applied from the config
///  anyway. Measured on git 2.43: with <c>rebase.updateRefs=true</c>, a bare
///  <c>git rebase master</c> already reports "Updated the following refs with
///  --update-refs", so the box exists to say NO to that config for one rebase, and to
///  say yes without editing the config.</para>
///
///  <para><b>Interactive without an editor.</b> This port wires no editor to git, so
///  <c>-i</c> runs with <c>GIT_SEQUENCE_EDITOR=true</c> and the generated todo is
///  accepted as generated (see <see cref="BranchTagService.RebaseStreaming"/>). What
///  the checkbox still buys is <c>--autosquash</c> — upstream gates autosquash on
///  interactive for exactly this reason (<c>FormRebase.cs:216</c>) — and a session
///  marked interactive, which is what makes the banner's rebase commands apply. The
///  caption below the checkbox says so rather than letting the user discover it.</para>
///
///  <para>Threading: refs, the current branch, the dirty flag and the remembered
///  auto-stash choice are loaded OFF the UI thread in <see cref="ShowAsync"/> and
///  handed to the constructor — the git services block synchronously and deadlock
///  when called from the UI thread.</para>
/// </summary>
public sealed class RebaseDialog : Theming.ZoomWindow
{
    /// <summary>
    ///  Upstream's illustration for this dialog (<c>FormRebase.Designer.cs:561-562</c>:
    ///  <c>Image1 = HelpCommandRebase</c>, <c>Image2 = null</c>) — a rebase has no
    ///  second scenario to show on hover, unlike the merge dialog's fast forward.
    /// </summary>
    private static readonly HelpImageSpec HelpSpec = new("FormRebase", "HelpCommandRebase");

    /// <summary>
    ///  Width of the options column alone. Sized to the longest caption the dialog can
    ///  show ("Committer date is author date", whose translations run longer still)
    ///  plus the range row's label + field + combo.
    /// </summary>
    private const double OptionsWidth = 560;

    private readonly string _repoPath;
    private readonly bool _execute;

    private readonly HelpImagePanel _help;

    private readonly HeaderedContentControl _group;
    private readonly TextBlock _ontoLabel;
    private readonly ComboBox _ontoCombo;
    private readonly TextBlock _currentCaption;
    private readonly TextBlock _currentValue;

    private readonly CheckBox _interactive;
    private readonly CheckBox _autosquash;
    private readonly CheckBox _autostash;
    private readonly CheckBox _ignoreDate;
    private readonly CheckBox _committerDateIsAuthorDate;
    private readonly CheckBox _rebaseMerges;
    private readonly CheckBox _updateRefs;
    private readonly TextBlock _interactiveNote;

    private readonly CheckBox _specificRange;
    private readonly TextBlock _fromLabel;
    private readonly TextBox _from;
    private readonly Button _chooseFrom;
    private readonly TextBlock _toLabel;
    private readonly ComboBox _to;
    private readonly TextBlock _rangeNote;

    private readonly TextBlock _commandPreview;

    private readonly Button _rebaseBtn;
    private readonly Button _cancelBtn;

    private readonly bool _isDirty;

    // The repository's effective rebase.updateRefs. Kept because the flag is only sent
    // when the checkbox DISAGREES with it (see the class remarks).
    private readonly bool _updateRefsConfig;

    // Guards the mutual-exclusion handlers against re-entering each other: unchecking a
    // box from inside another box's handler raises IsCheckedChanged again.
    private bool _applyingExclusions;

    // The property observers below fire the instant they are subscribed, i.e. while the
    // constructor is still assigning fields; nothing may be read before this is set.
    private bool _built;

    private RebaseDialogResult? _result;

    private RebaseDialog(
        string repoPath,
        RebaseDialogData data,
        HelpImageAssets helpAssets,
        string? defaultOnto,
        bool execute)
    {
        _repoPath = repoPath ?? string.Empty;
        _execute = execute;
        _isDirty = data.IsDirty;
        _updateRefsConfig = data.UpdateRefsConfig;

        // Tall enough for the whole options block plus the range rows and the command
        // preview; the ScrollViewer below is the safety net for translations that wrap.
        // Raised from 560 when the update-refs box was added: at 560 the "To" row was
        // already half under the docked preview on first open.
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        _help = new HelpImagePanel(HelpSpec, helpAssets)
        {
            Margin = new Thickness(Metrics.Space.Md, Metrics.Space.Md, 0, Metrics.Space.Md),
        };
        _help.ExpandedChanged += ApplyHelpGeometry;
        ApplyHelpGeometry();

        // ---- Target ---------------------------------------------------------
        _ontoLabel = Label(string.Empty);

        // Editable: any commit-ish is a legal rebase target, and the port has no commit
        // picker to offer instead (see the class remarks).
        _ontoCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = Metrics.Density.ControlMinHeight,
        };
        foreach (string name in data.Refs)
        {
            _ontoCombo.Items.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(defaultOnto))
        {
            if (_ontoCombo.Items.Contains(defaultOnto))
            {
                _ontoCombo.SelectedItem = defaultOnto;
            }

            _ontoCombo.Text = defaultOnto;
        }

        _currentCaption = Label(string.Empty);
        _currentValue = Label(data.CurrentBranch);
        _currentValue.FontWeight = Metrics.Text.ActiveWeight;

        Grid targetGrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = Metrics.Space.Md,
            RowSpacing = Metrics.Space.Sm,
        };
        AddAt(targetGrid, _ontoLabel, 0, 0);
        AddAt(targetGrid, _ontoCombo, 0, 1);
        AddAt(targetGrid, _currentCaption, 1, 0);
        AddAt(targetGrid, _currentValue, 1, 1);

        // ---- Options --------------------------------------------------------
        _interactive = MakeCheck();
        _autosquash = MakeCheck();
        _autostash = MakeCheck();
        _ignoreDate = MakeCheck();
        _committerDateIsAuthorDate = MakeCheck();
        _rebaseMerges = MakeCheck();
        _updateRefs = MakeCheck();

        // FormRebase.cs:333 — the config IS the starting value, so a repository already
        // configured for update-refs shows a ticked box and produces no flag.
        _updateRefs.IsChecked = data.UpdateRefsConfig;

        _interactiveNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = Metrics.Text.Caption,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            Margin = new Thickness(Metrics.Space.Xl, 0, 0, Metrics.Space.Xs),
        };

        // FormRebase.cs:132 — the repository's own rebase.autosquash is the checkbox's
        // starting value, so a repo configured for autosquash does not need the box
        // ticked by hand every time.
        _autosquash.IsChecked = data.AutoSquashConfig;

        // FormRebase.cs:138 — the remembered global choice seeds the per-rebase one.
        _autostash.IsChecked = data.AutoStash;

        // FormRebase.cs:182 — with a clean working tree there is nothing to stash, and
        // upstream disables the box rather than passing a flag with no effect.
        _autostash.IsEnabled = data.IsDirty;

        _interactive.IsCheckedChanged += (_, _) => ApplyExclusions();
        _ignoreDate.IsCheckedChanged += (_, _) => ApplyExclusions();
        _committerDateIsAuthorDate.IsCheckedChanged += (_, _) => ApplyExclusions();
        _rebaseMerges.IsCheckedChanged += (_, _) => UpdatePreview();
        _autostash.IsCheckedChanged += (_, _) => UpdatePreview();
        _autosquash.IsCheckedChanged += (_, _) => UpdatePreview();
        _updateRefs.IsCheckedChanged += (_, _) => UpdatePreview();

        // ---- Specific range -------------------------------------------------
        _specificRange = MakeCheck();
        _fromLabel = Label(string.Empty);
        _from = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                IsEnabled = false,
                MinHeight = Metrics.Density.ControlMinHeight,
                Padding = Metrics.Density.InputPadding,
                BorderBrush = Brush("App.BorderStrong", new SolidColorBrush(Color.Parse("#88898F"))),
                BorderThickness = new Thickness(1),
            },
            Brush("App.Panel", Brushes.DimGray),
            Brush("App.Text", Brushes.Gainsboro));

        _toLabel = Label(string.Empty);
        _to = new ComboBox
        {
            IsEditable = true,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = Metrics.Density.ControlMinHeight,
        };

        // FormRebase.cs:122-124 — only local heads can be the branch that gets moved.
        foreach (string name in data.LocalBranches)
        {
            _to.Items.Add(name);
        }

        if (data.LocalBranches.Contains(data.CurrentBranch))
        {
            _to.SelectedItem = data.CurrentBranch;
        }

        _to.Text = data.CurrentBranch;

        // Half a range is silently NOT a range: upstream (FormRebase.cs:348) and the
        // core both fall back to a plain `git rebase <onto>` when either field is blank,
        // which is a different rebase from the one the user asked for by ticking the
        // box. The command preview already shows the truth; this line says WHY it says
        // that, because "I ticked specific range and got a full rebase" is otherwise
        // only discoverable by reading the command line character by character.
        _rangeNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = Metrics.Text.Caption,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
            IsVisible = false,
        };

        // Upstream's btnChooseFromRevision (FormRebase.Designer.cs), which this port had
        // to leave out until the commit picker existed. The caption is the ellipsis
        // upstream uses; what it opens says the rest.
        _chooseFrom = new Button
        {
            Content = "…",
            IsEnabled = false,
            MinWidth = 36,
            MinHeight = Metrics.Density.ControlMinHeight,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = T("Pick the commit from the history…"),
        };
        _chooseFrom.Click += (_, _) => _ = ChooseFromAsync();

        Grid rangeGrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = Metrics.Space.Md,
            RowSpacing = Metrics.Space.Sm,
            Margin = new Thickness(Metrics.Space.Xl, Metrics.Space.Xs, 0, 0),
        };
        AddAt(rangeGrid, _fromLabel, 0, 0);
        AddAt(rangeGrid, _from, 0, 1);
        AddAt(rangeGrid, _chooseFrom, 0, 2);
        AddAt(rangeGrid, _toLabel, 1, 0);
        AddAt(rangeGrid, _to, 1, 1);

        // The "To" row has no picker of its own — only a local branch can be the branch
        // that gets moved, and that is a combo of names, not a commit — so its field
        // takes the width the button leaves on the row above.
        Grid.SetColumnSpan(_to, 2);

        _specificRange.IsCheckedChanged += (_, _) =>
        {
            // FormRebase.chkUseFromOnto_CheckedChanged (:391-396).
            bool on = _specificRange.IsChecked == true;
            _from.IsEnabled = on;
            _chooseFrom.IsEnabled = on;
            _to.IsEnabled = on;
            UpdatePreview();
        };

        _from.GetObservable(TextBox.TextProperty).Subscribe(new Observer(UpdatePreview));
        _to.GetObservable(ComboBox.TextProperty).Subscribe(new Observer(UpdatePreview));

        // ---- Command preview -------------------------------------------------
        // Not an upstream control. Upstream shows the command line only once the rebase
        // is already running, inside FormProcess; here the same information is worth
        // showing BEFORE, because this dialog replaced a yes/no confirmation and the
        // command is the honest answer to "what is about to happen". It is docked
        // beside the buttons rather than placed inside the scrolling options, so that
        // ticking the option that makes the panel taller cannot scroll away the very
        // line that describes what ticking it did.
        _commandPreview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"),
            FontSize = Metrics.Text.Body,
            Foreground = Brush("App.TextDim", Brushes.Gray),
            Margin = new Thickness(0, Metrics.Space.Sm, 0, 0),
        };

        StackPanel options = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = Metrics.Space.Sm,
            Margin = new Thickness(0, Metrics.Space.Lg, 0, 0),
            Children =
            {
                _interactive,
                _interactiveNote,
                _autosquash,
                _autostash,
                _rebaseMerges,
                _updateRefs,
                _ignoreDate,
                _committerDateIsAuthorDate,
                _specificRange,
                rangeGrid,
            },
        };

        // Autosquash is a sub-option of interactive, and the two date rewrites are a
        // pair; indenting says so without a second group box.
        _autosquash.Margin = new Thickness(Metrics.Space.Xl, 0, 0, 0);

        _group = MakeGroup(new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { targetGrid, options },
        });

        // ---- Footer ----------------------------------------------------------
        _cancelBtn = MakeButton();
        _cancelBtn.Click += (_, _) => Close();

        _rebaseBtn = MakeButton();
        _rebaseBtn.Background = Brush("App.Accent", new SolidColorBrush(Color.Parse("#007ACC")));
        _rebaseBtn.Foreground = Brushes.White;
        _rebaseBtn.Click += (_, _) => _ = OnRebaseAsync();

        StackPanel footer = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = Metrics.Space.Sm,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Metrics.Space.Md, 0, 0),
            Children = { _cancelBtn, _rebaseBtn },
        };

        DockPanel body = new() { Margin = Metrics.Space.All(Metrics.Space.Md) };
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_commandPreview, Dock.Bottom);

        // Docked, not inside the scrolling options, for the same reason as the preview
        // it explains: the range rows sit at the very bottom of a panel that has just
        // grown, so a note placed there is exactly the thing scrolled out of sight.
        // Measured: with the note in the StackPanel it was off-screen on first open.
        DockPanel.SetDock(_rangeNote, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(_commandPreview);
        body.Children.Add(_rangeNote);
        body.Children.Add(new ScrollViewer
        {
            Content = _group,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Grid root = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_help, 0);
        Grid.SetColumn(body, 1);
        root.Children.Add(_help);
        root.Children.Add(body);
        Content = root;
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        _built = true;

        // Subscribed last: the handler touches _rebaseBtn and the preview, and Avalonia
        // pushes the property's CURRENT value at subscribe time.
        _ontoCombo.GetObservable(ComboBox.TextProperty).Subscribe(new Observer(UpdatePreview));

        ApplyExclusions();
    }

    /// <summary>
    ///  Shows the rebase dialog modally over <paramref name="owner"/> and returns what
    ///  the user confirmed — plus git's verdict when the dialog ran the rebase itself —
    ///  or <c>null</c> when the dialog was cancelled or closed.
    /// </summary>
    /// <param name="owner">Window to own the modal.</param>
    /// <param name="repoPath">Active repository.</param>
    /// <param name="defaultOnto">
    ///  Ref or commit to preselect as the rebase target — the branch the panel's
    ///  selection sits on, or the revision the grid's context menu was opened on.
    /// </param>
    /// <param name="execute">
    ///  When true (the default) the dialog runs the rebase itself through
    ///  <see cref="GitProcessDialog"/> before closing. Pass false to only collect the
    ///  options.
    /// </param>
    public static async Task<RebaseDialogResult?> ShowAsync(
        Window owner,
        string repoPath,
        string? defaultOnto = null,
        bool execute = true)
    {
        // The palette must be read here, on the UI thread; decoding and recolouring the
        // diagram then happens on the worker together with the git data.
        (Color Text, Color Window) palette = HelpImagePanel.ReadPalette();

        RebaseDialogData data = await Task.Run(() => LoadData(repoPath));
        HelpImageAssets helpAssets = await Task.Run(() => HelpImagePanel.Prepare(HelpSpec, palette));

        RebaseDialog dialog = new(repoPath, data, helpAssets, defaultOnto, execute);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private static RebaseDialogData LoadData(string repoPath)
    {
        try
        {
            return new BranchTagService().LoadRebaseData(repoPath);
        }
        catch (Exception)
        {
            // A repository that cannot be read still gets a usable dialog: the target
            // can be typed.
            return RebaseDialogData.Empty;
        }
    }

    // --- State ------------------------------------------------------------

    private string Onto
        => (_ontoCombo.SelectedItem as string ?? _ontoCombo.Text ?? string.Empty).Trim();

    /// <summary>
    ///  Opens the commit picker for the "From" field and writes back the short hash of
    ///  whatever the user chose (upstream's <c>btnChooseFromRevision_Click</c>,
    ///  <c>FormRebase.cs:400-441</c>).
    ///
    ///  <para>The list is bounded the way upstream bounds it: the commits of the current
    ///  branch, ending at its merge base with the rebase target. Anything older than that
    ///  base is already on the target and cannot be part of what this rebase replays, so
    ///  offering it would be offering a range git will do nothing with.</para>
    ///
    ///  <para>A cancel leaves the field alone — including a field the user typed by hand,
    ///  which the picker is an alternative to and not a replacement for.</para>
    /// </summary>
    private async Task ChooseFromAsync()
    {
        ChosenCommit? chosen = await ChooseCommitDialog.ShowAsync(
            this,
            _repoPath,
            new ChooseCommitRequest(
                T("Choose the commit the range starts after"),
                // The field is "From (exc.)" and the parenthesis is the whole meaning: the
                // commit picked here is the LAST one kept as it is. Said in words, because
                // an off-by-one here silently rebases one commit too many or too few.
                T("The rebase replays the commits AFTER this one. The commit you pick is not itself rebased."),
                Preselect: (_from.Text ?? string.Empty).Trim(),
                CurrentBranchOnly: true,
                ExcludeAncestorsOf: Onto));

        if (chosen is not null)
        {
            _from.Text = chosen.ShortHash;
        }
    }

    /// <summary>
    ///  Keeps the window exactly as wide as the options column plus whatever the
    ///  illustration column currently occupies, so hiding the help really shrinks the
    ///  dialog — the same arrangement as <see cref="MergeDialog"/>.
    /// </summary>
    private void ApplyHelpGeometry() => Width = OptionsWidth + _help.CurrentWidth + Metrics.Space.Md;

    /// <summary>
    ///  Upstream's interlocks, verbatim in effect: <c>chkInteractive_CheckedChanged</c>
    ///  (<c>FormRebase.cs:214-217</c>) and <c>ToggleDateCheckboxMutualExclusions</c>
    ///  (<c>:229-236</c>).
    ///
    ///  <para>Why they exist, in git's terms rather than the form's: the two date
    ///  rewrites are implemented by the apply backend, which cannot run an interactive
    ///  todo or recreate merges, so git rejects the combinations outright — better to
    ///  grey them out than to let the user assemble a command that fails. And the two
    ///  date options contradict each other by definition (one forces the author date to
    ///  now, the other forces the committer date to the author's), so each disables the
    ///  other.</para>
    ///
    ///  <para>Disabled boxes are also UNCHECKED here. Upstream only disables them,
    ///  which leaves a ticked-but-greyed box whose flag still reaches
    ///  <c>Commands.Rebase</c> — harmless there only because the argument builder
    ///  happens to drop interactive and merge flags in the date branches
    ///  (<c>Commands.Arguments.cs:510-529</c>). Clearing them makes the dialog say the
    ///  truth about the command it will run.</para>
    /// </summary>
    private void ApplyExclusions()
    {
        if (_applyingExclusions)
        {
            return;
        }

        _applyingExclusions = true;
        try
        {
            bool ignoreDate = _ignoreDate.IsChecked == true;
            bool committerDate = _committerDateIsAuthorDate.IsChecked == true;
            bool anyDate = ignoreDate || committerDate;

            _committerDateIsAuthorDate.IsEnabled = !ignoreDate;
            _ignoreDate.IsEnabled = !committerDate;

            _interactive.IsEnabled = !anyDate;
            _rebaseMerges.IsEnabled = !anyDate;
            if (anyDate)
            {
                _interactive.IsChecked = false;
                _rebaseMerges.IsChecked = false;
            }

            _autosquash.IsEnabled = _interactive.IsChecked == true && !anyDate;
            if (!_autosquash.IsEnabled)
            {
                _autosquash.IsChecked = false;
            }

            _interactiveNote.IsVisible = _interactive.IsChecked == true;

            // Re-assert the dirty-tree rule: nothing above touches it, but it is the
            // one enable state not derived from another checkbox.
            _autostash.IsEnabled = _isDirty;
        }
        finally
        {
            _applyingExclusions = false;
        }

        UpdatePreview();
    }

    /// <summary>
    ///  The structured choice the dialog represents. Read on the UI thread only: the
    ///  rebase runs on a background thread, where touching a control throws.
    /// </summary>
    private RebaseChoice CurrentChoice()
    {
        bool updateRefs = _updateRefs.IsChecked == true;
        bool range = _specificRange.IsChecked == true;
        string from = range ? (_from.Text ?? string.Empty).Trim() : string.Empty;
        string to = range
            ? (_to.SelectedItem as string ?? _to.Text ?? string.Empty).Trim()
            : string.Empty;

        return new RebaseChoice(
            Onto: Onto,
            Interactive: _interactive.IsChecked == true,
            AutoSquash: _autosquash.IsChecked == true,
            AutoStash: _autostash.IsChecked == true,
            IgnoreDate: _ignoreDate.IsChecked == true,
            CommitterDateIsAuthorDate: _committerDateIsAuthorDate.IsChecked == true,
            RebaseMerges: _rebaseMerges.IsChecked == true,
            From: from,
            BranchToMove: to,

            // FormRebase.cs:331-335: a flag only when the user contradicts the config —
            // agreeing with it means git already does this and the command stays clean.
            UpdateRefs: updateRefs == _updateRefsConfig ? null : updateRefs);
    }

    private void UpdatePreview()
    {
        // Called from property observers that fire during construction, before every
        // field this reads has been assigned.
        if (!_built)
        {
            return;
        }

        // Only while the range is actually claimed AND incomplete: a ticked box with
        // both ends filled needs no explanation, an unticked one has no range to lose.
        RebaseChoice choice = CurrentChoice();
        _rangeNote.IsVisible = _specificRange.IsChecked == true && !choice.HasRange;

        bool ready = Onto.Length > 0;
        _rebaseBtn.IsEnabled = ready;
        _commandPreview.Text = ready
            ? "git " + BranchTagService.BuildRebaseArguments(choice).ToString()
            : string.Empty;
    }

    // --- Actions ----------------------------------------------------------

    private async Task OnRebaseAsync()
    {
        RebaseChoice choice = CurrentChoice();
        if (choice.Onto.Length == 0)
        {
            return;
        }

        string repo = _repoPath;

        // FormRebase.cs:327 — the per-rebase auto-stash decision becomes the remembered
        // default. Off the UI thread: it rewrites the settings file.
        bool autoStash = choice.AutoStash;
        _ = Task.Run(() => new BranchTagService().SaveRebaseAutoStash(autoStash));

        if (!_execute)
        {
            _result = new RebaseDialogResult(choice, Executed: false, Success: false, Output: string.Empty);
            Close();
            return;
        }

        BranchTagResult? res = null;
        await GitProcessDialog.RunStreamingAsync(
            this,
            T("FormRebase/btnRebase.Text", "Rebase"),
            emit =>
            {
                res = new BranchTagService().RebaseStreaming(repo, choice, emit);
                return new GitProcessOutcome(res.Success, res.Output);
            },

            // A rebase started this way asks nothing: both editors are pinned to the
            // no-op, so the piped path is correct and nothing can wait for a human.
            interactive: false);

        _result = new RebaseDialogResult(
            choice,
            Executed: true,
            Success: res?.Success == true,
            Output: res?.Output ?? string.Empty);

        Close();
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        Title = T("FormRebase/$this.Text", "Rebase");

        _group.Header = T("FormRebase/lblRebase.Text", "Rebase current branch on top of another branch");
        _ontoLabel.Text = T("FormRebase/label2.Text", "Rebase on");
        _currentCaption.Text = T("FormRebase/lblCurrent.Text", "Current branch:");

        Caption(_interactive, T("FormRebase/chkInteractive.Text", "Interactive Rebase"));
        _interactiveNote.Text = T(
            "The generated todo list runs unchanged: this port wires no editor to git. Reorder or edit the remaining steps from the rebase banner once git stops.");

        Caption(_autosquash, T("FormRebase/chkAutosquash.Text", "Autosquash"));
        Caption(_autostash, T("FormRebase/chkStash.Text", "Auto stash"));
        ToolTip.SetTip(_autostash, T(
            "Stashes the working tree before the rebase and restores it afterwards (--autostash). Without it git refuses to start with uncommitted changes."));

        // Upstream's caption is "Preserve Merges" because that was the flag's name;
        // the flag is gone and the replacement behaves differently enough that keeping
        // the old wording would misdescribe the command. Hence a port-local string.
        Caption(_rebaseMerges, T("Rebase merges"));
        ToolTip.SetTip(_rebaseMerges, T(
            "Recreates the merge commits in the range instead of flattening them (--rebase-merges). Replaces git's removed --preserve-merges."));

        Caption(_updateRefs, T("FormRebase/checkBoxUpdateRefs.Text", "Update refs"));
        ToolTip.SetTip(_updateRefs, T(
            "Moves the other local branches that point INSIDE the range being replayed, instead of leaving them on the old commits (--update-refs). Starts from this repository's rebase.updateRefs; a flag is only sent when you change it."));

        Caption(_ignoreDate, T("FormRebase/chkIgnoreDate.Text", "Ignore date"));
        ToolTip.SetTip(_ignoreDate, T(
            "FormRebase/chkIgnoreDate.toolTip1",
            "Sets the author date to the current date (same as\ncommit date), ignoring the original author date."));

        Caption(_committerDateIsAuthorDate,
            T("FormRebase/chkCommitterDateIsAuthorDate.Text", "Committer date is author date"));
        ToolTip.SetTip(_committerDateIsAuthorDate, T(
            "FormRebase/chkCommitterDateIsAuthorDate.toolTip1",
            "Sets the commit date to the original author date\n(instead of the current date)."));

        Caption(_specificRange, T("FormRebase/chkSpecificRange.Text", "Specific range"));
        _fromLabel.Text = T("FormRebase/lblRangeFrom.Text", "From (exc.)");
        _toLabel.Text = T("FormRebase/lblRangeTo.Text", "To");
        _rangeNote.Text = T(
            "Both ends are needed. While either field is empty the range is ignored and the whole current branch is replayed — the command below is the one that will run.");

        _rebaseBtn.Content = T("FormRebase/btnRebase.Text", "Rebase");
        _cancelBtn.Content = T("Cancel");

        _help.ApplyTranslations(
            T("HelpImageDisplayUserControl/linkLabelHide.Text", "Hide help"),
            T("HelpImageDisplayUserControl/linkLabelShowHelp.Text", "Show help").ReplaceLineEndings(" "),
            hoverNotice: string.Empty);
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // --- Chrome helpers ---------------------------------------------------

    private static void AddAt(Grid grid, Control child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private HeaderedContentControl MakeGroup(Control content) => new()
    {
        Content = content,
        Padding = Metrics.Space.All(Metrics.Space.Md),
        Margin = new Thickness(0, 0, 0, Metrics.Space.Md),
        BorderBrush = Brush("App.Border", Brushes.Gray),
        BorderThickness = new Thickness(1),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    // Captions live in a wrapping TextBlock, not in a string Content: they are full
    // phrases whose translations are longer still, and a string Content would be
    // clipped (and would eat '_' as an access key).
    private CheckBox MakeCheck() => new()
    {
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        Content = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
    };

    private static void Caption(ContentControl control, string text)
    {
        if (control.Content is TextBlock block)
        {
            block.Text = text;
            return;
        }

        control.Content = text;
    }

    private Button MakeButton() => new()
    {
        MinWidth = 90,
        MinHeight = Metrics.Density.ControlMinHeight,
        Padding = Metrics.Density.ButtonPadding,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = Brush("App.Control", Brushes.DimGray),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;

    // Minimal IObserver for the editable combos' and the text box's Text property: the
    // command preview must react to TYPED refs, not only to picking one from a list.
    private sealed class Observer : IObserver<string?>
    {
        private readonly Action _onNext;

        public Observer(Action onNext) => _onNext = onNext;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(string? value) => _onNext();
    }
}
