using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Dialogs;
using Avalonia.Media;
using Avalonia.Styling;

namespace GitExtensions.Avalonia;

/// <summary>
///  Makes Avalonia's <em>managed</em> file picker — the one M67 switched to with
///  <c>UseManagedSystemDialogs()</c>, because the X11 <c>StorageProvider</c> never
///  reached the XDG portal — paint itself from the app palette instead of Fluent's
///  raw base surfaces (a pure-black slab in the dark theme).
///
///  <para><b>Why it is overridable at all.</b>
///  <c>Avalonia.Dialogs.ManagedFileChooser</c> is a <see cref="TemplatedControl"/>
///  and Avalonia.Dialogs.dll ships <em>no</em> styles for it (its only embedded
///  resources are a font and <c>AboutAvaloniaDialog.xaml</c>). Its
///  <c>ControlTheme</c> lives in <b>Avalonia.Themes.Fluent</b>, keyed by
///  <c>typeof(ManagedFileChooser)</c>, and every surface it paints goes through a
///  <c>DynamicResource</c> — so redefining those keys in
///  <see cref="Application.Resources"/> wins, because
///  <c>Application.TryGetResource</c> searches <c>Resources</c> <em>before</em>
///  <c>Styles</c> (where the <see cref="Avalonia.Themes.Fluent.FluentTheme"/> sits).
///  The whole theme uses exactly six brush keys; the list below is the complete
///  set, read off the compiled XAML of Avalonia.Themes.Fluent 11.3.9
///  (<c>CompiledAvaloniaXaml.!AvaloniaResources</c>, the <c>ControlTheme</c> whose
///  <c>TargetType</c> is <c>ManagedFileChooser</c>).</para>
///
///  <para><b>Why <see cref="Application.Resources"/> and not the picker's window.</b>
///  Scoping would have been tidier — <c>ManagedFileDialogOptions.ContentRootFactory</c>
///  lets you supply your own host <see cref="Window"/>, whose <c>Resources</c> would
///  shadow Fluent for that subtree only. It is not reachable: the options are read
///  from <c>AvaloniaLocator</c>, and in 11.3.9 <c>AvaloniaLocator.CurrentMutable</c>
///  and <c>Bind&lt;T&gt;</c> are <b>internal in the reference assembly</b> (public
///  only in the implementation one), so binding them needs either reflection over a
///  private API or an <c>AppBuilder.With&lt;ManagedFileDialogOptions&gt;(…)</c> line
///  in <c>Program.cs</c>. Going global is safe here because the blast radius is
///  three setters, all of which <em>want</em> the palette value — see each key.</para>
///
///  <para><b>What cannot be themed, and why (a real dead end, not an omission).</b>
///  The folder / file / volume glyphs are amber-gradient <c>DrawingGroup</c>s
///  hard-coded in the Fluent <c>ControlTheme</c>'s own <c>Resources</c>, under the
///  key <c>Icons</c> — a <c>ResourceSelectorConverter</c>, which is itself a
///  <c>ResourceDictionary</c> — and the template reaches them with
///  <c>StaticResource</c>. <c>StaticResource</c> resolves against the parent stack
///  at build time and the <c>ControlTheme</c>'s own dictionary is the first entry of
///  that stack, so no outer dictionary can win. Recolouring them would mean
///  shipping a full replacement <c>ControlTheme</c> for the chooser (≈700 lines of
///  template). They are non-text content, so the 4.5:1 text rule does not cover
///  them; they stay amber in both themes.</para>
///
///  <para>All values are the <see cref="Theming.ThemeManager"/> brush
///  <b>instances</b>, taken by reference, so the hot theme switch — which mutates
///  each brush's colour in place — repaints an already-open picker too.</para>
/// </summary>
internal static class ManagedFileChooserTheming
{
    /// <summary>
    ///  Redefines the Fluent keys the managed chooser resolves, and gives its file
    ///  list the app's content surface. Call from <see cref="Application.Initialize"/>,
    ///  after <see cref="Theming.ThemeManager.Initialize"/> has registered the palette.
    /// </summary>
    public static void Install(Application app)
    {
        // The chooser's own Background — i.e. the entire dialog surface, since its
        // template root Border template-binds to it and PART_Files is Transparent.
        // Fluent gives this #000000 in dark and #FFFFFF in light.
        //
        // The other two consumers of this key in the whole Fluent theme are the
        // ControlThemes of Window and EmbeddableControlRoot, both for their default
        // Background: "the window surface" is exactly what App.Window means, so the
        // spill is the intended value, not a regression. (Views that set Background
        // themselves are unaffected — a local value beats a ControlTheme setter.)
        Map(app, "SystemRegionBrush", "App.Window");

        // Quick-links sidebar. Chooser-only. Note this is a no-op upstream: the
        // setter's selector is ListBox#QuickLinks while the element is actually
        // named PART_QuickLinks, so Fluent never applies it either and the sidebar
        // keeps taking its background from the plain ListBox theme. Mapped anyway,
        // so the day Avalonia fixes the name the sidebar lands on the palette.
        Map(app, "SystemControlBackgroundChromeMediumBrush", "App.PanelAlt");

        // Nav bar (up / location / refresh), the bottom bar that carries
        // "Show hidden files" + OK + Cancel, and the GridSplitter. Chooser-only.
        Map(app, "SystemControlHighlightAltBaseMediumLowBrush", "App.Toolbar");

        // Quick-link entry, :pointerover and :selected. Chooser-only.
        Map(app, "SystemControlBackgroundAltMediumBrush", "App.PanelAlt");
        Map(app, "SystemControlBackgroundAltMediumHighBrush", "App.Selection");

        // The selected-row marker stripe. Also read by the ProgressBar theme, for
        // its indicator — which wants the accent too.
        Map(app, "SystemControlHighlightAccentBrush", "App.Accent");

        // The file list itself. Fluent leaves it Transparent over the chooser
        // background; giving it App.Panel makes it read as a content pane, the way
        // every other list in the app does, and separates it from the two bars.
        Style files = new(x => x.OfType<ManagedFileChooser>()
                                .Template()
                                .OfType<ListBox>()
                                .Name("PART_Files"));
        if (B("App.Panel") is { } panel)
        {
            files.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, panel));
            app.Styles.Add(files);
        }
    }

    /// <summary>Points a Fluent resource key at an App.* palette brush, by reference.</summary>
    private static void Map(Application app, string fluentKey, string appKey)
    {
        if (B(appKey) is { } brush)
        {
            app.Resources[fluentKey] = brush;
        }
    }

    /// <summary>
    ///  The palette brush for <paramref name="key"/>, or null when the key is not
    ///  registered. Deliberately no hard-coded fallback: per the M62 trap, a
    ///  fallback silently pins a colour that stops following the theme — better to
    ///  leave Fluent's own value in place than to freeze a wrong one.
    /// </summary>
    private static IBrush? B(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out object? v) == true
            ? v as IBrush
            : null;
}
