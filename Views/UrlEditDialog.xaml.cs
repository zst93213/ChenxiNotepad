using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑网址条目的对话框。回车保存（备注框中回车换行，Ctrl+回车保存），Esc 取消。
/// </summary>
public partial class UrlEditDialog : Window
{
    private readonly UrlEntry? _existing;

    // 无障碍服务实例
    private readonly AccessibilityService _a11y = new();

    /// <summary>编辑结果（点击确定后有效）。</summary>
    public UrlEntry? Result { get; private set; }

    public UrlEditDialog(UrlEntry? existing, IReadOnlyList<string> categories, IReadOnlyList<PasswordEntry> passwords)
    {
        InitializeComponent();
        _existing = existing;

        foreach (var category in categories)
        {
            categoryBox.Items.Add(category);
        }

        // 关联密码：首项为“（无）”，其余为密码条目标题
        linkedPasswordBox.Items.Add(new ComboBoxItem { Content = "（无）", Tag = null });
        foreach (var password in passwords)
        {
            linkedPasswordBox.Items.Add(new ComboBoxItem { Content = password.Title, Tag = password.Id });
        }

        if (existing is not null)
        {
            titleBox.Text = existing.Title;
            urlBox.Text = existing.Url;
            accountBox.Text = existing.Account;
            categoryBox.Text = existing.Category;
            notesBox.Text = existing.Notes;
            SelectLinkedPassword(existing.LinkedPasswordId);
            Title = "编辑网址";
        }
        else
        {
            categoryBox.Text = categories.Count > 0 ? categories[0] : "默认";
            linkedPasswordBox.SelectedIndex = 0;
            Title = "新建网址";
        }
    }

    private void SelectLinkedPassword(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            linkedPasswordBox.SelectedIndex = 0;
            return;
        }

        for (var i = 0; i < linkedPasswordBox.Items.Count; i++)
        {
            if (linkedPasswordBox.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == id)
            {
                linkedPasswordBox.SelectedIndex = i;
                return;
            }
        }

        linkedPasswordBox.SelectedIndex = 0;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(this) as TextBox;
        var inMultiline = focused is not null && focused.AcceptsReturn;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // 单行字段回车保存；备注框中回车换行，Ctrl+回车保存
        if (ctrl || !inMultiline)
        {
            e.Handled = true;
            SaveAndClose();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
        var title = titleBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            _a11y.Announce(titleBox, "请输入站点名称。");
            titleBox.Focus();
            return;
        }

        var category = string.IsNullOrWhiteSpace(categoryBox.Text) ? "默认" : categoryBox.Text.Trim();
        var linked = linkedPasswordBox.SelectedItem as ComboBoxItem;

        Result = new UrlEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Url = urlBox.Text?.Trim() ?? string.Empty,
            Account = accountBox.Text?.Trim() ?? string.Empty,
            Category = category,
            Notes = notesBox.Text ?? string.Empty,
            LinkedPasswordId = linked?.Tag as string,
            IsFavorite = _existing?.IsFavorite ?? false,
            LastCheckedTime = _existing?.LastCheckedTime,
            LastCheckStatus = _existing?.LastCheckStatus ?? "Unknown",
            CreatedTime = _existing?.CreatedTime ?? DateTime.Now,
            ModifiedTime = DateTime.Now
        };

        DialogResult = true;
        Close();
    }
}
