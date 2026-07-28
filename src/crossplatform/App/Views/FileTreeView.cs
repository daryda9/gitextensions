using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitExtensions.Avalonia.Services;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The browse window's "File tree" tab: every tracked file of the selected
///  commit's tree, as a real folder tree, next to the <b>content</b> of the
///  selected file at that commit.
///
///  <para>Upstream this tab has no class of its own — it is a second
///  <c>RevisionDiffControl</c> bound with <c>revisionFileTree: null</c>
///  (<c>FormBrowse.Designer.cs:76</c>, <c>FormBrowse.cs:331</c>), so
///  <c>IsFileTreeMode</c> is true: it reuses the changed-files list and shows the
///  blob (<c>forceFileView: IsFileTreeMode</c>) instead of a patch. The port does
///  the same, over <see cref="FileStatusListView"/>: the list, its regular
///  expression filter and its folder tree come from there, with its grouping
///  toolbar hidden and its status glyphs off — exactly as upstream hides
///  <c>Toolbar</c> in file-tree mode, and because a tree entry has no change kind
///  to report.</para>
///
///  <para>Jumping to another tab is not this view's business: it raises
///  <see cref="BlameRequested"/> / <see cref="FileHistoryRequested"/> with the
///  path (as <see cref="DiffView"/> does) and the window wires them.</para>
///
///  <para>All git work runs off the UI thread and never throws — failures surface
///  as a status line. Captions come from the upstream <c>FileStatusList</c> /
///  <c>FileViewer</c> ids where there is one, and are re-applied on
///  <see cref="TranslationService.LanguageChanged"/>.</para>
/// </summary>
public sealed class FileTreeView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    // Token colours, in the same key as the diff viewer's (the palette has no
    // token resources, so both views carry the literal values).
    private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));
    private static readonly IBrush StringBrush = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
    private static readonly IBrush CommentBrush = new SolidColorBrush(Color.FromRgb(0x7E, 0x9E, 0x7E));
    private static readonly IBrush NumberBrush = new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8));
    private static readonly IBrush PreprocessorBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0x86, 0xC0));

    // Highlighting splits a line into several Runs, and every Run is its own text
    // layout box: past this many lines the file renders one Run per line, which is
    // the same rule (and the same cap) the diff viewer uses.
    private const int MaxHighlightLines = 20_000;

    // Bytes of a blob to sniff for a NUL before deciding it is binary; git's own
    // heuristic looks at the first 8000 bytes.
    private const int BinarySniffLength = 8000;

    private readonly FileStatusListView _files;
    private readonly SelectableTextBlock _content;
    private readonly ScrollViewer _contentScroll;
    private readonly TextBlock _status;
    private readonly ExternalToolService _tools = new();

    // The File-tree tab is always a path tree: its grouping is not the session
    // choice the diff pane and the commit dialog share, so it gets its own state.
    private readonly FileStatusListOptions _treeOptions = new()
    {
        GroupMode = DiffFileGroupMode.Path,
        AsTree = true,
    };

    // Kept so a language switch can re-label them in place (a ContextMenu
    // re-populated from Opening mis-measures).
    private readonly MenuItem _collapseAllItem;
    private readonly MenuItem _expandAllItem;
    private readonly MenuItem _collapseRootFoldersItem;
    private readonly Separator _treeSeparator;
    private readonly MenuItem _openWorkingFileItem;
    private readonly MenuItem _openRevisionFileItem;
    private readonly MenuItem _showInFolderItem;
    private readonly MenuItem _saveAsItem;
    private readonly CopyPathsMenuItem _copyPathItem;
    private readonly MenuItem _historyItem;
    private readonly MenuItem _blameItem;

    private string? _repoPath;
    private string? _commitHash;

    // Set while the tab shows an artificial row instead of a commit; it decides
    // which side the file CONTENT is read from (the index, or the file on disk).
    private ArtificialDiff? _artificial;

    // The short hash currently listed, or null while nothing is loaded: it lets a
    // language switch re-state the header without re-running git.
    private string? _shortHash;
    private int _fileCount;
    private string? _loadError;

    // The path whose content the viewer shows, for the syntax highlighter.
    private string? _contentPath;

    private CancellationTokenSource? _contentCts;

    public FileTreeView()
    {
        _files = new FileStatusListView(_treeOptions)
        {
            ShowToolbar = false,          // upstream: Toolbar.Visible = false in file-tree mode
            ShowStatusGlyphs = false,     // a tree entry is neither added nor modified
            CollapseGroupsOnLoad = true,  // upstream: expandIfFewFiles = !_isFileTreeMode
        };
        _files.SelectedFileChanged += OnSelectedFileChanged;
        _files.List.DoubleTapped += OnListDoubleTapped;

        // A right click must also move the selection onto the row under the
        // pointer, otherwise the menu acts on whatever was selected before (the
        // same defect the blame grid had in M51). Tunnelling, and handled events
        // too, because the ListBox handles the press itself.
        _files.List.AddHandler(
            PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        // ---- context menu: the tree commands, then the file commands ----
        _collapseAllItem = new MenuItem();
        _collapseAllItem.Click += (_, _) => _files.CollapseAllGroups();
        _expandAllItem = new MenuItem();
        _expandAllItem.Click += (_, _) => _files.ExpandAllGroups();
        _collapseRootFoldersItem = new MenuItem();
        _collapseRootFoldersItem.Click += (_, _) => _files.CollapseRootFolders();
        _treeSeparator = new Separator();

        _openWorkingFileItem = new MenuItem();
        _openWorkingFileItem.Click += (_, _) => OpenSelectedWorkingFile();
        _openRevisionFileItem = new MenuItem();
        _openRevisionFileItem.Click += (_, _) => OpenSelectedRevisionFile();
        _showInFolderItem = new MenuItem();
        _showInFolderItem.Click += (_, _) => ShowSelectedInFolder();
        _saveAsItem = new MenuItem();
        _saveAsItem.Click += (_, _) => SaveSelectedAs();
        _copyPathItem = new CopyPathsMenuItem(
            () => _files.SelectedFiles.Select(r => r.Name),
            () => _repoPath,
            CopyToClipboard);
        _historyItem = new MenuItem();
        _historyItem.Click += (_, _) => RaiseFileAction(FileHistoryRequested);
        _blameItem = new MenuItem();
        _blameItem.Click += (_, _) => RaiseFileAction(BlameRequested);

        ContextMenu fileMenu = new()
        {
            ItemsSource = new Control[]
            {
                _collapseAllItem,
                _expandAllItem,
                _collapseRootFoldersItem,
                _treeSeparator,
                _openWorkingFileItem,
                _openRevisionFileItem,
                _showInFolderItem,
                _saveAsItem,
                new Separator(),
                _copyPathItem,
                _historyItem,
                _blameItem,
            },
        };
        fileMenu.Opening += (_, _) => UpdateMenuState();
        _files.List.ContextMenu = fileMenu;

        // ---- the content pane ----
        _status = new TextBlock
        {
            Margin = new Thickness(8, 4, 8, 4),
            Foreground = B("App.TextDim"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _content = new SelectableTextBlock
        {
            FontFamily = Monospace,
            FontSize = 12,
            Foreground = B("App.Text"),
            Background = B("App.Panel"),
            Margin = new Thickness(6, 2, 6, 6),
        };

        _contentScroll = new ScrollViewer
        {
            Content = _content,
            Background = B("App.Panel"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Border statusBar = new()
        {
            Background = B("App.Toolbar"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _status,
        };

        DockPanel right = new() { Background = B("App.Panel") };
        DockPanel.SetDock(statusBar, Dock.Top);
        right.Children.Add(statusBar);
        right.Children.Add(_contentScroll);

        GridSplitter splitter = new()
        {
            Width = 4,
            Background = B("App.Border"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        Grid root = new()
        {
            ColumnDefinitions = new ColumnDefinitions("300,Auto,*"),
            Background = B("App.Window"),
        };
        Grid.SetColumn(_files, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        root.Children.Add(_files);
        root.Children.Add(splitter);
        root.Children.Add(right);

        Content = root;
        ClipToBounds = true;

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------ public surface

    /// <summary>
    ///  Raised with a repository-relative path when the user asks for that file's
    ///  blame. The window switches to the Blame tab and loads it.
    /// </summary>
    public event Action<string>? BlameRequested;

    /// <summary>
    ///  Raised with a repository-relative path when the user asks for that file's
    ///  history (also on a double click, as upstream's
    ///  <c>DiffFiles_DoubleClick</c> opens the file-history dialog).
    /// </summary>
    public event Action<string>? FileHistoryRequested;

    /// <summary>The selected file's repository-relative path, or <see langword="null"/>.</summary>
    public string? SelectedPath => _files.SelectedFile?.Name;

    /// <summary>Puts the caret in the list's filter box.</summary>
    public void FocusFilter() => _files.FocusFilter();

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string ErrorWord() => T("Error");

    // Re-states the header in the active language. A failed load keeps its
    // message: it belongs to a git run that already happened.
    private void ApplyTranslations()
    {
        _collapseAllItem.Header = T("FileStatusList/_collapseAll.Text", "Collapse all");
        _expandAllItem.Header = T("FileStatusList/_expandAll.Text", "Expand all");
        _collapseRootFoldersItem.Header = T("FileStatusList/_collapseRootFolders.Text", "Collapse root folders");
        _openWorkingFileItem.Header = T(
            "FileStatusList/tsmiOpenWorkingDirectoryFile.Text", "Open working directory file");
        _openRevisionFileItem.Header = T(
            "FileStatusList/tsmiOpenRevisionFile.Text", "Open this revision (temp file)");
        _showInFolderItem.Header = T("FileStatusList/tsmiShowInFolder.Text", "Show in folder");
        _saveAsItem.Header = T("FileStatusList/tsmiSaveAs.Text", "Save selected as...");
        _copyPathItem.ApplyTranslations();
        _historyItem.Header = T("FileStatusList/tsmiFileHistory.Text", "File history");
        _blameItem.Header = T("FileStatusList/tsmiBlame.Text", "Blame");

        UpdateStatus();
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    // The status line describes what the pane shows: the loaded file, or the tree.
    private void UpdateStatus()
    {
        if (_loadError is { Length: > 0 } error)
        {
            _status.Text = F(T("Could not list files at {0}: {1}"), _shortHash, error);
            return;
        }

        if (_shortHash is not { Length: > 0 } hash)
        {
            _status.Text = T("No commit selected.");
            return;
        }

        if (_contentPath is { Length: > 0 } path)
        {
            _status.Text = F("{0}  @  {1}", path, hash);
            return;
        }

        _status.Text = _fileCount > 0
            ? F(T("{0} file(s) at {1}"), _fileCount, hash)
            : F(T("(no tracked files at {0})"), hash);
    }

    // ------------------------------------------------------------ tree loading

    /// <summary>
    ///  Loads the tree of <paramref name="commitHash"/> in the repository at
    ///  <paramref name="repoPath"/> and lists every tracked file it holds. Heavy
    ///  git work runs off the UI thread; results are marshalled back to it.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _artificial = null;
        Load(repoPath, commitHash, commitHash.Length > 8 ? commitHash[..8] : commitHash,
            () => DiffService.GetTreeFiles(repoPath, commitHash));
    }

    /// <summary>
    ///  Loads the file tree of one of the two <b>artificial</b> revision rows —
    ///  the File tree half of the
    ///  <c>RevisionGridView.ArtificialRevisionSelected</c> contract, called by the
    ///  host instead of <see cref="ShowCommit"/> when the selection lands there.
    ///
    ///  <list type="bullet">
    ///   <item><see cref="ArtificialDiff.WorkTree"/> lists the files as they are
    ///    <b>on disk</b> (tracked plus untracked non-ignored, minus what was
    ///    deleted) and reads their content from disk.</item>
    ///   <item><see cref="ArtificialDiff.Index"/> lists the <b>index</b>
    ///    (<c>git ls-files</c>) and reads each file's staged content
    ///    (<c>git show :&lt;path&gt;</c>), which is what makes a partially staged
    ///    file show its staged version here and its working version there.</item>
    ///  </list>
    ///
    ///  <para>Upstream shows a tree for these rows too, with no message and nothing
    ///  disabled ("File Tree tab […] works for artificial commits, too",
    ///  <c>FormBrowse.cs:1223</c>).</para>
    /// </summary>
    public void ShowArtificial(string repoPath, ArtificialDiff which)
    {
        _artificial = which;

        // The sentinel hash identifies the load (the stale-result guard compares
        // it); it is never handed to git as a revision — see RevisionOfContent.
        string hash = which == ArtificialDiff.Index ? DiffService.IndexHash : DiffService.WorkTreeHash;

        Load(repoPath, hash, ArtificialRevisionName.Of(which),
            () => DiffService.GetArtificialTreeFiles(repoPath, which));
    }

    // Shared loader: <paramref name="label"/> is what the status line calls the
    // thing being listed (a short hash, or an artificial row's name).
    private void Load(string repoPath, string commitHash, string label, Func<IReadOnlyList<DiffFileRow>> load)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;
        _shortHash = label;
        _fileCount = 0;
        _loadError = null;
        _contentPath = null;
        ClearContent();

        _files.Clear();
        _status.Text = F(T("Loading files at {0}…"), _shortHash);

        _ = Task.Run(() =>
        {
            IReadOnlyList<DiffFileRow> rows = [];
            string? error = null;
            try
            {
                rows = load();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // A newer selection already superseded this load.
                if (!string.Equals(_commitHash, commitHash, StringComparison.Ordinal))
                {
                    return;
                }

                _loadError = error;
                if (error is { Length: > 0 })
                {
                    UpdateStatus();
                    return;
                }

                _fileCount = rows.Count;

                // A new list instance every time: re-assigning the same one leaves
                // the realized containers showing their old visuals.
                _files.SetFiles(rows);
                UpdateStatus();
            });
        });
    }

    /// <summary>Empties the tab (no repository, or a failed load).</summary>
    public void Clear()
    {
        _repoPath = null;
        _commitHash = null;
        _artificial = null;
        _shortHash = null;
        _fileCount = 0;
        _loadError = null;
        _contentPath = null;
        _files.Clear();
        ClearContent();
        UpdateStatus();
    }

    // ------------------------------------------------------------ file content

    private void OnSelectedFileChanged(DiffFileRow? row)
    {
        _contentPath = row?.Name;
        if (row is null)
        {
            ClearContent();
            UpdateStatus();
            return;
        }

        LoadContent(row.Name);
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_files.List).Properties.IsRightButtonPressed)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } row)
        {
            return;
        }

        if (row.DataContext is FileListFileNode node)
        {
            _files.List.SelectedItem = node;
            return;
        }

        // A folder header: the press is swallowed, because letting the ListBox
        // select it would fold the folder — a left click's job, not the menu's.
        // The context menu still opens: it answers the pointer release.
        if (row.DataContext is FileListGroupNode)
        {
            e.Handled = true;
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Upstream's double click in file-tree mode opens the file history dialog
        // (RevisionDiffControl.DiffFiles_DoubleClick).
        RaiseFileAction(FileHistoryRequested);
    }

    private void ClearContent()
    {
        _content.Inlines?.Clear();
        _content.Text = string.Empty;
    }

    // Loads the blob at the displayed commit and renders it. This is the port of
    // "forceFileView: IsFileTreeMode": the tab shows the file, never a patch.
    private void LoadContent(string path)
    {
        if (_repoPath is not string repoPath || _commitHash is null)
        {
            return;
        }

        string? commit = RevisionOfContent();

        _contentCts?.Cancel();
        _contentCts?.Dispose();
        _contentCts = new CancellationTokenSource();
        CancellationToken token = _contentCts.Token;

        UpdateStatus();

        _ = Task.Run(async () =>
        {
            try
            {
                byte[] bytes = await DiffTextService
                    .GetFileBytesAsync(repoPath, commit, path, token)
                    .ConfigureAwait(false);

                bool binary = IsBinary(bytes);
                string text = binary
                    ? F(T("(binary file — {0} byte(s))"), bytes.Length)
                    : Decode(bytes);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    RenderContent(text, path, highlight: !binary);
                    _contentScroll.ScrollToHome();
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by another selection; ignore.
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        ClearContent();
                        _content.Text = F("{0}: {1}", ErrorWord(), ex.Message);
                    }
                });
            }
        });
    }

    // Which side the displayed file's content is read from: the commit for a real
    // revision, the index (":<path>") for the "Commit index" row, and the file on
    // disk (null) for the "Working directory" row. Never the sentinel hash, which
    // git could not resolve.
    private string? RevisionOfContent() => _artificial switch
    {
        ArtificialDiff.Index => ":",
        ArtificialDiff.WorkTree => null,
        _ => _commitHash,
    };

    // A NUL byte in the head of the blob means binary, as git decides it too.
    private static bool IsBinary(byte[] bytes)
    {
        int end = Math.Min(bytes.Length, BinarySniffLength);
        for (int i = 0; i < end; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Renders the file with the shared syntax highlighter, one Run per token
    // (and one per line when there is nothing to colour, or the file is too big).
    private void RenderContent(string text, string path, bool highlight)
    {
        _content.Text = string.Empty;
        InlineCollection inlines = _content.Inlines ??= [];
        inlines.Clear();

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        SyntaxLanguage? language = highlight && lines.Length <= MaxHighlightLines
            ? DiffSyntaxHighlighter.Detect(path)
            : null;

        if (language is null)
        {
            _content.Text = text;
            return;
        }

        SyntaxState state = new();
        List<SyntaxSpan> spans = [];

        foreach (string line in lines)
        {
            spans.Clear();
            DiffSyntaxHighlighter.Tokenize(language, line, 0, state, spans);

            int pos = 0;
            int firstRun = inlines.Count;
            foreach (SyntaxSpan span in spans)
            {
                if (span.Start < pos || span.Start + span.Length > line.Length)
                {
                    continue;
                }

                if (span.Start > pos)
                {
                    inlines.Add(Segment(line[pos..span.Start], null));
                }

                inlines.Add(Segment(line.Substring(span.Start, span.Length), TokenBrush(span.Kind)));
                pos = span.Start + span.Length;
            }

            if (pos < line.Length)
            {
                inlines.Add(Segment(line[pos..], null));
            }

            // The line break rides on the line's last Run, so an uncoloured line
            // still costs exactly one Run.
            if (inlines.Count == firstRun)
            {
                inlines.Add(Segment("\n", null));
            }
            else if (inlines[^1] is Run tail)
            {
                tail.Text += "\n";
            }
        }
    }

    private static Run Segment(string text, IBrush? foreground) => new(text)
    {
        Foreground = foreground ?? B("App.Text"),
    };

    private static IBrush TokenBrush(SyntaxTokenKind kind) => kind switch
    {
        SyntaxTokenKind.Keyword => KeywordBrush,
        SyntaxTokenKind.String => StringBrush,
        SyntaxTokenKind.Comment => CommentBrush,
        SyntaxTokenKind.Number => NumberBrush,
        SyntaxTokenKind.Preprocessor => PreprocessorBrush,
        _ => B("App.Text"),
    };

    // ------------------------------------------------------- file commands

    // Only IsEnabled/IsVisible here: the items themselves were built in the
    // constructor.
    private void UpdateMenuState()
    {
        bool hasFile = _files.SelectedFile is not null && _repoPath is not null;
        bool onDisk = hasFile && File.Exists(SelectedWorkingPath());

        // The tree commands only make sense while there are folders to fold, which
        // is how upstream hides them (UpdateStatusOfTreeContextMenuItems).
        bool hasGroups = _files.HasGroups;
        _collapseAllItem.IsVisible = hasGroups;
        _expandAllItem.IsVisible = hasGroups;
        _collapseRootFoldersItem.IsVisible = hasGroups;
        _treeSeparator.IsVisible = hasGroups;

        _openWorkingFileItem.IsEnabled = onDisk;
        _showInFolderItem.IsEnabled = onDisk;
        _openRevisionFileItem.IsEnabled = hasFile && _commitHash is not null;
        _saveAsItem.IsEnabled = hasFile && _commitHash is not null;
        _copyPathItem.IsEnabled = hasFile;
        _historyItem.IsEnabled = hasFile;
        _blameItem.IsEnabled = hasFile;
    }

    private void RaiseFileAction(Action<string>? action)
    {
        if (_files.SelectedFile is DiffFileRow row)
        {
            action?.Invoke(row.Name);
        }
    }

    // Absolute path of the selected file in the working tree (it may not exist:
    // the file can belong to an old revision only).
    private string? SelectedWorkingPath() =>
        _files.SelectedFile is DiffFileRow row && _repoPath is not null
            ? Path.GetFullPath(Path.Combine(_repoPath, row.Name))
            : null;

    private void CopyToClipboard(string text)
        => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);

    private void OpenSelectedWorkingFile()
    {
        if (SelectedWorkingPath() is not string path || _repoPath is not string repoPath)
        {
            return;
        }

        RunFileCommand(() => _tools.OpenInEditor(path, repoPath));
    }

    private void ShowSelectedInFolder()
    {
        if (SelectedWorkingPath() is not string path)
        {
            return;
        }

        RunFileCommand(() => _tools.ShowInFolder(path));
    }

    // Materialises the file as of the listed commit into a temp directory and
    // opens that copy — the original's "Open this revision".
    private void OpenSelectedRevisionFile()
    {
        if (_files.SelectedFile is not DiffFileRow row ||
            _repoPath is not string repoPath ||
            _commitHash is not string commit)
        {
            return;
        }

        string name = row.Name;

        // The listed side, not the sentinel: the index for the "Commit index" row,
        // the file on disk for "Working directory" (see RevisionOfContent).
        string? rev = RevisionOfContent();
        string label = _artificial switch
        {
            ArtificialDiff.Index => "index",
            ArtificialDiff.WorkTree => "worktree",
            _ => commit.Length > 8 ? commit[..8] : commit,
        };

        RunFileLaunch(async () =>
        {
            byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, rev, name)
                .ConfigureAwait(false);

            string dir = Path.Combine(Path.GetTempPath(), "GitExtensions.Avalonia", label);
            Directory.CreateDirectory(dir);

            string temp = Path.Combine(dir, Path.GetFileName(name));
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);

            return _tools.OpenInEditor(temp, repoPath);
        });
    }

    private void SaveSelectedAs()
    {
        if (_files.SelectedFile is not DiffFileRow row ||
            _repoPath is not string repoPath ||
            _commitHash is null)
        {
            return;
        }

        _ = SaveSelectedAsCoreAsync(repoPath, RevisionOfContent(), row.Name);
    }

    // The picker must run on the UI thread; the git read and the write do not.
    // "commit" is null when the listed side is the working tree (see RevisionOfContent).
    private async Task SaveSelectedAsCoreAsync(string repoPath, string? commit, string name)
    {
        try
        {
            IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                _status.Text = T("No file picker is available on this display.");
                return;
            }

            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("FileStatusList/tsmiSaveAs.Text", "Save selected as..."),
                SuggestedFileName = Path.GetFileName(name),
                ShowOverwritePrompt = true,
            });

            if (target is null)
            {
                return;   // cancelled
            }

            string? destination = target.TryGetLocalPath();
            if (destination is null)
            {
                _status.Text = T("The chosen location is not a local file.");
                return;
            }

            _status.Text = F(T("Saving {0}…"), destination);

            await Task.Run(async () =>
            {
                byte[] bytes = await DiffTextService.GetFileBytesAsync(repoPath, commit, name)
                    .ConfigureAwait(false);
                await File.WriteAllBytesAsync(destination, bytes).ConfigureAwait(false);
            });

            _status.Text = F(T("Saved {0}"), destination);
        }
        catch (Exception ex)
        {
            _status.Text = F("{0}: {1}", ErrorWord(), ex.Message);
        }
    }

    // Runs a blocking external-tool launch off the UI thread and reports the
    // outcome on the status line. Never throws into the caller.
    private void RunFileCommand(Func<ExternalToolResult> command) =>
        RunFileLaunch(() => Task.FromResult(command()));

    private void RunFileLaunch(Func<Task<ExternalToolResult>> command) =>
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                ExternalToolResult result = await command().ConfigureAwait(false);
                message = result.Message;
            }
            catch (Exception ex)
            {
                message = F("{0}: {1}", ErrorWord(), ex.Message);
            }

            Dispatcher.UIThread.Post(() => _status.Text = message);
        });
}
