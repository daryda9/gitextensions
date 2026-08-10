using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitCommands;
using GitCommands.Git.Tag;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The three ref-manipulation modals of the port, kept in one file because they
///  share the same plumbing (pre-loaded data in, plain DTO out, no git call of
///  their own):
///  <list type="bullet">
///   <item><see cref="CheckoutBranchDialog"/> — upstream <c>FormCheckoutBranch</c>'s
///    "Local changes" group (Don't change / Merge / Reset / Stash + "Set as
///    default"), shown <b>only</b> when the working tree is dirty.</item>
///   <item><see cref="CreateBranchDialog"/> — upstream <c>FormCreateBranch</c>:
///    branch name + "Checkout after create".</item>
///   <item><see cref="CreateTagDialog"/> — upstream <c>FormCreateTag</c>: tag name,
///    message, lightweight/annotated/signed, key id, Force, "Push tag to".</item>
///  </list>
///
///  <para><b>Threading contract.</b> The services block on async work, so calling
///  them from the UI thread deadlocks the whole app (bug M43). Every dialog here
///  therefore receives its data (working-tree state, branch names, remotes)
///  already loaded; the static <c>AskAsync</c> helpers do that loading in
///  <see cref="Task.Run"/> before constructing the window.</para>
/// </summary>
public sealed class CheckoutBranchDialog : Theming.ZoomWindow
{
    private readonly RadioButton _dontChange;
    private readonly RadioButton _merge;
    private readonly RadioButton _reset;
    private readonly RadioButton _stash;
    private readonly CheckBox _setDefault;

    /// <summary>True when the user pressed Checkout (not Cancel / window close).</summary>
    public bool Confirmed { get; private set; }

    /// <summary>What to do with the local changes.</summary>
    public LocalChangesAction SelectedAction => _reset.IsChecked == true ? LocalChangesAction.Reset
        : _merge.IsChecked == true ? LocalChangesAction.Merge
        : _stash.IsChecked == true ? LocalChangesAction.Stash
        : LocalChangesAction.DontChange;

    /// <summary>Whether the choice should be remembered for the next checkout.</summary>
    public bool SetAsDefault => _setDefault.IsChecked == true && _reset.IsChecked != true;

    public CheckoutBranchDialog(string branchName, WorkingTreeState state, LocalChangesAction initial)
    {
        IBrush window = Brush("App.Window", "#1F1F1F");
        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#9A9A9A");

        Title = T("FormCheckoutBranch/$this.Text", "Checkout branch");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = window;

        TextBlock header = new()
        {
            Text = TF("Checkout “{0}”", branchName),
            Foreground = text,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        };

        TextBlock summary = new()
        {
            Text = TF("The working directory has {0} uncommitted change(s). Choose what to do with them.", state.ChangedCount),
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        const string group = "localChanges";
        _dontChange = Radio(group, T("FormCheckoutBranch/rbDontChange.Text", "Don't change"),
            T("Keep the changes in the working directory. Git refuses the checkout if a changed file would be overwritten."), text, dim);
        _merge = Radio(group, T("FormCheckoutBranch/rbMerge.Text", "Merge"),
            T("Merge the local changes into the checked-out branch (git checkout --merge). Conflicts are left in the working directory."), text, dim);
        _reset = Radio(group, T("FormCheckoutBranch/rbReset.Text", "Reset"),
            T("DISCARD the local changes and check out (git checkout --force). The changes cannot be recovered."), Danger, Danger);
        _stash = Radio(group, T("FormCheckoutBranch/rbStash.Text", "Stash"),
            T("Save the local changes to the stash (including untracked files) and check out with a clean working directory. Pop the stash to get them back."), text, dim);

        switch (initial)
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
            Margin = new Thickness(0, 10, 0, 0),
        };

        // Upstream never remembers "Reset" as a default: too destructive.
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

        Button ok = new()
        {
            Content = T("FormCheckoutBranch/Ok.Text", "Checkout"),
            MinWidth = 90,
            IsDefault = true,
        };
        ok.Click += (_, _) => { Confirmed = true; Close(); };

        Button cancel = new()
        {
            Content = T("TranslatedStrings/_cancelText.Text", "Cancel"),
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => Close();

        StackPanel choices = new()
        {
            Spacing = 6,
            Margin = new Thickness(4, 8, 0, 0),
            Children = { _dontChange, _merge, _reset, _stash },
        };

        Border box = new()
        {
            BorderBrush = Brush("App.Border", "#3F3F3F"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = T("FormCheckoutBranch/localChangesGB.Text", "Local changes"),
                        Foreground = text,
                        FontWeight = FontWeight.Bold,
                    },
                    choices,
                    _setDefault,
                },
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                header,
                summary,
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { ok, cancel },
                },
            },
        };

        DialogKeys.InstallEscapeClose(this);
    }

    /// <summary>
    ///  Full checkout decision flow: loads the working-tree state and the stored
    ///  default off the UI thread, returns <see cref="LocalChangesAction.DontChange"/>
    ///  straight away when the tree is clean (no dialog at all), otherwise asks
    ///  and persists the choice when "Set as default" is ticked.
    ///  Returns <c>null</c> when the user cancelled.
    /// </summary>
    public static async Task<LocalChangesAction?> AskAsync(Window? owner, string repoPath, string branchName)
    {
        WorkingTreeState state;
        LocalChangesAction initial;
        try
        {
            (state, initial) = await Task.Run(() =>
            {
                WorkingTreeState s = new BranchTagService().LoadWorkingTreeState(repoPath);
                AppPreferences prefs = new SettingsService().Load();
                LocalChangesAction a = Enum.TryParse(prefs.DefaultCheckoutLocalChangesAction, out LocalChangesAction parsed)
                    ? parsed
                    : LocalChangesAction.DontChange;
                return (s, a);
            });
        }
        catch
        {
            // Never block a checkout because the pre-flight probe failed.
            return LocalChangesAction.DontChange;
        }

        // Clean working tree (or headless, with no window to parent a modal to):
        // check out immediately, exactly as before.
        if (!state.IsDirty || owner is null)
        {
            return LocalChangesAction.DontChange;
        }

        CheckoutBranchDialog dialog = new(branchName, state, initial);
        await dialog.ShowDialog(owner);
        if (!dialog.Confirmed)
        {
            return null;
        }

        LocalChangesAction action = dialog.SelectedAction;
        if (dialog.SetAsDefault)
        {
            _ = Task.Run(() =>
            {
                SettingsService settings = new();
                AppPreferences prefs = settings.Load();
                prefs.DefaultCheckoutLocalChangesAction = action.ToString();
                settings.Save(prefs);
            });
        }

        return action;
    }

    private static RadioButton Radio(string group, string label, string explanation, IBrush text, IBrush dim)
        => new()
        {
            GroupName = group,
            Foreground = text,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = label, Foreground = text },
                    new TextBlock
                    {
                        Text = explanation,
                        Foreground = dim,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420,
                    },
                },
            },
        };

    /// <summary>Warning/destructive colour: there is no App.* red in the theme.</summary>
    internal static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#D97070"));

    internal static IBrush Brush(string key, string fallback)
        => Application.Current?.Resources[key] as IBrush ?? new SolidColorBrush(Color.Parse(fallback));

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(null, englishFormat, args);
}

/// <summary>What <see cref="CreateBranchDialog"/> returns.</summary>
public sealed record CreateBranchRequest(string Name, bool Checkout);

/// <summary>
///  "Create branch" modal (upstream <c>FormCreateBranch</c>): the branch name, the
///  revision it is created at, and — the point of the exercise — a
///  <b>"Checkout after create"</b> checkbox, on by default as in the original.
///  Existing branch names are passed in so the name can be validated without
///  touching git from the UI thread.
/// </summary>
public sealed class CreateBranchDialog : Theming.ZoomWindow
{
    private readonly TextBox _name;
    private readonly CheckBox _checkout;
    private readonly TextBlock _error;

    /// <summary>The user's choice, or <c>null</c> when cancelled.</summary>
    public CreateBranchRequest? Result { get; private set; }

    public CreateBranchDialog(string startPointDisplay, IReadOnlyCollection<string> existingBranches, bool checkoutAfterCreate = true, string namePrefix = "")
    {
        IBrush text = CheckoutBranchDialog.Brush("App.Text", "#DCDCDC");
        IBrush dim = CheckoutBranchDialog.Brush("App.TextDim", "#9A9A9A");

        Title = T("FormCreateBranch/$this.Text", "Create branch");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = CheckoutBranchDialog.Brush("App.Window", "#1F1F1F");

        _name = new TextBox { Text = namePrefix ?? string.Empty };
        _checkout = new CheckBox
        {
            Content = T("FormCreateBranch/chkCheckoutAfterCreate.Text", "Checkout after create"),
            IsChecked = checkoutAfterCreate,
            Foreground = text,
            Margin = new Thickness(0, 12, 0, 0),
        };

        _error = new TextBlock
        {
            Foreground = CheckoutBranchDialog.Danger,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        Button ok = new() { Content = T("FormCreateBranch/cmdOk.Text", "Create branch"), MinWidth = 110, IsDefault = true };
        ok.Click += (_, _) =>
        {
            string name = (_name.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                _error.Text = T("FormCreateBranch/_branchNameIsEmpty.Text", "Enter branch name.");
                return;
            }

            if (existingBranches.Contains(name, StringComparer.Ordinal))
            {
                _error.Text = TF("A branch named “{0}” already exists.", name);
                return;
            }

            Result = new CreateBranchRequest(name, _checkout.IsChecked == true);
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

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = T("FormCreateBranch/lblCreateBranch.Text", "Create branch at this revision"),
                    Foreground = dim,
                },
                new TextBlock
                {
                    Text = startPointDisplay,
                    Foreground = text,
                    FontFamily = Theming.AppFonts.Monospace,
                    Margin = new Thickness(0, 2, 0, 12),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock { Text = T("FormCreateBranch/label1.Text", "Branch name"), Foreground = text, Margin = new Thickness(0, 0, 0, 4) },
                _name,
                _checkout,
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

        DialogKeys.InstallEscapeClose(this);

        Opened += (_, _) =>
        {
            _name.Focus();

            // Caret after the prefix, so typing continues the name instead of replacing
            // the "feature/" a folder node filled in.
            _name.CaretIndex = _name.Text?.Length ?? 0;
        };
    }

    /// <summary>
    ///  Loads the existing branch names off the UI thread, shows the dialog and
    ///  returns the request (or <c>null</c> when cancelled / headless).
    /// </summary>
    /// <param name="namePrefix">
    ///  Prefilled name prefix, with the caret placed after it. Used by the left panel's
    ///  branch FOLDER nodes, whose "Create Branch…" offers the folder as prefix —
    ///  upstream passes the same thing as <c>newBranchNamePrefix</c>
    ///  (<c>LeftPanel/BranchPathNode.cs:24-28</c>).
    /// </param>
    public static async Task<CreateBranchRequest?> AskAsync(Window? owner, string repoPath, string startPointDisplay, string namePrefix = "")
    {
        if (owner is null)
        {
            return null;
        }

        List<string> branches;
        try
        {
            branches = await Task.Run(() => new BranchTagService()
                .LoadRefs(repoPath).Branches
                .Where(b => !b.IsRemote)
                .Select(b => b.Name)
                .ToList());
        }
        catch
        {
            branches = [];
        }

        CreateBranchDialog dialog = new(startPointDisplay, branches, namePrefix: namePrefix);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string TF(string englishFormat, params object?[] args)
        => TranslationService.TFormat(null, englishFormat, args);
}

/// <summary>What <see cref="CreateTagDialog"/> returns.</summary>
public sealed record CreateTagRequest(
    string Name,
    string Message,
    TagOperation Operation,
    string SignKeyId,
    bool Force,
    string PushToRemote);

/// <summary>
///  "Create tag" modal (upstream <c>FormCreateTag</c>): name, message, the four
///  tag kinds (lightweight / annotated / signed with the default GPG key /
///  signed with a specific key), <b>Force</b>, and an optional
///  <b>push to remote</b> right after creating it.
/// </summary>
public sealed class CreateTagDialog : Theming.ZoomWindow
{
    private readonly TextBox _name;
    private readonly TextBox _message;
    private readonly ComboBox _kind;
    private readonly TextBox _keyId;
    private readonly CheckBox _force;
    private readonly CheckBox _push;
    private readonly ComboBox _remote;
    private readonly TextBlock _error;

    /// <summary>The user's choice, or <c>null</c> when cancelled.</summary>
    public CreateTagRequest? Result { get; private set; }

    public CreateTagDialog(string commitDisplay, IReadOnlyList<string> remotes)
    {
        IBrush text = CheckoutBranchDialog.Brush("App.Text", "#DCDCDC");
        IBrush dim = CheckoutBranchDialog.Brush("App.TextDim", "#9A9A9A");

        Title = T("FormCreateTag/$this.Text", "Create tag");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = CheckoutBranchDialog.Brush("App.Window", "#1F1F1F");

        _name = new TextBox();
        _message = new TextBox
        {
            AcceptsReturn = true,
            MinHeight = 70,
            TextWrapping = TextWrapping.Wrap,
        };

        _kind = new ComboBox
        {
            ItemsSource = new[]
            {
                T("FormCreateTag/_trsLightweight.Text", "Lightweight tag"),
                T("FormCreateTag/_trsAnnotated.Text", "Annotated tag"),
                T("FormCreateTag/_trsSignDefault.Text", "Sign with default GPG"),
                T("FormCreateTag/_trsSignSpecificKey.Text", "Sign with specific GPG"),
            },
            SelectedIndex = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 220,
        };

        _keyId = new TextBox { IsEnabled = false, Watermark = T("FormCreateTag/keyIdLbl.Text", "Specific Key Id") };
        _kind.SelectionChanged += (_, _) => _keyId.IsEnabled = _kind.SelectedIndex == 3;

        _force = new CheckBox
        {
            Content = T("FormCreateTag/ForceTag.Text", "Force"),
            Foreground = text,
        };

        _remote = new ComboBox
        {
            ItemsSource = remotes,
            SelectedIndex = remotes.Count > 0 ? 0 : -1,
            IsEnabled = false,
            MinWidth = 140,
            Margin = new Thickness(8, 0, 0, 0),
        };

        _push = new CheckBox
        {
            Content = T("Push tag to"),
            Foreground = text,
            IsEnabled = remotes.Count > 0,
        };
        _push.IsCheckedChanged += (_, _) => _remote.IsEnabled = _push.IsChecked == true;

        _error = new TextBlock
        {
            Foreground = CheckoutBranchDialog.Danger,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        Button ok = new() { Content = T("FormCreateTag/Ok.Text", "Create tag"), MinWidth = 100, IsDefault = true };
        ok.Click += (_, _) =>
        {
            string name = (_name.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                _error.Text = T("Enter tag name.");
                return;
            }

            TagOperation op = _kind.SelectedIndex switch
            {
                0 => TagOperation.Lightweight,
                2 => TagOperation.SignWithDefaultKey,
                3 => TagOperation.SignWithSpecificKey,
                _ => TagOperation.Annotate,
            };

            string keyId = (_keyId.Text ?? string.Empty).Trim();
            if (op == TagOperation.SignWithSpecificKey && keyId.Length == 0)
            {
                _error.Text = T("Enter the GPG key id to sign with.");
                return;
            }

            Result = new CreateTagRequest(
                name,
                _message.Text ?? string.Empty,
                op,
                keyId,
                _force.IsChecked == true,
                _push.IsChecked == true && _remote.SelectedItem is string r ? r : string.Empty);
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

        StackPanel pushRow = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { _push, _remote },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = T("FormCreateTag/label3.Text", "Create tag at this revision"), Foreground = dim },
                new TextBlock
                {
                    Text = commitDisplay,
                    Foreground = text,
                    FontFamily = Theming.AppFonts.Monospace,
                    Margin = new Thickness(0, 2, 0, 12),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock { Text = T("FormCreateTag/label1.Text", "Tag name"), Foreground = text, Margin = new Thickness(0, 0, 0, 4) },
                _name,
                new TextBlock { Text = T("FormCreateTag/label2.Text", "Message"), Foreground = text, Margin = new Thickness(0, 10, 0, 4) },
                _message,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children = { _kind, _force },
                },
                _keyId,
                pushRow,
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

        DialogKeys.InstallEscapeClose(this);

        Opened += (_, _) => _name.Focus();
    }

    /// <summary>
    ///  Loads the remote names off the UI thread, shows the dialog and returns the
    ///  request (or <c>null</c> when cancelled / headless).
    /// </summary>
    public static async Task<CreateTagRequest?> AskAsync(Window? owner, string repoPath, string commitDisplay)
    {
        if (owner is null)
        {
            return null;
        }

        IReadOnlyList<string> remotes;
        try
        {
            remotes = await Task.Run(() => new BranchTagService().LoadRemotes(repoPath));
        }
        catch
        {
            remotes = [];
        }

        CreateTagDialog dialog = new(commitDisplay, remotes);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
