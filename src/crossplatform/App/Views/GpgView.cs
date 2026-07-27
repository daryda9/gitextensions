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
///  settings entry for it.</para>
///
///  <para>All git work runs off the UI thread and never throws. The view's own
///  wording is translated with the <c>RevisionGpgInfoControl</c> ids where
///  upstream has one; git's messages stay verbatim, as on the command line.</para>
/// </summary>
public sealed class GpgView : UserControl
{
    private static readonly FontFamily Monospace = new("monospace,Consolas,Menlo");

    private static IBrush B(string key) => (IBrush)Application.Current!.Resources[key]!;

    private readonly Grid _rows;
    private readonly Image _commitIcon;
    private readonly Image _tagIcon;
    private readonly TextBox _commitText;
    private readonly TextBox _tagText;
    private readonly Border _tagRow;

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

        Content = _rows;
        Background = B("App.Window");
        ClipToBounds = true;

        _placeholder = null;
        ShowPlaceholder(noCommit: true);
        TranslationService.LanguageChanged += OnLanguageChanged;
    }

    private static TextBox MessageBox() => new()
    {
        AcceptsReturn = true,
        IsReadOnly = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = Monospace,
        Background = B("App.Panel"),
        Foreground = B("App.Text"),
        BorderThickness = new Thickness(0),
        VerticalContentAlignment = VerticalAlignment.Top,
    };

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
        // all: upstream drops the tab, the port states why and stops.
        if (!ObjectId.TryParse(commitHash, out ObjectId objectId) || objectId.IsArtificial)
        {
            ShowPlaceholder(noCommit: false);
            return;
        }

        _placeholder = null;
        _commitText.Text = F(T("Verifying signature of {0}…"),
            commitHash.Length > 8 ? commitHash[..8] : commitHash);
        _commitIcon.IsVisible = false;
        _tagIcon.IsVisible = false;
        SetTagRowVisible(false);

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
                // A newer selection already superseded this verification.
                if (!string.Equals(_commitHash, commitHash, StringComparison.Ordinal))
                {
                    return;
                }

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

    /// <summary>Empties the tab (no repository selected).</summary>
    public void Clear()
    {
        _commitHash = null;
        ShowPlaceholder(noCommit: true);
    }

    private void ShowPlaceholder(bool noCommit)
    {
        _placeholder = noCommit
            ? T("No commit selected.")
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
