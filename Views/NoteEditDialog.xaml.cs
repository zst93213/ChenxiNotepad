using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑日记条目的对话框（日记模块，加密存储）。回车保存（内容框中回车换行，Ctrl+回车保存），Esc 取消。
/// 内容最多 10000 字，实时显示字数统计。
/// 新建时自动填充日期标题。编辑时光标定位到内容末尾方便续写。
/// 支持天气和心情标签。
/// 支持自动草稿保存：每 30 秒自动保存草稿到磁盘，关闭时清除。
/// </summary>
public partial class NoteEditDialog : Window
{
    private const string DraftModuleKey = "note";

    private readonly AccessibilityService _a11y = new();
    private readonly NoteEntry? _existing;
    private readonly bool _isNew;
    private readonly DispatcherTimer? _draftTimer;

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

        // 填充排版预设下拉菜单
        foreach (var preset in TextFormatService.Presets)
        {
            formatPresetBox.Items.Add(preset.Name);
        }
        formatPresetBox.SelectedIndex = 0;

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

        // 设置草稿自动保存计时器
        _draftTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _draftTimer.Tick += OnDraftTimerTick;
        _draftTimer.Start();

        Closed += (_, _) =>
        {
            _draftTimer?.Stop();
            SpeechService.Stop();
            if (Result is not null)
                DraftService.Clear(DraftModuleKey);
        };
    }

    /// <summary>从草稿恢复内容。</summary>
    public void SetDraftContent(string title, string category, string weather, string mood, string content)
    {
        titleBox.Text = title;
        categoryBox.Text = category;
        weatherBox.Text = weather;
        moodBox.Text = mood;
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
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content)) return;

        var draft = new DraftService.DraftData
        {
            Module = DraftModuleKey,
            Title = title,
            Category = categoryBox.Text?.Trim() ?? "默认",
            Weather = weatherBox.Text?.Trim() ?? "",
            Mood = moodBox.Text?.Trim() ?? "",
            Content = content,
            IsNew = _isNew
        };
        DraftService.Save(DraftModuleKey, draft);
    }

    private void OnContentTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCharCount();
    }

    private void UpdateCharCount()
    {
        var len = contentBox.Text?.Length ?? 0;
        charCountText.Text = $"共 {len} 字";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+H 查找替换
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.H)
        {
            e.Handled = true;
            OnFindReplace(sender, e);
            return;
        }

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

    // ================================================================
    //  查找替换、朗读、语音输入
    // ================================================================

    /// <summary>打开查找替换对话框。</summary>
    private void OnFindReplace(object sender, RoutedEventArgs e)
    {
        var dialog = new FindReplaceDialog(contentBox) { Owner = this };
        dialog.Show();
    }

    /// <summary>朗读选中文本或全部内容。</summary>
    private void OnSpeak(object sender, RoutedEventArgs e)
    {
        var text = contentBox.SelectedText;
        if (string.IsNullOrEmpty(text))
            text = contentBox.Text;

        if (string.IsNullOrEmpty(text))
        {
            _a11y.Announce(speakButton, "内容为空，无法朗读。");
            return;
        }

        SpeechService.SpeakAsync(text, rate: 0);
        _a11y.Announce(speakButton, "开始朗读。");
        statusText.Text = "正在朗读...";
    }

    /// <summary>停止朗读。</summary>
    private void OnStopSpeak(object sender, RoutedEventArgs e)
    {
        SpeechService.Stop();
        _a11y.Announce(stopSpeakButton, "已停止朗读。");
        statusText.Text = "";
    }

    /// <summary>启动语音输入（Windows 听写）。</summary>
    private void OnVoiceInput(object sender, RoutedEventArgs e)
    {
        contentBox.Focus();
        VoiceInputService.StartDictation();
        _a11y.Announce(voiceInputButton, "已启动语音输入，请说话。说完成后文字会插入到光标位置。");
        statusText.Text = "语音输入已启动...";
    }

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
