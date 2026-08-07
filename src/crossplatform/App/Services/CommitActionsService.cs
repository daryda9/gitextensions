using System.Text;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The per-commit switches offered by the CommitDialog "Options" menu. They map
///  one-to-one onto real <c>git commit</c> flags (except
///  <see cref="CloseAfterCommit"/>, which is pure dialog behaviour).
/// </summary>
public sealed record CommitOptions(
    bool Amend = false,
    bool SignOff = false,
    bool NoVerify = false,
    bool ResetAuthor = false,
    bool CloseAfterCommit = false);

/// <summary>
///  A commit-message template offered by the "Commit templates" menu: either the
///  file configured in <c>commit.template</c> or a conventional template file
///  found inside the repository.
/// </summary>
public sealed record CommitTemplate(string Name, string Path, string Source)
{
    public override string ToString() => Name;
}

/// <summary>Outcome of a git action started from the CommitDialog.</summary>
public sealed record CommitActionResult(bool Success, string Output);

/// <summary>
///  Git actions backing the CommitDialog commands that the original Git Extensions
///  commit form exposes but the Avalonia dialog used to stub out: committing with
///  the Options switches, stashing only the staged changes, discovering commit
///  message templates and creating a branch off HEAD.
///  All methods are synchronous and MUST be called off the UI thread.
/// </summary>
public sealed class CommitActionsService
{
    /// <summary>
    ///  Commits the staged changes with the flags selected in the Options menu:
    ///  <c>git commit -F &lt;file&gt; [--amend] [--signoff] [--no-verify] [--reset-author]</c>.
    ///  The message is passed through a temp file so multi-line text and quoting
    ///  never go through the shell.
    ///
    ///  The temp file is written in the repository's configured commit encoding
    ///  (<c>i18n.commitEncoding</c>, exposed by the core as
    ///  <see cref="GitModule.CommitEncoding"/>) — git reads a <c>-F</c> message file
    ///  as bytes in that encoding, so writing it as UTF-8 unconditionally corrupted
    ///  every non-ASCII character whenever the repo used e.g. cp1251 or latin1.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Runs through <see cref="GitStreamRunner"/> rather than the core's
    ///   <c>Execute</c>, because the caller shows the run in a
    ///   <c>GitProcessDialog</c>: that is the only way the user gets to see what
    ///   upstream's <c>FormProcess</c> shows them — the command line, git's own
    ///   output, and above all <b>the output of the pre-commit hook</b>, which is
    ///   otherwise swallowed whole (a hook that refuses the commit used to fail with
    ///   nothing but a one-line status).
    ///  </para>
    ///  <para>
    ///   <paramref name="emit"/> is called from the calling (background) thread for
    ///   every output line as git produces it. The message file is deleted only after
    ///   the process has exited, since <see cref="GitStreamRunner.Run"/> blocks.
    ///  </para>
    /// </remarks>
    /// <param name="emit">Sink for every output line, including the command header.</param>
    public CommitActionResult Commit(string repoPath, string message, CommitOptions options, Action<string> emit)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string messageFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(messageFile, message ?? string.Empty, CommitEncodingOf(module));

            string args = CommitArguments(module, options, messageFile).ToString();
            StringBuilder log = new();
            int exit = GitStreamRunner.Run(
                repoPath,
                args,
                line =>
                {
                    log.AppendLine(line);
                    emit(line);
                });

            return new CommitActionResult(exit == 0, log.ToString());
        }
        catch (Exception ex)
        {
            emit(ex.Message);
            return new CommitActionResult(false, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(messageFile);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    /// <summary>
    ///  The <c>git commit</c> command line actually executed.
    ///  <see cref="DescribeCommit"/> is its message-file-free echo for status text;
    ///  the console shows this one verbatim.
    /// </summary>
    private static GitArgumentBuilder CommitArguments(GitModule module, CommitOptions options, string messageFile)
        => new("commit")
        {
            { options.Amend, "--amend" },
            { options.SignOff, "--signoff" },
            { options.NoVerify, "--no-verify" },
            { options.ResetAuthor && options.Amend, "--reset-author" },
            "-F",
            module.GetPathForGitExecution(messageFile).Quote(),
        };

    /// <summary>
    ///  Returns the exact command line that <see cref="Commit"/> will run, for the
    ///  dialog status line (the message file is elided).
    /// </summary>
    public static string DescribeCommit(CommitOptions options)
    {
        List<string> parts = ["git", "commit"];
        if (options.Amend)
        {
            parts.Add("--amend");
        }

        if (options.SignOff)
        {
            parts.Add("--signoff");
        }

        if (options.NoVerify)
        {
            parts.Add("--no-verify");
        }

        if (options.ResetAuthor && options.Amend)
        {
            parts.Add("--reset-author");
        }

        parts.Add("-F <message>");
        return string.Join(' ', parts);
    }

    // ---------------- stash staged ----------------

    /// <summary>
    ///  Stashes ONLY the staged (index) changes, leaving unstaged changes in the
    ///  working tree. Primary path is <c>git stash push --staged -m &lt;msg&gt;</c>
    ///  (git 2.35+). Older git does not know <c>--staged</c>; in that case
    ///  <see cref="StashStagedFallback"/> reproduces the same effect with plumbing
    ///  (<c>write-tree</c> / <c>commit-tree</c> / <c>stash store</c> plus a reverse
    ///  apply of the cached diff).
    /// </summary>
    public CommitActionResult StashStaged(string repoPath, string message)
    {
        GitModule module = GitContext.CreateModule(repoPath);

        // Normalised once: the argument is declared non-null but has always been
        // treated as optional here (an empty message means "let git name the stash"),
        // and the two spellings of that test made the fallback's parameter read as
        // possibly null.
        string text = message ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Exec(module, new GitArgumentBuilder("diff") { "--cached", "--name-only" })))
        {
            return new CommitActionResult(false, "There are no staged changes to stash.");
        }

        GitArgumentBuilder args = new("stash")
        {
            "push",
            "--staged",
            { !string.IsNullOrWhiteSpace(text), "-m" },
            { !string.IsNullOrWhiteSpace(text), text.Quote() },
        };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (result.ExitedSuccessfully)
        {
            return new CommitActionResult(true, result.AllOutput);
        }

        string output = result.AllOutput ?? string.Empty;
        bool unsupported =
            output.Contains("--staged", StringComparison.Ordinal)
            && (output.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
                || output.Contains("usage:", StringComparison.OrdinalIgnoreCase)
                || output.Contains("error:", StringComparison.OrdinalIgnoreCase));

        return unsupported
            ? StashStagedFallback(module, text)
            : new CommitActionResult(false, output);
    }

    // Emulates `git stash push --staged` on git < 2.35:
    //   1. capture the cached diff (binary-safe patch);
    //   2. build a stash-shaped commit from the index tree
    //      (parents: HEAD + an "index on …" commit) WITHOUT touching the repo;
    //   3. reverse-apply the cached patch to index+worktree (this is the only
    //      destructive step, so it runs before the stash is registered);
    //   4. register the commit with `git stash store`.
    private static CommitActionResult StashStagedFallback(GitModule module, string message)
    {
        string patch = Exec(module, new GitArgumentBuilder("diff") { "--cached", "--binary" }, trim: false);
        if (string.IsNullOrEmpty(patch))
        {
            return new CommitActionResult(false, "There are no staged changes to stash.");
        }

        string head = Exec(module, new GitArgumentBuilder("rev-parse") { "HEAD" });
        string branch = Exec(module, new GitArgumentBuilder("rev-parse") { "--abbrev-ref", "HEAD" });
        string indexTree = Exec(module, new GitArgumentBuilder("write-tree"));
        if (head.Length == 0 || indexTree.Length == 0)
        {
            return new CommitActionResult(false, "Cannot build a stash entry: unresolved HEAD or index tree.");
        }

        string label = string.IsNullOrWhiteSpace(message)
            ? $"On {branch}: staged changes"
            : $"On {branch}: {message.Trim()}";

        string indexCommit = Exec(module, new GitArgumentBuilder("commit-tree")
        {
            indexTree, "-p", head, "-m", $"index on {branch}".Quote(),
        });
        if (indexCommit.Length == 0)
        {
            return new CommitActionResult(false, "Cannot build a stash entry (commit-tree failed).");
        }

        string stashCommit = Exec(module, new GitArgumentBuilder("commit-tree")
        {
            indexTree, "-p", head, "-p", indexCommit, "-m", label.Quote(),
        });
        if (stashCommit.Length == 0)
        {
            return new CommitActionResult(false, "Cannot build a stash entry (commit-tree failed).");
        }

        string patchFile = Path.Combine(Path.GetTempPath(), $"gitext-staged-{Guid.NewGuid():N}.patch");
        try
        {
            File.WriteAllText(patchFile, patch);
            ExecutionResult applied = module.GitExecutable.Execute(
                new GitArgumentBuilder("apply")
                {
                    "--reverse", "--index", "--whitespace=nowarn",
                    module.GetPathForGitExecution(patchFile).Quote(),
                },
                throwOnErrorExit: false);

            if (!applied.ExitedSuccessfully)
            {
                return new CommitActionResult(
                    false,
                    "Could not remove the staged changes from the working tree (a file probably has both "
                    + "staged and unstaged edits, and this git is too old for `stash push --staged`).\n"
                    + applied.AllOutput);
            }
        }
        catch (Exception ex)
        {
            return new CommitActionResult(false, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(patchFile);
            }
            catch
            {
                // best-effort cleanup
            }
        }

        ExecutionResult stored = module.GitExecutable.Execute(
            new GitArgumentBuilder("stash") { "store", "-m", label.Quote(), stashCommit },
            throwOnErrorExit: false);

        return stored.ExitedSuccessfully
            ? new CommitActionResult(true, $"Stashed staged changes ({stashCommit[..Math.Min(8, stashCommit.Length)]}).")
            : new CommitActionResult(false, $"git stash store failed; the stash commit is {stashCommit}.\n{stored.AllOutput}");
    }

    // ---------------- commit templates ----------------

    /// <summary>
    ///  Lists the available commit-message templates: the file configured in
    ///  <c>commit.template</c> (any scope) first, then conventional template files
    ///  living in the repository (<c>.gitmessage*</c>, <c>.github/*TEMPLATE*</c>,
    ///  <c>.github/PULL_REQUEST_TEMPLATE/*</c>).
    /// </summary>
    public IReadOnlyList<CommitTemplate> ListTemplates(string repoPath)
    {
        List<CommitTemplate> templates = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string source)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full) && seen.Add(full))
                {
                    templates.Add(new CommitTemplate(Path.GetFileName(full), full, source));
                }
            }
            catch
            {
                // ignore unusable paths
            }
        }

        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            string configured = Exec(module, new GitArgumentBuilder("config") { "--get", "commit.template" });
            if (configured.Length > 0)
            {
                Add(ResolvePath(repoPath, configured), "commit.template");
            }
        }
        catch
        {
            // config unavailable → repository files only
        }

        foreach (string name in new[] { ".gitmessage", ".gitmessage.txt", ".git-commit-template", ".gitmessage.md" })
        {
            Add(Path.Combine(repoPath, name), "repository");
        }

        TryAddDirectory(Path.Combine(repoPath, ".github"), "*TEMPLATE*", Add);
        TryAddDirectory(Path.Combine(repoPath, ".github", "PULL_REQUEST_TEMPLATE"), "*", Add);
        TryAddDirectory(Path.Combine(repoPath, ".github", "ISSUE_TEMPLATE"), "*", Add);
        TryAddDirectory(Path.Combine(repoPath, ".gitmessages"), "*", Add);

        return templates;
    }

    private static void TryAddDirectory(string dir, string pattern, Action<string, string> add)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal))
            {
                add(file, "repository");
            }
        }
        catch
        {
            // unreadable directory → skip
        }
    }

    /// <summary>Reads a template file, returning an error marker instead of throwing.</summary>
    public static string ReadTemplate(CommitTemplate template)
    {
        try
        {
            return File.ReadAllText(template.Path).Replace("\r\n", "\n");
        }
        catch (Exception ex)
        {
            return "# Could not read template: " + ex.Message;
        }
    }

    private static string ResolvePath(string repoPath, string value)
    {
        string path = value.Trim().Trim('"');
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return Path.IsPathRooted(path) ? path : Path.Combine(repoPath, path);
    }

    // ---------------- branch ----------------

    /// <summary>Current branch name, or "(detached)" when HEAD is detached.</summary>
    public string CurrentBranch(string repoPath)
    {
        try
        {
            string branch = Exec(GitContext.CreateModule(repoPath), new GitArgumentBuilder("rev-parse") { "--abbrev-ref", "HEAD" });
            return branch.Length == 0 || branch == "HEAD" ? "(detached)" : branch;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///  Validates <paramref name="name"/> as a branch name via
    ///  <c>git check-ref-format --branch</c> and rejects names already taken.
    ///  Returns <see langword="null"/> when the name is acceptable, otherwise the
    ///  reason to show the user.
    /// </summary>
    public string? ValidateBranchName(string repoPath, string name)
    {
        string candidate = (name ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            return "Enter a branch name.";
        }

        GitModule module = GitContext.CreateModule(repoPath);
        ExecutionResult check = module.GitExecutable.Execute(
            new GitArgumentBuilder("check-ref-format") { "--branch", candidate.Quote() },
            throwOnErrorExit: false);
        if (!check.ExitedSuccessfully)
        {
            return $"'{candidate}' is not a valid branch name.";
        }

        ExecutionResult exists = module.GitExecutable.Execute(
            new GitArgumentBuilder("show-ref") { "--verify", "--quiet", $"refs/heads/{candidate}".Quote() },
            throwOnErrorExit: false);
        return exists.ExitedSuccessfully ? $"Branch '{candidate}' already exists." : null;
    }

    /// <summary>
    ///  Creates <paramref name="name"/> at HEAD, optionally checking it out
    ///  (<c>git checkout -b</c> vs <c>git branch</c>). Staged and unstaged changes
    ///  are carried over by git, which is exactly what the original commit form does.
    /// </summary>
    public CommitActionResult CreateBranch(string repoPath, string name, bool checkout)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        GitArgumentBuilder args = checkout
            ? new GitArgumentBuilder("checkout") { "-b", name.Quote(), "HEAD" }
            : new GitArgumentBuilder("branch") { name.Quote(), "HEAD" };
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        return new CommitActionResult(result.ExitedSuccessfully, result.AllOutput);
    }

    // ---------------- helpers ----------------

    // The encoding git expects for commit messages in this repository
    // (i18n.commitEncoding), falling back to BOM-less UTF-8 — git's own default.
    // GitModule.CommitEncoding already resolves the configured name (and maps the
    // "utf-8" spelling onto the BOM-less encoding); it only throws if git config
    // itself is unreadable, hence the guard.
    private static Encoding CommitEncodingOf(GitModule module)
    {
        UTF8Encoding fallback = new(false);
        Encoding resolved;
        try
        {
            resolved = module.CommitEncoding ?? fallback;
        }
        catch
        {
            return fallback;
        }

        if (resolved.CodePage != fallback.CodePage)
        {
            return resolved;
        }

        // UTF-8 codepage: either nothing is configured, or it is configured as UTF-8,
        // or the configured name is a spelling .NET's encoding table does not know
        // (git/iconv accept e.g. "cp1251") and the core silently handed the UTF-8
        // default back. Try the codepage mapping; otherwise stay on UTF-8 — always our
        // own BOM-less instance, because a BOM in the message would end up in the
        // commit text (Encoding.UTF8 / Encoding.Default do emit one).
        Encoding? byCodePage = ResolveByCodePage(
            Exec(module, new GitArgumentBuilder("config") { "--get", "i18n.commitEncoding" }));

        return byCodePage is not null && byCodePage.CodePage != fallback.CodePage ? byCodePage : fallback;
    }

    // Maps encoding names .NET's table misses onto a codepage: "cp1251"/"1251" → 1251.
    // Returns null for anything else (including UTF-8 spellings, where the BOM-less
    // default the core returned is exactly what we want).
    private static Encoding? ResolveByCodePage(string? configured)
    {
        string name = (configured ?? string.Empty).Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return null;
        }

        string digits = name.StartsWith("cp", StringComparison.Ordinal) ? name[2..] : name;
        if (!int.TryParse(digits, out int codePage))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch
        {
            return null;
        }
    }

    private static string Exec(GitModule module, ArgumentString args, bool trim = true)
    {
        ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
        if (!result.ExitedSuccessfully)
        {
            return string.Empty;
        }

        return trim ? result.StandardOutput.Trim() : result.StandardOutput;
    }
}
