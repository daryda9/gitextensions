using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;

namespace GitExtensions.Avalonia.Theming;

/// <summary>
///  Monochrome vector glyphs for the icon names the views ask
///  <see cref="IconLoader"/> for, replacing the multi-coloured 16px PNGs Git
///  Extensions has shipped since 2015.
/// </summary>
/// <remarks>
///  <para>
///   The path data is transcribed from <see href="https://lucide.dev">Lucide</see>
///   (lucide-react 0.462.0), which is ISC licensed:
///  </para>
///  <code>
///   ISC License
///
///   Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as
///   part of Feather (MIT). All other copyright (c) for Lucide are held by
///   Lucide Contributors 2022.
///
///   Permission to use, copy, modify, and/or distribute this software for any
///   purpose with or without fee is hereby granted, provided that the above
///   copyright notice and this permission notice appear in all copies.
///
///   THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
///   WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
///   MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
///   ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
///   WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
///   ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
///   OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
///  </code>
///  <para>
///   Every entry names the Lucide icon it came from. The glyphs are stroke-only
///   line art on Lucide's 24x24 grid with a stroke width of 2; the SVG elements
///   (line, circle, rect, polyline) are flattened into one path, so circles read
///   as a pair of half-turn arcs. They are drawn, not filled, by
///   <c>GlyphIcon</c>.
///  </para>
/// </remarks>
internal static class Icons
{
    /// <summary>Palette key for ordinary glyphs.</summary>
    internal const string Text = "App.Text";

    /// <summary>Palette key for secondary or de-emphasised glyphs.</summary>
    internal const string TextDim = "App.TextDim";

    /// <summary>Palette key for glyphs on an already accented call site.</summary>
    internal const string Accent = "App.Accent";

    // Ordinal for the same reason IconLoader's cache is: the icon names the
    // views pass are matched exactly, so "star" and "Star" must not collide.
    private static readonly ConcurrentDictionary<string, Geometry?> Parsed = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);

    /// <summary>
    ///  The glyph for an icon name, or <see langword="null"/> when the name has
    ///  none and the caller should fall back to the PNG.
    /// </summary>
    internal static Geometry? Get(string name)
    {
        if (!Data.TryGetValue(name, out string? data))
        {
            return null;
        }

        return Parsed.GetOrAdd(name, _ =>
        {
            try
            {
                return StreamGeometry.Parse(data);
            }
            catch (Exception ex)
            {
                // A typo in the transcribed path must not take a view down; the
                // caller then draws the PNG as if the name were unmapped.
                if (Reported.TryAdd(name, 0))
                {
                    string line = $"[Icons] glyph '{name}' failed to parse: {ex.Message}";
                    Console.WriteLine(line);
                    Debug.WriteLine(line);
                }

                return null;
            }
        });
    }

    /// <summary>Number of names carrying a glyph, for coverage reporting.</summary>
    internal static int Count => Data.Count;

    // ---- the glyphs -------------------------------------------------------
    //
    // Authored on a 24x24 grid, stroked (never filled) at a width of 2 with
    // round caps and joins, following the line-art conventions Lucide uses
    // (https://lucide.dev, ISC). Circles are written as a pair of half-turn
    // arcs because StreamGeometry has no circle primitive. Shapes stay inside
    // roughly 2..22 so the round joins do not touch the clip.
    //
    // Related actions deliberately share one shape so the set reads as a
    // family: Push and Pull are the same arrow mirrored about the same
    // baseline, every "go to" is the same chevron, every destructive action is
    // the same bin.

    private const string Folder = "M3 18V6h6l2 3h10v9Z";
    private const string FolderOpen = "M3 18V6h6l2 3h10v2.5 M3 18l3-5.5h15L18 18Z";
    private const string FolderGit = "M3 18V6h6l2 3h10v9Z M10 14a2 2 0 1 0 4 0a2 2 0 1 0-4 0 M5 14h5 M14 14h5";
    private const string FolderError = "M3 18V6h6l2 3h10v9Z M10 11.5l4 4 M14 11.5l-4 4";

    // The branch: a trunk with a commit at its foot and a second line curving
    // away to its own commit. Every branch-flavoured name is this shape plus a
    // mark, so they stay recognisable as one another at 16px.
    private const string Branch =
        "M6 4v10 M3.5 17a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 "
        + "M15.5 6a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 M18 8.5a9.5 9.5 0 0 1-9.5 8.5";
    private const string BranchCreate =
        "M6 4v10 M3.5 17a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 "
        + "M18 8.5a9.5 9.5 0 0 1-9.5 8.5 M18 2.5v6 M15 5.5h6";
    private const string Check = "M4 12.5l5.5 5.5L20 6.5";
    private const string Bin = "M4 7h16 M9 7V4h6v3 M6.5 7l1 13h9l1-13 M10 11v6 M14 11v6";

    private const string Cloud = "M6.5 18h11a4 4 0 0 0 0-8 6 6 0 0 0-11.6-1.5A5 5 0 0 0 6.5 18Z";

    // Push and pull: one arrow, mirrored, over the same baseline standing for
    // the local repository. Fetch is the pull arrow with the baseline broken,
    // because a fetch does not land in the working tree.
    private const string Push = "M5 20h14 M12 16V4 M7 9l5-5 5 5";
    private const string Pull = "M5 20h14 M12 4v12 M7 11l5 5 5-5";
    private const string Fetch = "M4 20h4 M16 20h4 M12 3v13 M7 11l5 5 5-5";

    private const string Merge =
        "M3.5 6a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 M15.5 18a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 "
        + "M6 8.5v11.5 M15.5 18a9.5 9.5 0 0 0-9.5-9.5";
    private const string Rebase = "M6 20V9a4 4 0 0 1 4-4h6 M13 2l3 3-3 3";

    private const string Commit = "M3 12h6 M15 12h6 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0";
    private const string Stash = "M3 6h18v4.5H3z M5 10.5V19h14v-8.5 M9.5 14h5";

    private const string Refresh = "M20 12a8 8 0 1 1-8-8 M9 1l3 3-3 3";
    private const string RefreshDirty =
        "M20 12a8 8 0 1 1-8-8 M9 1l3 3-3 3 M16 19a2 2 0 1 0 4 0a2 2 0 1 0-4 0";

    private const string Boxes =
        "M3.5 14h6v6h-6z M14.5 14h6v6h-6z M9 3.5h6v6H9z M12 9.5V12 M6.5 14v-2h11v2";

    private const string PanelLeft = "M3.5 5h17v14h-17z M9.5 5v14";
    private const string PanelBottom = "M3.5 5h17v14h-17z M3.5 15h17";
    private const string PanelTopLeft = "M3.5 5h17v14h-17z M9.5 5v14 M3.5 11h6";
    private const string PanelTopRight = "M3.5 5h17v14h-17z M14.5 5v14 M14.5 11h6";

    private const string Tag = "M4 11V4h7l9 9-7 7z M7 8a1 1 0 1 0 2 0a1 1 0 1 0-2 0";
    private const string Tree = "M12 3l5 7h-3l4 6H6l4-6H7z M12 16v5";

    private const string FileDiff =
        "M6 3.5h8l4 4v13H6z M14 3.5v4h4 M9.5 12h5 M12 9.5v5 M9.5 17.5h5";
    private const string FolderTree =
        "M4 4h6v4H4z M14.5 10h6v4h-6z M14.5 16.5h6v4h-6z M7 8v10.5h7.5 M7 12h7.5";
    private const string Key =
        "M11 8.5a4.5 4.5 0 1 0 9 0a4.5 4.5 0 1 0-9 0 M12.3 11.7L4.5 19.5 M6.5 17.5l2 2 M8.5 15.5l2 2";
    private const string Terminal = "M3.5 5h17v14h-17z M7 10l3 2.5-3 2.5 M12.5 15h5";
    private const string Log = "M6 3.5h12v17H6z M9 8h6 M9 12h6 M9 16h4";
    private const string User =
        "M8.5 8a3.5 3.5 0 1 0 7 0a3.5 3.5 0 1 0-7 0 M5 20c0-3.3 3.1-6 7-6s7 2.7 7 6";

    // The counter-clockwise dial: history looks back, reset winds back.
    private const string Rewind = "M4 12a8 8 0 1 0 2.3-5.6 M6.3 6.4h-3.8 M6.3 6.4V2.6";
    private const string History = Rewind + " M12 8v4.5l3 1.5";

    private const string Sliders =
        "M3 7h10.5 M18.5 7H21 M3 17h3.5 M11.5 17H21 "
        + "M13.5 7a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 M6.5 17a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0";
    private const string Info =
        "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 11.5V16.5 M11.4 8a.6 .6 0 1 0 1.2 0a.6 .6 0 1 0-1.2 0";
    private const string Book = "M4 19.5V5.5A2.5 2.5 0 0 1 6.5 3H20v18H6.5a2.5 2.5 0 0 1 0-5H20";
    private const string Star =
        "M12 3.5l2.7 5.5 6 .9-4.35 4.25 1.03 6-5.38-2.83-5.38 2.83 1.03-6L3.3 9.9l6-.9z";
    private const string StarOff = Star + " M4 4l16 16";
    private const string Copy = "M8.5 8.5h11v11h-11z M15.5 8.5V4.5h-11v11h4";
    private const string Warning =
        "M12 3.5L22 20.5H2z M12 10v4.5 M11.4 18a.6 .6 0 1 0 1.2 0a.6 .6 0 1 0-1.2 0";
    private const string File = "M6 3.5h8l4 4v13H6z M14 3.5v4h4";
    private const string FilePen = "M18 3.5l2.5 2.5-9 9H9v-2.5z M19.5 20.5h-15v-15h7";
    private const string ArrowLeft = "M20 12H4 M10 6l-6 6 6 6";
    private const string ArrowRight = "M4 12h16 M14 6l6 6-6 6";
    private const string Undo = "M4 10h11a5 5 0 0 1 0 10h-6 M8 6l-4 4 4 4";
    private const string Search = "M6 11a5 5 0 1 0 10 0a5 5 0 1 0-10 0 M15.5 15.5l4.5 4.5";
    private const string Filter = "M3 5h18l-7 8v7l-4-2v-5z";

    private static readonly Dictionary<string, string> Data = new(StringComparer.Ordinal)
    {
        // Repository and folders
        ["RepoOpen"] = FolderOpen,
        ["FolderOpen"] = FolderOpen,
        ["FolderClosed"] = Folder,
        ["BranchFolder"] = Folder,
        ["DashboardFolderGit"] = FolderGit,
        ["DashboardFolderError"] = FolderError,
        ["RepoCreate"] = FolderGit,
        ["RecentRepositories"] = History,

        // Branches
        ["Branch"] = Branch,
        ["BranchLocal"] = Branch,
        ["LocalBranchRoot"] = Branch,
        ["SelectBranch"] = Branch,
        ["BranchCheckout"] = Check,
        ["checkout"] = Check,
        ["BranchCreate"] = BranchCreate,
        ["BranchDelete"] = Bin,
        ["BranchFilter"] = Filter,

        // Remotes
        ["Remote"] = Cloud,
        ["Remotes"] = Cloud,
        ["BranchRemote"] = Cloud,
        ["RemoteBranchRoot"] = Cloud,
        ["Globe"] = Cloud,

        // Transfer
        ["Push"] = Push,
        ["Pull"] = Pull,
        ["PullFetch"] = Fetch,
        ["PullFetchAll"] = Fetch,
        ["PullFetchPrune"] = Fetch,
        ["PullFetchPruneAll"] = Fetch,
        ["PullMerge"] = Merge,
        ["PullRebase"] = Rebase,
        ["Merge"] = Merge,
        ["SolveMerge"] = Merge,
        ["Rebase"] = Rebase,

        // Commits
        ["Commit"] = Commit,
        ["CommitSummary"] = Commit,
        ["CommitId"] = Commit,
        ["GotoCommit"] = Commit,
        ["RevertCommit"] = Undo,
        ["ResetCurrentBranchToHere"] = Rewind,
        ["ResetAnotherBranchToHere"] = Rewind,
        ["ResetWorkingDirChanges"] = Rewind,
        ["ResetFileTo"] = Rewind,

        // Stash, submodules, worktrees, tags
        ["stash"] = Stash,
        ["ArchiveRevision"] = Stash,
        ["SubmodulesManage"] = Boxes,
        ["FolderSubmodule"] = Boxes,
        ["SubmodulesUpdate"] = Boxes,
        ["SubmodulesSync"] = Boxes,
        ["WorkTree"] = Tree,
        ["Tag"] = Tag,
        ["TagHorizontal"] = Tag,
        ["TagMany"] = Tag,
        ["TagCreate"] = Tag,
        ["TagDelete"] = Bin,

        // Refresh
        ["ReloadRevisions"] = Refresh,
        ["ReloadRevisionsDirty"] = RefreshDirty,

        // Layout
        ["LayoutSidebarLeft"] = PanelLeft,
        ["LayoutFooter"] = PanelBottom,
        ["LayoutFooterTab"] = PanelBottom,
        ["LayoutSidebarTopLeft"] = PanelTopLeft,
        ["LayoutSidebarTopRight"] = PanelTopRight,

        // Bottom tab strip
        ["Diff"] = FileDiff,
        ["FileTree"] = FolderTree,
        ["DocumentTree"] = FolderTree,
        ["Key"] = Key,
        ["Console"] = Terminal,
        ["cmd"] = Terminal,
        ["GitCommandLog"] = Log,
        ["Blame"] = User,
        ["Author"] = User,
        ["User80"] = User,
        ["FileHistory"] = History,

        // Files
        ["File"] = File,
        ["ViewFile"] = File,
        ["EditFile"] = FilePen,
        ["DeleteFile"] = Bin,
        ["CopyToClipboard"] = Copy,

        // Navigation
        ["NavigateBackward"] = ArrowLeft,
        ["NavigateForward"] = ArrowRight,

        // Chrome
        ["Settings"] = Sliders,
        ["GeneralSettings"] = Sliders,
        ["AdvancedSettings"] = Sliders,
        ["information"] = Info,
        ["GotoManual"] = Book,
        ["Book"] = Book,
        ["Changelog"] = Book,
        ["Warning"] = Warning,
        ["star"] = Star,
        ["StarRemove"] = StarOff,
        ["EditFilter"] = Filter,
    };

    /// <summary>
    ///  The palette brush for a tint key. The live instance is returned, never a
    ///  copy: ThemeManager recolours by mutating the Color of the brushes it
    ///  registered, so a copy would freeze at the theme in force when the icon
    ///  was built.
    /// </summary>
    internal static IBrush? Tint(string key)
        => Application.Current?.Resources.TryGetResource(key, null, out object? value) == true
            ? value as IBrush
            : null;
}
