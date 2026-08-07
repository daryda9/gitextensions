namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Prepares a string for use as a menu item's header.
/// </summary>
/// <remarks>
///  <para>Avalonia reads an underscore in a <c>MenuItem</c> header as the access-key
///  marker: the character is swallowed and the next one is underlined. Menu entries that
///  quote a git ref therefore mangled every name containing one — a worktree branch
///  called <c>my_feature</c> read "myfeature" in the context menu, and the user had no
///  way to tell whether the underscore was really there.</para>
///
///  <para>Doubling the underscore is Avalonia's own escape for a literal one. Applied to
///  the WHOLE header rather than only to the interpolated name: an underscore in a
///  translated caption is just as literal, and no caption in this app declares an access
///  key that way (upstream marks them with <c>&amp;</c>, which the mnemonic strip removes
///  first).</para>
/// </remarks>
internal static class MenuText
{
    /// <summary>The header text as the user should read it.</summary>
    internal static string Escape(string header) => header.Replace("_", "__");
}
