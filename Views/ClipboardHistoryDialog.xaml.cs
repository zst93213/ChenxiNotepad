using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 剪贴板历史列表对话框。Enter 复制选中项到剪贴板，Del 删除，Esc 关闭。
/// </summary>
public partial class ClipboardHistoryDialog : Window
{
    private readonly ClipboardMonitorService _monitor;
    private readonly AccessibilityService _a11y = new();
    private List<ClipboardHistoryEntry> _filtered = new();

    public ClipboardHistoryDialog(ClipboardMonitorService monitor)
    {
        InitializeComponent();
        _monitor = monitor;
        RefreshList();
    }

    private void RefreshList()
    {
        var search = (searchBox.Text ?? string.Empty).Trim();
        historyList.Items.Clear();
        _filtered = _monitor.History
            .Where(e => string.IsNullOrEmpty(search) || e.Content.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var entry in _filtered)
        {
            var prefix = entry.IsPinned ? "📌 " : "";
            var preview = entry.Content.Length > 80
                ? entry.Content[..80].Replace("\n", " ").Replace("\r", "") + "..."
                : entry.Content.Replace("\n", " ").Replace("\r", "");
            var label = $"{prefix}{entry.Timestamp:HH:mm:ss} {preview}";
            var item = new ListBoxItem { Content = label, Tag = entry };
            AutomationProperties.SetName(item, $"{(entry.IsPinned ? "置顶 " : "")}{entry.Timestamp:yyyy-MM-dd HH:mm}，{preview}");
            item.ContextMenu = BuildItemContextMenu(entry);
            historyList.Items.Add(item);
        }

        if (historyList.Items.Count > 0)
        {
            historyList.SelectedIndex = 0;
        }
        else
        {
            _a11y.Announce(historyList, "剪贴板历史为空。");
        }
    }

    private ContextMenu BuildItemContextMenu(ClipboardHistoryEntry entry)
    {
        var menu = new ContextMenu();
        var copyItem = new MenuItem { Header = "复制到剪贴板", InputGestureText = "Enter" };
        copyItem.Click += (_, _) => CopySelected();
        menu.Items.Add(copyItem);

        var pinItem = new MenuItem { Header = entry.IsPinned ? "取消置顶" : "置顶" };
        pinItem.Click += (_, _) => TogglePin(entry);
        menu.Items.Add(pinItem);

        menu.Items.Add(new Separator());

        var editItem = new MenuItem { Header = "编辑内容", InputGestureText = "Ctrl+E" };
        editItem.Click += (_, _) => EditSelected();
        menu.Items.Add(editItem);

        var delItem = new MenuItem { Header = "删除", InputGestureText = "Del" };
        delItem.Click += (_, _) => DeleteSelected();
        menu.Items.Add(delItem);
        return menu;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中项变化时播报
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CopySelected();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.Enter && historyList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            CopySelected();
            return;
        }

        if (e.Key == Key.Delete && historyList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            DeleteSelected();
            return;
        }

        if (ctrl && e.Key == Key.E)
        {
            e.Handled = true;
            EditSelected();
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(searchBox.Text))
            {
                searchBox.Clear();
                e.Handled = true;
            }
            else
            {
                Close();
            }
            return;
        }
    }

    private void CopySelected()
    {
        if (historyList.SelectedItem is not ListBoxItem item || item.Tag is not ClipboardHistoryEntry entry) return;
        _monitor.CopyToClipboard(entry);
        _a11y.Announce(historyList, "已复制到剪贴板。");
        Close();
    }

    private void DeleteSelected()
    {
        if (historyList.SelectedItem is not ListBoxItem item || item.Tag is not ClipboardHistoryEntry entry) return;
        _monitor.RemoveEntry(entry.Id);
        _a11y.Announce(historyList, "已删除。");
        RefreshList();
    }

    private void EditSelected()
    {
        if (historyList.SelectedItem is not ListBoxItem item || item.Tag is not ClipboardHistoryEntry entry) return;

        var dialog = new ClipboardEditDialog(entry) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _monitor.UpdateEntry(dialog.Result);
            _a11y.Announce(historyList, "已更新剪贴板内容。");
            RefreshList();
        }
    }

    private void TogglePin(ClipboardHistoryEntry entry)
    {
        entry.IsPinned = !entry.IsPinned;
        _monitor.UpdateEntry(entry);
        _a11y.Announce(historyList, entry.IsPinned ? "已置顶。" : "已取消置顶。");
        RefreshList();
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        if (_monitor.History.Count == 0) return;
        if (MessageBox.Show("确认清空所有非置顶的剪贴板历史吗？", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _monitor.ClearAll();
        _a11y.Announce(historyList, "已清空所有非置顶历史。");
        RefreshList();
    }
}
