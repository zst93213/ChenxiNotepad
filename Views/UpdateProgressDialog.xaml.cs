using System.IO;
using System.Windows;
using BlindNotepad.Services;

namespace BlindNotepad;

/// <summary>
/// 更新进度对话框。显示下载/解压进度，支持取消。
/// 由调用方通过 StartUpdate 启动整个更新流程。
/// </summary>
public partial class UpdateProgressDialog : Window
{
    private readonly UpdateService.ReleaseInfo _release;
    private readonly UpdateService.ReleaseAsset _asset;
    private CancellationTokenSource? _cts;

    /// <summary>更新是否已进入不可取消阶段（解压/安装）。</summary>
    public bool UpdateCompleted { get; private set; }

    /// <summary>更新是否因错误失败。</summary>
    public string? ErrorMessage { get; private set; }

    public UpdateProgressDialog(UpdateService.ReleaseInfo release, UpdateService.ReleaseAsset asset)
    {
        InitializeComponent();
        _release = release;
        _asset = asset;
        cancelButton.IsEnabled = true;
    }

    /// <summary>启动异步更新流程：下载 → 解压 → 安装。完成后自动关闭对话框。</summary>
    public async Task StartUpdateAsync()
    {
        _cts = new CancellationTokenSource();

        try
        {
            // ===== 1. 下载 =====
            statusText.Text = $"正在下载 {_asset.Name}...";
            var tempZip = Path.Combine(Path.GetTempPath(),
                $"suixinji_update_{_release.TagName}.zip");

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
                    }
                    else
                    {
                        sizeText.Text = $"已下载 {FormatSize(p.downloaded)}";
                    }
                });
            });

            await UpdateService.DownloadFileAsync(
                _asset.DownloadUrl, tempZip, progress, _cts.Token);

            // ===== 2. 解压 =====
            cancelButton.IsEnabled = false;
            statusText.Text = "正在解压安装包...";
            percentText.Text = "";
            sizeText.Text = "";
            progressBar.IsIndeterminate = true;

            var extractDir = Path.Combine(Path.GetTempPath(),
                $"suixinji_extract_{_release.TagName}");
            await Task.Run(() => UpdateService.ExtractZip(tempZip, extractDir), _cts.Token);

            // ===== 3. 安装 =====
            statusText.Text = "正在安装更新，应用即将重启...";
            progressBar.IsIndeterminate = true;

            var appDir = UpdateService.AppDir;
            var appExePath = UpdateService.AppExePath;

            // 启动更新脚本并关闭应用
            UpdateService.LaunchUpdater(extractDir, appDir, appExePath, tempZip);

            UpdateCompleted = true;

            // 给读屏一点时间播报状态
            await Task.Delay(1500);

            // 关闭对话框 → 主窗口随后关闭应用
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "更新已取消。";
            percentText.Text = "";
            sizeText.Text = "";
            progressBar.Value = 0;
            progressBar.IsIndeterminate = false;
            cancelButton.IsEnabled = true;
            DialogResult = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            statusText.Text = $"更新失败：{ex.Message}";
            percentText.Text = "";
            sizeText.Text = "";
            progressBar.Value = 0;
            progressBar.IsIndeterminate = false;
            cancelButton.IsEnabled = true;
            cancelButton.Content = "关闭";
            DialogResult = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts is not null && !_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        else
        {
            // 已经完成或已取消，直接关闭
            DialogResult = false;
            Close();
        }
    }

    /// <summary>格式化文件大小。</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
