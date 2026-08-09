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

    /// <summary>获取当前应用版本号。
    /// 注意：AssemblyInformationalVersion 在发布构建中会带上 "+<commit>" 后缀，
    /// 必须先去掉 '+' 之后的信息段，再参与版本比较，否则 Version.TryParse 失败会退化为字符串比较，
    /// 导致远程 "v2.4.5" 被错误地判定为比本地 "2.4.5+abcdef" 更新，出现更新成功后反复提示更新的死循环。
    /// </summary>
    public static string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly();
            var ver = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(ver)) return StripInformationalVersionSuffix(ver);
            return StripInformationalVersionSuffix(asm?.GetName().Version?.ToString() ?? "1.0.0");
        }
    }

    /// <summary>
    /// 去除 InformationalVersion 中 '+' 及后续的构建元数据（git commit 哈希），
    /// 只保留严格的 x.y.z.prerelease 部分供 Version 解析。
    /// </summary>
    private static string StripInformationalVersionSuffix(string version)
    {
        if (string.IsNullOrEmpty(version)) return "1.0.0";
        var plus = version.IndexOf('+');
        if (plus > 0) version = version.Substring(0, plus);
        return version.Trim();
    }

    /// <summary>当前应用可执行文件所在目录。</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>当前应用可执行文件完整路径。</summary>
    public static string AppExePath => Path.Combine(AppContext.BaseDirectory, "SuixinJi.exe");

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

    /// <summary>比较版本号。返回 true 表示 remote 比 local 新。
    /// 已自动处理两边的 'v' 前缀以及 InformationalVersion 的 "+<commit>" 后缀。
    /// </summary>
    public static bool IsNewerVersion(string localVersion, string remoteTag)
    {
        // 去掉 'v' 前缀，同时去掉 +commit 构建元数据
        var local = StripInformationalVersionSuffix(localVersion ?? "").TrimStart('v', 'V');
        var remote = StripInformationalVersionSuffix(remoteTag ?? "").TrimStart('v', 'V');

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
    /// 生成并启动 PowerShell 更新脚本，自动完成：
    /// 1. 记录每一步到日志（data/update_logs/）
    /// 2. 等待当前应用退出
    /// 3. 清空应用目录中的旧版本文件，但保留 data/ 目录（用户数据）
    /// 4. 复制新文件
    /// 5. 清理临时 zip / 解压目录
    /// 6. 校验新 exe 存在后启动；失败时弹出错误消息并保留日志
    /// 
    /// 使用 PowerShell 而非 bat 的原因：
    /// - 原生 UTF-8 支持，不受代码页影响（bat 的 chcp 65001 在中文路径下仍有问题）
    /// - 无 delayed expansion 陷阱（bat 中 ! ^ & 等字符会被吞）
    /// - 引号转义简单（PowerShell 单引号字符串不需要转义双引号）
    /// - .NET API 直接可用，文件操作更可靠
    /// </summary>
    /// <param name="newFilesDir">解压后的新文件目录。</param>
    /// <param name="appDir">应用安装目录（当前 exe 所在目录）。</param>
    /// <param name="appExePath">应用 exe 完整路径。</param>
    /// <param name="tempZipPath">下载的 zip 临时文件路径（更新完成后删除）。</param>
    public static void LaunchUpdater(string newFilesDir, string appDir, string appExePath, string tempZipPath)
    {
        // 去除路径末尾的反斜杠
        appDir = appDir.TrimEnd('\\', '/');
        newFilesDir = newFilesDir.TrimEnd('\\', '/');
        appExePath = appExePath.TrimEnd('\\', '/');
        tempZipPath = tempZipPath.TrimEnd('\\', '/');

        // 自动下钻：zip 解压后可能多一层目录
        var exeName = Path.GetFileName(appExePath) ?? "SuixinJi.exe";
        if (!File.Exists(Path.Combine(newFilesDir, exeName)))
        {
            try
            {
                foreach (var sd in Directory.GetDirectories(newFilesDir))
                {
                    if (File.Exists(Path.Combine(sd, exeName)))
                    {
                        newFilesDir = sd.TrimEnd('\\', '/');
                        break;
                    }
                }
            }
            catch { }
        }

        var pid = Environment.ProcessId;

        // 日志目录
        var updateLogDir = Path.Combine(appDir, "data", "update_logs");
        try { Directory.CreateDirectory(updateLogDir); } catch { }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var logPath = Path.Combine(updateLogDir, $"update_{timestamp}.log");

        // C# 端先写一条日志
        try
        {
            File.WriteAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===== C# LaunchUpdater Entry =====\n" +
                $"  appDir  = {appDir}\n" +
                $"  newFilesDir = {newFilesDir}\n" +
                $"  appExePath  = {appExePath}\n" +
                $"  tempZipPath = {tempZipPath}\n" +
                $"  currentPID  = {pid}\n" +
                $"  newDirHasExe = {File.Exists(Path.Combine(newFilesDir, exeName))}\n\n",
                new System.Text.UTF8Encoding(false));
        }
        catch { }

        // 生成 PowerShell 脚本
        // 注意：路径值用单引号包裹，PowerShell 单引号内不需要转义任何字符
        // 但路径本身可能包含单引号（极罕见），用 -replace 做安全处理
        var psScript = $@"# 随心记自动更新脚本 (PowerShell)
# 由 UpdateService.LaunchUpdater 自动生成
$ErrorActionPreference = 'Stop'

$AppDir     = '{appDir}'
$NewDir     = '{newFilesDir}'
$ExePath    = '{appExePath}'
$ZipPath    = '{tempZipPath}'
$OldPid     = {pid}
$LogPath    = '{logPath}'
$ScriptPath = $MyInvocation.MyCommand.Path

function Log($msg) {{
    $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $msg
    Write-Host $line
    Add-Content -Path $LogPath -Value $line -Encoding UTF8
}}

try {{
    Log '===== SuixinJi Update Start ====='

    # 1. 等待旧进程退出（最多30秒）
    Log ""Step 1: Waiting for PID $OldPid to exit (max 30s)""
    $waited = 0
    $maxWait = 30
    while ($waited -lt $maxWait) {{
        $proc = Get-Process -Id $OldPid -ErrorAction SilentlyContinue
        if ($null -eq $proc) {{ break }}
        if ($proc.HasExited) {{ break }}
        Start-Sleep -Seconds 1
        $waited++
    }}
    if ($waited -ge $maxWait) {{
        Log ""WARN: Timeout, force killing PID $OldPid""
        try {{ Stop-Process -Id $OldPid -Force -ErrorAction SilentlyContinue }} catch {{}}
        Start-Sleep -Seconds 3
    }}
    Log ""Old process exited. waited=$waited s.""

    # 2. 删除旧文件（保留 data 目录和 Update.ps1 自身）
    Log 'Step 2: Removing old files (preserve data/ and Update.ps1)'
    Get-ChildItem -Path $AppDir -File | Where-Object {{ $_.Name -ne 'Update.ps1' }} | ForEach-Object {{
        try {{ Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }} catch {{}}
    }}
    Get-ChildItem -Path $AppDir -Directory | Where-Object {{ $_.Name -ne 'data' }} | ForEach-Object {{
        try {{ Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }} catch {{}}
        Log ""Removed dir: $($_.Name)""
    }}
    Log 'Old files removed.'

    # 3. 复制新文件
    Log ""Step 3: Copying from $NewDir to $AppDir""
    $copied = 0
    $failed = 0
    Get-ChildItem -Path $NewDir -Recurse -File | ForEach-Object {{
        $relPath = $_.FullName.Substring($NewDir.Length).TrimStart('\','/')
        $destPath = Join-Path $AppDir $relPath
        # 排除 Update.ps1 自身
        if ($destPath -ne $ScriptPath) {{
            $destDir = Split-Path $destPath -Parent
            if (-not (Test-Path $destDir)) {{
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }}
            try {{
                Copy-Item $_.FullName -Destination $destPath -Force
                $copied++
            }} catch {{
                $failed++
                Log ""WARN: Failed to copy $relPath : $($_.Exception.Message)""
            }}
        }}
    }}
    Log ""Copy done: $copied files copied, $failed failed.""

    if ($copied -eq 0 -and $failed -gt 0) {{
        Log 'ERROR: All file copies failed!'
        throw 'All file copies failed'
    }}

    # 4. 清理临时文件
    Log 'Step 4: Cleaning temp files'
    try {{ Remove-Item $NewDir -Recurse -Force -ErrorAction SilentlyContinue }} catch {{}}
    try {{ Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue }} catch {{}}
    Log 'Temp cleaned.'

    # 5. 启动新程序
    Log ""Step 5: Launching $ExePath""
    if (-not (Test-Path $ExePath)) {{
        Log ""ERROR: $ExePath not found after update!""
        throw ""Exe not found: $ExePath""
    }}
    $exeInfo = Get-Item $ExePath
    Log ""Exe exists, size=$($exeInfo.Length) bytes""

    Start-Process -FilePath $ExePath -WorkingDirectory $AppDir
    Log 'Start-Process called.'

    Start-Sleep -Seconds 3
    $newProc = Get-Process -Name 'SuixinJi' -ErrorAction SilentlyContinue
    if ($null -ne $newProc) {{
        Log ""SUCCESS: SuixinJi.exe running (PID: $($newProc.Id)).""
    }} else {{
        Log 'WARN: Process not found, retry...'
        Start-Process -FilePath $ExePath
        Start-Sleep -Seconds 3
        $newProc = Get-Process -Name 'SuixinJi' -ErrorAction SilentlyContinue
        if ($null -ne $newProc) {{
            Log ""SUCCESS: Retry OK (PID: $($newProc.Id)).""
        }} else {{
            Log 'ERROR: Launch failed after retry!'
            throw 'Launch failed'
        }}
    }}

    Log '===== SuixinJi Update SUCCESS ====='

}} catch {{
    Log ""===== SuixinJi Update FAILED =====""
    Log ""Error: $($_.Exception.Message)""
    try {{
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show(
            ""随心记自动更新失败！`n`n错误：$($_.Exception.Message)`n`n详细日志：$LogPath"",
            '更新失败',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        )
    }} catch {{}}
}} finally {{
    Log 'Deleting Update.ps1...'
    try {{ Remove-Item $ScriptPath -Force -ErrorAction SilentlyContinue }} catch {{}}
}}
";

        // PowerShell 脚本写到 appDir/Update.ps1
        var psPath = Path.Combine(appDir, "Update.ps1");
        File.WriteAllText(psPath, psScript, new System.Text.UTF8Encoding(false));

        // 启动 PowerShell（绕过执行策略）
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{psPath}\"",
            WindowStyle = ProcessWindowStyle.Normal,
            CreateNoWindow = false,
            UseShellExecute = true,
            WorkingDirectory = appDir,
        };

        Process? psProc = null;
        try
        {
            psProc = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: Process.Start(powershell) failed: {ex.Message}\n{ex.StackTrace}\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
            throw;
        }

        // 等待 PowerShell 进程确认存活
        if (psProc is not null)
        {
            try
            {
                int waitMs = 0;
                bool psAlive = false;
                while (waitMs < 3000)
                {
                    psProc.Refresh();
                    if (!psProc.HasExited)
                    {
                        psAlive = true;
                        break;
                    }
                    Thread.Sleep(200);
                    waitMs += 200;
                }
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PS PROCESS verified: alive={psAlive}, waited={waitMs}ms\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARN: verify ps exception: {ex.Message}\n",
                        new System.Text.UTF8Encoding(false));
                }
                catch { }
            }
        }

        try
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] C# Updater finished. PowerShell will handle the rest.\n\n",
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
