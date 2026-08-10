using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Small modal dialog collecting a username and a (masked) password, used to
///  retry a remote operation that failed authentication. Shown with
///  <see cref="ShowAsync"/> (a thin wrapper over <see cref="Window.ShowDialog{T}"/>)
///  and resolves to a <see cref="GitCredentials"/> on OK, or <see langword="null"/>
///  on cancel / close.
/// </summary>
public sealed class CredentialsDialog : Theming.ZoomWindow
{
    private readonly TextBox _username;
    private readonly TextBox _password;

    // Promoted from locals only because ApplyTranslations has to re-label them; the
    // layout is untouched.
    private readonly TextBlock _prompt;
    private readonly TextBlock _usernameLabel;
    private readonly TextBlock _passwordLabel;
    private readonly Button _ok;
    private readonly Button _cancel;

    public CredentialsDialog()
    {
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        _prompt = new TextBlock
        {
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        _username = new TextBox();
        _username.KeyDown += OnKeyDown;

        _password = new TextBox { PasswordChar = '•' };
        _password.KeyDown += OnKeyDown;

        _ok = new Button { IsDefault = true, MinWidth = 80 };
        _ok.Click += (_, _) => Accept();

        _cancel = new Button { IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        _cancel.Click += (_, _) => Close(null);

        _usernameLabel = new TextBlock { Margin = new Thickness(0, 0, 0, 2) };
        _passwordLabel = new TextBlock { Margin = new Thickness(0, 8, 0, 2) };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(_ok);
        buttons.Children.Add(_cancel);

        StackPanel root = new() { Margin = new Thickness(16) };
        root.Children.Add(_prompt);
        root.Children.Add(_usernameLabel);
        root.Children.Add(_username);
        root.Children.Add(_passwordLabel);
        root.Children.Add(_password);
        root.Children.Add(buttons);

        Content = root;
        DialogKeys.InstallEscapeClose(this);

        ApplyTranslations();
        TranslationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => TranslationService.LanguageChanged -= OnLanguageChanged;

        Opened += (_, _) => _username.Focus();
    }

    /// <summary>
    ///  Shows the dialog modally over <paramref name="owner"/> and returns the
    ///  entered credentials, or <see langword="null"/> if cancelled.
    /// </summary>
    public static Task<GitCredentials?> ShowAsync(Window owner)
        => new CredentialsDialog().ShowDialog<GitCredentials?>(owner);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void Accept()
    {
        string user = _username.Text ?? string.Empty;
        string pass = _password.Text ?? string.Empty;
        Close(new GitCredentials(user, pass));
    }

    // --- Translations -----------------------------------------------------

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTranslations);

    private void ApplyTranslations()
    {
        // There is no WinForms credentials dialog to borrow ids from — upstream asks
        // for a password inside FormRemoteProcess — so only the two OK/Cancel verbs
        // and the "User name" caption have real ids; the rest goes through the
        // source-text overload and simply stays English until a translator adds it.
        Title = T("Git credentials");
        _prompt.Text = T("Authentication is required for this remote.");

        // GitConfigSettingsPage/label3 is the settings page's "User name" field: same
        // word, and it is the only id in the catalogue that carries it.
        _usernameLabel.Text = T("GitConfigSettingsPage/label3.Text", "Username");
        _passwordLabel.Text = T("Password");
        _username.Watermark = _usernameLabel.Text;
        _password.Watermark = T("Password / token");

        _ok.Content = T("TranslatedStrings/_okText.Text", "OK");
        _cancel.Content = T("TranslatedStrings/_cancelText.Text", "Cancel");
    }

    private static string T(string english) => TranslationService.T(english);

    private static string T(string? key, string english) => TranslationService.T(key, english);
}
