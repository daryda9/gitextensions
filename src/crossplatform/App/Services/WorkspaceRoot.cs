using System.Collections.Concurrent;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Which checkout a path belongs to — the <b>outermost</b> repository above it,
///  which for a submodule is its superproject and for an ordinary repository is
///  itself.
///
///  <para><b>Why this exists.</b> Two clones of one project used side by side
///  (say <c>~/work/api</c> and <c>~/review/api</c>) contain submodules with
///  identical names, so a submodule tab opened from either looks exactly like the
///  other. The repository tab strip already lengthens a label until two paths stop
///  colliding, but a longer label is read, not seen — and the two clones are told
///  apart faster by a colour that says "this tab belongs to that checkout" than by
///  comparing two strings that differ in the middle. That colour needs a stable
///  key per checkout, and this is it.</para>
///
///  <para><b>Filesystem only, no git process.</b> The answer is "the topmost
///  ancestor that is a working tree", which is a question about directories:
///  running <c>git rev-parse --show-superproject-working-tree</c> once per tab per
///  repaint would be a process per tab for something that cannot change while the
///  tab is open. Results are cached for the life of the process for the same
///  reason.</para>
/// </summary>
public static class WorkspaceRoot
{
    // Bounded by the filesystem, but a bound of its own keeps a pathological path
    // (a symlink loop resolved into something enormous) from walking forever.
    private const int MaxDepth = 64;

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>
    ///  The outermost working tree containing <paramref name="path"/>, or
    ///  <paramref name="path"/> itself when there is none above it. Never throws:
    ///  an unreadable ancestor simply ends the walk.
    /// </summary>
    public static string Of(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        return Cache.GetOrAdd(path, Resolve);
    }

    private static string Resolve(string path)
    {
        string outermost = path;

        try
        {
            DirectoryInfo? directory = new(path);
            for (int depth = 0; depth < MaxDepth && directory is not null; depth++)
            {
                if (IsWorkingTree(directory.FullName))
                {
                    outermost = directory.FullName;
                }

                directory = directory.Parent;
            }
        }
        catch
        {
            // A path we cannot walk is its own root: worse answers than that would
            // be inventing one.
        }

        return outermost;
    }

    /// <summary>
    ///  Whether <paramref name="directory"/> is the top of a working tree. A
    ///  submodule's <c>.git</c> is a FILE holding a gitdir pointer rather than a
    ///  directory, so both shapes count — testing only for the directory would miss
    ///  every submodule, which is the case this class exists for.
    /// </summary>
    private static bool IsWorkingTree(string directory)
    {
        string dot = Path.Combine(directory, ".git");
        return Directory.Exists(dot) || File.Exists(dot);
    }
}
