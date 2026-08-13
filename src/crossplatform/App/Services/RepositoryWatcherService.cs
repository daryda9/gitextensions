namespace GitExtensions.Avalonia.Services;

/// <summary>What made the watcher ask for a refresh.</summary>
public enum RepositoryChangeKind
{
    /// <summary>A tracked/untracked file under the working tree changed.</summary>
    WorkTree,

    /// <summary>Something inside the git directory changed (HEAD, refs, index…).</summary>
    GitDir,

    /// <summary>The five-minute safety net fired; nothing specific was observed.</summary>
    Periodic,
}

/// <summary>
///  Watches a repository for changes made behind the app's back (a commit from a
///  shell, a checkout, a pull run in a terminal) and raises a single, debounced
///  <see cref="Changed"/> signal so the window can refresh itself without the user
///  pressing F5.
///
///  <para>This is the Linux/Avalonia counterpart of the original
///  <c>GitUI/CommandsDialogs/BrowseDialog/GitStatusMonitor.cs</c>: two
///  <see cref="FileSystemWatcher"/>s (work tree + git directory), a debounce on
///  file events, a floor on how often a refresh may run, and a periodic refresh as
///  a safety net. It deliberately does <b>no git work of its own</b> — it only says
///  "something moved"; deciding what to reload is the window's job.</para>
///
///  <para><b>Threading.</b> <see cref="Changed"/> is raised on a thread-pool thread
///  (the timer callback), never on the UI thread, and the service never runs git.
///  Subscribers must marshal their own UI mutations. Nothing here throws: a watcher
///  that cannot be created degrades to the periodic timer and reports it through
///  <see cref="Degraded"/>.</para>
///
///  <para><b>Loops.</b> The app's own git commands write into the repository, so a
///  naive watcher would refresh, notice its own writes, and refresh again forever.
///  Two guards prevent that: <see cref="Suspend"/> (held for the duration of an
///  operation the app itself started) and <see cref="NotifyRefreshed"/> (called by
///  the window after every refresh, which drops the events accumulated so far and
///  keeps a short settle window in which new ones are ignored).</para>
/// </summary>
public sealed class RepositoryWatcherService : IDisposable
{
    // Trailing debounce applied to file events: mirrors GitStatusMonitor's
    // FileChangedUpdateDelay (1 s). Re-armed by every event, so a burst collapses
    // into one refresh.
    private const int DebounceMs = 1000;

    // …but a burst that never stops (a `git checkout` across thousands of files
    // keeps inotify busy for seconds) must not postpone the refresh forever, so the
    // debounce is capped at this long after the FIRST event of the burst.
    private const int MaxBurstMs = 4000;

    // Floor between two automatic refreshes. GitStatusMonitor uses 30 s because it
    // pays for a full `git status`; the port's refresh is cheaper and the user
    // expects the window to track a shell within a couple of seconds, so the floor
    // is shorter here.
    private const int MinIntervalMs = 5000;

    // After the window reports a refresh, ignore events for this long: the refresh
    // itself (and the git command that preceded it) touches the repository, and
    // those echoes must not schedule another refresh.
    private const int SettleMs = 1500;

    // Safety net when the watchers miss something (GitStatusMonitor:
    // PeriodicUpdateInterval). Shortened to a minute when watching failed
    // altogether, which is then the only automatic refresh left.
    private const int PeriodicMs = 5 * 60 * 1000;
    private const int PeriodicDegradedMs = 60 * 1000;

    // Polling clock, like GitStatusMonitor's 100 ms Forms timer: all the deadlines
    // below are evaluated on its tick, which keeps the event handlers trivial
    // (they only stamp a deadline) and the whole thing free of timer churn.
    private const int TickMs = 250;

    private readonly object _gate = new();
    private readonly Timer _clock;

    private FileSystemWatcher? _workTreeWatcher;
    private FileSystemWatcher? _gitDirWatcher;
    private FileSystemWatcher? _commonDirWatcher;

    private string? _repoPath;
    private string? _gitDir;

    private bool _pending;
    private RepositoryChangeKind _pendingKind;
    private int _dueTicks;
    private int _firstEventTicks;
    private int _earliestTicks;
    private int _ignoreUntilTicks;
    private int _nextPeriodicTicks;
    private int _suspendCount;
    private bool _degraded;
    private bool _disposed;

    public RepositoryWatcherService()
    {
        _nextPeriodicTicks = Environment.TickCount + PeriodicMs;
        _clock = new Timer(_ => Tick(), state: null, TickMs, TickMs);
    }

    /// <summary>
    ///  Raised, off the UI thread, when the repository should be reloaded. Never
    ///  raised more often than once every <see cref="MinIntervalMs"/>.
    /// </summary>
    public event Action<RepositoryChangeKind>? Changed;

    /// <summary>
    ///  Raised when file watching could not be established (typically the inotify
    ///  per-user watch limit on a very large repository). The argument is a
    ///  ready-to-show, human-readable explanation; automatic refresh continues at
    ///  the degraded periodic interval and F5 keeps working.
    /// </summary>
    public event Action<string>? Degraded;

    /// <summary>True when the file watchers are live (false = periodic only).</summary>
    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return _workTreeWatcher is not null && !_degraded;
            }
        }
    }

    /// <summary>
    ///  Resolves the real git directory of <paramref name="repoPath"/>. Usually
    ///  <c>&lt;repo&gt;/.git</c>, but in a linked worktree (and for a submodule)
    ///  <c>.git</c> is a <i>file</i> holding <c>gitdir: &lt;path&gt;</c>, possibly
    ///  relative to the working tree. Returns null when nothing resolves.
    /// </summary>
    public static string? ResolveGitDir(string repoPath)
    {
        try
        {
            string dotGit = Path.Combine(repoPath, ".git");
            if (Directory.Exists(dotGit))
            {
                return dotGit;
            }

            if (!File.Exists(dotGit))
            {
                return null;
            }

            const string Prefix = "gitdir:";
            string text = File.ReadAllText(dotGit).Trim();
            if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return null;
            }

            string target = text[Prefix.Length..].Trim();
            if (!Path.IsPathRooted(target))
            {
                target = Path.GetFullPath(Path.Combine(repoPath, target));
            }

            return Directory.Exists(target) ? Path.GetFullPath(target) : null;
        }
        catch
        {
            // An unreadable .git is not this service's problem to report.
            return null;
        }
    }

    /// <summary>
    ///  Starts (or moves) the watch to <paramref name="repoPath"/>. Safe to call
    ///  repeatedly; a null/absent path just stops watching.
    /// </summary>
    public void Watch(string? repoPath)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            StopWatchersLocked();
            _pending = false;
            _degraded = false;
            _repoPath = null;
            _gitDir = null;

            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            {
                return;
            }

            _repoPath = Path.GetFullPath(repoPath);
            _gitDir = ResolveGitDir(_repoPath);
            _nextPeriodicTicks = Environment.TickCount + PeriodicMs;
        }

        // Creating the watchers can block briefly (inotify walks the tree) and can
        // fail, so it happens outside the lock and outside the caller's thread.
        _ = Task.Run(StartWatchers);
    }

    /// <summary>Stops watching and forgets the repository.</summary>
    public void Stop() => Watch(null);

    /// <summary>
    ///  Suppresses automatic refreshes for as long as the returned scope is alive.
    ///  Wrap every git command the app itself starts: the writes it makes are
    ///  already followed by an explicit refresh, and reacting to them as if they
    ///  came from outside would refresh the window mid-operation (and, for a
    ///  refresh that itself touches the index, forever).
    /// </summary>
    public IDisposable Suspend()
    {
        lock (_gate)
        {
            _suspendCount++;
        }

        return new Scope(this);
    }

    /// <summary>
    ///  Told by the window after it has reloaded: drops whatever piled up (it is
    ///  already reflected in what was just loaded) and holds off for a moment so
    ///  the refresh's own filesystem echoes do not schedule the next one.
    /// </summary>
    public void NotifyRefreshed()
    {
        lock (_gate)
        {
            int now = Environment.TickCount;
            _pending = false;
            _ignoreUntilTicks = now + SettleMs;
            _earliestTicks = now + MinIntervalMs;
            _nextPeriodicTicks = now + (_degraded ? PeriodicDegradedMs : PeriodicMs);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopWatchersLocked();
        }

        _clock.Dispose();
    }

    // ---- watcher plumbing --------------------------------------------------------

    private void StartWatchers()
    {
        string repo;
        string? gitDir;
        lock (_gate)
        {
            if (_disposed || _repoPath is null)
            {
                return;
            }

            repo = _repoPath;
            gitDir = _gitDir;
        }

        FileSystemWatcher? work = null;
        FileSystemWatcher? git = null;
        FileSystemWatcher? common = null;
        string? failure = null;

        try
        {
            work = Create(repo, NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size);
            work.Changed += OnWorkTreeEvent;
            work.Created += OnWorkTreeEvent;
            work.Deleted += OnWorkTreeEvent;
            work.Renamed += OnWorkTreeEvent;
            work.Error += OnWatcherError;
            work.EnableRaisingEvents = true;

            // The git dir only needs its own watcher when it is NOT inside the work
            // tree (linked worktree, submodule): otherwise the work-tree watcher
            // already reports it and a second one would double every event — the
            // same condition GitStatusMonitor applies.
            if (gitDir is not null && !IsUnder(gitDir, repo))
            {
                git = Create(gitDir, NotifyFilters.FileName | NotifyFilters.LastWrite);
                git.Changed += OnGitDirEvent;
                git.Created += OnGitDirEvent;
                git.Deleted += OnGitDirEvent;
                git.Error += OnWatcherError;
                git.EnableRaisingEvents = true;

                // In a linked worktree, refs/ and the object store live in the main
                // repository's git dir (named by "commondir"), so branch and tag
                // changes only show up there.
                string? commonDir = ResolveCommonDir(gitDir);
                if (commonDir is not null && !IsUnder(commonDir, repo) && !IsUnder(commonDir, gitDir))
                {
                    common = Create(commonDir, NotifyFilters.FileName | NotifyFilters.LastWrite);
                    common.Changed += OnGitDirEvent;
                    common.Created += OnGitDirEvent;
                    common.Deleted += OnGitDirEvent;
                    common.Error += OnWatcherError;
                    common.EnableRaisingEvents = true;
                }
            }
        }
        catch (Exception ex)
        {
            // On Linux every watched directory costs one inotify watch, and the
            // per-user budget (/proc/sys/fs/inotify/max_user_watches) is shared with
            // every other program; a huge tree can simply run out. That is a
            // degradation, not a failure: drop the watchers and lean on the timer.
            Dispose(work);
            Dispose(git);
            Dispose(common);
            work = git = common = null;
            failure = ex.Message;
        }

        bool report;
        lock (_gate)
        {
            if (_disposed || _repoPath != repo)
            {
                Dispose(work);
                Dispose(git);
                Dispose(common);
                return;
            }

            _workTreeWatcher = work;
            _gitDirWatcher = git;
            _commonDirWatcher = common;
            _degraded = failure is not null;
            report = _degraded;
            if (_degraded)
            {
                _nextPeriodicTicks = Environment.TickCount + PeriodicDegradedMs;
            }
        }

        if (report)
        {
            Raise(Degraded, "Automatic refresh is limited on this repository (the system ran out of "
                + $"file-watch slots: {failure}). The window still refreshes every minute — press F5 for an immediate refresh.");
        }
    }

    private static FileSystemWatcher Create(string path, NotifyFilters filters)
        => new(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = filters,

            // A `git checkout` of a large branch can outrun the default 8 KB
            // kernel buffer and lose events (Error → overflow); a bigger buffer
            // makes that rare, and the overflow path recovers anyway.
            InternalBufferSize = 64 * 1024,
        };

    private static void Dispose(FileSystemWatcher? watcher)
    {
        try
        {
            watcher?.Dispose();
        }
        catch
        {
            // Nothing useful to do while tearing down.
        }
    }

    private void StopWatchersLocked()
    {
        Dispose(_workTreeWatcher);
        Dispose(_gitDirWatcher);
        Dispose(_commonDirWatcher);
        _workTreeWatcher = null;
        _gitDirWatcher = null;
        _commonDirWatcher = null;
    }

    // "commondir" holds the main repository's git dir for a linked worktree,
    // usually as a path relative to the worktree's own git dir.
    private static string? ResolveCommonDir(string gitDir)
    {
        try
        {
            string marker = Path.Combine(gitDir, "commondir");
            if (!File.Exists(marker))
            {
                return null;
            }

            string target = File.ReadAllText(marker).Trim();
            if (target.Length == 0)
            {
                return null;
            }

            if (!Path.IsPathRooted(target))
            {
                target = Path.GetFullPath(Path.Combine(gitDir, target));
            }

            return Directory.Exists(target) ? Path.GetFullPath(target) : null;
        }
        catch
        {
            return null;
        }
    }

    // ---- event filtering ---------------------------------------------------------

    private void OnWorkTreeEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            string path = e.FullPath;
            string? gitDir = _gitDir;

            // The work-tree watcher also sees the repository's own .git; route
            // those through the stricter git-dir rules instead of treating them as
            // ordinary file edits.
            if (gitDir is not null && IsUnder(path, gitDir))
            {
                OnGitDirEvent(sender, e);
                return;
            }

            if (IsWorkTreeNoise(path))
            {
                return;
            }

            Schedule(RepositoryChangeKind.WorkTree);
        }
        catch
        {
            // A watcher callback must never throw: it would take the process down.
        }
    }

    private void OnGitDirEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (IsGitDirNoise(e.FullPath))
            {
                return;
            }

            Schedule(RepositoryChangeKind.GitDir);
        }
        catch
        {
            // See above.
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow: events were lost, so we know something happened but not
        // what. GitStatusMonitor schedules a refresh and so do we.
        try
        {
            Schedule(RepositoryChangeKind.GitDir);
        }
        catch
        {
            // Never throw from a watcher callback.
        }
    }

    /// <summary>
    ///  Work-tree paths that are pure noise. Nested repositories (submodules) churn
    ///  their own <c>.git</c>; editors and build tools drop temporary files.
    /// </summary>
    internal static bool IsWorkTreeNoise(string fullPath)
    {
        // Windows reports these paths with backslashes, so every separator-sensitive
        // test below runs on a slash-normalised copy — as IsGitDirNoise already does.
        // Without it none of them matched on Windows and the whole of .git was
        // classified as ordinary work-tree churn.
        string path = fullPath.Replace('\\', '/');
        string name = Path.GetFileName(path);

        // A nested .git (submodule) and its index lock: GitStatusMonitor skips both.
        if (name is ".git")
        {
            return true;
        }

        if (Contains(path, "/.git/"))
        {
            // Inside a nested repository: only the interesting files matter, and
            // the same git-dir rules decide.
            return IsGitDirNoise(path);
        }

        return IsTempName(name);
    }

    /// <summary>
    ///  Git-directory paths that must not trigger a refresh. The object store and
    ///  the lock/temp files are written constantly by every git command (including
    ///  the read-only ones the refresh itself runs), so reacting to them is both
    ///  pointless and the classic way to build an endless refresh loop.
    /// </summary>
    internal static bool IsGitDirNoise(string fullPath)
    {
        string path = fullPath.Replace('\\', '/');
        string name = Path.GetFileName(path);

        // index.lock and every other *.lock: transient, and the real change is
        // reported when the lock is renamed onto its target.
        if (name.EndsWith(".lock", StringComparison.Ordinal))
        {
            return true;
        }

        if (IsTempName(name))
        {
            return true;
        }

        // Loose objects and packs: thousands of writes per fetch, and never
        // interesting on their own — refs are what make objects visible.
        if (Contains(path, "/objects/") || Contains(path, "/lfs/"))
        {
            return true;
        }

        // The fsmonitor daemon's socket/cookies, and submodule git dirs (each
        // submodule has its own watcher when it is opened as the repository).
        if (Contains(path, "/fsmonitor--daemon/") || Contains(path, "/modules/"))
        {
            return true;
        }

        // Written by the app's own commit flow before the commit exists; the commit
        // itself shows up as a HEAD/refs/logs change a moment later.
        if (name is "COMMIT_EDITMSG" or "GITGUI_EDITMSG" or "gitk.cache" or "FETCH_HEAD")
        {
            return true;
        }

        return false;
    }

    private static bool IsTempName(string name)
        => name.StartsWith("tmp_", StringComparison.Ordinal)
        || name.StartsWith(".tmp", StringComparison.Ordinal)
        || name.StartsWith("~", StringComparison.Ordinal)
        || name.EndsWith("~", StringComparison.Ordinal)
        || name.EndsWith(".swp", StringComparison.Ordinal)
        || name.EndsWith(".tmp", StringComparison.Ordinal);

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.Ordinal);

    // Containment test on the NATIVE separator, and case-insensitively on Windows.
    // The first version compared slash-separated, case-sensitive strings, so on
    // Windows ".git" never looked like it was under the work tree: the git dir got a
    // second watcher of its own (every event twice) and, worse, OnWorkTreeEvent
    // stopped routing .git writes through the git-dir noise filter — so every loose
    // object, lock file and FETCH_HEAD our own refresh writes scheduled another
    // refresh, which is the self-reloading window reported on Windows.
    private static bool IsUnder(string path, string root)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Equals(r, comparison)
            || p.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }

    // ---- debounce ----------------------------------------------------------------

    private void Schedule(RepositoryChangeKind kind)
    {
        lock (_gate)
        {
            if (_disposed || _repoPath is null || _suspendCount > 0)
            {
                return;
            }

            int now = Environment.TickCount;

            // Inside the settle window that follows one of our own refreshes.
            if (now - _ignoreUntilTicks < 0)
            {
                return;
            }

            if (!_pending)
            {
                _pending = true;
                _pendingKind = kind;
                _firstEventTicks = now;
            }
            else if (kind == RepositoryChangeKind.GitDir)
            {
                // A git-dir change is the more informative of the two.
                _pendingKind = kind;
            }

            // Trailing debounce, capped so an unending burst still refreshes.
            int due = now + DebounceMs;
            int cap = _firstEventTicks + MaxBurstMs;
            _dueTicks = due - cap > 0 ? cap : due;
        }
    }

    private void Tick()
    {
        RepositoryChangeKind kind;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            int now = Environment.TickCount;

            if (_suspendCount > 0)
            {
                // Keep the periodic deadline ahead of an operation in progress.
                _nextPeriodicTicks = now + (_degraded ? PeriodicDegradedMs : PeriodicMs);
                return;
            }

            if (!_pending && _repoPath is not null && now - _nextPeriodicTicks >= 0)
            {
                _pending = true;
                _pendingKind = RepositoryChangeKind.Periodic;
                _firstEventTicks = now;
                _dueTicks = now;
            }

            if (!_pending || now - _dueTicks < 0 || now - _earliestTicks < 0)
            {
                return;
            }

            kind = _pendingKind;
            _pending = false;

            // The window calls NotifyRefreshed when it is done, which re-arms these;
            // seed them here too so a subscriber that forgets cannot spin.
            _earliestTicks = now + MinIntervalMs;
            _nextPeriodicTicks = now + (_degraded ? PeriodicDegradedMs : PeriodicMs);
        }

        Raise(Changed, kind);
    }

    private static void Raise<T>(Action<T>? handler, T argument)
    {
        try
        {
            handler?.Invoke(argument);
        }
        catch
        {
            // A refresh path must never throw (HANDOFF §3).
        }
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (_suspendCount > 0)
            {
                _suspendCount--;
            }

            if (_suspendCount == 0)
            {
                // Everything the operation wrote is about to be picked up by the
                // caller's own refresh; drop it and settle.
                int now = Environment.TickCount;
                _pending = false;
                _ignoreUntilTicks = now + SettleMs;
            }
        }
    }

    private sealed class Scope(RepositoryWatcherService owner) : IDisposable
    {
        private RepositoryWatcherService? _owner = owner;

        public void Dispose()
        {
            RepositoryWatcherService? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Resume();
        }
    }
}
