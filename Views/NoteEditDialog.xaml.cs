using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑日记条目的对话框（日记模块，加密存储）。回车保存（内容框中回车换行，Ctrl+回车保存），Esc 取消。
/// 内容最多 10000 字，实时显示字数统计。
/// 新建时自动填充日期标题。编辑时光标定位到内容末尾方便续写。
/// 支持天气和心情标签。
/// </summary>
public partial class NoteEditDialog : Window
{
    private const int MaxContentLength = 10000;

    private readonly AccessibilityService _a11y = new();
    private readonly NoteEntry? _existing;
    private readonly bool _isNew;

    public NoteEntry? Result { get; private set; }

    public NoteEditDialog(NoteEntry? existing)
    {
        InitializeComponent();
        _existing = existing;
        _isNew = existing is null;

        // 填充天气选项
        var weathers = new[] { "", "晴", "多云", "阴", "小雨", "中雨", "大雨", "雷阵雨", "雪", "雾", "沙尘" };
        foreach (var w in weathers) weatherBox.Items.Add(w);

        // 填充心情选项
        var moods = new[] { "", "开心", "平静", "兴奋", "感恩", "疲惫", "焦虑", "低落", "愤怒", "迷茫", "释然" };
        foreach (var m in moods) moodBox.Items.Add(m);

        if (existing is not null)
        {
            titleBox.Text = existing.Title;
            categoryBox.Text = existing.Category;
            weatherBox.Text = existing.Weather;
            moodBox.Text = existing.Mood;
            contentBox.Text = existing.Content;
            Title = "编辑日记";
        }
        else
        {
            // 新建时自动填充日期标题
            var now = DateTime.Now;
            var weekdays = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            titleBox.Text = $"{now:yyyy-MM-dd} {weekdays[(int)now.DayOfWeek]}";
            Title = "新建日记";
        }

        UpdateCharCount();

        // 编辑时光标定位到内容末尾（续写），新建时焦点在标题
        if (!_isNew)
        {
            Loaded += (_, _) =>
            {
                contentBox.Focus();
                contentBox.CaretIndex = contentBox.Text.Length;
                contentBox.ScrollToEnd();
            };
        }
    }

    private void OnContentTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCharCount();
    }

    private void UpdateCharCount()
    {
        var len = contentBox.Text?.Length ?? 0;
        charCountText.Text = $"{len} / {MaxContentLength}";
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
        var title = titleBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            _a11y.Announce(titleBox, "请输入日记标题。");
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

        Result = new NoteEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Category = string.IsNullOrWhiteSpace(categoryBox.Text) ? "默认" : categoryBox.Text.Trim(),
            Weather = weatherBox.Text?.Trim() ?? "",
            Mood = moodBox.Text?.Trim() ?? "",
            Content = content,
            IsFavorite = _existing?.IsFavorite ?? false,
            CreatedTime = _existing?.CreatedTime ?? DateTime.Now,
            ModifiedTime = DateTime.Now
        };
        DialogResult = true;
        Close();
    }
}
