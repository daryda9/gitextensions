using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Git maintenance" dialog for the Avalonia / Linux port, mirroring the
///  original GitExtensions maintenance menu. It offers four housekeeping
///  actions on the current repository and shows their output in a read-only
///  panel:
///
///  <list type="bullet">
///    <item><description>Compress database — <c>git gc</c></description></item>
///    <item><description>Verify database — <c>git fsck</c></description></item>
///    <item><description>Delete <c>.git/index.lock</c></description></item>
///    <item><description>Edit <c>.git/config</c> (opens in the default editor via <c>xdg-open</c>)</description></item>
///  </list>
///
///  The git-backed operations (<c>gc</c>/<c>fsck</c>) run off the UI thread via
///  <see cref="Task.Run"/> so a long compaction never freezes the window; every
///  handler is exception-guarded and reports failures as text rather than
///  throwing on the UI thread. Colors come from the shared <c>App.*</c> palette.
/// </summary>
public sealed class MaintenanceDialog : Window
{
    private readonly string _repoPath;
    private readonly MaintenanceService _service = new();
    private readonly ExternalToolService _externalTools = new();

    private readonly TextBox _output;
    private readonly Button _gc;
    private readonly Button _fsck;
    private readonly Button _unlock;
    private readonly Button _config;

    public MaintenanceDialog(string repoPath)
    {
        _repoPath = repoPath;

        IBrush text = Resource("App.Text", "#DCDCDC");
        IBrush dim = Resource("App.TextDim", "#9B9B9B");

        Title = "Git maintenance";
        Width = 620;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Resource("App.Window", "#1E1E1E");

        TextBlock heading = new()
        {
            Text = "Repository maintenance",
            Foreground = text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        TextBlock subtitle = new()
        {
            Text = repoPath,
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        _gc = ActionButton("Compress database (git gc)");
        _fsck = ActionButton("Verify database (git fsck)");
        _unlock = ActionButton("Delete .git/index.lock");
        _config = ActionButton("Edit .git/config");

        _gc.Click += (_, _) => _ = RunGitAsync("Compress database", () => _service.CompressDatabase(_repoPath));
        _fsck.Click += (_, _) => _ = RunGitAsync("Verify database", () => _service.VerifyDatabase(_repoPath));
        _unlock.Click += (_, _) => DeleteLock();
        _config.Click += (_, _) => EditConfig();

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };
        actions.Children.Add(_gc);
        actions.Children.Add(_fsck);
        actions.Children.Add(_unlock);
        actions.Children.Add(_config);

        // TextBoxSurface: see OutputView — the Fluent per-state repaint beats the local
        // Background, so clicking this read-only log flipped its surface to pure
        // black (dark) / pure white (light).
        _output = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("monospace"),
                Text = "Choose a maintenance action above.",
                [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
                [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
            },
            Resource("App.Panel", "#252526"),
            text);

        Button close = new()
        {
            Content = "Close",
            IsCancel = true,
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        close.Click += (_, _) => Close();

        Grid root = new()
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(new GridLength(1, GridUnitType.Star)),
                new RowDefinition(GridLength.Auto),
            },
        };
        Grid.SetRow(heading, 0);
        Grid.SetRow(subtitle, 1);
        Grid.SetRow(actions, 2);
        Grid.SetRow(_output, 3);
        Grid.SetRow(close, 4);
        _output.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(heading);
        root.Children.Add(subtitle);
        root.Children.Add(actions);
        root.Children.Add(_output);
        root.Children.Add(close);

        Content = root;
        DialogKeys.InstallEscapeClose(this);
    }

    /// <summary>Shows the maintenance dialog modally over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner, string repoPath)
        => new MaintenanceDialog(repoPath).ShowDialog(owner);

    // Runs a git-backed maintenance op off the UI thread and shows its output.
    private async Task RunGitAsync(string label, Func<MaintenanceResult> op)
    {
        SetBusy(true);
        _output.Text = $"{label}…";

        MaintenanceResult result;
        try
        {
            result = await Task.Run(op);
        }
        catch (Exception ex)
        {
            _output.Text = $"{label} failed: {ex.Message}";
            SetBusy(false);
            return;
        }

        string status = result.Success ? "completed" : "FAILED";
        _output.Text = $"$ {label} — {status}\n\n{result.Output}";
        SetBusy(false);
    }

    // Deletes a stale index.lock (handles the missing-file case) on the UI thread —
    // it is a trivial local file operation.
    private void DeleteLock()
    {
        MaintenanceResult result = _service.DeleteIndexLock(_repoPath);
        _output.Text = $"$ Delete .git/index.lock — {(result.Success ? "done" : "FAILED")}\n\n{result.Output}";
    }

    // Opens .git/config in the default editor via xdg-open.
    private void EditConfig()
    {
        string configPath = _service.ResolveConfigPath(_repoPath);
        ExternalToolResult result = _externalTools.OpenOrCreateFile(configPath);
        _output.Text = result.Message;
    }

    private void SetBusy(bool busy)
    {
        _gc.IsEnabled = !busy;
        _fsck.IsEnabled = !busy;
        _unlock.IsEnabled = !busy;
        _config.IsEnabled = !busy;
    }

    private static Button ActionButton(string content) => new()
    {
        Content = content,
        MinWidth = 80,
    };

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
