using System.Windows;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

public partial class SettingsDialog : Window
{
    private readonly AccessibilityService _a11y = new();

    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        autoLockBox.Text = settings.AutoLockMinutes.ToString();
        expiryBox.Text = settings.PasswordExpiryDays.ToString();
        clipboardBox.Text = settings.ClipboardClearSeconds.ToString();
        auditMaxBox.Text = settings.AuditLogMaxEntries.ToString();
        antiScreenshotCheck.IsChecked = settings.AntiScreenshot;
        auditLogCheck.IsChecked = settings.AuditLogEnabled;
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
        if (!int.TryParse(autoLockBox.Text, out var autoLock) || autoLock < 0)
        {
            _a11y.Announce(autoLockBox, "自动锁定分钟数必须是非负整数。");
            return;
        }
        if (!int.TryParse(expiryBox.Text, out var expiry) || expiry < 0)
        {
            _a11y.Announce(expiryBox, "到期天数必须是非负整数。");
            return;
        }
        if (!int.TryParse(clipboardBox.Text, out var clipSec) || clipSec < 5)
        {
            _a11y.Announce(clipboardBox, "剪贴板清除秒数必须≥5。");
            return;
        }
        if (!int.TryParse(auditMaxBox.Text, out var auditMax) || auditMax < 10)
        {
            _a11y.Announce(auditMaxBox, "审计日志上限必须≥10。");
            return;
        }

        Result.AutoLockMinutes = autoLock;
        Result.PasswordExpiryDays = expiry;
        Result.ClipboardClearSeconds = clipSec;
        Result.AuditLogMaxEntries = auditMax;
        Result.AntiScreenshot = antiScreenshotCheck.IsChecked == true;
        Result.AuditLogEnabled = auditLogCheck.IsChecked == true;

        DialogResult = true;
        Close();
    }
}
