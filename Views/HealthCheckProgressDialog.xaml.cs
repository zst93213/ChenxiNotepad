using System.Windows;
using System.Windows.Input;
using BlindNotepad.Models;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 导入后批量检测网址健康的进度对话框：并发检测、可随时取消。
/// 对话框关闭后：
///   - DialogResult=true：检测完成（或用户取消后接受已完成部分）
///   - 调用方通过 DetectedBrokenEntries 获取所有"非 OK / 非 跳过"的条目（去重）。
/// </summary>
public partial class HealthCheckProgressDialog : Window
{
    private readonly AccessibilityService _a11y = new();
    private readonly List<UrlEntry> _entries;
    private CancellationTokenSource? _cts;
    private int _doneCount;
    private int _brokenCount;

    /// <summary>检测后收集到的失效条目（LastCheckStatus != OK / 跳过）。</summary>
    public List<UrlEntry> DetectedBrokenEntries { get; } = new();

    /// <summary>是否用户主动取消。</summary>
    public bool CanceledByUser { get; private set; }

    public HealthCheckProgressDialog(List<UrlEntry> entries)
    {
        InitializeComponent();
        _entries = entries;
        cancelButton.IsEnabled = true;
    }

    private void SetStage(string stage, string detail = "")
    {
        Dispatcher.BeginInvoke(() =>
        {
            stageText.Text = stage;
            detailText.Text = detail;
            var msg = string.IsNullOrEmpty(detail) ? stage : $"{stage}。{detail}";
            _a11y.Announce(this, msg);
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        try
        {
            var total = _entries.Count;
            SetStage("准备开始检测",
                total == 0 ? "没有可检测的网址。" : $"本次共检测 {total} 个网址，并发上限 8，每个最多耗时约 10 秒。");

            if (total == 0)
            {
                DialogResult = true;
                Close();
                return;
            }

            await Task.Delay(400, _cts.Token);

            progressBar.Maximum = total;
            progressBar.Value = 0;

            // 并发检测
            var progress = new Progress<(int done, int total, UrlEntry entry)>(p =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Increment(ref _doneCount);
                    progressBar.Value = p.done;
                    var pct = total > 0 ? (int)(p.done * 100.0 / total) : 0;
                    percentText.Text = $"{pct}%";
                    // 计数失效
                    if (!string.Equals(p.entry.LastCheckStatus, "OK", StringComparison.Ordinal)
                        && !string.Equals(p.entry.LastCheckStatus, "跳过", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _brokenCount);
                    }
                    countText.Text = $"已完成 {p.done}/{p.total}，检测到失效 {_brokenCount} 个";
                });
            });

            await UrlHealthChecker.CheckAllParallelAsync(
                _entries, progress: progress, concurrencyLimit: 8,
                cancellationToken: _cts.Token);

            // 汇总失效条目
            DetectedBrokenEntries.Clear();
            foreach (var entry in _entries)
            {
                if (!string.Equals(entry.LastCheckStatus, "OK", StringComparison.Ordinal)
                    && !string.Equals(entry.LastCheckStatus, "跳过", StringComparison.Ordinal)
                    && !string.Equals(entry.LastCheckStatus, "取消", StringComparison.Ordinal))
                {
                    DetectedBrokenEntries.Add(entry);
                }
            }

            SetStage("检测完成",
                $"共检测 {total} 条，失效 {DetectedBrokenEntries.Count} 条，即将进入确认。");
            cancelButton.IsEnabled = false;
            cancelButton.Content = "关闭(_C)";
            await Task.Delay(600, _cts.Token);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            CanceledByUser = true;
            SetStage("检测已取消", $"已检测 {_doneCount}/{_entries.Count}，检测到失效 {_brokenCount} 个。您仍可对已完成部分决定是否删除。");
            // 取消后也汇总当前已完成的失效条目，让用户选择
            DetectedBrokenEntries.Clear();
            foreach (var entry in _entries)
            {
                if (!string.Equals(entry.LastCheckStatus, "OK", StringComparison.Ordinal)
                    && !string.Equals(entry.LastCheckStatus, "跳过", StringComparison.Ordinal)
                    && !string.Equals(entry.LastCheckStatus, "取消", StringComparison.Ordinal))
                {
                    DetectedBrokenEntries.Add(entry);
                }
            }
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭(_C)";
            progressBar.IsIndeterminate = false;
        }
        catch (Exception ex)
        {
            SetStage("检测出错", ex.Message);
            MessageBox.Show(this, "检测出错：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            CanceledByUser = true;
            _cts.Cancel();
            SetStage("正在停止检测…", "稍候，会保留当前已完成的结果。");
        }
        else
        {
            DialogResult = DetectedBrokenEntries.Count > 0 || _doneCount > 0;
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancel(sender, e);
        }
    }
}
