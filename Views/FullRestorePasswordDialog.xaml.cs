using System.Windows;
using System.Windows.Input;

namespace BlindNotepad;

/// <summary>
/// 全量恢复时输入主密码的对话框。
/// </summary>
public partial class FullRestorePasswordDialog : Window
{
    /// <summary>用户输入的主密码。对话框关闭后可通过此属性获取。</summary>
    public string Password => passwordBox.Password;

    public FullRestorePasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => passwordBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}
