using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BlindNotepad;

/// <summary>
/// 按月浏览日记对话框。列出所有包含日记的月份标签供用户选择。
/// 回车确认选择，Esc 取消。返回选中月份索引（-1 表示取消）。
/// </summary>
public partial class MonthPickerDialog : Window
{
    /// <summary>选中月份索引，-1 表示取消。</summary>
    public int SelectedIndex { get; private set; } = -1;

    public MonthPickerDialog(List<string> months)
    {
        InitializeComponent();

        foreach (var month in months)
        {
            var item = new ListBoxItem { Content = month };
            AutomationProperties.SetName(item, month);
            monthList.Items.Add(item);
        }

        if (monthList.Items.Count > 0)
        {
            monthList.SelectedIndex = 0;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        monthList.Focus();
    }

    private void monthList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedIndex = monthList.SelectedIndex;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            DialogResult = true;
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
    }
}
