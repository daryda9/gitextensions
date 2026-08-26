using Avalonia;
using Avalonia.Dialogs;

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

        WarmUpCoreOutputEncoding();

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

        // Intern the X11 atoms Avalonia only looks up with "only_if_exists: true",
        // so that its atom table is not left all-zero on a fresh X server. Without
        // this the window advertises no WM_DELETE_WINDOW, the decoration's "X" kills
        // the connection instead of closing the window, and Closing/PersistLayout
        // never runs — the UI state is lost. Must happen before the Avalonia app
        // builder, which is where X11Atoms is populated.
        // See Services/X11AtomPrimer for the full diagnosis; it never throws.
        Services.X11AtomPrimer.TryPrime();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    ///  Materialises the core's default output encoding ONCE, on the main thread,
    ///  before anything can issue git commands concurrently.
    ///
    ///  <para><c>GitCommands.ExecutableExtensions</c> (src/app/GitCommands/Git/
    ///  ExecutableExtensions.cs:15) holds
    ///  <c>static readonly Lazy&lt;Encoding&gt; _defaultOutputEncoding = new(() =&gt;
    ///  GitModule.SystemEncoding, false)</c> — <c>isThreadSafe: false</c>, i.e.
    ///  <see cref="LazyThreadSafetyMode.None"/>. Every core call that leaves
    ///  <c>outputEncoding</c> null dereferences it (<c>GetOutputAsync</c> line 97,
    ///  <c>ExecuteAsync</c> line 291) as its FIRST statement, so the very first two
    ///  git commands of the process, if they start on two different threads, race
    ///  inside that <c>Lazy</c> and one of them dies with
    ///  <c>InvalidOperationException</c> ("ValueFactory attempted to access the Value
    ///  property"). This port fans revision/status/ref loading out over
    ///  <c>Task.Run</c> from the very first refresh, so it hits that window; upstream
    ///  WinForms did not, which is why the field is still declared that way.</para>
    ///
    ///  <para>The core is NOT patched (it is shared with the Windows build). Instead
    ///  the first dereference happens here, single-threaded: the public member that
    ///  touches it is <c>ExecutableExtensions.GetOutput</c> — with
    ///  <c>outputEncoding: null</c> it runs <c>outputEncoding ??=
    ///  _defaultOutputEncoding.Value</c> before it even starts the process, so
    ///  <c>git --version</c> is merely the cheapest excuse to reach that line. Once
    ///  the <c>Lazy</c> holds a value, later concurrent reads are plain field reads
    ///  and safe.</para>
    ///
    ///  <para>Failure is swallowed: whatever <c>SystemEncodingReader</c> would throw,
    ///  it would throw on the first real git command too (and be cached by the
    ///  <c>Lazy</c> either way), so warming up adds no new failure mode.</para>
    /// </summary>
    private static void WarmUpCoreOutputEncoding()
    {
        try
        {
            _ = GitCommands.ExecutableExtensions.GetOutput(new GitCommands.Executable("git"), "--version");
        }
        catch
        {
            // See above: nothing here is worse than the first real git call.
        }
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
            // OVERLAY POPUPS, on both backends, and this is load-bearing for the UI zoom
            // (M86). By default a popup — a menu, a ComboBox dropdown, a context menu, a
            // tooltip — is a NATIVE window on Win32, i.e. its own visual root. That is
            // precisely why the per-window layout transform of M81 provably never scaled
            // popups (measured in M83: the popup's visual chain reaches the Window while
            // bypassing our host), and it is the reason M84 abandoned the transform.
            // Hosting popups in the window's own OverlayLayer puts their content back
            // INSIDE the zoom host, so a menu over a window at 125% is drawn at 125% too.
            //
            // The cost is real and is stated on the Appearance page rather than hidden: an
            // overlay popup is clipped to its window's bounds, so a dropdown near the
            // bottom edge of a SMALL dialog has less room to open into than a native one
            // would. Avalonia's positioner flips and constrains it to fit.
            //
            // Set on both option objects because UsePlatformDetect picks the backend at
            // run time: Win32 here, X11 on the Linux target. Options for a backend that is
            // not selected are simply never read.
            .With(new Win32PlatformOptions { OverlayPopups = true })
            // WmClass, explicitly: the first element of WM_CLASS is the INSTANCE name,
            // which Avalonia takes from the process. Started through the SDK — the way
            // run.sh did it — that is "dotnet", so a desktop shell looking for an icon
            // by WM_CLASS finds nothing of ours and shows its generic placeholder even
            // though _NET_WM_ICON is set on the window (measured: the app bar kept a
            // gear while xprop reported a 128x128 icon). Naming it here makes the pair
            // read "GitNext","GitNext" however the app was launched, and it is the name
            // packaging/gitnext.desktop declares as StartupWMClass.
            .With(new X11PlatformOptions { OverlayPopups = true, WmClass = "GitNext" })
            // Every `Browse…` in the app went through Avalonia's X11 StorageProvider, which on this
            // desktop never reaches the XDG portal at all: measured on a real Wayland/XWayland
            // session with a working portal (a manual `org.freedesktop.portal.FileChooser.OpenFile`
            // gets served), `dbus-monitor` sees *zero* traffic from this process and the picker
            // returns an empty list without throwing. Avalonia's managed dialogs are in-process, so
            // they work on both the real display and headless Xvfb.
            .UseManagedSystemDialogs()
            .WithInterFont()
            .LogToTrace();
}
