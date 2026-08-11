using System.Diagnostics;
using GitCommands;
using GitCommands.Git;
using GitExtUtils;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Which of the three merge stages a resolution should keep.
///  Maps onto the index stage numbers that <c>git checkout-index --stage=N</c>
///  understands, exactly as upstream's <c>GitModule.HandleConflictSelectSide</c>
///  does ("BASE" → 1, "LOCAL" → 2, "REMOTE" → 3).
/// </summary>
public enum ConflictChoice
{
    /// <summary>The merge base, index stage 1.</summary>
    Base = 1,

    /// <summary>Our side (the checked-out branch), index stage 2.</summary>
    Ours = 2,

    /// <summary>Their side (the branch being merged in), index stage 3.</summary>
    Theirs = 3,
}

/// <summary>
///  The kind of conflict, derived <b>only</b> from which index stages
///  <c>git ls-files --unmerged</c> reports for a path. These six cases are the
///  complete set that the stage triple can distinguish; nothing here is guessed
///  from git's (localised!) console messages.
///
///  <list type="table">
///    <item><term>1+2+3</term><description><see cref="BothModified"/> — <c>UU</c></description></item>
///    <item><term>2+3</term><description><see cref="BothAdded"/> — <c>AA</c></description></item>
///    <item><term>1+2</term><description><see cref="DeletedByThem"/> — <c>UD</c></description></item>
///    <item><term>1+3</term><description><see cref="DeletedByUs"/> — <c>DU</c></description></item>
///    <item><term>2</term><description><see cref="AddedByUs"/> — <c>AU</c></description></item>
///    <item><term>3</term><description><see cref="AddedByThem"/> — <c>UA</c></description></item>
///  </list>
/// </summary>
public enum ConflictKind
{
    /// <summary>Base, ours and theirs all present: the classic content conflict.</summary>
    BothModified,

    /// <summary>No base: the same path was created on both sides.</summary>
    BothAdded,

    /// <summary>Base and ours present, theirs missing: we modified, they deleted.</summary>
    DeletedByThem,

    /// <summary>Base and theirs present, ours missing: they modified, we deleted.</summary>
    DeletedByUs,

    /// <summary>Only our side exists.</summary>
    AddedByUs,

    /// <summary>Only their side exists.</summary>
    AddedByThem,
}

/// <summary>
///  One side (index stage) of a conflicted path. <see cref="Exists"/> is false
///  when <c>ls-files --unmerged</c> reported no entry for that stage, which is
///  what makes the delete/add cases distinguishable.
/// </summary>
public sealed record ConflictSide(bool Exists, string? Path, string? Sha, string? Mode)
{
    /// <summary>An absent stage.</summary>
    public static readonly ConflictSide Missing = new(false, null, null, null);

    /// <summary>The stage's object id abbreviated to 8 characters, or null when absent.</summary>
    public string? ShortSha => Sha is { Length: >= 8 } ? Sha[..8] : Sha;

    /// <summary>
    ///  Whether this stage is a <b>gitlink</b> — mode 160000, i.e. a submodule
    ///  pointer rather than a file. There is no blob behind it: what conflicts is
    ///  which commit of the submodule the superproject records.
    /// </summary>
    public bool IsSubmodule => Mode == "160000";
}

/// <summary>
///  A conflicted path with its three stages, as read from
///  <c>git ls-files --unmerged -z</c>.
/// </summary>
public sealed record ConflictEntry(string Path, ConflictSide Base, ConflictSide Ours, ConflictSide Theirs)
{
    /// <summary>The conflict kind implied by which stages exist.</summary>
    public ConflictKind Kind => (Base.Exists, Ours.Exists, Theirs.Exists) switch
    {
        (true, true, true) => ConflictKind.BothModified,
        (false, true, true) => ConflictKind.BothAdded,
        (true, true, false) => ConflictKind.DeletedByThem,
        (true, false, true) => ConflictKind.DeletedByUs,
        (_, true, false) => ConflictKind.AddedByUs,
        _ => ConflictKind.AddedByThem,
    };

    /// <summary>
    ///  True when a three-way merge is possible, i.e. all three stages exist.
    ///  The merge tool cannot do anything useful otherwise, so the view offers
    ///  the side-picking actions instead.
    /// </summary>
    public bool CanThreeWayMerge => Base.Exists && Ours.Exists && Theirs.Exists && !IsSubmodule;

    /// <summary>
    ///  True when the conflicting path is a submodule pointer. Any stage answering
    ///  is enough: a path cannot be a gitlink on one side and a file on the other
    ///  without git having reported a type change, which lands here too and is
    ///  better treated as a submodule than as a file that cannot be merged.
    /// </summary>
    public bool IsSubmodule => Base.IsSubmodule || Ours.IsSubmodule || Theirs.IsSubmodule;

    /// <summary>The stage for <paramref name="choice"/>.</summary>
    public ConflictSide Side(ConflictChoice choice) => choice switch
    {
        ConflictChoice.Base => Base,
        ConflictChoice.Ours => Ours,
        _ => Theirs,
    };

    public override string ToString() => Path;
}

/// <summary>Outcome of a conflict-resolution action, with git's output for display.</summary>
public sealed record ConflictActionResult(bool Success, string Message);

/// <summary>
///  Everything <see cref="Views.ResolveConflictsDialog"/> needs in order to port
///  upstream's <c>FormResolveConflicts</c>: enumerate the unmerged paths with
///  their stages, report the configured merge tool, launch it detached, and
///  resolve a path by keeping one side or by marking it resolved.
///
///  <para><b>Why not reuse <see cref="WorkingDirectoryService"/>?</b> That service
///  (owned by another unit) exposes <c>ListConflicts</c>, which returns bare path
///  strings from <c>git diff --name-only --diff-filter=U</c>. That is enough to
///  fill a list but carries <b>no stage information</b>, so the conflict *kind*
///  (both-modified vs deleted-by-us vs deleted-by-them vs add/add) and the
///  ours/base/theirs filenames cannot be derived from it — and those are exactly
///  what the dialog's info box and its three side rows display. Its
///  <c>TakeOurs</c>/<c>TakeTheirs</c> also always run <c>git checkout --ours</c>,
///  which fails outright when that stage is missing (the delete cases), and it
///  passes the path <b>unquoted</b> to <see cref="GitArgumentBuilder"/>, which
///  re-splits on spaces. This service therefore reads
///  <c>git ls-files --unmerged -z</c>, quotes every path, and picks
///  checkout-index / <c>git rm</c> per stage. Nothing in
///  <see cref="WorkingDirectoryService"/> is modified.</para>
///
///  <para>All methods are synchronous and block; call them from
///  <see cref="Task.Run"/>, never from the UI thread.</para>
/// </summary>
public sealed class ConflictService
{
    /// <summary>
    ///  Reads the unmerged index entries via <c>git ls-files --unmerged -z</c>.
    ///  Empty when the repository is not in a conflicted state.
    ///
    ///  <para><c>-z</c> matters: without it git applies <c>core.quotePath</c> and
    ///  escapes unusual bytes, and paths containing spaces would still be
    ///  ambiguous to re-parse. The output is structured (mode, sha, stage, TAB,
    ///  path) so nothing depends on git's message locale.</para>
    /// </summary>
    public IReadOnlyList<ConflictEntry> ListConflicts(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        GitArgumentBuilder args = new("ls-files")
        {
            "--unmerged",
            "-z",
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return [];
        }

        // Preserve git's (sorted) path order while grouping the up-to-three lines
        // per path into one entry.
        Dictionary<string, ConflictSide[]> stages = [];
        List<string> order = [];

        foreach (string record in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> <sha> <stage>\t<path>"
            int tab = record.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            string[] meta = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 3 || !int.TryParse(meta[2], out int stage) || stage is < 1 or > 3)
            {
                continue;
            }

            string path = record[(tab + 1)..];
            if (path.Length == 0)
            {
                continue;
            }

            if (!stages.TryGetValue(path, out ConflictSide[]? sides))
            {
                sides = [ConflictSide.Missing, ConflictSide.Missing, ConflictSide.Missing];
                stages[path] = sides;
                order.Add(path);
            }

            sides[stage - 1] = new ConflictSide(true, path, meta[1], meta[0]);
        }

        return [.. order.Select(path =>
        {
            ConflictSide[] sides = stages[path];
            return new ConflictEntry(path, sides[0], sides[1], sides[2]);
        })];
    }

    /// <summary>
    ///  The name of the configured merge tool, or <see langword="null"/> when
    ///  none is set. Mirrors upstream's <c>InitMergetool</c>: <c>merge.guitool</c>
    ///  first (<c>SettingKeyString.MergeToolKey</c>), then <c>merge.tool</c>
    ///  (<c>MergeToolNoGuiKey</c>) as the fallback. Read through
    ///  <c>git config --get</c> so includes and the global/system files are all
    ///  honoured.
    /// </summary>
    public string? GetMergeToolName(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        return ReadConfig(module, "merge.guitool") ?? ReadConfig(module, "merge.tool");
    }

    private static string? ReadConfig(GitModule module, string key)
    {
        GitArgumentBuilder args = new("config")
        {
            "--get",
            key,
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return null;
        }

        string value = result.StandardOutput.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    ///  True when the merge is still unresolved. Used to close the dialog once
    ///  the last conflict is gone, as upstream does at the end of
    ///  <c>Initialize()</c>.
    /// </summary>
    public bool InTheMiddleOfConflictedMerge(string repoPath)
        => GitContext.CreateModule(repoPath).InTheMiddleOfConflictedMerge();

    /// <summary>
    ///  True during a rebase. Upstream swaps the meaning of the two side labels
    ///  in this case (<c>GetLocalSideString</c>/<c>GetRemoteSideString</c>,
    ///  <c>FormResolveConflicts.cs:782-784</c>): while rebasing, "local" is the
    ///  branch you are rebasing <i>onto</i>, so it is git's <i>theirs</i>.
    /// </summary>
    public bool InTheMiddleOfRebase(string repoPath)
        => GitContext.CreateModule(repoPath).InTheMiddleOfRebase();

    /// <summary>
    ///  Launches <c>git mergetool --no-prompt [-- &lt;path&gt;]</c> detached, so
    ///  the interactive tool never blocks the UI thread. Without
    ///  <paramref name="path"/> git walks every conflicted file in turn, which is
    ///  upstream's "Start mergetool" (<c>Module.RunMergeTool()</c>).
    ///
    ///  <para><paramref name="onExit"/> is raised on a thread-pool thread when the
    ///  tool quits, so the view can rescan: <c>git mergetool</c> stages the file
    ///  itself when the tool reports success, so the list is stale until then.</para>
    ///
    ///  <para>A detached launch cannot read git's own "no tool configured"
    ///  message, so the configuration is pre-checked here and reported as text,
    ///  the same shape upstream shows in its <c>_noMergeTool</c> message box.
    ///  Never throws.</para>
    /// </summary>
    public ConflictActionResult LaunchMergetool(string repoPath, string? path, Action? onExit = null)
    {
        string? tool = GetMergeToolName(repoPath);
        if (tool is null)
        {
            return new ConflictActionResult(false,
                "There is no mergetool configured. Set one with 'git config merge.tool <tool>' (e.g. kdiff3, meld, vimdiff).");
        }

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "git",
                UseShellExecute = false,
                WorkingDirectory = repoPath,
            };
            psi.ArgumentList.Add("mergetool");
            psi.ArgumentList.Add("--no-prompt");
            if (path is not null)
            {
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(path);
            }

            // ArgumentList (not a single command line) — spaces in the path are
            // passed through verbatim, no quoting or re-splitting involved.
            Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return new ConflictActionResult(false, "Could not start git mergetool.");
            }

            if (onExit is not null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    try
                    {
                        onExit();
                    }
                    catch
                    {
                        // A rescan failure must never take down the process-exit callback.
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                };
            }

            return new ConflictActionResult(true, path is null
                ? $"Started {tool} for all conflicted files."
                : $"Opened {path} in {tool}.");
        }
        catch (Exception ex)
        {
            return new ConflictActionResult(false, $"Error starting mergetool: {ex.Message}");
        }
    }

    /// <summary>
    ///  True when <paramref name="tool"/> resolves to an executable on PATH.
    ///  Only used to warn (never to disable the button): git's mergetool
    ///  definitions can point somewhere else entirely through
    ///  <c>mergetool.&lt;tool&gt;.path</c>, so a negative answer is not proof
    ///  the tool will fail.
    /// </summary>
    public bool IsToolOnPath(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return false;
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar is null)
        {
            return false;
        }

        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, tool)))
                {
                    return true;
                }
            }
            catch
            {
                // Unreadable PATH entry — keep looking.
            }
        }

        return false;
    }

    /// <summary>
    ///  Resolves <paramref name="entry"/> by keeping one side.
    ///
    ///  <para>When that stage exists, this runs upstream's pair
    ///  (<c>GitModule.HandleConflictSelectSide</c>,
    ///  <c>GitModule.cs:524-548</c>): <c>git checkout-index -f --stage=N</c>
    ///  followed by <c>git add</c>. When the stage is <b>absent</b> — the
    ///  delete/add cases — keeping that side means accepting the deletion, so
    ///  <c>git rm</c> is used instead; <c>checkout-index</c> would simply fail
    ///  there.</para>
    ///
    ///  <para>Unlike the upstream helper this does not call
    ///  <c>Directory.SetCurrentDirectory</c> (a process-global side effect, and
    ///  this runs on a thread-pool thread): the module's working directory
    ///  already scopes the command.</para>
    /// </summary>
    public ConflictActionResult ChooseSide(string repoPath, ConflictEntry entry, ConflictChoice choice)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string quoted = Quote(entry.Path);

        if (!entry.Side(choice).Exists)
        {
            // The chosen side does not have this file: the resolution is a delete.
            GitArgumentBuilder rmArgs = new("rm")
            {
                "-f",
                "--",
                quoted,
            };
            ExecutionResult rm = module.GitExecutable.Execute(rmArgs, throwOnErrorExit: false);
            return new ConflictActionResult(rm.ExitedSuccessfully, rm.AllOutput);
        }

        if (entry.IsSubmodule)
        {
            return ChooseSubmoduleSide(module, repoPath, entry, choice);
        }

        GitArgumentBuilder checkoutArgs = new("checkout-index")
        {
            "-f",
            $"--stage={(int)choice}",
            "--",
            quoted,
        };
        ExecutionResult checkout = module.GitExecutable.Execute(checkoutArgs, throwOnErrorExit: false);
        if (!checkout.ExitedSuccessfully)
        {
            return new ConflictActionResult(false, checkout.AllOutput);
        }

        return Stage(module, quoted);
    }

    /// <summary>
    ///  Resolves a <b>submodule pointer</b> conflict to one side.
    ///
    ///  <para><b>The file path cannot do this.</b> A gitlink has no blob, so
    ///  <c>checkout-index --stage=N</c> writes nothing and — this is the part that
    ///  matters — <b>exits 0</b>. The <c>git add</c> that followed then staged whatever
    ///  commit the submodule happened to be checked out at on disk, which is usually
    ///  "ours" whichever button was pressed. Measured on a real conflict: choosing
    ///  THEIRS left the index at ours, reported as success. A wrong answer announced as
    ///  a right one is worse than a refusal, and this is what that was.</para>
    ///
    ///  <para>The index entry is therefore written directly with
    ///  <c>update-index --cacheinfo 160000,&lt;sha&gt;,&lt;path&gt;</c>, which both records
    ///  the chosen commit and clears the three conflict stages in one step. The
    ///  submodule's own checkout is then moved to match, because an index that says one
    ///  commit and a work tree that shows another is exactly the state that makes the
    ///  superproject look dirty the moment the merge is committed.</para>
    ///
    ///  <para>If that second step fails — most often because the chosen commit was
    ///  never fetched into the submodule — the resolution still stands and the message
    ///  says what is left to do. Silently succeeding is the thing being fixed here; the
    ///  half-done case has to speak.</para>
    /// </summary>
    private static ConflictActionResult ChooseSubmoduleSide(
        GitModule module, string repoPath, ConflictEntry entry, ConflictChoice choice)
    {
        ConflictSide side = entry.Side(choice);
        if (side.Sha is not { Length: > 0 } sha)
        {
            return new ConflictActionResult(false, $"The {choice} side records no commit for {entry.Path}.");
        }

        GitArgumentBuilder args = new("update-index")
        {
            "--cacheinfo",
            $"160000,{sha},{entry.Path}",
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return new ConflictActionResult(false, result.AllOutput);
        }

        string submodulePath = Path.Combine(repoPath, entry.Path);
        if (!Directory.Exists(Path.Combine(submodulePath, ".git")) && !File.Exists(Path.Combine(submodulePath, ".git")))
        {
            // Not initialised: the pointer is resolved and there is no work tree to
            // move, which is a complete answer for a submodule nobody has cloned.
            return new ConflictActionResult(
                true, $"{entry.Path} now points at {side.ShortSha} (the submodule is not initialised here).");
        }

        GitModule submodule = GitContext.CreateModule(submodulePath);
        GitArgumentBuilder checkout = new("checkout")
        {
            "--force",
            sha,
        };
        ExecutionResult moved = submodule.GitExecutable.Execute(checkout, throwOnErrorExit: false);

        return moved.ExitedSuccessfully
            ? new ConflictActionResult(true, $"{entry.Path} now points at {side.ShortSha}.")
            : new ConflictActionResult(
                true,
                $"{entry.Path} now points at {side.ShortSha} in the index, but the submodule could not be "
                    + $"checked out there — fetch it and run `git checkout {side.ShortSha}` inside it.\n\n"
                    + moved.AllOutput);
    }

    /// <summary>
    ///  Marks a conflict resolved by staging the work-tree file as it stands
    ///  (<c>git add -- &lt;path&gt;</c>) — the file the user has just edited by
    ///  hand or in the merge tool.
    /// </summary>
    public ConflictActionResult MarkResolved(string repoPath, string path)
        => Stage(GitContext.CreateModule(repoPath), Quote(path));

    private static ConflictActionResult Stage(GitModule module, string quotedPath)
    {
        GitArgumentBuilder args = new("add")
        {
            "--",
            quotedPath,
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new ConflictActionResult(result.ExitedSuccessfully, result.AllOutput);
    }

    /// <summary>
    ///  Aborts the whole resolution with <c>git reset --hard</c>, upstream's
    ///  <b>Reset</b> button (<c>FormResolveConflicts.cs:770-779</c> →
    ///  <c>Module.Reset(ResetMode.Hard)</c>). Destructive: the caller must have
    ///  confirmed first, as upstream does with its two-step
    ///  <c>ShowAbortMessage</c>.
    /// </summary>
    public ConflictActionResult ResetHard(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        ArgumentString args = Commands.Reset(ResetMode.Hard);
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new ConflictActionResult(result.ExitedSuccessfully, result.AllOutput);
    }

    // GitArgumentBuilder re-splits arguments on spaces, so every path must arrive
    // quoted. Conflicted paths containing spaces are entirely ordinary (and are
    // part of this unit's GUI fixture), so this is not theoretical.
    private static string Quote(string path) => (path.ToPosixPath() ?? path).Quote();
}
