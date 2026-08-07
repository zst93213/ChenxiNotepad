using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

public partial class ImportDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private List<UrlEntry> _importedUrls = new();
    private List<PasswordEntry> _importedPasswords = new();

    public List<UrlEntry> ImportedUrls => _importedUrls;
    public List<PasswordEntry> ImportedPasswords => _importedPasswords;

    public ImportDialog()
    {
        InitializeComponent();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (urlImportRadio.IsChecked == true)
        {
            var dlg = new OpenFileDialog { Filter = "书签文件|*.html;*.htm|所有文件|*.*", Title = "选择书签文件" };
            if (dlg.ShowDialog() == true) filePathBox.Text = dlg.FileName;
        }
        else
        {
            var dlg = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*", Title = "选择 CSV 文件" };
            if (dlg.ShowDialog() == true) filePathBox.Text = dlg.FileName;
        }
    }

    private void OnImport(object? sender, RoutedEventArgs? e)
    {
        var path = filePathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            _a11y.Announce(filePathBox, "请先选择文件。");
            return;
        }

        if (urlImportRadio.IsChecked == true)
        {
            _importedUrls = ImportService.ImportBookmarks(path);
            resultBox.Text = $"已导入 {_importedUrls.Count} 条网址。";
            _a11y.Announce(resultBox, $"已导入 {_importedUrls.Count} 条网址。");
        }
        else
        {
            _importedPasswords = ImportService.ImportPasswordsFromCsv(path);
            resultBox.Text = $"已导入 {_importedPasswords.Count} 条密码。\nCSV 格式：title,userName,password,url,phoneNumber,email,notes";
            _a11y.Announce(resultBox, $"已导入 {_importedPasswords.Count} 条密码。");
        }

        okButton.IsEnabled = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is not TextBox)
        {
            e.Handled = true;
            OnImport(null, null);
        }
    }
}
