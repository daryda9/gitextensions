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
        RequestedThemeVariant = ThemeVariant.Dark;

        Resources["App.Window"] = new SolidColorBrush(Color.Parse("#1E1E1E"));
        Resources["App.Panel"] = new SolidColorBrush(Color.Parse("#252526"));
        Resources["App.PanelAlt"] = new SolidColorBrush(Color.Parse("#2D2D30"));
        Resources["App.Toolbar"] = new SolidColorBrush(Color.Parse("#333337"));
        Resources["App.Border"] = new SolidColorBrush(Color.Parse("#3F3F46"));
        Resources["App.Text"] = new SolidColorBrush(Color.Parse("#DCDCDC"));
        Resources["App.TextDim"] = new SolidColorBrush(Color.Parse("#9B9B9B"));
        Resources["App.Accent"] = new SolidColorBrush(Color.Parse("#007ACC"));
        Resources["App.Selection"] = new SolidColorBrush(Color.Parse("#094771"));
        Resources["App.GraphGreen"] = new SolidColorBrush(Color.Parse("#4EC9B0"));

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
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
