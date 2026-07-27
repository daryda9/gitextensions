using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace GitExtensions.Avalonia;

public class App : Application
{
    /// <summary>Repository path to open on startup; set from the command line.</summary>
    public static string? InitialRepoPath { get; set; }

    // GitExtensions-like dark palette, exposed as app resources so every view
    // pulls the same colors (see Theming/AppColors for the keys).
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());

        // Palette brushes + dark/light theme switching (see Theming/ThemeManager).
        Theming.ThemeManager.Initialize(this);

        // Normalize control sizing: Fluent's default tab headers are oversized
        // for a dense tool; keep them compact and consistent with the rest.
        Style tabItem = new(x => x.OfType<TabItem>());
        tabItem.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, 13.0));
        tabItem.Setters.Add(new Setter(TemplatedControl.FontWeightProperty, FontWeight.Normal));
        tabItem.Setters.Add(new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 6)));
        tabItem.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0.0));
        Styles.Add(tabItem);

        // A sane default text size app-wide (Fluent defaults to 14, which reads
        // large next to the 12px grid/diff); views can still override.
        Style text = new(x => x.OfType<TextBlock>());
        text.Setters.Add(new Setter(TextBlock.FontSizeProperty, 13.0));
        Styles.Add(text);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Join the catalogue pre-load started in Program.Main before building
            // any control, so a non-English shell is born translated rather than
            // flashing English first. The parse has been running in parallel with
            // Avalonia's start-up, so by now it is normally already done; the
            // timeout is only there so a pathological catalogue can never hang the
            // app — it would just start in English. English never gets here at all
            // (no pre-load task → returns immediately).
            Services.TranslationService.WaitForPreload(TimeSpan.FromSeconds(5));

            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
