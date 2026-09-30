using System.Windows;
using System.Windows.Controls;
using TermDeck.Core;

namespace TermDeck.Views;

/// <summary>Asks for the password of a Windows "run as" account. The password is never written to disk.</summary>
public sealed class CredentialWindow : Window
{
    readonly PasswordBox _pwd;
    readonly CheckBox _remember;

    public string Password => _pwd.Password;
    public bool Remember => _remember.IsChecked == true;

    CredentialWindow(string account)
    {
        Title = "Run as another user";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (Application.Current?.MainWindow != null)
            SetResourceReference(BackgroundProperty, "ChromeBg");

        _pwd = new PasswordBox { Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(3) };
        _remember = new CheckBox { Content = "Remember until TermDeck closes", Margin = new Thickness(0, 0, 0, 12) };

        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = $"Password for {account}:", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_pwd);
        root.Children.Add(_remember);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => _pwd.Focus();
    }

    /// <summary>
    /// Returns a credential for <paramref name="account"/>, using the session cache when possible,
    /// otherwise prompting. Returns null if the user cancels.
    /// </summary>
    public static WinCredential? Acquire(Window? owner, string account)
    {
        if (CredentialCache.TryGet(account, out var cached))
            return new WinCredential(account, cached);

        var w = new CredentialWindow(account) { Owner = owner };
        if (w.ShowDialog() != true) return null;
        if (w.Remember) CredentialCache.Remember(account, w.Password);
        return new WinCredential(account, w.Password);
    }
}
