using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The app's style tokens: the small, closed vocabulary of spacings, text sizes,
///  corner radii and motion timings the UI is allowed to use.
///
///  <para><b>Why this exists.</b> The port grew its numbers one view at a time and
///  the result is a scale that is not a scale. Measured on the tree this file was
///  added to (<c>App/Views</c>, 54 files):</para>
///  <list type="bullet">
///   <item><description><c>FontSize</c>: <b>12</b> in 81 places, <b>11</b> in 20,
///     <b>10</b> in 10, <b>13</b> in 6 — four sizes inside a 3px band, which is
///     noise, not hierarchy.</description></item>
///   <item><description><c>Thickness</c>: mostly 16/12/8/4 (a clean base-4 scale)
///     but with <b>6</b> and <b>10</b> mixed in, which are off-scale.</description></item>
///   <item><description><c>CornerRadius</c>: <b>6 occurrences in the whole
///     code base</b> — corners are essentially unused.</description></item>
///  </list>
///
///  <para><b>Scope.</b> This file defines the vocabulary; it does not impose it.
///  Converting the 117 literal font sizes in the views is deliberately left to a
///  later pass (it is a mechanical but large edit, and it touches every view file).
///  What consumes the tokens today is <see cref="ModernStyles"/>, which applies them
///  through app-wide styles and Fluent resource keys — i.e. without editing a single
///  view.</para>
///
///  <para><b>Colours are not here.</b> They live in <see cref="ThemeManager"/> under
///  the <c>App.*</c> keys and must be read as brush <em>instances</em> from
///  <see cref="Application.Resources"/>, because a theme switch mutates each brush's
///  colour in place. Copying a colour into a new brush freezes it.</para>
/// </summary>
public static class Metrics
{
    /// <summary>
    ///  The spacing scale: base 4, five steps. Every margin, padding and gap should
    ///  be one of these five numbers.
    ///
    ///  <para><b>Off-scale values to retire:</b> <c>6</c> and <c>10</c>. They appear
    ///  in a handful of <c>Thickness</c> literals in the views and read as
    ///  near-misses of <see cref="Sm"/> (8) and <see cref="Md"/> (12) — close enough
    ///  that nothing looks intentional, far enough that nothing lines up. The one
    ///  deliberate survivor is the tab-header padding in <see cref="ModernStyles"/>,
    ///  kept at 12,6 only so moving the style out of <c>App.Initialize</c> changes no
    ///  pixels; see the comment there.</para>
    /// </summary>
    public static class Space
    {
        /// <summary>4 — hairline gap: icon-to-label, chip innards.</summary>
        public const double Xs = 4;

        /// <summary>8 — tight gap: between siblings in a row of controls.</summary>
        public const double Sm = 8;

        /// <summary>12 — the default gap: control padding, list-row insets.</summary>
        public const double Md = 12;

        /// <summary>16 — panel padding, dialog content inset.</summary>
        public const double Lg = 16;

        /// <summary>24 — section separation inside a dialog.</summary>
        public const double Xl = 24;

        /// <summary>Uniform <see cref="Thickness"/> from a scale step.</summary>
        public static Thickness All(double step) => new(step);

        /// <summary>Horizontal / vertical <see cref="Thickness"/> from scale steps.</summary>
        public static Thickness Hv(double horizontal, double vertical) => new(horizontal, vertical);
    }

    /// <summary>
    ///  The type scale: <b>five</b> levels, and only five.
    ///
    ///  <para><b>The rule that matters is not the size.</b> The four sizes the views
    ///  use today (10/11/12/13) span 3px: at normal reading distance that difference
    ///  is invisible, so the UI currently has no hierarchy at all — just jitter.
    ///  Hierarchy here comes from <b>weight and colour first, size last</b>:</para>
    ///  <list type="bullet">
    ///   <item><description>demote with colour — <c>App.TextDim</c> instead of
    ///     <c>App.Text</c>;</description></item>
    ///   <item><description>promote with weight — <see cref="SubtitleWeight"/> /
    ///     <see cref="TitleWeight"/>;</description></item>
    ///   <item><description>change the size only when the element genuinely changes
    ///     rank (a caption is not a title).</description></item>
    ///  </list>
    ///
    ///  <para>Two neighbouring levels therefore differ by <em>at least</em> a weight
    ///  step or a colour role, never by size alone.</para>
    /// </summary>
    public static class Text
    {
        /// <summary>11 — captions, timestamps, counts. Pair with <c>App.TextDim</c>.</summary>
        public const double Caption = 11;

        /// <summary>12 — dense body text: grids, diffs, file lists.</summary>
        public const double Body = 12;

        /// <summary>13 — one rank above body text. NOT the app default: since M81 the
        /// app-wide size is <see cref="Body"/> (12), which is what upstream Git
        /// Extensions draws its chrome in and what ModernStyles installs through
        /// Fluent's <c>ControlContentThemeFontSize</c>.</summary>
        public const double Subtitle = 13;

        /// <summary>16 — a section or dialog heading.</summary>
        public const double Title = 16;

        /// <summary>20 — the one big number on a page (dashboard, About).</summary>
        public const double Display = 20;

        /// <summary>Caption weight: normal. It is demoted by <em>colour</em>.</summary>
        public static readonly FontWeight CaptionWeight = FontWeight.Normal;

        /// <summary>Body weight: normal.</summary>
        public static readonly FontWeight BodyWeight = FontWeight.Normal;

        /// <summary>Subtitle weight: normal by default, SemiBold when it is the
        /// selected / active one of a set (see the TabItem rule in
        /// <see cref="ModernStyles"/>).</summary>
        public static readonly FontWeight SubtitleWeight = FontWeight.Normal;

        /// <summary>The "active sibling" weight — how a selected tab, a current
        /// branch or a chosen row is promoted without changing size.</summary>
        public static readonly FontWeight ActiveWeight = FontWeight.SemiBold;

        /// <summary>Title weight: SemiBold. A title is a title by weight, not by size.</summary>
        public static readonly FontWeight TitleWeight = FontWeight.SemiBold;

        /// <summary>Display weight: Bold.</summary>
        public static readonly FontWeight DisplayWeight = FontWeight.Bold;
    }

    /// <summary>
    ///  The corner scale: three radii. Corners are near-absent in the port today
    ///  (six literals in total), so this is as much a decision to <em>have</em>
    ///  rounded corners as it is a scale.
    /// </summary>
    public static class Radius
    {
        /// <summary>4 — inputs and anything that must sit flush in a dense row
        /// (TextBox, ComboBox, small toggles).</summary>
        public const double Sm = 4;

        /// <summary>6 — buttons and standalone surfaces.</summary>
        public const double Md = 6;

        /// <summary>10 — cards, flyouts, dialog-level containers.</summary>
        public const double Lg = 10;

        /// <summary><see cref="Sm"/> as a <see cref="CornerRadius"/>.</summary>
        public static readonly CornerRadius SmCorner = new(Sm);

        /// <summary><see cref="Md"/> as a <see cref="CornerRadius"/>.</summary>
        public static readonly CornerRadius MdCorner = new(Md);

        /// <summary><see cref="Lg"/> as a <see cref="CornerRadius"/>.</summary>
        public static readonly CornerRadius LgCorner = new(Lg);
    }

    /// <summary>
    ///  Motion. Short enough to feel like a response, long enough to be seen:
    ///  120–160 ms, nothing else.
    ///
    ///  <para><b>Only four properties may be animated</b> —
    ///  <c>Background</c>, <c>Foreground</c>, <c>BorderBrush</c>, <c>Opacity</c>.
    ///  Never a layout property (Width/Height/Margin/Padding/FontSize): those
    ///  re-run measure/arrange every frame, and in this app that means the
    ///  custom-drawn, virtualized revision grid.</para>
    /// </summary>
    public static class Motion
    {
        /// <summary>120 ms — the quick end: things leaving a state.</summary>
        public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);

        /// <summary>140 ms — the default state change.</summary>
        public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(140);

        /// <summary>160 ms — the slow end, for a larger surface.</summary>
        public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(160);

        /// <summary>For what is arriving: fast start, soft landing.</summary>
        public static readonly Easing EaseOut = new CubicEaseOut();

        /// <summary>For what is leaving: soft start, quick exit.</summary>
        public static readonly Easing EaseIn = new CubicEaseIn();
    }
}
