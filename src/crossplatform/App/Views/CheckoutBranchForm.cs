using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  What <see cref="CheckoutBranchForm"/> returns: exactly the five arguments of the
///  core command <c>Commands.CheckoutBranch</c> (<c>src/app/GitCommands/Git/Commands.cs:10</c>),
///  plus whether the local-changes choice should be remembered.
/// </summary>
public sealed record CheckoutBranchChoice(
    string BranchName,
    bool IsRemote,
    LocalChangesAction LocalChanges,
    CheckoutNewBranchMode NewBranchMode,
    string? NewBranchName,
    bool SetLocalChangesAsDefault);

/// <summary>
///  Full port of upstream <c>FormCheckoutBranch</c> — the dialog that makes checking out
///  a <b>remote</b> branch possible from the GUI.
///
///  <para>Three groups, laid out like the original:</para>
///  <list type="number">
///   <item><b>branch selection</b>: "Local branch" / "Remote branch" radios switching the
///    list, an editable branch box (type-to-filter, as upstream's editable combo) and the
///    ahead/behind counter of upstream's <c>lbChanges</c>;</item>
///   <item><b>remote options</b> (shown only for a remote branch — upstream hides the whole
///    <c>tlpnlRemoteOptions</c> row otherwise): the three <see cref="CheckoutNewBranchMode"/>
///    values, i.e. "create local branch with custom name" (<c>-b … --track</c>), "reset local
///    branch with the name" (<c>-B …</c>, whose caption becomes "create local branch with same
///    name" when that branch does not exist yet, exactly as
///    <c>FormCheckoutBranch.cs:469</c>), and "checkout the commit (in detached head)";</item>
///   <item><b>local changes</b> (shown only when the working tree is dirty, as upstream's
///    <c>localChangesGB.Visible = HasUncommittedChanges</c>): don't change / merge / reset /
///    stash + "Set as default".</item>
///  </list>
///
///  <para><b>Threading.</b> The dialog never calls git: every list it shows arrives in a
///  pre-loaded <see cref="CheckoutBranchData"/> (see <see cref="AskAsync"/>), and the only
///  live query — the ahead/behind counter — goes through a delegate invoked in
///  <see cref="Task.Run"/>. The services block on async work, so calling them on the UI
///  thread deadlocks the app (bug M43).</para>
///
///  <para>The lighter <see cref="CheckoutBranchDialog"/> is untouched and still serves the
///  fast path (a ref is already known, only the local-changes question remains).</para>
/// </summary>
public sealed class CheckoutBranchForm : Window
{
    private readonly CheckoutBranchData _data;
    private readonly Func<string, string>? _aheadBehind;

    private readonly RadioButton _localBranch;
    private readonly RadioButton _remoteBranch;
    private readonly AutoCompleteBox _branches;
    private readonly TextBlock _changes;

    private readonly Border _remoteOptions;
    private readonly RadioButton _createCustom;
    private readonly TextBox _customName;
    private readonly RadioButton _resetBranch;
    private readonly TextBlock _resetBranchName;
    private readonly RadioButton _detached;

    private readonly Border _localChangesBox;
    private readonly RadioButton _dontChange;
    private readonly RadioButton _merge;
    private readonly RadioButton _reset;
    private readonly RadioButton _stash;
    private readonly CheckBox _setDefault;

    private readonly TextBlock _error;

    private readonly string _resetBranchDefaultText;
    private readonly string _createSameNameText;

    private int _aheadBehindToken;

    /// <summary>The user's choice, or <c>null</c> when cancelled.</summary>
    public CheckoutBranchChoice? Result { get; private set; }

    public CheckoutBranchForm(CheckoutBranchData data, string branch, bool remote, Func<string, string>? aheadBehind = null)
    {
        _data = data;
        _aheadBehind = aheadBehind;

        IBrush text = CheckoutBranchDialog.Brush("App.Text", "#DCDCDC");
        IBrush dim = CheckoutBranchDialog.Brush("App.TextDim", "#9A9A9A");
        IBrush border = CheckoutBranchDialog.Brush("App.Border", "#3F3F3F");

        Title = T("FormCheckoutBranch/$this.Text", "Checkout branch");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = CheckoutBranchDialog.Brush("App.Window", "#1F1F1F");

        _resetBranchDefaultText = T("FormCheckoutBranch/rbResetBranch.Text", "Reset local branch with the name:");
        _createSameNameText = T("FormCheckoutBranch/_createBranch.Text", "Create local branch with same name:");

        // --- 1. which branch --------------------------------------------------
        const string kindGroup = "checkoutBranchKind";
        _localBranch = new RadioButton
        {
            GroupName = kindGroup,
            Content = T("FormCheckoutBranch/LocalBranch.Text", "Local branch"),
            Foreground = text,
        };
        _remoteBranch = new RadioButton
        {
            GroupName = kindGroup,
            Content = T("FormCheckoutBranch/Remotebranch.Text", "Remote branch"),
            Foreground = text,
            Margin = new Thickness(16, 0, 0, 0),
        };

        // Upstream's Branches is an editable combo: pick from the list or type a name.
        // AutoCompleteBox is the Avalonia control with those two behaviours in one.
        _branches = new AutoCompleteBox
        {
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,
            MaxDropDownHeight = 260,
            Watermark = T("Type or pick a branch"),
        };

        _changes = new TextBlock
        {
            Foreground = dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 90,
        };

        // --- 2. remote options ------------------------------------------------
        const string modeGroup = "checkoutNewBranchMode";
        _createCustom = new RadioButton
        {
            GroupName = modeGroup,
            Content = T("FormCheckoutBranch/rbCreateBranchWithCustomName.Text", "Create local branch with custom name:"),
            Foreground = text,
        };
        _customName = new TextBox { Margin = new Thickness(22, 4, 0, 0), IsEnabled = false };
        _createCustom.IsCheckedChanged += (_, _) =>
        {
            _customName.IsEnabled = _createCustom.IsChecked == true;
            if (_customName.IsEnabled)
            {
                _customName.SelectAll();
            }
        };

        _resetBranch = new RadioButton
        {
            GroupName = modeGroup,
            Content = _resetBranchDefaultText,
            Foreground = text,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _resetBranchName = new TextBlock
        {
            Foreground = dim,
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
            Margin = new Thickness(22, 2, 0, 0),
        };

        _detached = new RadioButton
        {
            GroupName = modeGroup,
            Content = T("FormCheckoutBranch/rbDontCreate.Text", "Checkout the commit (in detached head)"),
            Foreground = text,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // Upstream's default is AppSettings.CreateLocalBranchForRemote, which ships
        // false, so the designer's rbResetBranch.Checked = true wins.
        _resetBranch.IsChecked = true;

        _remoteOptions = new Border
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 0),
            IsVisible = false,
            Child = new StackPanel
            {
                Children = { _createCustom, _customName, _resetBranch, _resetBranchName, _detached },
            },
        };

        // --- 3. local changes -------------------------------------------------
        const string changesGroup = "checkoutLocalChanges";
        _dontChange = Radio(changesGroup, T("FormCheckoutBranch/rbDontChange.Text", "Don't change"), text);
        _merge = Radio(changesGroup, T("FormCheckoutBranch/rbMerge.Text", "Merge"), text);
        _reset = Radio(changesGroup, T("FormCheckoutBranch/rbReset.Text", "Reset"), CheckoutBranchDialog.Danger);
        _stash = Radio(changesGroup, T("FormCheckoutBranch/rbStash.Text", "Stash"), text);

        switch (data.DefaultLocalChanges)
        {
            case LocalChangesAction.Merge: _merge.IsChecked = true; break;
            case LocalChangesAction.Reset: _reset.IsChecked = true; break;
            case LocalChangesAction.Stash: _stash.IsChecked = true; break;
            default: _dontChange.IsChecked = true; break;
        }

        _setDefault = new CheckBox
        {
            Content = T("FormCheckoutBranch/chkSetLocalChangesActionAsDefault.Text", "Set as default"),
            Foreground = text,
            Margin = new Thickness(0, 8, 0, 0),
        };

        // Upstream never remembers "Reset" as a default: too destructive
        // (rbReset_CheckedChanged).
        void SyncDefaultEnabled()
        {
            bool destructive = _reset.IsChecked == true;
            _setDefault.IsEnabled = !destructive;
            if (destructive)
            {
                _setDefault.IsChecked = false;
            }
        }

        foreach (RadioButton r in new[] { _dontChange, _merge, _reset, _stash })
        {
            r.IsCheckedChanged += (_, _) => SyncDefaultEnabled();
        }

        SyncDefaultEnabled();

        _localChangesBox = new Border
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 0),
            // Upstream: localChangesGB.Visible = HasUncommittedChanges.
            IsVisible = data.WorkingTree.IsDirty,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = TF("{0} ({1} uncommitted)", T("FormCheckoutBranch/localChangesGB.Text", "Local changes"), data.WorkingTree.ChangedCount),
                        Foreground = text,
                        FontWeight = FontWeight.Bold,
                        Margin = new Thickness(0, 0, 0, 6),
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 14,
                        Children = { _dontChange, _merge, _reset, _stash },
                    },
                    _setDefault,
                },
            },
        };

        _error = new TextBlock
        {
            Foreground = CheckoutBranchDialog.Danger,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            IsVisible = false,
        };

        Button ok = new() { Content = T("FormCheckoutBranch/Ok.Text", "Checkout"), MinWidth = 100, IsDefault = true };
        ok.Click += (_, _) => Confirm();

        Button cancel = new()
        {
            Content = T("TranslatedStrings/_cancelText.Text", "Cancel"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        Grid selection = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetColumn(_branches, 0);
        Grid.SetColumn(_changes, 1);
        selection.Children.Add(_branches);
        selection.Children.Add(_changes);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { _localBranch, _remoteBranch },
                },
                selection,
                _remoteOptions,
                _localChangesBox,
                _error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { ok, cancel },
                },
            },
        };

        // Wire the reactions only now that every control exists: switching the branch
        // kind repopulates the list and re-runs the selection logic, as upstream's
        // RemoteBranchCheckedChanged → PopulateBranches → Branches_SelectedIndexChanged.
        _localBranch.IsCheckedChanged += (_, _) => BranchKindChanged();
        _remoteBranch.IsCheckedChanged += (_, _) => BranchKindChanged();
        _branches.TextChanged += (_, _) => BranchSelectionChanged();

        _localBranch.IsChecked = !remote;
        _remoteBranch.IsChecked = remote;
        PopulateBranches();

        // Upstream adds the requested branch to the list even when it is not in it,
        // then selects it (FormCheckoutBranch.cs:68-72).
        if (!string.IsNullOrWhiteSpace(branch))
        {
            _branches.Text = branch;
        }

        BranchSelectionChanged();

        DialogKeys.InstallEscapeClose(this);
        Opened += (_, _) => _branches.Focus();
    }

    private bool IsRemote => _remoteBranch.IsChecked == true;

    private LocalChangesAction ChangesMode => _reset.IsChecked == true ? LocalChangesAction.Reset
        : _merge.IsChecked == true ? LocalChangesAction.Merge
        : _stash.IsChecked == true ? LocalChangesAction.Stash
        : LocalChangesAction.DontChange;

    private void BranchKindChanged()
    {
        _remoteOptions.IsVisible = IsRemote;
        PopulateBranches();
        BranchSelectionChanged();
    }

    private void PopulateBranches()
    {
        _branches.ItemsSource = IsRemote ? _data.RemoteBranches : _data.LocalBranches;
    }

    // Upstream's Branches_SelectedIndexChanged: recompute the tracking branch name, the
    // default custom name, the caption of the reset radio, and refresh the ahead/behind
    // counter off the UI thread.
    private void BranchSelectionChanged()
    {
        string branch = (_branches.Text ?? string.Empty).Trim();

        if (branch.Length == 0 || !IsRemote)
        {
            _resetBranchName.Text = string.Empty;
            _resetBranch.Content = _resetBranchDefaultText;
        }
        else
        {
            RemoteBranchNaming naming = _data.NamingFor(branch);
            string tracking = naming.TrackingBranch.Length > 0 ? naming.TrackingBranch : naming.ShortName;

            _resetBranchName.Text = $"'{tracking}'";
            _resetBranch.Content = _data.LocalBranchExists(tracking) ? _resetBranchDefaultText : _createSameNameText;
            _customName.Text = DefaultNewBranchName(naming);
        }

        RefreshAheadBehind(branch);
    }

    // Upstream: "<remote>_<remoteBranchName>", de-duplicated against the existing local
    // branches with a numeric suffix (FormCheckoutBranch.cs:455-465).
    private string DefaultNewBranchName(RemoteBranchNaming naming)
    {
        string candidate = naming.Remote.Length > 0 ? $"{naming.Remote}_{naming.ShortName}" : naming.ShortName;
        string unique = candidate;
        int i = 2;
        while (_data.LocalBranchExists(unique))
        {
            unique = $"{candidate}_{i}";
            i++;
        }

        return unique;
    }

    private void RefreshAheadBehind(string branch)
    {
        _changes.Text = string.Empty;
        if (_aheadBehind is null || branch.Length == 0)
        {
            return;
        }

        int token = ++_aheadBehindToken;
        Func<string, string> probe = _aheadBehind;
        _ = Task.Run(() =>
        {
            string info;
            try
            {
                info = probe(branch);
            }
            catch
            {
                info = string.Empty;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Ignore a probe whose branch is no longer the selected one.
                if (token == _aheadBehindToken)
                {
                    _changes.Text = info;
                }
            });
        });
    }

    private void Confirm()
    {
        string branch = (_branches.Text ?? string.Empty).Trim();
        IReadOnlyList<string> known = IsRemote ? _data.RemoteBranches : _data.LocalBranches;

        // Upstream Branches_Validating: "An existing branch must be selected."
        if (branch.Length == 0 || !known.Contains(branch, StringComparer.Ordinal))
        {
            Fail(T("FormCheckoutBranch/_invalidBranchName.Text", "An existing branch must be selected."));
            return;
        }

        CheckoutNewBranchMode mode = CheckoutNewBranchMode.DontCreate;
        string? newBranchName = null;

        if (IsRemote)
        {
            if (_createCustom.IsChecked == true)
            {
                mode = CheckoutNewBranchMode.Create;
                newBranchName = (_customName.Text ?? string.Empty).Trim();
                if (newBranchName.Length == 0)
                {
                    Fail(T("FormCheckoutBranch/_customBranchNameIsEmpty.Text",
                        "Custom branch name is empty. Enter valid branch name or select predefined value."));
                    return;
                }

                if (!IsValidBranchName(newBranchName))
                {
                    Fail(TF("“{0}” is not valid branch name.", newBranchName));
                    return;
                }
            }
            else if (_resetBranch.IsChecked == true)
            {
                mode = CheckoutNewBranchMode.Reset;
                RemoteBranchNaming naming = _data.NamingFor(branch);
                newBranchName = naming.TrackingBranch.Length > 0 ? naming.TrackingBranch : naming.ShortName;
                if (newBranchName.Length == 0)
                {
                    Fail(T("FormCheckoutBranch/_invalidBranchName.Text", "An existing branch must be selected."));
                    return;
                }
            }
        }

        LocalChangesAction changes = _localChangesBox.IsVisible ? ChangesMode : LocalChangesAction.DontChange;

        Result = new CheckoutBranchChoice(
            branch,
            IsRemote,
            changes,
            mode,
            newBranchName,
            _setDefault.IsChecked == true && changes != LocalChangesAction.Reset);

        Close();
    }

    private void Fail(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
    }

    /// <summary>
    ///  Local stand-in for upstream's <c>Module.CheckBranchFormat</c> (which shells out to
    ///  <c>git check-ref-format</c> — not callable from the UI thread). Same rules that
    ///  <c>git check-ref-format --branch</c> enforces, so a name accepted here is accepted
    ///  by git; a name rejected here would have been rejected by git too.
    /// </summary>
    internal static bool IsValidBranchName(string name)
    {
        if (name.Length == 0 || name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/')
            || name.EndsWith(".lock", StringComparison.Ordinal) || name.EndsWith('.')
            || name.Contains("..", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal) || name == "@")
        {
            return false;
        }

        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\' or '\x7f')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///  Loads the branch lists / working-tree state off the UI thread, shows the form
    ///  pre-selected on <paramref name="branch"/> and returns the choice (<c>null</c> when
    ///  cancelled or when there is no window to parent the modal to).
    /// </summary>
    public static async Task<CheckoutBranchChoice?> AskAsync(Window? owner, string repoPath, string branch = "", bool remote = false)
    {
        if (owner is null)
        {
            return null;
        }

        BranchTagService service = new();
        CheckoutBranchData data;
        try
        {
            data = await Task.Run(() =>
            {
                AppPreferences prefs = new SettingsService().Load();
                LocalChangesAction fallback = Enum.TryParse(prefs.DefaultCheckoutLocalChangesAction, out LocalChangesAction parsed)
                    ? parsed
                    : LocalChangesAction.DontChange;
                return service.LoadCheckoutBranchData(repoPath, fallback);
            });
        }
        catch
        {
            data = CheckoutBranchData.Empty;
        }

        CheckoutBranchForm form = new(data, branch, remote, b => service.GetAheadBehindInfo(repoPath, b));
        await form.ShowDialog(owner);

        if (form.Result is not { } choice)
        {
            return null;
        }

        if (choice.SetLocalChangesAsDefault)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    SettingsService settings = new();
                    AppPreferences prefs = settings.Load();
                    prefs.DefaultCheckoutLocalChangesAction = choice.LocalChanges.ToString();
                    settings.Save(prefs);
                }
                catch
                {
                    // A preference that cannot be stored must not fail the checkout.
                }
            });
        }

        return choice;
    }

    private static RadioButton Radio(string group, string label, IBrush foreground)
        => new() { GroupName = group, Content = label, Foreground = foreground };

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(null, englishFormat, args);
}
