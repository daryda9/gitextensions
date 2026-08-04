using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Submodules manager" for the Avalonia port: lists the repository's
///  submodules (path + coarse state) and offers Update (selected), Update all,
///  Synchronize all and Init all, each delegating to <see cref="SubmoduleService"/>
///  (which shells out to <c>git submodule …</c>). Command output/status is shown
///  in a read-only pane, and the list re-reflects the new state after every
///  action. All git work runs off the UI thread via <see cref="Task.Run"/> and
///  marshals back with <see cref="Dispatcher.UIThread"/>. <see cref="Changed"/>
///  is set when any mutation succeeds so the caller can refresh the repository
///  tree after the dialog closes.
/// </summary>
public sealed class SubmodulesDialog : Theming.ZoomWindow
{
    private readonly SubmoduleService _service = new();
    private readonly string _repoPath;
    private readonly ListBox _list;
    private readonly Button _update;
    private readonly TextBox _output;
    private readonly TextBlock _status;

    private bool _busy;

    /// <summary>
    ///  True when at least one update/sync/init succeeded, so the owner can
    ///  refresh its view once the dialog is dismissed.
    /// </summary>
    public bool Changed { get; private set; }

    public SubmodulesDialog(string repoPath)
    {
        _repoPath = repoPath;

        Title = "Submodules";
        Width = 640;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Panel", Brushes.DimGray);

        _list = new ListBox
        {
            Background = Brush("App.PanelAlt", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, _) => DoUpdateSelected();

        _update = MakeButton("Update");
        Button updateAll = MakeButton("Update all");
        Button syncAll = MakeButton("Synchronize all");
        // "Init all" runs a real `git submodule init` — it registers the submodules
        // from .gitmodules into .git/config WITHOUT cloning or checking anything
        // out. It used to call UpdateAll, making it a mislabelled duplicate of
        // "Update all" that quietly did far more than initialise.
        Button initAll = MakeButton("Init all");
        ToolTip.SetTip(initAll, "git submodule init — registers the submodules in .git/config; does not fetch or check out.");
        Button close = MakeButton("Close");

        _update.Click += (_, _) => DoUpdateSelected();
        updateAll.Click += (_, _) => Run("Update all", () => _service.UpdateAll(_repoPath));
        syncAll.Click += (_, _) => Run("Synchronize all", () => _service.SynchronizeAll(_repoPath));
        initAll.Click += (_, _) => Run("Init all", () => _service.InitAll(_repoPath));
        close.Click += (_, _) => Close();

        // Escape = Close (upstream's CancelButton). Bubbling, so inner popups keep
        // their own Escape; Close() does not touch <see cref="Changed"/>.
        KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Width = 140,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(_update);
        buttons.Children.Add(updateAll);
        buttons.Children.Add(syncAll);
        buttons.Children.Add(initAll);
        buttons.Children.Add(new Border { Height = 8 });
        buttons.Children.Add(close);

        Grid top = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_list, 0);
        Grid.SetColumn(buttons, 1);
        top.Children.Add(_list);
        top.Children.Add(buttons);

        _status = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gainsboro),
            Margin = new Thickness(0, 8, 0, 4),
            Text = string.Empty,
        };

        // TextBoxSurface: see OutputView — the Fluent per-state repaint beats the local
        // Background, so clicking this read-only log flipped its surface to pure
        // black (dark) / pure white (light).
        _output = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Height = 120,
                FontFamily = new FontFamily("monospace"),
                VerticalContentAlignment = VerticalAlignment.Top,
            },
            Brush("App.PanelAlt", Brushes.Black),
            Brush("App.Text", Brushes.Gainsboro));

        Grid body = new()
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
        };
        Grid.SetRow(top, 0);
        Grid.SetRow(_status, 1);
        Grid.SetRow(_output, 2);
        body.Children.Add(top);
        body.Children.Add(_status);
        body.Children.Add(_output);

        Content = body;
        DialogKeys.EnsureFocusRoute(this);

        Opened += (_, _) => ReloadList();
        UpdateButtons();
    }

    private SubmoduleItem? Selected => _list.SelectedItem as SubmoduleItem;

    private void UpdateButtons() => _update.IsEnabled = Selected is not null && !_busy;

    private void DoUpdateSelected()
    {
        if (Selected is not { } item)
        {
            return;
        }

        Run($"Update '{item.Row.Path}'", () => _service.Update(_repoPath, item.Row.Path));
    }

    private void ReloadList()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        _ = Task.Run(() =>
        {
            IReadOnlyList<SubmoduleRow> rows;
            try
            {
                rows = _service.ListSubmodules(_repoPath);
            }
            catch
            {
                rows = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                string? keep = Selected?.Row.Path;
                List<SubmoduleItem> items = rows.Select(r => new SubmoduleItem(r)).ToList();
                _list.ItemsSource = items;
                if (keep is not null)
                {
                    _list.SelectedItem = items.FirstOrDefault(i => i.Row.Path == keep);
                }

                UpdateButtons();
            });
        });
    }

    private void Run(string label, Func<SubmoduleOpResult> work)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _status.Text = $"{label}…";
        UpdateButtons();
        _ = Task.Run(() =>
        {
            SubmoduleOpResult result;
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                result = new SubmoduleOpResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (result.Success)
                {
                    Changed = true;
                }

                _status.Text = $"{label}: {(result.Success ? "OK" : "failed")}";
                _output.Text = result.Output;
                ReloadList();
            });
        });
    }

    private static Button MakeButton(string text)
        => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    // Display wrapper: the ListBox renders ToString(), so fold the coarse state
    // into a human-readable label while keeping the underlying row for actions.
    private sealed record SubmoduleItem(SubmoduleRow Row)
    {
        public override string ToString() => Row.Status switch
        {
            SubmoduleState.NotInitialized => $"{Row.Display}  (not initialized)",
            SubmoduleState.OutOfDate => $"{Row.Display}  (out of date)",
            SubmoduleState.Initialized => $"{Row.Display}  (up to date)",
            _ => Row.Display,
        };
    }
}
