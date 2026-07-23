using Microsoft.VisualStudio.Threading;

namespace GitUI;

/// <summary>
///  Compiled into the cross-platform GitExtUtils assembly so it can reach
///  <see cref="ThreadHelper"/>'s internal JoinableTaskContext setter. The real
///  WinForms app initializes this from its message loop; the Avalonia host calls
///  <see cref="InitializeThreading"/> once at startup.
/// </summary>
public static class CrossPlatformBootstrap
{
    public static void InitializeThreading()
    {
        if (!ThreadHelper.HasJoinableTaskContext)
        {
            ThreadHelper.JoinableTaskContext = new JoinableTaskContext();
        }
    }
}
