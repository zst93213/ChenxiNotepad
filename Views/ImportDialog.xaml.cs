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

    /// <summary>结构化导入结果（仅 URL 导入时填充），用于主窗口后续走健康检查流程。</summary>
    public ImportService.BookmarkImportResult? BookmarkResult { get; private set; }

    /// <summary>是否勾选了"导入后自动检测失效网址"。</summary>
    public bool WantsHealthCheck => healthCheckAfterImportCheck.IsChecked == true && BookmarkResult is not null;

    public List<UrlEntry> ImportedUrls => _importedUrls;
    public List<PasswordEntry> ImportedPasswords => _importedPasswords;

    public ImportDialog()
    {
        InitializeComponent();
        var dummy = new RoutedEventArgs();
        FlatFolderCheck_Changed(urlImportRadio, dummy);
        UrlImportRadio_Changed(urlImportRadio, dummy);
    }

    private void FlatFolderCheck_Changed(object sender, RoutedEventArgs? e)
    {
        flatFolderBox.IsEnabled = flatFolderCheck.IsChecked == true;
    }

    private void UrlImportRadio_Changed(object sender, RoutedEventArgs? e)
    {
        var isUrl = urlImportRadio.IsChecked == true;
        urlOptionsGroup.IsEnabled = isUrl;
        healthCheckAfterImportCheck.IsEnabled = isUrl;
        if (!isUrl)
        {
            flatFolderCheck.IsChecked = false;
            healthCheckAfterImportCheck.IsChecked = false;
        }
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
            string? flatFolder = flatFolderCheck.IsChecked == true ? flatFolderBox.Text?.Trim() : null;
            var result = ImportService.ImportBookmarksDetailed(path, flatFolder);
            BookmarkResult = result;
            _importedUrls = result.SuccessEntries;

            var sb = new System.Text.StringBuilder();
            sb.Append($"共解析到 {result.TotalScanned} 条书签。")
              .Append($"成功 {result.SuccessCount} 条，跳过 {result.Failures.Count} 条。")
              .Append($"本次使用{(result.UsedFlatMode ? "合并模式" : "层级模式")}，")
              .Append(result.UsedFlatMode ? $"根文件夹：{result.UsedRootFolder}。" : "保留原书签字典结构。");
            summaryText.Text = sb.ToString();
            _a11y.Announce(summaryText, summaryText.Text);

            var detail = new System.Text.StringBuilder();
            detail.AppendLine("=== 导入汇总 ===");
            detail.AppendLine($"解析总数：{result.TotalScanned}");
            detail.AppendLine($"成功导入：{result.SuccessCount}");
            detail.AppendLine($"跳过/失败：{result.Failures.Count}");
            detail.AppendLine($"模式：{(result.UsedFlatMode ? $"合并到同一文件夹（{result.UsedRootFolder}）" : "保留层级")}");

            if (result.SuccessCount > 0)
            {
                detail.AppendLine();
                detail.AppendLine("=== 已导入条目预览（前 50 条） ===");
                int preview = Math.Min(50, result.SuccessEntries.Count);
                for (var i = 0; i < preview; i++)
                {
                    var u = result.SuccessEntries[i];
                    detail.AppendLine($"  [{i + 1}] [{u.Category}] {u.Title}  {u.Url}");
                }
                if (preview < result.SuccessEntries.Count)
                    detail.AppendLine($"  ...（其余 {result.SuccessEntries.Count - preview} 条已省略）");
            }

            if (result.Failures.Count > 0)
            {
                detail.AppendLine();
                detail.AppendLine("=== 跳过的条目（前 50 条） ===");
                int previewF = Math.Min(50, result.Failures.Count);
                for (var i = 0; i < previewF; i++)
                {
                    var f = result.Failures[i];
                    var name = string.IsNullOrEmpty(f.Title) ? "(无标题)" : f.Title;
                    var addr = string.IsNullOrEmpty(f.Url) ? "(无URL)" : f.Url;
                    detail.AppendLine($"  [{i + 1}] 原因：{f.Reason}  名称：{name}  URL：{addr}");
                }
                if (previewF < result.Failures.Count)
                    detail.AppendLine($"  ...（其余 {result.Failures.Count - previewF} 条已省略）");
            }

            resultBox.Text = detail.ToString();
            okButton.IsEnabled = true;
        }
        else
        {
            _importedPasswords = ImportService.ImportPasswordsFromCsv(path);
            BookmarkResult = null;
            summaryText.Text = $"共导入 {_importedPasswords.Count} 条密码。\nCSV 格式：title,userName,password,url,phoneNumber,email,notes";
            _a11y.Announce(summaryText, $"已导入 {_importedPasswords.Count} 条密码。");
            resultBox.Text = summaryText.Text;
            okButton.IsEnabled = true;
        }
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

