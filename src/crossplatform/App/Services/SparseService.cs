using GitCommands;
using GitExtensions.Extensibility;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Result of a sparse-checkout operation: a success flag plus the full
///  textual output (git's stdout/stderr, or a short status message). The
///  output is surfaced to the user verbatim.
/// </summary>
public sealed record SparseResult(bool Success, string Output);

/// <summary>
///  Wraps the core git <c>sparse-checkout</c> plumbing for the Avalonia port:
///  read the current pattern set (<c>list</c>), enable cone-mode sparse checkout
///  (<c>init --cone</c>), set the tracked patterns (<c>set</c>) and disable the
///  feature (<c>disable</c>).
///
///  Every operation reuses the Git Extensions core (<see cref="GitModule"/>) via
///  <see cref="GitContext.CreateModule"/>, exactly like
///  <see cref="MaintenanceService"/>. All methods are synchronous, are meant to
///  be called off the UI thread, and never throw for an ordinary git failure —
///  the failure is reported through <see cref="SparseResult"/>.
/// </summary>
public sealed class SparseService
{
    /// <summary>
    ///  Reads the current sparse-checkout pattern set (<c>git sparse-checkout
    ///  list</c>). When sparse checkout is not enabled git exits successfully with
    ///  no output; the caller treats an empty successful result as "disabled".
    /// </summary>
    public SparseResult List(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("sparse-checkout") { "list" });

    /// <summary>
    ///  Enables cone-mode sparse checkout (<c>git sparse-checkout init --cone</c>).
    /// </summary>
    public SparseResult Enable(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("sparse-checkout") { "init", "--cone" });

    /// <summary>
    ///  Sets the tracked directories/patterns (<c>git sparse-checkout set
    ///  &lt;patterns&gt;</c>). Each entry is passed as a separate argument.
    /// </summary>
    public SparseResult SetPatterns(string repoPath, IReadOnlyList<string> patterns)
    {
        GitArgumentBuilder args = new("sparse-checkout") { "set" };
        foreach (string pattern in patterns)
        {
            args.Add(pattern);
        }

        return Run(repoPath, args);
    }

    /// <summary>
    ///  Disables sparse checkout and restores the full working tree
    ///  (<c>git sparse-checkout disable</c>).
    /// </summary>
    public SparseResult Disable(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("sparse-checkout") { "disable" });

    // ---------------------------------------------------------------------
    // Legacy (non-cone) mode — what upstream's FormSparseWorkingCopy does.
    //
    // Upstream never calls `git sparse-checkout` at all: it sets
    // `core.sparsecheckout`, writes `.git/info/sparse-checkout` by hand and
    // refreshes with `read-tree -m -u HEAD`
    // (FormSparseWorkingCopyViewModel.cs: GetPathToSparseCheckoutFile / SaveChanges /
    // RefreshWorkingCopy). That is the only mode that accepts the full .gitignore
    // pattern language, negation included — cone mode rejects it outright:
    //   $ git sparse-checkout set --cone '!gamma'
    //   fatal: Specify directories rather than patterns. …
    // so `!` cannot be expressed in cone mode at all, and the port's cone-only
    // implementation could not reach upstream parity.
    // ---------------------------------------------------------------------

    /// <summary>The rules file upstream edits: <c>&lt;git-dir&gt;/info/sparse-checkout</c>.</summary>
    public static string RulesFilePath(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        return Path.Join(module.ResolveGitInternalPath("info"), "sparse-checkout");
    }

    /// <summary>The <c>core.sparsecheckout</c> key, spelled as upstream writes it.</summary>
    public const string CoreSparseCheckout = "core.sparsecheckout";

    /// <summary>
    ///  Whether legacy sparse checkout is switched on for this repository, i.e.
    ///  <c>core.sparsecheckout</c> is effectively true.
    /// </summary>
    public bool IsLegacyEnabled(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            return string.Equals(
                module.GetEffectiveSetting(CoreSparseCheckout).Trim(),
                bool.TrueString,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///  Reads the rules file verbatim, or an empty string when it does not exist
    ///  (a repository that has never used sparse checkout). Decoded with the same
    ///  encoding upstream writes it in.
    /// </summary>
    public string ReadRules(string repoPath)
    {
        try
        {
            string path = RulesFilePath(repoPath);
            return File.Exists(path)
                ? GitModule.SystemEncoding.GetString(File.ReadAllBytes(path))
                : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///  Applies the legacy sparse configuration in upstream's order: rules file first,
    ///  then <c>core.sparsecheckout</c>, then the working-copy refresh. The refresh is
    ///  what actually adds or removes files, so it runs last and its output is what the
    ///  caller sees.
    /// </summary>
    /// <param name="rules">
    ///  The full text of the rules file, in <c>.gitignore</c> syntax: matched paths are
    ///  <i>included</i>, a leading <c>!</c> excludes and <c>#</c> comments a line.
    /// </param>
    /// <param name="enabled">The value to write to <c>core.sparsecheckout</c>.</param>
    public SparseResult ApplyLegacy(string repoPath, string rules, bool enabled)
    {
        try
        {
            WriteRules(repoPath, rules);
            SetLegacyEnabled(repoPath, enabled);
        }
        catch (Exception ex)
        {
            return new SparseResult(false, ex.Message);
        }

        return RefreshWorkingCopy(repoPath);
    }

    private static void WriteRules(string repoPath, string rules)
    {
        string path = RulesFilePath(repoPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, GitModule.SystemEncoding.GetBytes(rules));
    }

    private static void SetLegacyEnabled(string repoPath, bool enabled)
        => GitContext.CreateModule(repoPath)
            .SetSetting(CoreSparseCheckout, enabled ? "true" : "false");

    /// <summary>
    ///  Re-applies the tree to the index and the working copy
    ///  (<c>git read-tree -m -u HEAD</c>) — upstream's
    ///  <c>RefreshWorkingCopyCommandName</c>. Nothing appears or disappears in the
    ///  working copy until this has run.
    /// </summary>
    public SparseResult RefreshWorkingCopy(string repoPath)
        => Run(repoPath, new GitArgumentBuilder("read-tree") { "-m", "-u", "HEAD" });

    /// <summary>
    ///  Upstream's <c>SaveChangesTurningOffSparseSpecialCase</c>: simply flipping
    ///  <c>core.sparsecheckout</c> to false is not enough, because git keeps honouring
    ///  the rules still in the file and the working copy stays truncated. So the rules
    ///  are rewritten to <c>/*</c> followed by every previous rule commented out, which
    ///  matches everything and is reversible, and only then is the flag cleared.
    ///
    ///  <para>
    ///  <b>The order matters and is not upstream's.</b> Writing the rules, clearing the
    ///  flag and only then refreshing leaves the working copy truncated while reporting
    ///  success: with <c>core.sparsecheckout=false</c>, <c>read-tree -m -u HEAD</c> is a
    ///  silent no-op that never clears the <c>skip-worktree</c> bits, so the excluded
    ///  files stay missing and git still exits 0. Measured on git 2.43.0: after the
    ///  refresh, <c>git ls-files -v</c> still reported <c>S gamma/g.txt</c> and the
    ///  directory was still absent. The bits are only recomputed while sparse checkout
    ///  is <i>still enabled</i>, so the refresh runs first, with the flag on and the
    ///  all-inclusive rules already in place, and the flag is cleared afterwards.
    ///  </para>
    /// </summary>
    /// <returns>The rewritten rules text alongside the refresh outcome.</returns>
    public (SparseResult Result, string Rules) DisableLegacy(string repoPath)
    {
        string rules = ReadRules(repoPath);
        string[] lines = rules.Split('\n');
        string[] effective = lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#')
            .ToArray();

        // An empty or all-commented rules file is *not* "already full": it is exactly
        // upstream's "with the sparse pass-filter empty or missing" case, so still write
        // the explicit "/*" rather than leave git nothing to match.
        bool alreadyFull = effective.Length > 0 && effective.All(l => l == "/*");

        string newRules = alreadyFull
            ? rules
            : string.Join(
                Environment.NewLine,
                new[] { "/*" }.Concat(lines
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Length > 0)
                    .Select(l => string.IsNullOrWhiteSpace(l) || l[0] == '#' ? l : "#" + l)));

        SparseResult result;
        try
        {
            // 1. all-inclusive rules on disk, 2. flag still ON so read-tree actually
            // recomputes skip-worktree, 3. refresh restores the files, 4. flag off.
            WriteRules(repoPath, newRules);
            SetLegacyEnabled(repoPath, true);
            result = RefreshWorkingCopy(repoPath);
            SetLegacyEnabled(repoPath, false);
        }
        catch (Exception ex)
        {
            return (new SparseResult(false, ex.Message), newRules);
        }

        return (result, newRules);
    }

    private static SparseResult Run(string repoPath, GitArgumentBuilder args)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            string output = result.AllOutput;
            if (string.IsNullOrWhiteSpace(output))
            {
                output = result.ExitedSuccessfully ? "(completed with no output)" : "(failed with no output)";
            }

            return new SparseResult(result.ExitedSuccessfully, output);
        }
        catch (Exception ex)
        {
            return new SparseResult(false, ex.Message);
        }
    }
}
