using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  What the merge dialog produced: the ref the user chose, the options it was
///  merged with, and — when the dialog ran the merge itself — git's verdict.
///  <para><see cref="Success"/> is <c>false</c> for a conflicted merge (git exits
///  non-zero), which is not an error but the state the caller must react to: the
///  "solve conflicts now?" question and the resolve dialog live outside this
///  dialog, and this record is what tells the caller to ask.</para>
/// </summary>
public sealed record MergeDialogResult(
    string Branch,
    MergeOptions Options,
    bool Executed,
    bool Success,
    string Output);

/// <summary>
///  Merge configuration dialog, the port of the original Git Extensions
///  <c>FormMergeBranch</c>: the user picks the ref to merge into the current
///  branch, chooses between keeping a single branch line (fast forward) and always
///  creating a merge commit, may stop short of committing, and — behind "Show
///  advanced options" — may squash, pick a non-default merge strategy, allow
///  unrelated histories, add <c>--log</c> messages and dictate the merge message.
///  The resulting <c>git merge</c> runs through the shared
///  <see cref="GitProcessDialog"/>, so the command line, <c>Auto-merging …</c> and
///  <c>CONFLICT (content): …</c> are visible live exactly as in the Windows
///  original (screenshot <c>01_merge windows with process and conflict.png</c>).
///
///  <para>Every option maps 1:1 onto a parameter of <c>Commands.MergeBranch</c>
///  through <see cref="MergeOptions"/> — there are no decorative controls here.
///  The dialog never assembles a command line itself: <see cref="BranchTagService"/>
///  does.</para>
///
///  <para>Two deliberate deviations from the Windows dialog:</para>
///  <list type="bullet">
///   <item>the left-hand <b>illustration panel</b> (and with it the <c>Hide help</c>
///    link that only shows/hides that panel) is not ported — the same decision as
///    for the pull dialog's illustration in M50/P3. Porting the link without the
///    panel would leave a control that does nothing;</item>
///   <item>the <b>commit picker button</b> to the right of the branch combo is not
///    reproduced: the port has no <c>FormChooseCommit</c> (established in M69), and
///    a button that opens nothing is worse than no button. The combo is editable
///    instead, so any commit-ish (a SHA, <c>HEAD~2</c>, a ref the list does not
///    show) can simply be typed.</item>
///  </list>
///
///  <para>Threading: the refs, the current branch and the persisted option state
///  are loaded OFF the UI thread in <see cref="ShowAsync"/> and handed to the
///  constructor — the git services block synchronously on async work and deadlock
///  when called from the UI thread.</para>
/// </summary>
public sealed class MergeDialog : Window
{
    /// <summary>
    ///  The strategies upstream's combo offers (<c>FormMergeBranch.Designer.cs</c>).
    ///  The combo stays editable: <c>git merge --strategy=</c> accepts more (e.g.
    ///  <c>ort</c>, the default since git 2.34).
    /// </summary>
    private static readonly string[] Strategies = ["resolve", "recursive", "octopus", "ours", "subtree"];

    private readonly string _repoPath;
    private readonly string _currentBranch;
    private readonly bool _execute;

    // Merge group.
    private readonly HeaderedContentControl _mergeGroup;
    private readonly TextBlock _branchLabel;
    private readonly ComboBox _branchCombo;
    private readonly TextBlock _currentBranchCaption;
    private readonly TextBlock _currentBranchValue;

    // Fast-forward choice + no-commit.
    private readonly RadioButton _fastForward;
    private readonly RadioButton _noFastForward;
    private readonly CheckBox _noCommit;
    private readonly CheckBox _advanced;

    // Advanced options.
    private readonly StackPanel _advancedPanel;
    private readonly CheckBox _nonDefaultStrategy;
    private readonly ComboBox _strategyCombo;
    private readonly CheckBox _squash;
    private readonly CheckBox _allowUnrelatedHistories;
    private readonly CheckBox _addLogMessages;
    private readonly NumericUpDown _logCount;
    private readonly CheckBox _addMergeMessage;
    private readonly TextBox _mergeMessage;

    private readonly Button _mergeBtn;

    private MergeDialogResult? _result;

    private MergeDialog(string repoPath, MergeDialogData data, string? defaultBranch, bool execute)
    {
        _repoPath = repoPath ?? string.Empty;
        _currentBranch = data.CurrentBranch;
        _execute = execute;

        Width = 640;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // ---- Merge group ---------------------------------------------------
        _branchLabel = Label(string.Empty);

        // Editable: see the class remarks on the missing commit picker.
        _branchCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (string name in data.MergeableRefs)
        {
            _branchCombo.Items.Add(name);
        }

        string preselect = !string.IsNullOrWhiteSpace(defaultBranch) ? defaultBranch! : data.DefaultBranch;
        if (!string.IsNullOrWhiteSpace(preselect))
        {
            if (_branchCombo.Items.Contains(preselect))
            {
                _branchCombo.SelectedItem = preselect;
            }

            _branchCombo.Text = preselect;
        }

        _branchCombo.SelectionChanged += (_, _) => UpdateEnabledState();
        _branchCombo.GetObservable(ComboBox.TextProperty).Subscribe(new AnonymousObserver(() => UpdateEnabledState()));

        _currentBranchCaption = Label(string.Empty);
        _currentBranchValue = Label(_currentBranch);
        _currentBranchValue.FontWeight = FontWeight.Bold;

        Grid branchGrid = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8,
        };
        AddAt(branchGrid, _branchLabel, 0, 0);
        AddAt(branchGrid, _branchCombo, 0, 1);
        AddAt(branchGrid, _currentBranchCaption, 1, 0);
        AddAt(branchGrid, _currentBranchValue, 1, 1);

        // ---- Fast-forward / no-commit --------------------------------------
        _fastForward = MakeRadio("FastForward");
        _noFastForward = MakeRadio("FastForward");
        _noCommit = MakeCheck();
        _advanced = MakeCheck();

        _fastForward.IsChecked = !data.Prefs.NoFastForward;
        _noFastForward.IsChecked = data.Prefs.NoFastForward;
        _noCommit.IsChecked = data.Prefs.NoCommit;

        // ---- Advanced options ----------------------------------------------
        _nonDefaultStrategy = MakeCheck();
        _strategyCombo = new ComboBox
        {
            IsEditable = true,
            MinWidth = 170,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        foreach (string strategy in Strategies)
        {
            _strategyCombo.Items.Add(strategy);
        }

        _squash = MakeCheck();
        _allowUnrelatedHistories = MakeCheck();
        _addLogMessages = MakeCheck();
        _logCount = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 999,
            Increment = 1,
            Value = data.Prefs.LogMessagesCount,
            Width = 100,
            FormatString = "0",
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = data.Prefs.AddLogMessages,
        };
        _addLogMessages.IsChecked = data.Prefs.AddLogMessages;
        _addMergeMessage = MakeCheck();
        // TextBoxSurface: the Fluent per-state repaint beats a local Background, so a
        // plain TextBox flips to pure black/white on hover, focus and — this box
        // starts disabled — in the disabled state too.
        _mergeMessage = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                AcceptsReturn = true,
                Height = 60,
                IsEnabled = false,
                BorderBrush = Brush("App.Border", Brushes.Gray),
                BorderThickness = new Thickness(1),
            },
            Brush("App.Panel", Brushes.DimGray),
            Brush("App.Text", Brushes.Gainsboro));

        StackPanel strategyRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _nonDefaultStrategy, _strategyCombo },
        };
        _nonDefaultStrategy.VerticalAlignment = VerticalAlignment.Center;

        StackPanel logRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _addLogMessages, _logCount },
        };
        _addLogMessages.VerticalAlignment = VerticalAlignment.Center;

        _advancedPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(18, 6, 0, 0),
            IsVisible = false,
            Children = { strategyRow, _squash, _allowUnrelatedHistories, logRow, _addMergeMessage, _mergeMessage },
        };

        // Upstream's interlocks (FormMergeBranch.*_CheckedChanged).
        _nonDefaultStrategy.IsCheckedChanged += (_, _) =>
        {
            _strategyCombo.IsVisible = _nonDefaultStrategy.IsChecked == true;
            if (_nonDefaultStrategy.IsChecked != true)
            {
                _strategyCombo.SelectedItem = null;
                _strategyCombo.Text = string.Empty;
            }
        };
        _advanced.IsCheckedChanged += (_, _) => ApplyAdvancedState();
        _noFastForward.IsCheckedChanged += (_, _) =>
        {
            // --squash and --no-ff contradict each other: a squashed merge does not
            // create a merge commit at all.
            bool noFf = _noFastForward.IsChecked == true;
            _squash.IsEnabled = !noFf;
            if (noFf)
            {
                _squash.IsChecked = false;
            }
        };
        _addLogMessages.IsCheckedChanged += (_, _) => _logCount.IsEnabled = _addLogMessages.IsChecked == true;
        _addMergeMessage.IsCheckedChanged += (_, _) => _mergeMessage.IsEnabled = _addMergeMessage.IsChecked == true;

        _advanced.IsChecked = data.Prefs.ShowAdvanced;
        ApplyAdvancedState();

        StackPanel options = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { _fastForward, _noFastForward, _noCommit, _advanced, _advancedPanel },
        };

        _mergeGroup = MakeGroup(new StackPanel
        {
            Orientation = Orientation.Vertical,
            Children = { branchGrid, options },
        });

        // ---- Footer ---------------------------------------------------------
        _mergeBtn = MakeButton();
        _mergeBtn.Background = Brush("App.Accent", new SolidColorBrush(Color.Parse("#007ACC")));
        _mergeBtn.Foreground = Brushes.White;
        _mergeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        _mergeBtn.Margin = new Thickness(0, 10, 0, 0);
        _mergeBtn.Click += (_, _) => _ = OnMergeAsync();

        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(_mergeBtn, Dock.Bottom);
        body.Children.Add(_mergeBtn);
        body.Children.Add(new ScrollViewer
        {
            Content = _mergeGroup,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Content = body;
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        UpdateEnabledState();
    }

    /// <summary>
    ///  Shows the merge dialog modally over <paramref name="owner"/>. Returns what
    ///  the user confirmed (and, when <paramref name="execute"/> is true, git's
    ///  verdict on the merge that was run), or <c>null</c> when the dialog was
    ///  simply closed.
    /// </summary>
    /// <param name="owner">Window to own the modal.</param>
    /// <param name="repoPath">Active repository.</param>
    /// <param name="defaultBranch">
    ///  Ref to preselect — the branch/tag the caller's context menu was opened on.
    ///  When null the current branch's configured upstream is preselected, the way
    ///  <c>FormMergeBranchLoad</c> does it.
    /// </param>
    /// <param name="execute">
    ///  When true (the default) the dialog runs the merge itself through
    ///  <see cref="GitProcessDialog"/> before closing. Pass false to only collect
    ///  the options.
    /// </param>
    public static async Task<MergeDialogResult?> ShowAsync(
        Window owner,
        string repoPath,
        string? defaultBranch = null,
        bool execute = true)
    {
        MergeDialogData data = await Task.Run(() => LoadData(repoPath));
        MergeDialog dialog = new(repoPath, data, defaultBranch, execute);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private static MergeDialogData LoadData(string repoPath)
    {
        try
        {
            return new BranchTagService().LoadMergeData(repoPath);
        }
        catch (Exception)
        {
            // A repository that cannot be read still gets a usable dialog: the ref
            // can be typed.
            return MergeDialogData.Empty;
        }
    }

    // --- State ------------------------------------------------------------

    private string SelectedBranch
        => (_branchCombo.SelectedItem as string ?? _branchCombo.Text ?? string.Empty).Trim();

    private void UpdateEnabledState() => _mergeBtn.IsEnabled = SelectedBranch.Length > 0;

    // FormMergeBranch.advanced_CheckedChanged: hiding the advanced panel also RESETS
    // the options it holds, so a hidden option can never be in force.
    private void ApplyAdvancedState()
    {
        bool on = _advanced.IsChecked == true;
        _advancedPanel.IsVisible = on;

        if (!on)
        {
            _nonDefaultStrategy.IsChecked = false;
            _squash.IsChecked = false;
            _allowUnrelatedHistories.IsChecked = false;
            _addMergeMessage.IsChecked = false;
            _strategyCombo.SelectedItem = null;
            _strategyCombo.Text = string.Empty;
        }

        _strategyCombo.IsVisible = _nonDefaultStrategy.IsChecked == true;
        _squash.IsEnabled = _noFastForward.IsChecked != true;
    }

    /// <summary>
    ///  The structured choice the dialog represents. Read on the UI thread only:
    ///  the merge itself runs on a background thread, where touching a control
    ///  throws.
    /// </summary>
    private MergeOptions CurrentOptions()
    {
        string strategy = _nonDefaultStrategy.IsChecked == true
            ? (_strategyCombo.SelectedItem as string ?? _strategyCombo.Text ?? string.Empty).Trim()
            : string.Empty;

        string? message = _addMergeMessage.IsChecked == true && !string.IsNullOrWhiteSpace(_mergeMessage.Text)
            ? _mergeMessage.Text
            : null;

        int? log = _addLogMessages.IsChecked == true ? (int)(_logCount.Value ?? 20) : null;

        return new MergeOptions(
            AllowFastForward: _noFastForward.IsChecked != true,
            Squash: _squash.IsChecked == true,
            NoCommit: _noCommit.IsChecked == true,
            Strategy: strategy,
            AllowUnrelatedHistories: _allowUnrelatedHistories.IsChecked == true,
            MergeMessage: message,
            LogMessages: log);
    }

    private MergePrefs CurrentPrefs() => new(
        NoFastForward: _noFastForward.IsChecked == true,
        NoCommit: _noCommit.IsChecked == true,
        ShowAdvanced: _advanced.IsChecked == true,
        AddLogMessages: _addLogMessages.IsChecked == true,
        LogMessagesCount: (int)(_logCount.Value ?? 20));

    // --- Actions ----------------------------------------------------------

    private async Task OnMergeAsync()
    {
        string branch = SelectedBranch;
        if (branch.Length == 0)
        {
            return;
        }

        MergeOptions options = CurrentOptions();
        MergePrefs prefs = CurrentPrefs();
        string repo = _repoPath;

        // Remember the option state exactly where upstream remembers it, off the UI
        // thread (it writes settings files).
        _ = Task.Run(() => new BranchTagService().SaveMergePrefs(repo, prefs));

        if (!_execute)
        {
            _result = new MergeDialogResult(branch, options, Executed: false, Success: false, Output: string.Empty);
            Close();
            return;
        }

        BranchTagResult? res = null;
        await GitProcessDialog.RunStreamingAsync(
            this,
            T("FormMergeBranch/Ok.Text", "Merge"),
            emit =>
            {
                res = new BranchTagService().MergeBranchStreaming(repo, branch, options, emit);
                return new GitProcessOutcome(res.Success, res.Output);
            },

            // A merge asks nothing and must never wait for a human: the piped path
            // keeps it strictly non-interactive (git's own `--no-edit` is already in
            // the command).
            interactive: false);

        _result = new MergeDialogResult(
            branch,
            options,
            Executed: true,
            Success: res?.Success == true,
            Output: res?.Output ?? string.Empty);

        Close();
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        Title = T("FormMergeBranch/$this.Text", "Merge branches");

        _mergeGroup.Header = T("FormMergeBranch/groupBox1.Text", "Merge");
        _branchLabel.Text = T("FormMergeBranch/label2.Text", "Merge branch");
        _currentBranchCaption.Text = T("FormMergeBranch/Currentbranch.Text", "Into current branch");

        Caption(_fastForward, T("FormMergeBranch/fastForward.Text",
            "Keep a single branch line if possible (fast forward)"));
        Caption(_noFastForward, T("FormMergeBranch/noFastForward.Text", "Always create a new merge commit"));
        Caption(_noCommit, T("FormMergeBranch/noCommit.Text", "Do not commit"));
        Caption(_advanced, T("FormMergeBranch/advanced.Text", "Show advanced options"));

        Caption(_nonDefaultStrategy, T("FormMergeBranch/NonDefaultMergeStrategy.Text",
            "Use non-default merge strategy"));
        Caption(_squash, T("FormMergeBranch/squash.Text", "Squash commits"));
        ToolTip.SetTip(_squash, T("A squashed merge stages the result without recording a merge commit (--squash); it cannot be combined with \"always create a new merge commit\"."));
        Caption(_allowUnrelatedHistories, T("FormMergeBranch/allowUnrelatedHistories.Text",
            "Allow unrelated histories"));
        Caption(_addLogMessages, T("FormMergeBranch/addLogMessages.Text", "Add log messages"));
        ToolTip.SetTip(_logCount, T("How many one-line descriptions of the merged commits git puts in the merge message (--log=N)."));
        Caption(_addMergeMessage, T("FormMergeBranch/addMergeMessage.Text", "Specify merge message"));

        _mergeBtn.Content = T("FormMergeBranch/Ok.Text", "Merge");
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
        Padding = new Thickness(10),
        Margin = new Thickness(0, 0, 0, 10),
        BorderBrush = Brush("App.Border", Brushes.Gray),
        BorderThickness = new Thickness(1),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    // Captions live in a wrapping TextBlock, not in a string Content: the radio
    // captions are full sentences whose translations are longer still, and a string
    // Content would be clipped (and would eat '_' as an access key).
    private RadioButton MakeRadio(string group) => new()
    {
        GroupName = group,
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        VerticalAlignment = VerticalAlignment.Center,
        Content = WrappingCaption(),
    };

    private CheckBox MakeCheck() => new()
    {
        Foreground = Brush("App.Text", Brushes.Gainsboro),
        Content = WrappingCaption(),
    };

    private static TextBlock WrappingCaption() => new()
    {
        Text = string.Empty,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
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

    // Minimal IObserver for the editable combo's Text property: the Merge button
    // must react to TYPED refs too, not only to picking one from the list.
    private sealed class AnonymousObserver : IObserver<string?>
    {
        private readonly Action _onNext;

        public AnonymousObserver(Action onNext) => _onNext = onNext;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(string? value) => _onNext();
    }
}
