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
        if (shortcutList.SelectedItem is ListBoxItem item && item.Tag is ShortcutBinding b)
        {
            gestureBox.Text = b.KeyGesture;
            _a11y.Announce(gestureBox, $"选中：{b.DisplayName}，当前快捷键：{b.KeyGesture}。在此按下新的快捷键组合。");
        }
        _capturing = true;
        gestureBox.Focus();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (shortcutList.SelectedItem is ListBoxItem item && item.Tag is ShortcutBinding b)
        {
            if (!string.IsNullOrEmpty(b.KeyGesture))
            {
                RefreshList();
                _a11y.Announce(gestureBox, $"已设置{b.DisplayName}的快捷键为：{b.KeyGesture}。");
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        if (e.Key == Key.Escape || e.Key == Key.Enter) return;

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
            _a11y.Announce(gestureBox, $"捕获到快捷键：{gesture}。点击应用确认。");
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
