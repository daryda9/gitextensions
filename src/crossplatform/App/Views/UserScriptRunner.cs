using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Runs the user scripts bound to an event, with the UI around them: the confirmation,
///  the process window, and the report when a background script fails. The port of
///  upstream's <c>ScriptsManager.RunEventScripts</c> + <c>ScriptRunner</c>.
///
///  <para><b>A failing <c>Before…</c> script stops the operation</b>, exactly as upstream:
///  <c>RunEventScripts</c> returns false and its caller does not proceed. That is the whole
///  point of a pre-hook — a check that cannot veto is a log line. An <c>After…</c> script
///  is reported and ignored: whatever it guards has already happened.</para>
/// </summary>
public static class UserScriptRunner
{
    /// <summary>
    ///  Runs every enabled script bound to <paramref name="scriptEvent"/>, in file order.
    ///  Returns <see langword="false"/> as soon as one is declined or fails, so a
    ///  <c>Before…</c> caller can abandon its operation.
    /// </summary>
    public static async Task<bool> RunEventAsync(
        Window? owner, UserScriptEvent scriptEvent, UserScriptContext context)
    {
        IReadOnlyList<UserScript> scripts = new UserScriptService().For(scriptEvent);
        foreach (UserScript script in scripts)
        {
            if (!await RunAsync(owner, script, context))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///  Runs one script: asks first if it says so, then either shows it in the process
    ///  window or runs it out of sight. Returns whether the caller may carry on.
    /// </summary>
    public static async Task<bool> RunAsync(Window? owner, UserScript script, UserScriptContext context)
    {
        if (!script.Enabled)
        {
            return true;
        }

        if (script.AskConfirmation && owner is not null
            && !await ConfirmAsync(owner, TranslationService.TFormat(
                null, "Run the script \"{0}\"?", ScriptName(script))))
        {
            // Declining is an answer, not a failure — but it still stops a Before script's
            // operation, because the user has just said they did not want this to happen.
            return false;
        }

        if (!script.RunInBackground && owner is not null)
        {
            GitProcessOutcome outcome = await GitProcessDialog.RunAsync(
                owner,
                ScriptName(script),
                () =>
                {
                    UserScriptResult result = UserScriptService.Run(script, context);
                    return new GitProcessOutcome(result.Success, result.Output);
                });

            return outcome.Success;
        }

        UserScriptResult background = await Task.Run(() => UserScriptService.Run(script, context));
        if (!background.Success && owner is not null)
        {
            // Silence on success, never on failure: a background script that quietly did
            // not run is how a pre-push check stops protecting anything.
            await ReportAsync(owner, script, background.Output);
        }

        return background.Success;
    }

    private static string ScriptName(UserScript script)
        => script.Name is { Length: > 0 } name ? name : script.Command;

    // The failure report for a background script. Its own little window rather than the
    // process dialog: that dialog is built around a live run, and this one is over.
    private static async Task ReportAsync(Window owner, UserScript script, string output)
    {
        Button close = new()
        {
            Content = TranslationService.T("FormSettings/buttonOk.Text", "OK"),
            IsDefault = true,
            IsCancel = true,
            MinWidth = 84,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Theming.ZoomWindow dialog = new()
        {
            Title = TranslationService.TFormat(null, "The script \"{0}\" failed", ScriptName(script)),
            Width = 560,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        close.Click += (_, _) => dialog.Close();

        DockPanel content = new() { Margin = new global::Avalonia.Thickness(14) };
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(close);
        content.Children.Add(new TextBox
        {
            Text = output.Length > 0
                ? output
                : TranslationService.T("(the script printed nothing)"),
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = Theming.AppFonts.Monospace,
            Margin = new global::Avalonia.Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.NoWrap,
        });

        dialog.Content = content;
        await dialog.ShowDialog(owner);
    }

    private static async Task<bool> ConfirmAsync(Window owner, string prompt)
    {
        TaskCompletionSource<bool> answer = new();
        Button yes = new()
        {
            Content = TranslationService.T("Yes"),
            IsDefault = true,
            Margin = new global::Avalonia.Thickness(0, 0, 6, 0),
        };
        Button no = new() { Content = TranslationService.T("No"), IsCancel = true };

        Theming.ZoomWindow dialog = new()
        {
            Title = TranslationService.T("Confirm"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        yes.Click += (_, _) => { answer.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { answer.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => answer.TrySetResult(false);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);

        StackPanel content = new() { Margin = new global::Avalonia.Thickness(16), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return await answer.Task;
    }
}
