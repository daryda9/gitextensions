namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  How large the whole interface is drawn, chosen from Settings → Appearance.
///
///  <para><b>This is a real zoom, not a font size (M86).</b> The chosen level becomes a
///  scale factor applied to every window's content by <see cref="UiScaling"/>, so chrome,
///  spacing, control heights, icons, the revision grid, the diff and the file lists all
///  grow together, and text grows as a consequence rather than on its own. It replaced
///  M84's font-size-only mechanism, which the user rejected precisely because it moved the
///  text and left the UI where it was.</para>
///
///  <para><b>Two levels, because that is what was asked for.</b> The four steps of
///  M81/M84 (Small/Normal/Large/VeryLarge) existed to nudge one font size up or down. A
///  zoom is a coarser thing: the useful questions are "match the tool I already use" and
///  "make it bigger on a high-DPI screen", and those are two answers, not four. Values
///  persisted by the four-step versions are migrated by
///  <see cref="UiSizes.Parse(string?)"/>.</para>
///
///  <para>Orthogonal to both <see cref="AppStyle"/> and
///  <see cref="Avalonia.Styling.ThemeVariant"/>: a scale factor is not a palette, so both
///  levels render in Classic and Modern, Light and Dark alike.</para>
/// </summary>
public enum UiSize
{
    /// <summary>
    ///  1.0 — no transform at all, which is upstream Git Extensions' own scale.
    ///  See <see cref="UiSizes.Scale(UiSize)"/> for why this is exactly 1.0.
    /// </summary>
    Standard,

    /// <summary>1.25 — the zoomed level. See <see cref="UiSizes.Scale(UiSize)"/>.</summary>
    Large,
}

/// <summary>
///  The scale factor of each <see cref="UiSize"/>, its label and its persisted name, kept
///  together so the list of levels exists in exactly one place.
/// </summary>
public static class UiSizes
{
    /// <summary>The levels offered in Settings, in the order they are shown.</summary>
    public static readonly UiSize[] All = [UiSize.Standard, UiSize.Large];

    /// <summary>
    ///  The zoom factor applied to a window's content.
    ///
    ///  <para><b>Standard is exactly 1.0, and that is a finding rather than a
    ///  convenience.</b> The user's original complaint (M81) was that the port read
    ///  <em>larger</em> than upstream Git Extensions. The cause was found and fixed then:
    ///  Fluent draws its chrome at 14px, upstream draws it in <c>SystemFonts.MessageBoxFont</c>
    ///  — Segoe UI 9pt, i.e. <b>12px</b> at 100% DPI (<c>AppSettings.Font</c>,
    ///  GitCommands/Settings/AppSettings.cs:1550) — and the port now writes 12
    ///  (<see cref="UiScaling.InstallChromeBaseline"/>). The port's own metrics were
    ///  measured off upstream too: main toolbar 25px, revision grid row 24px, image-only
    ///  toolbar buttons 23x22 with 16px icons. So at 1.0 the port is already at upstream's
    ///  scale and there is nothing left to correct with a factor. Standard therefore
    ///  installs <b>no transform</b>, and the default path through the app is identical to
    ///  a build without this feature.</para>
    ///
    ///  <para><b>Large is 1.25.</b> 125% is the conventional first step on both Windows
    ///  display scaling and GNOME, so it is the factor a user asking for "more zoomed"
    ///  most likely means. It is also the smallest step that is genuinely useful on a
    ///  high-DPI panel — 110% is within the noise of a font tweak, which is the mechanism
    ///  the user just rejected. 150% was considered and not chosen: Git Extensions is a
    ///  dense tool whose value is how much history fits on screen, and at 150% the
    ///  revision grid loses about a third of its visible rows. At 1.25 the 12px chrome
    ///  lands on 15px, which is the size M84 already shipped as its largest step, so the
    ///  legibility of the result is not a guess.</para>
    ///
    ///  <para>Note that a zoom cannot promise whole pixels the way a font size could: at
    ///  1.25 a 25px toolbar measures 31.25px and the compositor rounds. That is inherent
    ///  to any non-integer scale and is not claimed away here.</para>
    /// </summary>
    public static double Scale(UiSize size) => size switch
    {
        UiSize.Large => 1.25,
        _ => 1.0,
    };

    /// <summary>The label shown in the Settings combo.</summary>
    public static string Label(UiSize size) => size switch
    {
        UiSize.Large => "Large (125%)",
        _ => "Standard (like Git Extensions)",
    };

    /// <summary>The name written to <c>ui-state.json</c>.</summary>
    public static string Name(UiSize size) => size.ToString();

    /// <summary>
    ///  Parses a persisted name, <b>migrating the four-step names of M81/M84</b> and
    ///  falling back to <see cref="UiSize.Standard"/> for anything unrecognised — the same
    ///  rule as <c>Theme</c> and <c>Style</c>, so a hand-edited or older file can never
    ///  produce a level the app cannot draw.
    ///
    ///  <para><b>The migration is not a fallback.</b> A file written by M81/M84 holds
    ///  <c>Small</c>, <c>Normal</c>, <c>Large</c> or <c>VeryLarge</c>. Letting the unknown
    ///  ones fall through to <see cref="UiSize.Standard"/> would be wrong for
    ///  <c>VeryLarge</c>: a user who had picked the <em>largest</em> step would silently
    ///  be moved to the <em>smallest</em> level on upgrade. So the two enlarging names map
    ///  to <see cref="UiSize.Large"/> and the two non-enlarging ones to
    ///  <see cref="UiSize.Standard"/>:</para>
    ///  <list type="bullet">
    ///   <item><c>Small</c> (11px, 92%) → <see cref="UiSize.Standard"/>. Nothing below
    ///    upstream's scale is offered any more; Standard is the closest level that
    ///    exists.</item>
    ///   <item><c>Normal</c> (12px, 100%) → <see cref="UiSize.Standard"/>, the same
    ///    thing under its new name.</item>
    ///   <item><c>Large</c> (13px, 108%) → <see cref="UiSize.Large"/>. Parses as the new
    ///    member by name anyway; listed here so the intent is on the record.</item>
    ///   <item><c>VeryLarge</c> (15px, 125%) → <see cref="UiSize.Large"/>, whose 1.25 is
    ///    the very factor that step was nominally after.</item>
    ///  </list>
    /// </summary>
    public static UiSize Parse(string? name)
    {
        // Checked BEFORE Enum.TryParse. "Large" is the one name the two vocabularies
        // share, and it means the same thing in both, so the order does not matter for
        // it — but going through this switch first keeps every legacy name answered in
        // one place instead of splitting them across two mechanisms.
        switch (name?.Trim().ToLowerInvariant())
        {
            case "small":
            case "normal":
                return UiSize.Standard;
            case "verylarge":
                return UiSize.Large;
        }

        return Enum.TryParse(name, ignoreCase: true, out UiSize size) && Array.IndexOf(All, size) >= 0
            ? size
            : UiSize.Standard;
    }
}
