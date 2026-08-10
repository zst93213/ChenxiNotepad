using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

public partial class ShortcutConfigDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private List<ShortcutBinding> _bindings;
    private bool _capturing;

    public ShortcutConfigDialog(List<ShortcutBinding> bindings)
    {
        InitializeComponent();
        _bindings = bindings;
        RefreshList();
    }

    private void RefreshList()
    {
        shortcutList.Items.Clear();
        foreach (var b in _bindings)
        {
            var display = $"[{b.Category}] {b.DisplayName} — {b.KeyGesture}";
            var item = new ListBoxItem { Content = display, Tag = b };
            AutomationProperties.SetName(item, display);
            shortcutList.Items.Add(item);
        }
        if (shortcutList.Items.Count > 0)
            shortcutList.SelectedIndex = 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中项变化时只更新显示，不自动进入捕获模式
        _capturing = false;
        applyButton.Content = "修改快捷键(_A)";
        if (shortcutList.SelectedItem is ListBoxItem item && item.Tag is ShortcutBinding b)
        {
            gestureBox.Text = b.KeyGesture;
            _a11y.Announce(gestureBox, $"选中：{b.DisplayName}，当前快捷键：{b.KeyGesture}。点击修改快捷键按钮来设置新的快捷键。");
        }
    }

    /// <summary>用户点击"修改快捷键"按钮时进入捕获模式。</summary>
    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!_capturing)
        {
            // 进入捕获模式
            _capturing = true;
            applyButton.Content = "正在捕获...（按ESC取消）";
            gestureBox.Focus();
            if (shortcutList.SelectedItem is ListBoxItem item && item.Tag is ShortcutBinding b)
            {
                _a11y.Announce(gestureBox, $"正在捕获{b.DisplayName}的快捷键，请按下新的组合键。按ESC取消。");
            }
            else
            {
                _a11y.Announce(gestureBox, "请先在列表中选择一个操作。");
                _capturing = false;
                applyButton.Content = "修改快捷键(_A)";
            }
        }
        else
        {
            // 确认应用
            _capturing = false;
            applyButton.Content = "修改快捷键(_A)";
            if (shortcutList.SelectedItem is ListBoxItem item && item.Tag is ShortcutBinding b)
            {
                if (!string.IsNullOrEmpty(b.KeyGesture))
                {
                    RefreshList();
                    _a11y.Announce(gestureBox, $"已设置{b.DisplayName}的快捷键为：{b.KeyGesture}。");
                }
            }
        }
    }

    /// <summary>输入框获得焦点时不自动进入捕获模式（与旧版不同）。</summary>
    private void OnGestureBoxFocus(object sender, RoutedEventArgs e)
    {
        // 仅在已在捕获模式时保持，不因聚焦而自动进入
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // ESC：如果在捕获模式，退出捕获而非关闭窗口
        if (e.Key == Key.Escape && _capturing)
        {
            _capturing = false;
            applyButton.Content = "修改快捷键(_A)";
            _a11y.Announce(gestureBox, "已取消捕获。");
            e.Handled = true;
            return;
        }

        if (!_capturing) return;
        if (e.Key == Key.Enter)
        {
            // Enter 确认捕获
            _capturing = false;
            applyButton.Content = "修改快捷键(_A)";
            e.Handled = true;
            return;
        }

        e.Handled = true;
        var key = e.Key;
        if (key == Key.System) key = e.SystemKey;

        // 忽略单独的修饰键
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LWin || key == Key.RWin)
            return;

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");

        var keyStr = key.ToString();
        // 简化数字键显示
        if (keyStr.StartsWith("D") && keyStr.Length == 2) keyStr = keyStr[1..];

        parts.Add(keyStr);
        var gesture = string.Join("+", parts);

        if (shortcutList.SelectedItem is ListBoxItem item && item.Tag is ShortcutBinding b)
        {
            b.KeyGesture = gesture;
            gestureBox.Text = gesture;
            _capturing = false;
            applyButton.Content = "修改快捷键(_A)";
            _a11y.Announce(gestureBox, $"捕获到快捷键：{gesture}。已设置{b.DisplayName}的快捷键。");
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "快捷键配置|*.json", FileName = "shortcuts.json" };
        if (dlg.ShowDialog() == true)
        {
            if (ShortcutConfigService.Export(_bindings, dlg.FileName))
                _a11y.Announce(shortcutList, "配置已导出。");
            else
                _a11y.Announce(shortcutList, "导出失败。");
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "快捷键配置|*.json" };
        if (dlg.ShowDialog() == true)
        {
            var imported = ShortcutConfigService.Import(dlg.FileName);
            if (imported is not null && imported.Count > 0)
            {
                _bindings = imported;
                RefreshList();
                _a11y.Announce(shortcutList, "配置已导入。");
            }
            else
                _a11y.Announce(shortcutList, "导入失败或文件无效。");
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确认重置所有快捷键为默认值吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _bindings = ShortcutConfigService.GetDefaultBindings();
            RefreshList();
            _a11y.Announce(shortcutList, "已重置为默认快捷键。");
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ShortcutConfigService.Save(_bindings);
        DialogResult = true;
        Close();
    }
}
