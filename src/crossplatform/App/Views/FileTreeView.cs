using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Lists every tracked file in a commit's tree (<c>git ls-tree -r --name-only
///  &lt;hash&gt;</c>), one path per row, in a scrollable monospace list. Mirrors the
///  original browse window's "File tree" tab. All git work runs off the UI thread
///  and never throws — failures surface as a status line.
/// </summary>
public sealed class FileTreeView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private readonly ListBox _list;
    private readonly TextBlock _status;

    public FileTreeView()
    {
        _status = new TextBlock
        {
            Margin = new Thickness(8, 6, 8, 4),
            Foreground = Brush("App.TextDim", Brushes.Gray),
            Background = Brush("App.Toolbar", Brushes.DimGray),
            Padding = new Thickness(4, 4, 4, 4),
            Text = "No commit selected.",
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
    }

    /// <summary>
    ///  Loads and lists the files of the tree at <paramref name="commitHash"/> in
    ///  the repository at <paramref name="repoPath"/>. Heavy git work runs off the
    ///  UI thread; results are marshalled back to the UI thread.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _list.ItemsSource = null;
        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;
        _status.Text = $"Loading files at {shortHash}…";

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
                    _status.Text = $"Could not list files at {shortHash}: {error}";
                    return;
                }

                _list.ItemsSource = files;
                _status.Text = files.Count > 0
                    ? $"{files.Count} file(s) at {shortHash}"
                    : $"(no tracked files at {shortHash})";
            });
        });
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
