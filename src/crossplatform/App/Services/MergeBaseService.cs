using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The one git query the revision grid's "Go to common ancestor (merge base)"
///  entry needs: <c>git merge-base A B</c>.
///
///  <para>It lives in its own service rather than in
///  <see cref="RevisionService"/> because it is not part of the revision WALK:
///  nothing about paging, ordering, ref scope or the revision filter applies to
///  it, and the grid calls it once per user request instead of once per page.</para>
///
///  <para>Best effort by design. Two commits with no common ancestor (unrelated
///  histories), a repository path that is not a work tree, or any git failure all
///  come back as <see langword="null"/>, which the caller reports in the status
///  line — a menu entry that quietly does nothing would be worse than one that
///  says why. Runs synchronously and MUST therefore be called from a background
///  thread (the grid wraps it in <c>Task.Run</c>).</para>
/// </summary>
public static class MergeBaseService
{
    /// <summary>
    ///  Returns the full hash of the most recent common ancestor of
    ///  <paramref name="first"/> and <paramref name="second"/>, or
    ///  <see langword="null"/> when there is none (or git failed).
    /// </summary>
    public static string? FindMergeBase(string repoPath, string first, string second)
        => string.IsNullOrEmpty(repoPath) ? null : FindMergeBase(GitContext.CreateModule(repoPath), first, second);

    /// <summary>
    ///  As <see cref="FindMergeBase(string, string, string)"/>, for a caller that
    ///  already holds the module. The multi-revision diff
    ///  (<see cref="DiffService.GetSelectionDiffGroups"/>) asks for up to three merge
    ///  bases in one selection change, and each <c>CreateModule</c> re-discovers the
    ///  repository — so the module is passed in there rather than rebuilt per query.
    ///
    ///  <para>Answers <see langword="null"/> for the two cases upstream's own
    ///  <c>GetMergeBase</c> local function short-circuits (an empty side, or both
    ///  sides being the same commit): a commit is trivially its own base, and saying
    ///  so would make the caller believe it had found a branch point.</para>
    /// </summary>
    public static string? FindMergeBase(GitModule module, string first, string second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)
            || string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            ExecutionResult result = module.GitExecutable.Execute(
                new GitArgumentBuilder("merge-base") { first, second },
                throwOnErrorExit: false);

            if (!result.ExitedSuccessfully)
            {
                // Exit code 1 is git's "no merge base" answer, not an error.
                return null;
            }

            string hash = result.StandardOutput.Trim();
            return hash.Length > 0 ? hash : null;
        }
        catch
        {
            // A refresh path must never throw (see the grid's conventions).
            return null;
        }
    }
}
