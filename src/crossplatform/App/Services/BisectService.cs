using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Result of a bisect operation: success flag plus the full git output. On a
///  successful mark git prints the next commit to test (or the final
///  "first bad commit"); that text is surfaced to the user via
///  <see cref="Output"/>.
/// </summary>
public sealed record BisectResult(bool Success, string Output);

/// <summary>
///  Everything a control surface needs to know about the bisect session of one
///  repository, so that no button has to be shown without the data behind it.
///
///  <para><b>Why this exists.</b> Upstream's own bisect surfaces
///  (<c>FormBisect.UpdateButtonsState:27-35</c>,
///  <c>RevisionGridControl.cs:2256-2261</c>,
///  <c>InteractiveGitActionControl.RefreshBisect:47-61</c>) ask exactly one
///  question — <c>GitModule.InTheMiddleOfBisect()</c>, i.e. does
///  <c>.git/BISECT_START</c> exist — and show no progress at all. That is enough to
///  gate the actions but not to tell the user how far along the search is, which is
///  the one thing git itself prints on every mark ("Bisecting: 3 revisions left to
///  test after this (roughly 2 steps)") and then throws away. This record keeps that
///  information queryable at any moment.</para>
///
///  <para><b>Where the numbers come from.</b> Not from parsing git's message: that
///  text is localised (on this machine git speaks Italian — "Bisezione in corso: 3
///  revisioni rimanenti…"), so scraping it would break outside an English locale.
///  They come from <c>git rev-list --bisect-vars</c>, which prints the same figures
///  as machine-readable <c>name=value</c> lines and is what git's own
///  <c>bisect--helper</c> uses.</para>
/// </summary>
/// <param name="InProgress">
///  Whether a session is open — <c>.git/BISECT_START</c> exists, the exact test
///  upstream's <c>InTheMiddleOfBisect()</c> makes.
/// </param>
/// <param name="BadKnown">A <c>refs/bisect/bad</c> ref exists: a bad commit was marked.</param>
/// <param name="GoodKnown">At least one <c>refs/bisect/good-*</c> ref exists.</param>
/// <param name="RevisionsLeft">
///  <c>bisect_nr</c>: commits still to test after the current one. Meaningful only
///  once <see cref="Ready"/> — before that git cannot bound the range.
/// </param>
/// <param name="StepsLeft"><c>bisect_steps</c>: git's own estimate of remaining marks.</param>
/// <param name="Candidates">
///  <c>bisect_all</c>: the size of the candidate range including its endpoints. It
///  collapses to 1 exactly when the search is over, which is how
///  <see cref="Finished"/> is decided.
/// </param>
/// <param name="CulpritHash">
///  The commit <c>refs/bisect/bad</c> points at. While the search runs this is
///  merely the current upper bound; once <see cref="Finished"/> it is the first bad
///  commit — the answer git prints as "&lt;hash&gt; is the first bad commit".
/// </param>
public sealed record BisectSession(
    bool InProgress,
    bool BadKnown = false,
    bool GoodKnown = false,
    int RevisionsLeft = 0,
    int StepsLeft = 0,
    int Candidates = 0,
    string? CulpritHash = null)
{
    /// <summary>No session open — also the answer for "no repository" and for any failure.</summary>
    public static readonly BisectSession None = new(false);

    /// <summary>
    ///  True once both ends of the range are known, i.e. git has something to
    ///  bisect and the counts below are meaningful. Until then git is "waiting for
    ///  good and bad commits" and there is no progress to report.
    /// </summary>
    public bool Ready => InProgress && BadKnown && GoodKnown;

    /// <summary>
    ///  True when the search has converged and <see cref="CulpritHash"/> is the
    ///  first bad commit. The session is still open at this point — git keeps it
    ///  open until <c>git bisect reset</c> — so a surface should stop offering
    ///  good/bad/skip and offer the reset instead.
    /// </summary>
    public bool Finished => Ready && Candidates == 1;

    /// <summary>
    ///  True when there is a remaining-work figure worth displaying. Requires
    ///  <see cref="RevisionsLeft"/> above zero: git's last step reports
    ///  "0 revisions left to test after this (roughly 0 steps)", which is accurate but
    ///  reads as if nothing were left to do — a surface should say "this is the last
    ///  commit to test" instead.
    /// </summary>
    public bool HasProgress => Ready && !Finished && RevisionsLeft > 0;
}

/// <summary>
///  Drives <c>git bisect</c> by reusing the Git Extensions core
///  (<see cref="GitModule"/>) via <see cref="GitContext.CreateModule"/>. All
///  methods are synchronous and are meant to be called off the UI thread,
///  mirroring the other Avalonia services (e.g. <see cref="WorktreeService"/>).
/// </summary>
public sealed class BisectService
{
    /// <summary>Begins a bisect session (<c>git bisect start</c>).</summary>
    public BisectResult Start(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "start" });

    /// <summary>Marks <paramref name="hash"/> as good (<c>git bisect good &lt;hash&gt;</c>).</summary>
    public BisectResult MarkGood(string repoPath, string hash)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "good", hash });

    /// <summary>Marks <paramref name="hash"/> as bad (<c>git bisect bad &lt;hash&gt;</c>).</summary>
    public BisectResult MarkBad(string repoPath, string hash)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "bad", hash });

    /// <summary>Skips <paramref name="hash"/> (<c>git bisect skip &lt;hash&gt;</c>).</summary>
    public BisectResult Skip(string repoPath, string hash)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "skip", hash });

    /// <summary>Ends the bisect session and restores HEAD (<c>git bisect reset</c>).</summary>
    public BisectResult Reset(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "reset" });

    /// <summary>
    ///  The session transcript (<c>git bisect log</c>) — the list of marks made so
    ///  far, in the replayable form git emits. Fails (with git's message in
    ///  <see cref="BisectResult.Output"/>) when no session is open, which is why
    ///  callers should offer it only while <see cref="InTheMiddleOfBisect"/>.
    /// </summary>
    public BisectResult Log(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("bisect") { "log" });

    /// <summary>
    ///  True when a bisect session is open, by the same test upstream makes —
    ///  <c>.git/BISECT_START</c> exists (<c>GitModule.InTheMiddleOfBisect</c>,
    ///  <c>GitModule.cs:1968-1971</c>). Never throws: an unreadable git directory
    ///  answers false, which merely leaves the mark actions disabled instead of
    ///  offering an action that would fail.
    /// </summary>
    public bool InTheMiddleOfBisect(string repoPath)
    {
        try
        {
            string? gitDir = RepositoryWatcherService.ResolveGitDir(repoPath);
            return gitDir is { Length: > 0 }
                && File.Exists(Path.Combine(gitDir, "BISECT_START"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///  Reads the whole state of the bisect session: whether it is open, whether
    ///  both ends of the range are known yet, how much searching is left, and — once
    ///  it has converged — which commit is the first bad one. See
    ///  <see cref="BisectSession"/> for where each figure comes from.
    ///
    ///  <para>Costs at most two git processes and only when a session is actually
    ///  open, so it is cheap enough for the repository-changed notification but must
    ///  not run on the UI thread (like every other method here).</para>
    ///
    ///  <para>Degrades one field at a time rather than all-or-nothing: if the ref
    ///  listing works but <c>--bisect-vars</c> does not, the caller still learns that
    ///  a session is open and which ends are known, and simply has no counter to
    ///  show. Never throws.</para>
    /// </summary>
    public BisectSession GetSession(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !InTheMiddleOfBisect(repoPath))
        {
            return BisectSession.None;
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);

            // The bisect refs are the range's endpoints: refs/bisect/bad is the
            // single upper bound, refs/bisect/good-<sha> one ref per commit marked
            // good. Enumerated rather than globbed because the arguments never go
            // through a shell — a literal "refs/bisect/good-*" would reach git
            // unexpanded.
            //
            // The format asks for the ref NAME ONLY, deliberately. Every argument
            // built here is flattened into a single ProcessStartInfo.Arguments string
            // (Executable.cs:59,136), so an argument that contains a space is split
            // back into two by the runtime's parser: "--format=%(refname)
            // %(objectname)" arrives as two arguments, git reads the second as
            // another ref pattern, and the hash column silently never appears. The
            // bad commit's hash is therefore resolved on its own, below.
            ExecutionResult refs = module.GitExecutable.Execute(
                new GitArgumentBuilder("for-each-ref")
                {
                    "--format=%(refname)",
                    "refs/bisect",
                },
                throwOnErrorExit: false);

            bool badKnown = false;
            List<string> goodRefs = [];

            foreach (string raw in refs.StandardOutput.Split('\n'))
            {
                string name = raw.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (name.Equals("refs/bisect/bad", StringComparison.Ordinal))
                {
                    badKnown = true;
                }
                else if (name.StartsWith("refs/bisect/good-", StringComparison.Ordinal))
                {
                    goodRefs.Add(name);
                }
            }

            string? bad = null;
            if (badKnown)
            {
                ExecutionResult resolved = module.GitExecutable.Execute(
                    new GitArgumentBuilder("rev-parse") { "refs/bisect/bad" },
                    throwOnErrorExit: false);

                if (resolved.ExitedSuccessfully)
                {
                    string hash = resolved.StandardOutput.Trim();
                    bad = hash.Length > 0 ? hash : null;
                }
            }

            if (!badKnown || goodRefs.Count == 0)
            {
                // git is still "waiting for good and bad commits": the range is
                // unbounded, so there is no honest number to report yet.
                return new BisectSession(true, badKnown, goodRefs.Count > 0, CulpritHash: bad);
            }

            GitArgumentBuilder vars = new("rev-list")
            {
                "--bisect-vars",
                "refs/bisect/bad",
                "--not",
            };

            foreach (string good in goodRefs)
            {
                vars.Add(good);
            }

            ExecutionResult result = module.GitExecutable.Execute(vars, throwOnErrorExit: false);

            int revisionsLeft = 0;
            int steps = 0;
            int candidates = 0;

            if (result.ExitedSuccessfully)
            {
                foreach (string raw in result.StandardOutput.Split('\n'))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=', StringComparison.Ordinal);
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line[..eq];
                    string value = line[(eq + 1)..].Trim('\'', ' ');

                    switch (key)
                    {
                        case "bisect_nr":
                            revisionsLeft = ParseCount(value);
                            break;
                        case "bisect_steps":
                            steps = ParseCount(value);
                            break;
                        case "bisect_all":
                            candidates = ParseCount(value);
                            break;
                    }
                }
            }

            return new BisectSession(true, true, true, revisionsLeft, steps, candidates, bad);
        }
        catch
        {
            // A session is open — that much was established from the marker file —
            // but nothing more could be read. Report only what is certain.
            return new BisectSession(true);
        }
    }

    /// <summary>
    ///  Parses one <c>--bisect-vars</c> figure. Git prints <c>bisect_good=-1</c> once
    ///  the search is over, and a negative count would only ever be rendered as
    ///  nonsense, so anything below zero is clamped away.
    /// </summary>
    private static int ParseCount(string value)
        => int.TryParse(value, out int parsed) && parsed > 0 ? parsed : 0;

    /// <summary>
    ///  True when a bisect session is in progress. Detected first via the
    ///  <c>.git/BISECT_LOG</c> / <c>.git/BISECT_START</c> marker files (fast,
    ///  handles linked worktrees through the resolved git dir); falls back to
    ///  the exit status of
    ///  <c>git bisect log</c>, which only succeeds mid-session. Never throws.
    /// </summary>
    public bool IsInProgress(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        try
        {
            string gitDir = module.WorkingDirGitDir;
            if (gitDir.Length > 0 &&
                (File.Exists(Path.Combine(gitDir, "BISECT_LOG")) ||
                 File.Exists(Path.Combine(gitDir, "BISECT_START"))))
            {
                return true;
            }
        }
        catch
        {
            // fall through to the git-log probe
        }

        GitArgumentBuilder args = new("bisect") { "log" };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return result.ExitedSuccessfully;
    }

    private static BisectResult Run(string repoPath, GitArgumentBuilder args)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new BisectResult(result.ExitedSuccessfully, result.AllOutput);
    }
}
