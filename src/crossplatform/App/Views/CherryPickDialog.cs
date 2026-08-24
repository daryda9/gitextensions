using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Views;

/// <summary>What the cherry-pick dialog did, for the caller's refresh and status line.</summary>
public sealed record CherryPickDialogResult(bool Executed, bool Success, string Output);

/// <summary>
///  The cherry-pick configuration dialog — the port of upstream's
///  <c>FormCherryPick</c> (<c>src/app/GitUI/CommandsDialogs/FormCherryPick.cs</c>).
///  Until this existed the row menu ran <c>git cherry-pick &lt;hash&gt;</c> directly,
///  which had three consequences upstream does not have: a <b>merge commit</b> could
///  not be picked at all (git refuses without <c>-m</c>, and there was no way to say
///  which parent is the mainline), the <c>-x</c> provenance line was unreachable, and
///  git's own output went to a status-bar line instead of a console.
///
///  <para>The form's pieces, each with its upstream twin:</para>
///  <list type="bullet">
///   <item>the "Cherry pick this commit" panel (hash, subject, author — upstream's
///    <c>commitSummaryUserControl1</c>), reloaded whenever the target changes;</item>
///   <item>"Choose another revision" through <see cref="ChooseCommitDialog"/>
///    (upstream's <c>btnChooseRevision</c> → <c>FormChooseCommit</c>,
///    <c>FormCherryPick.cs:193-204</c>);</item>
///   <item>the parents list, shown only for a merge commit
///    (<c>OnRevisionChanged</c>, <c>:102-137</c>): picking parent N becomes
///    <c>-m N</c>, and Cherry pick refuses with upstream's own message while nothing
///    is selected;</item>
///   <item>the two checkboxes, persisted in the same <see cref="AppSettings"/> keys
///    upstream uses (<c>CommitAutomaticallyAfterCherryPick</c>,
///    <c>AddCommitReferenceToCherryPick</c>) and saved only on a pick, as upstream
///    saves only on OK (<c>SaveSettings</c>, <c>:74-81</c>);</item>
///   <item>the pick itself inside the shared <see cref="GitProcessDialog"/>
///    (upstream runs it in <c>FormProcess</c>, <c>:179</c>). Conflicts are the
///    caller's turn: it runs <c>ConflictFlow.HandleAsync</c> after this closes, the
///    port of the <c>MergeConflictHandler</c> call at <c>:181</c>.</item>
///  </list>
///
///  <para>Threading: everything git is asked (commit details, is-it-a-merge, the
///  parent list, the settings file) is read on a worker, because the services block
///  and deadlock on the UI thread (HANDOFF §3).</para>
/// </summary>
public sealed class CherryPickDialog : Theming.ZoomWindow
{
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

    private readonly string _repoPath;
    private string _hash;

    private readonly TextBlock _summaryHash;
    private readonly TextBlock _summarySubject;
    private readonly TextBlock _summaryAuthor;

    private readonly TextBlock _parentsLabel;
    private readonly ListBox _parentsList;

    private readonly CheckBox _autoCommit;
    private readonly CheckBox _addReference;
    private readonly TextBlock _status;
    private readonly Button _pick;
    private readonly Button _choose;

    private CherryPickDialogResult? _result;

    /// <summary>
    ///  Everything the window shows about one candidate commit, read in one trip off
    ///  the UI thread. <see cref="Parents"/> is empty for a non-merge commit — its
    ///  emptiness is what hides the parents list, the way upstream's <c>_isMerge</c>
    ///  collapses it.
    /// </summary>
    private sealed record CandidateData(string Hash, CommitDetailInfo? Info, IReadOnlyList<string> Parents);

    /// <summary>
    ///  Opens the dialog on <paramref name="commitHash"/> and returns what it did:
    ///  <see langword="null"/> or <c>Executed: false</c> for a cancel, otherwise
    ///  whether git reported success — a conflicted stop is <c>Success: false</c>
    ///  with the conflict lines in <c>Output</c>, and the caller's conflict flow
    ///  takes it from there.
    /// </summary>
    public static async Task<CherryPickDialogResult?> ShowAsync(Window owner, string repoPath, string commitHash)
    {
        (CandidateData data, bool autoCommit, bool addReference) = await Task.Run(() =>
        {
            // Upstream's LoadSettings. The settings store reads a file, so it is
            // asked here with the git questions rather than on the UI thread.
            bool auto = false;
            bool addRef = false;
            try
            {
                auto = AppSettings.CommitAutomaticallyAfterCherryPick;
                addRef = AppSettings.AddCommitReferenceToCherryPick;
            }
            catch (Exception)
            {
                // Unreadable settings leave the boxes at upstream's defaults (false).
            }

            return (Load(repoPath, commitHash), auto, addRef);
        });

        CherryPickDialog dialog = new(repoPath, data, autoCommit, addReference);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private static CandidateData Load(string repoPath, string hash)
    {
        CommitDetailInfo? info = null;
        List<string> parents = [];

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            ObjectId id = ObjectId.Parse(hash);

            try
            {
                info = new CommitDetailService().LoadCommit(repoPath, hash);
            }
            catch (Exception)
            {
                // The summary panel says "(commit details unavailable)"; the pick
                // itself does not need the details.
            }

            // Upstream: Module.IsMerge + Module.GetParentRevisions
            // (FormCherryPick.cs:95, :117). The list rows carry the same three facts
            // upstream's columns do (message, author, date); the ordinal is the -m
            // argument and comes from the row's position.
            if (module.IsMerge(id))
            {
                int ordinal = 0;
                foreach (GitRevision parent in module.GetParentRevisions(id))
                {
                    ordinal++;
                    string author = parent.Author ?? string.Empty;
                    parents.Add($"{ordinal}.  {parent.Subject}  —  {author}, {parent.CommitDate:d}");
                }
            }
        }
        catch (Exception)
        {
            // A repository that cannot be read still gets a dialog: the pick will
            // fail in the process dialog with git's own words.
        }

        return new CandidateData(hash, info, parents);
    }

    private CherryPickDialog(string repoPath, CandidateData data, bool autoCommit, bool addReference)
    {
        _repoPath = repoPath;
        _hash = data.Hash;

        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#9B9B9B");
        IBrush border = Brush("App.Border", "#3F3F46");

        Title = Strip(T("FormCherryPick/$this.Text", "Cherry pick commit"));
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", "#1E1E1E");

        _summaryHash = new TextBlock
        {
            Foreground = text,
            FontFamily = Monospace,
            FontSize = 12,
        };
        _summarySubject = new TextBlock
        {
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _summaryAuthor = new TextBlock
        {
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        Border summaryPanel = new()
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Children = { _summaryHash, _summarySubject, _summaryAuthor },
            },
        };

        // Upstream's lblAnotherRev + btnChooseRevision, folded into one button: there
        // is no free-text twin here (unlike the archive dialog), so the label IS the
        // action. The upstream caption ends in a colon that a button does not want.
        _choose = new Button
        {
            Content = Strip(T("FormCherryPick/lblAnotherRev.Text", "Choose another revision:")).TrimEnd(':') + "…",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _choose.Click += (_, _) => _ = ChooseAnotherAsync();

        _parentsLabel = new TextBlock
        {
            Text = Strip(T("FormCherryPick/lblParents.Text", "This commit is a merge, select parent:")),
            Foreground = text,
            Margin = new Thickness(0, 12, 0, 4),
        };
        _parentsList = new ListBox
        {
            Background = Brush("App.Panel", "#252526"),
            Foreground = text,
            MaxHeight = 140,
            FontSize = 12,
        };
        _autoCommit = new CheckBox
        {
            Content = Strip(T("FormCherryPick/cbxAutoCommit.Text", "Automatically create a commit")),
            Foreground = text,
            IsChecked = autoCommit,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _addReference = new CheckBox
        {
            Content = Strip(T("FormCherryPick/cbxAddReference.Text", "Add commit reference to commit message")),
            Foreground = text,
            IsChecked = addReference,
            [ToolTip.TipProperty] = T("git cherry-pick -x: append a \"(cherry picked from commit …)\" line to the message."),
        };

        _status = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
        };

        // Subscribed after _status exists — the lambda reads it.
        _parentsList.SelectionChanged += (_, _) =>
        {
            // The refusal message clears the moment a parent is picked.
            if (_parentsList.SelectedIndex >= 0)
            {
                _status.Text = string.Empty;
            }
        };

        _pick = new Button
        {
            Content = Strip(T("FormCherryPick/btnPick.Text", "Cherry pick")),
            MinWidth = 100,
            IsDefault = true,
        };
        _pick.Click += (_, _) => _ = PickAsync();
        Button cancel = new()
        {
            Content = Strip(T("FormCherryPick/btnAbort.Text", "Abort")),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = Strip(T("FormCherryPick/lblBranchInfo.Text", "Cherry pick this commit:")),
                    Foreground = dim,
                    Margin = new Thickness(0, 0, 0, 4),
                },
                summaryPanel,
                _choose,
                _parentsLabel,
                _parentsList,
                _autoCommit,
                _addReference,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { _pick, cancel },
                },
            },
        };

        DialogKeys.InstallEscapeClose(this);
        Apply(data);
    }

    // Fills the summary panel and the parents list from one candidate — the port of
    // upstream's OnRevisionChanged.
    private void Apply(CandidateData data)
    {
        _hash = data.Hash;
        _summaryHash.Text = ShortHash(data.Hash);

        if (data.Info is null)
        {
            _summarySubject.Text = T("(commit details unavailable)");
            _summaryAuthor.Text = string.Empty;
        }
        else
        {
            _summarySubject.Text = data.Info.Subject;
            _summaryAuthor.Text = $"{data.Info.Author} — {data.Info.AuthorDate}";
        }

        bool isMerge = data.Parents.Count > 0;
        _parentsLabel.IsVisible = isMerge;
        _parentsList.IsVisible = isMerge;
        _parentsList.ItemsSource = data.Parents;
        if (isMerge)
        {
            // Upstream preselects the first parent (lvParentsList.TopItem.Selected),
            // which is the mainline a plain `git merge` on this branch would have had.
            _parentsList.SelectedIndex = 0;
        }

        _status.Text = string.Empty;
    }

    /// <summary>
    ///  Retargets the dialog through the commit picker (upstream's
    ///  <c>btnChooseRevision_Click</c>). A cancel changes nothing.
    /// </summary>
    private async Task ChooseAnotherAsync()
    {
        ChosenCommit? chosen = await ChooseCommitDialog.ShowAsync(
            this,
            _repoPath,
            new ChooseCommitRequest(
                Strip(T("FormCherryPick/$this.Text", "Cherry pick commit")),
                T("The commit picked here replaces the one the dialog opened on."),
                Preselect: _hash));

        if (chosen is null)
        {
            return;
        }

        SetBusy(true);
        string repo = _repoPath;
        CandidateData data = await Task.Run(() => Load(repo, chosen.Hash));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Apply(data);
            SetBusy(false);
        });
    }

    /// <summary>
    ///  Upstream's <c>btnPick_Click</c>: refuse a merge with no parent chosen, build
    ///  <c>-m N</c> / <c>-x</c>, persist the checkboxes, run the pick in the process
    ///  dialog and close. "Don't verify whether the command is successful. If it
    ///  fails, likely there is a conflict that needs to be resolved" — same stance
    ///  here: the result is reported, and the caller's conflict flow asks the
    ///  question that matters.
    /// </summary>
    private async Task PickAsync()
    {
        if (_parentsList.IsVisible && _parentsList.SelectedIndex < 0)
        {
            _status.Text = T("FormCherryPick/_noneParentSelectedText.Text", "None parent is selected!");
            return;
        }

        List<string> extra = [];
        if (_parentsList.IsVisible)
        {
            extra.Add($"-m {_parentsList.SelectedIndex + 1}");
        }

        if (_addReference.IsChecked == true)
        {
            extra.Add("-x");
        }

        bool autoCommit = _autoCommit.IsChecked == true;
        bool addReference = _addReference.IsChecked == true;
        string arguments = string.Join(' ', extra);
        string repo = _repoPath;
        string hash = _hash;

        // Upstream persists the two checkboxes only when the dialog is accepted
        // (SaveSettings gated on DialogResult.OK). Off the UI thread: it writes a file.
        _ = Task.Run(() =>
        {
            try
            {
                AppSettings.CommitAutomaticallyAfterCherryPick = autoCommit;
                AppSettings.AddCommitReferenceToCherryPick = addReference;
            }
            catch (Exception)
            {
                // A settings store that refuses to write must not fail the pick.
            }
        });

        // Non-interactive on purpose: cherry-pick asks nothing on this path — a clean
        // pick reuses the original message without an editor, and a conflict stops the
        // command instead of prompting.
        StashOpResult? result = null;
        await GitProcessDialog.RunStreamingAsync(
            this,
            Strip(T("FormCherryPick/btnPick.Text", "Cherry pick")),
            emit =>
            {
                result = new StashOpsService().CherryPickStreaming(repo, hash, autoCommit, arguments, emit);
                return new GitProcessOutcome(result.Success, result.Output);
            },
            interactive: false);

        _result = new CherryPickDialogResult(
            Executed: true,
            Success: result?.Success == true,
            Output: result?.Output ?? string.Empty);
        Close();
    }

    private void SetBusy(bool busy)
    {
        _pick.IsEnabled = !busy;
        _choose.IsEnabled = !busy;
        _parentsList.IsEnabled = !busy;
        _autoCommit.IsEnabled = !busy;
        _addReference.IsEnabled = !busy;
    }

    private static string ShortHash(string hash) => hash.Length > 10 ? hash[..10] : hash;

    private static string Strip(string caption) => RevisionFilterDialog.StripMnemonic(caption);

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
