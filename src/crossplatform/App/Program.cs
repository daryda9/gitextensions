using Avalonia;

namespace GitExtensions.Avalonia;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // Initialize the reused Git Extensions core threading context.
        GitUI.CrossPlatformBootstrap.InitializeThreading();

        // Headless self-test: exercise the reused git core without a display.
        // Usage: GitExtensions.Avalonia --selftest [repoPath]
        if (args.Length > 0 && args[0] == "--selftest")
        {
            void Log(string m) { Console.WriteLine(m); Console.Out.Flush(); }
            string repo = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
            Log($"[1] Repo: {repo}");
            Log($"[2] IsGitRepository: {GitService.IsGitRepository(repo)}");
            Log("[3] Reading branch…");
            Log($"[4] Branch: {GitService.ReadCurrentBranch(repo)}");
            Log("[5] Reading commits…");
            var commits = GitService.ReadCommits(repo, maxCount: 10);
            Log($"[6] Commits read: {commits.Count}");
            foreach (var c in commits)
            {
                Log("  " + c.Display);
            }

            return;
        }

        // First argument that is an existing directory becomes the initial repo.
        foreach (string a in args)
        {
            if (Directory.Exists(a))
            {
                App.InitialRepoPath = Path.GetFullPath(a);
                break;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
