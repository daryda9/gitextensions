using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitExtensions.Avalonia.Services;
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
///
///  <para>Only this view's own wording is translated (through
///  <see cref="TranslationService"/>, with <c>RevisionGpgInfoControl</c> ids
///  where upstream has an equivalent): git's own signature output is left
///  verbatim, because it is what the user would see on the command line. The
///  placeholder is re-stated on
///  <see cref="TranslationService.LanguageChanged"/>; a signature already
///  displayed is not re-verified.</para>
/// </summary>
public sealed class GpgView : UserControl
{
    private readonly TextBox _text;
    private readonly ScrollViewer _scroll;

    // False while the pane shows its placeholder rather than git output.
    private bool _hasCommit;

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

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    private void ApplyTranslations()
    {
        if (!_hasCommit)
        {
            _text.Text = T("No commit selected.");
        }
    }

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    /// <summary>
    ///  Loads and shows the signature / verification output for
    ///  <paramref name="commitHash"/> in the repository at
    ///  <paramref name="repoPath"/>. Heavy git work runs off the UI thread.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        string shortHash = commitHash.Length > 8 ? commitHash[..8] : commitHash;
        _hasCommit = true;
        _text.Text = F(T("Verifying signature of {0}…"), shortHash);

        // Snapshotted on the UI thread: the strings are needed inside the git
        // run below, and Task.Run must not reach back into the view.
        string notSigned = F("({0})", T("RevisionGpgInfoControl/_commitNotSigned.Text", "this commit is not signed"));
        string noInfo = T("(no signature information)");
        string failedFormat = T("Could not verify signature: {0}");

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
                        : (output.Trim().Length > 0 ? output : notSigned);
                }
            }
            catch (Exception ex)
            {
                output = F(failedFormat, ex.Message);
            }

            string final = output.Trim().Length > 0 ? output : noInfo;
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
