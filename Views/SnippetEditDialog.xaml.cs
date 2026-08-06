using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑笔记条目的对话框（记事本模块，不加密）。回车保存（内容框中回车换行，Ctrl+回车保存），Esc 取消。
/// 内容最多 10000 字，实时显示字数统计。
/// </summary>
public partial class SnippetEditDialog : Window
{
    private const int MaxContentLength = 10000;

    private readonly SnippetEntry? _existing;

    // 无障碍服务实例
    private readonly AccessibilityService _a11y = new();

    /// <summary>编辑结果（点击确定后有效）。</summary>
    public SnippetEntry? Result { get; private set; }

    public SnippetEditDialog(SnippetEntry? existing, IReadOnlyList<string> categories)
    {
        InitializeComponent();
        _existing = existing;

        foreach (var category in categories)
        {
            categoryBox.Items.Add(category);
        }

        if (existing is not null)
        {
            titleBox.Text = existing.Title;
            categoryBox.Text = existing.Category;
            contentBox.Text = existing.Content;
            Title = "编辑笔记";
        }
        else
        {
            categoryBox.Text = categories.Count > 0 ? categories[0] : "默认";
            Title = "新建笔记";
        }

        UpdateCharCount();
    }

    /// <summary>
    /// 内容文本变化时更新字数统计，并在接近上限时语音提醒。
    /// </summary>
    private void OnContentTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCharCount();
    }

    /// <summary>
    /// 更新字数统计显示。
    /// </summary>
    private void UpdateCharCount()
    {
        var len = contentBox.Text?.Length ?? 0;
        charCountText.Text = $"{len} / {MaxContentLength}";
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

        // 单行字段回车保存；内容框中回车换行，Ctrl+回车保存
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
            _a11y.Announce(titleBox, "请输入笔记标题。");
            titleBox.Focus();
            return;
        }

        var content = contentBox.Text ?? string.Empty;
        if (content.Length > MaxContentLength)
        {
            _a11y.Announce(contentBox, $"内容超出限制，最多 {MaxContentLength} 字，当前 {content.Length} 字。");
            contentBox.Focus();
            return;
        }

        Result = new SnippetEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Category = string.IsNullOrWhiteSpace(categoryBox.Text) ? "默认" : categoryBox.Text.Trim(),
            Content = content,
            IsFavorite = _existing?.IsFavorite ?? false,
            CreatedTime = _existing?.CreatedTime ?? DateTime.Now,
            ModifiedTime = DateTime.Now
        };

        DialogResult = true;
        Close();
    }
}
