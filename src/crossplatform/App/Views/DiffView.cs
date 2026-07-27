using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  A two-pane view of a single commit's diff: the changed-files list on the
///  left, the unified diff of the selected file on the right. Heavy git work is
///  performed off the UI thread, matching <c>MainWindow</c>.
///
///  <para>Captions go through <see cref="TranslationService"/>. The XLIFF ids
///  come from the two upstream controls this view merges: <c>FileStatusList</c>
///  (the changed-files list and its context menu) and <c>FileViewer</c> (the
///  diff pane's toolbar strip and its settings menu). Strings with no upstream
///  equivalent — the zoom commands, the encoding tooltip, the status line —
///  use the source-text overload and simply stay English when a catalogue has
///  no match. The view re-labels itself in place on
///  <see cref="TranslationService.LanguageChanged"/>; it is never rebuilt, so
///  the loaded diff and the scroll position survive a language switch.</para>
/// </summary>
public sealed class DiffView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Diff line colours tuned for the dark palette; hunk/meta pull from app resources.
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0xC7, 0x76));
    private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));

    // File-status glyph colours: modified=accent, added=green, deleted=red.
    private static readonly IBrush ModifiedGlyph = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xD6));
    private static readonly IBrush AddedGlyph = new SolidColorBrush(Color.FromRgb(0x6A, 0xC7, 0x76));
    private static readonly IBrush DeletedGlyph = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));

    // Which comparison the currently loaded file list represents, so file
    // selection loads the matching per-file diff.
    private enum CompareMode
    {
        Commit,       // a single commit vs its first parent
        Range,        // BASE..other (two commits)
        WorkingTree,  // a commit vs the current working tree
    }

    private readonly ListBox _files;
    private readonly SelectableTextBlock _diff;
    private readonly TextBlock _status;
    private readonly ScrollViewer _diffScroll;

    // Diff-toolbar state (session-persisted in DiffTextService.Session).
    private readonly DiffDisplayOptions _options = DiffTextService.Session;
    private readonly ToggleButton _ignoreWhitespaceButton;
    private readonly ToggleButton _nonPrintingButton;
    private readonly ToggleButton _wordDiffButton;
    private readonly ComboBox _encodingBox;

    // Kept so a language switch can re-label them in place (see ApplyTranslations).
    private readonly MenuItem _copyPathItem;
    private readonly MenuItem _blameItem;
    private readonly MenuItem _historyItem;
    private readonly MenuItem _difftoolItem;
    private readonly MenuItem _compareWorkingDirItem;
    private readonly MenuItem _copyDiffItem;
    private readonly MenuItem _selectAllCopyItem;
    private readonly Button _prevChangeButton;
    private readonly Button _nextChangeButton;
    private readonly Button _zoomInButton;
    private readonly Button _zoomOutButton;
    private readonly Button _settingsButton;

    // False while the view shows its "nothing loaded yet" placeholder, so a
    // language switch can re-translate that placeholder without clobbering a
    // real status message (a command line, an error) with a stale one.
    private bool _hasCommit;

    // Line indices (into the currently rendered diff) of each hunk header, and
    // where the ▲/▼ navigation currently sits.
    private readonly List<int> _hunkLines = [];
    private int _hunkIndex = -1;

    private string? _repoPath;
    private string? _commitHash;   // the (right/"new") commit; also the "other" side in Range mode
    private string? _baseHash;     // the ("old"/left) commit in Range mode
    private CompareMode _mode = CompareMode.Commit;
    private CancellationTokenSource? _diffCts;

    // Whether the last file diff was loaded as "commit vs working tree" through
    // the context-menu command, so a toggle re-runs the same comparison.
    private bool _forceWorkingTreeCompare;

    // The raw unified-diff text currently displayed (the SelectableTextBlock's
    // Text is cleared while inlines are rendered, so keep our own copy to copy).
    private string _currentDiffText = string.Empty;

    public DiffView()
    {
        _files = new ListBox
        {
            FontFamily = Monospace,
            FontSize = 12,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderThickness = new Thickness(0),
            ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<DiffFileRow>(
                (row, _) => BuildFileRow(row),
                supportsRecycling: true),
        };
        _files.SelectionChanged += OnFileSelected;

        // Tight rows + an App.Selection highlight, matching the revision grid.
        _files.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 1, 8, 1)),
                new Setter(ListBoxItem.MinHeightProperty, 0d),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            },
        });
        _files.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":pointerover")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.PanelAlt")) },
        });
        _files.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected")
            .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, B("App.Selection")) },
        });

        _copyPathItem = new MenuItem();
        _copyPathItem.Click += (_, _) => CopySelectedFilePath();
        _blameItem = new MenuItem();
        _blameItem.Click += (_, _) => RaiseFileAction(BlameRequested);
        _historyItem = new MenuItem();
        _historyItem.Click += (_, _) => RaiseFileAction(FileHistoryRequested);
        _difftoolItem = new MenuItem();
        _difftoolItem.Click += (_, _) => OpenSelectedInExternalDiffTool();
        _compareWorkingDirItem = new MenuItem();
        _compareWorkingDirItem.Click += (_, _) => CompareSelectedToWorkingDirectory();
        _files.ContextMenu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                _copyPathItem,
                new Separator(),
                _blameItem,
                _historyItem,
                new Separator(),
                _difftoolItem,
                _compareWorkingDirItem,
            },
        };

        _diff = new SelectableTextBlock
        {
            FontFamily = Monospace,
            FontSize = 12,
            Foreground = B("App.Text"),
            Margin = new Thickness(12, 10, 12, 12),
            TextWrapping = TextWrapping.NoWrap,
        };

        _copyDiffItem = new MenuItem();
        _copyDiffItem.Click += (_, _) => CopyDiffText();
        _selectAllCopyItem = new MenuItem();
        _selectAllCopyItem.Click += (_, _) => SelectAllAndCopy();
        _diff.ContextMenu = new ContextMenu { ItemsSource = new[] { _copyDiffItem, _selectAllCopyItem } };

        _diff.FontSize = _options.FontSize;

        _diffScroll = new ScrollViewer
        {
            Content = _diff,
            Background = B("App.Window"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // ---- diff toolbar (mirrors the Windows diff viewer's right-hand strip) ----
        AddToolbarStyles();

        // Tooltips are not passed here: every one of them is (re-)applied by
        // ApplyTranslations, which also runs on a language switch.
        _prevChangeButton = ToolButton("▲", GoToPreviousChange);
        _nextChangeButton = ToolButton("▼", GoToNextChange);
        _zoomInButton = ToolButton("A+", () => Zoom(+1));
        _zoomOutButton = ToolButton("A−", () => Zoom(-1));

        _ignoreWhitespaceButton = ToggleTool(
            "-w", _options.IgnoreWhitespace,
            v =>
            {
                _options.IgnoreWhitespace = v;
                ReloadDiff();
            });

        _nonPrintingButton = ToggleTool(
            "¶", _options.ShowNonPrinting,
            v =>
            {
                _options.ShowNonPrinting = v;
                RenderDiff(_currentDiffText);
            });

        _wordDiffButton = ToggleTool(
            "<div>", _options.WordDiff,
            v =>
            {
                _options.WordDiff = v;
                ReloadDiff();
            });

        _encodingBox = new ComboBox
        {
            ItemsSource = DiffTextService.EncodingNames,
            SelectedItem = DiffTextService.EncodingNames.Contains(_options.EncodingName)
                ? _options.EncodingName
                : DiffTextService.DefaultEncodingName,
            Width = 190,
            FontSize = 12,
            Padding = new Thickness(6, 1, 4, 1),
            MinHeight = 0,
            Background = B("App.Panel"),
            Foreground = B("App.Text"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _encodingBox.SelectionChanged += (_, _) =>
        {
            if (_encodingBox.SelectedItem is not string name)
            {
                return;
            }

            _options.EncodingName = name;
            ReloadDiff();
        };

        _settingsButton = ToolButton("⚙", null);
        _settingsButton.Click += (_, _) => ShowSettingsMenu(_settingsButton);

        // Every item here is a glyph or a data-driven combo box, so no caption
        // grows when the UI is translated and a plain horizontal strip is safe.
        StackPanel toolbar = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 2, 6, 2),
        };
        toolbar.Children.Add(_nextChangeButton);
        toolbar.Children.Add(_prevChangeButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_zoomInButton);
        toolbar.Children.Add(_zoomOutButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_ignoreWhitespaceButton);
        toolbar.Children.Add(_nonPrintingButton);
        toolbar.Children.Add(_wordDiffButton);
        toolbar.Children.Add(ToolSeparator());
        toolbar.Children.Add(_encodingBox);
        toolbar.Children.Add(_settingsButton);

        Border toolbarBar = new()
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbar,
        };

        DockPanel diffPane = new();
        DockPanel.SetDock(toolbarBar, Dock.Top);
        diffPane.Children.Add(toolbarBar);
        diffPane.Children.Add(_diffScroll);

        _status = new TextBlock
        {
            Padding = new Thickness(12, 7, 12, 7),
            Background = B("App.Toolbar"),
            Foreground = B("App.TextDim"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Grid split = new()
        {
            Background = B("App.Panel"),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
        };

        Grid.SetColumn(_files, 0);
        _files.Width = 320;

        GridSplitter splitter = new()
        {
            Width = 4,
            Background = B("App.Border"),
            ResizeDirection = GridResizeDirection.Columns,
        };
        Grid.SetColumn(splitter, 1);

        Grid.SetColumn(diffPane, 2);

        split.Children.Add(_files);
        split.Children.Add(splitter);
        split.Children.Add(diffPane);

        DockPanel root = new() { Background = B("App.Window") };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(split);

        Content = root;

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;

        // Ctrl+C: copy the file path when the file list is focused, otherwise the diff.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>The catalogue's word for "Error", used to prefix a raw git message.</summary>
    private static string ErrorWord() => T("TranslatedStrings/_error.Text", "Error");

    // The load-time labelling pass, re-run whenever the language changes. It
    // touches captions and tooltips only, never the loaded content, so a switch
    // costs nothing and loses nothing.
    private void ApplyTranslations()
    {
        _copyPathItem.Header = T("FileStatusList/tsmiCopyPaths.Text", "Copy file path");
        _blameItem.Header = T("FileStatusList/tsmiBlame.Text", "Blame");
        _historyItem.Header = T("FileStatusList/tsmiFileHistory.Text", "File history");
        _difftoolItem.Header = T("FileStatusList/tsmiOpenWithDifftool.Text", "Open in external difftool");
        _compareWorkingDirItem.Header = T(
            "RevisionGridControl/compareToWorkingDirectoryMenuItem.Text", "Compare file to working directory");

        // No usable upstream id: FileViewer's "Copy &patch" is mistranslated as
        // "copy and apply" in at least one catalogue, so these two stay on the
        // source-text lookup and fall back to English.
        _copyDiffItem.Header = T("Copy diff");
        _selectAllCopyItem.Header = T("Select all + copy");

        ToolTip.SetTip(_prevChangeButton, T("FileViewer/previousChangeButton.ToolTipText", "Previous change"));
        ToolTip.SetTip(_nextChangeButton, T("FileViewer/nextChangeButton.ToolTipText", "Next change"));
        ToolTip.SetTip(_zoomInButton, T("Increase text size"));
        ToolTip.SetTip(_zoomOutButton, T("Decrease text size"));

        // The git flag is appended outside the translated sentence: it is a
        // command-line token, identical in every language.
        ToolTip.SetTip(_ignoreWhitespaceButton,
            F("{0}  ({1})", T("FileViewer/ignoreAllWhitespaces.ToolTipText", "Ignore all whitespace changes"), "git diff -w"));
        ToolTip.SetTip(_nonPrintingButton,
            T("FileViewer/showNonPrintChars.ToolTipText", "Show nonprinting characters"));
        ToolTip.SetTip(_wordDiffButton,
            F("{0}  ({1})", T("FileViewer/showGitWordColoringToolStripMenuItem.Text", "Word diff"), "git diff --word-diff"));

        ToolTip.SetTip(_encodingBox, T("Encoding used to decode the diff text"));
        ToolTip.SetTip(_settingsButton, T("FileViewer/settingsButton.ToolTipText", "Settings"));

        if (!_hasCommit)
        {
            _status.Text = T("No commit selected.");
        }
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    // A changed-file row: a coloured status glyph (M/A/D/R/C) followed by the path.
    private static Control BuildFileRow(DiffFileRow? row)
    {
        if (row is null)
        {
            return new TextBlock();
        }

        (char glyph, IBrush glyphBrush) = row.Kind switch
        {
            DiffChangeKind.Added => ('A', AddedGlyph),
            DiffChangeKind.Deleted => ('D', DeletedGlyph),
            DiffChangeKind.Renamed => ('R', ModifiedGlyph),
            DiffChangeKind.Copied => ('C', ModifiedGlyph),
            _ => ('M', ModifiedGlyph),
        };

        string path = row.OldName is null || row.OldName == row.Name
            ? row.Name
            : $"{row.OldName} -> {row.Name}";

        StackPanel panel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = glyph.ToString(),
            Foreground = glyphBrush,
            FontFamily = Monospace,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = path,
            Foreground = B("App.Text"),
            FontFamily = Monospace,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_files.IsKeyboardFocusWithin)
            {
                CopySelectedFilePath();
            }
            else
            {
                CopyDiffText();
            }

            e.Handled = true;
        }
    }

    /// <summary>Raised (with the repo-relative file path) to blame the selected file.</summary>
    public event Action<string>? BlameRequested;

    /// <summary>Raised (with the repo-relative file path) to show the selected file's history.</summary>
    public event Action<string>? FileHistoryRequested;

    private void RaiseFileAction(Action<string>? handler)
    {
        if (_files.SelectedItem is DiffFileRow row)
        {
            handler?.Invoke(row.Name);
        }
    }

    // Fire-and-forget: launch the configured external difftool for the selected
    // file. The launch itself runs off the UI thread and the core runs the tool
    // detached, so neither call blocks; only a config error is surfaced (status).
    private void OpenSelectedInExternalDiffTool()
    {
        if (_files.SelectedItem is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        string repoPath = _repoPath;
        string commitHash = _commitHash;

        _ = Task.Run(() =>
        {
            try
            {
                string? message = DiffService.LaunchExternalDiffTool(repoPath, commitHash, row);
                if (!string.IsNullOrEmpty(message))
                {
                    Dispatcher.UIThread.Post(() => _status.Text = message);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = F(T("Difftool error: {0}"), ex.Message));
            }
        });
    }

    // Loads the diff of the selected file's committed version against the current
    // working-tree version and renders it in the shared coloured diff pane.
    private void CompareSelectedToWorkingDirectory()
    {
        if (_files.SelectedItem is not DiffFileRow || _repoPath is null || _commitHash is null)
        {
            return;
        }

        // Sticky, so a toolbar toggle re-runs the same comparison.
        _forceWorkingTreeCompare = true;
        LoadSelectedFileDiff();
    }

    private void CopySelectedFilePath()
    {
        if (_files.SelectedItem is DiffFileRow row)
        {
            CopyToClipboard(row.Name);
        }
    }

    private void CopyDiffText() => CopyToClipboard(_currentDiffText);

    private void SelectAllAndCopy()
    {
        _diff.SelectAll();
        CopyToClipboard(_currentDiffText);
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    /// <summary>
    ///  Loads the changed-files list for <paramref name="commitHash"/> in the
    ///  repository at <paramref name="repoPath"/>. Selecting a file loads its diff.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _baseHash = null;
        _mode = CompareMode.Commit;

        LoadFileList(
            () => DiffService.GetChangedFiles(repoPath, commitHash),
            count => F(ChangedFilesFormat(), commitHash, count),
            F(LoadingFilesFormat(), commitHash));
    }

    /// <summary>
    ///  Loads the changed-files list and per-file diffs for the range
    ///  <paramref name="baseHash"/>..<paramref name="otherHash"/>
    ///  (i.e. <c>git diff &lt;base&gt; &lt;other&gt;</c>).
    /// </summary>
    public void ShowRange(string repoPath, string baseHash, string otherHash)
    {
        _repoPath = repoPath;
        _commitHash = otherHash;
        _baseHash = baseHash;
        _mode = CompareMode.Range;

        string shortBase = baseHash.Length > 8 ? baseHash[..8] : baseHash;
        string shortOther = otherHash.Length > 8 ? otherHash[..8] : otherHash;

        string range = F("{0} .. {1}", shortBase, shortOther);

        LoadFileList(
            () => DiffService.GetDiffFilesBetween(repoPath, baseHash, otherHash),
            count => F(ChangedFilesFormat(), range, count),
            F(LoadingFilesFormat(), range));
    }

    /// <summary>
    ///  Loads the changed-files list and per-file diffs comparing
    ///  <paramref name="commitHash"/> against the current working tree
    ///  (i.e. <c>git diff &lt;commit&gt;</c>).
    /// </summary>
    public void ShowAgainstWorkingDirectory(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _baseHash = null;
        _mode = CompareMode.WorkingTree;

        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;

        string range = F("{0} .. {1}", shortHash, T("TranslatedStrings/_workingDirectoryText.Text", "working directory"));

        LoadFileList(
            () => DiffService.GetChangedFilesAgainstWorkingTree(repoPath, commitHash),
            count => F(ChangedFilesFormat(), range, count),
            F(LoadingFilesFormat(), range));
    }

    // Composed status texts are single formats with placeholders, never
    // assembled from translated fragments: {0} is the comparison being shown
    // (a hash or a range) and {1} the file count.
    private static string ChangedFilesFormat() => T("{0}  —  {1} changed file(s)");

    private static string LoadingFilesFormat() => T("Loading changed files for {0}…");

    // Shared changed-file-list loader: clears the panes, loads the file rows off
    // the UI thread, then populates the list and auto-selects the first row so
    // its per-file diff loads via OnFileSelected (which dispatches on _mode).
    private void LoadFileList(
        Func<IReadOnlyList<DiffFileRow>> load,
        Func<int, string> statusFor,
        string loadingText)
    {
        _files.ItemsSource = null;
        _diff.Inlines?.Clear();
        _diff.Text = string.Empty;
        _currentDiffText = string.Empty;
        _status.Text = loadingText;
        _hasCommit = true;

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<DiffFileRow> rows = load();
                Dispatcher.UIThread.Post(() =>
                {
                    _files.ItemsSource = rows;
                    _status.Text = statusFor(rows.Count);
                    if (rows.Count > 0)
                    {
                        _files.SelectedIndex = 0;
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _status.Text = F("{0}: {1}", ErrorWord(), ex.Message));
            }
        });
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        // A plain selection always shows the comparison the file list belongs to.
        _forceWorkingTreeCompare = false;
        LoadSelectedFileDiff();
    }

    // ---------------------------------------------------------------- toolbar

    // Flat toolbar chrome: the Fluent templates paint a button's background
    // through their inner ContentPresenter, so style that part directly.
    private void AddToolbarStyles()
    {
        IBrush hover = B("App.PanelAlt");
        IBrush border = B("App.Border");
        IBrush selection = B("App.Selection");

        // Each style is "difftool" plus zero or more pseudo-classes; they must be
        // chained as separate Class(...) calls (a single "a:b" string would be read
        // as one class name and never match).
        void Chrome<T>(string[] pseudo, IBrush background, IBrush stroke)
            where T : TemplatedControl =>
            Styles.Add(new Style(x =>
            {
                Selector s = x.OfType<T>().Class("difftool");
                foreach (string cls in pseudo)
                {
                    s = s.Class(cls);
                }

                return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, background),
                    new Setter(ContentPresenter.BorderBrushProperty, stroke),
                    new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(3)),
                },
            });

        Chrome<Button>([], Brushes.Transparent, Brushes.Transparent);
        Chrome<Button>([":pointerover"], hover, border);
        Chrome<ToggleButton>([], Brushes.Transparent, Brushes.Transparent);
        Chrome<ToggleButton>([":pointerover"], hover, border);
        Chrome<ToggleButton>([":checked"], selection, B("App.Accent"));
        Chrome<ToggleButton>([":checked", ":pointerover"], selection, B("App.Accent"));
    }

    // The caption is always a glyph, never a translated word; the tooltip is set
    // separately by ApplyTranslations so a language switch can revisit it.
    private Button ToolButton(string glyph, Action? onClick)
    {
        Button button = new()
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = B("App.Text"),
                VerticalAlignment = VerticalAlignment.Center,
            },
            Padding = new Thickness(6, 2),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("difftool");

        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }

        return button;
    }

    private ToggleButton ToggleTool(string glyph, bool isChecked, Action<bool> onChanged)
    {
        ToggleButton button = new()
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = B("App.Text"),
                VerticalAlignment = VerticalAlignment.Center,
            },
            Padding = new Thickness(6, 2),
            MinWidth = 0,
            MinHeight = 0,
            IsChecked = isChecked,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("difftool");
        button.IsCheckedChanged += (_, _) => onChanged(button.IsChecked == true);

        return button;
    }

    private Control ToolSeparator() => new Border
    {
        Width = 1,
        Margin = new Thickness(3, 4),
        Background = B("App.Border"),
    };

    // The gear menu: the same options as the toolbar, plus the zoom commands.
    // The flyout's items are built in full BEFORE ShowAt (mutating them from
    // Opening leaves the popup mis-measured).
    private void ShowSettingsMenu(Control anchor)
    {
        MenuItem ignore = new()
        {
            Header = F("{0}  ({1})",
                T("FileViewer/ignoreAllWhitespaceChangesToolStripMenuItem.Text", "Ignore all whitespace changes"), "-w"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.IgnoreWhitespace,
        };
        ignore.Click += (_, _) => _ignoreWhitespaceButton.IsChecked = !_options.IgnoreWhitespace;

        MenuItem nonPrinting = new()
        {
            Header = T("FileViewer/showNonprintableCharactersToolStripMenuItem.Text", "Show nonprinting characters"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.ShowNonPrinting,
        };
        nonPrinting.Click += (_, _) => _nonPrintingButton.IsChecked = !_options.ShowNonPrinting;

        MenuItem word = new()
        {
            Header = F("{0}  ({1})",
                T("FileViewer/showGitWordColoringToolStripMenuItem.Text", "Word diff"), "--word-diff"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _options.WordDiff,
        };
        word.Click += (_, _) => _wordDiffButton.IsChecked = !_options.WordDiff;

        MenuItem zoomIn = new() { Header = T("Increase text size") };
        zoomIn.Click += (_, _) => Zoom(+1);
        MenuItem zoomOut = new() { Header = T("Decrease text size") };
        zoomOut.Click += (_, _) => Zoom(-1);
        MenuItem zoomReset = new() { Header = T("Reset text size") };
        zoomReset.Click += (_, _) => Zoom(0);

        // The encoding name is data: it must not be looked up, and its
        // underscores (if any) must be escaped so the access-key parser keeps them.
        MenuItem encodingReset = new()
        {
            Header = F(T("Reset encoding to {0}"), DiffTextService.DefaultEncodingName.Replace("_", "__")),
        };
        encodingReset.Click += (_, _) => _encodingBox.SelectedItem = DiffTextService.DefaultEncodingName;

        MenuFlyout flyout = new()
        {
            ItemsSource = new Control[]
            {
                ignore,
                nonPrinting,
                word,
                new Separator(),
                zoomIn,
                zoomOut,
                zoomReset,
                new Separator(),
                encodingReset,
            },
            Placement = PlacementMode.BottomEdgeAlignedRight,
        };

        flyout.ShowAt(anchor);
    }

    // direction: +1 larger, -1 smaller, 0 reset to the default size.
    private void Zoom(int direction)
    {
        double size = direction == 0
            ? DiffDisplayOptions.DefaultFontSize
            : Math.Clamp(_options.FontSize + direction, 6, 32);

        _options.FontSize = size;
        _diff.FontSize = size;
        _status.Text = F(T("Text size {0:0}pt"), size);
    }

    // ------------------------------------------------------- hunk navigation

    private void GoToNextChange() => GoToChange(+1);

    private void GoToPreviousChange() => GoToChange(-1);

    private void GoToChange(int step)
    {
        if (_hunkLines.Count == 0)
        {
            _status.Text = T("No changes to navigate in this file.");
            return;
        }

        int next = _hunkIndex < 0
            ? (step > 0 ? 0 : _hunkLines.Count - 1)
            : Math.Clamp(_hunkIndex + step, 0, _hunkLines.Count - 1);

        _hunkIndex = next;
        ScrollToLine(_hunkLines[next]);
        _status.Text = F(T("Change {0} of {1}"), next + 1, _hunkLines.Count);
    }

    // The diff pane is a uniform monospace block, so a line's offset is simply
    // its index times the measured average line height.
    private void ScrollToLine(int line)
    {
        int lineCount = Math.Max(1, _currentDiffText.Split('\n').Length);
        double height = _diff.Bounds.Height;
        double lineHeight = height > 0 ? height / lineCount : _diff.FontSize * 1.4;
        double y = Math.Max(0, (line * lineHeight) + _diff.Margin.Top - (lineHeight * 2));

        _diffScroll.Offset = new Vector(_diffScroll.Offset.X, y);
    }

    // ---------------------------------------------------------- diff loading

    // Re-runs the diff of the currently selected file with the current options
    // (called by every toolbar toggle that maps onto a git argument).
    private void ReloadDiff() => LoadSelectedFileDiff();

    // Loads the selected file's patch through DiffTextService, so the toolbar
    // options (-w, --word-diff, encoding) become real git arguments.
    private void LoadSelectedFileDiff()
    {
        if (_files.SelectedItem is not DiffFileRow row || _repoPath is null || _commitHash is null)
        {
            return;
        }

        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        CancellationToken token = _diffCts.Token;

        DiffTextKind kind = _forceWorkingTreeCompare || _mode == CompareMode.WorkingTree
            ? DiffTextKind.WorkingTree
            : _mode == CompareMode.Range
                ? DiffTextKind.Range
                : DiffTextKind.Commit;

        DiffTextRequest request = new(kind, _repoPath, _commitHash, _baseHash, row.Name, row.OldName);

        // Snapshot the options: they live on the UI thread and the git run does not.
        DiffDisplayOptions options = new()
        {
            IgnoreWhitespace = _options.IgnoreWhitespace,
            ShowNonPrinting = _options.ShowNonPrinting,
            WordDiff = _options.WordDiff,
            EncodingName = _options.EncodingName,
            FontSize = _options.FontSize,
        };

        _diff.Inlines?.Clear();
        _diff.Text = T("FormBrowse/_loading.Text", "Loading diff…");

        _ = Task.Run(async () =>
        {
            try
            {
                string text = await DiffTextService.GetDiffTextAsync(request, options, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        RenderDiff(text);

                        // Show the command that produced the patch, so the effect of
                        // the toolbar toggles (-w, --word-diff) is visible.
                        _status.Text = DiffTextService.DescribeCommand(request, options);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another selection/toggle; ignore.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _diff.Inlines?.Clear();
                        _diff.Text = F("{0}: {1}", ErrorWord(), ex.Message);
                    }
                });
            }
        });
    }

    // Renders spaces/tabs/CR as visible symbols when the ¶ toggle is on.
    private static string ApplyNonPrinting(string line) => line
        .Replace("\r", "␍", StringComparison.Ordinal)
        .Replace("\t", "→   ", StringComparison.Ordinal)
        .Replace(" ", "·", StringComparison.Ordinal);

    // Colour each diff line: added green, removed red, hunk headers blue,
    // file/meta headers gray.
    private void RenderDiff(string diffText)
    {
        _currentDiffText = diffText;
        _diff.Text = string.Empty;
        InlineCollection inlines = _diff.Inlines ??= [];
        inlines.Clear();
        _hunkLines.Clear();
        _hunkIndex = -1;

        int lineNumber = -1;
        foreach (string rawLine in diffText.Split('\n'))
        {
            lineNumber++;
            string line = rawLine;
            IBrush? brush = null;

            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("diff ", StringComparison.Ordinal) ||
                line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("new file", StringComparison.Ordinal) ||
                line.StartsWith("deleted file", StringComparison.Ordinal) ||
                line.StartsWith("rename ", StringComparison.Ordinal) ||
                line.StartsWith("copy ", StringComparison.Ordinal) ||
                line.StartsWith("similarity ", StringComparison.Ordinal))
            {
                brush = B("App.TextDim");
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                brush = B("App.Accent");
                _hunkLines.Add(lineNumber);
            }
            else if (line.StartsWith('+'))
            {
                brush = AddedBrush;
            }
            else if (line.StartsWith('-'))
            {
                brush = RemovedBrush;
            }

            if (_options.ShowNonPrinting)
            {
                line = ApplyNonPrinting(line);
            }

            Run run = new(line + "\n");
            if (brush is not null)
            {
                run.Foreground = brush;
            }

            inlines.Add(run);
        }
    }
}
