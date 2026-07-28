using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The file list's "Copy path(s)" command, as a menu item with a sub-menu of
///  path flavours. Port of <c>GitUI.CommandsDialogs.Menus.CopyPathsToolStripMenuItem</c>.
///
///  <para>Upstream offers five flavours; only three survive on Linux:</para>
///  <list type="bullet">
///   <item><description>The <b>WSL</b> and <b>Cygwin</b> full paths translate a Windows
///    drive letter (<c>PathUtil.ToMountPath</c> rewrites <c>X:</c> to <c>/mnt/x</c> or
///    <c>/cygdrive/x</c>). A Linux path has no drive letter, so both would return the
///    path unchanged and duplicate the native entry. They are not offered here.</description></item>
///   <item><description>The <b>relative POSIX</b> and <b>relative native</b> entries differ
///    only in the directory separator, and on Linux the native separator <em>is</em>
///    <c>/</c> — the two are byte-identical. They are collapsed into one entry.</description></item>
///  </list>
///
///  <para>The remaining "copy the bare file name" entry has no upstream counterpart in
///  this menu; it is an addition.</para>
///
///  <para>The default flavour — the absolute native path — is shown in bold and carries
///  the Ctrl+C gesture, matching upstream's
///  <c>copyFullPathsNativeToolStripMenuItem</c>.</para>
/// </summary>
internal sealed class CopyPathsMenuItem : MenuItem
{
    /// <summary>Which path a sub-menu entry puts on the clipboard.</summary>
    internal enum PathFlavour
    {
        /// <summary>Absolute path in the working tree, native separators (the default).</summary>
        FullNative,

        /// <summary>Path as git reports it: relative to the repository root.</summary>
        Relative,

        /// <summary>The last segment only, with no directory part.</summary>
        FileName,
    }

    // Avalonia resolves a ControlTheme by the control's exact type, so a subclass of
    // MenuItem finds no theme, gets no template, and lays out at zero height: the
    // command silently disappears from the menu, leaving only the gap between its
    // neighbouring separators. Point the lookup back at MenuItem.
    protected override Type StyleKeyOverride => typeof(MenuItem);

    private readonly Func<IEnumerable<string?>> _getPaths;
    private readonly Func<string?> _getWorkingDir;
    private readonly Action<string> _setClipboard;

    private readonly MenuItem _fullNativeItem;
    private readonly MenuItem _relativeItem;
    private readonly MenuItem _fileNameItem;

    /// <param name="getPaths">
    ///  The repo-relative paths of the current selection, in display order. May be empty.
    /// </param>
    /// <param name="getWorkingDir">The repository working directory, or <see langword="null"/>.</param>
    /// <param name="setClipboard">Writes the assembled text to the clipboard.</param>
    public CopyPathsMenuItem(
        Func<IEnumerable<string?>> getPaths,
        Func<string?> getWorkingDir,
        Action<string> setClipboard)
    {
        _getPaths = getPaths;
        _getWorkingDir = getWorkingDir;
        _setClipboard = setClipboard;

        _fullNativeItem = new MenuItem
        {
            // Upstream bolds the default flavour so the sub-menu says which one the
            // parent command (and Ctrl+C) would have used.
            FontWeight = FontWeight.Bold,
            InputGesture = new KeyGesture(Key.C, KeyModifiers.Control),
        };
        _fullNativeItem.Click += (_, _) => Copy(PathFlavour.FullNative);

        _relativeItem = new MenuItem();
        _relativeItem.Click += (_, _) => Copy(PathFlavour.Relative);

        _fileNameItem = new MenuItem();
        _fileNameItem.Click += (_, _) => Copy(PathFlavour.FileName);

        // Populated here, once: mutating a MenuFlyout's Items from Opening leaves the
        // popup mis-measured and it renders as a thin sliver.
        ItemsSource = new[] { _fullNativeItem, _relativeItem, _fileNameItem };

        ApplyTranslations();
    }

    /// <summary>
    ///  Re-labels the item and its sub-menu. The ids come from upstream's
    ///  <c>FormBrowse</c> group; the "-&#160;native" suffix in the catalogues is accurate
    ///  on Linux too (the native separator is <c>/</c>). The bare-file-name entry has no
    ///  upstream id and stays on the source-text lookup.
    /// </summary>
    public void ApplyTranslations()
    {
        Header = TranslationService.T("FileStatusList/tsmiCopyPaths.Text", "Copy file path");
        _fullNativeItem.Header = TranslationService.T(
            "FormBrowse/copyFullPathsNativeToolStripMenuItem.Text", "Copy full path(s) - native");
        _relativeItem.Header = TranslationService.T(
            "FormBrowse/copyRelativePathsNativeToolStripMenuItem.Text", "Copy relative path(s) - native");
        _fileNameItem.Header = TranslationService.T("Copy file name(s)");
    }

    /// <summary>
    ///  Copies the current selection in the given flavour. Public so a host can bind the
    ///  same behaviour to a keyboard shortcut without going through the menu.
    /// </summary>
    public void Copy(PathFlavour flavour)
    {
        string text = BuildText(_getPaths(), _getWorkingDir(), flavour);

        // Upstream leaves the clipboard alone rather than blanking it when there is
        // nothing to copy.
        if (!string.IsNullOrWhiteSpace(text))
        {
            _setClipboard(text);
        }
    }

    /// <summary>
    ///  Assembles the clipboard text, following upstream's <c>GetFilePaths</c>: nulls
    ///  dropped, duplicates collapsed, one path per line separated by
    ///  <see cref="Environment.NewLine"/>.
    /// </summary>
    internal static string BuildText(IEnumerable<string?> paths, string? workingDir, PathFlavour flavour)
    {
        // Only the absolute flavour prefixes the working directory; upstream passes
        // prefixDir: "" for the relative ones.
        string prefixDir = flavour == PathFlavour.FullNative
            ? workingDir ?? string.Empty
            : string.Empty;

        return string.Join(
            Environment.NewLine,
            paths
                .Where(path => path is not null)
                .Distinct(StringComparer.Ordinal)
                .Select(path => Convert(prefixDir, path!, flavour)));
    }

    private static string Convert(string prefixDir, string path, PathFlavour flavour)
    {
        if (flavour == PathFlavour.FileName)
        {
            // A tree node can arrive with a trailing separator; GetFileName would
            // return "" for it.
            string name = Path.GetFileName(path.TrimEnd('/'));
            return name.Length == 0 ? "." : name;
        }

        // Upstream's own guard: with no prefix and no path there is nothing to write,
        // and "." is the directory git would have meant.
        if (prefixDir.Length == 0 && path.Length == 0)
        {
            return ".";
        }

        // PathUtil.ToNativePath is the identity on Linux (the native separator is '/'),
        // so a plain Combine already produces the native form.
        return Path.Combine(prefixDir, path);
    }
}
