namespace GitExtensions.Avalonia;

/// <summary>
///  Display-only shaping of filesystem paths shown in the shell chrome.
///
///  <para>The single home of <see cref="CollapseHome"/>, which used to exist twice
///  (once in <c>MainToolbar</c>, once copied into <c>RevisionGridView</c> because
///  that file was owned by another unit at the time). Both callers now share this
///  one implementation, so the toolbar's repository caption, its recent-repository
///  dropdown and the revision grid's status line can never drift apart.</para>
/// </summary>
internal static class PathDisplay
{
    /// <summary>
    ///  Replaces a leading user-home prefix with <c>~</c> for a compact display.
    ///  Purely cosmetic: the persisted/opened path stays absolute and normalized.
    ///  The home directory comes from <see cref="UserHome.Path"/> (snapshotted at
    ///  assembly load, before the core rewrites <c>HOME</c>).
    /// </summary>
    internal static string CollapseHome(string path)
    {
        string home = UserHome.Path;
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        string trimmedHome = home.TrimEnd('/');
        if (trimmedHome.Length == 0)
        {
            return path;
        }

        if (string.Equals(path, trimmedHome, StringComparison.Ordinal))
        {
            return "~";
        }

        // Only collapse on a real directory boundary, so "/home/dariofoo" is left alone.
        if (path.StartsWith(trimmedHome + "/", StringComparison.Ordinal))
        {
            return "~" + path[trimmedHome.Length..];
        }

        return path;
    }
}

/// <summary>
///  The user's real home directory, snapshotted at assembly load.
///
///  It cannot be read on demand: the Git Extensions core rewrites the process
///  <c>HOME</c> variable on startup (<c>EnvironmentConfiguration.SetEnvironmentVariables</c>
///  assigns it <see cref="Environment.SpecialFolder.Personal"/>, which on Linux is
///  <c>~/Documents</c>, and can also clear it), and that runs on a background thread
///  while the shell is already building. A later
///  <c>GetFolderPath(SpecialFolder.UserProfile)</c> therefore returns <c>~/Documents</c>
///  or an empty string depending on timing — which is why the toolbar repo caption
///  intermittently showed the full absolute path instead of the <c>~</c> form.
///
///  The module initializer runs before <c>Main</c> and before any core assembly code,
///  so the value captured here is the genuine login home.
/// </summary>
internal static class UserHome
{
    private static string _path = string.Empty;

    /// <summary>Gets the home directory as it was before any core code touched <c>HOME</c>.</summary>
    internal static string Path => _path;

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Capture()
    {
        try
        {
            _path = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home
                ? home
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch
        {
            _path = string.Empty;
        }
    }
}
