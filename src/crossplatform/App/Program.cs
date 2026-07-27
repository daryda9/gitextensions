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

            // Validate the shared GitModule factory + reused core APIs.
            Log("[7] Building GitModule via GitContext…");
            var module = GitContext.CreateModule(repo);
            Log($"[8] Module.WorkingDir: {module.WorkingDir}");
            _ = new GitCommands.RevisionReader(module);
            var head = module.GetCurrentCheckout();
            Log($"[9] HEAD: {head}");
            var changed = module.GetAllChangedFiles();
            Log($"[10] Working-dir changed files: {changed.Count}");

            // HOME as git children see it, after the core has had every chance to
            // rewrite it. When this points somewhere without the user's .gitconfig,
            // git finds no credential.helper and re-asks for credentials on every push.
            Log($"[11] HOME for git children: {GitCommands.EnvironmentConfiguration.GetHomeDir()}");
            Log($"[12] credential.helper: {ReadCredentialHelper(repo)}");

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

        // Start parsing the remembered translation catalogue NOW, on the thread
        // pool, so it runs concurrently with Avalonia's own start-up and the shell
        // can be built already translated (App.OnFrameworkInitializationCompleted
        // joins it just before the first window). Doing this later — from the
        // window's Opened handler, as the first version did — made a non-English UI
        // appear in English and re-label itself about a second afterwards.
        // English is the default and costs nothing here: BeginPreload returns
        // immediately without touching the disk.
        try
        {
            BeginTranslationPreload();
        }
        catch
        {
            // A missing/corrupt ui-state.json just means "English".
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    ///  Reads the persisted UI language (a few hundred bytes of JSON) and hands it
    ///  to <see cref="Services.TranslationService.BeginPreload"/>. Kept in its own
    ///  method so <see cref="Main"/> stays readable.
    /// </summary>
    private static void BeginTranslationPreload()
        => Services.TranslationService.BeginPreload(new Services.UiStateService().Load().Language);

    /// <summary>
    ///  Asks git itself which credential helper it resolves for <paramref name="repo"/>,
    ///  inheriting this process's environment — i.e. exactly what a push would see.
    /// </summary>
    private static string ReadCredentialHelper(string repo)
    {
        try
        {
            using System.Diagnostics.Process? p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "config", "--get", "credential.helper" },
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (p is null)
            {
                return "<git not started>";
            }

            string helper = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return helper.Length > 0 ? helper : "<none — push will re-prompt every time>";
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
