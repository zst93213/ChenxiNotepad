using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 网址文件夹选择/新建对话框。
/// 供"新建文件夹"与"移动到文件夹"复用：既可从已有文件夹列表中选择，
/// 也可在输入框中直接输入新路径（用 "/" 表示层级，如 "导入/学习"）。
/// 点击"新建并选中"会把当前输入框中的路径加入列表并选中；确定后通过 Result 返回该路径。
/// </summary>
public partial class UrlFolderDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private readonly List<string> _folders;

    /// <summary>选中的文件夹路径（点击确定后有效）。</summary>
    public string? Result { get; private set; }

    /// <param name="existingFolders">已存在的文件夹路径列表。</param>
    /// <param name="title">窗口标题。</param>
    /// <param name="hint">顶部说明文字。</param>
    /// <param name="initialPath">输入框初始内容（可空）。</param>
    public UrlFolderDialog(IReadOnlyList<string> existingFolders, string title, string hint, string? initialPath = null)
    {
        InitializeComponent();
        Title = title;
        hintText.Text = hint;
        _folders = new List<string>(existingFolders);
        folderPathBox.Text = initialPath ?? string.Empty;
        RefreshList();
    }

    private void RefreshList()
    {
        folderList.Items.Clear();
        foreach (var f in _folders)
        {
            var item = new ListBoxItem { Content = f };
            AutomationProperties.SetName(item, f);
            folderList.Items.Add(item);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (folderList.Items.Count > 0)
            folderList.SelectedIndex = 0;
        _a11y.Announce(this, Title);
    }

    private void folderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (folderList.SelectedItem is ListBoxItem item)
        {
            folderPathBox.Text = item.Content as string ?? string.Empty;
        }
    }

    /// <summary>把输入框中的路径规范化：去除首尾空白，去掉重复的 "/" 与首尾 "/"。</summary>
    private static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        return string.Join("/", parts);
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var path = NormalizePath(folderPathBox.Text);
        if (path is null)
        {
            _a11y.Announce(folderPathBox, "请输入文件夹名。");
            folderPathBox.Focus();
            return;
        }
        if (!_folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _folders.Add(path);
            RefreshList();
            // 选中刚加入的项
            for (var i = 0; i < folderList.Items.Count; i++)
            {
                if (folderList.Items[i] is ListBoxItem li && (li.Content as string) == path)
                {
                    folderList.SelectedIndex = i;
                    break;
                }
            }
            _a11y.Announce(folderPathBox, $"已加入列表：{path}。");
        }
        else
        {
            _a11y.Announce(folderPathBox, "该文件夹已在列表中。");
        }
        folderPathBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
        var path = NormalizePath(folderPathBox.Text);
        if (path is null)
        {
            _a11y.Announce(folderPathBox, "请输入或选择一个文件夹。");
            folderPathBox.Focus();
            return;
        }
        Result = path;
        DialogResult = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveAndClose();
        }
    }
}
