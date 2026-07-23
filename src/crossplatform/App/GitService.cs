using GitCommands;
using GitExtensions.Extensibility;

namespace GitExtensions.Avalonia;

/// <summary>
///  A commit as read from git, for display.
/// </summary>
public sealed record CommitRow(string ShortHash, string Author, string Date, string Subject)
{
    public string Display => $"{ShortHash}  {Date}  {Author,-20}  {Subject}";
}

/// <summary>
///  Reads git data by reusing the Git Extensions core process layer
///  (<see cref="Executable"/> + <c>GetOutput</c>) — the same code path the
///  Windows app uses, now running on Linux.
/// </summary>
public static class GitService
{
    private const char FieldSep = ''; // ASCII unit separator

    public static bool IsGitRepository(string path)
        => GitModule.IsValidGitWorkingDir(path);

    public static IReadOnlyList<CommitRow> ReadCommits(string repoPath, int maxCount = 200)
    {
        Executable git = new("git", repoPath);

        // No spaces inside the pretty format so it survives argument splitting.
        ArgumentString args =
            $"log --max-count={maxCount} --date=short " +
            $"--pretty=format:%h{FieldSep}%an{FieldSep}%ad{FieldSep}%s";

        string output = git.GetOutput(args);

        List<CommitRow> commits = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(FieldSep);
            if (parts.Length == 4)
            {
                commits.Add(new CommitRow(parts[0], parts[1], parts[2], parts[3]));
            }
        }

        return commits;
    }

    public static string ReadCurrentBranch(string repoPath)
    {
        Executable git = new("git", repoPath);
        return git.GetOutput("rev-parse --abbrev-ref HEAD").Trim();
    }
}
