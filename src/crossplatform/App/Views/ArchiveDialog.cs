using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Modal for archiving a single commit — the port of upstream's
///  <c>FormArchive</c>. Beyond the format selector and the output path it now
///  carries the parts of that form the port was missing:
///  <list type="bullet">
///   <item>the <b>"This revision will be archived"</b> panel
///    (<c>FormArchive.Designer.cs:86-106</c>) — hash, subject, author and date,
///    instead of just a hash in the title bar;</item>
///   <item><b>path filter</b> (<c>:318-337</c>): one pathspec per line, passed to
///    <c>git archive … -- &lt;paths&gt;</c>;</item>
///   <item><b>revision filter</b> (<c>:306-316</c>, <c>.cs:148-158</c>): archive only
///    the files that changed since another revision, resolved through
///    <see cref="DiffService.GetDiffFilesBetween"/> with deleted files dropped —
///    a deleted file is not in the archived tree and would make
///    <c>git archive</c> fail outright.</item>
///  </list>
///
///  <para>
///  Upstream picks the "other" revision with <c>btnChooseRevision</c> →
///  <c>FormChooseCommit</c>. The port has no reusable commit picker, so the
///  revision is typed instead (any expression git understands) and validated with
///  <c>rev-parse</c> before use; wiring a real picker is left for when one exists.
///  </para>
///
///  <para>
///  <c>git archive</c> and the diff both run off the UI thread; errors are shown
///  inline. On success the dialog closes and the written path is exposed through
///  <see cref="ArchivedPath"/>. Escape closes the window (M57 convention).
///  Styled from the shared App.* brushes so it matches the active theme.
///  </para>
/// </summary>
public sealed class ArchiveDialog : Window
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private readonly string _repoPath;
    private readonly string _commitHash;

    private readonly TextBlock _revisionHash;
    private readonly TextBlock _revisionSubject;
    private readonly TextBlock _revisionAuthor;

    private readonly ComboBox _format;
    private readonly TextBox _outputPath;

    private readonly CheckBox _usePathFilter;
    private readonly TextBox _paths;
    private readonly CheckBox _useRevisionFilter;
    private readonly TextBox _sinceRevision;

    private readonly TextBlock _status;
    private readonly Button _archive;
    private readonly Button _browse;

    /// <summary>The path of the written archive, or null if the dialog was cancelled / failed.</summary>
    public string? ArchivedPath { get; private set; }

    public ArchiveDialog(string repoPath, string commitHash)
    {
        _repoPath = repoPath;
        _commitHash = commitHash;

        IBrush text = Brush("App.Text", "#DCDCDC");
        IBrush dim = Brush("App.TextDim", "#9B9B9B");
        IBrush border = Brush("App.Border", "#3F3F46");

        Title = T("FormArchive/$this.Text", "Archive revision");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("App.Window", "#1E1E1E");

        _revisionHash = new TextBlock
        {
            Text = ShortHash(commitHash),
            Foreground = text,
            FontFamily = Monospace,
            FontSize = 12,
        };
        _revisionSubject = new TextBlock
        {
            Text = "…",
            Foreground = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _revisionAuthor = new TextBlock
        {
            Text = string.Empty,
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        Border revisionPanel = new()
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Children = { _revisionHash, _revisionSubject, _revisionAuthor },
            },
        };

        _format = new ComboBox
        {
            ItemsSource = new[] { "zip", "tar.gz" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 140,
        };
        _format.SelectionChanged += (_, _) => SyncExtension();

        _outputPath = new TextBox { Watermark = T("Output file path") };

        _browse = new Button { Content = T("Browse…"), Margin = new Thickness(8, 0, 0, 0) };
        _browse.Click += (_, _) => _ = BrowseAsync();

        Grid pathRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetColumn(_outputPath, 0);
        Grid.SetColumn(_browse, 1);
        pathRow.Children.Add(_outputPath);
        pathRow.Children.Add(_browse);

        _usePathFilter = new CheckBox
        {
            Content = T("FormArchive/checkBoxPathFilter.Text", "Only files matching these paths"),
            Foreground = text,
        };
        _paths = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Monospace,
            FontSize = 12,
            Height = 56,
            Margin = new Thickness(0, 4, 0, 0),
            Watermark = T("One path per line, relative to the repository root"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_paths, ScrollBarVisibility.Auto);

        _useRevisionFilter = new CheckBox
        {
            Content = T("FormArchive/checkboxRevisionFilter.Text", "Only files changed since another revision"),
            Foreground = text,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _sinceRevision = new TextBox
        {
            Watermark = T("Revision to compare with (hash, branch, tag, HEAD~1…)"),
            FontFamily = Monospace,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _usePathFilter.IsCheckedChanged += (_, _) => SyncFilters();
        _useRevisionFilter.IsCheckedChanged += (_, _) => SyncFilters();
        SyncFilters();

        _status = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = dim,
            TextWrapping = TextWrapping.Wrap,
        };

        _archive = new Button
        {
            Content = T("FormArchive/btnArchiveRevision.Text", "Archive"),
            MinWidth = 90,
            IsDefault = true,
        };
        _archive.Click += (_, _) => _ = ArchiveAsync();
        Button cancel = new()
        {
            Content = T("Cancel"),
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
                    Text = T("FormArchive/lblChooseRevision.Text", "This revision will be archived:"),
                    Foreground = dim,
                    Margin = new Thickness(0, 0, 0, 4),
                },
                revisionPanel,
                new TextBlock { Text = T("Format:"), Foreground = text, Margin = new Thickness(0, 14, 0, 4) },
                _format,
                new TextBlock { Text = T("Output file:"), Foreground = text, Margin = new Thickness(0, 12, 0, 4) },
                pathRow,
                new TextBlock
                {
                    Text = T("FormArchive/groupBoxFilter.Text", "Filter"),
                    Foreground = dim,
                    Margin = new Thickness(0, 16, 0, 6),
                },
                _usePathFilter,
                _paths,
                _useRevisionFilter,
                _sinceRevision,
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { _archive, cancel },
                },
            },
        };

        // Escape = Close (upstream's CancelButton).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Bubble);

        DialogKeys.EnsureFocusRoute(this);

        _ = LoadRevisionAsync();
    }

    private ArchiveFormat SelectedFormat => _format.SelectedIndex == 1 ? ArchiveFormat.TarGz : ArchiveFormat.Zip;

    private string Extension => SelectedFormat == ArchiveFormat.TarGz ? ".tar.gz" : ".zip";

    // Fills the "This revision will be archived" panel. Reading the commit needs
    // git, so it happens off the UI thread and the panel starts with just the hash.
    private async Task LoadRevisionAsync()
    {
        CommitDetailInfo? info;
        try
        {
            info = await Task.Run(() => new CommitDetailService().LoadCommit(_repoPath, _commitHash));
        }
        catch (Exception)
        {
            info = null;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (info is null)
            {
                _revisionSubject.Text = T("(commit details unavailable)");
                return;
            }

            _revisionHash.Text = info.ShortHash;
            _revisionSubject.Text = info.Subject;
            _revisionAuthor.Text = $"{info.Author} — {info.AuthorDate}";
        });
    }

    private void SyncFilters()
    {
        _paths.IsEnabled = _usePathFilter.IsChecked == true;
        _sinceRevision.IsEnabled = _useRevisionFilter.IsChecked == true;
    }

    // Keeps the output path's extension in sync with the chosen format.
    private void SyncExtension()
    {
        string? current = _outputPath.Text;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        string trimmed = current;
        if (trimmed.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^7];
        }
        else if (trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        _outputPath.Text = trimmed + Extension;
    }

    private async Task BrowseAsync()
    {
        try
        {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Save archive as"),
                SuggestedFileName = $"{ShortHash(_commitHash)}{Extension}",
                DefaultExtension = Extension.TrimStart('.'),
            });

            if (file is null)
            {
                return;
            }

            string? localPath = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
            {
                _outputPath.Text = localPath;
            }
        }
        catch (Exception ex)
        {
            _status.Text = T("Error: ") + ex.Message;
        }
    }

    private async Task ArchiveAsync()
    {
        string path = _outputPath.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            _status.Text = T("Choose an output file.");
            return;
        }

        bool byPaths = _usePathFilter.IsChecked == true;
        bool byRevision = _useRevisionFilter.IsChecked == true;
        string[] typedPaths = Lines(_paths.Text);
        string sinceRevision = (_sinceRevision.Text ?? string.Empty).Trim();

        if (byPaths && typedPaths.Length == 0)
        {
            _status.Text = T("The path filter is on but no path was given.");
            return;
        }

        if (byRevision && sinceRevision.Length == 0)
        {
            _status.Text = T("FormArchive/_noRevisionSelected.Text", "Select a revision to compare with.");
            return;
        }

        SetBusy(true);
        _status.Text = T("Archiving…");

        ArchiveFormat format = SelectedFormat;
        string repo = _repoPath;
        string hash = _commitHash;

        (bool Ok, string Message) outcome;
        try
        {
            outcome = await Task.Run(() =>
            {
                List<string> pathspec = [];

                if (byPaths)
                {
                    pathspec.AddRange(typedPaths);
                }
                else if (byRevision)
                {
                    string? other = RevertArchiveService.ResolveCommit(repo, sinceRevision);
                    if (other is null)
                    {
                        return (false, string.Format(T("Not a revision: {0}"), sinceRevision));
                    }

                    // Deleted files are not in the archived tree; asking git archive
                    // for them fails the whole command ("pathspec did not match").
                    IReadOnlyList<DiffFileRow> changed = DiffService.GetDiffFilesBetween(repo, other, hash);
                    pathspec.AddRange(changed
                        .Where(f => f.Kind != DiffChangeKind.Deleted)
                        .Select(f => f.Name));

                    if (pathspec.Count == 0)
                    {
                        return (false, T("Nothing changed between those two revisions."));
                    }
                }

                RevertArchiveResult result = new RevertArchiveService()
                    .Archive(repo, hash, format, path, pathspec);
                return (result.Success, result.Output);
            });
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = T("Archive failed: ") + ex.Message;
            return;
        }

        if (outcome.Ok)
        {
            ArchivedPath = path;
            Close();
            return;
        }

        SetBusy(false);
        _status.Text = T("Archive failed:") + Environment.NewLine + outcome.Message;
    }

    private void SetBusy(bool busy)
    {
        _archive.IsEnabled = !busy;
        _browse.IsEnabled = !busy;
        _format.IsEnabled = !busy;
        _outputPath.IsEnabled = !busy;
        _usePathFilter.IsEnabled = !busy;
        _useRevisionFilter.IsEnabled = !busy;
        _paths.IsEnabled = !busy && _usePathFilter.IsChecked == true;
        _sinceRevision.IsEnabled = !busy && _useRevisionFilter.IsChecked == true;
    }

    private static string[] Lines(string? text)
        => (text ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToArray();

    private static string ShortHash(string hash) => hash.Length > 10 ? hash[..10] : hash;

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
