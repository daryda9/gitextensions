using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitCommands;
using GitExtensions.Avalonia.Views;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  Which mid-operation state a repository is in. All three flags are read
///  <b>structurally</b> (unmerged index entries, and the presence of
///  <c>.git/rebase-merge</c> / <c>.git/rebase-apply</c> plus their
///  <c>applying</c>/<c>rebasing</c> marker files) — never by matching git's
///  message text, which is localised on this user's machine.
/// </summary>
/// <param name="ConflictedMerge">
///  The index has unmerged entries (<c>git ls-files -u</c>), i.e. upstream's
///  <c>GitModule.InTheMiddleOfConflictedMerge()</c>.
/// </param>
/// <param name="Patch">
///  A <c>git am</c> session is in progress (upstream's
///  <c>GitModule.InTheMiddleOfPatch()</c>: the rebase dir exists and has no
///  <c>rebasing</c> marker).
/// </param>
/// <param name="Rebase">
///  A rebase is in progress (upstream's <c>GitModule.InTheMiddleOfRebase()</c>:
///  the rebase dir exists and has no <c>applying</c> marker).
/// </param>
public readonly record struct MidOperationState(bool ConflictedMerge, bool Patch, bool Rebase)
{
    /// <summary>A clean repository: no conflicted merge, no <c>am</c>, no rebase.</summary>
    public static readonly MidOperationState None = new(false, false, false);

    /// <summary>True when the repository is in any of the three states.</summary>
    public bool Any => ConflictedMerge || Patch || Rebase;
}

/// <summary>
///  What <see cref="ConflictFlow.HandleAsync"/> did.
/// </summary>
/// <param name="HadConflicts">
///  The repository was in a conflicted merge when the flow ran — this is the
///  <see langword="bool"/> that upstream's <c>HandleMergeConflicts</c> returns,
///  and the signal a caller uses to decide "the operation did not finish".
/// </param>
/// <param name="Asked">The confirmation modal was actually shown to the user.</param>
/// <param name="Accepted">The user answered Yes (or the bypass setting skipped the question).</param>
/// <param name="Resolved">
///  Nothing is unmerged any more, so the caller may go on to offer the commit
///  dialog. This is how the port expresses upstream's <c>offerCommit</c>
///  parameter: upstream hands the flag down into <c>FormResolveConflicts</c>,
///  whereas the port's <see cref="ResolveConflictsDialog.ShowAsync"/> already
///  returns "everything resolved" and lets the caller chain the commit.
/// </param>
public sealed record ConflictFlowResult(bool HadConflicts, bool Asked, bool Accepted, bool Resolved)
{
    /// <summary>The repository was not in a conflicted merge; nothing was asked.</summary>
    public static readonly ConflictFlowResult NoConflicts = new(false, false, false, false);
}

/// <summary>
///  Port of upstream's <c>GitUI.CommandsDialogs.MergeConflictHandler</c>
///  (<c>src/app/GitUI/CommandsDialogs/MergeConflictHandler.cs:9-50</c>): the
///  "There are unresolved merge conflicts, solve conflicts now?" question that
///  every conflict-producing operation is supposed to ask, and the follow-up
///  chain that opens the resolve dialog (and, after it, the patch-apply dialog
///  when a <c>git am</c> session is still open).
///
///  <para>Before this existed the port was <b>silent</b>: after a conflicting
///  merge, pull, cherry-pick, revert or <c>git am</c> the user was asked nothing
///  and only discovered the conflicted index by opening the commit dialog.</para>
///
///  <para><b>Threading.</b> Every probe here goes through the core
///  <see cref="GitModule"/>, which blocks. Nothing in this class touches git on
///  the UI thread: all reads are wrapped in <see cref="Task.Run"/> (the
///  <c>PushDialog</c> deadlock lesson from HANDOFF §3). The public API is
///  <see langword="async"/> for exactly that reason, and must be awaited from
///  the UI thread.</para>
///
///  <para><b>Deviations from upstream, deliberate:</b>
///  <list type="bullet">
///   <item>upstream's <c>else if (InTheMiddleOfRebase())</c> branch asks "You are
///    in the middle of a rebase, continue rebase?" and opens
///    <c>FormRebase</c>. The port has <b>no</b> rebase dialog and no
///    <c>git rebase --continue</c> anywhere (the only mention is
///    <c>RepositoryProgressBanner.cs:338</c>, which merely prints the command as
///    advice), so asking the question would lead nowhere. Per the "no fake
///    buttons" rule the branch is <b>not</b> ported; the state is still reported
///    in <see cref="MidOperationState.Rebase"/> so it can be wired the day a
///    rebase dialog exists.</item>
///   <item>upstream gates the submodule offer behind two more settings
///    (<c>UpdateSubmodulesOnCheckout</c>, a tri-state, and
///    <c>DontConfirmUpdateSubmodulesOnCheckout</c>). Only the always-ask form is
///    ported; the two settings have no home in the port's
///    <see cref="AppPreferences"/> and inventing them would add two options no UI
///    can reach.</item>
///  </list></para>
/// </summary>
public static class ConflictFlow
{
    /// <summary>
    ///  Reads the repository's mid-operation state. <b>Blocking</b> — call it from
    ///  <see cref="Task.Run"/>, never from the UI thread.
    ///
    ///  <para><see cref="MidOperationState.Patch"/> and
    ///  <see cref="MidOperationState.Rebase"/> come from the core's
    ///  <c>InTheMiddleOfPatch()</c>/<c>InTheMiddleOfRebase()</c>, which are pure
    ///  file-existence checks under the git dir, so they are locale-proof by
    ///  construction. Note that both can be true at once when the rebase dir
    ///  carries neither marker file; upstream resolves that by testing
    ///  <see cref="MidOperationState.Patch"/> first, and so does
    ///  <see cref="HandleAsync"/>.</para>
    /// </summary>
    public static MidOperationState Probe(string repoPath)
    {
        try
        {
            GitModule module = GitContext.CreateModule(repoPath);
            return new MidOperationState(
                ConflictedMerge: module.InTheMiddleOfConflictedMerge(),
                Patch: module.InTheMiddleOfPatch(),
                Rebase: module.InTheMiddleOfRebase());
        }
        catch
        {
            // A probe must never break a refresh path (HANDOFF §3).
            return MidOperationState.None;
        }
    }

    /// <summary>Same as <see cref="Probe"/>, already off the UI thread.</summary>
    public static Task<MidOperationState> ProbeAsync(string repoPath)
        => Task.Run(() => Probe(repoPath));

    /// <summary>
    ///  Port of <c>MergeConflictHandler.HandleMergeConflicts</c>. Call this right
    ///  after any operation that can leave an unmerged index (merge, pull,
    ///  cherry-pick, revert, <c>git am</c>, stash apply/pop …), whether it
    ///  reported failure or not — the state, not the exit code, decides.
    /// </summary>
    /// <param name="owner">Window the modals are shown over.</param>
    /// <param name="repoPath">Working-directory path of the repository.</param>
    /// <param name="offerUpdateSubmodules">
    ///  When there are <b>no</b> conflicts, offer to update the submodules
    ///  (upstream's <c>offerUpdateSubmodules</c>, which routes into
    ///  <c>GitUICommands.UpdateSubmodules</c>). Defaults to
    ///  <see langword="false"/>: upstream's own callers pass it only from the
    ///  checkout paths, and every port call site added for 12.B.3 is a
    ///  conflict-producing operation, not a checkout.
    /// </param>
    /// <returns>
    ///  What happened; <see cref="ConflictFlowResult.Resolved"/> is the flag a
    ///  caller uses to chain the commit dialog.
    /// </returns>
    public static async Task<ConflictFlowResult> HandleAsync(
        Window owner,
        string repoPath,
        bool offerUpdateSubmodules = false)
    {
        ArgumentNullException.ThrowIfNull(owner);

        MidOperationState state = await ProbeAsync(repoPath).ConfigureAwait(true);

        if (!state.ConflictedMerge)
        {
            if (offerUpdateSubmodules)
            {
                await OfferUpdateSubmodulesAsync(owner, repoPath).ConfigureAwait(true);
            }

            return ConflictFlowResult.NoConflicts;
        }

        // Upstream: AppSettings.DontConfirmResolveConflicts || MessageBoxes.ConfirmResolveMergeConflicts(owner)
        bool bypass = await Task.Run(() => new SettingsService().Load().DontConfirmResolveConflicts)
            .ConfigureAwait(true);

        bool asked = !bypass;
        bool accepted = bypass || await ConfirmAsync(
                owner,
                T("MessageBoxes/_unresolvedMergeConflicts.Text",
                    "There are unresolved merge conflicts, solve conflicts now?"),
                T("MessageBoxes/_unresolvedMergeConflictsCaption.Text", "Merge conflicts"))
            .ConfigureAwait(true);

        if (!accepted)
        {
            // Upstream still returns true: the conflicts are there, the caller must
            // not treat the operation as finished just because the user said No.
            return new ConflictFlowResult(HadConflicts: true, asked, Accepted: false, Resolved: false);
        }

        bool resolved = await SolveAsync(owner, repoPath).ConfigureAwait(true);
        return new ConflictFlowResult(HadConflicts: true, asked, Accepted: true, resolved);
    }

    /// <summary>
    ///  Port of <c>MergeConflictHandler.SolveMergeConflicts</c>: resolve the
    ///  conflicts, then — because a conflicted <c>git am</c> leaves <i>both</i> an
    ///  unmerged index and an open patch session — offer to go back to the patch
    ///  dialog and finish the series. Public so a caller that already knows the
    ///  user wants to resolve (e.g. a banner's explicit <c>Resolve…</c> button)
    ///  can skip the question.
    /// </summary>
    /// <returns>True when nothing is unmerged any more.</returns>
    public static async Task<bool> SolveAsync(Window owner, string repoPath)
    {
        ArgumentNullException.ThrowIfNull(owner);

        bool resolved = false;

        // Re-probe: upstream re-tests InTheMiddleOfConflictedMerge() here too,
        // because the state can have changed while the question was on screen.
        MidOperationState state = await ProbeAsync(repoPath).ConfigureAwait(true);
        if (state.ConflictedMerge)
        {
            resolved = await ResolveConflictsDialog.ShowAsync(owner, repoPath).ConfigureAwait(true);
        }

        state = await ProbeAsync(repoPath).ConfigureAwait(true);
        if (state.Patch)
        {
            if (await ConfirmAsync(
                    owner,
                    T("MessageBoxes/_middleOfPatchApply.Text",
                        "You are in the middle of a patch apply, continue patch apply?"),
                    T("MessageBoxes/_middleOfPatchApplyCaption.Text", "Patch apply"))
                .ConfigureAwait(true))
            {
                ApplyPatchDialog dialog = new(repoPath);
                await dialog.ShowDialog(owner).ConfigureAwait(true);
                resolved = !(await ProbeAsync(repoPath).ConfigureAwait(true)).ConflictedMerge;
            }
        }

        // No "continue rebase?" branch: see the class remarks — the port has no
        // rebase dialog, so the question would have nowhere to go.
        return resolved;
    }

    /// <summary>
    ///  Upstream's <c>GitUICommands.UpdateSubmodules</c> reduced to what the port
    ///  can honestly do: nothing at all unless the repository actually declares
    ///  submodules, then the upstream question, then
    ///  <c>git submodule update --init --recursive</c> with its output shown.
    /// </summary>
    private static async Task OfferUpdateSubmodulesAsync(Window owner, string repoPath)
    {
        SubmoduleService service = new();
        bool hasSubmodules = await Task.Run(() =>
        {
            try
            {
                return service.ListSubmodules(repoPath).Count > 0;
            }
            catch
            {
                return false;
            }
        }).ConfigureAwait(true);

        if (!hasSubmodules)
        {
            return;
        }

        if (!await ConfirmAsync(
                owner,
                T("MessageBoxes/_theRepositorySubmodules.Text", "Update submodules on checkout?"),
                T("MessageBoxes/_updateSubmodules.Text", "Update submodules"))
            .ConfigureAwait(true))
        {
            return;
        }

        SubmoduleOpResult result = await Task.Run(() => service.UpdateAll(repoPath)).ConfigureAwait(true);
        if (!result.Success && !string.IsNullOrWhiteSpace(result.Output))
        {
            await InfoAsync(
                owner,
                result.Output,
                T("MessageBoxes/_updateSubmodules.Text", "Update submodules")).ConfigureAwait(true);
        }
    }

    // ---- the modals ----------------------------------------------------------

    /// <summary>
    ///  A themed Yes/No modal with upstream's question glyph — the port has no
    ///  message-box package, and until now every dialog carried its own private
    ///  copy of this method (<c>StashPanel.cs:1077</c>, <c>RemotesDialog.cs:902</c>,
    ///  <c>BisectDialog.cs:456</c>, <c>VerifyDialog.cs:697</c>,
    ///  <c>WorktreesDialog.cs:299</c>, <c>BranchTagPanel.cs:432</c>, …). Reusable
    ///  on purpose so new call sites do not add a seventh.
    /// </summary>
    /// <returns>True for Yes. Closing the window with Esc or the title bar is No.</returns>
    public static async Task<bool> ConfirmAsync(Window owner, string text, string title)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await ShowAsync(owner, text, title, withCancel: true).ConfigureAwait(true);
    }

    /// <summary>Same modal with a single OK button, for reporting an outcome.</summary>
    public static async Task InfoAsync(Window owner, string text, string title)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await ShowAsync(owner, text, title, withCancel: false).ConfigureAwait(true);
    }

    private static async Task<bool> ShowAsync(Window owner, string text, string title, bool withCancel)
    {
        IBrush foreground = Brush("App.Text", Brushes.Gainsboro);
        IBrush accent = Brush("App.Accent", Brushes.SteelBlue);

        bool result = false;

        // Upstream's MessageBoxButtons.YesNo with MessageBoxIcon.Question: a round
        // accent badge holding a "?" (there is no system icon to borrow here).
        Border glyph = new()
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "?",
                FontSize = 20,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        TextBlock message = new()
        {
            Text = text,
            Foreground = foreground,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Button.Content as a string eats '_' as an access key (HANDOFF §3), so the
        // label always goes in a child TextBlock.
        Button primary = new()
        {
            Content = new TextBlock
            {
                Text = withCancel ? T("TranslatedStrings/_yes.Text", "Yes") : "OK",
            },
            MinWidth = 80,
            IsDefault = true,
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(primary);

        Theming.ZoomWindow dialog = new()
        {
            Title = title,
            Width = 420,
            MinWidth = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            // App.Panel, not App.Window: the modal is usually shown straight over the
            // main window, and matching its background made the question look like it
            // was floating loose on the grid (there is no WM frame to separate them
            // on a bare X server, and Xfce/GNOME draw only a thin one).
            Background = Brush("App.Panel", Brushes.Black),
        };

        primary.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        if (withCancel)
        {
            Button secondary = new()
            {
                Content = new TextBlock { Text = T("TranslatedStrings/_no.Text", "No") },
                MinWidth = 80,
                IsCancel = true,
            };
            secondary.Click += (_, _) =>
            {
                result = false;
                dialog.Close();
            };
            buttons.Children.Add(secondary);
        }

        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(message, 1);
        row.Children.Add(glyph);
        row.Children.Add(message);

        StackPanel content = new() { Margin = new Thickness(20) };
        content.Children.Add(row);
        content.Children.Add(buttons);

        dialog.Content = new Border
        {
            BorderBrush = Brush("App.Border", Brushes.DimGray),
            BorderThickness = new Thickness(1),
            Child = content,
        };

        DialogKeys.InstallEscapeClose(dialog);
        DialogKeys.EnsureFocusRoute(dialog);

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    private static IBrush Brush(string key, IBrush fallback)
        => Application.Current?.Resources[key] as IBrush ?? fallback;

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
