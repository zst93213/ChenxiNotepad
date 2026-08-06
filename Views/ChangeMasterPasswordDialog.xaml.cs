using System.Windows;
using System.Windows.Input;
using BlindNotepad.Services;

namespace BlindNotepad;

public partial class ChangeMasterPasswordDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private readonly string _currentPassword;

    public string? NewPassword { get; private set; }

    public ChangeMasterPasswordDialog(string currentPassword)
    {
        InitializeComponent();
        _currentPassword = currentPassword;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveAndClose();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
        var old = oldPasswordBox.Password;
        if (!string.Equals(old, _currentPassword, StringComparison.Ordinal))
        {
            _a11y.Announce(oldPasswordBox, "当前主密码不正确。");
            oldPasswordBox.Focus();
            return;
        }

        var newPwd = newPasswordBox.Password;
        if (newPwd.Length < 6)
        {
            _a11y.Announce(newPasswordBox, "新主密码至少需要 6 位。");
            newPasswordBox.Focus();
            return;
        }

        var confirm = confirmPasswordBox.Password;
        if (!string.Equals(newPwd, confirm, StringComparison.Ordinal))
        {
            _a11y.Announce(confirmPasswordBox, "两次输入的新密码不一致。");
            confirmPasswordBox.Focus();
            return;
        }

        NewPassword = newPwd;
        DialogResult = true;
        Close();
    }
}
