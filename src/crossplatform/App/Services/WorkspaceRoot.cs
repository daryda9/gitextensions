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
    ///  submodule's or linked worktree's <c>.git</c> is a FILE holding a gitdir
    ///  pointer rather than a directory, so both shapes count — testing only for the
    ///  directory would miss every submodule, which is the case this class exists for.
    ///
    ///  <para><b>The entry has to look like git's, not merely be named after it.</b>
    ///  The first version accepted any <c>.git</c>, and an <b>empty</b> <c>/tmp/.git</c>
    ///  directory — the kind a script leaves behind — made every repository under
    ///  <c>/tmp</c> answer "<c>/tmp</c>" here. git itself says "not a repository" to
    ///  that path, but the tab strip believed it: one checkout for every tab, so the
    ///  colour that tells two checkouts apart stayed off and the tooltip named a
    ///  checkout that does not exist. A directory therefore has to hold <c>HEAD</c>,
    ///  and a file has to start with <c>gitdir:</c>; anything else is a folder that
    ///  happens to be called <c>.git</c>. Both checks are one stat and 8 bytes, on a
    ///  path already walked once per tab and cached for the process.</para>
    /// </summary>
    private static bool IsWorkingTree(string directory)
    {
        string dot = Path.Combine(directory, ".git");

        if (Directory.Exists(dot))
        {
            return File.Exists(Path.Combine(dot, "HEAD"));
        }

        return File.Exists(dot) && PointsAtGitDir(dot);
    }

    /// <summary>
    ///  Whether a <c>.git</c> file is git's pointer file. Read as bytes and compared
    ///  to ASCII: the prefix git writes is fixed, and a file too short or unreadable
    ///  is simply not one.
    /// </summary>
    private static bool PointsAtGitDir(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            Span<byte> head = stackalloc byte[8];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                && head.SequenceEqual("gitdir: "u8);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
