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
        ServiceContainer container = new();
        container.AddService<IGitDirectoryResolver>(new GitDirectoryResolver());
        GitCommands.ServiceContainerRegistry.RegisterServices(container);

        IGitExecutorProvider provider = container.GetRequiredService<IGitExecutorProvider>();
        return new GitModule(provider, repoPath);
    }
}
