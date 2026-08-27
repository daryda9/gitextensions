namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  The <c>avares:</c> base for this application's own resources.
/// </summary>
/// <remarks>
///  <para><b>Why this exists.</b> The host part of an <c>avares:</c> URI is the
///  assembly name, and this file is what stops that name from being written down. It
///  used to be a literal in five places, and renaming the assembly to <c>GitNext</c>
///  broke them in a way that is worse than a crash: <see cref="Avalonia.Platform.AssetLoader"/>
///  answers "no such asset" for an unknown host, and the callers were written to treat
///  a missing asset as "draw nothing". The merge and rebase dialogs lost the
///  illustration in their left column and said nothing about it — the user found that,
///  not the build and not the harnesses.</para>
///
///  <para>Asked of the running assembly, once. Any future rename moves with it.</para>
/// </remarks>
internal static class AssetUri
{
    /// <summary>e.g. <c>avares://GitNext/</c>, with the trailing slash.</summary>
    internal static string Base { get; } =
        $"avares://{typeof(AssetUri).Assembly.GetName().Name}/";

    /// <summary>The URI of a resource, given its path inside the assembly.</summary>
    internal static string For(string relativePath) => Base + relativePath;
}
