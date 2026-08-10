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
public sealed class SparseDialog : Theming.ZoomWindow
{
    private readonly SparseService _service = new();
    private readonly string _repoPath;
    private readonly TextBox _patterns;
    private readonly TextBox _output;
    private readonly Button _enable;
    private readonly Button _set;
    private readonly Button _disable;
    private readonly Button _refresh;
    private readonly Button _close;
    private readonly TextBlock _status;
    private readonly TextBlock _patternsLabel;
    private readonly TextBlock _help;
    private readonly TextBlock _outputLabel;
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

        Width = 720;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", Brushes.DimGray);

        // Upstream's help text, verbatim in substance (FormSparseWorkingCopy.cs): the
        // rules are .gitignore syntax, matched items are *included*, "!" excludes and
        // "#" comments. Without this the "!" support is undiscoverable.
        _help = new TextBlock
        {
            Foreground = Brush("App.TextDim", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _coneMode = new CheckBox
        {
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

        _outputLabel = new TextBlock
        {
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

        _enable = MakeButton();
        _set = MakeButton();
        _disable = MakeButton();
        _refresh = MakeButton();
        _close = MakeButton();

        _enable.Click += (_, _) => DoEnable();
        _set.Click += (_, _) => DoSetPatterns();
        _disable.Click += (_, _) => DoDisable();
        _refresh.Click += (_, _) => ReloadStatus();
        _close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,

            // MinWidth, not Width: "Save & apply" becomes "Salva e applica" and a hard
            // width would clip it rather than grow this Auto-sized column.
            MinWidth = 140,
            Margin = new Thickness(10, 0, 0, 0),
        };
        buttons.Children.Add(_enable);
        buttons.Children.Add(_set);
        buttons.Children.Add(_disable);
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_close);

        // Left column: patterns editor over the output pane; right column: buttons.
        DockPanel left = new();
        DockPanel.SetDock(_help, Dock.Top);
        DockPanel.SetDock(_coneMode, Dock.Top);
        DockPanel.SetDock(_patternsLabel, Dock.Top);
        DockPanel.SetDock(_patterns, Dock.Top);
        DockPanel.SetDock(_outputLabel, Dock.Top);
        left.Children.Add(_help);
        left.Children.Add(_coneMode);
        left.Children.Add(_patternsLabel);
        left.Children.Add(_patterns);
        left.Children.Add(_outputLabel);
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

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        Opened += (_, _) => ReloadStatus();
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    // Everything the dialog says that does not depend on git's answer. The mode-
    // dependent captions are delegated to SyncModeLabels so the two paths cannot drift.
    private void ApplyTranslations()
    {
        Title = T("Globalized/SparseWorkingCopy.Text", "Sparse working copy");

        // Upstream's own help paragraph. The port's literal is a shortened rewording of
        // it, so the id — not the source text — is what makes the two meet.
        _help.Text = T(
            "Globalized/SpecifyTheRulesForIncludingOrExcludingFilesAndDirectoriesLine2.Text",
            "Rules use the “.gitignore” format and matched items are included. "
            + "To exclude, prefix a rule with an exclamation mark “!”. "
            + "“#” comments a line. This is only a filter: it cannot change the "
            + "structure, e.g. pull a deep subfolder up to the first level.");

        // Cone mode has no upstream counterpart at all: FormSparseWorkingCopy predates
        // `git sparse-checkout` and only ever drives the legacy rules file.
        _coneMode.Content = T("Cone mode (directories only — no “!” negation)");

        _outputLabel.Text = T("Current status / output:");
        _enable.Content = T("Globalized/Enable.Text", "Enable");

        // Globalized has no bare "Disable"; DisableGitSparse ("Disable Git Sparse") is
        // the only unit for this exact action and reads correctly on this dialog, where
        // the button disables nothing else.
        _disable.Content = T("Globalized/DisableGitSparse.Text", "Disable");
        _refresh.Content = T("FormBrowse/refreshToolStripMenuItem.Text", "Reload");
        _close.Content = T("TranslatedStrings/_closeText.Text", "Close");

        SyncModeLabels();
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    // Keeps the labels honest about which of the two mechanisms the buttons will use.
    private void SyncModeLabels()
    {
        _patternsLabel.Text = ConeMode
            ? T("Directories to keep, one per line (cone mode):")
            : T("Rules — the contents of .git/info/sparse-checkout:");
        _set.Content = ConeMode ? T("Set patterns") : T("Save & apply");
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
        _status.Text = T("Reading sparse-checkout status…");
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

                        // Upstream's wording for "the feature is off"; the parenthetical
                        // is the port's own addition and stays in the literal, where a
                        // translator can drop it, because the id's sentence is complete
                        // without it.
                        : T("Globalized/SparseWorkingCopySupportHasNotBeenEnabledForThisRepository.Text",
                            "Sparse checkout is not enabled (the full working tree is checked out).")
                          + (list.Success ? string.Empty : Environment.NewLine + Environment.NewLine + "git: " + listText);
                    if (listHasPatterns)
                    {
                        _patterns.Text = listText;
                    }

                    _status.Text = listHasPatterns
                        ? T("Sparse checkout is enabled (cone).")
                        : T("Sparse checkout is disabled.");
                    return;
                }

                // Legacy: always mirror the rules file into the editor, even when the
                // feature is off, so rules can be prepared before enabling.
                _patterns.Text = rules;

                // NOT translated, deliberately: `core.sparsecheckout = true`, the rules
                // file path and `git sparse-checkout list:` are the literal shape of
                // git's configuration and of a git command line. Translating them would
                // produce a transcript that cannot be pasted into a shell.
                _output.Text =
                    $"core.sparsecheckout = {(legacyEnabled ? "true" : "false")}"
                    + Environment.NewLine
                    + SparseService.RulesFilePath(_repoPath)
                    + Environment.NewLine + Environment.NewLine
                    + (rules.Trim().Length > 0 ? rules.TrimEnd() : T("(no rules)"))
                    + Environment.NewLine + Environment.NewLine
                    + "git sparse-checkout list:" + Environment.NewLine
                    + (listHasPatterns ? listText : T("(none)"));

                _status.Text = legacyEnabled
                    ? T("Sparse checkout is enabled (legacy, “!” supported).")
                    : T("Sparse checkout is disabled.");
            });
        });
    }

    // Legacy Enable just flips core.sparsecheckout on, keeping whatever rules are in
    // the editor — upstream's "&Enable" button. In cone mode it is `init --cone`.
    private void DoEnable()
    {
        if (ConeMode)
        {
            Run(
                TranslationService.TFormat(null, "{0} (cone)", T("Globalized/Enable.Text", "Enable")),
                () => _service.Enable(_repoPath),
                mutating: true);
            return;
        }

        string rules = _patterns.Text ?? string.Empty;
        Run(
            T("Globalized/Enable.Text", "Enable"),
            () => _service.ApplyLegacy(_repoPath, rules, enabled: true),
            mutating: true);
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
                _status.Text = T("Enter at least one rule (or use Disable to restore the full tree).");
                return;
            }

            Run(T("Save & apply"), () => _service.ApplyLegacy(_repoPath, rules, enabled: true), mutating: true);
            return;
        }

        string[] patterns = (_patterns.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (patterns.Length == 0)
        {
            _status.Text = T("Enter at least one pattern to set (or use Disable to clear).");
            return;
        }

        Run(T("Set patterns"), () => _service.SetPatterns(_repoPath, patterns), mutating: true);
    }

    private void DoDisable()
    {
        if (ConeMode)
        {
            Run(T("Globalized/DisableGitSparse.Text", "Disable"), () => _service.Disable(_repoPath), mutating: true);
            return;
        }

        // Upstream rewrites the rules to "/*" plus the old ones commented out; the
        // rewritten text goes straight back into the editor so what is on screen is
        // what is on disk.
        Run(T("Globalized/DisableGitSparse.Text", "Disable"), () =>
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
        _status.Text = TranslationService.TFormat(null, "{0}…", label);
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

                    _status.Text = TranslationService.TFormat(null, "{0} succeeded.", label);
                    ReloadStatus();
                }
                else
                {
                    _status.Text = TranslationService.TFormat(null, "{0} failed — see output.", label);
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

    // No caption here: ApplyTranslations / SyncModeLabels own every button label.
    private Button MakeButton() => new()
    {
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
