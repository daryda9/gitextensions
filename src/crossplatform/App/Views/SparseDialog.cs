using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Sparse working copy" dialog for the Avalonia port, wrapping the core
///  git <c>sparse-checkout</c> plumbing via <see cref="SparseService"/>:
///
///  <list type="bullet">
///    <item><description>Shows the current pattern set (<c>sparse-checkout list</c>).</description></item>
///    <item><description>Enable — cone-mode init (<c>sparse-checkout init --cone</c>).</description></item>
///    <item><description>Set — applies the newline-separated patterns from the editor
///      (<c>sparse-checkout set &lt;patterns&gt;</c>).</description></item>
///    <item><description>Disable — restores the full tree (<c>sparse-checkout disable</c>).</description></item>
///  </list>
///
///  All git work runs off the UI thread via <see cref="Task.Run"/> and marshals
///  back with <see cref="Dispatcher.UIThread"/>; git output is surfaced verbatim.
///  <see cref="Changed"/> is set whenever a mutating operation succeeds so the
///  caller can refresh the main view. Styled from the shared App.* brushes to
///  match the active theme, mirroring <see cref="ReflogWindow"/>.
/// </summary>
public sealed class SparseDialog : Window
{
    private readonly SparseService _service = new();
    private readonly string _repoPath;
    private readonly TextBox _patterns;
    private readonly TextBox _output;
    private readonly Button _enable;
    private readonly Button _set;
    private readonly Button _disable;
    private readonly Button _refresh;
    private readonly TextBlock _status;

    private bool _busy;

    /// <summary>
    ///  True when a mutating sparse-checkout operation succeeded, so the owner
    ///  can refresh its views once the dialog is dismissed (the working tree may
    ///  have gained or lost files).
    /// </summary>
    public bool Changed { get; private set; }

    public SparseDialog(string repoPath)
    {
        _repoPath = repoPath;

        Title = "Sparse working copy";
        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        TextBlock patternsLabel = new()
        {
            Text = "Patterns (one directory/pattern per line, cone mode):",
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Margin = new Thickness(0, 0, 0, 4),
        };
        _patterns = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Height = 150,
            Watermark = "src/\ndocs/",
        };

        TextBlock outputLabel = new()
        {
            Text = "Current status / output:",
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Margin = new Thickness(0, 10, 0, 4),
        };
        _output = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            Background = Brush("App.Control", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
        };
        ScrollViewer outputScroll = new()
        {
            Content = _output,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _enable = MakeButton("Enable (cone)");
        _set = MakeButton("Set patterns");
        _disable = MakeButton("Disable");
        _refresh = MakeButton("Refresh");
        Button close = MakeButton("Close");

        _enable.Click += (_, _) => Run("Enable", () => _service.Enable(_repoPath), mutating: true);
        _set.Click += (_, _) => DoSetPatterns();
        _disable.Click += (_, _) => Run("Disable", () => _service.Disable(_repoPath), mutating: true);
        _refresh.Click += (_, _) => ReloadStatus();
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Width = 140,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(_enable);
        buttons.Children.Add(_set);
        buttons.Children.Add(_disable);
        buttons.Children.Add(_refresh);
        buttons.Children.Add(close);

        // Left column: patterns editor over the output pane; right column: buttons.
        DockPanel left = new();
        DockPanel.SetDock(patternsLabel, Dock.Top);
        DockPanel.SetDock(_patterns, Dock.Top);
        DockPanel.SetDock(outputLabel, Dock.Top);
        left.Children.Add(patternsLabel);
        left.Children.Add(_patterns);
        left.Children.Add(outputLabel);
        left.Children.Add(outputScroll);

        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(left);
        row.Children.Add(buttons);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        DockPanel body = new() { Margin = new Thickness(12) };
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);
        body.Children.Add(row);
        Content = body;

        // Escape is inert while a sparse-checkout apply is in flight.
        DialogKeys.InstallEscapeClose(this, () => !_busy);

        Opened += (_, _) => ReloadStatus();
    }

    // Reads the current pattern set and reflects it in the output pane + editor.
    private void ReloadStatus()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _status.Text = "Reading sparse-checkout status…";
        _ = Task.Run(() =>
        {
            SparseResult result;
            try
            {
                result = _service.List(_repoPath);
            }
            catch (Exception ex)
            {
                result = new SparseResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                SetBusy(false);
                if (result.Success)
                {
                    string list = result.Output.Trim();
                    bool enabled = list.Length > 0 && !list.StartsWith("(completed", StringComparison.Ordinal);
                    _output.Text = enabled
                        ? list
                        : "Sparse checkout is not enabled (the full working tree is checked out).";
                    if (enabled)
                    {
                        _patterns.Text = list;
                    }

                    _status.Text = enabled
                        ? "Sparse checkout is enabled."
                        : "Sparse checkout is disabled.";
                }
                else
                {
                    // `sparse-checkout list` exits non-zero when the tree is not
                    // sparse (its normal "disabled" signal), so treat a failed list
                    // as the disabled state and surface git's message for context.
                    _output.Text = "Sparse checkout is not enabled (the full working tree is checked out)."
                        + Environment.NewLine + Environment.NewLine + "git: " + result.Output.Trim();
                    _status.Text = "Sparse checkout is disabled.";
                }
            });
        });
    }

    // Parses the editor into non-empty, trimmed patterns and applies them.
    private void DoSetPatterns()
    {
        string[] patterns = (_patterns.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (patterns.Length == 0)
        {
            _status.Text = "Enter at least one pattern to set (or use Disable to clear).";
            return;
        }

        Run("Set patterns", () => _service.SetPatterns(_repoPath, patterns), mutating: true);
    }

    // Shared runner for a sparse-checkout operation: runs it off the UI thread,
    // surfaces git's output, flags Changed on a successful mutation, and reloads
    // the status afterwards.
    private void Run(string label, Func<SparseResult> op, bool mutating)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _status.Text = $"{label}…";
        _ = Task.Run(() =>
        {
            SparseResult result;
            try
            {
                result = op();
            }
            catch (Exception ex)
            {
                result = new SparseResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                SetBusy(false);
                _output.Text = result.Output;
                if (result.Success)
                {
                    if (mutating)
                    {
                        Changed = true;
                    }

                    _status.Text = $"{label} succeeded.";
                    ReloadStatus();
                }
                else
                {
                    _status.Text = $"{label} failed — see output.";
                }
            });
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _enable.IsEnabled = !busy;
        _set.IsEnabled = !busy;
        _disable.IsEnabled = !busy;
        _refresh.IsEnabled = !busy;
    }

    private Button MakeButton(string text) => new()
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Background = Brush("App.Control", Brushes.DimGray),
        Foreground = Brush("App.Text", Brushes.Gainsboro),
    };

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
