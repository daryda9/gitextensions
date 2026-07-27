using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;

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
    // Layout of the summary line: repository name, separator, detail. A format
    // string rather than a concatenation so a translated detail can never be glued
    // on in the wrong order.
    private const string LineFormat = "{0}  —  {1}";

    private readonly TextBlock _text;
    private int _generation;

    // The repository the line currently describes, so a language switch can
    // recompute it instead of leaving a stale English caption behind.
    private string? _repoPath;

    public StatusBarView()
    {
        Background = Brush("App.Panel", "#252526");

        _text = new TextBlock
        {
            Text = T("No repository open."),
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

        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // A language switch re-computes the line in the new language. The recompute
    // goes through LoadRepository, so the git work stays off the UI thread; with no
    // repository open there is only the placeholder to swap.
    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_repoPath is { Length: > 0 } repo)
        {
            LoadRepository(repo);
        }
        else
        {
            _generation++;
            _text.Text = T("No repository open.");
        }
    });

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
        _repoPath = repoPath;
        int generation = ++_generation;
        string repoName = SafeFolderName(repoPath);
        _text.Text = string.Format(CultureInfo.CurrentCulture, LineFormat, repoName, T("FormBrowse/_loading.Text", "Loading…"));

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
                return string.Format(CultureInfo.CurrentCulture, LineFormat, repoName, T("(detached HEAD)"));
            }

            string upstream = module.GetRemoteBranch(branch);
            if (string.IsNullOrEmpty(upstream))
            {
                return string.Format(CultureInfo.CurrentCulture, LineFormat, repoName, branch);
            }

            // ahead  = commits on HEAD not on upstream
            // behind = commits on upstream not on HEAD
            int? ahead = module.GetCommitCount("HEAD", upstream, throwOnErrorExit: false);
            int? behind = module.GetCommitCount(upstream, "HEAD", throwOnErrorExit: false);

            if (ahead is null && behind is null)
            {
                return string.Format(CultureInfo.CurrentCulture, LineFormat, repoName, $"{branch}  →  {upstream}");
            }

            string track = $"↑{ahead ?? 0} ↓{behind ?? 0}";
            return string.Format(CultureInfo.CurrentCulture, LineFormat, repoName, $"{branch}  →  {upstream}  {track}");
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

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Brush(string key, string fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
