using System.Collections.Concurrent;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Everything a <see cref="JsonSettingsFile{T}"/> needs to know about the document it
///  stores, so the file machinery itself stays free of any one setting's shape.
/// </summary>
/// <param name="CreateDefault">The document a missing, empty or unreadable file means.</param>
/// <param name="Parse">Text to document. May throw or return <see langword="null"/>;
///  either is read as "corrupt", which collapses to <paramref name="CreateDefault"/>.</param>
/// <param name="Render">Document to the exact bytes to store.</param>
/// <param name="Sanitize">Clamps a document read from disk (or built by a mutation) to
///  what the UI can actually use. Applied on the way in AND on the way out, so a
///  hand-edited file cannot reach a surface and a broken mutation cannot reach the file.</param>
/// <param name="What">Names the write in the log line a failed background flush leaves —
///  "saving view preferences", "saving favorites", …</param>
/// <param name="Changed">Raised after every write attempt, on the thread that wrote.
///  Optional: only the settings with more than one editor need it.</param>
internal sealed record JsonSettingsModel<T>(
    Func<T> CreateDefault,
    Func<string, T?> Parse,
    Func<T, string> Render,
    Func<T, T> Sanitize,
    string What,
    Action? Changed = null)
    where T : class;

/// <summary>
///  One JSON settings file, written so that neither a second instance of the app nor a
///  process that dies mid-write can cost the user what they configured.
///
///  <para>Three separate hazards, three separate mechanisms:</para>
///  <list type="bullet">
///   <item><b>Torn file.</b> Every write goes to a temp file, is flushed to the platter
///    and is then <c>rename(2)</c>d over the target. A reader sees the whole old file or
///    the whole new one — never the truncated middle a plain <c>WriteAllText</c> leaves
///    when the process is killed, which every <c>Load</c> here reads as "no settings at
///    all" and which therefore silently resets everything.</item>
///   <item><b>Lost update.</b> <see cref="Update"/> takes a DELTA, not a document, and
///    replays it onto what the file says at the moment of writing. Two instances (or two
///    dialogs) editing different fields both keep their edit, where load-mutate-save
///    reverts whichever was composed first.</item>
///   <item><b>Interleaving.</b> A sidecar <c>.lock</c> file, held for the length of one
///    load-mutate-save, keeps two writers from reading the same state and merging onto
///    it. It is a sidecar because the write replaces the target inode by rename, so a
///    lock taken on the target would guard a file nobody writes to any more. On Linux
///    <c>FileShare.None</c> is an advisory <c>flock</c> the kernel drops when the holder
///    dies, so a crash cannot leave a lock nobody can break.</item>
///  </list>
///
///  <para>Nothing here ever blocks the calling thread on another process: the inline path
///  tries the lock once and, failing that, queues the delta for a background pump. So a
///  toggle stays instant on the UI thread even while another instance is writing.</para>
///
///  <para>Extracted from <c>ViewPrefsService</c>, where it was first built and where the
///  regression suite under <c>Tests/ViewPrefsRegression</c> demonstrates each of the three
///  hazards against a sabotaged build.</para>
/// </summary>
internal sealed class JsonSettingsFile<T>
    where T : class
{
    /// <summary>
    ///  How long the background pump waits for the cross-process lock before writing
    ///  without it. Generous, because nothing is waiting on it; finite, because a lock
    ///  held by something that is not going to release it must not cost the user a
    ///  setting. The lockless fall-back still re-reads and merges, so the worst case
    ///  degrades to the old race rather than to a silent discard.
    /// </summary>
    private const int PumpLockWaitMs = 5000;

    /// <summary>
    ///  One instance per resolved PATH, shared by every service object in the process. It
    ///  has to be shared: most call sites build their own <c>new XxxService()</c>, so
    ///  anything held per service instance would serialise nothing at all. Keyed by path
    ///  rather than by type, so a test that redirects <c>XDG_CONFIG_HOME</c> gets its own
    ///  queue instead of inheriting the previous file's.
    /// </summary>
    private static readonly ConcurrentDictionary<string, JsonSettingsFile<T>> Files =
        new(StringComparer.Ordinal);

    /// <summary>
    ///  Serialises this process's own writers. Taken with a zero timeout on the inline
    ///  path: a thread that cannot have it immediately queues its mutation rather than
    ///  standing in line, which is what keeps the UI thread free.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards <see cref="_pending"/>, <see cref="_inFlight"/> and <see cref="_draining"/>.</summary>
    private readonly object _queue = new();

    /// <summary>Queued and not yet picked up by the pump.</summary>
    private readonly List<Func<T, T>> _pending = [];

    /// <summary>Picked up and being written. Still visible to <see cref="Load"/>, so the
    /// preview does not blink off between the pick-up and the rename.</summary>
    private readonly List<Func<T, T>> _inFlight = [];

    /// <summary>Set whenever no pump is running, so <see cref="Flush"/> can wait for the
    /// queue to empty without ever joining the pump's task to its own thread.</summary>
    private readonly ManualResetEventSlim _idle = new(initialState: true);

    private readonly string _path;
    private readonly JsonSettingsModel<T> _model;

    /// <summary>Whether a pump is running. Flipped only under <see cref="_queue"/>, which
    /// is what makes "queue work, start a pump if none is running" atomic against the
    /// pump's own "queue is empty, stop" decision.</summary>
    private bool _draining;

    private JsonSettingsFile(string path, JsonSettingsModel<T> model)
    {
        _path = path;
        _model = model;
    }

    /// <summary>
    ///  The shared file object for <paramref name="path"/>, created on first use.
    ///
    ///  <para><paramref name="model"/> is used only when the object is created; a caller
    ///  passing a different one for the same path gets the first one, which is why every
    ///  caller passes a constant built from static members of a single service.</para>
    /// </summary>
    internal static JsonSettingsFile<T> For(string path, JsonSettingsModel<T> model)
        => Files.GetOrAdd(path, static (p, m) => new JsonSettingsFile<T>(p, m), model);

    /// <summary>The resolved path (for diagnostics and tests).</summary>
    internal string Path => _path;

    /// <summary>
    ///  The stored document, or defaults when the file is absent or unreadable.
    ///
    ///  <para>Any mutation this process has queued but not yet written is replayed onto
    ///  what the file says, so a surface that saves and immediately reads back sees its
    ///  own change even when the write was deferred. Replaying a mutation that has in
    ///  fact just landed is harmless: every one of them SETS a value rather than
    ///  incrementing one, which is idempotent.</para>
    /// </summary>
    internal T Load()
    {
        T doc = ReadFile();

        Func<T, T>[] queued;
        lock (_queue)
        {
            if (_inFlight.Count == 0 && _pending.Count == 0)
            {
                return doc;
            }

            queued = [.. _inFlight, .. _pending];
        }

        foreach (Func<T, T> entry in queued)
        {
            try
            {
                doc = entry(doc) ?? doc;
            }
            catch (Exception)
            {
                // A preview is a courtesy; a mutation that throws is the writer's problem.
            }
        }

        return _model.Sanitize(doc);
    }

    /// <summary>
    ///  Replaces the WHOLE file with <paramref name="doc"/>; best-effort (never throws).
    ///
    ///  <para>Prefer <see cref="Update"/>: this overload cannot merge, so it reverts any
    ///  field another writer changed meanwhile. It exists for the caller that genuinely
    ///  owns the entire document — a settings dialog whose OK button means "these are the
    ///  settings now". The object becomes the file's from this call on and must not be
    ///  mutated afterwards, since the write may be deferred.</para>
    /// </summary>
    internal void Save(T doc)
    {
        if (doc is null)
        {
            return;
        }

        Apply(_ => doc);
    }

    /// <summary>
    ///  Applies <paramref name="mutate"/> to the file's current contents and writes the
    ///  result back — the only safe way for one surface to update its own fields without
    ///  reverting a field another surface wrote meanwhile.
    ///
    ///  <para>The delegate is a DELTA, not a whole document, and that is what makes the
    ///  merge possible: it is handed a state read inside the interlock, at the last moment
    ///  before the write, so nothing the caller read earlier can go stale.</para>
    ///
    ///  <para><b>The one thing a caller must respect.</b> The delegate can run later, and
    ///  on another thread, than the call that queued it, and it may run more than once (a
    ///  preview inside <see cref="Load"/> replays it). So it must SET what it means to
    ///  save, out of state that still means the same thing when it runs — a local captured
    ///  before the call, not a loop variable and not something a later edit
    ///  reinterprets.</para>
    /// </summary>
    internal void Update(Action<T> mutate)
    {
        if (mutate is null)
        {
            return;
        }

        Apply(doc =>
        {
            mutate(doc);
            return doc;
        });
    }

    /// <summary>
    ///  Waits until every deferred write of this file has reached the disk, and reports
    ///  whether it got there within <paramref name="timeout"/>.
    ///
    ///  <para>For tests and for a deliberate shutdown only. The UI never needs it: a write
    ///  is deferred only when another instance holds the lock, and the pump finishes on
    ///  its own. It BLOCKS, so it must not be called on the UI thread.</para>
    /// </summary>
    internal bool Flush(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);

        while (true)
        {
            lock (_queue)
            {
                if (!_draining && _pending.Count == 0)
                {
                    return true;
                }
            }

            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return false;
            }

            // Waits on the pump's idle signal rather than on its Task: a task started by
            // someone else is exactly the shape that deadlocks when it needs the thread
            // doing the waiting (Async.Forget says the same). Re-checked from the top
            // because the pump can go idle and be restarted between two waits.
            _ = _idle.Wait((int)Math.Min(remaining, 25));
        }
    }

    // ------------------------------------------------------------------ writing

    // The one entry point of the write path: inline when the file is free, queued when it
    // is not. Never throws, never waits on another process.
    private void Apply(Func<T, T> entry)
    {
        if (TryApplyInline(entry))
        {
            // Outside the interlock on purpose: a subscriber is arbitrary code, and one
            // that wrote back from here would deadlock against a lock we still held.
            // Announced even if the write failed — the in-memory intent still changed.
            _model.Changed?.Invoke();
            return;
        }

        Defer(entry);
    }

    private bool TryApplyInline(Func<T, T> entry)
    {
        lock (_queue)
        {
            // Order before speed: a write that jumped the queue would be overwritten a
            // moment later by the older mutation the pump is about to replay on top of
            // it, which for two edits of the SAME field is the lost update again.
            if (_draining || _pending.Count > 0)
            {
                return false;
            }
        }

        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            // A single non-blocking attempt. Contention means another instance is in its
            // own load-mutate-save; waiting for it here would be waiting on a process we
            // do not control, on whatever thread called us — including the UI one.
            using FileStream? guard = TryLock();
            if (guard is null)
            {
                return false;
            }

            WriteMerged([entry]);
            return true;
        }
        catch (Exception)
        {
            // Taking the lock itself failed in a way retrying cannot help (no permission
            // to create the sidecar, say). Deferring would only fail again, slower.
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Defer(Func<T, T> entry)
    {
        lock (_queue)
        {
            _pending.Add(entry);

            if (!_draining)
            {
                _draining = true;
                _idle.Reset();

                // Started under the lock so that the flag, the signal and the queue can
                // never disagree. Task.Run only schedules; the pump's first act is to take
                // this same lock, so it does not run in here.
                Task.Run(Drain).Forget(_model.What);
            }
        }
    }

    // The background writer. Loops rather than handling one batch, so mutations queued
    // while it was writing do not each pay for a new task. Never throws: it is the body of
    // a fire-and-forget task, and an exception escaping one of those kills the process.
    private void Drain()
    {
        while (true)
        {
            int batch;
            lock (_queue)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    _idle.Set();
                    return;
                }

                _inFlight.AddRange(_pending);
                _pending.Clear();
                batch = _inFlight.Count;
            }

            _gate.Wait();
            try
            {
                // Null means the wait ran out; see PumpLockWaitMs for why that writes
                // anyway rather than dropping the user's setting.
                using FileStream? guard = TryLock(PumpLockWaitMs);
                WriteMerged(_inFlight);
            }
            catch (Exception)
            {
                // Persistence is best-effort by design, here as everywhere in this class.
            }
            finally
            {
                _gate.Release();
            }

            lock (_queue)
            {
                _inFlight.Clear();
            }

            // One event per call that was queued, matching what an inline write raises.
            for (int i = 0; i < batch; i++)
            {
                _model.Changed?.Invoke();
            }
        }
    }

    // The load-mutate-save critical section itself, called with the interlock held. The
    // re-read is the point: each entry is applied as a delta onto whatever is on disk NOW,
    // so a field another instance wrote while this mutation was being composed survives
    // instead of being reverted. Never throws.
    private void WriteMerged(IReadOnlyList<Func<T, T>> entries)
    {
        try
        {
            T doc = ReadFile();

            foreach (Func<T, T> entry in entries)
            {
                try
                {
                    doc = entry(doc) ?? doc;
                }
                catch (Exception)
                {
                    // One surface's broken mutation must not cost the other surfaces in
                    // the same batch their setting.
                }
            }

            WriteAtomic(_model.Sanitize(doc));
        }
        catch (Exception)
        {
            // Persistence is best-effort; a failure must not crash the app.
        }
    }

    // Write-then-rename. rename(2) is atomic, so a reader — this process, another
    // instance, or a person with an editor — sees either the whole old file or the whole
    // new one, never the half-written middle that a plain WriteAllText leaves behind when
    // the process dies mid-write. Load() reads such a middle as "no settings at all",
    // which is how a truncated file silently resets everything the user had configured.
    private void WriteAtomic(T doc)
    {
        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string text = _model.Render(doc);

        // Named per process and thread, not randomly: a run that is killed at the wrong
        // instant leaves at most one leftover per writer, which the next write of the same
        // writer truncates, instead of a growing litter of temp files.
        string temp = $"{_path}.tmp-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}";

        try
        {
            using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream))
            {
                writer.Write(text);
                writer.Flush();

                // The rename is only atomic with respect to the bytes the kernel already
                // has. Forcing them out first is what makes the guarantee survive a
                // machine that loses power rather than only a process that dies.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // Nothing else to do about it; the caller swallows either way.
            }

            throw;
        }
    }

    // The cross-process interlock. Held only for the duration of one load-mutate-save;
    // released by the kernel if this process dies holding it, so there is no stale lock to
    // break.
    private FileStream? TryLock(int waitMs = 0)
    {
        string? dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string lockPath = _path + ".lock";
        long deadline = Environment.TickCount64 + waitMs;
        int backoff = 1;

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException)
            {
                // Held by somebody else; the only failure worth retrying.
            }
            catch (UnauthorizedAccessException)
            {
                // No lock is obtainable here at all (read-only config directory). The
                // merge in WriteMerged is then the whole defence, which is still better
                // than refusing to save.
                return null;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            Thread.Sleep(backoff);
            backoff = Math.Min(backoff * 2, 16);
        }
    }

    // The file exactly as it is on disk, with no queued mutation replayed over it — what a
    // merge has to start from.
    private T ReadFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                T? loaded = _model.Parse(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    return _model.Sanitize(loaded);
                }
            }
        }
        catch (Exception)
        {
            // Missing/corrupt/unreadable → defaults.
        }

        return _model.CreateDefault();
    }
}

/// <summary>
///  Where the port's JSON settings files live. One place, so a new store cannot invent a
///  different directory and so the redirection every harness relies on
///  (<c>XDG_CONFIG_HOME</c>) has a single implementation.
/// </summary>
internal static class SettingsPaths
{
    /// <summary>
    ///  <c>$XDG_CONFIG_HOME/GitExtensions.Avalonia/<paramref name="fileName"/></c>, with
    ///  the platform's application-data directory and then <c>~/.config</c> as fall-backs.
    /// </summary>
    internal static string Resolve(string fileName)
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return System.IO.Path.Combine(baseDir, "GitExtensions.Avalonia", fileName);
    }
}
