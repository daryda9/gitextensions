using System.Diagnostics;
using GitCommands;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Result of a commit-editing (history-rewrite) operation: a success flag plus
///  the combined git stdout/stderr, surfaced to the user on failure.
/// </summary>
public sealed record CommitEditResult(bool Success, string Output);

/// <summary>
///  Commit-editing operations for the revision grid — reword, squash-with-previous
///  and fixup-with-previous — implemented as safe, non-interactive autosquash-style
///  rebases on the current branch.
///
///  <para>These rewrite history, so the caller is expected to guard against a dirty
///  working tree and confirm with the user first (see <see cref="IsWorkingTreeDirty"/>).</para>
///
///  <para>Interactive rebase is driven non-interactively by scripting the two editors
///  git would otherwise open: a <c>GIT_SEQUENCE_EDITOR</c> shell script rewrites the
///  generated todo list (flipping one line's <c>pick</c> to <c>reword</c>/<c>squash</c>/
///  <c>fixup</c>), and a <c>GIT_EDITOR</c> shell script copies a prepared message file
///  over the commit-message buffer. Both are tiny <c>chmod +x</c> temp scripts, wired
///  through the child process's environment for that one invocation only.</para>
///
///  <para>The git executable path is taken from the reused core
///  (<see cref="GitContext.CreateModule"/> → <c>GitExecutable.Command</c>) so the same
///  git binary the rest of the app uses is invoked here. Every method is synchronous,
///  never throws for an ordinary git failure, and — for the rebase-backed paths —
///  aborts a stuck rebase (<c>git rebase --abort</c>) so a conflict never leaves the
///  repository mid-rebase.</para>
/// </summary>
public sealed class CommitEditService
{
    /// <summary>True if the working tree has staged or unstaged changes (dirty).</summary>
    public bool IsWorkingTreeDirty(string repoPath)
    {
        string git = GitPath(repoPath);
        CommitEditResult r = Run(git, repoPath, null, null, "status", "--porcelain");
        return r.Success && r.Output.Trim().Length > 0;
    }

    /// <summary>True if <paramref name="hash"/> resolves to the current HEAD commit.</summary>
    public bool IsHead(string repoPath, string hash)
    {
        string git = GitPath(repoPath);
        CommitEditResult head = Run(git, repoPath, null, null, "rev-parse", "HEAD");
        CommitEditResult target = Run(git, repoPath, null, null, "rev-parse", hash);
        return head.Success && target.Success &&
               head.Output.Trim().Length > 0 &&
               head.Output.Trim() == target.Output.Trim();
    }

    /// <summary>True if <paramref name="hash"/> has a parent (i.e. is not the root commit).</summary>
    public bool HasParent(string repoPath, string hash)
        => RefExists(GitPath(repoPath), repoPath, hash + "~1");

    /// <summary>Full commit message (subject + body) of <paramref name="hash"/>.</summary>
    public string GetCommitMessage(string repoPath, string hash)
    {
        string git = GitPath(repoPath);
        CommitEditResult r = Run(git, repoPath, null, null, "log", "-1", "--pretty=%B", hash);
        return r.Success ? r.Output : string.Empty;
    }

    /// <summary>
    ///  Default combined message for a squash: the parent's message followed by the
    ///  selected commit's message (what git's squash editor would present).
    /// </summary>
    public string GetCombinedMessage(string repoPath, string hash)
    {
        string parent = GetCommitMessage(repoPath, hash + "~1").Trim();
        string target = GetCommitMessage(repoPath, hash).Trim();
        return string.IsNullOrEmpty(parent) ? target : parent + "\n\n" + target;
    }

    /// <summary>Rewrites the HEAD commit's message (<c>git commit --amend -m</c>).</summary>
    public CommitEditResult AmendHead(string repoPath, string message)
        => Run(GitPath(repoPath), repoPath, null, null, "commit", "--amend", "-m", message);

    /// <summary>
    ///  Rewords a non-HEAD commit via a scripted interactive rebase: the sequence
    ///  editor flips the target line to <c>reword</c> and the message editor supplies
    ///  <paramref name="message"/>.
    /// </summary>
    public CommitEditResult Reword(string repoPath, string hash, string message)
    {
        string git = GitPath(repoPath);
        bool hasParent = RefExists(git, repoPath, hash + "~1");
        string baseArg = hasParent ? hash + "~1" : "--root";
        return RunScriptedRebase(git, repoPath, baseArg, targetIndex: 1, action: "reword", message: message);
    }

    /// <summary>
    ///  Squashes the selected commit into its parent, prompting the caller to supply
    ///  the combined <paramref name="message"/>. The parent must exist.
    /// </summary>
    public CommitEditResult Squash(string repoPath, string hash, string message)
        => SquashOrFixup(repoPath, hash, action: "squash", message: message);

    /// <summary>
    ///  Fixes the selected commit up into its parent, discarding the selected commit's
    ///  message (parent's message is kept). The parent must exist.
    /// </summary>
    public CommitEditResult Fixup(string repoPath, string hash)
        => SquashOrFixup(repoPath, hash, action: "fixup", message: null);

    // ---- shared squash/fixup driver ------------------------------------------------

    private CommitEditResult SquashOrFixup(string repoPath, string hash, string action, string? message)
    {
        string git = GitPath(repoPath);
        if (!RefExists(git, repoPath, hash + "~1"))
        {
            return new CommitEditResult(false, "The root commit has no previous commit to combine with.");
        }

        // Rebase from the grandparent so the todo lists the parent (pick) then the
        // target (squash/fixup). If the parent is the root commit, rebase from --root.
        bool hasGrandparent = RefExists(git, repoPath, hash + "~2");
        string baseArg = hasGrandparent ? hash + "~2" : "--root";
        return RunScriptedRebase(git, repoPath, baseArg, targetIndex: 2, action: action, message: message);
    }

    // ---- scripted interactive rebase ----------------------------------------------

    // Runs `git rebase -i <baseArg>` with a scripted GIT_SEQUENCE_EDITOR that flips the
    // <targetIndex>-th todo command line from `pick` to <action>, and (when a message is
    // supplied, i.e. reword/squash) a scripted GIT_EDITOR that writes that message. On any
    // failure the rebase is aborted so the repo is never left mid-rebase.
    private CommitEditResult RunScriptedRebase(string git, string repoPath, string baseArg, int targetIndex, string action, string? message)
    {
        string seqScript = WriteSequenceEditor(targetIndex, action);
        string? editorScript = null;
        string? messageFile = null;

        try
        {
            string editorEnv;
            if (message is null)
            {
                // fixup: git does not open the message editor, but pin it to a no-op
                // so nothing interactive can ever pop up.
                editorEnv = "true";
            }
            else
            {
                messageFile = Path.GetTempFileName();
                File.WriteAllText(messageFile, message);
                editorScript = WriteMessageEditor(messageFile);
                editorEnv = editorScript;
            }

            List<string> args = new() { "-c", "core.editor=true", "rebase", "-i" };
            if (baseArg == "--root")
            {
                args.Add("--root");
            }
            else
            {
                args.Add(baseArg);
            }

            CommitEditResult result = Run(git, repoPath, seqScript, editorEnv, args.ToArray());

            if (!result.Success)
            {
                // Never leave a half-finished rebase behind.
                _ = Run(git, repoPath, null, null, "rebase", "--abort");
                return new CommitEditResult(false, result.Output.Trim() + "\n(rebase aborted — repository left unchanged)");
            }

            return result;
        }
        finally
        {
            TryDelete(seqScript);
            TryDelete(editorScript);
            TryDelete(messageFile);
        }
    }

    // ---- editor script generation -------------------------------------------------

    // GIT_SEQUENCE_EDITOR: rewrites the rebase todo, flipping the <index>-th command
    // line (skipping comment/blank lines) from `pick` to <action>.
    private static string WriteSequenceEditor(int index, string action)
    {
        string script =
            "#!/bin/sh\n" +
            "awk 'BEGIN{n=0} /^[[:space:]]*#/||/^[[:space:]]*$/{print;next} " +
            $"{{n++; if(n=={index}){{sub(/^pick/,\"{action}\")}} print}}' " +
            "\"$1\" > \"$1.tmp\" && mv \"$1.tmp\" \"$1\"\n";
        return WriteExecutableScript(script, "gex-seq-");
    }

    // GIT_EDITOR: copies the prepared message file over git's message buffer ($1).
    private static string WriteMessageEditor(string messageFile)
    {
        string script = "#!/bin/sh\ncp \"" + messageFile + "\" \"$1\"\n";
        return WriteExecutableScript(script, "gex-ed-");
    }

    private static string WriteExecutableScript(string content, string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    // ---- low-level git invocation --------------------------------------------------

    private bool RefExists(string git, string repoPath, string rev)
        => Run(git, repoPath, null, null, "rev-parse", "--verify", "-q", rev).Success;

    // Resolves the git executable path from the reused core, falling back to "git".
    private static string GitPath(string repoPath)
    {
        try
        {
            string command = GitContext.CreateModule(repoPath).GitExecutable.Command;
            return string.IsNullOrWhiteSpace(command) ? "git" : command;
        }
        catch
        {
            return "git";
        }
    }

    // Runs git with the given arguments, optionally wiring scripted editors for that
    // one invocation via the child process environment. Returns success + combined
    // stdout/stderr; never throws for an ordinary non-zero exit.
    private static CommitEditResult Run(string git, string repoPath, string? sequenceEditor, string? editor, params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = git,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Keep everything non-interactive.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (sequenceEditor is not null)
        {
            psi.Environment["GIT_SEQUENCE_EDITOR"] = sequenceEditor;
        }

        if (editor is not null)
        {
            psi.Environment["GIT_EDITOR"] = editor;
        }

        try
        {
            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            string combined = (stdout + stderr).Trim();
            return new CommitEditResult(process.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return new CommitEditResult(false, ex.Message);
        }
    }
}
