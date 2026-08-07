using System.ComponentModel.Design;
using GitCommands;
using GitCommands.Git;
using GitExtensions.Extensibility.Git;
using GitExtUtils;

namespace GitExtensions.Avalonia;

/// <summary>
///  Shared factory that produces a fully-wired <see cref="GitModule"/> for a
///  repository path, reusing the Git Extensions core service registration
///  (<see cref="ServiceContainerRegistry"/>). This is the single supported way
///  the Avalonia views obtain a live core module — do not hand-roll executor
///  wiring in individual views.
/// </summary>
public static class GitContext
{
    /// <summary>
    ///  Builds a <see cref="GitModule"/> bound to <paramref name="repoPath"/>.
    ///  The module exposes the whole reused core surface (revisions, diff,
    ///  status, stage/commit, branches, …).
    /// </summary>
    public static GitModule CreateModule(string repoPath)
    {
        IGitExecutorProvider provider = Container().GetRequiredService<IGitExecutorProvider>();
        return new GitModule(provider, repoPath);
    }

    /// <summary>
    ///  The core's branch-name normaliser (<c>git check-ref-format</c> rules, as
    ///  upstream's dialogs apply them). Obtained from the same container as everything
    ///  else: the implementation is internal to GitCommands, so the registration is the
    ///  only way to it, and re-deriving those rules by hand is exactly the duplication
    ///  the port avoids.
    /// </summary>
    public static IGitBranchNameNormaliser BranchNameNormaliser()
        => Container().GetRequiredService<IGitBranchNameNormaliser>();

    private static ServiceContainer Container()
    {
        ServiceContainer container = new();
        container.AddService<IGitDirectoryResolver>(new GitDirectoryResolver());
        GitCommands.ServiceContainerRegistry.RegisterServices(container);
        return container;
    }
}
