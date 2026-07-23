using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
