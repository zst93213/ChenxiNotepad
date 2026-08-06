using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 复制字段选择器对话框。列出当前密码条目的所有可复制字段；
/// 非敏感字段显示值，敏感字段显示“已隐藏”。上下光标选择，回车复制，Esc 关闭。
/// 返回选中字段索引（-1 表示取消）。
/// </summary>
public partial class FieldSelectorDialog : Window
{
    private readonly List<(string Name, string Value, bool Sensitive)> _options = new();

    /// <summary>选中字段索引，-1 表示取消。</summary>
    public int SelectedFieldIndex { get; private set; } = -1;

    /// <summary>选中字段的值。</summary>
    public string? SelectedValue { get; private set; }

    /// <summary>选中字段是否敏感。</summary>
    public bool SelectedSensitive { get; private set; }

    /// <summary>选中字段名称。</summary>
    public string? SelectedFieldName { get; private set; }

    // 无障碍服务实例
    private readonly AccessibilityService _a11y = new();

    public FieldSelectorDialog(PasswordEntry entry)
    {
        InitializeComponent();
        BuildOptions(entry);
        PopulateList();

        if (fieldListBox.Items.Count > 0)
        {
            fieldListBox.SelectedIndex = 0;
        }
    }

    private void BuildOptions(PasswordEntry entry)
    {
        // 基础字段始终列出
        _options.Add(("平台名称", entry.Title, false));
        _options.Add(("用户名", entry.UserName, false));
        _options.Add(("密码", entry.Password, true));

        if (!string.IsNullOrEmpty(entry.Url))
        {
            _options.Add(("网址", entry.Url, false));
        }

        if (!string.IsNullOrEmpty(entry.PhoneNumber))
        {
            _options.Add(("手机号", entry.PhoneNumber, false));
        }

        if (!string.IsNullOrEmpty(entry.Email))
        {
            _options.Add(("邮箱", entry.Email, false));
        }

        // TOTP 动态验证码（仅在设置了密钥时列出）
        if (!string.IsNullOrEmpty(entry.TotpSecret))
        {
            var code = StorageService.GenerateTotp(entry.TotpSecret);
            _options.Add(("动态验证码", code, true));
        }

        // 密保答案（敏感）
        for (var i = 0; i < entry.SecurityQuestions.Count; i++)
        {
            var question = entry.SecurityQuestions[i];
            if (!string.IsNullOrEmpty(question.Answer))
            {
                _options.Add(($"密保答案 {i + 1}", question.Answer, true));
            }
        }

        // 自定义字段（按其敏感标记）
        foreach (var field in entry.CustomFields)
        {
            if (!string.IsNullOrEmpty(field.Value))
            {
                var name = string.IsNullOrWhiteSpace(field.Name) ? "自定义字段" : field.Name;
                _options.Add((name, field.Value, field.Sensitive));
            }
        }

        // 备注（非敏感，仅在非空时列出）
        if (!string.IsNullOrEmpty(entry.Notes))
        {
            _options.Add(("备注", entry.Notes, false));
        }
    }

    private void PopulateList()
    {
        foreach (var option in _options)
        {
            var display = option.Sensitive
                ? $"{option.Name}：已隐藏"
                : $"{option.Name}：{option.Value}";

            var item = new ListBoxItem { Content = display };
            AutomationProperties.SetName(item, display);
            fieldListBox.Items.Add(item);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmSelection();
        }
        // Esc 由“关闭”按钮的 IsCancel 处理
    }

    private void ConfirmSelection()
    {
        var index = fieldListBox.SelectedIndex;
        if (index < 0)
        {
            _a11y.Announce(fieldListBox, "请先选择一个字段。");
            return;
        }

        var option = _options[index];
        SelectedFieldIndex = index;
        SelectedValue = option.Value;
        SelectedSensitive = option.Sensitive;
        SelectedFieldName = option.Name;

        DialogResult = true;
        Close();
    }
}
