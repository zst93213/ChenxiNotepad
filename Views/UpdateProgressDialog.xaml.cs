using System.Diagnostics;
using System.IO;
using System.Windows;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 更新进度对话框。完整的自动更新流程：
/// 检测 → 下载（实时进度+速度+剩余时间）→ 校验 → 解压 → 安装 → 重启。
/// 全程语音+文字双重提示，支持取消（解压/安装阶段不可取消）。
/// </summary>
public partial class UpdateProgressDialog : Window
{
    private readonly UpdateService.ReleaseInfo _release;
    private readonly UpdateService.ReleaseAsset _asset;
    private readonly AccessibilityService _a11y = new();
    private CancellationTokenSource? _cts;

    // 下载速度计算
    private long _lastDownloadedBytes;
    private DateTime _lastSpeedUpdateTime;
    private double _lastSpeedBytesPerSec;

    /// <summary>更新是否已进入不可取消阶段（解压/安装）。</summary>
    public bool UpdateCompleted { get; private set; }

    /// <summary>更新是否因错误失败。</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>是否为自动模式（不弹 MessageBox 确认，直接走完全流程）。</summary>
    public bool AutoMode { get; set; }

    public UpdateProgressDialog(UpdateService.ReleaseInfo release, UpdateService.ReleaseAsset asset)
    {
        InitializeComponent();
        _release = release;
        _asset = asset;
        cancelButton.IsEnabled = true;
    }

    /// <summary>更新当前阶段并语音+文字同步提示。</summary>
    private void SetStage(string stage, string detail = "")
    {
        Dispatcher.BeginInvoke(() =>
        {
            stageText.Text = stage;
            statusText.Text = detail;
            _a11y.Announce(this, string.IsNullOrEmpty(detail) ? stage : $"{stage}。{detail}");
        });
    }

    /// <summary>启动异步更新全流程。</summary>
    public async Task StartUpdateAsync()
    {
        _cts = new CancellationTokenSource();

        try
        {
            // ===== 1. 准备下载 =====
            SetStage("正在准备下载",
                $"版本 {_release.TagName}，安装包 {FormatSize(_asset.Size)}");

            var tempZip = Path.Combine(Path.GetTempPath(),
                $"suixinji_update_{_release.TagName}.zip");

            // 如果上次下载残留了临时文件，先清理
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }

            await Task.Delay(500, _cts.Token); // 给读屏时间朗读阶段提示

            // ===== 2. 下载 =====
            SetStage("正在下载更新",
                $"正在下载 {_asset.Name}...");

            _lastDownloadedBytes = 0;
            _lastSpeedUpdateTime = DateTime.Now;

            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (p.total > 0)
                    {
                        var pct = (int)(p.downloaded * 100.0 / p.total);
                        progressBar.Value = pct;
                        percentText.Text = $"{pct}%";
                        sizeText.Text = $"{FormatSize(p.downloaded)} / {FormatSize(p.total)}";

                        // 计算速度和剩余时间（每 1 秒更新一次）
                        var now = DateTime.Now;
                        var elapsed = (now - _lastSpeedUpdateTime).TotalSeconds;
                        if (elapsed >= 1.0)
                        {
                            var bytesDiff = p.downloaded - _lastDownloadedBytes;
                            _lastSpeedBytesPerSec = bytesDiff / elapsed;
                            _lastDownloadedBytes = p.downloaded;
                            _lastSpeedUpdateTime = now;

                            var speedStr = $"速度：{FormatSize((long)_lastSpeedBytesPerSec)}/s";
                            if (_lastSpeedBytesPerSec > 0 && p.total > p.downloaded)
                            {
                                var remainingBytes = p.total - p.downloaded;
                                var remainingSec = remainingBytes / _lastSpeedBytesPerSec;
                                speedStr += $"，预计剩余 {FormatTime(remainingSec)}";
                            }
                            speedText.Text = speedStr;

                            // 每 10% 语音播报一次进度
                            if (pct > 0 && pct % 10 == 0 && pct < 100)
                            {
                                _a11y.Announce(this, $"已下载 {pct}%");
                            }
                        }
                    }
                    else
                    {
                        sizeText.Text = $"已下载 {FormatSize(p.downloaded)}";
                    }
                });
            });

            await UpdateService.DownloadFileAsync(
                _asset.DownloadUrl, tempZip, progress, _cts.Token);

            // 下载完成
            progressBar.Value = 100;
            percentText.Text = "100%";
            SetStage("下载完成", "正在校验安装包...");

            // ===== 3. 校验文件大小 =====
            await Task.Delay(300, _cts.Token);
            var actualSize = new FileInfo(tempZip).Length;
            if (_asset.Size > 0 && actualSize != _asset.Size)
            {
                throw new IOException(
                    $"安装包校验失败：期望 {FormatSize(_asset.Size)}，实际 {FormatSize(actualSize)}。" +
                    "可能是网络不稳定导致下载不完整，请重试。");
            }

            // ===== 4. 解压 =====
            cancelButton.IsEnabled = false;
            cancelButton.Content = "请稍候...";
            progressBar.IsIndeterminate = true;
            percentText.Text = "";
            sizeText.Text = "";
            speedText.Text = "";
            SetStage("正在解压安装包", "请稍候，此过程不可取消...");

            var extractDir = Path.Combine(Path.GetTempPath(),
                $"suixinji_extract_{_release.TagName}");
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }

            await Task.Run(() => UpdateService.ExtractZip(tempZip, extractDir), _cts.Token);

            // ===== 5. 安装 =====
            SetStage("正在安装更新", "应用即将关闭并自动重启，请勿关闭电脑...");

            var appDir = UpdateService.AppDir;
            var appExePath = UpdateService.AppExePath;

            // 启动更新脚本（等待当前进程退出 → 复制文件 → 重启）
            UpdateService.LaunchUpdater(extractDir, appDir, appExePath, tempZip);

            UpdateCompleted = true;

            // ===== 6. 准备重启 =====
            SetStage("更新已就绪", "应用即将关闭并重启，请稍候...");
            await Task.Delay(3000, _cts.Token);

            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            progressBar.Value = 0;
            progressBar.IsIndeterminate = false;
            percentText.Text = "";
            sizeText.Text = "";
            speedText.Text = "";
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭(_C)";
            SetStage("更新已取消", "您可以稍后通过菜单「检查更新」重新开始。");
            DialogResult = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            progressBar.Value = 0;
            progressBar.IsIndeterminate = false;
            percentText.Text = "";
            sizeText.Text = "";
            speedText.Text = "";
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭(_C)";
            SetStage("更新失败", ex.Message);
            DialogResult = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts is not null && !_cts.IsCancellationRequested && cancelButton.IsEnabled)
        {
            _cts.Cancel();
        }
        else
        {
            DialogResult = false;
            Close();
        }
    }

    /// <summary>
    /// 从已下载好的本地 zip 文件直接进入安装阶段（跳过下载）。
    /// 用于启动时静默下载完成后的"一键安装"场景。
    /// </summary>
    public async Task StartInstallFromLocalAsync(string localZipPath)
    {
        _cts = new CancellationTokenSource();

        try
        {
            // ===== 校验本地文件 =====
            SetStage("正在校验安装包", "请稍候...");

            if (!File.Exists(localZipPath))
            {
                throw new FileNotFoundException("安装包文件不存在，请重新检查更新。", localZipPath);
            }

            // 校验文件大小
            if (_asset.Size > 0)
            {
                var actualSize = new FileInfo(localZipPath).Length;
                if (actualSize != _asset.Size)
                {
                    throw new IOException(
                        $"安装包校验失败：期望 {FormatSize(_asset.Size)}，实际 {FormatSize(actualSize)}。" +
                        "可能是下载不完整，请重新检查更新。");
                }
            }

            await Task.Delay(500, _cts.Token);

            // ===== 解压 =====
            cancelButton.IsEnabled = false;
            cancelButton.Content = "请稍候...";
            progressBar.IsIndeterminate = true;
            SetStage("正在解压安装包", "请稍候，此过程不可取消...");

            var extractDir = Path.Combine(Path.GetTempPath(),
                $"suixinji_extract_{_release.TagName}");
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }

            await Task.Run(() => UpdateService.ExtractZip(localZipPath, extractDir), _cts.Token);

            // ===== 安装 =====
            SetStage("正在安装更新", "应用即将关闭并自动重启，请勿关闭电脑...");

            var appDir = UpdateService.AppDir;
            var appExePath = UpdateService.AppExePath;

            UpdateService.LaunchUpdater(extractDir, appDir, appExePath, localZipPath);

            UpdateCompleted = true;

            // ===== 准备重启 =====
            SetStage("更新已就绪", "应用即将关闭并重启，请稍候...");
            await Task.Delay(3000, _cts.Token);

            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            progressBar.Value = 0;
            progressBar.IsIndeterminate = false;
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭(_C)";
            SetStage("安装已取消", "您可以稍后通过菜单「检查更新」重新开始。");
            DialogResult = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            progressBar.Value = 0;
            progressBar.IsIndeterminate = false;
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭(_C)";
            SetStage("安装失败", ex.Message);
            DialogResult = false;
        }
    }

    // ===== 工具方法 =====

    /// <summary>格式化文件大小。</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    /// <summary>格式化剩余时间。</summary>
    private static string FormatTime(double seconds) => seconds switch
    {
        < 1 => "不到 1 秒",
        < 60 => $"{(int)seconds} 秒",
        < 3600 => $"{(int)(seconds / 60)} 分 {(int)(seconds % 60)} 秒",
        _ => $"{(int)(seconds / 3600)} 小时 {(int)((seconds % 3600) / 60)} 分",
    };
}
