using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlindNotepad.Services;

/// <summary>
/// 应用更新服务。通过 GitHub Releases API 检查最新版本，
/// 支持自动下载安装包、解压、生成更新脚本并重启应用。
/// </summary>
public static class UpdateService
{
    public const string RepoOwner = "zst93213";
    public const string RepoName = "ChenxiNotepad";
    private const string ApiUrl = "https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    // 下载用 HttpClient（较长超时，大文件下载）
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    /// <summary>GitHub Release 附件资源。</summary>
    public class ReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("content_type")]
        public string ContentType { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";
    }

    /// <summary>GitHub Release 信息。</summary>
    public class ReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("body")]
        public string Body { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<ReleaseAsset> Assets { get; set; } = new();
    }

    /// <summary>获取当前应用版本号。</summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly();
            var ver = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(ver)) return ver;
            return asm?.GetName().Version?.ToString() ?? "1.0.0";
        }
    }

    /// <summary>当前应用可执行文件所在目录。</summary>
    public static string AppDir =>
        Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
        ?? AppContext.BaseDirectory;

    /// <summary>当前应用可执行文件完整路径。</summary>
    public static string AppExePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "SuixinJi.exe");

    /// <summary>从 GitHub 获取最新 Release 信息。失败返回 null。</summary>
    public static async Task<ReleaseInfo?> FetchLatestReleaseAsync(string repoOwner, string repoName)
    {
        try
        {
            var url = ApiUrl.Replace("{Owner}", repoOwner).Replace("{Repo}", repoName);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "SuixinJi-UpdateCheck");
            req.Headers.Add("Accept", "application/vnd.github+json");

            // 检查版本用短超时
            using var shortHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await shortHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ReleaseInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>比较版本号。返回 true 表示 remote 比 local 新。</summary>
    public static bool IsNewerVersion(string localVersion, string remoteTag)
    {
        // 去掉 'v' 前缀
        var local = localVersion.TrimStart('v', 'V');
        var remote = remoteTag.TrimStart('v', 'V');

        if (Version.TryParse(local, out var localVer) && Version.TryParse(remote, out var remoteVer))
        {
            return remoteVer > localVer;
        }

        // 降级为字符串比较
        return string.CompareOrdinal(remote, local) > 0;
    }

    // ================================================================
    //  下载与安装
    // ================================================================

    /// <summary>
    /// 从 Release 附件中找到 win-x64 zip 包。
    /// 优先匹配 *_win-x64.zip，其次匹配任何 .zip。
    /// </summary>
    public static ReleaseAsset? FindWinX64Asset(ReleaseInfo release)
    {
        if (release.Assets is null || release.Assets.Count == 0) return null;

        // 优先精确匹配 win-x64
        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset is not null) return asset;

        // 降级：任意 zip
        return release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 下载文件到指定路径，报告下载进度。
    /// progress 回调参数：(已下载字节数, 总字节数)。
    /// cancellationToken 支持取消。
    /// </summary>
    public static async Task DownloadFileAsync(
        string downloadUrl,
        string destPath,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken = default)
    {
        using var resp = await _http.GetAsync(downloadUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? 0;
        await using var contentStream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int lastReportPercent = -1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await contentStream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;

            // 每 2% 报告一次，减少 UI 线程负担
            var percent = totalBytes > 0
                ? (int)(downloaded * 100 / totalBytes)
                : -1;
            if (percent != lastReportPercent)
            {
                lastReportPercent = percent;
                progress?.Report((downloaded, totalBytes));
            }
        }

        progress?.Report((downloaded, totalBytes > 0 ? totalBytes : downloaded));
    }

    /// <summary>
    /// 解压 zip 到目标目录（覆盖已有文件）。
    /// </summary>
    public static void ExtractZip(string zipPath, string destDir)
    {
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);
        Directory.CreateDirectory(destDir);

        ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
    }

    /// <summary>
    /// 生成并启动更新脚本（.bat），自动完成：
    /// 1. 等待当前应用退出
    /// 2. 清空应用目录中的旧版本文件（保留目录本身）
    /// 3. 复制新版本文件到应用目录
    /// 4. 删除临时文件
    /// 5. 重新启动应用
    /// 注意：用户数据存储在 %LocalAppData%/SuixinJi/，与应用目录完全隔离，更新不会丢失数据。
    /// </summary>
    /// <param name="newFilesDir">解压后的新文件目录。</param>
    /// <param name="appDir">应用安装目录（当前 exe 所在目录）。</param>
    /// <param name="appExePath">应用 exe 完整路径。</param>
    /// <param name="tempZipPath">下载的 zip 临时文件路径（更新完成后删除）。</param>
    public static void LaunchUpdater(string newFilesDir, string appDir, string appExePath, string tempZipPath)
    {
        var pid = Environment.ProcessId;
        var tempDir = Path.GetTempPath();
        var batPath = Path.Combine(tempDir, $"suixinji_update_{pid}.bat");

        var batContent = $@"@echo off
chcp 65001 >nul
echo Updating SuixinJi, please wait...

REM 等待旧进程退出（tasklist 过滤器语法必须为 PID eq <pid>）
:waitloop
tasklist /FI ""PID eq {pid}"" /NH 2>nul | find ""{pid}"" >nul
if %errorlevel%==0 (
    ping -n 2 127.0.0.1 >nul
    goto waitloop
)

REM 清空应用目录中的旧版本文件（保留目录本身）
del /q /f /s ""{appDir}\*"" 2>nul
for /d %%i in (""{appDir}\*"") do rd /s /q ""%%i"" 2>nul

REM 复制新版本文件到应用目录
xcopy ""{newFilesDir}\*"" ""{appDir}\"" /E /Y /I /Q

REM 清理临时文件
rd /s /q ""{newFilesDir}"" 2>nul
del /q ""{tempZipPath}"" 2>nul

REM 启动新版本
start """" ""{appExePath}""

REM 删除自身
del /q ""{batPath}"" 2>nul
";

        // 使用 UTF-8 (无 BOM) 写入 bat，并配合 chcp 65001 切到 UTF-8 代码页，
        // 以正确支持路径中可能出现的中文（如 Windows 用户名为中文）。
        File.WriteAllText(batPath, batContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo
        {
            FileName = batPath,
            WindowStyle = ProcessWindowStyle.Normal,
            CreateNoWindow = false,
            UseShellExecute = true,
        };

        Process.Start(startInfo);
    }
}
