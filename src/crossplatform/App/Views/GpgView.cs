using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Shows a commit's GPG signature / verification, mirroring the original browse
///  window's "GPG" tab. Runs <c>git log -1 --show-signature --pretty=medium
///  &lt;hash&gt;</c> (with a <c>git verify-commit</c> fallback) and renders the
///  combined stdout/stderr verbatim in a monospace, read-only pane. All git work
///  runs off the UI thread and never throws.
/// </summary>
public sealed class GpgView : UserControl
{
    private readonly TextBox _text;
    private readonly ScrollViewer _scroll;

    public GpgView()
    {
        _text = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace,Consolas,Menlo"),
            Background = Brush("App.Panel", Brushes.Black),
            Foreground = Brush("App.Text", Brushes.Gainsboro),
            Text = "No commit selected.",
        };

        _scroll = new ScrollViewer
        {
            Content = _text,
            Background = Brush("App.Panel", Brushes.Black),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Content = _scroll;
        Background = Brush("App.Window", Brushes.DimGray);
        ClipToBounds = true;
    }

    /// <summary>
    ///  Loads and shows the signature / verification output for
    ///  <paramref name="commitHash"/> in the repository at
    ///  <paramref name="repoPath"/>. Heavy git work runs off the UI thread.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;
        _text.Text = $"Verifying signature of {shortHash}…";

        _ = Task.Run(() =>
        {
            string output;
            try
            {
                GitModule module = GitContext.CreateModule(repoPath);

                GitArgumentBuilder args = new("log")
                {
                    "-1", "--show-signature", "--pretty=medium", commitHash,
                };
                ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
                output = Combine(result.StandardOutput, result.StandardError);

                // If the log output carried no signature information at all, fall
                // back to an explicit verify-commit, which always reports status.
                if (!output.Contains("gpg", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("signature", StringComparison.OrdinalIgnoreCase))
                {
                    GitArgumentBuilder verify = new("verify-commit") { "-v", commitHash };
                    ExecutionResult vr = module.GitExecutable.Execute(verify, throwOnErrorExit: false);
                    string verifyOut = Combine(vr.StandardOutput, vr.StandardError);
                    output = verifyOut.Trim().Length > 0
                        ? verifyOut
                        : (output.Trim().Length > 0 ? output : "(this commit is not signed)");
                }
            }
            catch (Exception ex)
            {
                output = $"Could not verify signature: {ex.Message}";
            }

            string final = output.Trim().Length > 0 ? output : "(no signature information)";
            Dispatcher.UIThread.Post(() =>
            {
                _text.Text = final;
                _scroll.ScrollToHome();
            });
        });
    }

    private static string Combine(string? stdout, string? stderr)
    {
        string a = stdout ?? string.Empty;
        string b = stderr ?? string.Empty;
        if (a.Trim().Length == 0)
        {
            return b;
        }

        return b.Trim().Length == 0 ? a : a.TrimEnd() + Environment.NewLine + b;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.TryFindResource(key, out object? value) == true && value is IBrush b
            ? b
            : fallback;
}
