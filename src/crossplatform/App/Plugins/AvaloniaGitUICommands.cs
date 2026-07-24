using System.Drawing;
using System.Windows.Forms;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Plugins;
using GitExtensions.Extensibility.Settings;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Plugins;

/// <summary>
///  A deliberately minimal <see cref="IGitUICommands"/> for the Avalonia/Linux
///  host. Portable plugins interact with the app almost entirely through
///  <c>args.GitModule</c>, so this adapter exposes a live core
///  <see cref="GitModule"/> (built via <see cref="GitContext.CreateModule"/>) and
///  <see cref="GetEffectiveSettings"/>, and throws <see cref="NotSupportedException"/>
///  for the large WinForms surface (<c>Start*Dialog</c>, <c>ShowModelessForm</c>,
///  difftool, …) that has no Avalonia equivalent yet.
///
///  <para>Build a <see cref="GitUIEventArgs"/> from an instance of this class with
///  <c>OwnerForm = null</c>; the sample plugin only touches <c>GitModule</c>.</para>
/// </summary>
public sealed class AvaloniaGitUICommands : IGitUICommands
{
    private readonly GitModule _module;

    public AvaloniaGitUICommands(string repoPath)
    {
        _module = GitContext.CreateModule(repoPath);
    }

    public IGitModule Module => _module;

    /// <summary>The effective settings source for the open repository (git config).</summary>
    public SettingsSource GetEffectiveSettings() => _module.GetEffectiveSettings();

    // ---- events (never raised by this minimal host) ---------------------------------
    public event EventHandler<GitUIEventArgs>? PostBrowseInitialize { add { } remove { } }
    public event EventHandler<GitUIPostActionEventArgs>? PostCheckoutBranch { add { } remove { } }
    public event EventHandler<GitUIPostActionEventArgs>? PostCheckoutRevision { add { } remove { } }
    public event EventHandler<GitUIPostActionEventArgs>? PostCommit { add { } remove { } }
    public event EventHandler<GitUIPostActionEventArgs>? PostEditGitIgnore { add { } remove { } }
    public event EventHandler<GitUIEventArgs>? PostRegisterPlugin { add { } remove { } }
    public event EventHandler<GitUIEventArgs>? PostRepositoryChanged { add { } remove { } }
    public event EventHandler<GitUIPostActionEventArgs>? PostSettings { add { } remove { } }
    public event EventHandler<GitUIPostActionEventArgs>? PostUpdateSubmodules { add { } remove { } }
    public event EventHandler<GitUIEventArgs>? PreCheckoutBranch { add { } remove { } }
    public event EventHandler<GitUIEventArgs>? PreCheckoutRevision { add { } remove { } }
    public event EventHandler<GitUIEventArgs>? PreCommit { add { } remove { } }

    private static NotSupportedException NotSupported([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => new($"{member} is not supported by the Avalonia/Linux plugin host.");

    // ---- properties -----------------------------------------------------------------
    public IBrowseRepo? BrowseRepo { get => null; set => throw NotSupported(); }
    public ILockableNotifier RepoChangedNotifier => throw NotSupported();

    // ---- IServiceProvider -----------------------------------------------------------
    public object? GetService(Type serviceType) => null;

    // ---- module-bound helpers -------------------------------------------------------
    public IGitUICommands WithGitModule(IGitModule module) => throw NotSupported();
    public IGitUICommands WithWorkingDirectory(string? workingDirectory)
        => workingDirectory is { Length: > 0 } dir ? new AvaloniaGitUICommands(dir) : throw NotSupported();

    // ---- everything else: unsupported WinForms surface ------------------------------
    public void AddCommitTemplate(string key, Func<string> addingText, Image? icon, bool isRegex = false) => throw NotSupported();
    public void AddUpstreamRemote(IWin32Window? owner, IRepositoryHostPlugin gitHoster) => throw NotSupported();
    public IGitRemoteCommand CreateRemoteCommand() => throw NotSupported();
    public bool DoActionOnRepo(Func<bool> action) => throw NotSupported();
    public void OpenWithDifftool(IWin32Window? owner, IReadOnlyList<GitRevision?> revisions, string fileName, string? oldFileName, RevisionDiffKind diffKind, bool isTracked, string? customTool = null) => throw NotSupported();
    public void RaisePostBrowseInitialize(IWin32Window? owner) => throw NotSupported();
    public void RaisePostRegisterPlugin(IWin32Window? owner) => throw NotSupported();
    public void RemoveCommitTemplate(string key) => throw NotSupported();
    public bool RunCommand(IReadOnlyList<string> args) => throw NotSupported();
    public void ShowModelessForm(IWin32Window? owner, bool requiresValidWorkingDir, EventHandler<GitUIEventArgs>? preEvent, EventHandler<GitUIPostActionEventArgs>? postEvent, Func<Form> provideForm) => throw NotSupported();
    public bool StartAddFilesDialog(IWin32Window? owner, string? addFiles = null) => throw NotSupported();
    public bool StartAddToGitIgnoreDialog(IWin32Window? owner, bool localExclude, params string[] filePattern) => throw NotSupported();
    public bool StartAmendCommitDialog(IWin32Window? owner, GitRevision revision) => throw NotSupported();
    public bool StartApplyPatchDialog(IWin32Window? owner, string? patchFile = null) => throw NotSupported();
    public bool StartArchiveDialog(IWin32Window? owner = null, GitRevision? revision = null, GitRevision? revision2 = null, string? path = null) => throw NotSupported();
    public void StartBatchFileProcessDialog(string batchFile) => throw NotSupported();
    public bool StartBrowseDialog(IWin32Window? owner, BrowseArguments? args = null) => throw NotSupported();
    public bool StartCheckoutBranch(IWin32Window? owner, IReadOnlyList<ObjectId>? containObjectIds) => throw NotSupported();
    public bool StartCheckoutBranch(IWin32Window? owner, string branch = "", bool remote = false, IReadOnlyList<ObjectId>? containObjectIds = null) => throw NotSupported();
    public bool StartCheckoutRemoteBranch(IWin32Window? owner, string branch) => throw NotSupported();
    public bool StartCheckoutRevisionDialog(IWin32Window? owner, string? revision = null) => throw NotSupported();
    public bool StartCherryPickDialog(IWin32Window? owner = null, GitRevision? revision = null) => throw NotSupported();
    public bool StartCherryPickDialog(IWin32Window? owner, IEnumerable<GitRevision> revisions) => throw NotSupported();
    public bool StartCleanupRepositoryDialog(IWin32Window? owner = null, string? path = null) => throw NotSupported();
    public bool StartCloneDialog(IWin32Window? owner, string url, EventHandler<GitModuleEventArgs> gitModuleChanged) => throw NotSupported();
    public bool StartCloneDialog(IWin32Window? owner, string? url = null, bool openedFromProtocolHandler = false, EventHandler<GitModuleEventArgs>? gitModuleChanged = null) => throw NotSupported();
    public void StartCloneForkFromHoster(IWin32Window? owner, IRepositoryHostPlugin gitHoster, EventHandler<GitModuleEventArgs>? gitModuleChanged) => throw NotSupported();
    public bool StartCommandLineProcessDialog(IWin32Window? owner, IGitCommand command) => throw NotSupported();
    public bool StartCommandLineProcessDialog(IWin32Window? owner, string? command, ArgumentString arguments) => throw NotSupported();
    public bool StartCommitDialog(IWin32Window? owner, string? commitMessage = null, bool showOnlyWhenChanges = false) => throw NotSupported();
    public bool StartCompareRevisionsDialog(IWin32Window? owner = null) => throw NotSupported();
    public bool StartCreateBranchDialog(IWin32Window? owner = null, ObjectId objectId = default, string? newBranchNamePrefix = null) => throw NotSupported();
    public bool StartCreateBranchDialog(IWin32Window? owner, string? branch) => throw NotSupported();
    public void StartCreatePullRequest(IWin32Window? owner) => throw NotSupported();
    public void StartCreatePullRequest(IWin32Window? owner, IRepositoryHostPlugin gitHoster, string? chooseRemote = null, string? chooseBranch = null) => throw NotSupported();
    public bool StartCreateTagDialog(IWin32Window? owner = null, GitRevision? revision = null) => throw NotSupported();
    public bool StartDeleteBranchDialog(IWin32Window? owner, IEnumerable<string> branches) => throw NotSupported();
    public bool StartDeleteBranchDialog(IWin32Window? owner, string branch) => throw NotSupported();
    public bool StartDeleteRemoteBranchDialog(IWin32Window? owner, string remoteBranch) => throw NotSupported();
    public bool StartDeleteTagDialog(IWin32Window? owner, string? tag) => throw NotSupported();
    public bool StartEditGitAttributesDialog(IWin32Window? owner = null) => throw NotSupported();
    public bool StartEditGitIgnoreDialog(IWin32Window? owner, bool localExcludes) => throw NotSupported();
    public bool StartFileEditorDialog(string? filename, bool showWarning = false, int? lineNumber = null) => throw NotSupported();
    public void StartFileHistoryDialog(IWin32Window? owner, string fileName, GitRevision? revision = null, bool filterByRevision = false, bool showBlame = false) => throw NotSupported();
    public bool StartFixupCommitDialog(IWin32Window? owner, GitRevision revision) => throw NotSupported();
    public bool StartFormCommitDiff(ObjectId objectId) => throw NotSupported();
    public bool StartFormatPatchDialog(IWin32Window? owner = null) => throw NotSupported();
    public bool StartGeneralSettingsDialog(IWin32Window? owner) => throw NotSupported();
    public bool StartGitCommandProcessDialog(IWin32Window? owner, ArgumentString arguments) => throw NotSupported();
    public bool StartInitializeDialog(IWin32Window? owner = null, string? dir = null, EventHandler<GitModuleEventArgs>? gitModuleChanged = null) => throw NotSupported();
    public bool StartInteractiveRebase(IWin32Window? owner, string onto) => throw NotSupported();
    public bool StartMailMapDialog(IWin32Window? owner = null) => throw NotSupported();
    public bool StartMergeBranchDialog(IWin32Window? owner, string? branch) => throw NotSupported();
    public bool StartPluginSettingsDialog(IWin32Window? owner) => throw NotSupported();
    public bool StartPullDialog(IWin32Window? owner = null, string? remoteBranch = null, string? remote = null, GitPullAction pullAction = GitPullAction.None) => throw NotSupported();
    public bool StartPullDialogAndPullImmediately(IWin32Window? owner = null, string? remoteBranch = null, string? remote = null, GitPullAction pullAction = GitPullAction.None) => throw NotSupported();
    public bool StartPullDialogAndPullImmediately(out bool pullCompleted, IWin32Window? owner = null, string? remoteBranch = null, string? remote = null, GitPullAction pullAction = GitPullAction.None) => throw NotSupported();
    public void StartPullRequestsDialog(IWin32Window? owner, IRepositoryHostPlugin gitHoster) => throw NotSupported();
    public bool StartPushDialog(IWin32Window? owner, bool pushOnShow) => throw NotSupported();
    public bool StartPushDialog(IWin32Window? owner, bool pushOnShow, bool forceWithLease, out bool pushCompleted, string? branchName = null) => throw NotSupported();
    public bool StartRebase(IWin32Window? owner, string onto) => throw NotSupported();
    public bool StartRebaseDialog(IWin32Window? owner, string? from, string? to, string? onto, bool interactive = false, bool startRebaseImmediately = true) => throw NotSupported();
    public bool StartRebaseDialog(IWin32Window? owner, string? onto) => throw NotSupported();
    public bool StartRebaseDialogWithAdvOptions(IWin32Window? owner, string onto, string from = "") => throw NotSupported();
    public bool StartRemotesDialog(IWin32Window? owner, string? preselectRemote = null, string? preselectLocal = null) => throw NotSupported();
    public bool StartRenameDialog(IWin32Window? owner, string branch) => throw NotSupported();
    public bool StartRepoSettingsDialog(IWin32Window? owner) => throw NotSupported();
    public bool StartResetChangesDialog(IWin32Window? owner, IReadOnlyCollection<GitItemStatus> workTreeFiles, bool onlyWorkTree) => throw NotSupported();
    public bool StartResetCurrentBranchDialog(IWin32Window? owner, string branch) => throw NotSupported();
    public bool StartResolveConflictsDialog(IWin32Window? owner = null, bool offerCommit = true) => throw NotSupported();
    public bool StartRevertCommitDialog(IWin32Window? owner, GitRevision revision) => throw NotSupported();
    public bool StartSettingsDialog(IGitPlugin gitPlugin) => throw NotSupported();
    public bool StartSettingsDialog(IWin32Window? owner, SettingsPageReference? initialPage = null) => throw NotSupported();
    public bool StartSettingsDialog(Type pageType) => throw NotSupported();
    public bool StartSparseWorkingCopyDialog(IWin32Window? owner) => throw NotSupported();
    public bool StartSquashCommitDialog(IWin32Window? owner, GitRevision revision) => throw NotSupported();
    public bool StartStashDialog(IWin32Window? owner = null, bool manageStashes = true, string? initialStash = null) => throw NotSupported();
    public bool StartSubmodulesDialog(IWin32Window? owner) => throw NotSupported();
    public bool StartSyncSubmodulesDialog(IWin32Window? owner) => throw NotSupported();
    public bool StartTheContinueRebaseDialog(IWin32Window? owner) => throw NotSupported();
    public bool StartUpdateSubmoduleDialog(IWin32Window? owner, string submoduleLocalPath, string submoduleParentPath) => throw NotSupported();
    public bool StartUpdateSubmodulesDialog(IWin32Window? owner, string submoduleLocalPath = "") => throw NotSupported();
    public bool StartVerifyDatabaseDialog(IWin32Window? owner = null) => throw NotSupported();
    public bool StartViewPatchDialog(IWin32Window? owner, string? patchFile = null) => throw NotSupported();
    public bool StartViewPatchDialog(string patchFile) => throw NotSupported();
    public bool StashApply(IWin32Window? owner, string stashName) => throw NotSupported();
    public bool StashDrop(IWin32Window? owner, string stashName) => throw NotSupported();
    public bool StashPop(IWin32Window? owner, string stashName = "") => throw NotSupported();
    public bool StashSave(IWin32Window? owner, bool includeUntrackedFiles, bool keepIndex = false, string message = "", IReadOnlyList<string>? selectedFiles = null) => throw NotSupported();
    public bool StashStaged(IWin32Window? owner) => throw NotSupported();
    public void UpdateSubmodules(IWin32Window? owner) => throw NotSupported();
    public bool WorktreeCreate(IWin32Window? owner, string mainWorktreePath) => throw NotSupported();
    public bool WorktreeDelete(IWin32Window? owner, string worktreePath) => throw NotSupported();
    public bool WorktreeSwitch(IWin32Window? owner, string worktreePath) => throw NotSupported();
}
