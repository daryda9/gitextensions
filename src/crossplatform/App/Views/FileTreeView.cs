using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Lists every tracked file in a commit's tree (<c>git ls-tree -r --name-only
///  &lt;hash&gt;</c>), one path per row, in a scrollable monospace list. Mirrors the
///  original browse window's "File tree" tab. All git work runs off the UI thread
///  and never throws — failures surface as a status line.
///
///  <para>The header line goes through <see cref="TranslationService"/>. The tab
///  itself is named by <c>FormBrowse/TreeTabPage.Text</c> upstream, but the
///  status sentences this view composes ("N file(s) at abc1234") have no upstream
///  equivalent, so they use the source-text overload and stay English where a
///  catalogue has no match. Each is a single format with placeholders — the
///  hash and the count are substituted, never concatenated. The header is
///  re-stated on <see cref="TranslationService.LanguageChanged"/>.</para>
/// </summary>
public sealed class FileTreeView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private readonly ListBox _list;
    private readonly TextBlock _status;

    // The short hash currently listed, or null while nothing is loaded: it lets
    // a language switch re-state the header without re-running git.
    private string? _shortHash;
    private int _fileCount;

    public FileTreeView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            Background = Brush("App.Toolbar", Brushes.DimGray),
            Padding = new Thickness(4, 4, 4, 4),
        };

        _list = new ListBox
        {
            FontFamily = Monospace,
            FontSize = 12,
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            BorderThickness = new Thickness(0),
        };

        ScrollViewer scroll = new()
        {
            Content = _list,
            Background = Brush("App.Panel", Brushes.Black),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        DockPanel root = new() { Background = Brush("App.Window", Brushes.DimGray) };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        root.Children.Add(scroll);

        Content = root;
        ClipToBounds = true;

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    // Re-states the header in the active language. An error message is left
    // alone: it belongs to a git run that already happened.
    private void ApplyTranslations()
    {
        if (_shortHash is not { Length: > 0 } hash)
        {
            _status.Text = T("No commit selected.");
            return;
        }

        _status.Text = _fileCount > 0
            ? F(T("{0} file(s) at {1}"), _fileCount, hash)
            : F(T("(no tracked files at {0})"), hash);
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    /// <summary>
    ///  Loads and lists the files of the tree at <paramref name="commitHash"/> in
    ///  the repository at <paramref name="repoPath"/>. Heavy git work runs off the
    ///  UI thread; results are marshalled back to the UI thread.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _list.ItemsSource = null;
        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;
        _shortHash = null;
        _fileCount = 0;
        _status.Text = F(T("Loading files at {0}…"), shortHash);

        _ = Task.Run(() =>
        {
            List<string> files = new();
            string? error = null;
            try
            {
                GitModule module = GitContext.CreateModule(repoPath);
                GitArgumentBuilder args = new("ls-tree") { "-r", "--name-only", commitHash };
                ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
                if (result.ExitedSuccessfully)
                {
                    foreach (string rawLine in result.StandardOutput.Split('\n'))
                    {
                        string line = rawLine.Trim();
                        if (line.Length > 0)
                        {
                            files.Add(line);
                        }
                    }
                }
                else
                {
                    error = result.StandardError?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (error is { Length: > 0 })
                {
                    _status.Text = F(T("Could not list files at {0}: {1}"), shortHash, error);
                    return;
                }

                _list.ItemsSource = files;
                _shortHash = shortHash;
                _fileCount = files.Count;
                ApplyTranslations();
            });
        });
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
