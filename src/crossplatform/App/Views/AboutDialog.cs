using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands.Git;
using GitExtensions.Avalonia.Services;
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
public sealed class AboutDialog : Theming.ZoomWindow
{
    private readonly TextBlock _gitVersion;

    // Everything ApplyTranslations has to re-state. The info block is a run of
    // "<label>: <value>" lines built in a row, so its labels are collected into one
    // list instead of a field each: the list carries the XLIFF id next to the widget,
    // which is what a per-field version would have had to duplicate anyway.
    private readonly List<(TextBlock Block, string? Key, string English)> _infoLabels = [];
    private readonly TextBlock _subtitle;
    private readonly TextBlock _description;
    private readonly TextBlock _versionValue;
    private readonly TextBlock _buildValue;
    private readonly TextBlock _licence;
    private readonly TextBlock _warranty;
    private readonly TextBlock _iconCredit;
    private readonly Button _close;

    // git's own version string, kept raw: the "not found" sentence around it is
    // display text and has to be re-composed when the language changes, which is
    // impossible once the two have been concatenated into the TextBlock.
    private string _gitVersionText = string.Empty;

    public AboutDialog()
    {
        IBrush text = Resource("App.Text", "#DCDCDC");
        IBrush dim = Resource("App.TextDim", "#9B9B9B");

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

        // The product name is never translated — upstream marks its own copy of it
        // _NO_TRANSLATE_ (FormAbout.Designer.cs) because a brand read in a foreign
        // language stops identifying the program.
        titles.Children.Add(new TextBlock
        {
            Text = "Git Extensions",
            Foreground = text,
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
        });
        _subtitle = new TextBlock
        {
            Foreground = dim,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 0),
        };
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);

        _description = new TextBlock
        {
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

        // Values first: the two computed ones are re-stated by ApplyTranslations
        // because their "unknown" fallbacks are display text, while .NET / OS /
        // Avalonia are runtime facts and proper names that stay as they are.
        _versionValue = ValueBlock(text);
        _buildValue = ValueBlock(text);

        StackPanel info = new() { Margin = new Thickness(0, 16, 0, 0), Spacing = 4 };
        info.Children.Add(InfoLine(null, "Version", _versionValue, dim));
        info.Children.Add(InfoLine(null, "Build", _buildValue, dim));

        // "Git", ".NET" and "OS" are the names of the things themselves, not words;
        // they are registered so the colon is drawn by the same code path, and their
        // English literal is simply what every language shows.
        info.Children.Add(InfoLine(null, "Git", _gitVersion, dim));
        info.Children.Add(InfoLine(null, ".NET", ValueBlock(text, RuntimeInformation.FrameworkDescription), dim));
        info.Children.Add(InfoLine(null, "OS", ValueBlock(text, RuntimeInformation.OSDescription), dim));
        info.Children.Add(InfoLine(null, "UI toolkit", ValueBlock(text, "Avalonia"), dim));

        StackPanel credits = new() { Margin = new Thickness(0, 16, 0, 0), Spacing = 2 };

        // Upstream's labelCopyright is _NO_TRANSLATE_ (FormAbout.Designer.cs:136) —
        // the sentence is the team's own signature, so it stays English here too.
        credits.Children.Add(new TextBlock
        {
            Text = "Proudly presented by the Git Extensions team.",
            Foreground = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        _licence = new TextBlock
        {
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        credits.Children.Add(_licence);

        // The real copyright, read from the assembly rather than written out here:
        // CommonAssemblyInfo.cs carries [assembly: AssemblyCopyright(…)] and
        // Directory.Build.props:66 compiles it into every project, this one included.
        // Upstream's FormAbout shows only "Proudly presented by…" and never surfaces
        // the attribute, so the line is skipped rather than invented if it is absent.
        string? copyright = typeof(AboutDialog).Assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        if (!string.IsNullOrWhiteSpace(copyright))
        {
            credits.Children.Add(new TextBlock
            {
                Text = copyright,
                Foreground = dim,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Upstream's label1 (FormAbout.Designer.cs), verbatim — the GPL's
        // no-warranty notice, which the port was dropping. "of FITNESS" is
        // upstream's own typo for "or FITNESS"; corrected here since this is
        // display text, not a string being matched.
        _warranty = new TextBlock
        {
            Foreground = dim,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        credits.Children.Add(_warranty);

        // Attribution required by the licence of the reused icon set. The URL is
        // upstream's verbatim string (FormAbout.cs:27, README.md:124): written without
        // its scheme it reads as a mangled address ("p.yusukekamiyamane.com" looks
        // truncated) and cannot be copy-pasted into a browser as-is.
        _iconCredit = new TextBlock
        {
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        credits.Children.Add(_iconCredit);

        // On its own line and NoWrap: on one line with the credit it wrapped straight
        // after "http://", which is exactly the mangled look this was fixing.
        credits.Children.Add(new TextBlock
        {
            Text = "http://p.yusukekamiyamane.com/",
            Foreground = dim,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
        });

        _close = new Button
        {
            IsDefault = true,
            IsCancel = true,
            MinWidth = 80,
        };
        _close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(_close);

        StackPanel root = new() { Margin = new Thickness(20) };
        root.Children.Add(header);
        root.Children.Add(_description);
        root.Children.Add(info);
        root.Children.Add(credits);
        root.Children.Add(buttons);

        Content = root;

        // The Close button's IsCancel was the only Escape path here, and it never
        // fired: this window is text plus one button and nothing ever took focus, so
        // the key was never routed into the window at all. The helper focuses the
        // window when nothing else has, then closes on Escape.
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        _ = LoadGitVersionAsync();
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        Title = T("About Git Extensions");
        _subtitle.Text = T("Avalonia / Linux port");
        _description.Text = T(
            "A cross-platform graphical user interface for Git, "
            + "reusing the Git Extensions core logic on top of a native "
            + "Avalonia UI so it runs on Linux.");

        foreach ((TextBlock block, string? key, string english) in _infoLabels)
        {
            // The colon is punctuation of the layout, not of the caption, so it is
            // appended here rather than baked into every translatable literal.
            block.Text = T(key, english) + ":";
        }

        _versionValue.Text = ProductVersion();
        _buildValue.Text = BuildDescription();
        ShowGitVersion();

        _licence.Text = T("Licensed under the GNU General Public License.");

        // Upstream's own literal reads "MERCHANTABILITY of FITNESS" — a typo the port
        // corrects on screen. The id is given explicitly so a translated catalogue is
        // still found despite the two English texts differing by that one word.
        _warranty.Text = T(
            "FormAbout/label1.Text",
            "This program is distributed in the hope that it will be useful, "
            + "but WITHOUT ANY WARRANTY; without even the implied warranty of "
            + "MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.");

        _iconCredit.Text = T("FormAbout/linkLabelIcons.Text", "Some icons by Yusuke Kamiyamane (CCA3)") + ":";
        _close.Content = T("TranslatedStrings/_closeText.Text", "Close");
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
        {
            _gitVersionText = version;
            ShowGitVersion();
        });
    }

    // git's version is data and shown verbatim; only the sentence that stands in for
    // a missing git is display text, so it is composed here — through TFormat, so a
    // translation can move the two version numbers around.
    private void ShowGitVersion()
        => _gitVersion.Text = _gitVersionText.Length > 0
            ? _gitVersionText
            : TranslationService.TFormat(
                key: null,
                "not found (minimum: {0}, recommended: {1})",
                GitVersion.LastSupportedVersion,
                GitVersion.LastRecommendedVersion);

    /// <summary>
    ///  The product version, taken from the assembly's informational version — which
    ///  the project file seeds from <c>packaging/VERSION</c>, the same string the
    ///  .deb carries.
    /// </summary>
    private static string ProductVersion()
        => typeof(AboutDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(AboutDialog).Assembly.GetName().Version?.ToString()
           ?? T("unknown");

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
            return T("unknown (built outside a git checkout)");
        }

        // "(Dirty)" is upstream's own marker in UserEnvironmentInformation and is
        // reported in bug threads verbatim; keeping it English keeps those reports
        // comparable across languages.
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

    private static TextBlock ValueBlock(IBrush text, string? value = null) => new()
    {
        Text = value,
        Foreground = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    // Instance-level, unlike the previous static helper: the caption it builds has to
    // be registered for re-labelling, and a static method has nowhere to register it.
    private Control InfoLine(string? key, string english, Control value, IBrush dim)
    {
        TextBlock caption = new()
        {
            Foreground = dim,
            FontSize = 12,
            Width = 80,
        };
        _infoLabels.Add((caption, key, english));

        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(caption);
        line.Children.Add(value);
        return line;
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static IBrush Resource(string key, string fallback)
        => Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallback));
}
