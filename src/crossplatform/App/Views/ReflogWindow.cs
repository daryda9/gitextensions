using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal reflog browser for the Avalonia port: lists the HEAD reflog
///  (selector, short hash, date, action) read via <see cref="ReflogService"/>,
///  and offers per-entry "Copy hash" and "Checkout this" (a detached checkout of
///  the entry's commit through <see cref="BranchTagService"/>). All git work
///  runs off the UI thread via <see cref="Task.Run"/> and marshals back with
///  <see cref="Dispatcher.UIThread"/>.
///
///  <see cref="CheckedOut"/> is set when a checkout succeeds so the caller can
///  refresh the main view after the window closes. Styled from the shared App.*
///  brushes so it matches the active (dark) theme, mirroring
///  <see cref="RemotesDialog"/>.
/// </summary>
public sealed class ReflogWindow : Theming.ZoomWindow
{
    private readonly ReflogService _service = new();
    private readonly string _repoPath;
    private readonly ListBox _list;
    private readonly Button _copyHash;
    private readonly Button _checkout;
    private readonly Button _refresh;
    private readonly Button _close;
    private readonly TextBlock _status;

    private bool _busy;

    /// <summary>
    ///  True when a checkout from the reflog succeeded, so the owner can refresh
    ///  its views once the window is dismissed.
    /// </summary>
    public bool CheckedOut { get; private set; }

    public ReflogWindow(string repoPath)
    {
        _repoPath = repoPath;

        Width = 760;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        _list = new ListBox
        {
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            FontFamily = new FontFamily("monospace"),
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, _) => DoCheckout();

        _copyHash = MakeButton();
        _checkout = MakeButton();
        _refresh = MakeButton();
        _close = MakeButton();

        _copyHash.Click += (_, _) => _ = DoCopyHashAsync();
        _checkout.Click += (_, _) => DoCheckout();
        _refresh.Click += (_, _) => ReloadList();
        _close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Width = 130,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(_copyHash);
        buttons.Children.Add(_checkout);
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_close);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_list, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(_list);
        row.Children.Add(buttons);

        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);
        body.Children.Add(row);
        Content = body;
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        Opened += (_, _) => ReloadList();
        UpdateButtons();
    }

    private ReflogEntry? Selected => _list.SelectedItem as ReflogEntry;

    private void UpdateButtons()
    {
        bool has = Selected is not null;
        _copyHash.IsEnabled = has;
        _checkout.IsEnabled = has;
    }

    private void ReloadList()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _status.Text = T("Reading reflog…");
        _ = Task.Run(() =>
        {
            IReadOnlyList<ReflogEntry> entries;
            try
            {
                entries = _service.Read(_repoPath);
            }
            catch
            {
                entries = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                _list.ItemsSource = entries;
                // One whole format per plural form: gluing a translated stem to a
                // suffix ("entr" + "y"/"ies") is an English-only trick.
                _status.Text = entries.Count switch
                {
                    0 => T("No reflog entries (or not a git repository)."),
                    1 => TranslationService.TFormat(null, "{0} reflog entry.", entries.Count),
                    _ => TranslationService.TFormat(null, "{0} reflog entries.", entries.Count),
                };
                UpdateButtons();
            });
        });
    }

    private async Task DoCopyHashAsync()
    {
        if (Selected is not { } entry)
        {
            return;
        }

        if (Clipboard is { } clip)
        {
            await clip.SetTextAsync(entry.ShortHash);
            _status.Text = TranslationService.TFormat(
                null, "Copied {0} to the clipboard.", entry.ShortHash);
        }
    }

    // Detached checkout of the selected entry's commit; on success flags
    // CheckedOut so the owner refreshes, and reports git's outcome inline.
    private void DoCheckout() => _ = DoCheckoutAsync();

    private async Task DoCheckoutAsync()
    {
        if (_busy || Selected is not { } entry)
        {
            return;
        }

        string hash = entry.ShortHash;

        // Same contract as the rest of the app: a clean tree checks out straight
        // away, a dirty one asks what to do with the pending changes first.
        LocalChangesAction? action = await CheckoutBranchDialog.AskAsync(this, _repoPath, hash);
        if (action is null)
        {
            return;
        }

        _busy = true;
        _status.Text = TranslationService.TFormat(null, "Checking out {0}…", hash);
        _ = Task.Run(() =>
        {
            BranchTagResult result;
            try
            {
                result = new BranchTagService().Checkout(_repoPath, hash, action.Value);
            }
            catch (Exception ex)
            {
                result = new BranchTagResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (result.Success)
                {
                    CheckedOut = true;
                    _status.Text = TranslationService.TFormat(
                        null, "Checked out {0} (detached).", hash);
                }
                else
                {
                    // result.Output is git's own diagnostic: it stays verbatim.
                    _status.Text = TranslationService.TFormat(
                        null, "Checkout failed: {0}", result.Output);
                }
            });
        });
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        Title = T("FormReflog/$this.Text", "Reflog");

        // Upstream's reflog grid offers "Copy SHA-1" for exactly this action (it too
        // copies the selected entry's hash), so its id is the right one even though
        // the port spells the button "Copy hash".
        _copyHash.Content = T("FormReflog/copySha1ToolStripMenuItem.Text", "Copy hash");

        // FormCheckoutRevision's own caption for "check this revision out". Its target
        // carries a WinForms accelerator; Restyle folds it away because the literal
        // passed here has none.
        _checkout.Content = T("FormCheckoutRevision/label2.Text", "Checkout this");
        _refresh.Content = T("FormBrowse/RefreshButton.ToolTipText", "Refresh");
        _close.Content = T("TranslatedStrings/_closeText.Text", "Close");

        // The status line is deliberately left as it stands: it reports the result of
        // a git run that already happened (a hash checked out, a copy confirmation),
        // and re-stating it in the new language would mean re-running the command.
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static Button MakeButton()
        => new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;
}
