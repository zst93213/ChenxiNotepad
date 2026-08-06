using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;

namespace BlindNotepad;

/// <summary>
/// 编辑记账条目的对话框。回车保存（备注框中回车换行，Ctrl+回车保存），Esc 取消。
/// </summary>
public partial class AccountingEditDialog : Window
{
    private readonly AccountingEntry? _existing;

    /// <summary>编辑结果（点击确定后有效）。</summary>
    public AccountingEntry? Result { get; private set; }

    // 支出分类
    private static readonly string[] ExpenseCategories =
        { "餐饮", "交通", "购物", "娱乐", "医疗", "教育", "住房", "通讯", "日用品", "其他支出" };

    // 收入分类
    private static readonly string[] IncomeCategories =
        { "工资", "奖金", "投资收益", "兼职", "红包礼金", "退款", "其他收入" };

    // 支付方式
    private static readonly string[] PaymentMethods =
        { "现金", "微信", "支付宝", "银行卡", "信用卡", "其他" };

    public AccountingEditDialog(AccountingEntry? existing)
    {
        InitializeComponent();
        _existing = existing;

        // 填充支付方式
        foreach (var pm in PaymentMethods)
            paymentMethodBox.Items.Add(pm);

        if (existing is not null)
        {
            titleBox.Text = existing.Title;
            amountBox.Text = existing.Amount.ToString("F2");
            expenseRadio.IsChecked = existing.Type == "支出";
            incomeRadio.IsChecked = existing.Type == "收入";
            categoryBox.Text = existing.Category;
            paymentMethodBox.Text = existing.PaymentMethod;
            datePicker.SelectedDate = existing.Date;
            noteBox.Text = existing.Note;
            Title = "编辑记账";
        }
        else
        {
            datePicker.SelectedDate = DateTime.Today;
            paymentMethodBox.Text = "现金";
            Title = "新建记账";
        }

        // 初始化分类列表
        UpdateCategories();
    }

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        UpdateCategories();
    }

    private void UpdateCategories()
    {
        var isIncome = incomeRadio.IsChecked == true;
        var currentText = categoryBox.Text;
        categoryBox.Items.Clear();
        var categories = isIncome ? IncomeCategories : ExpenseCategories;
        foreach (var cat in categories)
            categoryBox.Items.Add(cat);

        // 如果当前文本在列表中，保留；否则设为第一个
        if (!string.IsNullOrEmpty(currentText) && Array.IndexOf(categories, currentText) >= 0)
            categoryBox.Text = currentText;
        else
            categoryBox.Text = categories[0];
    }

    private void OnAmountPreviewInput(object sender, TextCompositionEventArgs e)
    {
        // 只允许输入数字和小数点
        var text = amountBox.Text + e.Text;
        e.Handled = !decimal.TryParse(text, out _);
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
            titleBox.Focus();
            return;
        }

        if (!decimal.TryParse(amountBox.Text?.Trim(), out var amount) || amount <= 0)
        {
            amountBox.Focus();
            return;
        }

        var type = incomeRadio.IsChecked == true ? "收入" : "支出";

        Result = new AccountingEntry
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Amount = amount,
            Type = type,
            Category = string.IsNullOrWhiteSpace(categoryBox.Text) ? "其他支出" : categoryBox.Text.Trim(),
            PaymentMethod = string.IsNullOrWhiteSpace(paymentMethodBox.Text) ? "现金" : paymentMethodBox.Text.Trim(),
            Date = datePicker.SelectedDate ?? DateTime.Today,
            Note = noteBox.Text ?? string.Empty,
            IsFavorite = _existing?.IsFavorite ?? false,
            CreatedTime = _existing?.CreatedTime ?? DateTime.Now,
            ModifiedTime = DateTime.Now
        };

        DialogResult = true;
        Close();
    }
}
