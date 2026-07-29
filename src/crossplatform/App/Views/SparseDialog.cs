using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal "Sparse working copy" dialog for the Avalonia port — the port of
///  upstream's <c>FormSparseWorkingCopy</c>, driven through
///  <see cref="SparseService"/>.
///
///  <para>
///  <b>Two modes, and why.</b> Upstream does not use <c>git sparse-checkout</c> at
///  all: it sets <c>core.sparsecheckout</c>, edits
///  <c>.git/info/sparse-checkout</c> by hand and refreshes with
///  <c>git read-tree -m -u HEAD</c>. That legacy mode is the only one that accepts
///  the whole <c>.gitignore</c> pattern language, and in particular <b>negation</b>
///  — cone mode refuses it outright (<c>git sparse-checkout set --cone '!gamma'</c>
///  → <c>fatal: Specify directories rather than patterns</c>). The port used to
///  offer cone mode only, so a rule like <c>!docs/</c> was simply not expressible
///  and the port fell short of upstream. Legacy is therefore the default here and
///  matches upstream rule-for-rule; cone mode stays reachable behind a checkbox,
///  because it is faster on very large repositories and is what the port already
///  shipped.
///  </para>
///
///  <list type="bullet">
///    <item><description>Legacy: <b>Save &amp; apply</b> writes the rules and
///      <c>core.sparsecheckout=true</c>, then <c>read-tree -m -u HEAD</c>;
///      <b>Disable</b> follows upstream's special case — rewrite the rules to
///      <c>/*</c> with the old ones commented out, then clear the flag, because
///      clearing the flag alone leaves git honouring the stale rules.</description></item>
///    <item><description>Cone: <c>sparse-checkout init --cone</c> /
///      <c>set &lt;dirs&gt;</c> / <c>disable</c>, directories only.</description></item>
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
    private readonly TextBlock _patternsLabel;
    private readonly CheckBox _coneMode;

    private bool _busy;

    private bool ConeMode => _coneMode.IsChecked == true;

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

        // Upstream's help text, verbatim in substance (FormSparseWorkingCopy.cs): the
        // rules are .gitignore syntax, matched items are *included*, "!" excludes and
        // "#" comments. Without this the "!" support is undiscoverable.
        TextBlock help = new()
        {
            Text = "Rules use the “.gitignore” format and matched items are included. "
                 + "To exclude, prefix a rule with an exclamation mark “!”. "
                 + "“#” comments a line. This is only a filter: it cannot change the "
                 + "structure, e.g. pull a deep subfolder up to the first level.",
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _coneMode = new CheckBox
        {
            Content = "Cone mode (directories only — no “!” negation)",
            IsChecked = false,
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Margin = new Thickness(0, 0, 0, 8),
        };
        _coneMode.IsCheckedChanged += (_, _) =>
        {
            SyncModeLabels();
            ReloadStatus();
        };

        _patternsLabel = new TextBlock
        {
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
            Watermark = "/*\n!docs/",
        };

        TextBlock outputLabel = new()
        {
            Text = "Current status / output:",
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Margin = new Thickness(0, 10, 0, 4),
        };
        // TextBoxSurface (M62): see CommandLogWindow — the Fluent per-state repaint
        // beats the local Background, so clicking this read-only output flipped its
        // surface to pure black (dark) / pure white (light).
        _output = Theming.TextBoxSurface.Apply(
            new TextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("monospace"),
            },
            Brush("App.Control", Brushes.Black),
            Brush("App.Text", Brushes.Gainsboro));
        ScrollViewer outputScroll = new()
        {
            Content = _output,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        _enable = MakeButton("Enable");
        _set = MakeButton("Save & apply");
        _disable = MakeButton("Disable");
        _refresh = MakeButton("Reload");
        Button close = MakeButton("Close");

        _enable.Click += (_, _) => DoEnable();
        _set.Click += (_, _) => DoSetPatterns();
        _disable.Click += (_, _) => DoDisable();
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
        DockPanel.SetDock(help, Dock.Top);
        DockPanel.SetDock(_coneMode, Dock.Top);
        DockPanel.SetDock(_patternsLabel, Dock.Top);
        DockPanel.SetDock(_patterns, Dock.Top);
        DockPanel.SetDock(outputLabel, Dock.Top);
        left.Children.Add(help);
        left.Children.Add(_coneMode);
        left.Children.Add(_patternsLabel);
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

        SyncModeLabels();
        Opened += (_, _) => ReloadStatus();
    }

    // Keeps the labels honest about which of the two mechanisms the buttons will use.
    private void SyncModeLabels()
    {
        _patternsLabel.Text = ConeMode
            ? "Directories to keep, one per line (cone mode):"
            : "Rules — the contents of .git/info/sparse-checkout:";
        _set.Content = ConeMode ? "Set patterns" : "Save & apply";
        _enable.IsEnabled = !_busy;
    }

    // Reads the current state and reflects it in the output pane + editor. In legacy
    // mode the truth is core.sparsecheckout plus the rules file, which is also what
    // the editor must show; `sparse-checkout list` is appended for context because it
    // reports the effective patterns git actually parsed.
    private void ReloadStatus()
    {
        if (_busy)
        {
            return;
        }

        bool cone = ConeMode;
        SetBusy(true);
        _status.Text = "Reading sparse-checkout status…";
        _ = Task.Run(() =>
        {
            bool legacyEnabled = false;
            string rules = string.Empty;
            SparseResult list;
            try
            {
                if (!cone)
                {
                    legacyEnabled = _service.IsLegacyEnabled(_repoPath);
                    rules = _service.ReadRules(_repoPath);
                }

                list = _service.List(_repoPath);
            }
            catch (Exception ex)
            {
                list = new SparseResult(false, ex.Message);
            }

            Dispatcher.UIThread.Post(() =>
            {
                SetBusy(false);
                string listText = list.Output.Trim();
                bool listHasPatterns = list.Success
                    && listText.Length > 0
                    && !listText.StartsWith("(completed", StringComparison.Ordinal);

                if (cone)
                {
                    // `sparse-checkout list` exits non-zero when the tree is not sparse
                    // (its normal "disabled" signal), so a failed list is the disabled
                    // state, not an error.
                    _output.Text = listHasPatterns
                        ? listText
                        : "Sparse checkout is not enabled (the full working tree is checked out)."
                          + (list.Success ? string.Empty : Environment.NewLine + Environment.NewLine + "git: " + listText);
                    if (listHasPatterns)
                    {
                        _patterns.Text = listText;
                    }

                    _status.Text = listHasPatterns
                        ? "Sparse checkout is enabled (cone)."
                        : "Sparse checkout is disabled.";
                    return;
                }

                // Legacy: always mirror the rules file into the editor, even when the
                // feature is off, so rules can be prepared before enabling.
                _patterns.Text = rules;
                _output.Text =
                    $"core.sparsecheckout = {(legacyEnabled ? "true" : "false")}"
                    + Environment.NewLine
                    + SparseService.RulesFilePath(_repoPath)
                    + Environment.NewLine + Environment.NewLine
                    + (rules.Trim().Length > 0 ? rules.TrimEnd() : "(no rules)")
                    + Environment.NewLine + Environment.NewLine
                    + "git sparse-checkout list:" + Environment.NewLine
                    + (listHasPatterns ? listText : "(none)");

                _status.Text = legacyEnabled
                    ? "Sparse checkout is enabled (legacy, “!” supported)."
                    : "Sparse checkout is disabled.";
            });
        });
    }

    // Legacy Enable just flips core.sparsecheckout on, keeping whatever rules are in
    // the editor — upstream's "&Enable" button. In cone mode it is `init --cone`.
    private void DoEnable()
    {
        if (ConeMode)
        {
            Run("Enable (cone)", () => _service.Enable(_repoPath), mutating: true);
            return;
        }

        string rules = _patterns.Text ?? string.Empty;
        Run("Enable", () => _service.ApplyLegacy(_repoPath, rules, enabled: true), mutating: true);
    }

    // Applies the editor. Cone mode wants a list of directories; legacy mode wants the
    // rules file written verbatim, because whitespace, comments and "!" all matter and
    // trimming lines away would silently change the filter's meaning.
    private void DoSetPatterns()
    {
        if (!ConeMode)
        {
            string rules = _patterns.Text ?? string.Empty;
            if (rules.Trim().Length == 0)
            {
                _status.Text = "Enter at least one rule (or use Disable to restore the full tree).";
                return;
            }

            Run("Save & apply", () => _service.ApplyLegacy(_repoPath, rules, enabled: true), mutating: true);
            return;
        }

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

    private void DoDisable()
    {
        if (ConeMode)
        {
            Run("Disable", () => _service.Disable(_repoPath), mutating: true);
            return;
        }

        // Upstream rewrites the rules to "/*" plus the old ones commented out; the
        // rewritten text goes straight back into the editor so what is on screen is
        // what is on disk.
        Run("Disable", () =>
        {
            (SparseResult result, string newRules) = _service.DisableLegacy(_repoPath);
            Dispatcher.UIThread.Post(() => _patterns.Text = newRules);
            return result;
        }, mutating: true);
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
        _coneMode.IsEnabled = !busy;
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
