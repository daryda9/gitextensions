using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  A single parsed reflog entry, projected for display in the
///  <see cref="Views.ReflogWindow"/>. Parsed from <c>git reflog</c>.
/// </summary>
/// <param name="Selector">The reflog selector, e.g. <c>HEAD@{0}</c>.</param>
/// <param name="ShortHash">The abbreviated commit hash the entry points at.</param>
/// <param name="Action">The reflog subject / action (e.g. <c>commit: fix bug</c>).</param>
/// <param name="Date">Committer date in ISO 8601 form (may be empty on failure).</param>
public sealed record ReflogEntry(string Selector, string ShortHash, string Action, string Date)
{
    public string Display => $"{Selector,-12} {ShortHash}  {Date}  {Action}";

    public override string ToString() => Display;
}

/// <summary>
///  Reads the git reflog by reusing the Git Extensions core
///  (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>. The
///  method is synchronous and is meant to be called off the UI thread, mirroring
///  the other Avalonia services (e.g. <see cref="WorktreeService"/>).
/// </summary>
public sealed class ReflogService
{
    /// <summary>
    ///  Reads the reflog for HEAD, parsing each entry into a
    ///  <see cref="ReflogEntry"/>. Uses a tab-separated custom format so the
    ///  selector (<c>%gd</c>), short hash (<c>%h</c>), subject (<c>%gs</c>) and
    ///  committer date (<c>%cI</c>) parse unambiguously regardless of the message
    ///  contents. Returns an empty list on failure; never throws.
    /// </summary>
    public IReadOnlyList<ReflogEntry> Read(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // %gd = reflog selector (ordinal HEAD@{0} form), %h = abbrev hash,
        // %cI = strict-ISO committer date, %gs = reflog subject. No --date
        // option, so the selector stays in the ordinal HEAD@{n} form while the
        // date is carried independently in the %cI column.
        GitArgumentBuilder args = new("reflog")
        {
            "--format=%gd%x09%h%x09%cI%x09%gs",
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return [];
        }

        List<ReflogEntry> entries = [];
        foreach (string raw in result.StandardOutput.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            // Split into at most 4 fields; the subject itself may contain tabs
            // (rare) but the trailing field absorbs the remainder.
            string[] parts = line.Split('\t', 4);
            string selector = parts.Length > 0 ? parts[0] : string.Empty;
            string hash = parts.Length > 1 ? parts[1] : string.Empty;
            string date = parts.Length > 2 ? parts[2] : string.Empty;
            string action = parts.Length > 3 ? parts[3] : string.Empty;

            entries.Add(new ReflogEntry(selector, hash, action, date));
        }

        return entries;
    }
}
