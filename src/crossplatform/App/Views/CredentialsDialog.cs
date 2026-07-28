using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GitExtensions.Avalonia.Services;

namespace GitExtensions.Avalonia.Views;

/// <summary>
///  Small modal dialog collecting a username and a (masked) password, used to
///  retry a remote operation that failed authentication. Shown with
///  <see cref="ShowAsync"/> (a thin wrapper over <see cref="Window.ShowDialog{T}"/>)
///  and resolves to a <see cref="GitCredentials"/> on OK, or <see langword="null"/>
///  on cancel / close.
/// </summary>
public sealed class CredentialsDialog : Window
{
    private readonly TextBox _username;
    private readonly TextBox _password;

    public CredentialsDialog()
    {
        Title = "Git credentials";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (IBrush)Application.Current!.Resources["App.Window"]!;

        TextBlock prompt = new()
        {
            Text = "Authentication is required for this remote.",
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        _username = new TextBox { Watermark = "Username" };
        _username.KeyDown += OnKeyDown;

        _password = new TextBox
        {
            Watermark = "Password / token",
            PasswordChar = '•',
        };
        _password.KeyDown += OnKeyDown;

        Button ok = new() { Content = "OK", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) => Accept();

        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close(null);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        StackPanel root = new() { Margin = new Thickness(16) };
        root.Children.Add(prompt);
        root.Children.Add(new TextBlock { Text = "Username", Margin = new Thickness(0, 0, 0, 2) });
        root.Children.Add(_username);
        root.Children.Add(new TextBlock { Text = "Password", Margin = new Thickness(0, 8, 0, 2) });
        root.Children.Add(_password);
        root.Children.Add(buttons);

        Content = root;
        DialogKeys.InstallEscapeClose(this);

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
}
