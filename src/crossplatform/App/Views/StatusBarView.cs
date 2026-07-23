using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The bottom status strip for the shell, echoing the original
///  <c>FormBrowse</c> status bar: a compact, dim-text line showing the open
///  repository's folder name, the current branch, and — when a tracking branch
///  exists — how far the branch is ahead/behind its upstream.
///
///  All git work runs off the UI thread; results are marshalled back with
///  <see cref="Dispatcher.UIThread"/>. <see cref="SetText"/> is a fallback for
///  showing an arbitrary transient message.
/// </summary>
public sealed class StatusBarView : UserControl
{
    private readonly TextBlock _text;
    private int _generation;

    public StatusBarView()
    {
        Background = Brush("App.Panel", "#252526");

        _text = new TextBlock
        {
            Text = "No repository open.",
            Foreground = Brush("App.TextDim", "#9B9B9B"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Content = new Border
        {
            Padding = new Thickness(10, 3),
            Child = _text,
        };
    }

    /// <summary>
    ///  Sets an arbitrary status message (UI thread).
    /// </summary>
    public void SetText(string text)
    {
        // A manual message supersedes any in-flight repository computation.
        _generation++;
        _text.Text = text;
    }

    /// <summary>
    ///  Computes and shows the repository summary (folder name, current branch,
    ///  ahead/behind vs upstream). Git work runs off the UI thread.
    /// </summary>
    public void LoadRepository(string repoPath)
    {
        int generation = ++_generation;
        string repoName = SafeFolderName(repoPath);
        _text.Text = $"{repoName}  —  loading…";

        _ = Task.Run(() => Compute(repoPath)).ContinueWith(t =>
        {
            string message = t.Status == TaskStatus.RanToCompletion
                ? t.Result
                : repoName;
            Dispatcher.UIThread.Post(() =>
            {
                // Ignore stale results if another load / SetText happened meanwhile.
                if (generation == _generation)
                {
                    _text.Text = message;
                }
            });
        }, TaskScheduler.Default);
    }

    // Runs on a background thread.
    private static string Compute(string repoPath)
    {
        string repoName = SafeFolderName(repoPath);
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string branch = module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;

            if (string.IsNullOrEmpty(branch))
            {
                return $"{repoName}  —  (detached HEAD)";
            }

            string upstream = module.GetRemoteBranch(branch);
            if (string.IsNullOrEmpty(upstream))
            {
                return $"{repoName}  —  {branch}";
            }

            // ahead  = commits on HEAD not on upstream
            // behind = commits on upstream not on HEAD
            int? ahead = module.GetCommitCount("HEAD", upstream, throwOnErrorExit: false);
            int? behind = module.GetCommitCount(upstream, "HEAD", throwOnErrorExit: false);

            if (ahead is null && behind is null)
            {
                return $"{repoName}  —  {branch}  →  {upstream}";
            }

            string track = $"↑{ahead ?? 0} ↓{behind ?? 0}";
            return $"{repoName}  —  {branch}  →  {upstream}  {track}";
        }
        catch
        {
            return $"{repoName}";
        }
    }

    private static string SafeFolderName(string repoPath)
    {
        try
        {
            string trimmed = repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? repoPath : name;
        }
        catch
        {
            return repoPath;
        }
    }

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
