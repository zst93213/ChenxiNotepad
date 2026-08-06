using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑密码条目的对话框，字段分组排列。
/// 支持动态添加多组密保问题（答案用 PasswordBox）与自定义字段（含敏感复选框）。
/// 密码字段旁的“生成”按钮或 F5 可调出密码生成器。回车保存，Esc 取消。
/// </summary>
public partial class PasswordEditDialog : Window
{
    private readonly PasswordEntry? _existing;

    // 动态行控件缓存，便于读取与删除
    private readonly List<(Panel Host, TextBox Question, PasswordBox Answer)> _securityQuestionRows = new();
    private readonly List<(Panel Host, TextBox Name, TextBox Value, CheckBox Sensitive)> _customFieldRows = new();

    // 无障碍服务实例
    private readonly AccessibilityService _a11y = new();

    /// <summary>编辑结果（点击确定后有效）。</summary>
    public PasswordEntry? Result { get; private set; }

    public PasswordEditDialog(PasswordEntry? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            titleBox.Text = existing.Title;
            userNameBox.Text = existing.UserName;
            passwordBox.Password = existing.Password;
            urlBox.Text = existing.Url;
            phoneBox.Text = existing.PhoneNumber;
            emailBox.Text = existing.Email;
            totpBox.Text = existing.TotpSecret;
            tagsBox.Text = existing.Tags;
            notesBox.Text = existing.Notes;

            foreach (var question in existing.SecurityQuestions)
            {
                AddSecurityQuestionRow(question);
            }

            foreach (var field in existing.CustomFields)
            {
                AddCustomFieldRow(field);
            }

            Title = "编辑密码";
        }
        else
        {
            Title = "新建密码";
        }
    }

    // =========================================================================
    // 动态行：密保问题
    // =========================================================================

    private void OnAddSecurityQuestion(object sender, RoutedEventArgs e) => AddSecurityQuestionRow(null);

    private void AddSecurityQuestionRow(SecurityQuestion? existing)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

        var questionBox = new TextBox
        {
            Width = 230,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(questionBox, "密保问题");

        var answerBox = new PasswordBox
        {
            Width = 180,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(answerBox, "密保答案");

        var removeButton = new Button
        {
            Content = "删除",
            Padding = new Thickness(8, 2, 8, 2)
        };
        AutomationProperties.SetName(removeButton, "删除该密保问题");

        if (existing is not null)
        {
            questionBox.Text = existing.Question;
            answerBox.Password = existing.Answer;
        }

        var tuple = (row, questionBox, answerBox);
        removeButton.Click += (_, _) =>
        {
            securityQuestionsPanel.Children.Remove(row);
            _securityQuestionRows.Remove(tuple);
            _a11y.Announce(securityQuestionsPanel, "已删除一个密保问题。");
        };

        row.Children.Add(questionBox);
        row.Children.Add(answerBox);
        row.Children.Add(removeButton);
        securityQuestionsPanel.Children.Add(row);
        _securityQuestionRows.Add(tuple);

        if (existing is null)
        {
            questionBox.Focus();
        }
    }

    // =========================================================================
    // 动态行：自定义字段
    // =========================================================================

    private void OnAddCustomField(object sender, RoutedEventArgs e) => AddCustomFieldRow(null);

    private void AddCustomFieldRow(CustomField? existing)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

        var nameBox = new TextBox
        {
            Width = 140,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(nameBox, "自定义字段名称");

        var valueBox = new TextBox
        {
            Width = 200,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(valueBox, "自定义字段值");

        var sensitiveBox = new CheckBox
        {
            Content = "敏感",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(sensitiveBox, "该字段是否敏感");

        var removeButton = new Button
        {
            Content = "删除",
            Padding = new Thickness(8, 2, 8, 2)
        };
        AutomationProperties.SetName(removeButton, "删除该自定义字段");

        if (existing is not null)
        {
            nameBox.Text = existing.Name;
            valueBox.Text = existing.Value;
            sensitiveBox.IsChecked = existing.Sensitive;
        }

        var tuple = (row, nameBox, valueBox, sensitiveBox);
        removeButton.Click += (_, _) =>
        {
            customFieldsPanel.Children.Remove(row);
            _customFieldRows.Remove(tuple);
            _a11y.Announce(customFieldsPanel, "已删除一个自定义字段。");
        };

        row.Children.Add(nameBox);
        row.Children.Add(valueBox);
        row.Children.Add(sensitiveBox);
        row.Children.Add(removeButton);
        customFieldsPanel.Children.Add(row);
        _customFieldRows.Add(tuple);

        if (existing is null)
        {
            nameBox.Focus();
        }
    }

    // =========================================================================
    // 密码生成器
    // =========================================================================

    private void OnGeneratePassword(object sender, RoutedEventArgs e) => OpenGenerator();

    private void OpenGenerator()
    {
        var dialog = new PasswordGeneratorDialog { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.GeneratedPassword))
        {
            passwordBox.Password = dialog.GeneratedPassword;
            _a11y.Announce(passwordBox, "已生成新密码并填入。");
        }
    }

    // =========================================================================
    // 键盘与保存
    // =========================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F5 调出密码生成器
        if (e.Key == Key.F5 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OpenGenerator();
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        var focused = FocusManager.GetFocusedElement(this) as TextBox;
        var inMultiline = focused is not null && focused.AcceptsReturn;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // 单行字段回车保存；备注框回车换行，Ctrl+回车保存
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
            _a11y.Announce(titleBox, "请输入平台名称。");
            titleBox.Focus();
            return;
        }

        Result = new PasswordEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            UserName = userNameBox.Text ?? string.Empty,
            Password = passwordBox.Password,
            Url = urlBox.Text?.Trim() ?? string.Empty,
            PhoneNumber = phoneBox.Text ?? string.Empty,
            Email = emailBox.Text ?? string.Empty,
            TotpSecret = totpBox.Text ?? string.Empty,
            Notes = notesBox.Text ?? string.Empty,
            Tags = tagsBox.Text ?? string.Empty,
            SecurityQuestions = _securityQuestionRows
                .Select(r => new SecurityQuestion { Question = r.Question.Text, Answer = r.Answer.Password })
                .Where(q => !string.IsNullOrWhiteSpace(q.Question) || !string.IsNullOrEmpty(q.Answer))
                .ToList(),
            CustomFields = _customFieldRows
                .Select(r => new CustomField { Name = r.Name.Text, Value = r.Value.Text, Sensitive = r.Sensitive.IsChecked == true })
                .Where(f => !string.IsNullOrWhiteSpace(f.Name) || !string.IsNullOrEmpty(f.Value))
                .ToList(),
            IsFavorite = _existing?.IsFavorite ?? false,
            LastPasswordChange = _existing?.LastPasswordChange ?? DateTime.Now,
            ModifiedTime = DateTime.Now
        };

        DialogResult = true;
        Close();
    }
}
