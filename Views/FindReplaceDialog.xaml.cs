using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 查找替换对话框。操作传入的 TextBox 内容进行查找、替换。
/// 同时集成朗读和语音输入功能入口。
/// </summary>
public partial class FindReplaceDialog : Window
{
    private readonly TextBox _targetBox;
    private readonly AccessibilityService _a11y = new();
    private int _currentMatchIndex = -1;

    /// <summary>查找替换回调接口，由宿主编辑器实现。</summary>
    public IEditDialogHost? Host { get; set; }

    public FindReplaceDialog(TextBox targetBox)
    {
        InitializeComponent();
        _targetBox = targetBox;
        rateSlider.ValueChanged += (_, _) => rateText.Text = ((int)rateSlider.Value).ToString();

        Closed += (_, _) => SpeechService.Stop();
    }

    // ================================================================
    //  查找替换
    // ================================================================

    private Regex? BuildRegex()
    {
        var pattern = findBox.Text;
        if (string.IsNullOrEmpty(pattern)) return null;

        var options = RegexOptions.None;
        if (!caseSensitive.IsChecked.GetValueOrDefault())
            options |= RegexOptions.IgnoreCase;

        try
        {
            if (useRegex.IsChecked.GetValueOrDefault())
                return new Regex(pattern, options);
            else
                return new Regex(Regex.Escape(pattern), options);
        }
        catch (ArgumentException ex)
        {
            _a11y.Announce(findBox, $"正则表达式错误：{ex.Message}");
            return null;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        var regex = BuildRegex();
        if (regex is null) { _a11y.Announce(findBox, "请输入查找内容。"); return; }

        var text = _targetBox.Text;
        var start = _targetBox.SelectionStart + _targetBox.SelectionLength;
        if (start >= text.Length) start = 0;

        var match = regex.Match(text, start);
        if (!match.Success && start > 0)
        {
            // 回绕到开头
            match = regex.Match(text, 0);
        }

        if (match.Success)
        {
            _currentMatchIndex = match.Index;
            _targetBox.Focus();
            _targetBox.Select(match.Index, match.Length);
            _a11y.Announce(_targetBox, $"已找到：{match.Value}");
        }
        else
        {
            _currentMatchIndex = -1;
            _a11y.Announce(findBox, "未找到匹配内容。");
        }
    }

    private void OnFindPrev(object sender, RoutedEventArgs e)
    {
        var regex = BuildRegex();
        if (regex is null) { _a11y.Announce(findBox, "请输入查找内容。"); return; }

        var text = _targetBox.Text;
        var start = _targetBox.SelectionStart;

        var matches = regex.Matches(text);
        Match? prevMatch = null;
        foreach (Match m in matches)
        {
            if (m.Index < start)
            {
                prevMatch = m;
            }
            else
            {
                break;
            }
        }

        if (prevMatch is null && matches.Count > 0)
        {
            // 回绕到末尾
            prevMatch = matches[^1];
        }

        if (prevMatch is not null)
        {
            _currentMatchIndex = prevMatch.Index;
            _targetBox.Focus();
            _targetBox.Select(prevMatch.Index, prevMatch.Length);
            _a11y.Announce(_targetBox, $"已找到：{prevMatch.Value}");
        }
        else
        {
            _a11y.Announce(findBox, "未找到匹配内容。");
        }
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        var regex = BuildRegex();
        if (regex is null) { _a11y.Announce(findBox, "请输入查找内容。"); return; }

        // 如果当前选中的就是匹配项，替换它
        var selected = _targetBox.SelectedText;
        if (!string.IsNullOrEmpty(selected))
        {
            try
            {
                var replaced = useRegex.IsChecked.GetValueOrDefault()
                    ? regex.Replace(selected, replaceBox.Text, 1)
                    : replaceBox.Text;
                _targetBox.SelectedText = replaced;
                _a11y.Announce(replaceButton, "已替换。");
            }
            catch (Exception)
            {
                _a11y.Announce(replaceButton, "替换失败。");
            }
        }
        else
        {
            _a11y.Announce(replaceButton, "请先查找再替换。");
        }

        // 替换后自动查找下一个
        OnFindNext(sender, e);
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var regex = BuildRegex();
        if (regex is null) { _a11y.Announce(findBox, "请输入查找内容。"); return; }

        var text = _targetBox.Text;
        var replacement = replaceBox.Text;
        var count = 0;

        try
        {
            if (useRegex.IsChecked.GetValueOrDefault())
            {
                count = regex.Matches(text).Count;
                _targetBox.Text = regex.Replace(text, replacement);
            }
            else
            {
                count = regex.Matches(text).Count;
                _targetBox.Text = regex.Replace(text, replacement);
            }
            _a11y.Announce(replaceAllButton, $"已替换 {count} 处。");
        }
        catch (Exception ex)
        {
            _a11y.Announce(replaceAllButton, $"替换失败：{ex.Message}");
        }
    }

    // ================================================================
    //  朗读功能
    // ================================================================

    private void OnSpeak(object sender, RoutedEventArgs e)
    {
        var text = _targetBox.SelectedText;
        if (string.IsNullOrEmpty(text))
            text = _targetBox.Text;

        if (string.IsNullOrEmpty(text))
        {
            _a11y.Announce(speakButton, "内容为空，无法朗读。");
            return;
        }

        var rate = (int)rateSlider.Value;
        SpeechService.SpeakAsync(text, rate);
        _a11y.Announce(speakButton, "开始朗读。");
        statusText.Text = "正在朗读...";
    }

    private void OnStopSpeak(object sender, RoutedEventArgs e)
    {
        SpeechService.Stop();
        _a11y.Announce(stopSpeakButton, "已停止朗读。");
        statusText.Text = "";
    }

    // ================================================================
    //  语音输入
    // ================================================================

    private void OnVoiceInput(object sender, RoutedEventArgs e)
    {
        // 触发 Windows 内置语音输入（Win+H）
        VoiceInputService.StartDictation();
        _a11y.Announce(voiceInputButton, "已启动语音输入，请开始说话。说完成后文字将插入到光标位置。");
        statusText.Text = "语音输入已启动，请说话...";
    }
}

/// <summary>
/// 编辑对话框宿主接口，用于回调通知宿主进行操作。
/// </summary>
public interface IEditDialogHost
{
    TextBox ContentBox { get; }
}
