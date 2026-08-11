using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The small modal boxes the three GitHub windows share: a message, a question, and
///  the "you have not set a token yet" gate.
///
///  <para>Upstream reaches for <c>MessageBoxes</c>, which is WinForms and therefore
///  unavailable here; every port dialog has so far grown its own copy of the same
///  twenty lines. These three windows share one instead — not a general-purpose
///  message-box service (that would be a refactor of forty call sites, and this change
///  is not it), just the ones this feature needs.</para>
/// </summary>
internal static class GitHubDialogs
{
    /// <summary>An OK box. The text is selectable: it is usually an error worth pasting.</summary>
    public static async Task MessageAsync(Window owner, string title, string message)
    {
        Button ok = new()
        {
            Content = TranslationService.T("TranslatedStrings/_okText.Text", "OK"),
            IsDefault = true,
            IsCancel = true,
            MinWidth = 84,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Theming.ZoomWindow dialog = new()
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", "#1E1E1E"),
        };
        ok.Click += (_, _) => dialog.Close();

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 260,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brush("App.Text", "#DCDCDC"),
        });
        content.Children.Add(ok);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
    }

    /// <summary>
    ///  A Yes/No question. Closing the window counts as "no": the operations behind
    ///  these questions create things on a server, and the safe default is not to.
    /// </summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string prompt)
    {
        TaskCompletionSource<bool> answer = new();

        Button yes = new()
        {
            Content = TranslationService.T("Yes"),
            IsDefault = true,
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Button no = new() { Content = TranslationService.T("No"), IsCancel = true, MinWidth = 84 };

        Theming.ZoomWindow dialog = new()
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("App.Window", "#1E1E1E"),
        };
        yes.Click += (_, _) => { answer.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { answer.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => answer.TrySetResult(false);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { yes, no },
        };

        StackPanel content = new() { Margin = new Thickness(16), Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("App.Text", "#DCDCDC"),
        });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await answer.Task;
    }

    /// <summary>
    ///  Refuses to go on without a token, and offers the one thing that helps: the
    ///  page where GitHub makes one. Returns whether the caller may continue.
    ///
    ///  <para>Upstream opens its plugin settings page instead. The page is reachable
    ///  from here too (Settings ▸ GitHub), but a link straight to the token form is
    ///  what turns "I cannot" into "here is how".</para>
    /// </summary>
    public static async Task<bool> RequireTokenAsync(Window owner, GitHubService service)
    {
        if (service.IsConfigured)
        {
            return true;
        }

        bool open = await ConfirmAsync(
            owner,
            TranslationService.T("GitHub"),
            TranslationService.TFormat(
                null,
                "No personal access token is stored for {0}, so this window has nothing to ask.\n\n"
                    + "Open the page where GitHub creates one? Paste it afterwards into Settings ▸ GitHub.",
                service.Host));

        if (open)
        {
            new ExternalToolService().OpenUrl(service.NewTokenUrl);
        }

        return false;
    }

    /// <summary>
    ///  Reports a failed API call. A <see cref="GitHubApiException"/> already carries a
    ///  sentence written for a human; anything else is shown with its type, because at
    ///  that point the type IS the information.
    /// </summary>
    public static Task ReportAsync(Window owner, string title, Exception exception)
        => MessageAsync(
            owner,
            title,
            exception is GitHubApiException
                ? exception.Message
                : $"{exception.GetType().Name}: {exception.Message}");

    internal static IBrush Brush(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallback));
}
