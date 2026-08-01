using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Routes the two ref-mutating operations upstream runs inside <c>FormProcess</c> —
///  <b>create branch</b> (<c>FormCreateBranch.cs:163</c>, <c>:167</c> for the orphan
///  case) and <b>checkout</b> (<c>FormCheckoutBranch.cs:357</c>,
///  <c>StartCommandLineProcessDialog</c>) — through the port's
///  <see cref="GitProcessDialog"/>, so they get the same console the user already sees
///  for push/pull/merge/commit: the <c>Command to be executed:</c> header, live output,
///  and a footer that keeps the window open on failure.
///  <para>Everything here is <b>one call per call-site</b>: the helper owns the label,
///  the background thread, the <see cref="GitStreamRunner"/> plumbing and the
///  success/abort verdict, and hands back a plain <see cref="bool"/>. What it
///  deliberately does NOT own is what happens afterwards — refreshing the tree, the
///  revision grid or the status bar stays with the caller, because only the caller knows
///  what it has to reload.</para>
///  <para>A failure that never reached git (an unresolvable start point, an empty name)
///  still produces a readable console: the service writes an <c>error: …</c> line into
///  the same stream, so the dialog never shows up blank-and-failed.</para>
/// </summary>
public static class RefProcessRunner
{
    /// <summary>
    ///  Creates <paramref name="name"/> at <paramref name="startPoint"/> inside the
    ///  process dialog. <paramref name="checkout"/> is upstream's "Checkout after
    ///  create" (<c>git checkout -b</c> instead of <c>git branch</c>);
    ///  <paramref name="orphan"/> is its "Create orphan" (<c>git checkout --orphan</c>),
    ///  and <paramref name="clearWorkingTree"/> the "Clear the working tree" that only
    ///  runs — as upstream does — when the orphan checkout succeeded.
    /// </summary>
    /// <returns><see langword="true"/> when git succeeded and the user did not abort.</returns>
    public static Task<bool> CreateBranchAsync(
        Window? owner,
        string repoPath,
        string name,
        string startPoint,
        bool checkout,
        bool orphan = false,
        bool clearWorkingTree = false,
        BranchTagService? service = null)
    {
        BranchTagService branchTags = service ?? new BranchTagService();
        return RunAsync(
            owner,
            string.Format(T("Create branch {0}"), name),
            emit => branchTags.CreateBranchStreaming(
                repoPath, name, startPoint, checkout, emit, orphan, clearWorkingTree));
    }

    /// <summary>
    ///  Checks out a local branch, tag or revision, applying
    ///  <paramref name="changesAction"/> to the pending local changes (the answer the
    ///  <see cref="CheckoutBranchDialog"/> returns).
    /// </summary>
    /// <returns><see langword="true"/> when git succeeded and the user did not abort.</returns>
    public static Task<bool> CheckoutAsync(
        Window? owner,
        string repoPath,
        string name,
        LocalChangesAction changesAction = LocalChangesAction.DontChange,
        bool includeUntrackedInStash = true,
        BranchTagService? service = null)
    {
        BranchTagService branchTags = service ?? new BranchTagService();
        return RunAsync(
            owner,
            string.Format(T("Checkout {0}"), name),
            emit => branchTags.CheckoutStreaming(
                repoPath, name, emit, changesAction, includeUntrackedInStash));
    }

    /// <summary>
    ///  The full <c>FormCheckoutBranch</c> checkout: a remote branch as a new tracking
    ///  branch (<see cref="CheckoutNewBranchMode.Create"/>, <c>-b … --track</c>), as a
    ///  reset of an existing local branch (<see cref="CheckoutNewBranchMode.Reset"/>,
    ///  <c>-B</c>) or as a detached HEAD (<see cref="CheckoutNewBranchMode.DontCreate"/>).
    ///  A local branch simply falls through to a plain checkout, exactly as in the core
    ///  argument builder.
    ///  <para>The non-fast-forward confirmation upstream shows before a <c>-B</c> that
    ///  discards commits is NOT asked here — it belongs to the call-site, which already
    ///  has <see cref="BranchTagService.GetResetFastForwardInfo"/> for it.</para>
    /// </summary>
    /// <returns><see langword="true"/> when git succeeded and the user did not abort.</returns>
    public static Task<bool> CheckoutBranchAsync(
        Window? owner,
        string repoPath,
        string branchName,
        bool isRemote,
        LocalChangesAction changesAction = LocalChangesAction.DontChange,
        CheckoutNewBranchMode newBranchMode = CheckoutNewBranchMode.DontCreate,
        string? newBranchName = null,
        bool includeUntrackedInStash = true,
        BranchTagService? service = null)
    {
        BranchTagService branchTags = service ?? new BranchTagService();
        return RunAsync(
            owner,
            string.Format(T("Checkout {0}"), branchName),
            emit => branchTags.CheckoutBranchStreaming(
                repoPath, branchName, isRemote, emit, changesAction, newBranchMode, newBranchName,
                includeUntrackedInStash));
    }

    /// <summary>
    ///  Deletes the local branch <paramref name="name"/> inside the process dialog, the
    ///  way upstream's <c>FormDeleteBranch.cs:118-119</c> hands its delete to
    ///  <c>StartCommandLineProcessDialog</c>. <paramref name="force"/> is <c>-D</c>: the
    ///  refusal it overrides (<c>the branch 'x' is not fully merged</c>) is precisely what
    ///  the console exists to show, so the caller must have asked the user first.
    /// </summary>
    /// <returns><see langword="true"/> when git succeeded and the user did not abort.</returns>
    public static Task<bool> DeleteBranchAsync(
        Window? owner,
        string repoPath,
        string name,
        bool force,
        BranchTagService? service = null)
    {
        BranchTagService branchTags = service ?? new BranchTagService();
        return RunAsync(
            owner,
            string.Format(T("Delete branch {0}"), name),
            emit => branchTags.DeleteBranchStreaming(repoPath, name, force, emit));
    }

    /// <summary>
    ///  Deletes <paramref name="branch"/> on <paramref name="remote"/>
    ///  (<c>git push &lt;remote&gt; --delete</c>), as upstream's
    ///  <c>FormDeleteRemoteBranch</c> does. It talks to the network, so the live console
    ///  is the only thing that distinguishes "working" from "hung".
    /// </summary>
    /// <returns><see langword="true"/> when git succeeded and the user did not abort.</returns>
    public static Task<bool> DeleteRemoteBranchAsync(
        Window? owner,
        string repoPath,
        string remote,
        string branch,
        BranchTagService? service = null)
    {
        BranchTagService branchTags = service ?? new BranchTagService();
        return RunAsync(
            owner,
            string.Format(T("Delete branch {0}"), $"{remote}/{branch}"),
            emit => branchTags.DeleteRemoteBranchStreaming(repoPath, remote, branch, emit));
    }

    /// <summary>
    ///  The shared body: run <paramref name="operation"/> in the process dialog and
    ///  reduce its outcome to a boolean. An <b>Abort</b> is never a success, even when
    ///  the killed git had already reported one.
    /// </summary>
    private static async Task<bool> RunAsync(
        Window? owner,
        string label,
        Func<Action<string>, BranchTagResult> operation)
    {
        Window? host = owner ?? MainWindowOrNull();
        if (host is null)
        {
            // No window to own a modal dialog (a headless/unit context): still DO the
            // work rather than silently skipping it — the output simply goes nowhere.
            return await Task.Run(() => operation(_ => { }).Success);
        }

        GitProcessOutcome outcome = await GitProcessDialog.RunStreamingAsync(
            host,
            label,
            emit =>
            {
                BranchTagResult result = operation(emit);

                // The service already wrote a message for the failures that never
                // reached git, so an empty console here can only mean a git that said
                // nothing at all — say so rather than leaving the user guessing.
                if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
                {
                    emit("error: the operation failed and git produced no output.");
                }

                return new GitProcessOutcome(result.Success, result.Output);
            });

        return outcome.Success && !outcome.Aborted;
    }

    private static string T(string english) => Services.TranslationService.T(english);

    private static Window? MainWindowOrNull()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
