using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 批量检测失效网址后的确认删除对话框。
/// 每行是一个失效的 UrlEntry（带 LastCheckStatus 原因），勾选后在调用方拿到 SelectedIds 删除。
/// </summary>
public partial class BrokenUrlDialog : Window
{
    private readonly AccessibilityService _a11y = new();

    /// <summary>行模型，封装列表项是否被选中 + 显示用字段。</summary>
    public sealed class BrokenRow : INotifyPropertyChanged
    {
        private bool _delete;

        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Category { get; set; } = "";

        public bool Delete
        {
            get => _delete;
            set
            {
                if (_delete == value) return;
                _delete = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Delete)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly List<BrokenRow> _rows = new();

    /// <summary>调用方可通过此行集合读取结果。</summary>
    public IReadOnlyList<BrokenRow> Rows => _rows;

    /// <summary>选中需要删除的条目 Id 集合。</summary>
    public HashSet<string> SelectedIds => new(
        _rows.Where(r => r.Delete).Select(r => r.Id), StringComparer.Ordinal);

    public BrokenUrlDialog(IReadOnlyList<UrlEntry> brokenEntries)
    {
        InitializeComponent();
        foreach (var e in brokenEntries)
        {
            var row = new BrokenRow
            {
                Id = e.Id,
                Title = e.Title,
                Url = e.Url,
                Category = e.Category,
                Reason = string.IsNullOrEmpty(e.LastCheckStatus) ? "失效" : e.LastCheckStatus,
                // 默认全部勾选（检测到失效 → 建议删除）
                Delete = true
            };
            _rows.Add(row);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            listView.Items.Add(row);
        }

        var count = _rows.Count;
        hintText.Text = $"已检测到 {count} 个可能无法访问的网址（可能由于网络、临时故障或已失效）。\n勾选需要删除的条目，点击“删除已选中”确认，或“全部保留”跳过。";
        selectAllCheck.IsChecked = count > 0;
        _a11y.Announce(this, $"共发现 {count} 个失效网址。已自动勾选准备删除。");
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Delete = true;
        listView.Items.Refresh();
        selectAllCheck.IsChecked = true;
        _a11y.Announce(this, "已全部选中。");
    }

    private void OnUnselectAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Delete = false;
        listView.Items.Refresh();
        selectAllCheck.IsChecked = false;
        _a11y.Announce(this, "已全部取消选中。");
    }

    private void SelectAllCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (selectAllCheck.IsChecked == true) OnSelectAll(sender, e);
        else OnUnselectAll(sender, e);
    }

    private void listView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 允许读屏用户确认当前行信息
        if (listView.SelectedItem is BrokenRow row)
            _a11y.Announce(this, $"{row.Title}，{row.Reason}，删除：{(row.Delete ? "是" : "否")}");
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var n = _rows.Count(r => r.Delete);
        if (n == 0)
        {
            _a11y.Announce(this, "当前没有勾选任何条目。");
            if (MessageBox.Show(this, "当前没有勾选删除任何条目，是否仍然关闭并全部保留？",
                "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
        }
        else
        {
            var conf = MessageBox.Show(this,
                $"确认删除选中的 {n} 个失效网址？删除后不可恢复。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (conf != MessageBoxResult.Yes) return;
        }

        DialogResult = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 空格切换当前行删除勾选
        if (e.Key == Key.Space && listView.SelectedItem is BrokenRow row)
        {
            e.Handled = true;
            row.Delete = !row.Delete;
            listView.Items.Refresh();
            _a11y.Announce(this, $"{row.Title} 已切换删除状态：{(row.Delete ? "是" : "否")}");
        }
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (listView.SelectedItem is BrokenRow r2 && !string.IsNullOrEmpty(r2.Url))
            {
                e.Handled = true;
                var svc = new ClipboardService();
                svc.CopyToClipboard(r2.Url);
                _a11y.Announce(this, "已复制网址到剪贴板。");
            }
        }
    }
}

