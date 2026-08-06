using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑剪贴板内容的对话框。保存后自动复制到剪贴板。
/// </summary>
public partial class ClipboardEditDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private readonly ClipboardHistoryEntry _existing;

    public ClipboardHistoryEntry? Result { get; private set; }

    /// <summary>
    /// 构造函数。existing 为 null 时表示编辑最新剪贴板内容。
    /// </summary>
    public ClipboardEditDialog(ClipboardHistoryEntry? existing = null)
    {
        InitializeComponent();

        if (existing is not null)
        {
            _existing = existing;
            contentBox.Text = existing.Content;
            Title = "编辑剪贴板内容";
        }
        else
        {
            // 从系统剪贴板读取最新内容
            try
            {
                contentBox.Text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch
            {
                contentBox.Text = "";
            }
            _existing = new ClipboardHistoryEntry();
            Title = "编辑最新剪贴板内容";
        }

        UpdateCharCount();

        // 光标定位到末尾
        contentBox.Focus();
        contentBox.CaretIndex = contentBox.Text.Length;
        contentBox.ScrollToEnd();
    }

    private void OnContentTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCharCount();
    }

    private void UpdateCharCount()
    {
        var len = contentBox.Text?.Length ?? 0;
        charCountText.Text = $"{len} 字";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        var focused = FocusManager.GetFocusedElement(this) as TextBox;
        var inMultiline = focused is not null && focused.AcceptsReturn;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl || !inMultiline)
        {
            e.Handled = true;
            SaveAndClose();
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
        var content = contentBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(content))
        {
            _a11y.Announce(contentBox, "内容不能为空。");
            contentBox.Focus();
            return;
        }

        Result = new ClipboardHistoryEntry
        {
            Id = _existing.Id,
            Content = content,
            Timestamp = DateTime.Now,
            IsPinned = _existing.IsPinned,
            SourceApp = _existing.SourceApp,
        };

        // 保存后复制到剪贴板
        try
        {
            Clipboard.SetText(content);
        }
        catch
        {
            // 忽略剪贴板写入失败
        }

        DialogResult = true;
        Close();
    }
}
