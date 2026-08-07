using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑笔记条目的对话框（记事本模块，不加密）。回车保存（内容框中回车换行，Ctrl+回车保存），Esc 取消。
/// 内容最多 10000 字，实时显示字数统计。
/// 支持自动草稿保存：每 30 秒自动保存草稿到磁盘，关闭时清除。
/// </summary>
public partial class SnippetEditDialog : Window
{
    private const string DraftModuleKey = "snippet";

    private readonly SnippetEntry? _existing;
    private readonly DispatcherTimer? _draftTimer;

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

        // 填充排版预设下拉菜单
        foreach (var preset in TextFormatService.Presets)
        {
            formatPresetBox.Items.Add(preset.Name);
        }
        formatPresetBox.SelectedIndex = 0;

        // 设置草稿自动保存计时器
        _draftTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _draftTimer.Tick += OnDraftTimerTick;
        _draftTimer.Start();

        Closed += (_, _) =>
        {
            _draftTimer?.Stop();
            // 保存成功时清除草稿，取消时保留草稿
            if (Result is not null)
                DraftService.Clear(DraftModuleKey);
        };
    }

    /// <summary>从草稿恢复内容。</summary>
    public void SetDraftContent(string title, string category, string content)
    {
        titleBox.Text = title;
        categoryBox.Text = category;
        contentBox.Text = content;
        UpdateCharCount();
    }

    private void OnDraftTimerTick(object? sender, EventArgs e)
    {
        SaveDraftInternal();
    }

    /// <summary>自动保存草稿到磁盘。</summary>
    private void SaveDraftInternal()
    {
        var title = titleBox.Text?.Trim() ?? "";
        var content = contentBox.Text ?? "";
        // 只在有实际内容时保存
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content)) return;

        var draft = new DraftService.DraftData
        {
            Module = DraftModuleKey,
            Title = title,
            Category = categoryBox.Text?.Trim() ?? "默认",
            Content = content,
            IsNew = _existing is null
        };
        DraftService.Save(DraftModuleKey, draft);
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
        charCountText.Text = $"共 {len} 字";
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

    /// <summary>排版按钮点击：对内容框文本应用选中的排版预设。</summary>
    private void OnFormatClick(object sender, RoutedEventArgs e)
    {
        var content = contentBox.Text ?? "";
        if (string.IsNullOrEmpty(content))
        {
            _a11y.Announce(contentBox, "内容为空，无需排版。");
            return;
        }

        var presetName = formatPresetBox.SelectedItem as string ?? "不排版（原文）";
        var preset = TextFormatService.FindPreset(presetName);
        if (preset is null || preset.Options == TextFormatService.FormatOptions.None)
        {
            _a11y.Announce(formatPresetBox, "选择了不排版，内容保持不变。");
            return;
        }

        var formatted = TextFormatService.Format(content, preset.Options);
        contentBox.Text = formatted;
        UpdateCharCount();
        _a11y.Announce(formatButton, $"已应用「{preset.Name}」排版。");
        contentBox.Focus();
        contentBox.CaretIndex = contentBox.Text.Length;
    }

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
