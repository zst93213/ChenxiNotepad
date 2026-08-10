using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 编辑网址条目的对话框。回车保存（备注框中回车换行，Ctrl+回车保存），Esc 取消。
/// 支持多个账号密码对与多个密钥，动态增删、自动编号。
/// </summary>
public partial class UrlEditDialog : Window
{
    private readonly UrlEntry? _existing;

    // 无障碍服务实例
    private readonly AccessibilityService _a11y = new();

    /// <summary>编辑结果（点击确定后有效）。</summary>
    public UrlEntry? Result { get; private set; }

    // 运行时账号列表（编辑过程中的副本，带 Id）
    private readonly List<UrlAccount> _accounts = new();
    // 运行时密钥列表
    private readonly List<UrlSecret> _secrets = new();

    private static string ChineseIndex(int i)
    {
        // 账号一、账号二、... 账号十、账号十一...
        // 直接用中文数字更友好
        return i switch
        {
            1 => "一", 2 => "二", 3 => "三", 4 => "四", 5 => "五",
            6 => "六", 7 => "七", 8 => "八", 9 => "九", 10 => "十",
            _ => i.ToString()
        };
    }

    public UrlEditDialog(UrlEntry? existing, IReadOnlyList<string> categories, IReadOnlyList<PasswordEntry> passwords)
    {
        InitializeComponent();
        _existing = existing;

        foreach (var category in categories)
        {
            categoryBox.Items.Add(category);
        }

        // 关联密码：首项为"（无）"，其余为密码条目标题
        linkedPasswordBox.Items.Add(new ComboBoxItem { Content = "（无）", Tag = null });
        foreach (var password in passwords)
        {
            linkedPasswordBox.Items.Add(new ComboBoxItem { Content = password.Title, Tag = password.Id });
        }

        if (existing is not null)
        {
            titleBox.Text = existing.Title;
            urlBox.Text = existing.Url;
            categoryBox.Text = existing.Category;
            notesBox.Text = existing.Notes;
            SelectLinkedPassword(existing.LinkedPasswordId);

            // 载入账号（用 Accounts 列表，已有迁移保证不为空）
            foreach (var a in existing.Accounts) _accounts.Add(new UrlAccount { Id = a.Id, Account = a.Account, Password = a.Password });
            // 载入密钥
            foreach (var s in existing.Secrets) _secrets.Add(new UrlSecret { Id = s.Id, Secret = s.Secret });

            Title = "编辑网址";
        }
        else
        {
            categoryBox.Text = categories.Count > 0 ? categories[0] : "默认";
            linkedPasswordBox.SelectedIndex = 0;
            // 默认至少提供一个空账号行
            _accounts.Add(new UrlAccount());
            Title = "新建网址";
        }

        RebuildAccountsPanel();
        RebuildSecretsPanel();
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

    // ------------------------------------------------------------------
    // 账号动态 UI
    // ------------------------------------------------------------------

    /// <summary>重建账号列表 UI。</summary>
    private void RebuildAccountsPanel()
    {
        accountsPanel.Children.Clear();
        for (int i = 0; i < _accounts.Count; i++)
        {
            var index = i; // 捕获
            var acc = _accounts[i];
            var label = $"账号{ChineseIndex(i + 1)}";

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左列：标签（账号N、密码N）
            var labelCol = new StackPanel();
            var lblAcc = new Label
            {
                Content = $"{label}账号:",
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 0, 8, 2),
                Target = null
            };
            AutomationProperties.SetName(lblAcc, $"{label}账号");
            var lblPwd = new Label
            {
                Content = $"{label}密码:",
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 0, 8, 2),
                Target = null
            };
            AutomationProperties.SetName(lblPwd, $"{label}密码");
            labelCol.Children.Add(lblAcc);
            labelCol.Children.Add(lblPwd);
            Grid.SetColumn(labelCol, 0);
            row.Children.Add(labelCol);

            // 中列：账号输入框、密码输入框
            var inputCol = new StackPanel();
            var accBox = new TextBox { Margin = new Thickness(0, 4, 0, 4), Text = acc.Account };
            AutomationProperties.SetName(accBox, $"{label}账号输入框");
            accBox.TextChanged += (_, _) => acc.Account = accBox.Text ?? "";
            var pwdBox = new TextBox { Margin = new Thickness(0, 4, 0, 4), Text = acc.Password };
            AutomationProperties.SetName(pwdBox, $"{label}密码输入框");
            pwdBox.TextChanged += (_, _) => acc.Password = pwdBox.Text ?? "";
            inputCol.Children.Add(accBox);
            inputCol.Children.Add(pwdBox);
            Grid.SetColumn(inputCol, 1);
            row.Children.Add(inputCol);

            // 右列：删除按钮（至少保留 1 条时禁用删除；用户也可以全部留空 —— 保存时会过滤空行）
            var btnCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = _accounts.Count > 1
            };
            AutomationProperties.SetName(delBtn, $"删除{label}");
            delBtn.Click += (_, _) =>
            {
                _accounts.RemoveAt(index);
                RebuildAccountsPanel();
                _a11y.Announce($"已删除{label}。");
            };
            btnCol.Children.Add(delBtn);
            Grid.SetColumn(btnCol, 2);
            row.Children.Add(btnCol);

            accountsPanel.Children.Add(row);
        }
    }

    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        _accounts.Add(new UrlAccount());
        RebuildAccountsPanel();
        _a11y.Announce($"已添加账号{ChineseIndex(_accounts.Count)}。");
        // 滚动到底部让新增控件可见
        rootScroll.ScrollToEnd();
    }

    // ------------------------------------------------------------------
    // 密钥动态 UI
    // ------------------------------------------------------------------

    /// <summary>重建密钥列表 UI。</summary>
    private void RebuildSecretsPanel()
    {
        secretsPanel.Children.Clear();
        if (_secrets.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "（当前无密钥，点击下方按钮添加）",
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(4)
            };
            secretsPanel.Children.Add(hint);
            return;
        }

        for (int i = 0; i < _secrets.Count; i++)
        {
            var index = i;
            var sec = _secrets[i];
            var label = $"密钥{ChineseIndex(i + 1)}";

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new Label
            {
                Content = $"{label}:",
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 4, 8, 4),
                Target = null
            };
            AutomationProperties.SetName(lbl, label);
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var box = new TextBox { Margin = new Thickness(0, 4, 0, 4), Text = sec.Secret };
            AutomationProperties.SetName(box, $"{label}输入框");
            box.TextChanged += (_, _) => sec.Secret = box.Text ?? "";
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(delBtn, $"删除{label}");
            delBtn.Click += (_, _) =>
            {
                _secrets.RemoveAt(index);
                RebuildSecretsPanel();
                _a11y.Announce($"已删除{label}。");
            };
            Grid.SetColumn(delBtn, 2);
            row.Children.Add(delBtn);

            secretsPanel.Children.Add(row);
        }
    }

    private void OnAddSecret(object sender, RoutedEventArgs e)
    {
        _secrets.Add(new UrlSecret());
        RebuildSecretsPanel();
        _a11y.Announce($"已添加密钥{ChineseIndex(_secrets.Count)}。");
        rootScroll.ScrollToEnd();
    }

    // ------------------------------------------------------------------
    // 保存
    // ------------------------------------------------------------------

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

        // 过滤完全为空的账号/密钥行，避免存大量空数据
        var savedAccounts = _accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Account) || !string.IsNullOrWhiteSpace(a.Password))
            .Select(a => new UrlAccount { Id = a.Id, Account = (a.Account ?? "").Trim(), Password = a.Password ?? "" })
            .ToList();
        var savedSecrets = _secrets
            .Where(s => !string.IsNullOrWhiteSpace(s.Secret))
            .Select(s => new UrlSecret { Id = s.Id, Secret = s.Secret!.Trim() })
            .ToList();

        // 保持第一个账号（若存在）同步回 legacy Account 字段，兼容旧版本读回
        var legacyAccount = savedAccounts.Count > 0 ? savedAccounts[0].Account : "";

        // 保留原来的隐藏字段配置 & 其它状态字段
        var hiddenFields = _existing?.HiddenFields ?? new HashSet<string>();

        Result = new UrlEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Url = urlBox.Text?.Trim() ?? string.Empty,
            Account = legacyAccount, // 兼容字段
            Accounts = savedAccounts,
            Secrets = savedSecrets,
            Category = category,
            Notes = notesBox.Text ?? string.Empty,
            LinkedPasswordId = linked?.Tag as string,
            IsFavorite = _existing?.IsFavorite ?? false,
            LastCheckedTime = _existing?.LastCheckedTime,
            LastCheckStatus = _existing?.LastCheckStatus ?? "Unknown",
            CreatedTime = _existing?.CreatedTime ?? DateTime.Now,
            ModifiedTime = DateTime.Now,
            HiddenFields = hiddenFields
        };

        DialogResult = true;
        Close();
    }
}
