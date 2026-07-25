// Real file/folder pickers for the compat layer, backed by Avalonia's
// IStorageProvider (which uses the desktop portal / GTK picker on Linux).
//
// The WinForms API shape is preserved exactly — ShowDialog() blocks and returns
// a DialogResult, the chosen path lands in SelectedPath / FileName — so the
// reusable core (e.g. OsShellUtil.PickFolder) works unchanged.
//
// NOTE: on Linux Avalonia's IStorageProvider needs an XDG desktop portal
// (xdg-desktop-portal + a backend over DBus) to show a picker. On a normal
// desktop session that is present. In a bare headless session there is none:
// the FallbackStorageProvider still reports CanPickFolder = true but its task
// never completes, so ShowDialog() would block the calling thread. Hosts that
// must work without a portal should opt into Avalonia's managed pickers at
// startup (AppBuilder.UseManagedSystemDialogs()); that is an application-level
// decision and cannot be made from inside the shim.

using Avalonia.Platform.Storage;
using GitExtensions.Compat;

namespace System.Windows.Forms;

/// <summary>Folder picker. Replaces the previous always-Cancel stub.</summary>
public sealed class FolderBrowserDialog : IDisposable
{
    public string SelectedPath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool ShowNewFolderButton { get; set; } = true;

    public DialogResult ShowDialog() => ShowDialog(owner: null);

    public DialogResult ShowDialog(IWin32Window? owner)
        => AvaloniaHost.Run(
            async window =>
            {
                IReadOnlyList<IStorageFolder> picked = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = string.IsNullOrEmpty(Description) ? "Select folder" : Description,
                    AllowMultiple = false,
                    SuggestedStartLocation = await FileDialogHelpers.StartFolderAsync(window, SelectedPath),
                });

                if (picked.Count == 0 || FileDialogHelpers.LocalPath(picked[0]) is not { Length: > 0 } path)
                {
                    return DialogResult.Cancel;
                }

                SelectedPath = path;
                return DialogResult.OK;
            },
            fallback: DialogResult.Cancel);

    public void Dispose()
    {
    }
}

/// <summary>Common surface of the WinForms file dialogs.</summary>
public abstract class FileDialog : IDisposable
{
    /// <summary>WinForms filter string, e.g. <c>"Patch files (*.patch)|*.patch|All files (*.*)|*.*"</c>.</summary>
    public string Filter { get; set; } = string.Empty;

    public int FilterIndex { get; set; } = 1;
    public string FileName { get; set; } = string.Empty;
    public string[] FileNames { get; private protected set; } = [];
    public string InitialDirectory { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DefaultExt { get; set; } = string.Empty;
    public bool AddExtension { get; set; } = true;
    public bool RestoreDirectory { get; set; }

    public abstract DialogResult ShowDialog();

    public DialogResult ShowDialog(IWin32Window? owner) => ShowDialog();

    public void Dispose()
    {
    }
}

public sealed class OpenFileDialog : FileDialog
{
    public bool Multiselect { get; set; }
    public bool CheckFileExists { get; set; } = true;

    public override DialogResult ShowDialog()
        => AvaloniaHost.Run(
            async window =>
            {
                IReadOnlyList<IStorageFile> picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = string.IsNullOrEmpty(Title) ? "Open" : Title,
                    AllowMultiple = Multiselect,
                    FileTypeFilter = FileDialogHelpers.ParseFilter(Filter),
                    SuggestedStartLocation = await FileDialogHelpers.StartFolderAsync(window, InitialDirectory, FileName),
                });

                string[] paths = picked
                    .Select(FileDialogHelpers.LocalPath)
                    .Where(p => p.Length > 0)
                    .ToArray();

                if (paths.Length == 0)
                {
                    return DialogResult.Cancel;
                }

                FileNames = paths;
                FileName = paths[0];
                return DialogResult.OK;
            },
            fallback: DialogResult.Cancel);
}

public sealed class SaveFileDialog : FileDialog
{
    public bool OverwritePrompt { get; set; } = true;

    public override DialogResult ShowDialog()
        => AvaloniaHost.Run(
            async window =>
            {
                IStorageFile? picked = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = string.IsNullOrEmpty(Title) ? "Save as" : Title,
                    SuggestedFileName = IO.Path.GetFileName(FileName),
                    DefaultExtension = string.IsNullOrEmpty(DefaultExt) ? null : DefaultExt.TrimStart('.'),
                    ShowOverwritePrompt = OverwritePrompt,
                    FileTypeChoices = FileDialogHelpers.ParseFilter(Filter),
                    SuggestedStartLocation = await FileDialogHelpers.StartFolderAsync(window, InitialDirectory, FileName),
                });

                if (picked is null || FileDialogHelpers.LocalPath(picked) is not { Length: > 0 } path)
                {
                    return DialogResult.Cancel;
                }

                FileName = path;
                FileNames = [path];
                return DialogResult.OK;
            },
            fallback: DialogResult.Cancel);
}

internal static class FileDialogHelpers
{
    internal static string LocalPath(IStorageItem item)
    {
        try
        {
            return item.TryGetLocalPath() ?? item.Path.LocalPath;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///  Resolves the folder the picker should open in, from the first of
    ///  <paramref name="candidates"/> that exists (a directory, or a file's
    ///  directory).
    /// </summary>
    internal static async Task<IStorageFolder?> StartFolderAsync(Avalonia.Controls.Window window, params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string? directory = IO.Directory.Exists(candidate) ? candidate : IO.Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(directory) || !IO.Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                IStorageFolder? folder = await window.StorageProvider.TryGetFolderFromPathAsync(directory);
                if (folder is not null)
                {
                    return folder;
                }
            }
            catch (Exception)
            {
                // Fall through to the next candidate / provider default.
            }
        }

        return null;
    }

    /// <summary>
    ///  Converts a WinForms filter string into Avalonia file types.
    ///  <c>"Patch files (*.patch)|*.patch|All files (*.*)|*.*"</c>.
    /// </summary>
    internal static List<FilePickerFileType>? ParseFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        string[] parts = filter.Split('|');
        List<FilePickerFileType> types = [];

        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            string name = parts[i].Trim();
            string[] patterns = parts[i + 1]
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            if (name.Length == 0 || patterns.Length == 0)
            {
                continue;
            }

            types.Add(new FilePickerFileType(name) { Patterns = patterns });
        }

        return types.Count == 0 ? null : types;
    }
}
