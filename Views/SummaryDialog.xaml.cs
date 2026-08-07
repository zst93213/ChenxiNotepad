using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 文章总结对话框。显示文本摘要（可编辑）和提取的标题/标签，支持复制到剪贴板。
/// Tab 键在摘要编辑框、标题列表、标签之间切换。
/// </summary>
public partial class SummaryDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private readonly ClipboardService _clipboard = new();

    /// <summary>创建总结对话框。</summary>
    /// <param name="title">文章标题</param>
    /// <param name="content">文章内容</param>
    public SummaryDialog(string title, string content)
    {
        InitializeComponent();

        // 生成摘要
        var summary = TextSummaryService.Summarize(content, maxSentences: 3);
        summaryBox.Text = string.IsNullOrEmpty(summary) ? "（内容太短，无法生成摘要）" : summary;

        // 提取标题候选
        var titles = TextSummaryService.ExtractTitles(content, title);
        foreach (var t in titles)
        {
            titleListBox.Items.Add(t);
        }
        if (titleListBox.Items.Count > 0)
            titleListBox.SelectedIndex = 0;

        // 提取话题标签
        var tags = TextSummaryService.ExtractTags(content, maxCount: 5);
        tagsText.Text = tags.Count > 0 ? string.Join(" ", tags) : "（无可用标签）";
    }

    /// <summary>标题列表双击：复制到剪贴板。</summary>
    private void OnTitleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CopySelectedTitle();
    }

    /// <summary>标题列表键盘：回车复制，Ctrl+C 复制。</summary>
    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CopySelectedTitle();
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            CopySelectedTitle();
        }
    }

    /// <summary>复制摘要到剪贴板。</summary>
    private void OnCopySummary(object sender, RoutedEventArgs e)
    {
        var text = summaryBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _a11y.Announce(copySummaryButton, "摘要为空，无法复制。");
            return;
        }
        _clipboard.CopyToClipboard(text);
        _a11y.Announce(copySummaryButton, $"已复制摘要到剪贴板，共 {text.Length} 字。");
    }

    /// <summary>复制选中的标题到剪贴板。</summary>
    private void OnCopySelectedTitle(object sender, RoutedEventArgs e)
    {
        CopySelectedTitle();
    }

    /// <summary>复制话题标签到剪贴板。</summary>
    private void OnCopyTags(object sender, RoutedEventArgs e)
    {
        var text = tagsText.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text == "（无可用标签）")
        {
            _a11y.Announce(copyTagsButton, "没有可用标签。");
            return;
        }
        _clipboard.CopyToClipboard(text);
        _a11y.Announce(copyTagsButton, "已复制话题标签到剪贴板。");
    }

    /// <summary>复制当前选中的标题。</summary>
    private void CopySelectedTitle()
    {
        if (titleListBox.SelectedItem is not string title)
        {
            _a11y.Announce(titleListBox, "请先选择一个标题。");
            return;
        }
        _clipboard.CopyToClipboard(title);
        _a11y.Announce(titleListBox, $"已复制标题：{title}");
    }
}
