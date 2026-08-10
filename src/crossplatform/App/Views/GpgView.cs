using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitCommands;
using GitCommands.Git.Gpg;
using GitExtensions.Avalonia.Services;
using GitExtensions.Avalonia.Theming;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  The browse window's "GPG" tab: the signature verification of the selected
///  commit, and — as a second, separate section — of its annotated tag.
///
///  <para>A faithful port of <c>RevisionGpgInfoControl</c>: the commit row shows
///  the <c>%GG</c> verification message (or "Commit is not signed") next to a
///  status icon (<c>CommitSignatureOk</c> / <c>…Warning</c> for a missing public
///  key / <c>…Error</c>, hidden when there is no signature); the tag row shows the
///  <c>git verify-tag</c> message next to <c>TagOk</c> / <c>TagError</c> /
///  <c>TagMany</c> / <c>TagWarning</c>, and the whole row disappears when the
///  revision carries no annotated tag — which is also what turns the layout from
///  50/50 into 100/0 (<c>ApplyLayout</c>).</para>
///
///  <para>The statuses and messages come from
///  <see cref="GitGpgController"/> itself (it has no WinForms dependency), so the
///  port runs the very same git commands upstream does — <c>log --pretty=%G?</c>,
///  <c>log --pretty=%GG</c>, <c>verify-tag</c> — instead of parsing
///  <c>--show-signature</c> output, which also printed the commit body upstream
///  never shows.</para>
///
///  <para>Upstream <b>removes</b> this tab for an artificial revision (and when
///  the "show GPG information" setting is off, <c>FormBrowse.cs:1291-1303</c>).
///  The port's tab is fixed, so an artificial revision empties the pane with a
///  sentence saying why; the setting is not invented here, because the port has no
///  settings entry for it. <see cref="ShowArtificial"/> is the named entry point
///  for that case, and its sentence <b>names the row</b> ("Working directory" /
///  "Commit index") rather than talking vaguely about "the working tree".</para>
///
///  <para>All git work runs off the UI thread and never throws. The view's own
///  wording is translated with the <c>RevisionGpgInfoControl</c> ids where
///  upstream has one; git's messages stay verbatim, as on the command line.</para>
/// </summary>
public sealed class GpgView : UserControl
{
    // A property, not a field: a static field initialiser can run before the font
    // manager exists, which would cache the fallback for the life of the process.
    private static FontFamily Monospace => Theming.AppFonts.Monospace;

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private readonly Grid _rows;
    private readonly Image _commitIcon;
    private readonly Image _tagIcon;
    private readonly TextBox _commitText;
    private readonly TextBox _tagText;
    private readonly Border _tagRow;

    // Over both rows at once, because the pane IS the two rows: there is no toolbar here
    // and nothing to keep usable, and the tag row's very visibility is part of the answer
    // being recomputed (see SetTagRowVisible).
    private readonly BusyOverlay _busy = new();

    // What the pane currently states without git having said it (no commit
    // selected, artificial revision), so a language switch can re-state it.
    private string? _placeholder;

    // The last statuses, so a language switch re-labels "not signed" without
    // re-verifying.
    private CommitStatus _commitStatus = CommitStatus.NoSignature;
    private TagStatus _tagStatus = TagStatus.NoTag;
    private string? _commitMessage;
    private string? _tagMessage;

    // Identifies the load whose result may still be applied.
    private string? _commitHash;

    // Set while the pane shows an artificial row's placeholder, so a language
    // switch re-states it with the row's name (see OnLanguageChanged).
    private ArtificialDiff? _artificial;

    public GpgView()
    {
        _commitIcon = new Image { Width = 16, Height = 16, IsVisible = false };
        _tagIcon = new Image { Width = 16, Height = 16, IsVisible = false };

        _commitText = MessageBox();
        _tagText = MessageBox();

        _rows = new Grid
        {
            RowDefinitions = new RowDefinitions("*,*"),
            Background = B("App.Panel"),
        };

        Border commitRow = Section(_commitIcon, _commitText);
        _tagRow = Section(_tagIcon, _tagText);

        Grid.SetRow(commitRow, 0);
        Grid.SetRow(_tagRow, 1);
        _rows.Children.Add(commitRow);
        _rows.Children.Add(_tagRow);

        Content = new Panel { Children = { _rows, _busy } };
        Background = B("App.Window");
        ClipToBounds = true;

        _placeholder = null;
        ShowPlaceholder(noCommit: true);
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    // TextBoxSurface: see OutputView — the Fluent per-state repaint beats the local
    // Background, so clicking this read-only pane flipped its surface to pure
    // black (dark) / pure white (light).
    private static TextBox MessageBox() => Theming.TextBoxSurface.Apply(
        new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Monospace,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Top,
        },
        B("App.Panel"),
        B("App.Text"));

    // One row of the upstream table layout: the status icon, then the message.
    private static Border Section(Image icon, TextBox text)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        Border iconHolder = new()
        {
            Child = icon,
            Margin = new Thickness(8, 8, 4, 8),
            VerticalAlignment = VerticalAlignment.Top,
        };

        Grid.SetColumn(iconHolder, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(iconHolder);
        grid.Children.Add(text);

        return new Border
        {
            Background = B("App.Panel"),
            BorderBrush = B("App.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    // ------------------------------------------------------------ translation

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);

    private static string F(string format, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, format, args);

    // Shared with the revision grid, the left tree and the other detail panes: one spinner
    // and one word for every wait in the window, instead of a private sentence per pane.
    private static string LoadingCaption() => T("RevisionGridControl/_strLoading.Text", "Loading…");

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_placeholder is not null)
        {
            // Re-state whichever placeholder is up, in the new language.
            ShowPlaceholder(noCommit: _commitHash is null);
            return;
        }

        Display(_commitStatus, _commitMessage, _tagStatus, _tagMessage);
    });

    // ------------------------------------------------------------ loading

    /// <summary>
    ///  Verifies <paramref name="commitHash"/> in the repository at
    ///  <paramref name="repoPath"/> and shows the commit's (and its tag's)
    ///  signature status. Heavy git work runs off the UI thread.
    /// </summary>
    public void ShowCommit(string repoPath, string commitHash)
    {
        _commitHash = commitHash;

        // An artificial revision (the working tree, the index) has no signature at
        // all: upstream drops the tab, the port states why and stops. Reached when a
        // caller passes a sentinel hash through here; the named entry point is
        // ShowArtificial, which also says WHICH row it is.
        if (!ObjectId.TryParse(commitHash, out ObjectId objectId) || objectId.IsArtificial)
        {
            _artificial = DiffService.ArtificialFromHash(commitHash);
            ShowPlaceholder(noCommit: false);
            return;
        }

        _artificial = null;
        _placeholder = null;

        // "Verifying signature of <hash>…" is gone: it was the wait spelled out, and the
        // spinner now says that. The BLANKING it came with stays, and here it matters more
        // than in any other pane — BusyOverlay's rule that stale content may be left dimmed
        // underneath is right for rows and patches, but this pane's content is a security
        // claim about a specific object. A green "good signature" left showing for the
        // 250 ms before the veil arrives would be a claim about the wrong commit, so the
        // verdict, both icons and the tag row go before the first git command runs.
        _commitText.Text = string.Empty;
        _commitIcon.IsVisible = false;
        _tagIcon.IsVisible = false;
        SetTagRowVisible(false);
        _busy.Show(LoadingCaption());

        _ = Task.Run(async () =>
        {
            CommitStatus commitStatus = CommitStatus.NoSignature;
            TagStatus tagStatus = TagStatus.NoTag;
            string? commitMessage = null;
            string? tagMessage = null;
            string? error = null;

            try
            {
                GitModule module = GitContext.CreateModule(repoPath);
                GitGpgController controller = new(() => module);

                // GitGpgController reads the tag state off the revision's refs, so
                // they have to be loaded — the dereference refs ("…^{}") are the
                // annotated tags it looks for.
                GitRevision revision = new(objectId)
                {
                    Refs = [.. module.GetRefs(RefsFilter.Tags).Where(r => r.ObjectId == objectId)],
                };

                commitStatus = await controller.GetRevisionCommitSignatureStatusAsync(revision)
                    .ConfigureAwait(false);
                commitMessage = controller.GetCommitVerificationMessage(revision);

                tagStatus = await controller.GetRevisionTagSignatureStatusAsync(revision)
                    .ConfigureAwait(false);
                tagMessage = controller.GetTagVerifyMessage(revision);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // A newer selection already superseded this verification. The overlay is
                // deliberately left up: whatever superseded it either started its own
                // verification (which re-showed the same spinner) or wrote a placeholder
                // through ShowPlaceholder, which takes it down. Hiding it from here would
                // clear the spinner of a load that is still running.
                if (!string.Equals(_commitHash, commitHash, StringComparison.Ordinal))
                {
                    return;
                }

                // Verified or failed, the wait is over on both branches below.
                _busy.Hide();

                if (error is { Length: > 0 })
                {
                    _placeholder = F(T("Could not verify signature: {0}"), error);
                    _commitText.Text = _placeholder;
                    _commitIcon.IsVisible = false;
                    SetTagRowVisible(false);
                    return;
                }

                Display(commitStatus, commitMessage, tagStatus, tagMessage);
            });
        });
    }

    /// <summary>
    ///  Shows the placeholder of one of the two <b>artificial</b> revision rows —
    ///  the GPG half of the <c>RevisionGridView.ArtificialRevisionSelected</c>
    ///  contract. Nothing is signed until something is committed, so the pane names
    ///  the row and says so instead of keeping the previous commit's verification on
    ///  screen. Upstream removes the whole tab for these rows
    ///  (<c>FormBrowse.cs:1288-1317</c>); the port's tab is fixed, so it states why
    ///  it is empty.
    ///
    ///  <para>Synchronous and cheap: no git command runs.</para>
    /// </summary>
    public void ShowArtificial(ArtificialDiff which)
    {
        // Not a hash any load can match, so a verification still in flight for the
        // previously selected commit cannot overwrite the placeholder.
        _commitHash = which == ArtificialDiff.Index ? DiffService.IndexHash : DiffService.WorkTreeHash;
        _artificial = which;
        ShowPlaceholder(noCommit: false);
    }

    /// <summary>Empties the tab (no repository selected).</summary>
    public void Clear()
    {
        _commitHash = null;
        _artificial = null;
        ShowPlaceholder(noCommit: true);
    }

    private void ShowPlaceholder(bool noCommit)
    {
        // The single choke point for every "there is nothing to verify" answer — the
        // artificial rows, Clear(), and the artificial-hash branch of ShowCommit — which
        // is precisely why the spinner is taken down here rather than at each of them: a
        // verification in flight for the previously selected commit will bail out on the
        // hash check and never touch the pane again, so this is the last hand on it.
        _busy.Hide();

        _placeholder = noCommit
            ? T("No commit selected.")
            : _artificial is { } which
                ? F(T("{0} is not a commit: there is no signature to verify until it is committed."),
                    ArtificialRevisionName.Of(which))
                : T("Signature information is not available for the working tree.");

        _commitText.Text = _placeholder;
        _commitIcon.IsVisible = false;
        _tagIcon.IsVisible = false;
        _tagText.Text = string.Empty;
        SetTagRowVisible(false);
    }

    // ------------------------------------------------------------ rendering

    private void Display(CommitStatus commitStatus, string? commitMessage, TagStatus tagStatus, string? tagMessage)
    {
        _placeholder = null;
        _commitStatus = commitStatus;
        _commitMessage = commitMessage;
        _tagStatus = tagStatus;
        _tagMessage = tagMessage;

        // ---- commit section ----
        SetIcon(_commitIcon, commitStatus switch
        {
            CommitStatus.GoodSignature => "CommitSignatureOk",
            CommitStatus.MissingPublicKey => "CommitSignatureWarning",
            CommitStatus.SignatureError => "CommitSignatureError",
            _ => null,
        });

        _commitText.Text = commitStatus != CommitStatus.NoSignature && commitMessage is { Length: > 0 }
            ? commitMessage.TrimEnd()
            : T("RevisionGpgInfoControl/_commitNotSigned.Text", "Commit is not signed");

        // ---- tag section: the row itself is the "is there a tag" signal ----
        SetIcon(_tagIcon, tagStatus switch
        {
            TagStatus.OneGood => "TagOk",
            TagStatus.OneBad => "TagError",
            TagStatus.Many => "TagMany",
            TagStatus.NoPubKey => "TagWarning",
            _ => null,
        });

        SetTagRowVisible(tagStatus != TagStatus.NoTag);

        _tagText.Text = tagStatus switch
        {
            TagStatus.NoTag => string.Empty,
            TagStatus.TagNotSigned => T("RevisionGpgInfoControl/_tagNotSigned.Text", "Tag is not signed"),
            _ => (tagMessage ?? string.Empty).TrimEnd(),
        };
    }

    private static void SetIcon(Image target, string? name)
    {
        target.Source = name is null ? null : IconLoader.Load(name);
        target.IsVisible = target.Source is not null;
    }

    // Upstream's ApplyLayout: 50/50 with a tag, 100/0 without it.
    private void SetTagRowVisible(bool visible)
    {
        _tagRow.IsVisible = visible;
        _rows.RowDefinitions[0].Height = new GridLength(visible ? 50 : 100, GridUnitType.Star);
        _rows.RowDefinitions[1].Height = new GridLength(visible ? 50 : 0, GridUnitType.Star);
    }
}
