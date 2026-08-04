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

        // Make Avalonia's managed file pickers (UseManagedSystemDialogs, M67) paint
        // themselves from the palette above instead of Fluent's raw #000000/#FFFFFF
        // base surfaces. Must run after ThemeManager.Initialize, which is what
        // registers the App.* brushes this reads. See ManagedFileChooserTheming.
        //
        // Installed ONCE, and deliberately not re-run on a style change: it stores the
        // App.* brush INSTANCES, and a style change mutates those instances' Color in
        // place rather than replacing them (ThemeManager.Apply), so the pickers follow
        // the classic/modern switch on their own. Re-installing would only re-register
        // the same objects.
        ManagedFileChooserTheming.Install(this);

        // Typography defaults, control corners, hover/pressed/disabled/focus states
        // and their transitions — all app-wide, none of it editing a view. The TabItem
        // sizing that used to live right here moved into ModernStyles.Build. Must run
        // after ThemeManager.Initialize: every state colour is derived from the App.*
        // brush instances it registers. See Theming/ModernStyles.
        //
        // The style argument is what Settings can flip later: ModernStyles.Apply is
        // reversible, so passing AppStyle.Classic here (or from ThemeManager.Apply)
        // hands the Fluent keys back to their own ControlThemes. The startup value
        // matches ThemeManager's own default.
        Theming.ModernStyles.Apply(this, Theming.ThemeManager.CurrentStyle);

        // The app's chrome font baseline: 12px, upstream's own chrome size, over Fluent's
        // 14 (see Theming/UiScaling). It belongs here rather than in ModernStyles — the
        // baseline is not a modern-versus-classic question, and Classic's reference is
        // upstream's 12 too.
        //
        // Since M86 this is a FIXED write and no longer the UI-size option. The option is
        // now a real zoom of each window's content (a layout transform installed by
        // Theming/ZoomWindow), so text grows as a consequence of everything growing;
        // varying these font resources as well would be a second, competing size knob.
        // MainWindow applies the persisted zoom level from ui-state.json once it is known.
        Theming.UiScaling.InstallChromeBaseline();
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
