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
    /// 生成并启动更新脚本（.bat），自动完成：
    /// 1. 记录每一步到日志（%Temp%\suixinji_update_&lt;pid&gt;.log）
    /// 2. 等待当前应用退出
    /// 3. 清空应用目录中的旧版本文件，但保留 data/ 目录（用户数据）
    /// 4. 用 robocopy 优先复制新文件，失败回退 xcopy
    /// 5. 清理临时 zip / 解压目录
    /// 6. 校验新 exe 存在后启动；失败时弹出错误消息并保留日志
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
        var logPath = Path.Combine(tempDir, $"suixinji_update_{pid}.log");

        var batContent = $@"@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

set ""LOGFILE={logPath}""
set ""APPDIR={appDir}""
set ""NEWDIR={newFilesDir}""
set ""EXEPATH={appExePath}""
set ""ZIPPATH={tempZipPath}""
set ""PID={pid}""
set ""BATFILE={batPath}""

call :LOG ===== SuixinJi Update Start =====
call :LOG Time: %date% %time%
call :LOG AppDir: %APPDIR%
call :LOG NewFilesDir: %NEWDIR%
call :LOG CurrentPID: %PID%

REM 1. 等待旧进程退出（超时 30 秒；每 1 秒检查一次）
set /A MAXWAITS=30
set WAITS=0
:waitloop
REM 使用 wmic 精确检查指定 PID 是否还存在（比 tasklist+find 更可靠，不会误匹配包含该数字的其他进程）
wmic process where "ProcessId='%PID%'" get ProcessId 2>nul | find ""%PID%"" >nul
if %errorlevel%==0 (
    timeout /t 1 /nobreak >nul
    set /A WAITS+=1
    if !WAITS! GEQ %MAXWAITS% (
        call :LOG WARN: timed out waiting %MAXWAITS%s for PID %PID% to exit, continuing anyway (will kill it).
        REM 超时后强制杀掉，避免后续文件被占用导致复制失败
        taskkill /F /PID %PID% /T >nul 2>&1
        timeout /t 2 /nobreak >nul
        goto endwait
    )
    goto waitloop
)
:endwait
call :LOG Old process has exited or was killed. waited=%WAITS%s.

REM 2. 清理旧版本文件（保留 data 目录；保留子目录结构，因为 data/ 存放用户数据）
call :LOG Step 2: Cleaning old files in %APPDIR% while preserving data/
for /f ""delims="" %%F in ('dir /b /a-d ""%APPDIR%\*"" 2^>nul') do (
    del /q /f ""%APPDIR%\%%F"" >nul 2>&1
)
for /d %%D in (""%APPDIR%\*"") do (
    if /i not ""%%~nxD""==""data"" (
        rd /s /q ""%%D"" >nul 2>&1
        call :LOG Removed directory: %%D
    ) else (
        call :LOG Preserved data directory: %%D
    )
)

REM 3. 复制新文件：优先 robocopy（更稳，退出码 0-7 都算成功）
call :LOG Step 3: Copying new files from %NEWDIR%
if exist ""%SYSTEMROOT%\System32\robocopy.exe"" (
    call :LOG Using robocopy
    ""%SYSTEMROOT%\System32\robocopy.exe"" ""%NEWDIR%"" ""%APPDIR%"" /E /IS /IT /R:3 /W:2 /NFL /NDL /NP
    set RC=%ERRORLEVEL%
    if !RC! GEQ 8 (
        call :LOG ERROR: robocopy failed with exit code !RC!
        goto reporterror
    )
    call :LOG robocopy done, exit code !RC!
) else (
    call :LOG robocopy not found, fallback to xcopy
    xcopy ""%NEWDIR%\*"" ""%APPDIR%"" /E /Y /I /Q
    set RC=%ERRORLEVEL%
    if not ""!RC!""==""0"" (
        call :LOG ERROR: xcopy failed with exit code !RC!
        goto reporterror
    )
    call :LOG xcopy done.
)

REM 4. 清理临时文件
call :LOG Step 4: Cleaning up temp files
rd /s /q ""%NEWDIR%"" >nul 2>&1
del /q ""%ZIPPATH%"" >nul 2>&1

REM 5. 校验并启动新程序
call :LOG Step 5: Verifying and launching %EXEPATH%
if not exist ""%EXEPATH%"" (
    call :LOG ERROR: %EXEPATH% not found after update.
    goto reporterror
)

call :LOG Launching new app: %EXEPATH%
start """" ""%EXEPATH%""
set RC=%ERRORLEVEL%
call :LOG start returned %RC%

call :LOG ===== SuixinJi Update Success =====

REM 6. 删除自身（延迟一帧，避免被占用）
start /b "" cmd /c timeout /t 2 /nobreak >nul & del /q ""%BATFILE%""
goto :eof

:reporterror
call :LOG ===== SuixinJi Update FAILED =====
call :LOG Please check the log at: %LOGFILE%
msg * ""更新失败：\n\n新程序复制后无法启动。\n请查看更新日志：\n%LOGFILE%""
goto :eof

:LOG
echo %*
echo %* >> ""%LOGFILE%""
goto :eof
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
            WorkingDirectory = tempDir,
        };

        Process.Start(startInfo);

        // 写一条更新启动日志，方便排查 bat 没跑的问题
        try
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Updater launched. bat={batPath} appDir={appDir} newDir={newFilesDir}\n");
        }
        catch { }
    }
}
