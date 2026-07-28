using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands.Git;
using GitExtensions.Avalonia.Theming;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Small modal "About" dialog for the Avalonia / Linux port: app title and
///  subtitle, a one-paragraph description, the provenance block upstream's
///  <c>FormAbout</c> / <c>UserEnvironmentInformation</c> shows (product version,
///  <c>Build &lt;sha&gt; (Dirty)</c>, git version, OS, .NET) and the copyright and
///  icon-attribution lines. The Yusuke Kamiyamane (CCA3) credit
///  (<c>FormAbout.Designer.cs:113-125</c>) is a licence obligation of the icon set
///  the port reuses, not decoration — do not drop it.
///  Shown with <see cref="ShowAsync"/> and closed by the default/cancel button.
///  Colors come from the shared dark palette in <c>App.cs</c>
///  (<c>App.Window</c>/<c>App.Text</c>/<c>App.TextDim</c>).
/// </summary>
public sealed class AboutDialog : Window
{
    private readonly TextBlock _gitVersion;

    public AboutDialog()
    {
        IBrush text = Resource("App.Text", "#DCDCDC");
        IBrush dim = Resource("App.TextDim", "#9B9B9B");

        Title = "About Git Extensions";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Resource("App.Window", "#1E1E1E");

        StackPanel header = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Optional app icon; degrade gracefully when it is not linked in.
        Image? icon = IconLoader.Image("GitExtensions", 48);
        if (icon is not null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(icon);
        }

        StackPanel titles = new() { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = "Git Extensions",
            Foreground = text,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
        });
        titles.Children.Add(new TextBlock
        {
            Text = "Avalonia / Linux port",
            Foreground = dim,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 0),
        });
        header.Children.Add(titles);

        TextBlock description = new()
        {
            Text = "A cross-platform graphical user interface for Git, "
                 + "reusing the Git Extensions core logic on top of a native "
                 + "Avalonia UI so it runs on Linux.",
            Foreground = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        };

        // Git's version needs a git process, so it is filled in asynchronously
        // (see LoadGitVersionAsync); the dialog must never block on it.
        _gitVersion = new TextBlock
        {
            Text = "…",
            Foreground = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        StackPanel info = new() { Margin = new Thickness(0, 16, 0, 0), Spacing = 4 };
        info.Children.Add(InfoLine("Version", ProductVersion(), text, dim));
        info.Children.Add(InfoLine("Build", BuildDescription(), text, dim));
        info.Children.Add(InfoLine("Git", _gitVersion, dim));
        info.Children.Add(InfoLine(".NET", RuntimeInformation.FrameworkDescription, text, dim));
        info.Children.Add(InfoLine("OS", RuntimeInformation.OSDescription, text, dim));
        info.Children.Add(InfoLine("UI toolkit", "Avalonia", text, dim));

        StackPanel credits = new() { Margin = new Thickness(0, 16, 0, 0), Spacing = 2 };
        credits.Children.Add(new TextBlock
        {
            Text = "Proudly presented by the Git Extensions team.",
            Foreground = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        credits.Children.Add(new TextBlock
        {
            Text = "Licensed under the GNU General Public License.",
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        // Attribution required by the licence of the reused icon set.
        credits.Children.Add(new TextBlock
        {
            Text = "Some icons by Yusuke Kamiyamane (CCA3) — p.yusukekamiyamane.com",
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        Button close = new()
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            MinWidth = 80,
        };
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(close);

        StackPanel root = new() { Margin = new Thickness(20) };
        root.Children.Add(header);
        root.Children.Add(description);
        root.Children.Add(info);
        root.Children.Add(credits);
        root.Children.Add(buttons);

        Content = root;

        // The Close button's IsCancel was the only Escape path here, and it never
        // fired: this window is text plus one button and nothing ever took focus, so
        // the key was never routed into the window at all. The helper focuses the
        // window when nothing else has, then closes on Escape.
        DialogKeys.InstallEscapeClose(this);

        _ = LoadGitVersionAsync();
    }

    // GitVersion.Current shells out to `git --version`; do it off the UI thread and
    // post the answer back, so a slow or missing git cannot freeze the dialog.
    private async Task LoadGitVersionAsync()
    {
        string version;
        try
        {
            version = await Task.Run(() => GitVersion.Current?.ToString() ?? string.Empty);
        }
        catch (Exception)
        {
            version = string.Empty;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
            _gitVersion.Text = version.Length > 0
                ? version
                : $"not found (minimum: {GitVersion.LastSupportedVersion}, recommended: {GitVersion.LastRecommendedVersion})");
    }

    /// <summary>
    ///  The product version, taken from the assembly's informational version — which
    ///  the project file seeds from <c>packaging/VERSION</c>, the same string the
    ///  .deb carries.
    /// </summary>
    private static string ProductVersion()
        => typeof(AboutDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(AboutDialog).Assembly.GetName().Version?.ToString()
           ?? "unknown";

    /// <summary>
    ///  <c>&lt;sha&gt;</c> or <c>&lt;sha&gt; (Dirty)</c>, mirroring upstream's
    ///  <c>UserEnvironmentInformation</c>. Reads the metadata the
    ///  <c>StampBuildProvenance</c> target writes at build time; says so when the
    ///  binary was built outside a git checkout.
    /// </summary>
    private static string BuildDescription()
    {
        string? sha = Metadata("BuildGitSha");
        if (string.IsNullOrEmpty(sha))
        {
            return "unknown (built outside a git checkout)";
        }

        return string.Equals(Metadata("BuildGitIsDirty"), "true", StringComparison.OrdinalIgnoreCase)
            ? sha + " (Dirty)"
            : sha;
    }

    private static string? Metadata(string key)
        => typeof(AboutDialog).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>Shows the About dialog modally over <paramref name="owner"/>.</summary>
    public static Task ShowAsync(Window owner)
        => new AboutDialog().ShowDialog(owner);

    private static Control InfoLine(string label, string value, IBrush text, IBrush dim)
        => InfoLine(
            label,
            new TextBlock
            {
                Text = value,
                Foreground = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
            dim);

    private static Control InfoLine(string label, Control value, IBrush dim)
    {
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new TextBlock
        {
            Text = label + ":",
            Foreground = dim,
            FontSize = 12,
            Width = 80,
        });
        line.Children.Add(value);
        return line;
    }

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
