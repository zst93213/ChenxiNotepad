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
    /// 1. 记录每一步到日志（data/update_logs/）
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
        // 关键修复：去除所有路径末尾的反斜杠，避免与 bat 引号冲突导致语法错误
        appDir = appDir.TrimEnd('\\', '/');
        newFilesDir = newFilesDir.TrimEnd('\\', '/');
        appExePath = appExePath.TrimEnd('\\', '/');
        tempZipPath = tempZipPath.TrimEnd('\\', '/');

        var pid = Environment.ProcessId;

        // 日志写到 data/update_logs 目录，用户容易找到
        var updateLogDir = Path.Combine(appDir, "data", "update_logs");
        try { Directory.CreateDirectory(updateLogDir); } catch { }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var logPath = Path.Combine(updateLogDir, $"update_{timestamp}.log");
        // bat 文件固定放在 appDir 下，避免临时目录被清理
        var batPath = Path.Combine(appDir, "Update.bat");

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
call :LOG ExePath: %EXEPATH%
call :LOG CurrentPID: %PID%
call :LOG BatFile: %BATFILE%

REM ========== 1. 等待旧进程退出（超时 30 秒，双重检测） ==========
set /A MAXWAITS=30
set WAITS=0
:waitloop
set ""PROCALIVE=0""
REM 优先用 wmic 精确检测 PID
wmic process where ""ProcessId='%PID%'"" get ProcessId 2>nul | find ""%PID%"" >nul
if %errorlevel%==0 set ""PROCALIVE=1""
REM wmic 失败时用 tasklist 回退检测
if ""!PROCALIVE!""==""0"" (
    tasklist /FI ""PID eq %PID%"" /NH 2>nul | find ""%PID%"" >nul
    if %errorlevel%==0 set ""PROCALIVE=1""
)

if ""!PROCALIVE!""==""1"" (
    timeout /t 1 /nobreak >nul
    set /A WAITS+=1
    if !WAITS! GEQ %MAXWAITS% (
        call :LOG WARN: timeout waiting %MAXWAITS%s for PID %PID%, force kill.
        taskkill /F /PID %PID% /T >nul 2>&1
        timeout /t 3 /nobreak >nul
        goto endwait
    )
    goto waitloop
)
:endwait
call :LOG Old process exited. waited=%WAITS%s.

REM ========== 2. 移除目标目录只读属性，防止复制失败 ==========
call :LOG Step 2: Remove read-only attributes in %APPDIR%
attrib -r ""%APPDIR%\*.*"" /S /D >nul 2>&1

REM ========== 3. 清理旧版本文件（保留 data 目录） ==========
call :LOG Step 3: Cleaning old files in %APPDIR% (preserve data/)
REM 先删文件
for /f ""delims="" %%F in ('dir /b /a-d ""%APPDIR%\*"" 2^>nul') do (
    if /i not ""%%F""==""Update.bat"" (
        del /q /f ""%APPDIR%\%%F"" >nul 2>&1
    )
)
REM 再删子目录（保留 data）
for /d %%D in (""%APPDIR%\*"") do (
    if /i ""%%~nxD""==""data"" (
        call :LOG Preserved data dir: %%D
    ) else (
        rd /s /q ""%%D"" >nul 2>&1
        if exist ""%%D"" (
            call :LOG WARN: Cannot remove dir %%D, will try overwrite copy.
        ) else (
            call :LOG Removed dir: %%D
        )
    )
)

REM ========== 4. 复制新文件：robocopy 优先，xcopy 备用 ==========
call :LOG Step 4: Copying new files from %NEWDIR% to %APPDIR%
set COPYOK=0
if exist ""%SYSTEMROOT%\System32\robocopy.exe"" (
    call :LOG Using robocopy
    ""%SYSTEMROOT%\System32\robocopy.exe"" ""%NEWDIR%"" ""%APPDIR%"" /E /IS /IT /R:3 /W:2 /NFL /NDL /NP /XF Update.bat
    set RC=%ERRORLEVEL%
    if !RC! LSS 8 (
        set COPYOK=1
        call :LOG robocopy OK, code !RC!
    ) else (
        call :LOG robocopy FAILED, code !RC!
    )
)
if ""!COPYOK!""==""0"" (
    call :LOG Using xcopy fallback
    xcopy ""%NEWDIR%\*"" ""%APPDIR%"" /E /Y /I /Q /EXCLUDE:%BATFILE%.exclude.tmp 2>nul
    REM 写一个临时排除文件（排除 Update.bat）
    echo Update.bat > ""%APPDIR%\_exclude.tmp"" 2>nul
    xcopy ""%NEWDIR%\*"" ""%APPDIR%"" /E /Y /I /Q /EXCLUDE:%APPDIR%\_exclude.tmp >nul 2>&1
    del /q ""%APPDIR%\_exclude.tmp"" >nul 2>&1
    set RC=%ERRORLEVEL%
    if !RC!==0 (
        set COPYOK=1
        call :LOG xcopy OK.
    ) else (
        call :LOG xcopy FAILED, code !RC!
    )
)
if ""!COPYOK!""==""0"" (
    call :LOG ERROR: Both robocopy and xcopy failed!
    goto reporterror
)

REM ========== 5. 清理临时文件 ==========
call :LOG Step 5: Cleaning temp files
rd /s /q ""%NEWDIR%"" >nul 2>&1
del /q ""%ZIPPATH%"" >nul 2>&1
call :LOG Temp cleaned.

REM ========== 6. 校验并启动新程序 ==========
call :LOG Step 6: Verify and launch %EXEPATH%
if not exist ""%EXEPATH%"" (
    call :LOG ERROR: %EXEPATH% does NOT exist after copy!
    goto reporterror
)
REM 获取 exe 文件大小（用 for 循环避免参数引用问题）
for %%A in (""%EXEPATH%"") do call :LOG Exe exists, size=%%~zA bytes.

REM 关键修复：start 正确语法是 start "窗口标题" [/D工作目录] 命令
REM 窗口标题必须为空字符串 ""，否则如果命令路径带引号会被误当作标题
call :LOG About to launch [method 1]: start /D ""%APPDIR%"" title="" cmd=""%EXEPATH%""
cd /d ""%APPDIR%""
start """" /D ""%APPDIR%"" ""%EXEPATH%""
set RC=%ERRORLEVEL%
call :LOG start method 1 returned errorlevel=%RC%

REM 验证新进程是否真的启动了（等 3 秒后检查是否有 SuixinJi.exe 在跑）
timeout /t 3 /nobreak >nul
set ""NEWLAUNCHED=0""
tasklist /NH 2>nul | find /i ""SuixinJi.exe"" >nul
if %errorlevel%==0 (
    set ""NEWLAUNCHED=1""
    call :LOG SUCCESS: New SuixinJi.exe running after method 1.
)
if ""!NEWLAUNCHED!""==""0"" (
    call :LOG WARN: Method 1 did not start process, trying method 2.
    REM 备用方案2：不使用 /D，先 cd 再 start
    cd /d ""%APPDIR%""
    start """" ""%EXEPATH%""
    set RC=%ERRORLEVEL%
    call :LOG Method 2 start returned errorlevel=%RC%
    timeout /t 3 /nobreak >nul
    set ""NEWLAUNCHED=0""
    tasklist /NH 2>nul | find /i ""SuixinJi.exe"" >nul
    if %errorlevel%==0 (
        set ""NEWLAUNCHED=1""
        call :LOG SUCCESS: Method 2 OK.
    )
)
if ""!NEWLAUNCHED!""==""0"" (
    call :LOG WARN: Method 2 also failed, trying method 3: call via separate cmd instance.
    cd /d ""%APPDIR%""
    REM 避免引号嵌套问题：把命令写入临时 vbs 然后执行，或者直接用 explorer 打开
    REM 这里使用更稳妥的方案：用 for 变量保存路径，然后 start
    set ""APP=%EXEPATH%""
    for %%P in (""!APP!"") do (
        set ""APPDIRVAR=%%~dpP""
        set ""APPNAME=%%~nxP""
    )
    cd /d ""!APPDIRVAR!""
    start """" ""!APPNAME!""
    timeout /t 4 /nobreak >nul
    set ""NEWLAUNCHED=0""
    tasklist /NH 2>nul | find /i ""SuixinJi.exe"" >nul
    if %errorlevel%==0 (
        call :LOG SUCCESS: Method 3 OK.
    ) else (
        call :LOG ERROR: All 3 launch methods FAILED.
        goto reporterror
    )
)

call :LOG ===== SuixinJi Update SUCCESS =====
call :LOG Update finished at %date% %time%

REM ========== 7. 删除 Update.bat 自身（延迟删除） ==========
start /b "" cmd /c ""timeout /t 3 /nobreak >nul & del /q ""%BATFILE%"""
goto :eof

:reporterror
call :LOG ===== SuixinJi Update FAILED =====
call :LOG Failure time: %date% %time%
call :LOG Log file location: %LOGFILE%
echo.
echo ============================================
echo   UPDATE FAILED! Please check log:
echo   %LOGFILE%
echo ============================================
echo.
msg * ""随心记自动更新失败！\n\n详细日志已保存至：\n%LOGFILE%\n\n请将此日志反馈给开发者排查。""
pause
goto :eof

:LOG
echo [%time%] %*
echo [%date% %time%] %* >> ""%LOGFILE%""
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
            WorkingDirectory = appDir,
        };

        Process.Start(startInfo);

        // 写一条更新启动日志，方便排查 bat 没跑的问题
        try
        {
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] C# Updater launched OK.\n  bat={batPath}\n  appDir={appDir}\n  newDir={newFilesDir}\n  exe={appExePath}\n  zip={tempZipPath}\n  currentPID={pid}\n");
        }
        catch { }
    }
}
