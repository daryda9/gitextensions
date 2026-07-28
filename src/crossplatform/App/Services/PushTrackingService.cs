using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  What a one-shot push (the commit form's "Commit &amp; push", which offers no
///  remote/branch pickers) needs to know before it builds its command line.
/// </summary>
/// <param name="Remote">
///  The remote the branch should go to: its configured <c>branch.&lt;name&gt;.remote</c>
///  when it has one, otherwise <c>origin</c> when that exists, otherwise the first
///  remote. Empty when the repository has no remotes at all.
/// </param>
/// <param name="Branch">The current branch; empty on a detached HEAD.</param>
/// <param name="Upstream">
///  The branch's existing upstream (<c>&lt;remote&gt;/&lt;branch&gt;</c>), or
///  <see langword="null"/> when it has none.
/// </param>
/// <param name="AutoSetupMergeDisabled">
///  <c>branch.autosetupmerge=false</c>, in which case tracking must never be written.
/// </param>
public sealed record PushTracking(
    string Remote,
    string Branch,
    string? Upstream,
    bool AutoSetupMergeDisabled)
{
    public bool HasUpstream => !string.IsNullOrEmpty(Upstream);

    /// <summary>
    ///  <see langword="false"/> when <c>-u</c> must not be passed under any
    ///  circumstance, <see langword="true"/> when the user may be asked for it.
    ///  A branch that already tracks something is never re-pointed silently.
    /// </summary>
    public bool MayOfferTracking
        => !HasUpstream
            && !AutoSetupMergeDisabled
            && Branch.Length > 0
            && Remote.Length > 0;
}

/// <summary>
///  The tracking probe behind a push that has no dialog to ask on: it answers
///  "where does this branch go, and does it already track something?".
///
///  <para>The push dialog resolves the same question from its own controls
///  (<c>PushDialog.ResolveTrackingAsync</c>). The commit form has no controls at
///  all, so it used to pass the two-state <c>PushStreaming</c> overload, which
///  hard-codes <c>-u</c>: every "Commit &amp; push" silently re-pointed the
///  branch's upstream at whatever remote happened to be listed first.</para>
///
///  <para>Shells out to git — call off the UI thread.</para>
/// </summary>
public sealed class PushTrackingService
{
    public PushTracking Probe(string repoPath)
    {
        GitModule module = GitContext.CreateModule(repoPath);
        string branch = module.GetSelectedBranch(emptyIfDetached: true) ?? string.Empty;
        string? upstream = branch.Length > 0 ? ReadUpstream(module, branch) : null;
        string remote = ResolveRemote(module, branch, upstream);

        return new PushTracking(
            remote,
            branch,
            upstream,
            new PushRefsService().AutoSetupMergeDisabled(repoPath));
    }

    // `git rev-parse --abbrev-ref --symbolic-full-name <branch>@{upstream}` is the
    // one form that reports the upstream as git itself resolves it (config-only
    // reads miss the remote half). It exits non-zero when there is no upstream,
    // which is the normal "new branch" case and not an error.
    private static string? ReadUpstream(GitModule module, string branch)
    {
        try
        {
            GitArgumentBuilder args = new("rev-parse")
            {
                "--abbrev-ref",
                "--symbolic-full-name",
                $"{branch}@{{upstream}}",
            };
            ExecutionResult result = module.GitExecutable.Execute(args, throwOnErrorExit: false);
            if (!result.ExitedSuccessfully)
            {
                return null;
            }

            string value = (result.StandardOutput ?? string.Empty).Trim();
            return value.Length > 0 ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ResolveRemote(GitModule module, string branch, string? upstream)
    {
        try
        {
            if (branch.Length > 0)
            {
                string? configured = module.GetSetting($"branch.{branch}.remote");
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    return configured.Trim();
                }
            }

            IReadOnlyList<string> remotes = module.GetRemoteNames();

            // An upstream of "origin/main" names its remote in the first segment;
            // only trust it when a remote of that name really exists, since a
            // branch may legitimately contain a slash.
            if (!string.IsNullOrEmpty(upstream))
            {
                int slash = upstream.IndexOf('/');
                if (slash > 0)
                {
                    string candidate = upstream[..slash];
                    if (remotes.Any(r => string.Equals(r, candidate, StringComparison.Ordinal)))
                    {
                        return candidate;
                    }
                }
            }

            if (remotes.Any(r => string.Equals(r, "origin", StringComparison.Ordinal)))
            {
                return "origin";
            }

            return remotes.Count > 0 ? remotes[0] : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
