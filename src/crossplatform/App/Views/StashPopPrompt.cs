using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>Which flow is asking whether to re-apply the stash it made.</summary>
public enum StashPopScope
{
    /// <summary>A checkout that stashed the local changes first.</summary>
    Checkout,

    /// <summary>The Pull dialog's "Stash changes" button.</summary>
    Pull,
}

/// <summary>
///  "Apply the stashed items again?" — upstream's question after an operation that
///  stashed on the user's behalf (<c>FormPull.PopStash</c>,
///  <c>FormCheckoutBranch.PopStash</c>), together with the setting that remembers the
///  answer.
///
///  <para>Upstream stores the memory as a NULLABLE bool: null asks, true/false is the
///  remembered answer, and the "don't show again" box on the question is what turns
///  the null into one of them. The port spells the same three states as "Ask" /
///  "Always" / "Never", because they also have to be offerable in a Settings drop-down,
///  where a tri-state checkbox would say nothing about which way it leans.</para>
/// </summary>
public static class StashPopPrompt
{
    /// <summary>
    ///  Whether the stash should be popped now. Asks only in "Ask" mode, and writes the
    ///  remembered answer when the user ticks the box. Never throws: a missing owner
    ///  window answers "no", which leaves the stash on the stack — recoverable, unlike
    ///  a pop the user did not want.
    /// </summary>
    public static async Task<bool> ShouldPopAsync(Window? owner, StashPopScope scope)
    {
        SettingsService service = new();
        AppPreferences prefs = service.Load();
        string answer = scope == StashPopScope.Pull
            ? prefs.AutoPopStashAfterPull
            : prefs.AutoPopStashAfterCheckout;

        if (string.Equals(answer, "Always", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(answer, "Never", StringComparison.Ordinal) || owner is null)
        {
            return false;
        }

        TaskCompletionSource<bool> tcs = new();
        CheckBox remember = new()
        {
            Content = TranslationService.T("TranslatedStrings/_dontShowAgain.Text", "Don't show again"),
        };

        Button yes = new()
        {
            Content = TranslationService.T("Yes"),
            Margin = new global::Avalonia.Thickness(0, 0, 6, 0),
            IsDefault = true,
        };
        Button no = new() { Content = TranslationService.T("No"), IsCancel = true };

        Theming.ZoomWindow dialog = new()
        {
            Title = TranslationService.T("FormPull/_applyStashedItemsAgainCaption.Text", "Auto stash"),
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        void Answer(bool pop)
        {
            if (remember.IsChecked == true)
            {
                AppPreferences current = service.Load();
                if (scope == StashPopScope.Pull)
                {
                    current.AutoPopStashAfterPull = pop ? "Always" : "Never";
                }
                else
                {
                    current.AutoPopStashAfterCheckout = pop ? "Always" : "Never";
                }

                service.Save(current);
            }

            tcs.TrySetResult(pop);
            dialog.Close();
        }

        yes.Click += (_, _) => Answer(true);
        no.Click += (_, _) => Answer(false);

        // Closing with the window button is "no", and deliberately does NOT record an
        // answer: dismissing a question is not answering it.
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        StackPanel content = new() { Margin = new global::Avalonia.Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = TranslationService.T(
                "FormPull/_applyStashedItemsAgain.Text",
                "Apply stashed items to working directory again?"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(remember);
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
