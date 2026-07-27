using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Remotes manager" for the Avalonia port: lists the repository's
///  configured remotes (name + fetch URL) and offers Add / Edit URL / Rename /
///  Remove, each delegating to <see cref="RemoteService"/> (which shells out to
///  <c>git remote …</c>). All git work runs off the UI thread via
///  <see cref="Task.Run"/> and marshals back with <see cref="Dispatcher.UIThread"/>.
///  <see cref="Changed"/> is set when any mutation succeeds so the caller can
///  refresh the repository tree after the dialog closes.
/// </summary>
public sealed class RemotesDialog : Window
{
    private readonly RemoteService _service = new();
    private readonly string _repoPath;
    private readonly ListBox _list;
    private readonly Button _editUrl;
    private readonly Button _rename;
    private readonly Button _remove;

    private bool _busy;

    /// <summary>
    ///  True when at least one add/rename/set-url/remove succeeded, so the owner
    ///  can refresh its view once the dialog is dismissed.
    /// </summary>
    public bool Changed { get; private set; }

    public RemotesDialog(string repoPath)
    {
        _repoPath = repoPath;

        Title = "Remotes";
        Width = 520;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Panel", Brushes.DimGray);

        _list = new ListBox
        {
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, _) => _ = DoEditUrlAsync();

        Button add = MakeButton("Add…");
        _editUrl = MakeButton("Edit URL…");
        _rename = MakeButton("Rename…");
        _remove = MakeButton("Remove");
        Button close = MakeButton("Close");

        add.Click += (_, _) => _ = DoAddAsync();
        _editUrl.Click += (_, _) => _ = DoEditUrlAsync();
        _rename.Click += (_, _) => _ = DoRenameAsync();
        _remove.Click += (_, _) => _ = DoRemoveAsync();
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Width = 120,
        };
        buttons.Children.Add(add);
        buttons.Children.Add(_editUrl);
        buttons.Children.Add(_rename);
        buttons.Children.Add(_remove);
        buttons.Children.Add(close);

        Grid body = new()
        {
            Margin = new Thickness(12),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        Grid.SetColumn(_list, 0);
        buttons.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(buttons, 1);
        body.Children.Add(_list);
        body.Children.Add(buttons);

        Content = body;

        Opened += (_, _) => ReloadList();
        UpdateButtons();
    }

    private void ReloadList()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            IReadOnlyList<RemoteRow> remotes;
            try
            {
                remotes = _service.ListRemotes(_repoPath);
            }
            catch
            {
                remotes = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                string? keep = Selected?.Name;
                _list.ItemsSource = remotes;
                if (keep is not null)
                {
                    _list.SelectedItem = remotes.FirstOrDefault(r => r.Name == keep);
                }

                UpdateButtons();
            });
        });
    }

    private RemoteRow? Selected => _list.SelectedItem as RemoteRow;

    private void UpdateButtons()
    {
        bool has = Selected is not null;
        _editUrl.IsEnabled = has;
        _rename.IsEnabled = has;
        _remove.IsEnabled = has;
    }

    // --- Operations -------------------------------------------------------

    private async Task DoAddAsync()
    {
        string? name = await PromptAsync("New remote name:", string.Empty);
        if (name is not { Length: > 0 })
        {
            return;
        }

        if (RemoteExists(name))
        {
            await ShowErrorAsync("Add remote", $"A remote named '{name}' already exists.");
            return;
        }

        string? url = await PromptAsync($"URL for remote '{name}':", string.Empty);
        if (url is not { Length: > 0 })
        {
            return;
        }

        RunMutation($"Add remote '{name}'", () => _service.AddRemote(_repoPath, name, url));
    }

    private async Task DoEditUrlAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        string? url = await PromptAsync($"URL for remote '{row.Name}':", row.FetchUrl);
        if (url is { Length: > 0 } target && !string.Equals(target, row.FetchUrl, StringComparison.Ordinal))
        {
            RunMutation($"Set URL of '{row.Name}'", () => _service.SetRemoteUrl(_repoPath, row.Name, target));
        }
    }

    private async Task DoRenameAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        string? name = await PromptAsync($"Rename remote '{row.Name}' to:", row.Name);
        if (name is not { Length: > 0 } target || string.Equals(target, row.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (RemoteExists(target))
        {
            await ShowErrorAsync("Rename remote", $"A remote named '{target}' already exists.");
            return;
        }

        RunMutation($"Rename '{row.Name}' to '{target}'", () => _service.RenameRemote(_repoPath, row.Name, target));
    }

    private async Task DoRemoveAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (await ConfirmAsync($"Remove remote '{row.Name}'?"))
        {
            RunMutation($"Remove remote '{row.Name}'", () => _service.RemoveRemote(_repoPath, row.Name));
        }
    }

    /// <summary>
    ///  Runs a remote mutation off the UI thread and — crucially — SHOWS git's own
    ///  message when it fails. The previous version kept only
    ///  <see cref="RemoteOpResult.Success"/> and threw the output away, so a
    ///  rejected add/rename/remove looked exactly like a successful one: the list
    ///  simply reloaded unchanged. Upstream surfaces the same text
    ///  (<c>FormRemotes</c> shows <c>result.UserMessage</c> / the <c>RemoveRemote</c>
    ///  output in a message box).
    /// </summary>
    /// <param name="label">What was attempted, used as the error caption.</param>
    private void RunMutation(string label, Func<RemoteOpResult> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _ = Task.Run(() =>
        {
            RemoteOpResult result;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                result = new RemoteOpResult(false, ex.GetBaseException().Message, AuthFailed: false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (result.Success)
                {
                    Changed = true;
                }

                ReloadList();

                if (!result.Success)
                {
                    // Git sometimes fails with no output at all (killed process,
                    // missing git); still tell the user something concrete.
                    string message = string.IsNullOrWhiteSpace(result.Output)
                        ? "git reported no output."
                        : result.Output.Trim();
                    _ = ShowErrorAsync(label, message);
                }
            });
        });
    }

    /// <summary>Shows git's failure text in a modal, dismissable box.</summary>
    private async Task ShowErrorAsync(string label, string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button ok = new() { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right };
        Window dialog = new()
        {
            Title = label,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        ok.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(true);

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"{label} failed:",
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            MaxHeight = 220,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("monospace"),
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        });
        content.Children.Add(ok);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        await tcs.Task;
    }

    /// <summary>
    ///  True when <paramref name="name"/> is already a configured remote. Adding or
    ///  renaming onto an existing name is refused up front, with the reason — git
    ///  would fail anyway, and upstream validates the same thing in
    ///  <c>FormRemotes.ValidateRemoteDoesNotExist</c>.
    /// </summary>
    private bool RemoteExists(string name)
        => _list.ItemsSource is IEnumerable<RemoteRow> rows
            && rows.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    // --- Inline prompt / confirm (mirrors RepoObjectsTree helpers) --------

    private async Task<bool> ConfirmAsync(string message)
    {
        TaskCompletionSource<bool> tcs = new();

        Button yes = new() { Content = "Confirm", Margin = new Thickness(0, 0, 6, 0) };
        Button no = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Confirm",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task<string?> PromptAsync(string message, string initial)
    {
        TaskCompletionSource<string?> tcs = new();

        TextBox input = new() { Text = initial };
        Button ok = new() { Content = "OK", Margin = new Thickness(0, 0, 6, 0) };
        Button cancel = new() { Content = "Cancel" };
        Window dialog = new()
        {
            Title = "Remote",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Panel", Brushes.DimGray),
        };
        ok.Click += (_, _) => { tcs.TrySetResult(input.Text?.Trim()); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                tcs.TrySetResult(input.Text?.Trim());
                dialog.Close();
                e.Handled = true;
            }
        };
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    private static Button MakeButton(string text)
        => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;
}
