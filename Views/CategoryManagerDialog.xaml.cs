using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Services;

namespace BlindNotepad;

public partial class CategoryManagerDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private readonly List<string> _categories;

    public List<string> Result { get; private set; }

    public CategoryManagerDialog(List<string> categories)
    {
        InitializeComponent();
        _categories = new List<string>(categories);
        Result = _categories;
        RefreshList();
    }

    private void RefreshList()
    {
        categoryList.Items.Clear();
        foreach (var cat in _categories)
        {
            var item = new ListBoxItem { Content = cat };
            AutomationProperties.SetName(item, cat);
            categoryList.Items.Add(item);
        }
        if (categoryList.Items.Count > 0)
            categoryList.SelectedIndex = 0;
    }

    private void CategoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { e.Handled = true; OnDelete(null, null); }
        if (e.Key == Key.F2) { e.Handled = true; OnRename(null, null); }
    }

    private void OnAdd(object? sender, RoutedEventArgs? e)
    {
        var name = newCategoryBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _a11y.Announce(newCategoryBox, "请输入分类名称。");
            return;
        }
        if (_categories.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            _a11y.Announce(newCategoryBox, "该分类已存在。");
            return;
        }
        _categories.Add(name);
        newCategoryBox.Clear();
        RefreshList();
        categoryList.SelectedIndex = _categories.Count - 1;
        _a11y.Announce(categoryList, $"已添加分类：{name}。");
    }

    private void OnRename(object? sender, RoutedEventArgs? e)
    {
        var index = categoryList.SelectedIndex;
        if (index < 0) { _a11y.Announce(categoryList, "请先选择一个分类。"); return; }
        var newName = newCategoryBox.Text?.Trim();
        if (string.IsNullOrEmpty(newName)) { _a11y.Announce(newCategoryBox, "请在输入框中输入新名称。"); return; }
        var oldName = _categories[index];
        _categories[index] = newName;
        newCategoryBox.Clear();
        RefreshList();
        categoryList.SelectedIndex = index;
        _a11y.Announce(categoryList, $"已将\"{oldName}\"重命名为\"{newName}\"。");
    }

    private void OnDelete(object? sender, RoutedEventArgs? e)
    {
        var index = categoryList.SelectedIndex;
        if (index < 0) { _a11y.Announce(categoryList, "请先选择一个分类。"); return; }
        var name = _categories[index];
        if (name == "默认")
        {
            _a11y.Announce(categoryList, "默认分类不可删除。");
            return;
        }
        if (MessageBox.Show($"确认删除分类\"{name}\"吗？该分类下的条目将变为\"默认\"分类。", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _categories.RemoveAt(index);
            RefreshList();
            _a11y.Announce(categoryList, $"已删除分类：{name}。");
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = _categories;
        DialogResult = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !(FocusManager.GetFocusedElement(this) is ListBox))
        {
            e.Handled = true;
            OnAdd(null, null);
        }
    }
}
