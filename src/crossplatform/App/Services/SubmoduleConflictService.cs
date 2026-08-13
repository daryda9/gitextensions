using GitCommands;
using GitExtUtils;
using GitExtensions.Extensibility;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  One commit of a <b>submodule</b>, as needed to decide a gitlink conflict.
///
///  <para><see cref="Exists"/> is false when the object is not in the submodule's
///  object database (<c>git cat-file -e &lt;sha&gt;^{commit}</c> failed). That is a
///  perfectly ordinary state — the side that moved the pointer may simply never have
///  been fetched here — and it is the reason every field below is nullable: without
///  the object there is no subject, no author and no date to show, only the sha.</para>
/// </summary>
public sealed record SubmoduleCommitInfo(string Sha, bool Exists, string? Subject, string? Author, DateTimeOffset? Date)
{
    /// <summary>A sha we know nothing about yet (or an absent stage).</summary>
    public static SubmoduleCommitInfo Unknown(string? sha) => new(sha ?? string.Empty, false, null, null, null);

    /// <summary>The abbreviated object id, the form every git UI shows.</summary>
    public string ShortSha => Sha.Length >= 8 ? Sha[..8] : Sha;

    /// <summary>True when this record carries no sha at all (the stage was missing).</summary>
    public bool IsEmpty => Sha.Length == 0;
}

/// <summary>
///  How the two conflicting submodule pointers relate to each other.
///
///  <para>The two "ancestor" cases are the ones worth calling out loudly: when one
///  pointer is an ancestor of the other there is nothing to merge inside the
///  submodule, the newer commit already contains the older, and the resolution is a
///  single click rather than a judgement call.</para>
/// </summary>
public enum SubmodulePointerRelation
{
    /// <summary>Could not be computed — objects missing, or the submodule is not initialised.</summary>
    Unknown,

    /// <summary>Both sides record the same commit. Not really a conflict any more.</summary>
    Same,

    /// <summary>Ours is an ancestor of theirs: our pointer is <b>behind</b> theirs.</summary>
    OursBehind,

    /// <summary>Theirs is an ancestor of ours: our pointer is <b>ahead</b> of theirs.</summary>
    OursAhead,

    /// <summary>Neither contains the other: real divergence, the interesting case.</summary>
    Diverged,
}

/// <summary>
///  Everything <see cref="Views.SubmoduleConflictDialog"/> shows about one
///  conflicting gitlink: the three pointers, how they relate, and what lies between
///  them.
///
///  <para><see cref="Unavailable"/> is the degraded path. When it is non-null the
///  history fields are empty and only the two side shas are trustworthy, so the view
///  must show the message and fall back to "keep mine / keep theirs" instead of
///  pretending to have a commit list.</para>
/// </summary>
public sealed record SubmoduleConflictReport(
    string Path,
    string WorkTree,
    bool IsInitialized,
    string? Unavailable,
    SubmoduleCommitInfo Base,
    SubmoduleCommitInfo Ours,
    SubmoduleCommitInfo Theirs,
    SubmodulePointerRelation Relation,
    string? MergeBase,
    bool MergeBaseIsRecordedBase,
    IReadOnlyList<SubmoduleCommitInfo> OnlyInOurs,
    IReadOnlyList<SubmoduleCommitInfo> OnlyInTheirs,
    IReadOnlyList<SubmoduleCommitInfo> Candidates)
{
    /// <summary>True when the commit lists and the relation can be believed.</summary>
    public bool HasHistory => Unavailable is null;
}

/// <summary>
///  Reads the history <b>inside</b> a submodule so a gitlink conflict can be decided
///  on evidence instead of on a coin toss, and writes the decision back into the
///  superproject's index.
///
///  <para><b>Why this is not part of <see cref="ConflictService"/>.</b> That service
///  answers "which side do you want?" and can already keep one — see its
///  <c>ChooseSubmoduleSide</c>. But for a submodule the honest answer is frequently
///  <i>neither</i>: when two branches move the same submodule forward along different
///  lines, the commit that contains both already exists (git even prints it as "a
///  possible merge resolution exists" and then refuses to use it). Choosing between
///  two pointers blind is the actual problem; every query here exists to replace that
///  blindness with the four facts that settle it — is one side simply behind the
///  other, where do they fork, what did each side add, and does a commit containing
///  both exist.</para>
///
///  <para>All commands run in the submodule's own work tree, not the superproject's:
///  the shas are commits of the submodule and mean nothing to the outer repository.
///  The one exception is <see cref="ChooseCommit"/>, which writes the outer index.</para>
///
///  <para>Synchronous and blocking, exactly like <see cref="ConflictService"/>; the
///  caller wraps them in <see cref="Task.Run"/>. Nothing here throws: a submodule
///  that was never initialised, a partially fetched one and a plain git failure all
///  come back as a report with <see cref="SubmoduleConflictReport.Unavailable"/> set.</para>
/// </summary>
public sealed class SubmoduleConflictService
{
    /// <summary>How many commits each "only in …" list is allowed to grow to.</summary>
    private const int MaxListed = 200;

    /// <summary>
    ///  Describes the conflict on <paramref name="path"/> given the three stage shas
    ///  read from the superproject's index (<see cref="ConflictEntry"/>).
    ///  <paramref name="baseSha"/> may be null — an add/add conflict has no stage 1 —
    ///  in which case only the ours/theirs relation is reported.
    /// </summary>
    public SubmoduleConflictReport Describe(string repoPath, string path, string? baseSha, string? oursSha, string? theirsSha)
    {
        string workTree = Path.Combine(repoPath, path);
        bool initialised = IsInitialized(workTree);

        SubmoduleCommitInfo bareBase = SubmoduleCommitInfo.Unknown(baseSha);
        SubmoduleCommitInfo bareOurs = SubmoduleCommitInfo.Unknown(oursSha);
        SubmoduleCommitInfo bareTheirs = SubmoduleCommitInfo.Unknown(theirsSha);

        if (!initialised)
        {
            // No object database: there is literally nothing to read. Say so and name
            // the command that fixes it rather than showing three empty panes.
            return Degraded(
                path, workTree, false,
                $"The submodule '{path}' is not initialised here, so its commits are not available. "
                    + $"Run `git submodule update --init -- {path}` and reopen this dialog to compare the two pointers.",
                bareBase, bareOurs, bareTheirs);
        }

        GitModule sub = GitContext.CreateModule(workTree);

        SubmoduleCommitInfo baseInfo = Describe(sub, baseSha);
        SubmoduleCommitInfo oursInfo = Describe(sub, oursSha);
        SubmoduleCommitInfo theirsInfo = Describe(sub, theirsSha);

        if (!oursInfo.Exists || !theirsInfo.Exists)
        {
            // One pointer is missing from the local clone. Any relation, merge base or
            // commit list computed from here would be a guess, so none is computed.
            string missing = string.Join(
                " and ",
                new[]
                {
                    oursInfo.Exists ? null : $"ours ({oursInfo.ShortSha})",
                    theirsInfo.Exists ? null : $"theirs ({theirsInfo.ShortSha})",
                }.Where(s => s is not null));

            return Degraded(
                path, workTree, true,
                $"The submodule '{path}' does not have {missing} in its object database, so the two pointers "
                    + $"cannot be compared. Run `git -C {path} fetch --all` and reopen this dialog.",
                baseInfo, oursInfo, theirsInfo);
        }

        SubmodulePointerRelation relation =
            string.Equals(oursInfo.Sha, theirsInfo.Sha, StringComparison.OrdinalIgnoreCase) ? SubmodulePointerRelation.Same
            : IsAncestor(sub, oursInfo.Sha, theirsInfo.Sha) ? SubmodulePointerRelation.OursBehind
            : IsAncestor(sub, theirsInfo.Sha, oursInfo.Sha) ? SubmodulePointerRelation.OursAhead
            : SubmodulePointerRelation.Diverged;

        string? mergeBase = MergeBaseService.FindMergeBase(sub, oursInfo.Sha, theirsInfo.Sha);

        // The superproject's stage 1 is the submodule commit recorded at the *merge
        // base of the outer branches*, which is not the same thing as the merge base
        // of the two submodule commits — they coincide in the ordinary case and
        // diverge exactly when someone moved the pointer sideways. Saying which of the
        // two it is tells the user whether the "only in" lists really are the whole
        // difference.
        bool baseMatches = mergeBase is not null && baseSha is not null
            && string.Equals(mergeBase, baseSha, StringComparison.OrdinalIgnoreCase);

        // Two separate `git log <mergebase>..<side>` calls rather than one
        // `--left-right <a>...<b>`: the ranges are identical, but the combined form
        // prefixes every line with '<'/'>' that has to be stripped before the null-
        // separated fields can be parsed, and it cannot be capped per side. Two calls
        // are one extra process and no parsing subtleties at all.
        string range = mergeBase ?? baseSha ?? string.Empty;
        IReadOnlyList<SubmoduleCommitInfo> onlyOurs = range.Length == 0 ? [] : LogRange(sub, range, oursInfo.Sha);
        IReadOnlyList<SubmoduleCommitInfo> onlyTheirs = range.Length == 0 ? [] : LogRange(sub, range, theirsInfo.Sha);

        return new SubmoduleConflictReport(
            path, workTree, true, null,
            baseInfo, oursInfo, theirsInfo,
            relation, mergeBase, baseMatches,
            onlyOurs, onlyTheirs,
            FindCommitsContainingBoth(sub, oursInfo.Sha, theirsInfo.Sha, relation));
    }

    /// <summary>
    ///  Commits (of the submodule) that already contain <b>both</b> pointers — the
    ///  third answer that the two side buttons cannot express. Almost always the right
    ///  resolution when one exists: it is the commit whose author already did this
    ///  merge inside the submodule.
    ///
    ///  <para>Computed as the intersection of the strict descendants of each side,
    ///  <c>git rev-list --ancestry-path --all --not &lt;sha&gt;</c>. <c>--ancestry-path</c>
    ///  is what makes it "descendants of" rather than "everything not reachable from",
    ///  which is a much larger and entirely useless set. Ordered oldest first, because
    ///  the oldest such commit is the least amount of unrelated history to swallow.</para>
    ///
    ///  <para>Empty when one side is an ancestor of the other: the newer side already
    ///  is the answer, and listing its descendants would only invite dragging the
    ///  submodule forward past what either branch asked for.</para>
    /// </summary>
    private static IReadOnlyList<SubmoduleCommitInfo> FindCommitsContainingBoth(
        GitModule sub, string ours, string theirs, SubmodulePointerRelation relation)
    {
        if (relation != SubmodulePointerRelation.Diverged)
        {
            return [];
        }

        HashSet<string> afterOurs = Descendants(sub, ours);
        if (afterOurs.Count == 0)
        {
            return [];
        }

        HashSet<string> afterTheirs = Descendants(sub, theirs);
        afterOurs.IntersectWith(afterTheirs);
        if (afterOurs.Count == 0)
        {
            return [];
        }

        List<SubmoduleCommitInfo> found = [.. afterOurs.Select(sha => Describe(sub, sha)).Where(c => c.Exists)];

        // Oldest first: the earliest commit that contains both is the tightest fit.
        found.Sort((a, b) => Nullable.Compare(a.Date, b.Date));
        return found.Count > MaxListed ? found[..MaxListed] : found;
    }

    private static HashSet<string> Descendants(GitModule sub, string sha)
    {
        GitArgumentBuilder args = new("rev-list")
        {
            "--ancestry-path",
            "--all",
            "--not",
            sha,
        };
        RunResult result = Run(sub, args);
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        if (result.Ok)
        {
            foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                set.Add(line.Trim());
            }
        }

        return set;
    }

    /// <summary>
    ///  Resolves anything the user may have typed (a sha prefix, a tag, a branch,
    ///  <c>HEAD~2</c>) to a full commit sha of the submodule, or null when it names
    ///  nothing. Lets the dialog accept a third commit without the user having to
    ///  paste 40 hex digits.
    /// </summary>
    public string? ResolveRevision(string repoPath, string path, string revision)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return null;
        }

        string workTree = Path.Combine(repoPath, path);
        if (!IsInitialized(workTree))
        {
            return null;
        }

        GitArgumentBuilder args = new("rev-parse")
        {
            "--verify",
            "--quiet",
            $"{revision.Trim()}^{{commit}}".Quote(),
        };
        RunResult result = Run(GitContext.CreateModule(workTree), args);
        if (!result.Ok)
        {
            return null;
        }

        string sha = result.Output.Trim();
        return sha.Length == 0 ? null : sha;
    }

    /// <summary>
    ///  Records <paramref name="sha"/> as the submodule's commit for
    ///  <paramref name="path"/> in the superproject's index, resolving the conflict.
    ///
    ///  <para><c>git update-index --cacheinfo 160000,&lt;sha&gt;,&lt;path&gt;</c> is the only
    ///  command that can do this: it writes the gitlink and clears the three conflict
    ///  stages in one step, and — the point of this whole dialog — it accepts a commit
    ///  that is <b>neither side</b>, which <c>checkout --ours/--theirs</c> and
    ///  <c>checkout-index --stage=N</c> structurally cannot.</para>
    ///
    ///  <para>The submodule's work tree is then moved to the same commit
    ///  (<c>git -C &lt;sub&gt; checkout --force &lt;sha&gt;</c>) rather than left alone: an
    ///  index that records one commit while the work tree shows another makes the
    ///  superproject dirty the instant the merge is committed, and the user would have
    ///  to undo it by hand. When that checkout fails (typically an unfetched commit)
    ///  the resolution still stands — the index is already correct — and the message
    ///  says what remains to be done, because a half-applied change that reports
    ///  success is worse than one that speaks.</para>
    /// </summary>
    public ConflictActionResult ChooseCommit(string repoPath, string path, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return new ConflictActionResult(false, $"No commit chosen for {path}.");
        }

        GitModule super = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = new("update-index")
        {
            "--cacheinfo",

            // Quoted as one argument: GitArgumentBuilder re-splits on spaces and a
            // submodule path containing one would otherwise become two arguments.
            $"160000,{sha},{path.ToPosixPath() ?? path}".Quote(),
        };
        RunResult written = Run(super, args);
        if (!written.Ok)
        {
            return new ConflictActionResult(false, written.AllOutput);
        }

        string shortSha = sha.Length >= 8 ? sha[..8] : sha;
        string workTree = Path.Combine(repoPath, path);
        if (!IsInitialized(workTree))
        {
            return new ConflictActionResult(
                true, $"{path} now points at {shortSha} (the submodule is not initialised here, nothing to check out).");
        }

        GitArgumentBuilder checkout = new("checkout")
        {
            "--force",
            sha,
        };
        RunResult moved = Run(GitContext.CreateModule(workTree), checkout);

        return moved.Ok
            ? new ConflictActionResult(true, $"{path} now points at {shortSha}, and the submodule is checked out there.")
            : new ConflictActionResult(
                true,
                $"{path} now points at {shortSha} in the index, but the submodule could not be checked out there — "
                    + $"run `git -C {path} fetch --all` and then `git -C {path} checkout {shortSha}`.\n\n"
                    + moved.AllOutput);
    }

    /// <summary>
    ///  True when <paramref name="workTree"/> is a checked-out submodule. A submodule
    ///  that has never been updated is an empty directory: no <c>.git</c> entry, hence
    ///  no objects — <c>.git</c> is a <i>file</i> pointing into the superproject's
    ///  <c>.git/modules</c> for a normal submodule and a directory for an old-style
    ///  one, so both spellings count.
    /// </summary>
    private static bool IsInitialized(string workTree)
    {
        try
        {
            string dotGit = Path.Combine(workTree, ".git");
            return Directory.Exists(dotGit) || File.Exists(dotGit);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///  Subject, author and date for one sha, or a record with
    ///  <see cref="SubmoduleCommitInfo.Exists"/> false when the object is absent.
    ///  Existence is probed with <c>cat-file -e &lt;sha&gt;^{commit}</c> first so a missing
    ///  object is reported as such instead of as a git error message.
    /// </summary>
    private static SubmoduleCommitInfo Describe(GitModule sub, string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return SubmoduleCommitInfo.Unknown(sha);
        }

        GitArgumentBuilder probe = new("cat-file")
        {
            "-e",
            $"{sha}^{{commit}}".Quote(),
        };
        if (!Run(sub, probe).Ok)
        {
            return SubmoduleCommitInfo.Unknown(sha);
        }

        GitArgumentBuilder log = new("log")
        {
            "-1",

            // NUL between the fields: a subject may contain anything at all, including
            // the separators a human-readable format would use.
            "--format=%H%x00%s%x00%an%x00%aI",
            sha,
        };
        RunResult result = Run(sub, log);
        return result.Ok
            ? Parse(result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()) ?? SubmoduleCommitInfo.Unknown(sha)
            : SubmoduleCommitInfo.Unknown(sha);
    }

    /// <summary>The commits in <c>&lt;from&gt;..&lt;to&gt;</c>, newest first.</summary>
    private static IReadOnlyList<SubmoduleCommitInfo> LogRange(GitModule sub, string from, string to)
    {
        GitArgumentBuilder args = new("log")
        {
            $"--max-count={MaxListed}",
            "--format=%H%x00%s%x00%an%x00%aI",
            $"{from}..{to}",
        };
        RunResult result = Run(sub, args);
        if (!result.Ok)
        {
            return [];
        }

        List<SubmoduleCommitInfo> commits = [];
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Parse(line) is { } commit)
            {
                commits.Add(commit);
            }
        }

        return commits;
    }

    private static SubmoduleCommitInfo? Parse(string? line)
    {
        if (line is null)
        {
            return null;
        }

        string[] fields = line.Split('\0');
        if (fields.Length < 4 || fields[0].Trim().Length == 0)
        {
            return null;
        }

        DateTimeOffset? date = DateTimeOffset.TryParse(fields[3].Trim(), out DateTimeOffset parsed) ? parsed : null;
        return new SubmoduleCommitInfo(fields[0].Trim(), true, fields[1], fields[2], date);
    }

    /// <summary>
    ///  <c>git merge-base --is-ancestor A B</c>: exit 0 means A is contained in B.
    ///  Exit 1 is the plain "no" answer, not a failure, which is why the executable is
    ///  told not to throw.
    /// </summary>
    private static bool IsAncestor(GitModule sub, string ancestor, string descendant)
    {
        GitArgumentBuilder args = new("merge-base")
        {
            "--is-ancestor",
            ancestor,
            descendant,
        };
        return Run(sub, args).Ok;
    }

    /// <summary>
    ///  One git invocation reduced to what the callers here need.
    ///
    ///  <para>Not <see cref="ExecutionResult"/>: that struct cannot be constructed
    ///  without a live <c>IExecutable</c>, so the "git could not be started at all"
    ///  case — a repository deleted under the dialog, most plausibly — has no honest
    ///  value to return. Failing softly matters more than the extra type: this service
    ///  only reads history, and a read must never take the window down.</para>
    /// </summary>
    private sealed record RunResult(bool Ok, string Output, string AllOutput);

    private static RunResult Run(GitModule module, ArgumentString args)
    {
        try
        {
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            return new RunResult(result.ExitedSuccessfully, result.StandardOutput, result.AllOutput);
        }
        catch (Exception ex)
        {
            return new RunResult(false, string.Empty, ex.Message);
        }
    }

    private static SubmoduleConflictReport Degraded(
        string path, string workTree, bool initialised, string message,
        SubmoduleCommitInfo baseInfo, SubmoduleCommitInfo ours, SubmoduleCommitInfo theirs)
        => new(
            path, workTree, initialised, message,
            baseInfo, ours, theirs,
            SubmodulePointerRelation.Unknown, null, false,
            [], [], []);
}
